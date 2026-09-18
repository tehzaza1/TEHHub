namespace TEHhub.OffsetDoctor.Evidence;

public sealed class EvidenceRecord
{
    public required string RuleName { get; init; }
    public required string Description { get; init; }
    public int ScoreDelta { get; init; }
    public bool Passed { get; init; }
    public bool IsIndependentValidator { get; init; }
}
