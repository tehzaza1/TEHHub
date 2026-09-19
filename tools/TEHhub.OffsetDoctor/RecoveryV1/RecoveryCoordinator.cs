namespace TEHhub.OffsetDoctor.RecoveryV1;

using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public sealed class RecoveryCoordinator
{
    public RecoveryResult Run(RecoverySession session, RecoveryTargetSpec target,
        IBlindDiscoveryStrategy discovery, IIndependentCandidateValidator validator,
        long? currentConfiguredValue = null, IEnumerable<long>? historicalValues = null)
    {
        var initialContext = session.Context;
        var context = initialContext.Facts;
        var ledger = new List<MutableCandidate>();
        var stages = new List<CandidateEliminationStage>();
        RecoveryCurrentValidation? currentValidation = null;
        ImmutableArray<RecoveryDependencyState> dependencies = [];
        long anchor = 0;
        string? detail = null;
        DiscoveryScanEvidence? scan = null;
        RecoveryTerminalResult? forced = null;

        string? Guard()
        {
            if (!session.IdentityIsCurrent) return "Process/build identity became stale.";
            return ContextRequirementEvaluator.CheckRequired(target, initialContext, session.Context);
        }

        RecoveryTerminalResult GuardResult(string reason) =>
            reason.StartsWith("Required context", StringComparison.Ordinal)
                ? RecoveryTerminalResult.BLOCKED_CONTEXT : RecoveryTerminalResult.ERROR;

        try
        {
            if (discovery.StrategyId != target.StrategyId)
                throw new InvalidOperationException("Strategy ID does not match target.");
            if (target.ApprovedHypotheses.Any(h => !Enum.IsDefined(h)))
                throw new InvalidOperationException("Unrecognized structural hypothesis is NON_BLIND.");
            if (target.RequiredContextKeys.Any(IsForbiddenBlindContextKey))
                throw new InvalidOperationException("Context key carries an offset or ranking hint and is NON_BLIND.");

            detail = Guard();
            if (detail is not null) forced = GuardResult(detail);

            if (forced is null && !target.Applicable)
                forced = RecoveryTerminalResult.NOT_APPLICABLE;

            if (forced is null && !DependencyEvaluator.TryResolve(session, target,
                    out anchor, out dependencies, out detail))
                forced = RecoveryTerminalResult.BLOCKED_DEPENDENCY;

            if (forced is null && currentConfiguredValue is long current)
            {
                var validation = validator.Validate(session.Memory,
                    new IndependentValidationRequest(target.Id, target.Scope, anchor, current, context));
                currentValidation = new RecoveryCurrentValidation(current, validation.Evidence, validation.Complete);
                detail = Guard();
                if (detail is not null) forced = GuardResult(detail);
                else if (!validation.Complete || validation.Error is not null)
                {
                    forced = RecoveryTerminalResult.ERROR;
                    detail = validation.Error ?? "Current-value validation read was incomplete.";
                }
                else if (HasUnknownRequired(validation.Evidence))
                {
                    forced = RecoveryTerminalResult.ERROR;
                    detail = "Current-value required predicate returned UNKNOWN.";
                }
                else if (IsIndependentlyValid(validation.Evidence))
                    forced = RecoveryTerminalResult.PASS_CURRENT;
            }

            if (forced is null)
            {
                var blindContext = target.RequiredContextKeys.ToImmutableDictionary(
                    key => key, key => context[key], StringComparer.Ordinal);
                var found = discovery.Discover(session.Memory,
                    new BlindDiscoveryRequest(target.Id, target.Scope, anchor, blindContext,
                        target.ApprovedHypotheses));
                scan = found.Scan;
                foreach (var candidate in found.Candidates)
                {
                    if (string.IsNullOrWhiteSpace(candidate.Id) ||
                        ledger.Any(c => c.Id == candidate.Id))
                        throw new InvalidOperationException("Discovery emitted a missing or duplicate candidate ID.");
                    ledger.Add(new MutableCandidate(candidate));
                }
                AddStage(stages, EliminationStage.Discovery, ledger, ledger, [], null);

                detail = Guard();
                if (detail is not null) forced = GuardResult(detail);
                else if (!found.Complete || found.Error is not null)
                {
                    forced = RecoveryTerminalResult.ERROR;
                    detail = found.Error ?? "Discovery scan was incomplete.";
                }

                if (forced is null)
                {
                    var input = ledger.ToArray();
                    foreach (var candidate in input)
                    {
                        foreach (var evidence in candidate.Evidence.Where(e => e.Required && e.Result == RecoveryEvidenceResult.FAIL))
                            candidate.Reject(EliminationStage.RequiredPredicates, evidence.Predicate, evidence.Detail);
                        if (HasUnknownRequired(candidate.Evidence))
                        {
                            forced = RecoveryTerminalResult.ERROR;
                            detail = "Discovery required predicate returned UNKNOWN.";
                        }
                    }
                    AddStage(stages, EliminationStage.RequiredPredicates, input,
                        input.Where(c => !c.IsRejected), input.Where(c => c.IsRejected), null);
                }

                if (forced is null)
                {
                    var input = ledger.Where(c => !c.IsRejected).ToArray();
                    var firstByValue = new Dictionary<long, MutableCandidate>();
                    foreach (var candidate in input)
                    {
                        if (firstByValue.TryGetValue(candidate.Value, out var first))
                            candidate.MarkEquivalent(first.Id);
                        else
                            firstByValue.Add(candidate.Value, candidate);
                    }
                    AddStage(stages, EliminationStage.Equivalence, input,
                        input.Where(c => !c.IsEquivalent), input.Where(c => c.IsEquivalent),
                        "Exact candidate value; first enumerated ID represents equivalent values.");
                }

                if (forced is null)
                {
                    var input = ledger.Where(c => !c.IsRejected && !c.IsEquivalent).ToArray();
                    foreach (var candidate in input)
                    {
                        var validation = validator.Validate(session.Memory,
                            new IndependentValidationRequest(target.Id, target.Scope, anchor,
                                candidate.Value, context));
                        candidate.ValidationEvidence.AddRange(validation.Evidence);

                        detail = Guard();
                        if (detail is not null) { forced = GuardResult(detail); break; }
                        if (!validation.Complete || validation.Error is not null)
                        {
                            forced = RecoveryTerminalResult.ERROR;
                            detail = validation.Error ?? "Independent validation read was incomplete.";
                            break;
                        }
                        if (HasUnknownRequired(validation.Evidence))
                        {
                            forced = RecoveryTerminalResult.ERROR;
                            detail = "Independent required predicate returned UNKNOWN.";
                            break;
                        }
                        foreach (var evidence in validation.Evidence.Where(e => e.Required && e.Result == RecoveryEvidenceResult.FAIL))
                            candidate.Reject(EliminationStage.IndependentValidation, evidence.Predicate, evidence.Detail);
                        if (!candidate.IsRejected && !IsIndependentlyValid(validation.Evidence))
                            candidate.Reject(EliminationStage.IndependentValidation,
                                "independent-proof", "No independent PASS evidence established the candidate.");
                    }
                    AddStage(stages, EliminationStage.IndependentValidation, input,
                        input.Where(c => !c.IsRejected), input.Where(c => c.IsRejected), null);
                }
            }
        }
        catch (Exception ex)
        {
            forced = RecoveryTerminalResult.ERROR;
            detail = ex.Message;
        }

        var validated = forced is null
            ? ledger.Where(c => !c.IsRejected && !c.IsEquivalent &&
                IsIndependentlyValid(c.ValidationEvidence)).ToArray()
            : [];
        RecoveryTerminalResult terminal = forced ?? (validated.Length switch
        {
            0 => RecoveryTerminalResult.NOT_FOUND,
            1 => RecoveryTerminalResult.PROPOSED,
            _ => RecoveryTerminalResult.AMBIGUOUS
        });
        foreach (var candidate in validated) candidate.Disposition = CandidateDisposition.Survivor;

        var frozenLedger = ledger.Select(c => c.Freeze()).ToImmutableArray();
        var survivorIds = validated.Select(c => c.Id).ToImmutableArray();
        long? proposal = terminal == RecoveryTerminalResult.PROPOSED ? validated[0].Value : null;
        var frozenDiscovery = new FrozenDiscoveryResult(frozenLedger, stages.ToImmutableArray(), survivorIds, scan);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { target.Id, terminal, survivorIds, proposal, frozenLedger, stages }))));
        var decision = new FrozenDecision(terminal, survivorIds, proposal, digest);

        // Comparison is constructed only after the terminal result, survivors,
        // proposal, and evidence digest have been frozen.
        var history = historicalValues?.ToImmutableArray() ?? [];
        HistoricalComparison? comparison = currentConfiguredValue is null && history.IsEmpty
            ? null
            : new HistoricalComparison(currentConfiguredValue, history,
                proposal is null || currentConfiguredValue is null ? null : proposal == currentConfiguredValue,
                proposal is null || history.IsEmpty ? null : history.Contains(proposal.Value));

        var result = new RecoveryResult(target, decision, frozenDiscovery,
            currentValidation, dependencies, context, comparison, detail);
        session.Record(result);
        return result;
    }

    private static bool HasUnknownRequired(IEnumerable<RecoveryEvidenceRecord> evidence) =>
        evidence.Any(e => e.Required && e.Result == RecoveryEvidenceResult.UNKNOWN);

    private static bool IsForbiddenBlindContextKey(string key) =>
        new[] { "offset", "history", "historical", "default", "score", "confidence", "distance" }
            .Any(word => key.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static bool IsIndependentlyValid(IEnumerable<RecoveryEvidenceRecord> evidence)
    {
        var items = evidence.ToArray();
        return items.Any(e => e.Independent && e.Required && e.Result == RecoveryEvidenceResult.PASS) &&
            items.All(e => !e.Required || e.Result == RecoveryEvidenceResult.PASS);
    }

    private static void AddStage(List<CandidateEliminationStage> stages, EliminationStage stage,
        IEnumerable<MutableCandidate> input, IEnumerable<MutableCandidate> survivors,
        IEnumerable<MutableCandidate> rejected, string? rule)
    {
        var inputArray = input.ToArray();
        var survivingArray = survivors.ToArray();
        var rejectedArray = rejected.ToArray();
        if (inputArray.Length != survivingArray.Length + rejectedArray.Length ||
            inputArray.Select(c => c.Id).Except(survivingArray.Select(c => c.Id)
                .Concat(rejectedArray.Select(c => c.Id)), StringComparer.Ordinal).Any())
            throw new InvalidOperationException("Candidate stage counts do not reconcile.");
        stages.Add(new CandidateEliminationStage(stage,
            inputArray.Select(c => c.Id).ToImmutableArray(),
            survivingArray.Select(c => c.Id).ToImmutableArray(),
            rejectedArray.Select(c => c.Id).ToImmutableArray(),
            rejectedArray.ToImmutableDictionary(c => c.Id,
                c => c.Rejections.Where(r => r.Stage == stage).ToImmutableArray(),
                StringComparer.Ordinal), rule));
    }

    private sealed class MutableCandidate(DiscoveredCandidate found)
    {
        public string Id { get; } = found.Id;
        public long Value { get; } = found.Value;
        public string Origin { get; } = found.Origin;
        public ImmutableArray<RecoveryEvidenceRecord> Evidence { get; } = found.Evidence;
        public List<RecoveryEvidenceRecord> ValidationEvidence { get; } = [];
        public List<CandidateRejection> Rejections { get; } = [];
        public CandidateDisposition Disposition { get; set; } = CandidateDisposition.Unresolved;
        public bool IsRejected => Disposition == CandidateDisposition.Rejected;
        public bool IsEquivalent => Disposition == CandidateDisposition.Equivalent;

        public void Reject(EliminationStage stage, string predicate, string reason)
        {
            Rejections.Add(new CandidateRejection(stage, predicate, reason));
            Disposition = CandidateDisposition.Rejected;
        }

        public void MarkEquivalent(string representative)
        {
            Rejections.Add(new CandidateRejection(EliminationStage.Equivalence,
                "exact-value-equivalence", $"Equivalent to {representative}."));
            Disposition = CandidateDisposition.Equivalent;
        }

        public RecoveryCandidate Freeze() => new(Id, Value, Origin, Evidence,
            ValidationEvidence.ToImmutableArray(), Rejections.ToImmutableArray(), Disposition);
    }
}
