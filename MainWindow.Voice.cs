using System.Windows;
using System.Windows.Controls;
using AudioRouter.Audio;
using AudioRouter.Windows;

namespace AudioRouter;

public partial class MainWindow
{
    private sealed record MicChoice(string Name, SourceVm? Row);
    private sealed record QuickRoute(SourceVm Row, bool WasManaged, HashSet<string> Before, HashSet<string> Applied);
    private readonly List<QuickRoute> _quickRoutes = [];
    private CableSignalMonitor? _cableMeter;
    private bool _inputBound;
    private int _meterTick;
    private string _otherCableApps = "";

    private void CheckOtherCableSources(IEnumerable<AudioApplicationInfo> apps)
    {
        _otherCableApps = "";
        if (!_inputBound) return;
        var cableIds = _devices.Where(d => !d.IsInput && d.Name.EndsWith("(VB-Audio Virtual Cable)", StringComparison.OrdinalIgnoreCase))
            .Select(d => d.Id).ToHashSet();
        var chosen = _vm.Sources.Where(r => r.Managed && Selected(r).Any(p => cableIds.Contains(p.Id)))
            .Select(r => r.App?.ProcessId).ToHashSet();
        var extra = apps.Where(a => !chosen.Contains(a.ProcessId) && a.SignalDeviceIds.Any(cableIds.Contains));
        _otherCableApps = string.Join("、", extra.Select(a => a.Name).Distinct());
    }

    private void RefreshVoiceChoices()
    {
        var sources = _vm.Sources.Where(s => s.Kind != SourceKind.Microphone).ToList();
        if (ShareSourcePicker.ItemsSource is not List<SourceVm> old || !old.SequenceEqual(sources))
        {
            var selected = ShareSourcePicker.SelectedItem as SourceVm;
            ShareSourcePicker.ItemsSource = sources;
            ShareSourcePicker.SelectedItem = selected != null && sources.Contains(selected) ? selected :
                sources.FirstOrDefault(s => s.Kind == SourceKind.File || s.App?.ProcessName.ToLowerInvariant() is
                    "cloudmusic" or "qqmusic" or "spotify" or "vlc" or "potplayer64" or "wmplayer");
        }
        var mics = _vm.Sources.Where(s => s.Kind == SourceKind.Microphone).ToList();
        var choices = VoiceMicPicker.ItemsSource as List<MicChoice>;
        if (choices == null || !choices.Skip(1).Select(m => m.Row).SequenceEqual(mics))
        {
            var previous = VoiceMicPicker.SelectedItem as MicChoice;
            var updated = new List<MicChoice> { new("仅发送应用声音", null) };
            updated.AddRange(mics.Select(s => new MicChoice(s.Name, s)));
            VoiceMicPicker.ItemsSource = updated;
            VoiceMicPicker.SelectedItem = previous != null && updated.Any(m => m.Row == previous.Row)
                ? updated.First(m => m.Row == previous.Row)
                : updated.FirstOrDefault(m => m.Row != null && IsHeadphone(m.Name))
                    ?? updated.FirstOrDefault(m => _devices.Any(d => d.Id == m.Row?.DeviceId && d.IsDefault)) ?? updated[0];
        }
        var monitors = _devices.Where(d => !d.IsInput && !d.IsVirtualCable).ToList();
        if (MonitorDevicePicker.ItemsSource is not List<AudioDeviceInfo> physical || !physical.SequenceEqual(monitors))
        {
            var previous = MonitorDevicePicker.SelectedItem as AudioDeviceInfo;
            MonitorDevicePicker.ItemsSource = monitors;
            MonitorDevicePicker.SelectedItem = monitors.FirstOrDefault(d => d.Id == previous?.Id)
                ?? monitors.FirstOrDefault(d => IsHeadphone(d.Name)) ?? monitors.FirstOrDefault(d => d.IsDefault) ?? monitors.FirstOrDefault();
        }
    }

