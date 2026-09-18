namespace TEHhub.OffsetDoctor.Process;

public sealed class ProcessMetadata
{
    public int ProcessId { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public string? ProcessPath { get; init; }
    public string FileVersion { get; init; } = string.Empty;
    public IntPtr ModuleBase { get; init; }
    public long ModuleMemorySize { get; init; }
    public bool IsElevated { get; init; }
    public DateTime AttachedTimeUtc { get; init; } = DateTime.UtcNow;
}
