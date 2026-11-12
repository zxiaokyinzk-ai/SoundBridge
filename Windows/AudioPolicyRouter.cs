using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace AudioRouter.Windows;

public sealed record ApplicationSessionVolume(string SessionId, float Volume, bool Muted);
public sealed record ApplicationRouteSnapshot(int ProcessId, long StartTimeTicks, string? ConsoleDeviceId,
    string? MultimediaDeviceId, IReadOnlyList<ApplicationSessionVolume> Sessions);

/// <summary>
/// Per-application output only; never changes system defaults or communications-device preferences.
/// This Windows internal ABI may change in a future Windows release. All failures are surfaced.
/// </summary>
public static class AudioPolicyRouter
{
    public static bool TryGetApplicationOutput(int processId, out string? deviceId, out string? error)
    {
        deviceId = null;
        error = null;
        try
        {
            ValidateProcess(processId);
            using var policy = new NativePolicy();
            deviceId = policy.Get(processId, 1);
            return true; // null means the application follows the Windows default.
        }
        catch (Exception ex) { error = Explain(ex); return false; }
    }

    public static ApplicationRouteSnapshot Capture(int processId)
    {
        var startTime = ValidateProcess(processId);
        using var policy = new NativePolicy();
        var console = policy.Get(processId, 0);
        var multimedia = policy.Get(processId, 1);
        var sessions = new List<ApplicationSessionVolume>();
        AudioDeviceService.VisitSessions((session, _) =>
        {
            if (session.GetProcessID != processId || (int)session.State == 2) return;
            var volume = session.SimpleAudioVolume;
            sessions.Add(new(session.GetSessionIdentifier, volume.Volume, volume.Mute));
        });
        return new(processId, startTime, console, multimedia, sessions);
    }

