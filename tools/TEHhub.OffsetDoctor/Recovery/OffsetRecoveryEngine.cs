namespace TEHhub.OffsetDoctor.Recovery;

using TEHhub.OffsetDoctor.Evidence;
using TEHhub.OffsetDoctor.Manifest;
using TEHhub.OffsetDoctor.Process;
using TEHhub.OffsetDoctor.Strategies;
using TEHhub.OffsetDoctor.Validation;

public sealed class OffsetRecoveryEngine
{
    private readonly OffsetValidatorEngine _validator = new();

    public OffsetDoctorReport RunValidation(IProcessMemoryReader reader, int? expectedGold = null)
    {
        var manifestNodes = OffsetManifest.CreateFullRepositoryManifest();
        var context = new RecoveryContext
        {
            ExpectedGoldAmount = expectedGold,
            AllNodes = manifestNodes
        };

        // Strict validate mode: allowRecovery = false
        var results = _validator.ValidateChain(reader, manifestNodes, context, allowRecovery: false);

        return new OffsetDoctorReport
        {
            ProcessMetadata = reader.Metadata,
            TimestampUtc = DateTime.UtcNow,
            Results = results,
            IsChainHealthy = results.All(r => r.Status == ValidationStatus.VALID)
        };
    }
}

public sealed class OffsetDoctorReport
{
    public required ProcessMetadata ProcessMetadata { get; init; }
    public DateTime TimestampUtc { get; init; }
    public List<ValidationResult> Results { get; init; } = [];
    public List<OffsetRecommendation> Recommendations { get; init; } = [];
    public bool IsChainHealthy { get; init; }

    public int ValidCount => Results.Count(r => r.Status == ValidationStatus.VALID);
    public int BrokenCount => Results.Count(r => r.Status == ValidationStatus.BROKEN);
    public int BlockedCount => Results.Count(r => r.Status == ValidationStatus.BLOCKED);
    public int UnverifiedCount => Results.Count(r => r.Status == ValidationStatus.UNVERIFIED);
    public int TotalNodesCount => Results.Count;
}

public sealed class OffsetRecommendation
{
    public required string NodeId { get; init; }
    public required string DisplayName { get; init; }
    public int CurrentOffset { get; init; }
    public int SuggestedOffset { get; init; }
    public int Delta { get; init; }
    public Confidence Confidence { get; init; }
    public int Score { get; init; }
    public required string SourceLocation { get; init; }
    public required string Rationale { get; init; }
}
