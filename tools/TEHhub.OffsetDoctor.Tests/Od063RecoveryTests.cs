namespace TEHhub.OffsetDoctor.Tests;

using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using TEHhub.OffsetDoctor.Process;
using TEHhub.OffsetDoctor.RecoveryV1;
using TEHhub.OffsetDoctor.Reporting;
using TEHhub.Offsets.Objects.Components;

public static class Od063RecoveryTests
{
    public static void RunAll(Action<bool, string> check)
    {
        using var fixture = new Fixture();
        fixture.Plant(0x1B0, 263, 321);
        var initial = fixture.Reader.SnapshotAllBlocks();
        var current = fixture.Run(0x1B0);
        check(current.Decision.TerminalResult == RecoveryTerminalResult.PASS_CURRENT &&
            current.Discovery.CandidateLedger.IsEmpty && current.CurrentValidation!.Complete,
            "OD-063 valid configured Health passes without blind discovery.");
        check(current.Dependencies.Single().IndependentlyValidated &&
            current.Dependencies.Single().CandidateId == "validated-life" &&
            current.Dependencies.Single().Anchor is > 0 && !current.Applied,
            "OD-063 uses a validated session-local Life parent and never applies.");
        check(fixture.Reader.MemoryMatchesSnapshot(initial), "OD-063 current validation is read-only.");

        fixture.Clear(0x1B0);
        fixture.Plant(0x1D0, 263, 321);
        var moved = fixture.Run(0x1B0);
        check(moved.Decision.TerminalResult == RecoveryTerminalResult.PROPOSED &&
            moved.Decision.Proposal == 0x1D0 && moved.Discovery.Scan!.BytesScanned > 0,
            "OD-063 recovers a moved Health field in a bounded scan.");
        check(moved.Discovery.CandidateLedger.Length == moved.Discovery.Scan!.RawMatchCount &&
            moved.Discovery.CandidateLedger.All(c => c.Evidence.Length > 0) &&
            moved.Discovery.EliminationStages.All(s => s.InputCount == s.SurvivingCount + s.RejectedCount),
            "OD-063 retains every aligned observation and reconciles elimination stages.");
        check(moved.Discovery.CandidateLedger.Single(c => c.Value == 0x1D0)
            .ValidationEvidence.Count(e => e.Independent && e.Result == RecoveryEvidenceResult.PASS) >= 3,
            "Moved Health has ownership, vital relationship, correlation, and repeat evidence.");

        fixture.Plant(0x258, 50, 80, owner: false);
        var wrongOwner = fixture.Run(0x1B0);
        check(wrongOwner.Decision.TerminalResult == RecoveryTerminalResult.PROPOSED &&
            wrongOwner.Discovery.CandidateLedger.Single(c => c.Value == 0x258)
                .RejectionReasons.Any(r => r.Predicate == "life-back-pointer"),
            "Plausible values with a wrong Life back pointer are rejected.");

        fixture.Plant(0x258, 50, 80);
        var unrelated = fixture.Run(0x1B0);
        check(unrelated.Decision.TerminalResult == RecoveryTerminalResult.PROPOSED &&
            unrelated.Discovery.CandidateLedger.Single(c => c.Value == 0x258)
                .RejectionReasons.Any(r => r.Predicate == "player-hp-current-correlation"),
            "A valid but unrelated VitalStruct fails independently observed player HP.");
        var ambiguous = fixture.Run(0x1B0, observedHp: false);
        check(ambiguous.Decision.TerminalResult == RecoveryTerminalResult.AMBIGUOUS &&
            ambiguous.Decision.SurvivorIds.Length == 2 && ambiguous.Decision.Proposal is null,
            "Two structurally valid VitalStructs without distinguishing HP evidence remain ambiguous.");

        fixture.Clear(0x1D0);
        fixture.Clear(0x258);
        var none = fixture.Run(0x1B0);
        check(none.Decision.TerminalResult == RecoveryTerminalResult.NOT_FOUND &&
            none.Discovery.Scan!.RawMatchCount == none.Discovery.CandidateLedger.Length,
            "Complete scan with no Health candidate is NOT_FOUND.");
        var noContext = fixture.Run(0x1B0, context: false);
        check(noContext.Decision.TerminalResult == RecoveryTerminalResult.BLOCKED_CONTEXT,
            "Missing player or Life context is BLOCKED_CONTEXT.");
        var absentLifeSession = fixture.Session();
        absentLifeSession.ReplaceContext(RecoveryContextSnapshot.Create(absentLifeSession.Context.Facts
            .Select(p => p.Key == "life-component-readable"
                ? new KeyValuePair<string, string>(p.Key, "false") : p)));
        fixture.TrustParent(absentLifeSession);
        check(Od063LifeHealthRecovery.Run(absentLifeSession, 0x1B0).Decision.TerminalResult ==
            RecoveryTerminalResult.BLOCKED_CONTEXT,
            "Explicitly unreadable Life context is BLOCKED_CONTEXT.");
        var noParent = fixture.Run(0x1B0, trustedParent: false);
        check(noParent.Decision.TerminalResult == RecoveryTerminalResult.BLOCKED_DEPENDENCY,
            "Untrusted Life parent is BLOCKED_DEPENDENCY.");

        fixture.Plant(0x1D0, 263, 321);
        var historyA = fixture.Run(0x20, history: [0x258]);
        var historyB = fixture.Run(0x28, history: [0x1D0]);
        check(historyA.Decision.TerminalResult == historyB.Decision.TerminalResult &&
            historyA.Decision.Proposal == historyB.Decision.Proposal &&
            historyA.Decision.EvidenceDigest == historyB.Decision.EvidenceDigest &&
            JsonSerializer.Serialize(historyA.Discovery) == JsonSerializer.Serialize(historyB.Discovery),
            "Changing stale current and historical offsets leaves blind discovery and decision unchanged.");
        check(historyA.PostResultComparison != historyB.PostResultComparison,
            "Configured and historical values enter only post-decision comparison.");

        var duplicateSession = fixture.Session();
        fixture.TrustParent(duplicateSession);
        var duplicateTarget = RecoveryTargetSpec.Create(Od063LifeHealthRecovery.TargetId,
            new RecoveryTargetScope("local-player-life-component", "synthetic"),
            Od063LifeHealthRecovery.StrategyId,
            ["local-player-present", "life-component-readable", "player-health-meaningful"],
            Od063LifeHealthRecovery.ParentId, approvedHypotheses: [StructuralHypothesis.ScalarField]);
        var duplicate = new RecoveryCoordinator().Run(duplicateSession, duplicateTarget,
            new DuplicateDiscovery(), new DuplicateValidator(), 0x20);
        check(duplicate.Decision.TerminalResult == RecoveryTerminalResult.PROPOSED &&
            duplicate.Discovery.CandidateLedger.Length == 2 &&
            duplicate.Discovery.CandidateLedger[1].Disposition == CandidateDisposition.Equivalent,
            "Duplicate parent and field observations use exact-offset equivalence without dropping raw entries.");

        using var shortFixture = new Fixture(0x300);
        shortFixture.Plant(0x1D0, 263, 321);
        var incomplete = shortFixture.Run(0x20);
        check(incomplete.Decision.TerminalResult == RecoveryTerminalResult.ERROR &&
            incomplete.Discovery.CandidateLedger.Length > 0 &&
            incomplete.Detail!.Contains("Incomplete bounded Life scan", StringComparison.Ordinal),
            "Incomplete bounded memory scan is ERROR and retains observed candidates.");

        string json = RecoveryJsonReportExporter.Serialize(new RecoveryReport("recovery-v1.phase3",
            fixture.Session().Identity, DateTime.UtcNow, [moved, ambiguous]));
        using var doc = JsonDocument.Parse(json);
        check(doc.RootElement.GetProperty("Results")[0].GetProperty("Applied").GetBoolean() == false &&
            doc.RootElement.GetProperty("Results")[1].GetProperty("Decision")
                .GetProperty("Proposal").ValueKind == JsonValueKind.Null,
            "OD-063 JSON reports Applied=false and no preferred ambiguous candidate.");
        using var writer = new StringWriter();
        RecoveryConsoleReportWriter.Write(writer, new RecoveryReport("recovery-v1.phase3",
            fixture.Session().Identity, DateTime.UtcNow, [moved, ambiguous]));
        check(writer.ToString().Contains("life+1D0") && writer.ToString().Contains("AMBIGUOUS") &&
            writer.ToString().Contains("Applied=false"),
            "OD-063 console report includes field, ambiguity, and read-only status.");
        Console.WriteLine("[OD-063] Synthetic scenarios passed.\n");
    }

