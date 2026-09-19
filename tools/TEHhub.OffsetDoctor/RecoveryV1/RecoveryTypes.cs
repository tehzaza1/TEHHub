namespace TEHhub.OffsetDoctor.RecoveryV1;

using System.Collections.Immutable;

public enum RecoveryTerminalResult
{
    PASS_CURRENT, NOT_FOUND, AMBIGUOUS, PROPOSED, BLOCKED_CONTEXT,
    BLOCKED_DEPENDENCY, NOT_APPLICABLE, ERROR
}

public enum RecoveryEvidenceResult { PASS, FAIL, UNKNOWN }
public enum CandidateDisposition { Rejected, Equivalent, Survivor, Unresolved }
public enum EliminationStage { Discovery, RequiredPredicates, Equivalence, IndependentValidation }
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
    public static RecoveryTargetSpec Create(string id, RecoveryTargetScope scope, string strategyId,
        IEnumerable<string>? requiredContextKeys = null, string? parentTargetId = null,
        long? rootAddress = null, IEnumerable<StructuralHypothesis>? approvedHypotheses = null,
        bool applicable = true) =>
        new(id, scope, strategyId,
            (requiredContextKeys ?? []).ToImmutableArray(), parentTargetId, rootAddress,
            (approvedHypotheses ?? []).ToImmutableArray(), applicable);
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
    CandidateDisposition Disposition);

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

public sealed record FrozenDecision(
    RecoveryTerminalResult TerminalResult,
    ImmutableArray<string> SurvivorIds,
    long? Proposal,
    string EvidenceDigest);

public sealed record RecoveryCurrentValidation(long Value, ImmutableArray<RecoveryEvidenceRecord> Evidence, bool Complete);

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
}

public sealed record RecoveryReport(
    string SchemaVersion, RecoveryIdentity Identity, DateTime TimestampUtc,
    ImmutableArray<RecoveryResult> Results)
{
    public static RecoveryReport Create(RecoverySession session) =>
        new("recovery-v1.phase1", session.Identity, DateTime.UtcNow,
            session.Results.OrderBy(r => r.Target.Id, StringComparer.Ordinal).ToImmutableArray());
}
