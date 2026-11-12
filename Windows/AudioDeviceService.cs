using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace AudioRouter.Windows;

public sealed record AudioDeviceInfo(string Id, string Name, bool IsInput, bool IsDefault)
{
    public bool IsVirtualCable => Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase)
        || Name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase)
        || Name.Contains("VoiceMeeter", StringComparison.OrdinalIgnoreCase);
    public bool IsMultichannelCable => IsVirtualCable && Name.Replace(" ", "").Contains("16ch", StringComparison.OrdinalIgnoreCase);
}

public sealed record AudioApplicationInfo(int ProcessId, string Name)
{
    public string ProcessName { get; init; } = "";
    public string? DeviceId { get; init; }
    public string? DeviceName { get; init; }
    public bool IsActive { get; init; }
    public float Peak { get; init; }
    public string[] ActiveDeviceIds { get; init; } = [];
    // Active audio clients can be completely silent even when another endpoint
    // is playing this process. Keep observed audio separate from client state.
    public string[] SignalDeviceIds { get; init; } = [];
    public string[] PlayingDeviceIds => SignalDeviceIds.Length > 0 ? SignalDeviceIds : ActiveDeviceIds;
    public string[] AudibleDeviceIds { get; init; } = [];
    public long StartTimeTicks { get; init; }
    public int[] RoutingProcessIds { get; init; } = [];
}

