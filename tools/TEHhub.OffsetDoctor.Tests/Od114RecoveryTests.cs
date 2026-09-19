using System.Text.Json;
using TEHhub.OffsetDoctor.Process;
using TEHhub.OffsetDoctor.RecoveryV1;
using TEHhub.OffsetDoctor.Reporting;

public static class Od114RecoveryTests
{
    public static void RunAll(Action<bool, string> check)
    {
        using var fixture = new Fixture();
        fixture.Plant(-0x120);
        var before = fixture.Reader.SnapshotAllBlocks();

        var current = fixture.Run(0x120);
        check(current.Decision.TerminalResult == RecoveryTerminalResult.PASS_CURRENT &&
            current.Discovery.Scan is null && current.CurrentValidation!.Complete,
            "OD-114 validates a correct configured relationship before discovery.");

        var moved = fixture.Run(-0x98, [-0x98, -0xA0]);
        check(moved.Decision.TerminalResult == RecoveryTerminalResult.PROPOSED &&
            moved.Decision.Proposal == 0x120 && moved.Discovery.Scan?.RawMatchCount == 129 &&
            moved.Discovery.Scan.BytesScanned == 1032 && moved.Discovery.CandidateLedger.Length == 129 &&
            moved.Decision.SurvivorIds.Length == 1 && !moved.Applied,
            "Moved OD-114 relationship yields one owner-derived proposal from 129 bounded candidates.");
        check(moved.Discovery.CandidateLedger.Min(c => c.Value) == (fixture.ListenerAddress - 0x200) &&
            moved.Discovery.CandidateLedger.Max(c => c.Value) == (fixture.ListenerAddress + 0x200) &&
            moved.Discovery.CandidateLedger.All(c => c.Value % 8 == 0) &&
            moved.Discovery.EliminationStages.All(s => s.InputCount == s.SurvivingCount + s.RejectedCount),
            "OD-114 scan retains every aligned candidate and reconciles every elimination stage.");
        check(moved.Discovery.EliminationStages.Select(s => s.Stage).Take(7).SequenceEqual(
            new[] { EliminationStage.Od114AlignedBases, EliminationStage.Od114ReadableShape,
                EliminationStage.Od114OwnerBackPointer, EliminationStage.Od114AnchorStructure,
                EliminationStage.Od114SocketCount, EliminationStage.Od114GoldenSlots,
                EliminationStage.Od114AnchorPosition }) &&
            moved.Discovery.EliminationStages[1].SurvivingCount > 1,
            "All seven structural stages are reported and many readable candidates are filtered.");
        check(moved.Context["listener-count"] == "1" &&
            moved.Context["listener-addresses"].Contains("0x", StringComparison.Ordinal) &&
            moved.Context["owner-search-bounds"] == "listener ± 0x200" &&
            moved.Context["owner-search-alignment"] == "8" &&
            moved.Context["owner-candidate-count"] == "129" &&
            moved.PostResultComparison!.HistoricalValues.Contains(-0x98) &&
            moved.PostResultComparison.HistoricalValues.Contains(-0xA0),
            "OD-114 report carries listener provenance, declared bounds, count, and post-result historical comparisons.");
        check(moved.Dependencies.Length == 2 && moved.Dependencies.All(d => d.IndependentlyValidated) &&
            moved.Discovery.CandidateLedger.Single(c => c.Disposition == CandidateDisposition.Survivor)
                .ValidationEvidence.Count(e => e.Independent && e.Result == RecoveryEvidenceResult.PASS) >= 7 &&
            fixture.Reader.MemoryMatchesSnapshot(before),
            "OD-114 uses both trusted dependencies, independent validation, and read-only memory.");

        var changedHistory = fixture.Run(0x88, [0x90]);
        check(JsonSerializer.Serialize(moved.Discovery) == JsonSerializer.Serialize(changedHistory.Discovery) &&
            moved.Decision.TerminalResult == changedHistory.Decision.TerminalResult &&
            moved.Decision.Proposal == changedHistory.Decision.Proposal &&
            moved.Decision.EvidenceDigest == changedHistory.Decision.EvidenceDigest &&
            moved.PostResultComparison != changedHistory.PostResultComparison,
            "Changing invalid current and historical deltas leaves discovery and FrozenDecision unchanged.");

        var noContext = fixture.Run(-0x98, includeContext: false);
        check(noContext.Decision.TerminalResult == RecoveryTerminalResult.BLOCKED_CONTEXT &&
            noContext.Discovery.Scan is null, "Absent Expedition Rune Station is BLOCKED_CONTEXT.");
        var noParent = fixture.Run(-0x98, trustStateMachine: false);
        check(noParent.Decision.TerminalResult == RecoveryTerminalResult.BLOCKED_DEPENDENCY &&
            noParent.Discovery.Scan is null, "Untrusted StateMachine blocks listener discovery.");
        var noEntity = fixture.Run(-0x98, trustEntity: false);
        check(noEntity.Decision.TerminalResult == RecoveryTerminalResult.BLOCKED_DEPENDENCY &&
            noEntity.Discovery.Scan is null, "Untrusted Rune Station entity blocks listener discovery.");

        fixture.SetComponentOwner(fixture.OtherEntity);
        var mismatchedOwner = fixture.Run(-0x98);
        check(mismatchedOwner.Decision.TerminalResult == RecoveryTerminalResult.BLOCKED_DEPENDENCY &&
            mismatchedOwner.Discovery.Scan is null,
            "StateMachine owner mismatch blocks discovery before reading the listener chain.");
        fixture.SetComponentOwner(fixture.Entity);

        fixture.ClearListener();
        var noListener = fixture.Run(-0x98);
        check(noListener.Decision.TerminalResult == RecoveryTerminalResult.BLOCKED_CONTEXT &&
            noListener.Discovery.Scan is null, "No usable listener is BLOCKED_CONTEXT.");
        fixture.RestoreListener();

        fixture.Plant(0x100);
        fixture.SetGoldenVector(0x100, false);
        var falseGolden = fixture.Run(-0x98);
        check(falseGolden.Decision.TerminalResult == RecoveryTerminalResult.PROPOSED &&
            falseGolden.Decision.Proposal == 0x120 &&
            falseGolden.Discovery.CandidateLedger.Any(c => c.RejectionReasons.Any(r =>
                r.Stage == EliminationStage.Od114GoldenSlots)),
            "A second back-pointer match with invalid GoldenSlots is rejected.");
        fixture.Clear(0x100);

        fixture.Plant(0x98, fixture.OtherEntity);
        var falseHistorical = fixture.Run(-0x98, [-0x98, -0xA0]);
        check(falseHistorical.Decision.TerminalResult == RecoveryTerminalResult.PROPOSED &&
            falseHistorical.Decision.Proposal == 0x120,
            "A historical displacement matching a false owner cannot affect discovery.");
        fixture.Clear(0x98);

        fixture.SetGoldenEmpty(-0x120);
        var emptyGolden = fixture.Run(0x120);
        check(emptyGolden.Decision.TerminalResult == RecoveryTerminalResult.PASS_CURRENT,
            "A structurally empty GoldenSlots vector remains valid.");
        fixture.SetGoldenVector(-0x120, true);

        fixture.SetAnchor(-0x120, true);
        var anchored = fixture.Run(0x120);
        check(anchored.Decision.TerminalResult == RecoveryTerminalResult.PASS_CURRENT,
            "A valid anchor holder and row relationship passes current validation.");
        fixture.SetAnchor(-0x120, false);
        var badAnchor = fixture.Run(-0x98);
        check(badAnchor.Decision.TerminalResult == RecoveryTerminalResult.NOT_FOUND &&
            badAnchor.Discovery.CandidateLedger.Any(c => c.RejectionReasons.Any(r =>
                r.Stage == EliminationStage.Od114AnchorPosition)),
            "A malformed anchor row relationship is rejected at the final structural stage.");
        fixture.ResetAnchor(-0x120);

        fixture.Plant(0x98, fixture.OtherEntity);
        var wrongOwner = fixture.Run(-0x98);
        check(wrongOwner.Decision.TerminalResult == RecoveryTerminalResult.PROPOSED &&
            wrongOwner.Discovery.CandidateLedger.Any(c => c.RejectionReasons.Any(r =>
                r.Stage == EliminationStage.Od114OwnerBackPointer)),
            "A plausible socket count with an incorrect owner is rejected.");
        fixture.Clear(0x98);

        fixture.DuplicateListener();
        var duplicates = fixture.Run(-0x98);
        check(duplicates.Decision.TerminalResult == RecoveryTerminalResult.PROPOSED &&
            duplicates.Discovery.CandidateLedger.Length == 258 &&
            duplicates.Discovery.CandidateLedger.Count(c => c.Disposition == CandidateDisposition.Equivalent) == 1 &&
            duplicates.Decision.SurvivorIds.Length == 1,
            "Duplicate listener observations remain in the ledger and deduplicate by owner and delta.");
        fixture.SingleListener();

        fixture.Plant(0x100);
        var ambiguous = fixture.Run(-0x98);
        check(ambiguous.Decision.TerminalResult == RecoveryTerminalResult.AMBIGUOUS &&
            ambiguous.Decision.SurvivorIds.Length == 2 && ambiguous.Decision.Proposal is null &&
            ambiguous.Discovery.CandidateLedger.Count(c => c.Disposition == CandidateDisposition.Survivor &&
                c.DiscoveryOrigin.Contains("listener-minus-owner=", StringComparison.Ordinal)) == 2,
            "Two owner-derived station layouts remain ambiguous with no preferred delta.");
        fixture.Clear(-0x120);
        fixture.Clear(0x100);
        var absent = fixture.Run(-0x98);
        check(absent.Decision.TerminalResult == RecoveryTerminalResult.NOT_FOUND &&
            absent.Discovery.CandidateLedger.Length == 129,
            "Present listener with no owner-derived station is NOT_FOUND after a complete scan.");

        using var partial = new Fixture(0x360);
        var incomplete = partial.Run();
        check(incomplete.Decision.TerminalResult == RecoveryTerminalResult.ERROR &&
            incomplete.Discovery.CandidateLedger.Length == 129,
            "A partial read inside the bounded scan is ERROR.");

        using var json = JsonDocument.Parse(RecoveryJsonReportExporter.Serialize(new RecoveryReport(
            "recovery-v1.phase4", fixture.RunSessionIdentity, DateTime.UtcNow, [moved, ambiguous])));
        check(json.RootElement.GetProperty("Results")[0].GetProperty("Applied").GetBoolean() == false &&
            json.RootElement.GetProperty("Results")[1].GetProperty("Decision")
                .GetProperty("Proposal").ValueKind == JsonValueKind.Null,
            "OD-114 report remains read-only and serializes ambiguity without a proposal.");
        Console.WriteLine("[OD-114] Synthetic scenarios passed.\n");
    }

