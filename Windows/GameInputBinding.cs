using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.CoreAudioApi;

namespace AudioRouter.Windows;

internal static class GameInputBinding
{
    private sealed record Change(DataFlow Flow, Role Role, string Before, string Applied);
    private sealed record LevelChange(string DeviceId, float? Volume, bool? Mute);
    private sealed record Backup(List<Change> Defaults, List<LevelChange> Levels);
    private static readonly Role[] Roles = [Role.Console, Role.Multimedia, Role.Communications];
    private static readonly string BackupPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundBridge", "voice-defaults.json");

    public static bool HasRestorePoint => File.Exists(BackupPath);

    public static void Enable(string cableCaptureDeviceId, string? physicalPlaybackId = null,
        string? cablePlaybackId = null) => UnderLock(() =>
    {
        using var devices = new MMDeviceEnumerator();
        using var cable = devices.GetDevice(cableCaptureDeviceId);
        if (cable.DataFlow != DataFlow.Capture || cable.State != DeviceState.Active || !IsVirtual(cable))
            throw new InvalidOperationException("虚拟录音设备不可用，请刷新设备。");

        using var playback = physicalPlaybackId is null ? null : devices.GetDevice(physicalPlaybackId);
        if (playback != null && (playback.DataFlow != DataFlow.Render || playback.State != DeviceState.Active || IsVirtual(playback)))
            throw new InvalidOperationException("请选择已连接的实体播放设备。");

        using var cableIn = cablePlaybackId == null ? null : devices.GetDevice(cablePlaybackId);
        if (cableIn != null && (cableIn.DataFlow != DataFlow.Render || cableIn.State != DeviceState.Active ||
            !IsStandardCable(cableIn, "CABLE Input") || !IsStandardCable(cable, "CABLE Output")))
            throw new InvalidOperationException("未找到标准 VB-CABLE 输入和输出配对。");

        var saved = ReadBackup();
        var desired = new List<Change>();
        var levels = new List<LevelChange>();
        foreach (var role in Roles)
        {
            using var current = devices.GetDefaultAudioEndpoint(DataFlow.Capture, role);
            Plan(DataFlow.Capture, role, current.ID, cable.ID);
            if (playback == null) continue;
            using var output = devices.GetDefaultAudioEndpoint(DataFlow.Render, role);
            if (IsCablePlayback(output)) Plan(DataFlow.Render, role, output.ID, playback.ID);
        }
        if (cableIn != null)
        {
            PlanLevel(cableIn);
            PlanLevel(cable);
        }
        if (desired.Count == 0 && levels.Count == 0) return;

        WriteBackup(saved);
        try
        {
            foreach (var change in desired) SetDefault(change.Applied, change.Role);
            foreach (var change in levels)
            {
                using var device = devices.GetDevice(change.DeviceId);
                var volume = device.AudioEndpointVolume;
                if (change.Volume.HasValue) volume.MasterVolumeLevelScalar = 1f;
                if (change.Mute.HasValue) volume.Mute = false;
                if (volume.Mute || volume.MasterVolumeLevelScalar < .9999f)
                    throw new InvalidOperationException("虚拟话筒未应用音量设置。");
            }
            foreach (var change in desired)
            {
                using var current = devices.GetDefaultAudioEndpoint(change.Flow, change.Role);
                if (current.ID != change.Applied) throw new InvalidOperationException("Windows 未应用选择的设备。");
            }
        }
        catch (Exception ex)
        {
            try { RestoreLocked(devices); }
            catch (Exception recovery) { throw new InvalidOperationException($"切换默认设备失败：{ex.Message}。{recovery.Message}", ex); }
            throw new InvalidOperationException("切换系统默认设备失败：" + ex.Message, ex);
        }

        void Plan(DataFlow flow, Role role, string current, string target)
        {
            if (current == target) return;
            var previous = saved.Defaults.FirstOrDefault(c => c.Flow == flow && c.Role == role);
            // Preserve a manual device selection made since the last connection.
            string before = previous?.Applied == current ? previous.Before : current;
            var change = new Change(flow, role, before, target);
            saved.Defaults.RemoveAll(c => c.Flow == flow && c.Role == role);
            saved.Defaults.Add(change);
            desired.Add(change);
        }

        void PlanLevel(MMDevice device)
        {
            var volume = device.AudioEndpointVolume;
            float current = volume.MasterVolumeLevelScalar;
            bool muted = volume.Mute;
            if (current >= .9999f && !muted) return;
            var previous = saved.Levels.FirstOrDefault(l => l.DeviceId == device.ID);
            var changes = new LevelChange(device.ID, current < .9999f ? current : null, muted ? true : null);
            levels.Add(changes);
            saved.Levels.RemoveAll(l => l.DeviceId == device.ID);
            saved.Levels.Add(new(device.ID, changes.Volume ?? previous?.Volume, changes.Mute ?? previous?.Mute));
        }
    });

