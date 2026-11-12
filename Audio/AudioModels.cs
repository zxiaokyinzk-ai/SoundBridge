namespace AudioRouter.Audio;

public enum SourceKind { Process, Microphone, File }

public sealed class SourceConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "音频来源";
    public SourceKind Kind { get; set; }
    public uint ProcessId { get; set; }
    public string? DeviceId { get; set; }
    public string? FilePath { get; set; }
    public float Gain { get; set; } = 1f;
    public bool Muted { get; set; }
    public bool Loop { get; set; }
    public List<string> DestinationIds { get; set; } = [];
}

public sealed class OutputConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "音频输出";
    public string DeviceId { get; set; } = "";
    public float Gain { get; set; } = 1f;
    public bool Muted { get; set; }
}

public sealed record AudioMeters(
    IReadOnlyDictionary<string, float> SourcePeaks,
    IReadOnlyDictionary<string, float> OutputPeaks);
