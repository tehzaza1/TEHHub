namespace TEHhub.OffsetDoctor.RecoveryV1;

using System.Collections.Immutable;

public enum RecoveryTerminalResult
{
    PASS_CURRENT, NOT_FOUND, AMBIGUOUS, PROPOSED, BLOCKED_CONTEXT,
    BLOCKED_DEPENDENCY, NOT_APPLICABLE, ERROR
}

public enum RecoveryEvidenceResult { PASS, FAIL, UNKNOWN }
public enum CandidateDisposition { Rejected, Equivalent, Survivor, Unresolved }
public enum EliminationStage
{
    Discovery, RequiredPredicates, Equivalence, IndependentValidation,
    Od114AlignedBases, Od114ReadableShape, Od114OwnerBackPointer, Od114AnchorStructure,
    Od114SocketCount, Od114GoldenSlots, Od114AnchorPosition,
    Od145GridDiscovery, Od145GridValidation, Od145ConnectionsDiscovery,
    Od145ConnectionsValidation, Od145CrossFieldValidation, Od145FieldPairEquivalence
}
public enum StructuralHypothesis { PointerField, ScalarField, StaticPattern }

public sealed record RecoveryIdentity(
    int ProcessId, string ProcessName, string? ProcessPath, string FileVersion, long ModuleBase,
    long ModuleSize, DateTime AttachedTimeUtc);

public sealed record RecoveryTargetScope(string Name, string InstanceId);

// No offset, old value, or numeric candidate hint is part of this specification.
public sealed record RecoveryTargetSpec(
    string Id, RecoveryTargetScope Scope, string StrategyId,
    ImmutableArray<string> RequiredContextKeys,
    string? ParentTargetId,
    long? RootAddress,
    ImmutableArray<StructuralHypothesis> ApprovedHypotheses,
    bool Applicable = true)
{
    public ImmutableArray<string> AdditionalDependencyIds { get; init; } = [];

    public static RecoveryTargetSpec Create(string id, RecoveryTargetScope scope, string strategyId,
        IEnumerable<string>? requiredContextKeys = null, string? parentTargetId = null,
        long? rootAddress = null, IEnumerable<StructuralHypothesis>? approvedHypotheses = null,
        bool applicable = true, IEnumerable<string>? additionalDependencyIds = null) =>
        new(id, scope, strategyId,
            (requiredContextKeys ?? []).ToImmutableArray(), parentTargetId, rootAddress,
            (approvedHypotheses ?? []).ToImmutableArray(), applicable)
        { AdditionalDependencyIds = (additionalDependencyIds ?? []).ToImmutableArray() };
}

public sealed record RecoveryContextSnapshot(ImmutableDictionary<string, string> Facts)
{
    public static RecoveryContextSnapshot Create(IEnumerable<KeyValuePair<string, string>> facts) =>
        new(facts.ToImmutableDictionary(StringComparer.Ordinal));
}

public sealed record RecoveryEvidenceRecord(
    string Predicate, RecoveryEvidenceResult Result, string Detail,
    bool Required, bool Independent);

public sealed record CandidateRejection(EliminationStage Stage, string Predicate, string Reason);

public sealed record RecoveryCandidate(
    string Id, long Value, string DiscoveryOrigin,
    ImmutableArray<RecoveryEvidenceRecord> Evidence,
    ImmutableArray<RecoveryEvidenceRecord> ValidationEvidence,
    ImmutableArray<CandidateRejection> RejectionReasons,
    CandidateDisposition Disposition)
{
    public ImmutableArray<int> ChildPath { get; init; } = [];
}

public sealed record CandidateEliminationStage(
    EliminationStage Stage,
    ImmutableArray<string> InputCandidateIds,
    ImmutableArray<string> SurvivingCandidateIds,
    ImmutableArray<string> RejectedCandidateIds,
    ImmutableDictionary<string, ImmutableArray<CandidateRejection>> RejectionReasons,
    string? EquivalenceRule)
{
    public int InputCount => InputCandidateIds.Length;
    public int SurvivingCount => SurvivingCandidateIds.Length;
    public int RejectedCount => RejectedCandidateIds.Length;
}

public sealed record FrozenDiscoveryResult(
    ImmutableArray<RecoveryCandidate> CandidateLedger,
    ImmutableArray<CandidateEliminationStage> EliminationStages,
    ImmutableArray<string> SurvivorIds,
    DiscoveryScanEvidence? Scan = null);

public sealed record AtlasFieldPair(long GridPositionOffset, long ConnectionsOffset)
{
    public override string ToString() => $"(Grid=+0x{GridPositionOffset:X}, Conn=+0x{ConnectionsOffset:X})";
}

public sealed record AtlasFieldPairComparison(
    AtlasFieldPair? CurrentPair,
    ImmutableArray<AtlasFieldPair> HistoricalPairs,
    bool? ProposalMatchesCurrent,
    bool? ProposalMatchesHistory);

public sealed record FrozenDecision(
    RecoveryTerminalResult TerminalResult,
    ImmutableArray<string> SurvivorIds,
    long? Proposal,
    string EvidenceDigest)
{
    public ImmutableArray<int> ChildPath { get; init; } = [];
    public ImmutableArray<int> ProposedChildPath { get; init; } = [];
    public AtlasFieldPair? ProposedFieldPair { get; init; }
    public ImmutableArray<AtlasFieldPair> SurvivingFieldPairs { get; init; } = [];
}