/// <summary>Read-only snapshots. COM endpoint/session wrappers never escape this class.</summary>
public static class AudioDeviceService
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int Pid, string Device), long> LastSignal = new();
    public static List<AudioDeviceInfo> GetDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var result = new List<AudioDeviceInfo>();
        foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
        {
            string? defaultId = null;
            if (enumerator.TryGetDefaultAudioEndpoint(flow, Role.Multimedia, out var defaultDevice))
            {
                using (defaultDevice) defaultId = defaultDevice!.ID;
            }
            using var endpoints = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
            for (var i = 0; i < endpoints.Count; i++)
            {
                try
                {
                    using var device = endpoints[i];
                    result.Add(new(device.ID, device.FriendlyName, flow == DataFlow.Capture, device.ID == defaultId));
                }
                catch (COMException) { /* An endpoint can disappear during a refresh. */ }
            }
        }
        return result.OrderBy(x => x.IsInput).ThenByDescending(x => x.IsDefault).ThenBy(x => x.Name).ToList();
    }

    public static List<AudioApplicationInfo> GetApplications()
    {
        var processes = ReadProcesses();
        var groups = new Dictionary<int, AudioApplicationInfo>();
        var activePaths = new HashSet<(int Pid, string Device)>();
        var now = Stopwatch.GetTimestamp();
        VisitSessions((session, device) =>
        {
            if (session.IsSystemSoundsSession) return;
            var pid = checked((int)session.GetProcessID);
            if (pid <= 0 || pid == Environment.ProcessId || !processes.ContainsKey(pid)) return;
            if ((int)session.State == 2) return; // Expired sessions are not selectable.
            var root = FindApplicationRoot(pid, processes);
            if (root == Environment.ProcessId) return;
            var process = processes[root];
            var isActive = (int)session.State == 1;
            float peak = 0;
            bool muted = false;
            try { peak = session.AudioMeterInformation.MasterPeakValue; } catch (Exception ex) when (ex is COMException or NotSupportedException) { }
            try { muted = session.SimpleAudioVolume.Mute; } catch (Exception ex) when (ex is COMException or NotSupportedException) { }
            var path = (pid, device.ID);
            if (isActive)
            {
                activePaths.Add(path);
                if (!muted && peak > 0.00001f) LastSignal[path] = now;
            }
            if (!groups.TryGetValue(root, out var existing) || (isActive && !existing.IsActive))
            {
                groups[root] = new(root, DisplayName(process.Name, root))
                {
                    ProcessName = process.Name,
                    DeviceId = device.ID,
                    DeviceName = device.FriendlyName,
                    IsActive = isActive,
                    RoutingProcessIds = existing?.RoutingProcessIds ?? [],
                    StartTimeTicks = StartTime(root)
                };
            }
            var app = groups[root];
            groups[root] = app with {
                Peak = Math.Max(app.Peak, peak),
                RoutingProcessIds = app.RoutingProcessIds.Append(pid).Distinct().ToArray(),
                ActiveDeviceIds = isActive ? app.ActiveDeviceIds.Append(device.ID).Distinct().ToArray() : app.ActiveDeviceIds,
                SignalDeviceIds = isActive && !muted && LastSignal.TryGetValue(path, out var last) &&
                    now - last < 2 * Stopwatch.Frequency
                    ? app.SignalDeviceIds.Append(device.ID).Distinct().ToArray() : app.SignalDeviceIds,
                AudibleDeviceIds = isActive && !muted ? app.AudibleDeviceIds.Append(device.ID).Distinct().ToArray() : app.AudibleDeviceIds
            };
        });
        foreach (var path in LastSignal.Keys)
            if (!activePaths.Contains(path) && LastSignal.TryGetValue(path, out var last) && last <= now)
                LastSignal.TryRemove(path, out _);
        // Windows cannot read per-app output preferences until an audio session exists.
        // Only offer registered audio-session applications, not every visible window.
        return groups.Values.OrderByDescending(x => x.IsActive).ThenBy(x => x.Name).ToList();
    }

    internal static void VisitSessions(Action<AudioSessionControl, MMDevice> visitor, DataFlow flow = DataFlow.Render)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var endpoints = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
        for (var i = 0; i < endpoints.Count; i++)
        {
            try
            {
                using var device = endpoints[i];
                // MMDevice owns the manager; the manager owns its current Sessions collection.
                var sessions = device.AudioSessionManager.Sessions;
                for (var j = 0; j < sessions.Count; j++)
                {
                    try
                    {
                        using var session = sessions[j];
                        visitor(session, device);
                    }
                    catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException) { }
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException) { }
        }
    }

    private static string DisplayName(string executable, int pid) => executable.ToLowerInvariant() switch
    {
        "msedge" => "Microsoft Edge", "chrome" => "Google Chrome", "firefox" => "Firefox",
        "cloudmusic" => "网易云音乐", "qqmusic" => "QQ 音乐", "spotify" => "Spotify",
        "wmplayer" => "Windows Media Player", "vlc" => "VLC", "potplayer64" => "PotPlayer",
        "steam" => "Steam", "discord" => "Discord", "wechat" or "weixin" => "微信",
        "wallpaper32" or "wallpaper64" => "Wallpaper Engine",
        _ => FriendlyProcessName(executable, pid)
    };

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> ProductNames = new();
    private static string FriendlyProcessName(string executable, int pid)
    {
        if (ProductNames.TryGetValue(executable, out var cached)) return cached;
        try
        {
            using var process = Process.GetProcessById(pid);
            var label = process.MainModule?.FileVersionInfo.FileDescription?.Trim();
            if (!string.IsNullOrWhiteSpace(label)) return ProductNames.GetOrAdd(executable, label);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception) { }
        return executable;
    }

    private static long StartTime(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.StartTime.ToUniversalTime().Ticks; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return 0; }
    }

    private static int FindApplicationRoot(int pid, Dictionary<int, ProcessEntry> processes)
    {
        var visited = new HashSet<int>();
        while (processes.TryGetValue(pid, out var process) && visited.Add(pid)
               && process.ParentId != Environment.ProcessId && processes.TryGetValue(process.ParentId, out var parent)
               && string.Equals(process.Name, parent.Name, StringComparison.OrdinalIgnoreCase))
            pid = parent.Id;
        return pid;
    }

    private sealed record ProcessEntry(int Id, int ParentId, string Name);

    private static Dictionary<int, ProcessEntry> ReadProcesses()
    {
        var result = new Dictionary<int, ProcessEntry>();
        var snapshot = CreateToolhelp32Snapshot(2, 0); // TH32CS_SNAPPROCESS
        if (snapshot == new IntPtr(-1)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32FirstW(snapshot, ref entry)) return result;
            do
            {
                var id = checked((int)entry.ProcessId);
                if (id > 0) result[id] = new(id, checked((int)entry.ParentProcessId), Path.GetFileNameWithoutExtension(entry.ExeFile));
                entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
            } while (Process32NextW(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size, Usage, ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
