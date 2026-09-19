namespace TEHhub.OffsetDoctor.Baseline;

using TEHhub.OffsetDoctor.Manifest;
using TEHhub.OffsetDoctor.Validation;

public enum DeltaSeverity
{
    Informational,
    Warning,
    Critical
}

public sealed class BaselineDelta
{
    public required DeltaSeverity Severity { get; init; }
    public required string NodeId { get; init; }
    public required string DisplayName { get; init; }
    public required string Category { get; init; }
    public ValidationStatus? BaselineStatus { get; init; }
    public ValidationStatus? CurrentStatus { get; init; }
    public string? BaselineValue { get; init; }
    public string? CurrentValue { get; init; }
    public required string Description { get; init; }
}

public sealed class BaselineComparisonResult
{
    public required BaselineSnapshot Baseline { get; init; }
    public required OffsetDoctorValidationReport CurrentReport { get; init; }
    public List<BaselineDelta> Deltas { get; init; } = [];

    public int CriticalCount => Deltas.Count(d => d.Severity == DeltaSeverity.Critical);
    public int WarningCount => Deltas.Count(d => d.Severity == DeltaSeverity.Warning);
    public int InformationalCount => Deltas.Count(d => d.Severity == DeltaSeverity.Informational);
    public bool HasCriticalRegressions => CriticalCount > 0;
}
