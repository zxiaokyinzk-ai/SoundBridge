using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AudioRouter.Audio;
using AudioRouter.Windows;

namespace AudioRouter;

public abstract class Bindable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Notify(name); return true;
    }
    public void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class OutputPort(string id, string label, string deviceName, bool available, bool isGame) : Bindable
{
    public string Id { get; } = id;
    public string Label { get; } = label;
    public string DeviceName { get; } = deviceName;
    public bool Available { get; } = available;
    public bool IsGame { get; } = isGame;
    private double _gain = 100;
    public double Gain { get => _gain; set => Set(ref _gain, Math.Clamp(value, 0, 100)); }
    public OutputConfig Config() => new() { Id = Id, DeviceId = Id, Name = DeviceName, Gain = (float)(Gain / 100) };
}

public sealed class RouteCell(OutputPort port) : Bindable
{
    public OutputPort Port { get; } = port;
    private bool _connected;
    public bool Connected { get => _connected; set => Set(ref _connected, value); }
}

public sealed class SourceVm : Bindable
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public SourceKind Kind { get; init; }
    public string? FilePath { get; init; }
    public string? DeviceId { get; init; }
    public AudioApplicationInfo? App { get; set; }
    public DateTime LastActive { get; set; }
    public DateTime LastAudio { get; set; }
    public bool Managed { get; set; }
    public string? NativeNotice { get; set; }
    public ObservableCollection<RouteCell> Ports { get; } = [];
    private string _status = "";
    public string Status { get => _status; set => Set(ref _status, value); }
    private double _meter;
    public double Meter { get => _meter; set => Set(ref _meter, value); }
    private double _gain = 100;
    public double Gain { get => _gain; set => Set(ref _gain, Math.Clamp(value, 0, 100)); }
    private bool _muted;
    public bool Muted { get => _muted; set => Set(ref _muted, value); }
    public SourceConfig Config() => new() { Id = Id, Name = Name, Kind = Kind, ProcessId = (uint)(App?.ProcessId ?? 0),
        DeviceId = DeviceId, FilePath = FilePath, Gain = (float)(Gain / 100), Muted = Muted, Loop = Kind == SourceKind.File };
}

public sealed class MainViewModel : Bindable
{
    public ObservableCollection<SourceVm> Sources { get; } = [];
    public ObservableCollection<OutputPort> Outputs { get; } = [];
    private string _status = "正在查找播放设备和应用…";
    public string StatusText { get => _status; set => Set(ref _status, value); }
    private string _detection = "";
    public string Detection { get => _detection; set => Set(ref _detection, value); }
    private string _cable = "";
    public string CableStatusText { get => _cable; set => Set(ref _cable, value); }
}

// Versions 1 and 2 share source metadata. Version 2 stores the actual endpoint IDs.
public sealed class RoutingPreset
{
    public int Version { get; set; } = 1;
    public List<PresetOutput> Outputs { get; set; } = [];
    public List<PresetSource> Sources { get; set; } = [];
}
public sealed record PresetOutput(string Id, string? DeviceId, double Gain);
public sealed record PresetSource(string Name, SourceKind Kind, string? ProcessName, string? DeviceId, string? FilePath,
    double Gain, bool Muted, bool ToHeadphones, bool ToSpeakers, bool ToGame, List<string>? Endpoints = null);
