namespace TEHhub.OffsetDoctor.RecoveryV1;

using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TEHhub.Offsets.Objects.States;
using TEHhub.Offsets.Objects.UiElement;

public sealed class RecoveryV1MultiTargetCoordinator
{
    public static RecoveryAggregateReport Recover(
        RecoverySession session,
        IEnumerable<string> requestedTargetIds,
        IReadOnlyDictionary<string, TargetExecutionInput>? targetInputs = null) =>
        new RecoveryV1MultiTargetCoordinator().Run(session, requestedTargetIds, targetInputs);

    public static RecoveryAggregateReport RecoverAll(
        RecoverySession session,
        IReadOnlyDictionary<string, TargetExecutionInput>? targetInputs = null) =>
        new RecoveryV1MultiTargetCoordinator().RunAll(session, targetInputs);

    public RecoveryAggregateReport RunAll(
        RecoverySession session,
        IReadOnlyDictionary<string, TargetExecutionInput>? targetInputs = null) =>
        Run(session, RecoveryV1Registry.AllTargetIds, targetInputs);

    public RecoveryAggregateReport RunTarget(
        RecoverySession session,
        string targetId,
        TargetExecutionInput? input = null)
    {
        var inputs = input is null ? null : new Dictionary<string, TargetExecutionInput>(StringComparer.OrdinalIgnoreCase)
        {
            [targetId] = input
        };
        return Run(session, [targetId], inputs);
    }

    public RecoveryAggregateReport Run(
        RecoverySession session,
        IEnumerable<string> requestedTargetIds,
        IReadOnlyDictionary<string, TargetExecutionInput>? targetInputs = null)
    {
        var runWatch = Stopwatch.StartNew();
        string runId = Guid.NewGuid().ToString("N");
        var requested = requestedTargetIds.Select(RecoveryV1Registry.NormalizeId).Distinct(StringComparer.Ordinal).ToImmutableArray();

        // 1. Resolve Execution Order & Auto-Expand Runnable Prerequisites
        var executionOrder = RecoveryV1Registry.ResolveExecutionOrder(requested, out var autoAddedRunnableDeps);

        var results = new List<RecoveryResult>();
        var metrics = new List<TargetBudgetMetric>();
        var errors = new List<string>();
        var liveEvidenceList = new List<CurrentRunLiveEvidence>();
        long? totalBytesScanned = null;

        // 2. Execute Targets in Dependency Order
        foreach (string targetId in executionOrder)
        {
            var descriptor = RecoveryV1Registry.Get(targetId);
            var targetInput = targetInputs != null && targetInputs.TryGetValue(targetId, out var inp)
                ? inp
                : new TargetExecutionInput(null, null);

            var targetWatch = Stopwatch.StartNew();
            RecoveryResult result;

            try
            {
                // Verify process identity has not become stale
                if (!session.IdentityIsCurrent)
                {
                    result = CreateFailedResult(session, descriptor, RecoveryTerminalResult.ERROR,
                        "Process/build identity became stale during multi-target recovery run.");
                }
                // Check runnable recovery dependencies (e.g. OD-001)
                else if (!CheckRunnableDependencies(session, descriptor, out string? depError))
                {
                    result = CreateFailedResult(session, descriptor, RecoveryTerminalResult.BLOCKED_DEPENDENCY, depError!);
                }
                // Check intermediate anchor revalidation if OD-001 is provisional
                else if (IsProvisionalOd001(session) && descriptor.RequiredTrustedAnchors.Any(a => a is "OD-014" or "OD-020" or "OD-134") &&
                         !TryRevalidateIntermediateUiChain(session, descriptor, out string? revalError))
                {
                    result = CreateFailedResult(session, descriptor, RecoveryTerminalResult.BLOCKED_DEPENDENCY,
                        revalError ?? "Intermediate UI anchor revalidation failed for provisional OD-001.");
                }
                // Check required non-runnable trusted anchors
                else if (!CheckRequiredTrustedAnchors(session, descriptor, out string? anchorError))
                {
                    result = CreateFailedResult(session, descriptor, RecoveryTerminalResult.BLOCKED_DEPENDENCY, anchorError!);
                }
                else
                {
                    // Execute the canonical strategy adapter
                    result = descriptor.ExecuteTarget(session, targetInput);
                }
            }
            catch (Exception ex)
            {
                result = CreateFailedResult(session, descriptor, RecoveryTerminalResult.ERROR,
                    $"Strategy execution threw unexpected exception: {ex.Message}");
                errors.Add($"{targetId}: {ex.Message}");
            }

            targetWatch.Stop();

            // Record budget metrics
            long elapsedMs = targetWatch.ElapsedMilliseconds;
            long? reads = null;
            if (result.Context.TryGetValue("read-count", out string? readText) && long.TryParse(readText, out long r))
                reads = r;
            long? bytes = result.Discovery.Scan?.BytesScanned;
            if (bytes.HasValue)
            {
                totalBytesScanned = (totalBytesScanned ?? 0) + bytes.Value;
            }
            int? candidates = result.Discovery.CandidateLedger.Length > 0
                ? result.Discovery.CandidateLedger.Length
                : null;

            metrics.Add(new TargetBudgetMetric(targetId, elapsedMs, reads, bytes, candidates));
            results.Add(result);

            // Determine current-run live evidence
            var liveStatus = result.Decision.TerminalResult switch
            {
                RecoveryTerminalResult.PASS_CURRENT => LiveEvidenceStatus.LIVE_CURRENT_VALIDATION_PROVEN,
                RecoveryTerminalResult.PROPOSED => LiveEvidenceStatus.LIVE_RECOVERY_PROVEN,
                RecoveryTerminalResult.BLOCKED_CONTEXT => LiveEvidenceStatus.LIVE_CONTEXT_PENDING,
                _ => LiveEvidenceStatus.SYNTHETIC_ONLY
            };
            liveEvidenceList.Add(new CurrentRunLiveEvidence(targetId, liveStatus,
                $"{result.Decision.TerminalResult} (proposal: {(result.Decision.Proposal is long p ? $"0x{p:X}" : result.Decision.ProposedChildPath.IsEmpty ? result.Decision.ProposedFieldPair?.ToString() ?? "none" : $"[{string.Join(",", result.Decision.ProposedChildPath)}]")})",
                DateTime.UtcNow));
        }

        runWatch.Stop();

        // 3. Deterministic Aggregate Precedence Evaluation
        var aggregateStatus = EvaluateAggregateStatus(results, session.IdentityIsCurrent);

        // 4. Summaries & Proposals Extraction
        var proposals = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var r in results)
        {
            if (r.Decision.TerminalResult == RecoveryTerminalResult.PROPOSED)
            {
                string propText = r.Decision.ProposedFieldPair?.ToString()
                    ?? (!r.Decision.ProposedChildPath.IsEmpty ? $"[{string.Join(",", r.Decision.ProposedChildPath)}]"
                    : (r.Decision.Proposal is long p ? $"0x{p:X}" : "unknown"));
                proposals[RecoveryV1Registry.NormalizeId(r.Target.Id)] = propText;
            }
        }