public sealed record ChildPathComparison(
    ImmutableArray<int> CurrentPath, ImmutableArray<ImmutableArray<int>> HistoricalPaths,
    bool? ProposalMatchesCurrent, bool? ProposalMatchesHistory);

public sealed record RecoveryCurrentValidation(long Value, ImmutableArray<RecoveryEvidenceRecord> Evidence, bool Complete)
{
    public ImmutableArray<int> ChildPath { get; init; } = [];
    public AtlasFieldPair? FieldPair { get; init; }
}

public sealed record HistoricalComparison(
    long? CurrentValue, ImmutableArray<long> HistoricalValues,
    bool? ProposalMatchesCurrent, bool? ProposalMatchesHistory);

public sealed record RecoveryDependencyState(string TargetId, RecoveryTerminalResult Result,
    bool IndependentlyValidated, string EvidenceDigest, long? Anchor = null,
    string? CandidateId = null);

public sealed record ProvisionalValue(long Value, string ParentTargetId, string CandidateId,
    string EvidenceDigest, RecoveryIdentity Identity);

public sealed record RecoveryResult(
    RecoveryTargetSpec Target, FrozenDecision Decision, FrozenDiscoveryResult Discovery,
    RecoveryCurrentValidation? CurrentValidation,
    ImmutableArray<RecoveryDependencyState> Dependencies,
    ImmutableDictionary<string, string> Context,
    HistoricalComparison? PostResultComparison,
    string? Detail)
{
    public bool Applied => false;
    public ChildPathComparison? PathComparison { get; init; }
    public AtlasFieldPairComparison? FieldPairComparison { get; init; }
}

public sealed record RecoveryReport(
    string SchemaVersion, RecoveryIdentity Identity, DateTime TimestampUtc,
    ImmutableArray<RecoveryResult> Results)
{
    public static RecoveryReport Create(RecoverySession session) =>
        new("recovery-v1.phase1", session.Identity, DateTime.UtcNow,
            session.Results.OrderBy(r => r.Target.Id, StringComparer.Ordinal).ToImmutableArray());
}

public enum LiveEvidenceStatus
{
    LIVE_CURRENT_VALIDATION_PROVEN,
    LIVE_RECOVERY_PROVEN,
    LIVE_CONTEXT_PENDING,
    SYNTHETIC_ONLY
}

public enum ThresholdClassification
{
    STRUCTURAL_ABI_INVARIANT,
    VERSIONED_SEMANTIC_INVARIANT,
    SAFETY_BUDGET,
    DISCOVERY_BOUND,
    VALIDATION_THRESHOLD
}

public sealed record VersionedThreshold(
    string Name,
    object Value,
    string DefiningMember,
    ThresholdClassification Classification,
    string Purpose);

public sealed record PriorEvidenceRecord(
    string EvidenceId,
    string TargetId,
    LiveEvidenceStatus Status,
    string BranchExercised,
    string? BuildIdentity,
    string? TimestampOrCommit,
    string Description);

public sealed record CurrentRunLiveEvidence(
    string TargetId,
    LiveEvidenceStatus Status,
    string Details,
    DateTime TimestampUtc);

public sealed record CurrentValidationInput(
    long? CurrentNumericValue = null,
    IReadOnlyList<int>? CurrentPath = null,
    AtlasFieldPair? CurrentFieldPair = null);

public sealed record PostDecisionComparisonInput(
    long? ConfiguredNumericValue = null,
    ImmutableArray<long> HistoricalNumericValues = default,
    IReadOnlyList<int>? ConfiguredPath = null,
    ImmutableArray<IReadOnlyList<int>> HistoricalPaths = default,
    AtlasFieldPair? ConfiguredFieldPair = null,
    ImmutableArray<AtlasFieldPair> HistoricalFieldPairs = default);

public sealed record TargetExecutionInput(
    CurrentValidationInput? CurrentValidation,
    PostDecisionComparisonInput? PostComparison);

public enum RecoveryAggregateStatus
{
    COMPLETE,
    PARTIAL_CONTEXT,
    ATTENTION_REQUIRED,
    ERROR
}

public sealed record TargetBudgetMetric(
    string TargetId,
    long ElapsedMilliseconds,
    long? ReadCount,
    long? BytesScanned,
    int? CandidateCount);

public sealed record RecoveryAggregateReport(
    string SchemaVersion,
    string RunId,
    RecoveryIdentity Identity,
    DateTime TimestampUtc,
    RecoveryAggregateStatus AggregateStatus,
    ImmutableArray<string> RequestedTargets,
    ImmutableArray<string> EffectiveTargets,
    ImmutableArray<string> AutoAddedRunnableDependencies,
    ImmutableArray<string> ExecutionOrder,
    ImmutableArray<RecoveryResult> Results,
    ImmutableArray<string> BlockedContextTargets,
    ImmutableArray<string> BlockedDependencyTargets,
    ImmutableArray<string> NotFoundTargets,
    ImmutableArray<string> AmbiguousTargets,
    ImmutableArray<string> ProposedTargets,
    ImmutableArray<string> PassedCurrentTargets,
    ImmutableArray<string> ErrorTargets,
    ImmutableDictionary<string, string> Proposals,
    ImmutableArray<string> Errors,
    ImmutableArray<TargetBudgetMetric> TargetMetrics,
    long TotalElapsedMilliseconds,
    long? TotalBytesScanned,
    ImmutableArray<CurrentRunLiveEvidence> CurrentRunLiveEvidence)
{
    public bool Applied => false;
}