    private sealed class Fixture : IDisposable
    {
        public SyntheticMemoryReader Reader { get; } = new();
        private readonly IntPtr _stateMachine;
        private readonly IntPtr _listenerBlock;
        private readonly IntPtr _node;
        private readonly IntPtr _vector;
        private readonly IntPtr _goldenSlots;
        private readonly IntPtr _holder;
        private readonly IntPtr _holderNode;
        private readonly IntPtr _table;
        public IntPtr Entity { get; }
        public IntPtr OtherEntity { get; }
        private IntPtr Listener => _listenerBlock + 0x300;
        public long ListenerAddress => Listener.ToInt64();
        public RecoveryIdentity RunSessionIdentity => Session().Identity;

        public Fixture(int listenerBlockSize = 0x800)
        {
            Entity = Reader.AllocateBlock(0x100);
            OtherEntity = Reader.AllocateBlock(0x100);
            _stateMachine = Reader.AllocateBlock(0x200);
            _listenerBlock = Reader.AllocateBlock(listenerBlockSize);
            _node = Reader.AllocateBlock(0x20);
            _vector = Reader.AllocateBlock(0x20);
            _goldenSlots = Reader.AllocateBlock(0x20);
            _holder = Reader.AllocateBlock(0x80);
            _holderNode = Reader.AllocateBlock(0x20);
            _table = Reader.AllocateBlock(0x400);
            Reader.Write(_goldenSlots, 0);
            Reader.WritePointer(_holder + 0x28, _holderNode);
            Reader.WritePointer(_holderNode, _table);
            SetComponentOwner(Entity);
            Reader.WritePointer(_vector, _node);
            Reader.WriteStdVector(_stateMachine + 0x20, _vector, _vector + 8, _vector + 8);
            RestoreListener();
        }

