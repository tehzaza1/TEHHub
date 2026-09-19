namespace TEHhub.OffsetDoctor.RecoveryV1;

using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using TEHhub.Offsets.Objects.States;
using TEHhub.Offsets.Objects.UiElement;

/// <summary>Versioned, observation-only semantic discovery for the Runeshape combinations panel.</summary>
public static class Od144RuneshapePanelRecovery
{
    public const string TargetId = "OD-144";
    public const string StrategyId = "od-144-runeshape-semantic-ui-v1";
    public const string ScopeKey = "RuneshapeCombinationsPanel";
    public const string GameUiId = "OD-020";
    public const uint VisibleMask = 0x800;
    // The observed 0x00462EF1 includes the visibility bit; mask that bit in
    // both the versioned signature and each observed element.
    public const uint RawFingerprintV1 = 0x00462EF1;
    public const uint MaskedFingerprint = RawFingerprintV1 & ~VisibleMask;
    public const int RecipeCountV1 = 321;
    public const int MaxNodes = 4096;
    public const int MaxDepth = 5;
    public const int MaxChildren = 512;
    public const int MaxReads = 32768;
    private static readonly int[] RecipeRelativePath = [3, 2, 1, 0];
    private static readonly string[] Chain = ["OD-001", "OD-007", "OD-010", "OD-014", GameUiId];