    private async void ConnectGameMic_Click(object sender, RoutedEventArgs e) => await RunActionAsync(async () =>
    {
        if (_inputBound || _quickRoutes.Count > 0 || GameInputBinding.HasRestorePoint)
        {
            await StopGameMixAsync();
            return;
        }
        if (ShareSourcePicker.SelectedItem is not SourceVm source)
            throw new InvalidOperationException("先播放音乐或添加音频文件，再选择要发给队友的声音来源。");
        if (source.Kind == SourceKind.Process && source.App?.IsActive != true)
            throw new InvalidOperationException("选中的应用已暂停，请让它开始播放。");
        if (MonitorDevicePicker.SelectedItem is not AudioDeviceInfo monitor)
            throw new InvalidOperationException("请选择耳机或音响作为本地播放设备。");
        var cable = _devices.FirstOrDefault(d => !d.IsInput && d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase)
            && d.Name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase));
        var input = _devices.FirstOrDefault(d => d.IsInput && d.Name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase)
            && d.Name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase));
        if (cable == null || input == null) throw new InvalidOperationException("未找到配对的 CABLE Input 和 CABLE Output，请检查 VB-CABLE。");
        var mic = (VoiceMicPicker.SelectedItem as MicChoice)?.Row;
        if (source.Muted || source.Gain == 0 || _vm.Outputs.First(p => p.Id == cable.Id).Gain == 0)
            throw new InvalidOperationException("声音来源或游戏麦转发音量为零，请在路由表取消静音或提高音量。");

        try
        {
            var targets = Selected(source).Where(p => !_devices.Any(d => d.Id == p.Id && d.IsMultichannelCable))
                .Select(p => p.Id).Append(monitor.Id).Append(cable.Id).ToArray();
            await Task.Run(() => GameInputBinding.Enable(input.Id, monitor.Id, cable.Id));
            _inputBound = true;
            await AddGameRouteAsync(source, targets);
            if (mic != null) await AddGameRouteAsync(mic, Selected(mic).Select(p => p.Id).Append(cable.Id));
            _cableMeter = await Task.Run(() => CableSignalMonitor.OpenAsync(input.Id));
            _vm.StatusText = $"正在发送 {source.Name}。游戏话筒选 CABLE Output；已打开的语音可能需要重新接通。";
        }
        catch
        {
            await StopGameMixAsync();
            throw;
        }
        finally { UpdateVoiceButtons(); }
    });

    private async Task AddGameRouteAsync(SourceVm row, IEnumerable<string> targets)
    {
        var before = Selected(row).Select(p => p.Id).ToHashSet();
        var after = targets.ToHashSet();
        _quickRoutes.Add(new(row, row.Managed, before, after));
        SetChecks(row, after);
        await ApplyRowAsync(row);
        if (!row.Managed || !Selected(row).Select(p => p.Id).ToHashSet().SetEquals(after))
            throw new InvalidOperationException($"{row.Name} 接入失败 · {row.Status}");
    }

    private async Task StopGameMixAsync()
    {
        Exception? restoreError = null;
        if (_inputBound || GameInputBinding.HasRestorePoint)
        {
            try { await Task.Run(() => GameInputBinding.Restore()); }
            catch (Exception ex) { restoreError = ex; }
        }
        _inputBound = false;
        if (_cableMeter != null) { await _cableMeter.DisposeAsync(); _cableMeter = null; }
        foreach (var change in _quickRoutes.ToArray())
        {
            if (_vm.Sources.Contains(change.Row) && change.Row.Managed)
            {
                var current = Selected(change.Row).Select(p => p.Id).ToHashSet();
                if (!change.WasManaged && current.SetEquals(change.Applied)) await ReleaseRowAsync(change.Row);
                else
                {
                    current.ExceptWith(change.Applied.Except(change.Before));
                    current.UnionWith(change.Before.Except(change.Applied));
                    SetChecks(change.Row, current);
                    await ApplyRowAsync(change.Row);
                }
            }
            _quickRoutes.Remove(change);
        }
        UpdateVoiceButtons();
        CableSignalText.Text = "游戏麦已断开";
        _vm.StatusText = "已停止快捷发送并恢复录音设置";
        if (restoreError != null) throw new InvalidOperationException("已停止快捷发送。恢复设备时遇到问题 · " + restoreError.Message, restoreError);
    }

    private async void ToggleMonitoring_Click(object sender, RoutedEventArgs e) => await RunActionAsync(async () =>
    {
        if ((VoiceMicPicker.SelectedItem as MicChoice)?.Row is not SourceVm mic || MonitorDevicePicker.SelectedItem is not AudioDeviceInfo monitor)
            throw new InvalidOperationException("开启耳返前，请选一个实体麦克风和耳机。");
        var targets = Selected(mic).Select(p => p.Id).ToHashSet();
        if (!targets.Remove(monitor.Id)) targets.Add(monitor.Id);
        SetChecks(mic, targets);
        await ApplyRowAsync(mic);
        UpdateVoiceButtons();
    });

    private void UpdateVoiceButtons()
    {
        bool sharing = _inputBound || _quickRoutes.Count != 0;
        ConnectGameButton.Content = sharing ? "断开游戏麦" : GameInputBinding.HasRestorePoint ? "恢复系统话筒" : "连接游戏麦";
        ShareSourcePicker.IsEnabled = !sharing;
        var mic = (VoiceMicPicker.SelectedItem as MicChoice)?.Row;
        var monitor = MonitorDevicePicker.SelectedItem as AudioDeviceInfo;
        bool audible = mic != null && Selected(mic).Any(p => p.Id == monitor?.Id);
        VoiceMicPicker.IsEnabled = !sharing && !audible;
        MonitorDevicePicker.IsEnabled = !sharing && !audible;
        MonitorButton.Content = audible ? "关闭耳返" : "开启耳返";
        MonitorButton.IsEnabled = mic != null && monitor != null;
    }

    private void MonitorVolume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VoiceMicPicker?.SelectedItem is MicChoice { Row: { } row } && MonitorDevicePicker?.SelectedItem is AudioDeviceInfo monitor)
            _router.Engine.SetRouteGain(row.Id, monitor.Id, (float)e.NewValue / 100);
    }

    private void ApplyMonitorVolume(SourceVm row)
    {
        if (row.Kind == SourceKind.Microphone && VoiceMicPicker.SelectedItem is MicChoice choice && choice.Row == row
            && MonitorDevicePicker.SelectedItem is AudioDeviceInfo monitor)
            _router.Engine.SetRouteGain(row.Id, monitor.Id, (float)MonitorVolume.Value / 100);
    }
}