    public static bool TrySetApplicationOutput(int processId, string? deviceId, out string? error)
    {
        error = null;
        try
        {
            ValidateProcess(processId);
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDevice(deviceId);
                if (device.DataFlow != DataFlow.Render || device.State != DeviceState.Active)
                    throw new InvalidOperationException("所选播放设备已断开，请刷新设备列表。");
            }
            using var policy = new NativePolicy();
            var oldConsole = policy.Get(processId, 0);
            var oldMultimedia = policy.Get(processId, 1);
            try
            {
                policy.Set(processId, 0, deviceId);
                policy.Set(processId, 1, deviceId);
            }
            catch
            {
                try { policy.Set(processId, 0, oldConsole); policy.Set(processId, 1, oldMultimedia); }
                catch { /* The original error remains the actionable failure. */ }
                throw;
            }
            return true;
        }
        catch (Exception ex) { error = Explain(ex); return false; }
    }

    public static bool TrySetApplicationVolume(int processId, float gain, bool muted, out string? error)
    {
        error = null;
        try
        {
            ValidateProcess(processId);
            var found = 0;
            Exception? failure = null;
            AudioDeviceService.VisitSessions((session, _) =>
            {
                if (session.GetProcessID != processId || (int)session.State == 2) return;
                try
                {
                    var volume = session.SimpleAudioVolume;
                    volume.Volume = Math.Clamp(gain, 0f, 1f);
                    volume.Mute = muted;
                    found++;
                }
                catch (Exception ex) { failure = ex; }
            });
            if (failure is not null) throw failure;
            if (found == 0) { error = "应用尚未创建音频流；开始播放后再调整音量。"; return false; }
            return true;
        }
        catch (Exception ex) { error = Explain(ex); return false; }
    }

    public static bool TryRestore(ApplicationRouteSnapshot snapshot, out string? error)
    {
        error = null;
        try
        {
            if (ValidateProcess(snapshot.ProcessId) != snapshot.StartTimeTicks)
                throw new InvalidOperationException("原应用已退出，未修改复用该进程编号的新应用。");
            using var policy = new NativePolicy();
            policy.Set(snapshot.ProcessId, 0, snapshot.ConsoleDeviceId);
            policy.Set(snapshot.ProcessId, 1, snapshot.MultimediaDeviceId);
            // The UI does not modify native session volumes. Preserve any volume
            // changes the user made in the original application while routing.
            return true;
        }
        catch (Exception ex) { error = Explain(ex); return false; }
    }

    private static long ValidateProcess(int processId)
    {
        if (processId <= 0 || processId == Environment.ProcessId)
            throw new ArgumentException("请选择其他正在运行的音频应用。");
        using var process = Process.GetProcessById(processId);
        return process.StartTime.ToUniversalTime().Ticks;
    }

    private static string Explain(Exception ex) => ex is COMException
        ? $"Windows 无法完成应用路由（0x{ex.HResult:X8}）。可在 Windows 音量混合器中选择该应用的输出设备。"
        : ex.Message;

    // ABI offsets and interface GUIDs independently implemented from EarTrumpet's published
    // interface declarations. See Windows/README.md. Explicit pointers avoid .NET's removed
    // WinRT HSTRING/IInspectable marshalling support.
    private sealed class NativePolicy : IDisposable
    {
        private const string EndpointPrefix = @"\\?\SWD#MMDEVAPI#";
        private const string RenderSuffix = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
        private IntPtr _factory;
        private readonly bool _uninitialize;
        private readonly SetEndpoint _set;
        private readonly GetEndpoint _get;

        internal NativePolicy()
        {
            var init = RoInitialize(1);
            _uninitialize = init >= 0;
            if (init < 0 && init != unchecked((int)0x80010106)) Marshal.ThrowExceptionForHR(init);
            try
            {
                using var classId = new HString("Windows.Media.Internal.AudioPolicyConfig");
                var iid = new Guid(Environment.OSVersion.Version.Build >= 22000
                    ? "ab3d4648-e242-459f-b02f-541c70306324" : "2a59116d-6c4f-45e0-a74f-707e3fef9258");
                Marshal.ThrowExceptionForHR(RoGetActivationFactory(classId.Value, ref iid, out _factory));
                var table = Marshal.ReadIntPtr(_factory);
                _set = Marshal.GetDelegateForFunctionPointer<SetEndpoint>(Marshal.ReadIntPtr(table, 25 * IntPtr.Size));
                _get = Marshal.GetDelegateForFunctionPointer<GetEndpoint>(Marshal.ReadIntPtr(table, 26 * IntPtr.Size));
            }
            catch { Dispose(); throw; }
        }

        internal string? Get(int pid, int role)
        {
            IntPtr value = IntPtr.Zero;
            try
            {
                var hr = _get(_factory, checked((uint)pid), 0, role, out value);
                if (hr == unchecked((int)0x80070490)) return null; // No persisted override.
                Marshal.ThrowExceptionForHR(hr);
                if (value == IntPtr.Zero) return null;
                var raw = WindowsGetStringRawBuffer(value, out var length);
                var id = Marshal.PtrToStringUni(raw, checked((int)length));
                if (string.IsNullOrWhiteSpace(id)) return null;
                if (id.StartsWith(EndpointPrefix, StringComparison.OrdinalIgnoreCase)) id = id[EndpointPrefix.Length..];
                if (id.EndsWith(RenderSuffix, StringComparison.OrdinalIgnoreCase)) id = id[..^RenderSuffix.Length];
                return id;
            }
            finally { if (value != IntPtr.Zero) WindowsDeleteString(value); }
        }

        internal void Set(int pid, int role, string? deviceId)
        {
            using var endpoint = new HString(string.IsNullOrWhiteSpace(deviceId) ? null : EndpointPrefix + deviceId + RenderSuffix);
            Marshal.ThrowExceptionForHR(_set(_factory, checked((uint)pid), 0, role, endpoint.Value));
        }

        public void Dispose()
        {
            if (_factory != IntPtr.Zero) { Marshal.Release(_factory); _factory = IntPtr.Zero; }
            if (_uninitialize) RoUninitialize();
        }

        private sealed class HString : IDisposable
        {
            public IntPtr Value { get; }
            public HString(string? value)
            {
                if (value is null) return;
                Marshal.ThrowExceptionForHR(WindowsCreateString(value, checked((uint)value.Length), out var handle));
                Value = handle;
            }
            public void Dispose() { if (Value != IntPtr.Zero) WindowsDeleteString(Value); }
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetEndpoint(IntPtr self, uint pid, int flow, int role, IntPtr deviceId);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetEndpoint(IntPtr self, uint pid, int flow, int role, out IntPtr deviceId);
        [DllImport("combase.dll")] private static extern int RoInitialize(uint initType);
        [DllImport("combase.dll")] private static extern void RoUninitialize();
        [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);
        [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string source, uint length, out IntPtr value);
        [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr value);
        [DllImport("combase.dll")] private static extern IntPtr WindowsGetStringRawBuffer(IntPtr value, out uint length);
    }
}
