using AudioRouter.Audio;

namespace AudioRouter.Windows;

// Calls are serialized by the window. Discovery alone never changes a session.
public sealed class LiveRouter
{
    public AudioEngine Engine { get; } = new();
    private readonly Dictionary<string, OriginalAudio> _originals = [];

    private sealed class OriginalAudio
    {
        public Dictionary<int, ApplicationRouteSnapshot> Routes { get; } = [];
        public HashSet<string> MutedSessions { get; } = [];
        public string? LastPolicyTarget { get; set; }
    }

    public async Task<string?> ConnectAsync(SourceConfig source, AudioApplicationInfo? app,
        IReadOnlyList<OutputConfig> selected)
    {
        var copies = selected.ToList();
        string? pending = null;
        if (source.Kind == SourceKind.Process)
        {
            if (app == null || app.ProcessId != source.ProcessId)
                throw new InvalidOperationException("应用的音频会话已结束，请先恢复播放。");
            if (!_originals.TryGetValue(source.Id, out var original))
                _originals[source.Id] = original = new();
            if (selected.Count == 0)
            {
                MuteOriginal(app, original);
            }
            else
            {
                RestoreMute(original);
                var originalDevices = app.PlayingDeviceIds.Length > 0 ? app.PlayingDeviceIds :
                    app.DeviceId is string deviceId ? [deviceId] : Array.Empty<string>();
                var chosen = selected.Select(o => o.DeviceId).ToHashSet();
                bool move = originalDevices.Length == 0 || originalDevices.Any(id => !chosen.Contains(id));
                if (move || (original.LastPolicyTarget != null && !chosen.Contains(original.LastPolicyTarget)))
                {
                    var target = originalDevices.FirstOrDefault(chosen.Contains)
                        ?? selected.FirstOrDefault(o => !IsBaseCableRender(o.Name))?.DeviceId
                        ?? selected[0].DeviceId;
                    // A multimedia helper can disappear or reject output policy while its
                    // parent continues playing. This must not abort the process-loopback copy.
                    foreach (var pid in app.RoutingProcessIds)
                    {
                        try
                        {
                            if (!original.Routes.ContainsKey(pid)) original.Routes[pid] = AudioPolicyRouter.Capture(pid);
                            if (AudioPolicyRouter.TrySetApplicationOutput(pid, target, out var error))
                                original.LastPolicyTarget = target;
                            else pending = error;
                        }
                        catch (Exception ex) { pending = ex.Message; }
                    }
                    // Persisting a preference does not prove the currently open stream actually moved.
                    if (app.IsActive && move)
                    {
                        originalDevices = await ObserveMoveAsync(app, target).ConfigureAwait(false);
                        if (originalDevices.Any(id => !chosen.Contains(id)))
                            pending = $"原声仍在 {app.DeviceName}。已向所选设备转发，请在播放器内切换输出或重开播放流，使原设备断开。";
                    }
                    else if (pending == null) originalDevices = [target];
                }
                copies.RemoveAll(output => originalDevices.Contains(output.DeviceId));
                // The standard VB-CABLE's stereo and 16-channel render ports share
                // CABLE Output. An app pinned to one of them is already in that mix.
                if (copies.Any(o => IsBaseCableRender(o.Name)))
                {
                    bool alreadyOnCable = AudioDeviceService.GetDevices().Any(d =>
                        originalDevices.Contains(d.Id) && !d.IsInput && IsBaseCableRender(d.Name));
                    if (alreadyOnCable) copies.RemoveAll(o => IsBaseCableRender(o.Name));
                }
            }
        }
        var firstCable = copies.FirstOrDefault(o => IsBaseCableRender(o.Name));
        if (firstCable != null)
            copies.RemoveAll(o => o.Id != firstCable.Id && IsBaseCableRender(o.Name));
        source.DestinationIds = copies.Select(o => o.Id).ToList();
        await Engine.ConnectAsync(source, copies).ConfigureAwait(false);
        return pending;
    }

    private static bool IsBaseCableRender(string name) =>
        name.EndsWith("(VB-Audio Virtual Cable)", StringComparison.OrdinalIgnoreCase) &&
        (name.StartsWith("CABLE Input", StringComparison.OrdinalIgnoreCase) ||
         name.StartsWith("CABLE In 16ch", StringComparison.OrdinalIgnoreCase));

    private static async Task<string[]> ObserveMoveAsync(AudioApplicationInfo app, string target)
    {
        var observed = app.PlayingDeviceIds;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            await Task.Delay(120).ConfigureAwait(false);
            var current = AudioDeviceService.GetApplications().FirstOrDefault(a => a.ProcessId == app.ProcessId);
            if (current?.IsActive != true || current.PlayingDeviceIds.Length == 0) continue;
            observed = current.PlayingDeviceIds;
            if (observed.All(d => d == target)) return observed;
        }
        return observed;
    }

    private static void MuteOriginal(AudioApplicationInfo app, OriginalAudio original)
    {
        int matched = 0;
        Exception? failure = null;
        AudioDeviceService.VisitSessions((session, device) =>
        {
            if (!app.RoutingProcessIds.Contains((int)session.GetProcessID) || (int)session.State == 2) return;
            try
            {
                matched++;
                if (session.SimpleAudioVolume.Mute) return;
                string key = device.ID + "|" + session.GetSessionInstanceIdentifier;
                session.SimpleAudioVolume.Mute = true;
                original.MutedSessions.Add(key);
            }
            catch (Exception ex) { failure = ex; }
        });
        if (failure != null) throw new InvalidOperationException("无法关闭应用原声。", failure);
        if (matched == 0) throw new InvalidOperationException("应用音频会话已结束。");
    }

    private static void RestoreMute(OriginalAudio original)
    {
        if (original.MutedSessions.Count == 0) return;
        Exception? failure = null;
        AudioDeviceService.VisitSessions((session, device) =>
        {
            string key = device.ID + "|" + session.GetSessionInstanceIdentifier;
            if (!original.MutedSessions.Contains(key)) return;
            try { session.SimpleAudioVolume.Mute = false; original.MutedSessions.Remove(key); }
            catch (Exception ex) { failure = ex; }
        });
        if (failure != null) throw new InvalidOperationException("无法恢复应用的静音状态。", failure);
        original.MutedSessions.Clear();
    }

    public async Task ReleaseAsync(string id)
    {
        await Engine.DisconnectAsync(id).ConfigureAwait(false);
        if (!_originals.TryGetValue(id, out var original)) return;
        RestoreMute(original);
        var failures = new List<string>();
        foreach (var entry in original.Routes.ToArray())
        {
            if (AudioPolicyRouter.TryRestore(entry.Value, out var error)) original.Routes.Remove(entry.Key);
            else failures.Add(error ?? "无法恢复应用的输出设置。");
        }
        if (failures.Count > 0) throw new InvalidOperationException(string.Join(" / ", failures));
        _originals.Remove(id);
    }
}