    public static void Restore() => UnderLock(() =>
    {
        if (!HasRestorePoint) return;
        using var devices = new MMDeviceEnumerator();
        RestoreLocked(devices);
    });

    private static void RestoreLocked(MMDeviceEnumerator devices)
    {
        var saved = ReadBackup();
        var remaining = new Backup([], []);
        foreach (var change in saved.Defaults)
        {
            try
            {
                using var current = devices.GetDefaultAudioEndpoint(change.Flow, change.Role);
                if (current.ID != change.Applied) continue;
                using var original = devices.GetDevice(change.Before);
                if (original.DataFlow != change.Flow || original.State != DeviceState.Active)
                    throw new InvalidOperationException();
                SetDefault(original.ID, change.Role);
                using var verified = devices.GetDefaultAudioEndpoint(change.Flow, change.Role);
                if (verified.ID != original.ID) throw new InvalidOperationException();
            }
            catch (Exception) { remaining.Defaults.Add(change); }
        }
        foreach (var change in saved.Levels)
        {
            try
            {
                using var device = devices.GetDevice(change.DeviceId);
                if (device.State != DeviceState.Active) throw new InvalidOperationException();
                var volume = device.AudioEndpointVolume;
                if (change.Mute is true && !volume.Mute) volume.Mute = true;
                if (change.Volume is float original && volume.MasterVolumeLevelScalar >= .9999f)
                    volume.MasterVolumeLevelScalar = original;
            }
            catch (Exception) { remaining.Levels.Add(change); }
        }
        if (remaining.Defaults.Count == 0 && remaining.Levels.Count == 0) File.Delete(BackupPath);
        else
        {
            WriteBackup(remaining);
            throw new InvalidOperationException("部分原设备未恢复。请确认原设备已连接，再点恢复。");
        }
    }

    private static bool IsVirtual(MMDevice device) =>
        new AudioDeviceInfo(device.ID, device.FriendlyName, device.DataFlow == DataFlow.Capture, false).IsVirtualCable;

    private static bool IsCablePlayback(MMDevice device) =>
        device.FriendlyName.Contains("CABLE", StringComparison.OrdinalIgnoreCase);

    private static bool IsStandardCable(MMDevice device, string name) =>
        device.FriendlyName.StartsWith(name + " (", StringComparison.OrdinalIgnoreCase)
        && device.FriendlyName.Contains("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase);

    private static Backup ReadBackup()
    {
        if (!HasRestorePoint) return new([], []);
        using var json = JsonDocument.Parse(File.ReadAllText(BackupPath));
        return json.RootElement.ValueKind == JsonValueKind.Array
            ? new(json.RootElement.Deserialize<List<Change>>()!, [])
            : json.RootElement.Deserialize<Backup>() ?? throw new InvalidDataException("无法读取上次的设备恢复记录。");
    }

    private static void WriteBackup(Backup changes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(BackupPath)!);
        var temp = BackupPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(changes));
        File.Move(temp, BackupPath, overwrite: true);
    }

    private static void UnderLock(Action operation)
    {
        using var mutex = new Mutex(false, @"Local\SoundBridge.GameInputDefaults");
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(4))) throw new InvalidOperationException("另一窗口正在切换设备，请稍后再试。");
        }
        catch (AbandonedMutexException) { /* A previous process exited while holding the lock. */ }
        try { operation(); }
        finally { mutex.ReleaseMutex(); }
    }

    // IPolicyConfig::SetDefaultEndpoint. The endpoint ID determines capture vs. playback.
    private static void SetDefault(string id, Role role)
    {
        var clsid = new Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");
        var iid = new Guid("F8679F50-850A-41CF-9C72-430F290290C8");
        Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out var policy));
        try
        {
            var method = Marshal.ReadIntPtr(Marshal.ReadIntPtr(policy), 13 * IntPtr.Size);
            Marshal.ThrowExceptionForHR(Marshal.GetDelegateForFunctionPointer<SetEndpoint>(method)(policy, id, role));
        }
        finally { Marshal.Release(policy); }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetEndpoint(IntPtr policy, [MarshalAs(UnmanagedType.LPWStr)] string id, Role role);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, int context, ref Guid iid, out IntPtr policy);
}
