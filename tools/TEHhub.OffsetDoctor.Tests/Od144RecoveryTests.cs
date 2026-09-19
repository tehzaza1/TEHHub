namespace TEHhub.OffsetDoctor.Tests;

using System.Collections.Immutable;
using System.Text.Json;
using TEHhub.OffsetDoctor.Process;
using TEHhub.OffsetDoctor.RecoveryV1;
using TEHhub.OffsetDoctor.Reporting;

public static class Od144RecoveryTests
{
    public static void RunAll(Action<bool, string> check)
    {
        using var fixture = new Fixture();
        var before = fixture.Reader.SnapshotAllBlocks();
        var current = fixture.Run([40]);
        check(current.Decision.TerminalResult == RecoveryTerminalResult.PASS_CURRENT &&
            current.Decision.ChildPath.SequenceEqual([40]) && current.Discovery.Scan is null,
            "OD-144 validates a healthy configured path before blind discovery.");
        check(fixture.Reader.MemoryMatchesSnapshot(before) && !current.Applied,
            "Current validation is read-only and does not apply a path.");

        fixture.MovePanel(17);
        var moved = fixture.Run([40], [[40]]);
        check(moved.Decision.TerminalResult == RecoveryTerminalResult.PROPOSED &&
            moved.Decision.ChildPath.SequenceEqual([17]) &&
            moved.Decision.ProposedChildPath.SequenceEqual([17]) && moved.Decision.Proposal is null,
            "A moved panel yields a semantic child path, never a numeric offset proposal.");
        check(moved.Discovery.CandidateLedger.Length == Fixture.RootChildren &&
            moved.Discovery.CandidateLedger.Count(c => c.Disposition == CandidateDisposition.Survivor) == 1 &&
            moved.Discovery.EliminationStages.All(s => s.InputCount == s.SurvivingCount + s.RejectedCount),
            "Every GameUi child is retained and elimination stages reconcile.");
        var winner = moved.Discovery.CandidateLedger.Single(c => c.Disposition == CandidateDisposition.Survivor);
        check(winner.Evidence.Any(e => e.Predicate == "masked-ui-fingerprint" && e.Result == RecoveryEvidenceResult.PASS) &&
            winner.ValidationEvidence.Any(e => e.Predicate == "versioned-runeshape-recipe-shape" && e.Result == RecoveryEvidenceResult.PASS) &&
            winner.ValidationEvidence.Any(e => e.Predicate == "repeated-read-stability" && e.Result == RecoveryEvidenceResult.PASS) &&
            moved.Context["recipe-count-invariant"].Contains("versioned V1", StringComparison.Ordinal),
            "Fingerprint, descendant rows, repeated reads and versioned recipe evidence are reported.");
        check(moved.Dependencies.Length == 5 && moved.Dependencies.All(d => d.IndependentlyValidated) &&
            moved.Context["ui-root-provenance"].Contains("OD-020", StringComparison.Ordinal) &&
            moved.Context["traversal-budgets"].Contains("reads=", StringComparison.Ordinal),
            "The full trusted dependency chain and traversal budgets are in the report.");

        var changedHistory = fixture.Run([8, 4, 2], [[53], [999]]);
        check(JsonSerializer.Serialize(moved.Discovery) == JsonSerializer.Serialize(changedHistory.Discovery) &&
            moved.Decision.EvidenceDigest == changedHistory.Decision.EvidenceDigest &&
            moved.Decision.ChildPath.SequenceEqual(changedHistory.Decision.ChildPath) &&
            !JsonSerializer.Serialize(moved.PathComparison).Equals(JsonSerializer.Serialize(changedHistory.PathComparison), StringComparison.Ordinal),
            "Arbitrary invalid current and historical paths cannot reach semantic discovery or FrozenDecision.");

        fixture.SetGenericFlags(5, Od144RuneshapePanelRecovery.MaskedFingerprint);
        var falseFingerprint = fixture.Run([40]);
        check(falseFingerprint.Decision.TerminalResult == RecoveryTerminalResult.PROPOSED &&
            falseFingerprint.Discovery.CandidateLedger.Any(c => c.Evidence.Any(e =>
                e.Predicate == "masked-ui-fingerprint" && e.Result == RecoveryEvidenceResult.PASS) &&
                c.RejectionReasons.Any(r => r.Predicate == "semantic-descendant-relationship")),
            "A readable fingerprint match without the recipe structure is rejected.");
        fixture.SetGenericFlags(5, 0);

        fixture.SetGenericFlags(40, Od144RuneshapePanelRecovery.MaskedFingerprint);
        var falseHistorical = fixture.Run([40], [[40]]);
        check(falseHistorical.Decision.TerminalResult == RecoveryTerminalResult.PROPOSED &&
            falseHistorical.Decision.ChildPath.SequenceEqual([17]) &&
            falseHistorical.Discovery.CandidateLedger.Any(c => c.DiscoveryOrigin.Contains("observed-path=[40]", StringComparison.Ordinal) &&
                c.RejectionReasons.Any(r => r.Predicate == "semantic-descendant-relationship")),
            "A readable fingerprint false panel at the historical path is not preferred.");
        fixture.SetGenericFlags(40, 0);

        fixture.ClonePanel(53);
        fixture.SetParent(53, fixture.Generic(2));
        var wrongParent = fixture.Run([40]);
        check(wrongParent.Decision.TerminalResult == RecoveryTerminalResult.PROPOSED &&
            wrongParent.Discovery.CandidateLedger.Any(c => c.RejectionReasons.Any(r =>
                r.Predicate == "parent-child-reciprocity")),
            "A second fingerprint and recipe match with the wrong parent is rejected.");
        fixture.SetParent(53, fixture.Root);
        var ambiguous = fixture.Run([40]);
        check(ambiguous.Decision.TerminalResult == RecoveryTerminalResult.AMBIGUOUS &&
            ambiguous.Decision.ChildPath.IsEmpty && ambiguous.Decision.SurvivorIds.Length == 2 &&
            ambiguous.Discovery.CandidateLedger.Where(c => c.Disposition == CandidateDisposition.Survivor)
                .Select(c => c.ChildPath.Single()).Order().SequenceEqual([17, 53]),
            "Two validated panels remain ambiguous with both derived paths and no preference.");
        fixture.SetFlags(53, 0);
        var countOnly = fixture.Run([40]);
        check(countOnly.Decision.TerminalResult == RecoveryTerminalResult.PROPOSED &&
            countOnly.Discovery.CandidateLedger.Any(c => c.RejectionReasons.Any(r =>
                r.Predicate == "masked-ui-fingerprint")),
            "A 321-row structure with an incorrect fingerprint is rejected.");
        fixture.RemovePanel(53);

        fixture.SetRecipeCount(17, 320);
        var wrongCount = fixture.Run([40]);
        check(wrongCount.Decision.TerminalResult == RecoveryTerminalResult.NOT_FOUND &&
            wrongCount.Discovery.CandidateLedger.Any(c => c.RejectionReasons.Any(r =>
                r.Predicate == "versioned-runeshape-recipe-shape")),
            "Changed catalog count does not silently select a fingerprint match.");
        fixture.SetRecipeCount(17, 321);
        fixture.SetRecipeRowParent(17, 200, fixture.Generic(2));
        check(fixture.Run([40]).Decision.TerminalResult == RecoveryTerminalResult.NOT_FOUND,
            "An incorrect recipe row parent fails the full 321-row semantic shape.");
        fixture.SetRecipeRowParent(17, 200, fixture.RecipeContainer(17));
        fixture.SetFlags(17, 0);
        check(fixture.Run([40]).Decision.TerminalResult == RecoveryTerminalResult.NOT_FOUND,
            "Complete valid traversal without a semantic panel is NOT_FOUND.");
        fixture.SetFlags(17, Od144RuneshapePanelRecovery.MaskedFingerprint);

        check(fixture.Run([40], includeContext: false).Decision.TerminalResult == RecoveryTerminalResult.BLOCKED_CONTEXT,
            "Missing Runeshape context is BLOCKED_CONTEXT.");
        check(fixture.Run([40], trustGameUi: false).Decision.TerminalResult == RecoveryTerminalResult.BLOCKED_DEPENDENCY,
            "Untrusted GameUi blocks traversal.");
        check(fixture.Run([40], trustUiRoot: false).Decision.TerminalResult == RecoveryTerminalResult.BLOCKED_DEPENDENCY,
            "Untrusted UiRoot blocks traversal.");
        fixture.BreakUiRootLink();
        check(fixture.Run([40]).Decision.TerminalResult == RecoveryTerminalResult.BLOCKED_DEPENDENCY,
            "A broken InGameState-to-UiRoot relationship blocks traversal.");
        fixture.RestoreUiRootLink();
        var changedContext = fixture.Run([40], passes: 2, beforeObservation: (session, pass) =>
        {
            if (pass == 1) session.ReplaceContext(RecoveryContextSnapshot.Create([]));
        });
        check(changedContext.Decision.TerminalResult == RecoveryTerminalResult.BLOCKED_CONTEXT,
            "A context change during validation blocks the result.");

        var duplicate = fixture.Run([40], passes: 2);
        check(duplicate.Decision.TerminalResult == RecoveryTerminalResult.PROPOSED &&
            duplicate.Discovery.CandidateLedger.Length == Fixture.RootChildren * 2 &&
            duplicate.Discovery.CandidateLedger.Count(c => c.Disposition == CandidateDisposition.Equivalent) == 1,
            "Repeated observations remain in the ledger and use explicit equivalence.");
        fixture.SetFlags(17, Od144RuneshapePanelRecovery.MaskedFingerprint);
        var transition = fixture.Run([40], passes: 2, state: "closed", beforeObservation: (session, pass) =>
        {
            if (pass == 1)
            {
                fixture.SetFlags(17, Od144RuneshapePanelRecovery.MaskedFingerprint | Od144RuneshapePanelRecovery.VisibleMask);
                session.ReplaceContext(RecoveryContextSnapshot.Create([
                    new("runeshape-ui-context", "true"), new("runeshape-ui-state", "open")]));
            }
        });
        check(transition.Decision.TerminalResult == RecoveryTerminalResult.PROPOSED &&
            transition.Discovery.CandidateLedger.Any(c => c.ValidationEvidence.Any(e =>
                e.Predicate == "visibility-state-correlation" && e.Result == RecoveryEvidenceResult.PASS)),
            "Closed-to-open observations correlate visibility while retaining semantic identity.");
        var reopen = fixture.Run([40], passes: 3, state: "closed", beforeObservation: (session, pass) =>
        {
            bool open = pass == 1;
            fixture.SetFlags(17, Od144RuneshapePanelRecovery.MaskedFingerprint |
                (open ? Od144RuneshapePanelRecovery.VisibleMask : 0));
            session.ReplaceContext(RecoveryContextSnapshot.Create([
                new("runeshape-ui-context", "true"), new("runeshape-ui-state", open ? "open" : "closed")]));
        });
        check(reopen.Decision.TerminalResult == RecoveryTerminalResult.PROPOSED &&
            reopen.Context["state-transition-evidence"] == "closed->open->closed",
            $"Closed-open-closed observations retain one semantic panel and report the transition ({reopen.Decision.TerminalResult}: {reopen.Detail}; reads={reopen.Context["read-count"]}; {reopen.Context["state-transition-evidence"]}).");
        fixture.SetFlags(17, Od144RuneshapePanelRecovery.MaskedFingerprint);

        var oldAddress = fixture.Panel(17);
        fixture.MovePanel(53, recreate: true);
        check(oldAddress != fixture.Panel(53) && fixture.Run([17]).Decision.ChildPath.SequenceEqual([53]),
            "A recreated UI address is rediscovered from semantic structure.");
        fixture.BreakChild(4);
        check(fixture.Run([17]).Decision.TerminalResult == RecoveryTerminalResult.ERROR,
            "Incomplete traversal is ERROR, not NOT_FOUND.");

        using var json = JsonDocument.Parse(RecoveryJsonReportExporter.Serialize(new RecoveryReport(
            "recovery-v1.phase5", fixture.RunIdentity, DateTime.UtcNow, [moved, ambiguous])));
        check(json.RootElement.GetProperty("Results")[0].GetProperty("Applied").GetBoolean() == false &&
            json.RootElement.GetProperty("Results")[1].GetProperty("Decision")
                .GetProperty("ChildPath").GetArrayLength() == 0,
            "JSON reports Applied=false and no preferred ambiguous path.");
        using var writer = new StringWriter();
        RecoveryConsoleReportWriter.Write(writer, new RecoveryReport("recovery-v1.phase5",
            fixture.RunIdentity, DateTime.UtcNow, [moved, ambiguous]));
        check(writer.ToString().Contains("derived child path: [17]", StringComparison.Ordinal) &&
            writer.ToString().Contains("semantic survivor", StringComparison.Ordinal),
            "Console report includes derived and ambiguous survivor paths.");
        Console.WriteLine("[OD-144] Synthetic scenarios passed.\n");
    }