        public void SetComponentOwner(IntPtr owner) => Reader.WritePointer(_stateMachine + 8, owner);
        public void ClearListener() => Reader.WritePointer(_node, IntPtr.Zero);
        public void RestoreListener() => Reader.WritePointer(_node, Listener);
        public void DuplicateListener()
        {
            Reader.WritePointer(_vector + 8, _node);
            Reader.WriteStdVector(_stateMachine + 0x20, _vector, _vector + 16, _vector + 16);
        }
        public void SingleListener() =>
            Reader.WriteStdVector(_stateMachine + 0x20, _vector, _vector + 8, _vector + 8);
        public void SetGoldenVector(int delta, bool valid)
        {
            var station = Listener + delta;
            Reader.WriteStdVector(station + 0x40, valid ? _goldenSlots : _goldenSlots + 8,
                _goldenSlots + 4, valid ? _goldenSlots + 4 : _goldenSlots + 8);
        }
        public void SetGoldenEmpty(int delta) =>
            Reader.WriteStdVector(Listener + delta + 0x40, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        public void SetAnchor(int delta, bool valid)
        {
            Reader.WritePointer(Listener + delta + 0x28, _table + (valid ? 0x68 : 0x69));
            Reader.WritePointer(Listener + delta + 0x30, _holder);
        }
        public void ResetAnchor(int delta)
        {
            Reader.WritePointer(Listener + delta + 0x28, IntPtr.Zero);
            Reader.WritePointer(Listener + delta + 0x30, IntPtr.Zero);
        }
        public void Plant(int delta, IntPtr? owner = null)
        {
            Reader.WritePointer(Listener + delta + 0x10, owner ?? Entity);
            Reader.WritePointer(Listener + delta + 0x28, IntPtr.Zero);
            Reader.WritePointer(Listener + delta + 0x30, IntPtr.Zero);
            Reader.Write(Listener + delta + 0x38, 6);
            Reader.Write(Listener + delta + 0x3C, 0);
            SetGoldenVector(delta, true);
        }
        public void Clear(int delta)
        {
            Reader.WritePointer(Listener + delta + 0x10, IntPtr.Zero);
            Reader.Write(Listener + delta + 0x38, 0);
            SetGoldenVector(delta, false);
        }
        private RecoverySession Session(bool includeContext = true) => new(Reader,
            RecoveryContextSnapshot.Create(includeContext
                ? [new("expedition-rune-station-present", "true"), new("state-machine-present", "true")]
                : []));

        public RecoveryResult Run(long? current = null, IEnumerable<long>? history = null,
            bool includeContext = true, bool trustStateMachine = true, bool trustEntity = true)
        {
            var session = Session(includeContext);
            if (trustEntity) Trust(session, Od114RuneStationOwnerRecovery.RuneStationEntityId, Entity);
            if (trustStateMachine) Trust(session, Od114RuneStationOwnerRecovery.StateMachineId, _stateMachine);
            return Od114RuneStationOwnerRecovery.Run(session, current, history);
        }
        private static void Trust(RecoverySession session, string id, IntPtr address)
        {
            var target = RecoveryTargetSpec.Create(id, new RecoveryTargetScope("synthetic-trusted", id),
                "synthetic-anchor", rootAddress: address.ToInt64());
            new RecoveryCoordinator().Run(session, target, new TrustedDiscovery(address.ToInt64()),
                new TrustedValidator(address.ToInt64()));
        }
        public void Dispose() => Reader.Dispose();
    }

    private sealed class TrustedDiscovery(long address) : IBlindDiscoveryStrategy
    {
        public string StrategyId => "synthetic-anchor";
        public DiscoveryOutcome Discover(IRecoveryReadOnlyMemory memory, BlindDiscoveryRequest request) =>
            new([new("trusted-anchor", address, "synthetic independently checked anchor",
                [new("anchor-readable", RecoveryEvidenceResult.PASS, "Addressable synthetic object.", true, false)])], true);
    }
    private sealed class TrustedValidator(long address) : IIndependentCandidateValidator
    {
        public IndependentValidationOutcome Validate(IRecoveryReadOnlyMemory memory,
            IndependentValidationRequest request) =>
            new([new("independent-anchor", memory.IsValidAddress(address) && request.CandidateValue == address
                ? RecoveryEvidenceResult.PASS : RecoveryEvidenceResult.FAIL,
                "Independent synthetic address check.", true, true)], true);
    }
}
