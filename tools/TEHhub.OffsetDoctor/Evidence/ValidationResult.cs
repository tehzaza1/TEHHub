namespace TEHhub.OffsetDoctor.Evidence;

using TEHhub.OffsetDoctor.Manifest;

public sealed class ValidationResult
{
    public required string NodeId { get; init; }
    public required string NodeDisplayName { get; init; }
    public ValidationStatus Status { get; set; }
    public int ConfiguredOffset { get; init; }
    public IntPtr ResolvedAddress { get; set; }
    public object? ExtractedValue { get; set; }
    public List<CandidateResult> Candidates { get; set; } = [];
    public CandidateResult? BestCandidate { get; set; }
    public string? ErrorMessage { get; set; }
    public List<EvidenceRecord> Evidence { get; set; } = [];
    public bool IsProvisional { get; set; }
}
