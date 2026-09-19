namespace TEHhub.OffsetDoctor.Baseline;

using System.Text.Json;
using System.Text.Json.Serialization;
using TEHhub.OffsetDoctor.Manifest;
using TEHhub.OffsetDoctor.Process;
using TEHhub.OffsetDoctor.Validation;

public sealed class BaselineSnapshot
{
    public string ToolVersion { get; init; } = "1.0.0";
    public string? CommitHash { get; init; }
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    public required string ProcessName { get; init; }
    public string? ProcessPath { get; init; }
    public int ProcessId { get; init; }
    public string ModuleBase { get; init; } = "0x0";
    public long ModuleSize { get; init; }
    public string? ModuleChecksum { get; init; }
    public ValidationGroundTruth? GroundTruth { get; init; }
    public required BaselineSummary Summary { get; init; }
    public List<BaselineNodeSnapshot> Nodes { get; init; } = [];

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string ToJson(BaselineSnapshot snapshot) => JsonSerializer.Serialize(snapshot, JsonOpts);

    public static BaselineSnapshot FromJson(string json) =>
        JsonSerializer.Deserialize<BaselineSnapshot>(json, JsonOpts) ??
        throw new InvalidOperationException("Failed to deserialize baseline JSON.");

    public static void SaveToFile(BaselineSnapshot snapshot, string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(filePath, ToJson(snapshot));
    }

    public static string GetDefaultBaselinePath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(localAppData, "TEHhub", "OffsetDoctor");
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        return Path.Combine(dir, "offsetdoctor-baseline.json");
    }

    public static BaselineSnapshot LoadFromFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Baseline file not found: {filePath}", filePath);
        }
        var json = File.ReadAllText(filePath);
        return FromJson(json);
    }
}

public sealed class BaselineSummary
{
    public int ValidCount { get; init; }
    public int BrokenCount { get; init; }
    public int BlockedCount { get; init; }
    public int UnverifiedCount { get; init; }
    public int TotalNodesCount { get; init; }
}

public sealed class BaselineNodeSnapshot
{
    public required string NodeId { get; init; }
    public required string DisplayName { get; init; }
    public required string Category { get; init; }
    public string? ParentId { get; init; }
    public int ConfiguredOffset { get; init; }
    public string? PatternName { get; init; }
    public ValueKind Kind { get; init; }
    public ValidationStatus Status { get; init; }
    public string? ObservedAddress { get; init; }
    public string? ResolvedAddress { get; init; }
    public string? TraversalAddress { get; init; }
    public string? ExtractedValue { get; init; }
    public string? ErrorMessage { get; init; }
    public List<string> EvidenceSummary { get; init; } = [];
    public bool IsOptionalStateDependent { get; init; }
    public bool IsStaticRoot { get; init; }
    public StaticPatternResolutionKind? StaticResolutionKind { get; init; }
}
