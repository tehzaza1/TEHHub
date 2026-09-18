namespace TEHhub.OffsetDoctor.Evidence;

public sealed class CandidateResult
{
    public int Offset { get; init; }
    public IntPtr TargetAddress { get; init; }
    public int Score { get; set; }
    public Confidence Confidence { get; set; }
    public List<EvidenceRecord> Evidence { get; init; } = [];
    public string? DownstreamEvidenceSummary { get; set; }
    public object? ExtractedValue { get; set; }

    public int IndependentValidatorsCount =>
        Evidence.Count(e => e.Passed && e.IsIndependentValidator);
}