        var blockedContext = results.Where(r => r.Decision.TerminalResult == RecoveryTerminalResult.BLOCKED_CONTEXT)
            .Select(r => RecoveryV1Registry.NormalizeId(r.Target.Id)).ToImmutableArray();
        var blockedDependency = results.Where(r => r.Decision.TerminalResult == RecoveryTerminalResult.BLOCKED_DEPENDENCY)
            .Select(r => RecoveryV1Registry.NormalizeId(r.Target.Id)).ToImmutableArray();
        var notFound = results.Where(r => r.Decision.TerminalResult == RecoveryTerminalResult.NOT_FOUND)
            .Select(r => RecoveryV1Registry.NormalizeId(r.Target.Id)).ToImmutableArray();
        var ambiguous = results.Where(r => r.Decision.TerminalResult == RecoveryTerminalResult.AMBIGUOUS)
            .Select(r => RecoveryV1Registry.NormalizeId(r.Target.Id)).ToImmutableArray();
        var proposed = results.Where(r => r.Decision.TerminalResult == RecoveryTerminalResult.PROPOSED)
            .Select(r => RecoveryV1Registry.NormalizeId(r.Target.Id)).ToImmutableArray();
        var passedCurrent = results.Where(r => r.Decision.TerminalResult == RecoveryTerminalResult.PASS_CURRENT)
            .Select(r => RecoveryV1Registry.NormalizeId(r.Target.Id)).ToImmutableArray();
        var errorTargets = results.Where(r => r.Decision.TerminalResult == RecoveryTerminalResult.ERROR)
            .Select(r => RecoveryV1Registry.NormalizeId(r.Target.Id)).ToImmutableArray();