    private sealed class Fixture : IDisposable
    {
        public SyntheticMemoryReader Reader { get; } = new();
        private readonly IntPtr _life;
        private readonly IntPtr _vtable;
        public Fixture(int lifeBytes = 0x400)
        {
            _life = Reader.AllocateBlock(lifeBytes);
            _vtable = Reader.AllocateBlock(0x100);
        }
        public void Plant(int offset, int current, int total, bool owner = true) =>
            Reader.Write(_life + offset, new VitalStruct
            {
                VtablePtr = _vtable,
                PtrToLifeComponent = owner ? _life : _vtable,
                Current = current,
                Total = total
            });
        public void Clear(int offset) => Reader.Write(_life + offset, new VitalStruct());
        public RecoverySession Session(bool context = true) => new(Reader, RecoveryContextSnapshot.Create(
            context ? [new("local-player-present", "true"), new("life-component-readable", "true"),
                new("player-health-meaningful", "true"), new("observed-player-hp-current", "263"),
                new("observed-player-hp-total", "321")] : []));
        public void TrustParent(RecoverySession session)
        {
            var target = RecoveryTargetSpec.Create(Od063LifeHealthRecovery.ParentId,
                new RecoveryTargetScope("local-player-life-component", "synthetic"), "synthetic-trusted-life",
                rootAddress: _life.ToInt64());
            new RecoveryCoordinator().Run(session, target, new ParentDiscovery(_life.ToInt64()),
                new ParentValidator());
        }
        public RecoveryResult Run(int current, bool observedHp = true, bool context = true,
            bool trustedParent = true, IEnumerable<long>? history = null)
        {
            var session = Session(context);
            if (!observedHp)
                session.ReplaceContext(RecoveryContextSnapshot.Create(session.Context.Facts
                    .Where(p => !p.Key.StartsWith("observed-player-hp-", StringComparison.Ordinal))));
            if (trustedParent) TrustParent(session);
            return Od063LifeHealthRecovery.Run(session, current, history);
        }
        public void Dispose() => Reader.Dispose();
    }