    private sealed class Fixture : IDisposable
    {
        public const int RootChildren = 64;
        public SyntheticMemoryReader Reader { get; } = new();
        public IntPtr Root { get; }
        private readonly IntPtr _inGame;
        private readonly IntPtr _uiRoot;
        private readonly IntPtr _rootVector;
        private readonly IntPtr[] _generic = new IntPtr[RootChildren];
        private readonly Dictionary<int, IntPtr> _panels = new();
        private readonly Dictionary<int, IntPtr> _recipes = new();
        public RecoveryIdentity RunIdentity => Session(true, null).Identity;

        public Fixture()
        {
            Root = Element(IntPtr.Zero, 0);
            _inGame = Reader.AllocateBlock(0x400);
            _uiRoot = Reader.AllocateBlock(0xC00);
            RestoreUiRootLink();
            Reader.WritePointer(_uiRoot + 0xBE0, Root);
            _rootVector = Reader.AllocateBlock(RootChildren * 8);
            Reader.WriteStdVector(Root + 0x10, _rootVector,
                _rootVector + RootChildren * 8, _rootVector + RootChildren * 8);
            for (int i = 0; i < RootChildren; i++)
            {
                _generic[i] = Element(Root, 0);
                SetRootChild(i, _generic[i]);
            }
            ClonePanel(40);
        }
        public IntPtr Generic(int index) => _generic[index];
        public IntPtr Panel(int index) => _panels[index];
        public IntPtr RecipeContainer(int index) => _recipes[index];
        private IntPtr Element(IntPtr parent, uint flags)
        {
            var element = Reader.AllocateBlock(0x300);
            Reader.WritePointer(element + 8, element);
            Reader.WritePointer(element + 0xB8, parent);
            Reader.Write(element + 0x168, flags);
            return element;
        }
        private void Children(IntPtr parent, int count, Dictionary<int, IntPtr> selected)
        {
            var vector = Reader.AllocateBlock(count * 8);
            for (int i = 0; i < count; i++)
                Reader.WritePointer(vector + i * 8, selected.GetValueOrDefault(i));
            Reader.WriteStdVector(parent + 0x10, vector, vector + count * 8, vector + count * 8);
        }
        private void SetRootChild(int index, IntPtr address) =>
            Reader.WritePointer(_rootVector + index * 8, address);
        public void ClonePanel(int index)
        {
            var panel = Element(Root, Od144RuneshapePanelRecovery.MaskedFingerprint);
            IntPtr current = panel;
            foreach (int childIndex in new[] { 3, 2, 1, 0 })
            {
                var child = Element(current, 0);
                Children(current, childIndex + 1, new() { [childIndex] = child });
                current = child;
            }
            var rows = Reader.AllocateBlock(Od144RuneshapePanelRecovery.RecipeCountV1 * 8);
            Reader.WriteStdVector(current + 0x10, rows,
                rows + Od144RuneshapePanelRecovery.RecipeCountV1 * 8,
                rows + Od144RuneshapePanelRecovery.RecipeCountV1 * 8);
            for (int i = 0; i < Od144RuneshapePanelRecovery.RecipeCountV1; i++)
                Reader.WritePointer(rows + i * 8, Element(current, 0));
            _panels[index] = panel;
            _recipes[index] = current;
            SetRootChild(index, panel);
        }
        public void RemovePanel(int index)
        {
            SetRootChild(index, _generic[index]);
            _panels.Remove(index);
            _recipes.Remove(index);
        }
        public void MovePanel(int index, bool recreate = false)
        {
            foreach (int old in _panels.Keys.ToArray())
            {
                if (!recreate)
                {
                    var panel = _panels[old];
                    var recipes = _recipes[old];
                    RemovePanel(old);
                    _panels[index] = panel;
                    _recipes[index] = recipes;
                    SetRootChild(index, panel);
                    return;
                }
                RemovePanel(old);
            }
            ClonePanel(index);
        }
        public void SetFlags(int index, uint flags) => Reader.Write(_panels[index] + 0x168, flags);
        public void SetGenericFlags(int index, uint flags) => Reader.Write(_generic[index] + 0x168, flags);
        public void SetParent(int index, IntPtr parent) => Reader.WritePointer(_panels[index] + 0xB8, parent);
        public void SetRecipeCount(int index, int count)
        {
            Reader.TryRead(_recipes[index] + 0x10, out IntPtr first);
            Reader.WritePointer(_recipes[index] + 0x18, first + count * 8);
        }
        public void SetRecipeRowParent(int panelIndex, int rowIndex, IntPtr parent)
        {
            Reader.TryRead(_recipes[panelIndex] + 0x10, out IntPtr first);
            Reader.TryRead(first + rowIndex * 8, out IntPtr row);
            Reader.WritePointer(row + 0xB8, parent);
        }
        public void BreakChild(int index) => Reader.FreeBlock(_generic[index]);
        public void BreakUiRootLink() => Reader.WritePointer(_inGame + 0x2F0, IntPtr.Zero);
        public void RestoreUiRootLink() => Reader.WritePointer(_inGame + 0x2F0, _uiRoot);
        private RecoverySession Session(bool includeContext, string? state) => new(Reader,
            RecoveryContextSnapshot.Create(includeContext
                ? state is null ? [new("runeshape-ui-context", "true")]
                    : [new("runeshape-ui-context", "true"), new("runeshape-ui-state", state)]
                : []));
        public RecoveryResult Run(IReadOnlyList<int>? current = null,
            IEnumerable<IReadOnlyList<int>>? history = null, bool includeContext = true,
            bool trustGameUi = true, bool trustUiRoot = true, int passes = 1,
            string? state = null, Action<RecoverySession, int>? beforeObservation = null)
        {
            var session = Session(includeContext, state);
            foreach (string id in new[] { "OD-001", "OD-007", "OD-010", "OD-014", "OD-020" })
            {
                if (id == "OD-014" && !trustUiRoot || id == "OD-020" && !trustGameUi) continue;
                Trust(session, id, id switch
                {
                    "OD-010" => _inGame,
                    "OD-014" => _uiRoot,
                    _ => Root
                });
            }
            return Od144RuneshapePanelRecovery.Run(session, current, history, passes,
                pass => beforeObservation?.Invoke(session, pass));
        }
        private static void Trust(RecoverySession session, string id, IntPtr address)
        {
            var target = RecoveryTargetSpec.Create(id, new RecoveryTargetScope("synthetic", id),
                "synthetic-trusted-ui", rootAddress: address.ToInt64());
            new RecoveryCoordinator().Run(session, target, new TrustedDiscovery(address.ToInt64()),
                new TrustedValidator(address.ToInt64()));
        }
        public void Dispose() => Reader.Dispose();
    }

    private sealed class TrustedDiscovery(long address) : IBlindDiscoveryStrategy
    {
        public string StrategyId => "synthetic-trusted-ui";
        public DiscoveryOutcome Discover(IRecoveryReadOnlyMemory memory, BlindDiscoveryRequest request) =>
            new([new("trusted", address, "synthetic independently validated UI anchor",
                [new("readable", RecoveryEvidenceResult.PASS, "Addressable", true, false)])], true);
    }
    private sealed class TrustedValidator(long address) : IIndependentCandidateValidator
    {
        public IndependentValidationOutcome Validate(IRecoveryReadOnlyMemory memory,
            IndependentValidationRequest request) => new([
                new("independent-ui-anchor", memory.IsValidAddress(address) && request.CandidateValue == address
                    ? RecoveryEvidenceResult.PASS : RecoveryEvidenceResult.FAIL,
                    "Synthetic trusted address", true, true)], true);
    }
}