        return new RecoveryAggregateReport(
            SchemaVersion: "recovery-v1.phase7",
            RunId: runId,
            Identity: session.Identity,
            TimestampUtc: DateTime.UtcNow,
            AggregateStatus: aggregateStatus,
            RequestedTargets: requested,
            EffectiveTargets: executionOrder,
            AutoAddedRunnableDependencies: autoAddedRunnableDeps,
            ExecutionOrder: executionOrder,
            Results: results.ToImmutableArray(),
            BlockedContextTargets: blockedContext,
            BlockedDependencyTargets: blockedDependency,
            NotFoundTargets: notFound,
            AmbiguousTargets: ambiguous,
            ProposedTargets: proposed,
            PassedCurrentTargets: passedCurrent,
            ErrorTargets: errorTargets,
            Proposals: proposals.ToImmutable(),
            Errors: errors.ToImmutableArray(),
            TargetMetrics: metrics.ToImmutableArray(),
            TotalElapsedMilliseconds: runWatch.ElapsedMilliseconds,
            TotalBytesScanned: totalBytesScanned,
            CurrentRunLiveEvidence: liveEvidenceList.ToImmutableArray());
    }

    private static RecoveryAggregateStatus EvaluateAggregateStatus(
        List<RecoveryResult> results,
        bool identityIsCurrent)
    {
        // ERROR: any target returned ERROR or run-level integrity failure
        if (!identityIsCurrent || results.Any(r => r.Decision.TerminalResult == RecoveryTerminalResult.ERROR))
            return RecoveryAggregateStatus.ERROR;

        // ATTENTION_REQUIRED: PROPOSED, AMBIGUOUS, NOT_FOUND, BLOCKED_DEPENDENCY
        if (results.Any(r => r.Decision.TerminalResult is RecoveryTerminalResult.PROPOSED
                                                      or RecoveryTerminalResult.AMBIGUOUS
                                                      or RecoveryTerminalResult.NOT_FOUND
                                                      or RecoveryTerminalResult.BLOCKED_DEPENDENCY))
            return RecoveryAggregateStatus.ATTENTION_REQUIRED;

        // PARTIAL_CONTEXT: at least one BLOCKED_CONTEXT, and no ERROR or ATTENTION_REQUIRED
        if (results.Any(r => r.Decision.TerminalResult == RecoveryTerminalResult.BLOCKED_CONTEXT))
            return RecoveryAggregateStatus.PARTIAL_CONTEXT;

        // COMPLETE: all requested applicable targets complete with acceptable healthy states (PASS_CURRENT / NOT_APPLICABLE)
        return RecoveryAggregateStatus.COMPLETE;
    }

    private static bool CheckRunnableDependencies(
        RecoverySession session,
        RecoveryV1TargetDescriptor descriptor,
        out string? error)
    {
        error = null;
        foreach (string depId in descriptor.RunnableRecoveryDependencies)
        {
            if (!session.TryGetResult(depId, out var depResult) || depResult is null)
            {
                error = $"Required runnable dependency '{depId}' has not been executed.";
                return false;
            }

            if (depResult.Decision.TerminalResult is not (RecoveryTerminalResult.PASS_CURRENT or RecoveryTerminalResult.PROPOSED))
            {
                error = $"Required runnable dependency '{depId}' terminated with non-viable status: {depResult.Decision.TerminalResult}.";
                return false;
            }
        }
        return true;
    }

    private static bool CheckRequiredTrustedAnchors(
        RecoverySession session,
        RecoveryV1TargetDescriptor descriptor,
        out string? error)
    {
        error = null;
        foreach (string anchorId in descriptor.RequiredTrustedAnchors)
        {
            // Anchors can be present as results (e.g. from parent target or revalidated anchor)
            // or registered in session dependency states
            if (!session.TryGetResult(anchorId, out var anchorResult) || anchorResult is null)
            {
                error = $"Required trusted anchor '{anchorId}' is missing in session.";
                return false;
            }

            if (anchorResult.Decision.TerminalResult is not (RecoveryTerminalResult.PASS_CURRENT or RecoveryTerminalResult.PROPOSED))
            {
                error = $"Required trusted anchor '{anchorId}' is invalid (terminal: {anchorResult.Decision.TerminalResult}).";
                return false;
            }
        }
        return true;
    }

    private static bool IsProvisionalOd001(RecoverySession session) =>
        session.TryGetResult(RecoveryV1Registry.Od001Id, out var res) &&
        res?.Decision.TerminalResult == RecoveryTerminalResult.PROPOSED &&
        session.TryGetProvisional(RecoveryV1Registry.Od001Id, out _);

    private static bool TryRevalidateIntermediateUiChain(
        RecoverySession session,
        RecoveryV1TargetDescriptor descriptor,
        out string? failureReason)
    {
        failureReason = null;

        // Obtain InGameState anchor (OD-010)
        if (!session.TryGetResult("OD-010", out var inGameRes) || inGameRes is null)
        {
            failureReason = "InGameState (OD-010) anchor missing for provisional UI revalidation.";
            return false;
        }

        long inGameAddr = inGameRes.Decision.Proposal
            ?? (inGameRes.CurrentValidation is { } cv && cv.Value > 0 ? cv.Value : 0);

        if (inGameAddr < 0x10000 || !session.Memory.IsValidAddress(inGameAddr))
        {
            failureReason = "InGameState address is invalid.";
            return false;
        }

        long uiRootField = Marshal.OffsetOf<InGameStateOffset>(nameof(InGameStateOffset.UiRootStructPtr)).ToInt64();
        long gameUiField = Marshal.OffsetOf<UiRootStruct>(nameof(UiRootStruct.GameUiPtr)).ToInt64();

        // Revalidate UiRoot (OD-014) at InGameState + uiRootField
        if (!session.Memory.TryRead(inGameAddr + uiRootField, out long uiRootAddr) ||
            uiRootAddr < 0x10000 || !session.Memory.IsValidAddress(uiRootAddr))
        {
            failureReason = $"Intermediate UI anchor revalidation failed: UiRoot at inGame+0x{uiRootField:X} is unreadable or invalid (0x{uiRootAddr:X}).";
            return false;
        }

        // Revalidate GameUi (OD-020) at UiRoot + gameUiField
        if (!session.Memory.TryRead(uiRootAddr + gameUiField, out long gameUiAddr) ||
            gameUiAddr < 0x10000 || !session.Memory.IsValidAddress(gameUiAddr))
        {
            failureReason = $"Intermediate UI anchor revalidation failed: GameUi at uiRoot+0x{gameUiField:X} is unreadable or invalid (0x{gameUiAddr:X}).";
            return false;
        }

        // Validate GameUi Self invariant
        if (!session.Memory.TryRead(gameUiAddr, out UiElementBaseOffset gameUiShape) ||
            gameUiShape.Self.ToInt64() != gameUiAddr)
        {
            failureReason = $"Intermediate UI anchor revalidation failed: GameUi Self invariant failed at 0x{gameUiAddr:X}.";
            return false;
        }

        // Store revalidated intermediate anchors in session with explicit provisional provenance
        if (!session.TryGetResult("OD-014", out _))
        {
            TrustAnchor(session, "OD-014", "UiRoot", uiRootAddr, "provisional-derived: OD-001");
        }
        if (!session.TryGetResult("OD-020", out _))
        {
            TrustAnchor(session, "OD-020", "GameUi", gameUiAddr, "provisional-derived: OD-001");
        }

        // If target requires Atlas Canvas (OD-134), ensure canvas is verified
        if (descriptor.RequiredTrustedAnchors.Contains("OD-134"))
        {
            if (!session.TryGetResult("OD-134", out var canvasRes) || canvasRes is null)
            {
                failureReason = "Required Atlas canvas anchor (OD-134) is missing in session.";
                return false;
            }
            long canvasAddr = canvasRes.Decision.Proposal
                ?? (canvasRes.CurrentValidation is { } canvasCv && canvasCv.Value > 0 ? canvasCv.Value : 0);
            if (canvasAddr < 0x10000 || !session.Memory.IsValidAddress(canvasAddr) ||
                !session.Memory.TryRead(canvasAddr, out UiElementBaseOffset canvasShape) ||
                canvasShape.Self.ToInt64() != canvasAddr)
            {
                failureReason = $"Atlas canvas anchor (OD-134) failed structural validation at 0x{canvasAddr:X}.";
                return false;
            }
        }

        return true;
    }

    private static void TrustAnchor(RecoverySession session, string id, string name, long address, string provenance)
    {
        var target = RecoveryTargetSpec.Create(id, new RecoveryTargetScope("provisional-revalidated", name),
            "provisional-anchor-revalidation", rootAddress: address);
        var evidence = ImmutableArray.Create(new RecoveryEvidenceRecord(
            "provisional-revalidation", RecoveryEvidenceResult.PASS,
            $"Revalidated from provisional parent with provenance '{provenance}'.", true, true));
        var candidate = new DiscoveredCandidate($"reval-{id}", address, provenance, evidence);
        var decision = new FrozenDecision(RecoveryTerminalResult.PROPOSED, [candidate.Id], address,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{id}:{address}:{provenance}"))));
        var discovery = new FrozenDiscoveryResult([new RecoveryCandidate(candidate.Id, address, provenance,
            evidence, evidence, [], CandidateDisposition.Survivor)], [], [candidate.Id]);
        var result = new RecoveryResult(target, decision, discovery, null, [],
            ImmutableDictionary<string, string>.Empty.SetItem("provenance", provenance), null, null);
        session.Record(result);
    }

    private static RecoveryResult CreateFailedResult(
        RecoverySession session,
        RecoveryV1TargetDescriptor descriptor,
        RecoveryTerminalResult terminal,
        string detail)
    {
        var target = RecoveryTargetSpec.Create(descriptor.TargetId,
            new RecoveryTargetScope(descriptor.ScopeKey, session.Identity.ProcessName),
            descriptor.StrategyFamily);
        var decision = new FrozenDecision(terminal, [], null, string.Empty);
        var discovery = new FrozenDiscoveryResult([], [], []);
        var result = new RecoveryResult(target, decision, discovery, null, [],
            ImmutableDictionary<string, string>.Empty, null, detail);
        session.Record(result);
        return result;
    }
}
