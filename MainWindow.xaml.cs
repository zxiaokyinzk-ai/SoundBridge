using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using AudioRouter.Audio;
using AudioRouter.Windows;
using Microsoft.Win32;

namespace AudioRouter;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private readonly LiveRouter _router = new();
    private readonly SemaphoreSlim _operations = new(1);
    private readonly DispatcherTimer _scan = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly DispatcherTimer _meters = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private bool _syncing, _closing, _closed;
    private int _scanCount;
    private List<AudioDeviceInfo> _devices = [];

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Loaded += async (_, _) =>
        {
            await ScanAsync(true);
            _scan.Start();
            _meters.Start();
        };
        _scan.Tick += async (_, _) => await ScanAsync(++_scanCount % 10 == 0);
        _meters.Tick += (_, _) => UpdateMeters();
        _router.Engine.Error += (_, error) => Dispatcher.BeginInvoke(async () =>
        {
            await RunActionAsync(async () => { await ReleaseAllAsync(); _vm.StatusText = error; });
        });
        Closing += WindowClosing;
    }

    private async Task ScanAsync(bool refreshDevices)
    {
        if (_closing || !await _operations.WaitAsync(0)) return;
        try
        {
            if (refreshDevices) await RefreshDevicesAsync();
            var apps = await Task.Run(AudioDeviceService.GetApplications);
            await UpdateAppsAsync(apps);
            if (_inputBound && _quickRoutes.FirstOrDefault()?.Row is { } sending && !_vm.Sources.Contains(sending))
            {
                await StopGameMixAsync();
                _vm.StatusText = $"{sending.Name} 已关闭，快捷发送已停止。";
            }
            RefreshVoiceChoices();
            UpdateVoiceButtons();
            CheckOtherCableSources(apps);
            if (GameMicPanel.IsExpanded)
            {
                try { GameMicResult.Text = (await Task.Run(GameMicrophoneCheck.Read)).Describe(); }
                catch (Exception ex) { GameMicResult.Text = "暂时无法读取录音设备 · " + ex.Message; }
            }
            var active = apps.Count(a => a.IsActive);
            _vm.Detection = active > 0 ? $"检测到 {active} 个活动应用" : "播放音乐或视频后，可在上方选择分享来源";
        }
        catch (Exception ex) { _vm.Detection = "无法读取音频会话，正在重试 · " + ex.Message; }
        finally { _operations.Release(); }
    }

    private async Task RefreshDevicesAsync()
    {
        var devices = await Task.Run(AudioDeviceService.GetDevices);
        if (_devices.Count > 0 && devices.SequenceEqual(_devices)) return;
        _devices = devices;
        var gains = _vm.Outputs.ToDictionary(p => p.Id, p => p.Gain);
        var selections = _vm.Sources.ToDictionary(s => s.Id, s => Selected(s).Select(p => p.Id).ToHashSet());
        var render = devices.Where(d => !d.IsInput).OrderByDescending(d => d.IsVirtualCable && !d.IsMultichannelCable)
            .ThenByDescending(d => IsHeadphone(d.Name))
            .ThenByDescending(d => d.IsDefault).ThenBy(d => d.IsVirtualCable).ThenBy(d => d.IsMultichannelCable);
        _vm.Outputs.Clear();
        foreach (var device in render)
        {
            var label = device.IsVirtualCable ? (device.IsMultichannelCable ? "游戏麦 · 16 声道" : "游戏麦克风") : IsHeadphone(device.Name) ? "耳机" : device.IsDefault ? "音响 · 默认" : "播放设备";
            var port = new OutputPort(device.Id, label, device.Name, true, device.IsVirtualCable) { Gain = gains.GetValueOrDefault(device.Id, 100) };
            SubscribeOutput(port);
            _vm.Outputs.Add(port);
        }
        if (!_vm.Outputs.Any(p => p.IsGame)) _vm.Outputs.Add(new("missing-game", "游戏麦克风", "需要虚拟声卡", false, true));
        _vm.CableStatusText = _vm.Outputs.Any(p => p.IsGame && p.Available)
            ? "将音乐应用和麦克风勾到同一个游戏麦克风列，在游戏里选择配对的录音设备（VB-CABLE 为 CABLE Output）。"
            : "游戏麦克风需要虚拟声卡。安装 VB-CABLE 后，此处会出现 CABLE Input。游戏的麦克风选择 CABLE Output。";
        RebuildColumns();
        foreach (var row in _vm.Sources.ToArray())
        {
            if (row.Kind == SourceKind.Microphone && !devices.Any(d => d.Id == row.DeviceId))
            {
                await ReleaseRowAsync(row);
                _vm.Sources.Remove(row);
                continue;
            }
            BindPorts(row, selections[row.Id]);
            if (row.Managed) await ApplyRowAsync(row);
        }
        foreach (var mic in devices.Where(d => d.IsInput && !d.IsVirtualCable))
        {
            if (_vm.Sources.Any(r => r.DeviceId == mic.Id)) continue;
            AddRow(new SourceVm { Id = "mic:" + mic.Id, Name = mic.Name, Kind = SourceKind.Microphone,
                DeviceId = mic.Id, Status = "麦克风 · 勾选输出口后采集" });
        }
        if (!_inputBound) _vm.StatusText = "设备已就绪";
    }

    private static bool IsHeadphone(string name) =>
        new[] { "耳机", "HECATE", "headphone", "headset" }.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase));

    private void SubscribeOutput(OutputPort port) => port.PropertyChanged += (_, e) =>
    {
        if (e.PropertyName == nameof(OutputPort.Gain)) _router.Engine.SetOutputGain(port.Id, (float)(port.Gain / 100));
    };

    private void RebuildColumns()
    {
        while (Matrix.Columns.Count > 2) Matrix.Columns.RemoveAt(2);
        for (int i = 0; i < _vm.Outputs.Count; i++)
        {
            var port = _vm.Outputs[i];
            var header = new StackPanel { ToolTip = port.DeviceName };
            header.Children.Add(new TextBlock { Text = port.Label, FontWeight = FontWeights.SemiBold });
            header.Children.Add(new TextBlock { Text = port.DeviceName, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
            var toggle = new FrameworkElementFactory(typeof(CheckBox));
            toggle.SetValue(FrameworkElement.StyleProperty, FindResource("RouteCheck"));
            toggle.SetValue(UIElement.IsEnabledProperty, port.Available);
            toggle.SetValue(AutomationProperties.NameProperty, "连接 " + port.DeviceName);
            toggle.SetValue(FrameworkElement.ToolTipProperty, port.Available ? port.DeviceName : "安装虚拟声卡后可用");
            toggle.SetBinding(CheckBox.IsCheckedProperty, new Binding($"Ports[{i}].Connected")
                { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            Matrix.Columns.Add(new DataGridTemplateColumn { Header = header, MinWidth = 129, Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                CellTemplate = new DataTemplate { VisualTree = toggle } });
        }
        var remove = new FrameworkElementFactory(typeof(Button));
        remove.SetValue(ContentControl.ContentProperty, "移除");
        remove.SetValue(FrameworkElement.StyleProperty, FindResource("QuietButton"));
        remove.SetValue(FrameworkElement.ToolTipProperty, "移除音频文件");
        remove.AddHandler(Button.ClickEvent, new RoutedEventHandler(RemoveFile_Click));
        var style = new Style(typeof(DataGridCell));
        style.Triggers.Add(new DataTrigger { Binding = new Binding("Kind"), Value = SourceKind.Process,
            Setters = { new Setter(VisibilityProperty, Visibility.Hidden) } });
        style.Triggers.Add(new DataTrigger { Binding = new Binding("Kind"), Value = SourceKind.Microphone,
            Setters = { new Setter(VisibilityProperty, Visibility.Hidden) } });
        Matrix.Columns.Add(new DataGridTemplateColumn { Width = 52, CellStyle = style, CellTemplate = new DataTemplate { VisualTree = remove } });
    }

    private async Task UpdateAppsAsync(List<AudioApplicationInfo> apps)
    {
        var now = DateTime.UtcNow;
        var seen = new HashSet<string>();
        foreach (var app in apps)
        {
            var id = $"app:{app.ProcessId}:{app.StartTimeTicks}";
            seen.Add(id);
            var row = _vm.Sources.FirstOrDefault(s => s.Id == id);
            if (row == null)
            {
                if (!app.IsActive) continue;
                row = new SourceVm { Id = id, Name = app.Name, Kind = SourceKind.Process, App = app };
                AddRow(row);
            }
            var previous = row.App;
            row.App = app;
            if (app.IsActive) row.LastActive = now;
            if (app.Peak > 0.00001f) row.LastAudio = now;
            if (!row.Managed && !app.IsActive && now - row.LastActive > TimeSpan.FromSeconds(5))
            {
                _vm.Sources.Remove(row);
                continue;
            }
            if (!row.Managed) SetChecks(row, NativeOutputs(row, app));
            else if (app.IsActive && (previous == null || !previous.IsActive ||
                !previous.ActiveDeviceIds.ToHashSet().SetEquals(app.ActiveDeviceIds) ||
                !previous.PlayingDeviceIds.ToHashSet().SetEquals(app.PlayingDeviceIds) ||
                !previous.RoutingProcessIds.ToHashSet().SetEquals(app.RoutingProcessIds) ||
                (!Selected(row).Any() && app.AudibleDeviceIds.Length > 0)))
            {
                await ApplyRowAsync(row);
            }
            var state = !app.IsActive ? "已暂停" : now - row.LastAudio < TimeSpan.FromSeconds(2) ? "正在播放" : "音频流活动";
            row.Status = row.NativeNotice ?? (row.Managed && !Selected(row).Any() ? "已断开所有输出" : $"{state} · {app.DeviceName}");
        }
        foreach (var row in _vm.Sources.Where(r => r.Kind == SourceKind.Process && !seen.Contains(r.Id)).ToArray())
        {
            if (row.Managed && row.App is { } app && SameProcessStillRunning(app))
            {
                row.App = app with { IsActive = false, Peak = 0, ActiveDeviceIds = [], SignalDeviceIds = [], AudibleDeviceIds = [] };
                row.Status = "等待应用恢复播放 · 已保留连接";
                continue;
            }
            if (row.Managed) await ReleaseRowAsync(row);
            _vm.Sources.Remove(row);
        }
    }

    private static bool SameProcessStillRunning(AudioApplicationInfo app)
    {
        try
        {
            using var process = Process.GetProcessById(app.ProcessId);
            return !process.HasExited && process.ProcessName.Equals(app.ProcessName, StringComparison.OrdinalIgnoreCase) &&
                (app.StartTimeTicks == 0 || process.StartTime.ToUniversalTime().Ticks == app.StartTimeTicks);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    private void AddRow(SourceVm row)
    {
        BindPorts(row, []);
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SourceVm.Gain)) _router.Engine.SetSourceGain(row.Id, (float)(row.Gain / 100));
            if (e.PropertyName == nameof(SourceVm.Muted)) _router.Engine.SetSourceMuted(row.Id, row.Muted);
        };
        if (row.Kind == SourceKind.Process)
            _vm.Sources.Insert(_vm.Sources.TakeWhile(s => s.Kind == SourceKind.Process).Count(), row);
        else _vm.Sources.Add(row);
    }

    private void BindPorts(SourceVm row, HashSet<string> selected)
    {
        row.Ports.Clear();
        foreach (var port in _vm.Outputs)
        {
            var cell = new RouteCell(port) { Connected = port.Available && selected.Contains(port.Id) };
            cell.PropertyChanged += async (_, _) =>
            {
                if (_syncing || _closing) return;
                // Mark ownership immediately so a pending discovery read cannot overwrite the user's click.
                row.Managed = true;
                await RunActionAsync(() => ApplyRowAsync(row));
            };
            row.Ports.Add(cell);
        }
    }

    private static List<OutputPort> Selected(SourceVm row) =>
        row.Ports.Where(c => c.Connected && c.Port.Available).Select(c => c.Port).ToList();

    private static IEnumerable<string> NativeOutputs(SourceVm row, AudioApplicationInfo app)
    {
        if (app.SignalDeviceIds.Length > 0) return app.AudibleDeviceIds.Intersect(app.SignalDeviceIds);
        if (app.AudibleDeviceIds.Length <= 1) return app.AudibleDeviceIds;
        return Selected(row).Select(p => p.Id).Intersect(app.AudibleDeviceIds);
    }

    private void SetChecks(SourceVm row, IEnumerable<string> ids)
    {
        var selected = ids.ToHashSet();
        _syncing = true;
        try { foreach (var cell in row.Ports) cell.Connected = selected.Contains(cell.Port.Id) && cell.Port.Available; }
        finally { _syncing = false; }
    }

    private async Task ApplyRowAsync(SourceVm row)
    {
        if (!_vm.Sources.Contains(row)) return;
        try
        {
            var config = row.Config();
            var selected = Selected(row);
            var outputs = selected.Select(p => p.Config()).ToList();
            var app = row.App;
            if (app != null)
            {
                var current = (await Task.Run(AudioDeviceService.GetApplications)).FirstOrDefault(a =>
                    a.ProcessId == app.ProcessId && a.StartTimeTicks == app.StartTimeTicks);
                if (current == null && SameProcessStillRunning(app))
                {
                    await _router.Engine.DisconnectAsync(row.Id);
                    row.Managed = true;
                    row.Status = "等待应用恢复播放 · 已保留连接";
                    _vm.StatusText = row.Name + " · " + row.Status;
                    return;
                }
                if (current == null) throw new InvalidOperationException("原应用已退出。");
                row.App = app = current;
            }
            row.NativeNotice = await Task.Run(() => _router.ConnectAsync(config, app, outputs));
            ApplyMonitorVolume(row);
            row.Managed = true;
            row.Status = row.NativeNotice ?? (selected.Count == 0 ? "已断开所有输出" : "已连接 · " + string.Join("、", selected.Select(p => p.DeviceName)));
            _vm.StatusText = row.Name + " · " + row.Status;
        }
        catch (Exception ex)
        {
            await ReleaseRowAsync(row);
            _vm.StatusText = $"{row.Name} 连接失败 · {ex.Message}" + (row.Managed ? " · 请点恢复系统播放重试" : "");
            row.Status = ex.Message;
        }
    }

    private async Task ReleaseRowAsync(SourceVm row)
    {
        try
        {
            await Task.Run(() => _router.ReleaseAsync(row.Id));
            row.Managed = false;
            row.NativeNotice = null;
            SetChecks(row, row.App is { } app ? NativeOutputs(row, app) : []);
            row.Status = row.Kind == SourceKind.Process ? "跟随应用播放" : "未连接";
        }
        catch (Exception ex) { _vm.StatusText = row.Name + " 恢复失败 · " + ex.Message; }
    }

    private async Task ReleaseAllAsync()
    {
        if (_inputBound || _quickRoutes.Count > 0 || GameInputBinding.HasRestorePoint) await StopGameMixAsync();
        foreach (var row in _vm.Sources.Where(s => s.Managed).ToArray()) await ReleaseRowAsync(row);
        if (!_vm.Sources.Any(s => s.Managed)) _vm.StatusText = "已停止转发，应用按系统设置播放";
    }

    private void UpdateMeters()
    {
        if (_cableMeter != null && ++_meterTick % 4 == 0)
        {
            var level = _cableMeter.ReadLevel();
            CableSignalText.Text = _otherCableApps.Length == 0 ? level :
                $"{level} · {_otherCableApps} 也在虚拟线上播放，可在该应用中把输出改为耳机";
        }
        var meters = _router.Engine.GetMeters();
        foreach (var r in _vm.Sources)
            r.Meter = 100 * Math.Max(r.App?.Peak ?? 0, meters.SourcePeaks.GetValueOrDefault(r.Id));
    }

    private async Task RunActionAsync(Func<Task> action)
    {
        await _operations.WaitAsync();
        try { IsEnabled = false; await action(); }
        catch (Exception ex) { _vm.StatusText = ex.Message; }
        finally { IsEnabled = true; _operations.Release(); }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ScanAsync(true);
    private async void Stop_Click(object sender, RoutedEventArgs e) => await RunActionAsync(ReleaseAllAsync);

    private void AddFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择要连接的文件", Filter = "音频文件|*.mp3;*.wav;*.flac;*.m4a;*.aac;*.wma;*.aiff;*.aif|所有文件|*.*", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var file in dialog.FileNames) AddRow(new SourceVm { Name = Path.GetFileName(file), Id = Guid.NewGuid().ToString("N"),
            Kind = SourceKind.File, FilePath = file, Status = "音频文件 · 勾选后循环播放" });
    }

    private async void RemoveFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SourceVm { Kind: SourceKind.File } row })
            await RunActionAsync(async () => { await ReleaseRowAsync(row); _vm.Sources.Remove(row); });
    }

    private void SavePreset_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { FileName = "我的音频方案.json", Filter = "路由方案|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var preset = new RoutingPreset { Version = 2,
                Outputs = _vm.Outputs.Where(p => p.Available).Select(p => new PresetOutput(p.Id, p.Id, p.Gain)).ToList(),
                Sources = _vm.Sources.Where(s => s.Managed || s.Kind == SourceKind.File).Select(s => new PresetSource(s.Name, s.Kind,
                    s.App?.ProcessName, s.DeviceId, s.FilePath, s.Gain, s.Muted, false, false, false, Selected(s).Select(p => p.Id).ToList())).ToList() };
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(preset, new JsonSerializerOptions { WriteIndented = true }));
            _vm.StatusText = "方案已保存";
        }
        catch (Exception ex) { _vm.StatusText = "保存失败 · " + ex.Message; }
    }

    private async void LoadPreset_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "载入并连接方案", Filter = "路由方案|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        await RunActionAsync(async () =>
        {
            var preset = JsonSerializer.Deserialize<RoutingPreset>(File.ReadAllText(dialog.FileName));
            if (preset == null || preset.Version is < 1 or > 2 || preset.Outputs == null || preset.Sources == null)
                throw new InvalidDataException("无法识别该方案。");
            await ReleaseAllAsync();
            if (_vm.Sources.Any(r => r.Managed)) return;
            foreach (var r in _vm.Sources.Where(s => s.Kind == SourceKind.File).ToArray()) _vm.Sources.Remove(r);
            foreach (var output in preset.Outputs)
            {
                var port = _vm.Outputs.FirstOrDefault(p => p.Id == output.DeviceId);
                if (port != null) port.Gain = Math.Clamp(output.Gain, 0, 100);
            }
            int skipped = 0;
            var warnings = new List<string>();
            foreach (var s in preset.Sources)
            {
                var row = s.Kind switch {
                    SourceKind.Process => _vm.Sources.FirstOrDefault(r => r.App?.ProcessName == s.ProcessName && r.Kind == SourceKind.Process),
                    SourceKind.Microphone => _vm.Sources.FirstOrDefault(r => r.DeviceId == s.DeviceId && r.Kind == SourceKind.Microphone),
                    _ => null
                };
                if (s.Kind == SourceKind.File && File.Exists(s.FilePath))
                {
                    row = new SourceVm { Id = Guid.NewGuid().ToString("N"), Kind = s.Kind, FilePath = s.FilePath, Name = s.Name };
                    AddRow(row);
                }
                if (row == null) { skipped++; continue; }
                var ids = preset.Version == 2 ? s.Endpoints ?? [] :
                    preset.Outputs.Where(p => (p.Id == "headphones" && s.ToHeadphones) || (p.Id == "speakers" && s.ToSpeakers) || (p.Id == "game" && s.ToGame))
                        .Select(p => p.DeviceId).OfType<string>().ToList();
                if (ids.Any(id => !_vm.Outputs.Any(p => p.Id == id && p.Available))) { skipped++; continue; }
                row.Gain = s.Gain; row.Muted = s.Muted;
                SetChecks(row, ids);
                await ApplyRowAsync(row);
                if (!row.Managed) warnings.Add(_vm.StatusText);
            }
            _vm.StatusText = $"方案已载入 · 跳过 {skipped} 个不可用来源或设备" + (warnings.Count > 0 ? " · " + string.Join(" / ", warnings) : "");
        });
    }

    private static void Open(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    private void OpenSoundSettings_Click(object sender, RoutedEventArgs e) => Open("ms-settings:apps-volume");
    private void OpenRecordingSettings_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("control.exe", "mmsys.cpl,,1") { UseShellExecute = true });
    private void OpenCableSite_Click(object sender, RoutedEventArgs e) => Open("https://vb-audio.com/Cable/");
    private void ShowHelp_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "正在播放的应用会自动出现，初始勾选表示 Windows 当前的播放设备。\n\n同一行勾选多个设备即可同时播放。取消最后一个输出口会使该应用静音，点击「恢复系统播放」可恢复原设置。\n\n要给队友听，把音乐和实体麦克风都勾到同一个游戏麦克风列。使用 VB-CABLE 时，声桥输出到 CABLE Input，游戏麦克风选择 CABLE Output。游戏音效的那一行只选自己的设备。\n\n转发音量控制声桥生成的副本，应用原声音量在原软件中调节。浏览器按照应用显示，多个标签页可能一起被捕获。", "声桥 · 使用指南");

    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (_closed) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        _scan.Stop();
        _meters.Stop();
        // Yield past the initial WPF Closing event before closing a second time.
        await Dispatcher.Yield();
        await RunActionAsync(ReleaseAllAsync);
        if ((_vm.Sources.Any(s => s.Managed) || GameInputBinding.HasRestorePoint) && MessageBox.Show(this, _vm.StatusText + "\n仍要退出吗？", "部分设备未恢复",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        { _closing = false; _scan.Start(); _meters.Start(); return; }
        await _router.Engine.DisposeAsync();
        _closed = true;
        Close();
    }
}
