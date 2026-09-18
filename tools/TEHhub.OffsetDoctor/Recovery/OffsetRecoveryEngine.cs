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
        var manifestNodes = OffsetManifest.CreateGoldChainManifest();
        var context = new RecoveryContext
        {
            ExpectedGoldAmount = expectedGold,
            AllNodes = manifestNodes
        };

        var results = _validator.ValidateChain(reader, manifestNodes, context);

        return new OffsetDoctorReport
        {
            ProcessMetadata = reader.Metadata,
            TimestampUtc = DateTime.UtcNow,
            Results = results,
            IsChainHealthy = results.All(r => r.Status == ValidationStatus.VALID)
        };
    }

    public OffsetDoctorReport RunRecovery(IProcessMemoryReader reader, int? expectedGold = null)
    {
        var manifestNodes = OffsetManifest.CreateGoldChainManifest();
        var context = new RecoveryContext
        {
            ExpectedGoldAmount = expectedGold,
            AllNodes = manifestNodes
        };

        var bestCandidates = new Dictionary<string, CandidateResult>();

        // Iterative refinement: Search for candidates and apply them provisionally to test downstream nodes
        bool changed;
        int maxPasses = 5;
        int currentPass = 0;
        List<ValidationResult> lastResults = [];

        do
        {
            changed = false;
            currentPass++;

            lastResults = _validator.ValidateChain(reader, manifestNodes, context);

            foreach (var r in lastResults)
            {
                if (r.BestCandidate != null && (r.Status == ValidationStatus.CANDIDATE_FOUND || r.Status == ValidationStatus.NEEDS_MANUAL_PROOF || r.Status == ValidationStatus.AMBIGUOUS))
                {
                    bestCandidates[r.NodeId] = r.BestCandidate;

                    if (!context.ProvisionalOffsets.TryGetValue(r.NodeId, out var existingOff) || existingOff != r.BestCandidate.Offset)
                    {
                        context.ProvisionalOffsets[r.NodeId] = r.BestCandidate.Offset;
                        changed = true;
                    }
                }
            }
        } while (changed && currentPass < maxPasses);

        // Build the final comprehensive results:
        // Keep original configured default offset on broken nodes and attach the best candidate + candidate status
        var finalResults = new List<ValidationResult>();
        foreach (var node in manifestNodes)
        {
            var res = lastResults.FirstOrDefault(r => r.NodeId == node.Id);
            if (res == null) continue;

            if (bestCandidates.TryGetValue(node.Id, out var candidate))
            {
                // If a candidate was recovered for this node
                var recoveredRes = new ValidationResult
                {
                    NodeId = node.Id,
                    NodeDisplayName = node.DisplayName,
                    ConfiguredOffset = node.DefaultOffset,
                    ResolvedAddress = candidate.TargetAddress,
                    ExtractedValue = candidate.ExtractedValue ?? $"0x{candidate.TargetAddress.ToInt64():X}",
                    BestCandidate = candidate,
                    Candidates = res.Candidates.Count > 0 ? res.Candidates : [candidate],
                    Status = candidate.Confidence switch
                    {
                        Confidence.HIGH => ValidationStatus.CANDIDATE_FOUND,
                        Confidence.MEDIUM => ValidationStatus.NEEDS_MANUAL_PROOF,
                        _ => ValidationStatus.NEEDS_MANUAL_PROOF
                    },
                    IsProvisional = res.IsProvisional
                };
                finalResults.Add(recoveredRes);
            }
            else
            {
                finalResults.Add(res);
            }
        }

        var recommendations = new List<OffsetRecommendation>();
        foreach (var (nodeId, candidate) in bestCandidates)
        {
            var node = manifestNodes.First(n => n.Id == nodeId);
            if (candidate.Offset != node.DefaultOffset)
            {
                recommendations.Add(new OffsetRecommendation
                {
                    NodeId = node.Id,
                    DisplayName = node.DisplayName,
                    CurrentOffset = node.DefaultOffset,
                    SuggestedOffset = candidate.Offset,
                    Delta = candidate.Offset - node.DefaultOffset,
                    Confidence = candidate.Confidence,
                    Score = candidate.Score,
                    SourceLocation = node.ProductionSourceLocation ?? string.Empty,
                    Rationale = string.Join("; ", candidate.Evidence.Where(e => e.Passed).Select(e => e.Description))
                });
            }
        }

        return new OffsetDoctorReport
        {
            ProcessMetadata = reader.Metadata,
            TimestampUtc = DateTime.UtcNow,
            Results = finalResults,
            Recommendations = recommendations,
            IsChainHealthy = finalResults.All(r => r.Status == ValidationStatus.VALID)
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