    public static RecoveryResult Run(RecoverySession session, IReadOnlyList<int>? currentPath = null,
        IEnumerable<IReadOnlyList<int>>? historicalPaths = null, int observationPasses = 1,
        Action<int>? beforeObservation = null)
    {
        var target = RecoveryTargetSpec.Create(TargetId,
            new RecoveryTargetScope(ScopeKey, session.Identity.ProcessName), StrategyId,
            ["runeshape-ui-context"], GameUiId,
            additionalDependencyIds: Chain.Take(4));
        var initial = session.Context;
        var ledger = new List<Observation>();
        var observedStates = new List<string?>();
        var stages = new List<CandidateEliminationStage>();
        var dependencies = ImmutableArray<RecoveryDependencyState>.Empty;
        RecoveryCurrentValidation? currentValidation = null;
        RecoveryTerminalResult? forced = null;
        string? detail = null;
        long gameUi = 0;
        int nodes = 0;
        int reads = 0;
        long bytesScanned = 0;
        var watch = Stopwatch.StartNew();
        const int MaxMilliseconds = 30000;
        string? Guard() => !session.IdentityIsCurrent ? "Process/build identity became stale." :
            ContextRequirementEvaluator.CheckRequired(target, initial, session.Context);
        static RecoveryTerminalResult GuardResult(string reason) =>
            reason.StartsWith("Required context", StringComparison.Ordinal)
                ? RecoveryTerminalResult.BLOCKED_CONTEXT : RecoveryTerminalResult.ERROR;
        var view = new UiRead(session.Memory,
            () => ++reads <= MaxReads && watch.ElapsedMilliseconds <= MaxMilliseconds,
            size => bytesScanned += size);

        try
        {
            if (observationPasses is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(observationPasses));
            detail = Guard();
            if (detail is not null) forced = GuardResult(detail);
            if (forced is null && !DependencyEvaluator.TryResolve(session, target,
                    out gameUi, out dependencies, out detail))
                forced = RecoveryTerminalResult.BLOCKED_DEPENDENCY;
            if (forced is null && (gameUi < 0x10000 || !session.Memory.IsValidAddress(gameUi)))
            {
                forced = RecoveryTerminalResult.BLOCKED_DEPENDENCY;
                detail = "Trusted GameUi anchor is not addressable.";
            }
            if (forced is null)
            {
                long inGame = dependencies.Single(d => d.TargetId == "OD-010").Anchor!.Value;
                long uiRoot = dependencies.Single(d => d.TargetId == "OD-014").Anchor!.Value;
                long uiRootField = Marshal.OffsetOf<InGameStateOffset>(nameof(InGameStateOffset.UiRootStructPtr)).ToInt64();
                long gameUiField = Marshal.OffsetOf<UiRootStruct>(nameof(UiRootStruct.GameUiPtr)).ToInt64();
                if (inGame < 0x10000 || uiRoot < 0x10000 ||
                    !session.Memory.TryRead(inGame + uiRootField, out long observedUiRoot) ||
                    !session.Memory.TryRead(uiRoot + gameUiField, out long observedGameUi) ||
                    observedUiRoot != uiRoot || observedGameUi != gameUi ||
                    !session.Memory.TryRead(gameUi, out UiElementBaseOffset rootShape) ||
                    rootShape.Self.ToInt64() != gameUi)
                {
                    forced = RecoveryTerminalResult.BLOCKED_DEPENDENCY;
                    detail = "InGameState, UiRoot and GameUi trusted pointer relationship is invalid.";
                }
            }
            if (forced is null && currentPath is { Count: > 0 })
            {
                var current = FollowPath(view, gameUi, currentPath);
                var evidence = current == 0 ? ImmutableArray.Create(new RecoveryEvidenceRecord(
                    "configured-child-path", RecoveryEvidenceResult.FAIL,
                    "Configured child path does not resolve in trusted GameUi.", true, true)) :
                    Inspect(view, gameUi, current, currentPath, true).Evidence;
                currentValidation = new(currentPath[0], evidence, true)
                { ChildPath = currentPath.ToImmutableArray() };
                if (evidence.Length > 0 && evidence.All(e => !e.Required || e.Result == RecoveryEvidenceResult.PASS))
                    forced = RecoveryTerminalResult.PASS_CURRENT;
                if (Guard() is string guard) { forced = GuardResult(guard); detail = guard; }
            }
            if (forced is null)
            {
                // The configured-path validation has a separate read budget. Blind
                // discovery starts from an identical budget for every stale path.
                reads = 0;
                bytesScanned = 0;
                watch.Restart();
                for (int pass = 0; pass < observationPasses; pass++)
                {
                    beforeObservation?.Invoke(pass);
                    if (Guard() is string guard) { forced = GuardResult(guard); detail = guard; break; }
                    observedStates.Add(session.Context.Facts.GetValueOrDefault("runeshape-ui-state"));
                    var root = view.Node(gameUi);
                    if (root.Self.ToInt64() != gameUi)
                        throw new IncompleteUiException("Trusted GameUi Self invariant failed.");
                    var children = view.Children(root, MaxChildren);
                    for (int index = 0; index < children.Length; index++)
                    {
                        if (++nodes > MaxNodes) throw new IncompleteUiException("UI node budget exceeded.");
                        long address = children[index];
                        if (address == 0) continue;
                        var node = view.Node(address);
                        // All direct children are considered. The index is provenance only; no index
                        // value is consulted by the semantic predicates or candidate ordering.
                        var candidate = new Observation($"observation-{pass:D2}-node-{index:D3}",
                            address, gameUi, [index], node, pass);
                        ledger.Add(candidate);
                        var shape = Inspect(view, gameUi, address, candidate.Path, false);
                        candidate.Evidence = shape.Evidence;
                    }
                    if (Guard() is string changed) { forced = GuardResult(changed); detail = changed; break; }
                }
                if (forced is null)
                {
                    var input = ledger.ToArray();
                    foreach (var c in input)
                        foreach (var e in c.Evidence.Where(e => e.Required && e.Result != RecoveryEvidenceResult.PASS))
                            c.Reject(EliminationStage.RequiredPredicates, e.Predicate, e.Detail);
                    Stage(stages, EliminationStage.RequiredPredicates, input);
                    input = ledger.Where(c => !c.Rejected).ToArray();
                    foreach (var c in input)
                    {
                        var repeated = Inspect(view, gameUi, c.Address, c.Path, true);
                        c.Validation = repeated.Evidence;
                        foreach (var e in c.Validation.Where(e => e.Required && e.Result != RecoveryEvidenceResult.PASS))
                            c.Reject(EliminationStage.IndependentValidation, e.Predicate, e.Detail);
                    }
                    if (observationPasses > 1 && observedStates.Count == observationPasses &&
                        observedStates.Zip(observedStates.Skip(1)).Any(pair => pair.First != pair.Second))
                    {
                            var stable = input.Where(c => !c.Rejected).ToArray();
                            var uniquePerPass = stable.GroupBy(c => c.Pass).All(g => g.Count() == 1) &&
                                stable.Select(c => c.Pass).Distinct().Count() == observationPasses;
                            var groups = uniquePerPass
                                ? new[] { stable.AsEnumerable() }
                                : stable.GroupBy(c => (c.Address, c.Parent)).Select(g => g.AsEnumerable());
                            foreach (var group in groups)
                            {
                                if (group.Count() < 2) continue;
                                bool correlated = group.Select(c => (c.Node.Flags & VisibleMask) != 0).Distinct().Count() > 1 &&
                                    group.All(c => observedStates[c.Pass] switch
                                    {
                                        "open" => (c.Node.Flags & VisibleMask) != 0,
                                        "closed" => (c.Node.Flags & VisibleMask) == 0,
                                        _ => false
                                    });
                                foreach (var c in group)
                                {
                                    c.Validation = c.Validation.Add(new("visibility-state-correlation",
                                        correlated ? RecoveryEvidenceResult.PASS : RecoveryEvidenceResult.FAIL,
                                        $"states={string.Join("->", observedStates)}; visibility transition={correlated}",
                                        true, true));
                                    if (!correlated)
                                        c.Reject(EliminationStage.IndependentValidation,
                                            "visibility-state-correlation", "UI state changed without a correlated visibility change.");
                                }
                            }
                    }
                    Stage(stages, EliminationStage.IndependentValidation, input);
                    input = ledger.Where(c => !c.Rejected).ToArray();
                    var seen = new Dictionary<(long Address, long Parent), string>();
                    foreach (var c in input)
                    {
                        var key = (c.Address, c.Parent);
                        if (seen.TryGetValue(key, out var first)) c.EquivalentTo = first;
                        else seen.Add(key, c.Id);
                    }
                    if (input.Length == observationPasses && observationPasses > 1 &&
                        input.Select(c => c.Pass).Distinct().Count() == observationPasses &&
                        input.All(c => c.Parent == input[0].Parent))
                        foreach (var c in input.Where(c => c.Pass > 0))
                            c.EquivalentTo = input[0].Id;
                    Stage(stages, EliminationStage.Equivalence, input,
                        "Same role, trusted parent and address within a pass; across passes only when each pass has exactly one validated semantic panel.");
                    if (Guard() is string guard) { forced = GuardResult(guard); detail = guard; }
                }
            }
        }
        catch (IncompleteUiException ex) { forced = RecoveryTerminalResult.ERROR; detail = ex.Message; }
        catch (Exception ex) { forced = RecoveryTerminalResult.ERROR; detail = ex.Message; }

        var survivors = forced is null ? ledger.Where(c => !c.Rejected && c.EquivalentTo is null).ToArray() : [];
        var terminal = forced ?? (survivors.Length switch
        {
            0 => RecoveryTerminalResult.NOT_FOUND,
            1 => RecoveryTerminalResult.PROPOSED,
            _ => RecoveryTerminalResult.AMBIGUOUS
        });
        var ids = survivors.Select(c => c.Id).ToImmutableArray();
        // Paths become output only after semantic and independent validation have frozen survivors.
        var path = terminal == RecoveryTerminalResult.PROPOSED
            ? ledger.Where(c => c.Id == survivors[0].Id || c.EquivalentTo == survivors[0].Id)
                .OrderBy(c => c.Pass).Last().Path.ToImmutableArray() :
            terminal == RecoveryTerminalResult.PASS_CURRENT && currentPath is not null ? currentPath.ToImmutableArray() : [];
        var frozenLedger = ledger.Select(c => c.Freeze(survivors.Contains(c))).ToImmutableArray();
        var scan = ledger.Count == 0 ? null : new DiscoveryScanEvidence(bytesScanned, ledger.Count,
            [$"trusted GameUi=0x{gameUi:X}; nodes={nodes}; max-nodes={MaxNodes}; max-depth={MaxDepth}; max-children={MaxChildren}; max-reads={MaxReads}; max-ms={MaxMilliseconds}"]);
        var discovery = new FrozenDiscoveryResult(frozenLedger, stages.ToImmutableArray(), ids, scan);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new { target.Id, terminal, ids, path, frozenLedger, stages }))));
        var decision = new FrozenDecision(terminal, ids, null, digest)
        {
            ChildPath = path,
            ProposedChildPath = terminal == RecoveryTerminalResult.PROPOSED ? path : []
        };
        // Materialize configured/historical paths only after the discovery and decision digest.
        var history = (historicalPaths ?? []).Select(p => p.ToImmutableArray()).ToImmutableArray();
        var comparison = new ChildPathComparison(currentPath?.ToImmutableArray() ?? [], history,
            path.IsEmpty || currentPath is null ? null : path.SequenceEqual(currentPath),
            path.IsEmpty || history.IsEmpty ? null : history.Any(p => p.SequenceEqual(path)));
        var facts = initial.Facts
            .SetItem("ui-root-provenance", string.Join(" -> ", Chain))
            .SetItem("trusted-game-ui", gameUi == 0 ? "unavailable" : $"0x{gameUi:X}")
            .SetItem("nodes-inspected", nodes.ToString())
            .SetItem("read-count", reads.ToString())
            .SetItem("state-transition-evidence", observedStates.Count == 0 ? "unavailable" :
                string.Join("->", observedStates.Select(s => s ?? "unlabeled")))
            .SetItem("recipe-count-invariant", $"{RecipeCountV1} recipes; versioned V1 hypothesis for {session.Identity.FileVersion}")
            .SetItem("traversal-budgets", $"nodes={MaxNodes},depth={MaxDepth},children={MaxChildren},reads={MaxReads},ms={MaxMilliseconds}");
        var result = new RecoveryResult(target, decision, discovery, currentValidation,
            dependencies, facts, null, detail) { PathComparison = comparison };
        session.Record(result);
        return result;
    }

    private static long FollowPath(UiRead view, long root, IReadOnlyList<int> path)
    {
        if (path.Count > MaxDepth) return 0;
        long address = root;
        foreach (int index in path)
        {
            if (index < 0 || index >= MaxChildren) return 0;
            var children = view.Children(view.Node(address), MaxChildren);
            if (index >= children.Length || children[index] == 0) return 0;
            address = children[index];
        }
        return address;
    }

    private static Shape Inspect(UiRead view, long root, long address, IReadOnlyList<int> path, bool independent)
    {
        var evidence = ImmutableArray.CreateBuilder<RecoveryEvidenceRecord>();
        void Add(string name, bool pass, string detail) => evidence.Add(new(name,
            pass ? RecoveryEvidenceResult.PASS : RecoveryEvidenceResult.FAIL, detail, true, independent));
        var panel = view.Node(address);
        Add("self-invariant", panel.Self.ToInt64() == address,
            $"address=0x{address:X}; Self=0x{panel.Self.ToInt64():X}");
        Add("masked-ui-fingerprint", (panel.Flags & ~VisibleMask) == MaskedFingerprint,
            $"flags=0x{panel.Flags:X}; masked=0x{panel.Flags & ~VisibleMask:X}; expected=0x{MaskedFingerprint:X}; visible={(panel.Flags & VisibleMask) != 0}");
        long expectedParent = path.Count == 1 ? root : FollowPath(view, root, path.Take(path.Count - 1).ToArray());
        bool parent = expectedParent != 0 && panel.ParentPtr.ToInt64() == expectedParent &&
            FollowPath(view, root, path) == address;
        Add("parent-child-reciprocity", parent,
            $"parent=0x{panel.ParentPtr.ToInt64():X}; trusted parent=0x{expectedParent:X}; path=[{string.Join(",", path)}]");
        bool containerValid = view.ValidVector(panel.ChildrensPtr, MaxChildren, out int childCount);
        Add("child-container-shape", containerValid,
            $"count={childCount}; first=0x{panel.ChildrensPtr.First.ToInt64():X}");
        long descendant = address;
        bool relation = containerValid;
        foreach (int index in RecipeRelativePath)
        {
            if (!relation) break;
            var node = view.Node(descendant);
            if (!view.ValidVector(node.ChildrensPtr, MaxChildren, out int count) || index >= count)
            { relation = false; break; }
            descendant = view.Child(node.ChildrensPtr, index);
            if (descendant == 0) { relation = false; break; }
            var child = view.Node(descendant);
            relation = child.Self.ToInt64() == descendant && child.ParentPtr.ToInt64() == node.Self.ToInt64();
        }
        Add("semantic-descendant-relationship", relation,
            $"relative=[{string.Join(",", RecipeRelativePath)}]; descendant=0x{descendant:X}");
        bool recipes = false;
        if (relation)
        {
            var container = view.Node(descendant);
            recipes = view.ValidVector(container.ChildrensPtr, RecipeCountV1, out int count) &&
                count == RecipeCountV1;
            if (recipes)
            {
                for (int index = 0; index < RecipeCountV1; index++)
                {
                    long rowAddress = view.Child(container.ChildrensPtr, index);
                    if (rowAddress == 0) { recipes = false; break; }
                    var row = view.Node(rowAddress);
                    if (row.Self.ToInt64() != rowAddress || row.ParentPtr.ToInt64() != descendant)
                    { recipes = false; break; }
                }
            }
        }
        Add("versioned-runeshape-recipe-shape", recipes,
            $"V1 recipe count={RecipeCountV1}; descendant=0x{descendant:X}; every row requires Self and parent reciprocity");
        if (independent)
        {
            var repeated = view.Node(address);
            Add("repeated-read-stability", repeated.Self.ToInt64() == address &&
                repeated.ParentPtr == panel.ParentPtr &&
                (repeated.Flags & ~VisibleMask) == (panel.Flags & ~VisibleMask) &&
                repeated.ChildrensPtr.First == panel.ChildrensPtr.First &&
                repeated.ChildrensPtr.Last == panel.ChildrensPtr.Last,
                "Repeated panel Self, parent, masked flags, and child vector agree.");
        }
        return new Shape(evidence.ToImmutable());
    }

    private static void Stage(List<CandidateEliminationStage> stages, EliminationStage stage,
        Observation[] input, string? rule = null) => stages.Add(new(stage,
            input.Select(c => c.Id).ToImmutableArray(),
            input.Where(c => !c.Rejected && c.EquivalentTo is null).Select(c => c.Id).ToImmutableArray(),
            input.Where(c => c.Rejected || c.EquivalentTo is not null).Select(c => c.Id).ToImmutableArray(),
            input.Where(c => c.Rejected || c.EquivalentTo is not null).ToImmutableDictionary(c => c.Id,
                c => c.Rejections.Where(r => r.Stage == stage).ToImmutableArray(), StringComparer.Ordinal), rule));

    private sealed record Shape(ImmutableArray<RecoveryEvidenceRecord> Evidence);
    private sealed class Observation(string id, long address, long parent, int[] path, UiElementBaseOffset node, int pass)
    {
        public string Id { get; } = id;
        public long Address { get; } = address;
        public long Parent { get; } = parent;
        public int[] Path { get; } = path;
        public UiElementBaseOffset Node { get; } = node;
        public int Pass { get; } = pass;
        public ImmutableArray<RecoveryEvidenceRecord> Evidence { get; set; } = [];
        public ImmutableArray<RecoveryEvidenceRecord> Validation { get; set; } = [];
        public List<CandidateRejection> Rejections { get; } = [];
        public bool Rejected => Rejections.Count > 0;
        public string? EquivalentTo { get; set; }
        public void Reject(EliminationStage stage, string predicate, string reason) =>
            Rejections.Add(new(stage, predicate, reason));
        public RecoveryCandidate Freeze(bool survivor) => new(Id, Address,
            $"GameUi child; parent=0x{Parent:X}; Self=0x{Node.Self.ToInt64():X}; flags=0x{Node.Flags:X}; visible={(Node.Flags & VisibleMask) != 0}; observed-path=[{string.Join(",", Path)}]",
            Evidence, Validation, Rejections.ToImmutableArray(),
            Rejected ? CandidateDisposition.Rejected : EquivalentTo is not null ? CandidateDisposition.Equivalent :
            survivor ? CandidateDisposition.Survivor : CandidateDisposition.Unresolved)
        { ChildPath = survivor ? Path.ToImmutableArray() : [] };
    }

    private sealed class IncompleteUiException(string message) : Exception(message);
    private sealed class UiRead(IRecoveryReadOnlyMemory memory, Func<bool> withinBudget, Action<int> accountBytes)
    {
        public UiElementBaseOffset Node(long address)
        {
            if (address < 0x10000 || !withinBudget() ||
                !memory.TryRead(address, out UiElementBaseOffset node))
                throw new IncompleteUiException($"Incomplete UI element read at 0x{address:X}.");
            accountBytes(Marshal.SizeOf<UiElementBaseOffset>());
            return node;
        }
        public bool ValidVector(TEHhub.Offsets.Natives.StdVector vector, int maximum, out int count)
        {
            long first = vector.First.ToInt64(), last = vector.Last.ToInt64(), end = vector.End.ToInt64();
            count = 0;
            if (first == 0 && last == 0 && end == 0) return true;
            if (first < 0x10000 || first % 8 != 0 || last < first || end < last ||
                (last - first) % 8 != 0 || (last - first) / 8 > maximum ||
                !memory.IsValidAddress(first) || last > first && !memory.IsValidAddress(last - 8)) return false;
            count = (int)((last - first) / 8);
            return true;
        }
        public long Child(TEHhub.Offsets.Natives.StdVector vector, int index)
        {
            long slot = vector.First.ToInt64() + index * 8L;
            if (!withinBudget() || !memory.TryRead(slot, out long child))
                throw new IncompleteUiException($"Incomplete UI child-vector read at 0x{slot:X}.");
            accountBytes(sizeof(long));
            return child;
        }
        public long[] Children(UiElementBaseOffset node, int maximum)
        {
            if (!ValidVector(node.ChildrensPtr, maximum, out int count))
                throw new IncompleteUiException("Malformed or over-budget UI child vector.");
            var children = new long[count];
            for (int i = 0; i < count; i++) children[i] = Child(node.ChildrensPtr, i);
            return children;
        }
    }
}