    private sealed class ParentDiscovery(long address) : IBlindDiscoveryStrategy
    {
        public string StrategyId => "synthetic-trusted-life";
        public DiscoveryOutcome Discover(IRecoveryReadOnlyMemory memory, BlindDiscoveryRequest request) =>
            new([new("validated-life", address, "synthetic validated Life parent",
                [new("life-structure", RecoveryEvidenceResult.PASS, "Synthetic local-player Life object.", true, false)])], true);
    }
    private sealed class ParentValidator : IIndependentCandidateValidator
    {
        public IndependentValidationOutcome Validate(IRecoveryReadOnlyMemory memory,
            IndependentValidationRequest request) =>
            new([new("owner-chain", RecoveryEvidenceResult.PASS,
                "Synthetic local player to Life ownership chain.", true, true)], true);
    }
    private sealed class DuplicateDiscovery : IBlindDiscoveryStrategy
    {
        public string StrategyId => Od063LifeHealthRecovery.StrategyId;
        public DiscoveryOutcome Discover(IRecoveryReadOnlyMemory memory, BlindDiscoveryRequest request) =>
            new([new("first", 0x1D0, "first observation",
                     [new("read", RecoveryEvidenceResult.PASS, "read", true, false)]),
                 new("repeat", 0x1D0, "repeat observation",
                     [new("read", RecoveryEvidenceResult.PASS, "read", true, false)])], true);
    }
    private sealed class DuplicateValidator : IIndependentCandidateValidator
    {
        public IndependentValidationOutcome Validate(IRecoveryReadOnlyMemory memory,
            IndependentValidationRequest request) =>
            new([new("duplicate-observation", request.CandidateValue == 0x1D0
                ? RecoveryEvidenceResult.PASS : RecoveryEvidenceResult.FAIL,
                "Same parent and field value.", true, true)], true);
    }
}
