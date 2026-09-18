namespace TEHhub.OffsetDoctor.Watch;

using TEHhub.OffsetDoctor.Evidence;
using TEHhub.OffsetDoctor.Manifest;
using TEHhub.OffsetDoctor.Process;
using TEHhub.OffsetDoctor.Validation;

public sealed class WatchReport
{
    public required ProcessMetadata ProcessMetadata { get; init; }
    public DateTime StartTimeUtc { get; init; }
    public DateTime EndTimeUtc { get; init; }
    public TimeSpan Duration => EndTimeUtc - StartTimeUtc;
    public int IntervalMs { get; init; }
    public required string TargetFilter { get; init; }
    public ValidationGroundTruth? GroundTruth { get; init; }
    public List<WatchTargetSummary> TargetSummaries { get; init; } = [];
    public List<string> TransitionLog { get; init; } = [];
    public int TotalTransitions => TransitionLog.Count;
}

public sealed class WatchTargetSummary
{
    public required string NodeId { get; init; }
    public required string DisplayName { get; init; }
    public required string Category { get; init; }
    public ValidationStatus? FirstStatus { get; init; }
    public ValidationStatus? BestStatus { get; init; }
    public ValidationStatus? LatestStatus { get; init; }
    public string? ObservedPointerOrValue { get; init; }
    public string? Reason { get; init; }
    public bool PlayerActionObserved { get; init; }
    public required string Recommendation { get; init; }
    public EvidenceRecord? StrongestEvidence { get; init; }
}
