namespace TEHhub.OffsetDoctor.RecoveryV1;

using System.Collections.Immutable;

// A hypothesis can express only one of the named structural categories. Numeric
// field or location hints are deliberately absent from the blind contract.
public sealed class BlindDiscoveryRequest
{
    internal BlindDiscoveryRequest(string targetId, RecoveryTargetScope scope, long anchorAddress,
        ImmutableDictionary<string, string> contextFacts,
        ImmutableArray<StructuralHypothesis> approvedHypotheses)
    {
        TargetId = targetId;
        Scope = scope;
        AnchorAddress = anchorAddress;
        ContextFacts = contextFacts;
        ApprovedHypotheses = approvedHypotheses;
    }

    public string TargetId { get; }
    public RecoveryTargetScope Scope { get; }
    public long AnchorAddress { get; }
    public ImmutableDictionary<string, string> ContextFacts { get; }
    public ImmutableArray<StructuralHypothesis> ApprovedHypotheses { get; }
}

public sealed record DiscoveredCandidate(string Id, long Value, string Origin,
    ImmutableArray<RecoveryEvidenceRecord> Evidence);

public sealed record DiscoveryScanEvidence(long BytesScanned, int RawMatchCount,
    ImmutableArray<string> Regions);

public sealed record DiscoveryOutcome(ImmutableArray<DiscoveredCandidate> Candidates,
    bool Complete, string? Error = null, DiscoveryScanEvidence? Scan = null);

public interface IBlindDiscoveryStrategy
{
    string StrategyId { get; }
    DiscoveryOutcome Discover(IRecoveryReadOnlyMemory memory, BlindDiscoveryRequest request);
}

public sealed record IndependentValidationRequest(
    string TargetId, RecoveryTargetScope Scope, long AnchorAddress, long CandidateValue,
    ImmutableDictionary<string, string> ContextFacts);

public sealed record IndependentValidationOutcome(
    ImmutableArray<RecoveryEvidenceRecord> Evidence, bool Complete, string? Error = null);

public interface IIndependentCandidateValidator
{
    IndependentValidationOutcome Validate(IRecoveryReadOnlyMemory memory,
        IndependentValidationRequest request);
}

public sealed class RecoveryTargetRegistry
{
    private readonly Dictionary<string, RecoveryTargetSpec> _targets = new(StringComparer.Ordinal);

    public void Add(RecoveryTargetSpec target)
    {
        if (!_targets.TryAdd(target.Id, target))
            throw new ArgumentException($"Duplicate target ID: {target.Id}", nameof(target));
    }

    public RecoveryTargetSpec Get(string id) => _targets[id];
    public IReadOnlyCollection<RecoveryTargetSpec> Targets => _targets.Values;
}
