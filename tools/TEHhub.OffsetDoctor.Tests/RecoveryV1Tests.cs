namespace TEHhub.OffsetDoctor.Tests;

using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using TEHhub.OffsetDoctor.Process;
using TEHhub.OffsetDoctor.RecoveryV1;
using TEHhub.OffsetDoctor.Reporting;

public static class RecoveryV1Tests
{
    public static void RunAll(Action<bool, string> check)
    {
        Console.WriteLine("[Recovery V1] Synthetic end-to-end and architecture tests...");
        using var fixture = new Fixture();
        fixture.Plant(0x10, 1);
        fixture.Plant(0x20, 0);
        fixture.Plant(0x30, 0);
        var memoryBefore = fixture.Reader.SnapshotAllBlocks();

        RecoveryResult Run(string id, long? current = null, IEnumerable<long>? history = null,
            IBlindDiscoveryStrategy? discovery = null, IIndependentCandidateValidator? validator = null,
            RecoveryContextSnapshot? context = null, string? parent = null, bool applicable = true)
        {
            var session = new RecoverySession(fixture.Reader, context ?? Fixture.Context());
            return new RecoveryCoordinator().Run(session, fixture.Target(id, parent, applicable),
                discovery ?? new SyntheticDiscovery(), validator ?? new SyntheticValidator(), current, history);
        }

        var unique = Run("unique");
        check(unique.Decision.TerminalResult == RecoveryTerminalResult.PROPOSED &&
            unique.Decision.Proposal == 0x10, "Unique validated value is proposed.");
        check(unique.Discovery.CandidateLedger.Length == 3 &&
            unique.Discovery.CandidateLedger.Count(c => c.Disposition == CandidateDisposition.Rejected) == 2,
            "Every discovered candidate remains in the ledger with its disposition.");
        check(unique.Discovery.CandidateLedger.Where(c => c.Disposition == CandidateDisposition.Rejected)
            .All(c => c.RejectionReasons.Any(r => r.Predicate == "semantic-marker")),
            "Rejected candidates retain explicit validator predicate and reason.");
        check(unique.Discovery.EliminationStages.All(s => s.InputCount == s.SurvivingCount + s.RejectedCount),
            "Every elimination stage reconciles its counts.");
        check(unique.Discovery.EliminationStages.All(s =>
            s.InputCandidateIds.ToHashSet().SetEquals(s.SurvivingCandidateIds.Concat(s.RejectedCandidateIds))),
            "Every stage accounts for every input candidate ID.");
        check(unique.Discovery.CandidateLedger.Single(c => c.Id == "slot-10")
            .ValidationEvidence.Any(e => e.Independent && e.Result == RecoveryEvidenceResult.PASS),
            "Proposal has independent PASS evidence.");
        check(!unique.Applied && unique.Decision.EvidenceDigest.Length == 64,
            "Result is frozen with digest and Applied=false.");

        var zero = Run("zero", validator: new SyntheticValidator(forceFail: true));
        check(zero.Decision.TerminalResult == RecoveryTerminalResult.NOT_FOUND &&
            zero.Decision.SurvivorIds.IsEmpty, "Zero validated candidates maps to NOT_FOUND.");
        fixture.SetMarker(0x30, 1);
        var multiple = Run("multiple");
        check(multiple.Decision.TerminalResult == RecoveryTerminalResult.AMBIGUOUS &&
            multiple.Decision.SurvivorIds.Length == 2 && multiple.Decision.Proposal is null,
            "Two distinct validated values map to AMBIGUOUS with no preferred proposal.");
        fixture.SetMarker(0x30, 0);

        var missingContext = Run("missing-context", context: RecoveryContextSnapshot.Create([]));
        check(missingContext.Decision.TerminalResult == RecoveryTerminalResult.BLOCKED_CONTEXT,
            "Missing required context blocks recovery.");
        var brokenParent = Run("broken-parent", parent: "missing-parent");
        check(brokenParent.Decision.TerminalResult == RecoveryTerminalResult.BLOCKED_DEPENDENCY &&
            brokenParent.Dependencies.Length == 1 && !brokenParent.Dependencies[0].IndependentlyValidated,
            "Broken dependency blocks child and yields no value.");
        var incomplete = Run("incomplete", discovery: new SyntheticDiscovery(incomplete: true));
        check(incomplete.Decision.TerminalResult == RecoveryTerminalResult.ERROR &&
            incomplete.Discovery.CandidateLedger.Length == 3, "Incomplete scan is ERROR without dropping enumerated candidates.");
        var incompleteRead = Run("incomplete-read", validator: new SyntheticValidator(incomplete: true));
        check(incompleteRead.Decision.TerminalResult == RecoveryTerminalResult.ERROR,
            "Incomplete independent read is ERROR.");

        var historyA = Run("history-test", current: 0x70, history: [0x20]);
        var historyB = Run("history-test", current: 0x80, history: [0x30]);
        check(historyA.Discovery.CandidateLedger.Select(c => (c.Id, c.Value))
            .SequenceEqual(historyB.Discovery.CandidateLedger.Select(c => (c.Id, c.Value))) &&
            historyA.Decision == historyB.Decision,
            "Current and historical changes do not affect blind candidates or frozen decision.");
        check(historyA.PostResultComparison != historyB.PostResultComparison &&
            historyA.PostResultComparison!.HistoricalValues.Contains(0x20),
            "Historical comparison is post-result data only, even when it matches a false candidate.");
        var currentPass = Run("current-pass", current: 0x10);
        check(currentPass.Decision.TerminalResult == RecoveryTerminalResult.PASS_CURRENT &&
            currentPass.CurrentValidation!.Evidence.Any(e => e.Independent && e.Result == RecoveryEvidenceResult.PASS),
            "Fully validated configured value maps to PASS_CURRENT.");

        var duplicate = Run("duplicate", discovery: new SyntheticDiscovery(duplicate: true));
        check(duplicate.Decision.TerminalResult == RecoveryTerminalResult.PROPOSED &&
            duplicate.Discovery.CandidateLedger.Length == 4 &&
            duplicate.Discovery.CandidateLedger.Single(c => c.Id == "duplicate-10").Disposition == CandidateDisposition.Equivalent &&
            duplicate.Discovery.EliminationStages.Single(s => s.Stage == EliminationStage.Equivalence).RejectedCount == 1,
            "Equivalent values are deduplicated transparently and retained in the ledger.");

        var unknown = Run("unknown", validator: new SyntheticValidator(unknown: true));
        check(unknown.Decision.TerminalResult == RecoveryTerminalResult.ERROR,
            "Required UNKNOWN without context change is ERROR.");
        var discoveryUnknown = Run("discovery-unknown", discovery: new SyntheticDiscovery(unknown: true));
        check(discoveryUnknown.Decision.TerminalResult == RecoveryTerminalResult.ERROR,
            "Required discovery UNKNOWN without context change is ERROR.");
        var discoveryFail = Run("discovery-fail", discovery: new SyntheticDiscovery(failFirst: true));
        check(discoveryFail.Decision.TerminalResult == RecoveryTerminalResult.NOT_FOUND &&
            discoveryFail.Discovery.CandidateLedger.Single(c => c.Id == "slot-10")
                .RejectionReasons.Any(r => r.Stage == EliminationStage.RequiredPredicates),
            "Required discovery FAIL rejects the candidate and retains its reason.");
        var changedSession = new RecoverySession(fixture.Reader, Fixture.Context());
        var changed = new RecoveryCoordinator().Run(changedSession, fixture.Target("changed"),
            new SyntheticDiscovery(afterDiscover: () => changedSession.ReplaceContext(
                RecoveryContextSnapshot.Create([new KeyValuePair<string, string>("zone", "changed")]))),
            new SyntheticValidator());
        check(changed.Decision.TerminalResult == RecoveryTerminalResult.BLOCKED_CONTEXT,
            "Required context change mid-run maps to BLOCKED_CONTEXT.");

        var staleSession = new RecoverySession(fixture.Reader, Fixture.Context());
        var stale = new RecoveryCoordinator().Run(staleSession, fixture.Target("stale"),
            new SyntheticDiscovery(afterDiscover: () => fixture.Reader.Metadata = new ProcessMetadata
            {
                ProcessId = 99999, ProcessName = "SyntheticPoE2.exe", FileVersion = "new-build",
                ModuleBase = fixture.Reader.MainModuleBase, ModuleMemorySize = fixture.Reader.MainModuleSize,
                AttachedTimeUtc = staleSession.Identity.AttachedTimeUtc
            }), new SyntheticValidator());
        check(stale.Decision.TerminalResult == RecoveryTerminalResult.ERROR &&
            stale.Detail!.Contains("stale", StringComparison.Ordinal), "Stale process/build identity is ERROR.");
        fixture.Reader.Metadata = new ProcessMetadata
        {
            ProcessId = 99999, ProcessName = "SyntheticPoE2.exe", FileVersion = "0.2.0.0",
            ModuleBase = fixture.Reader.MainModuleBase, ModuleMemorySize = fixture.Reader.MainModuleSize,
            AttachedTimeUtc = staleSession.Identity.AttachedTimeUtc
        };

        var notApplicable = Run("not-applicable", applicable: false);
        check(notApplicable.Decision.TerminalResult == RecoveryTerminalResult.NOT_APPLICABLE,
            "Inapplicable target maps to NOT_APPLICABLE.");

        var parentSession = new RecoverySession(fixture.Reader, Fixture.Context());
        var parentResult = new RecoveryCoordinator().Run(parentSession, fixture.Target("parent"),
            new SyntheticDiscovery(), new SyntheticValidator());
        check(parentSession.TryGetProvisional("parent", out var provisional) &&
            provisional!.Value == parentResult.Decision.Proposal &&
            provisional.CandidateId == parentResult.Decision.SurvivorIds.Single() &&
            provisional.EvidenceDigest == parentResult.Decision.EvidenceDigest,
            "Unique proposal creates a session-local value with candidate and evidence provenance.");
        check(DependencyEvaluator.TryResolve(parentSession, fixture.Target("child", "parent"),
            out var resolvedAnchor, out var dependencies, out _) &&
            resolvedAnchor == provisional!.Value && dependencies[0].IndependentlyValidated,
            "Only validated proposed parent resolves a child anchor.");
        check(!parentSession.TryGetProvisional("child", out _),
            "Child receives no provisional value before its own validation.");
        var failedParentSession = new RecoverySession(fixture.Reader, Fixture.Context());
        new RecoveryCoordinator().Run(failedParentSession, fixture.Target("failed-parent"),
            new SyntheticDiscovery(), new SyntheticValidator(forceFail: true));
        check(!failedParentSession.TryGetProvisional("failed-parent", out _) &&
            !DependencyEvaluator.TryResolve(failedParentSession,
                fixture.Target("blocked-child", "failed-parent"), out _, out _, out _),
            "A recorded but unvalidated parent never yields an address to a child.");

        var frozenTerminal = unique.Decision;
        var report = new RecoveryReport("recovery-v1.phase1", fixture.SessionIdentity,
            DateTime.UtcNow, [unique, multiple]);
        string json = RecoveryJsonReportExporter.Serialize(report);
        using var doc = JsonDocument.Parse(json);
        check(doc.RootElement.GetProperty("Results")[0].GetProperty("Applied").GetBoolean() == false &&
            doc.RootElement.GetProperty("Results")[1].GetProperty("Decision")
                .GetProperty("Proposal").ValueKind == JsonValueKind.Null,
            "JSON includes Applied=false and no ambiguous proposal.");
        check(doc.RootElement.GetProperty("SchemaVersion").GetString() == "recovery-v1.phase1" &&
            doc.RootElement.GetProperty("Identity").GetProperty("FileVersion").GetString() == "0.2.0.0" &&
            doc.RootElement.GetProperty("Results")[0].GetProperty("Discovery")
                .GetProperty("EliminationStages").GetArrayLength() == 4 &&
            doc.RootElement.GetProperty("Results")[0].GetProperty("PostResultComparison").ValueKind == JsonValueKind.Null,
            "JSON includes version, build identity, elimination ledger, and post-result comparison field.");
        using var writer = new StringWriter();
        RecoveryConsoleReportWriter.Write(writer, report);
        check(writer.ToString().Contains("AMBIGUOUS") &&
            writer.ToString().Contains("slot-10") && writer.ToString().Contains("slot-30") &&
            writer.ToString().Contains("Applied=false"),
            "Console report shows all ambiguous survivors and Applied=false.");
        check(ReferenceEquals(frozenTerminal, unique.Decision) &&
            unique.Decision.TerminalResult == RecoveryTerminalResult.PROPOSED,
            "Post-result reporting and comparison cannot mutate the frozen decision.");

        var blindProperties = typeof(BlindDiscoveryRequest).GetProperties().Select(p => p.Name).ToArray();
        check(!blindProperties.Any(p => p.Contains("Offset", StringComparison.OrdinalIgnoreCase) ||
            p.Contains("History", StringComparison.OrdinalIgnoreCase) ||
            p.Contains("Score", StringComparison.OrdinalIgnoreCase) ||
            p.Contains("Confidence", StringComparison.OrdinalIgnoreCase)),
            "Blind request exposes no current, historical, default, score, or confidence property.");
        check(typeof(StructuralHypothesis).IsEnum &&
            typeof(RecoveryTargetSpec).GetProperty(nameof(RecoveryTargetSpec.ApprovedHypotheses))!
                .PropertyType.GetGenericArguments().Single() == typeof(StructuralHypothesis),
            "ApprovedHypotheses contains typed structural enum values only, with no numeric field/location payload.");
        var nonBlindSession = new RecoverySession(fixture.Reader, Fixture.Context());
        var nonBlind = new RecoveryCoordinator().Run(nonBlindSession,
            fixture.Target("nonblind") with { ApprovedHypotheses = [(StructuralHypothesis)0x1234] },
            new SyntheticDiscovery(), new SyntheticValidator(), historicalValues: [0x1234]);
        check(nonBlind.Decision.TerminalResult == RecoveryTerminalResult.ERROR &&
            nonBlind.Detail!.Contains("NON_BLIND", StringComparison.Ordinal),
            "An arbitrary numeric hypothesis cannot smuggle a candidate location into discovery.");
        var offsetContext = RecoveryTargetSpec.Create("offset-context",
            new RecoveryTargetScope("synthetic", "one"), "synthetic-bounded-pointer",
            ["configured_offset"], rootAddress: 0x10000000,
            approvedHypotheses: [StructuralHypothesis.PointerField]);
        var offsetSession = new RecoverySession(fixture.Reader,
            RecoveryContextSnapshot.Create([new KeyValuePair<string, string>("configured_offset", "16")]));
        var rejectedContext = new RecoveryCoordinator().Run(offsetSession, offsetContext,
            new SyntheticDiscovery(), new SyntheticValidator());
        check(rejectedContext.Decision.TerminalResult == RecoveryTerminalResult.ERROR &&
            rejectedContext.Detail!.Contains("NON_BLIND", StringComparison.Ordinal),
            "Configured-offset context cannot be forwarded into blind discovery.");
        var recoveryTypes = typeof(RecoveryCoordinator).Assembly.GetTypes()
            .Where(t => t.Namespace?.StartsWith("TEHhub.OffsetDoctor.RecoveryV1", StringComparison.Ordinal) == true);
        check(recoveryTypes.All(t => t.GetFields(BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance | BindingFlags.Static).All(f =>
                    f.FieldType.FullName != "TEHhub.OffsetDoctor.Evidence.CandidateResult")) &&
            !typeof(RecoveryCoordinator).GetMethods().Any(m => m.ReturnType.FullName ==
                "TEHhub.OffsetDoctor.Evidence.CandidateResult"),
            "Recovery V1 types have no legacy CandidateResult field or return dependency.");
        check(!typeof(BlindDiscoveryRequest).GetConstructors().Any(),
            "Only the coordinator can construct a sanitized blind request.");
        string? repoRoot = Directory.GetCurrentDirectory();
        while (repoRoot is not null && !File.Exists(Path.Combine(repoRoot, "TEHhub.sln")))
            repoRoot = Directory.GetParent(repoRoot)?.FullName;
        if (repoRoot is not null)
        {
            string source = string.Join("\n", Directory.GetFiles(
                Path.Combine(repoRoot, "tools", "TEHhub.OffsetDoctor", "RecoveryV1"), "*.cs")
                .Select(File.ReadAllText));
            check(!source.Contains("CandidateResult", StringComparison.Ordinal) &&
                !source.Contains("DefaultOffset", StringComparison.Ordinal) &&
                !source.Contains(".Score", StringComparison.Ordinal) &&
                !source.Contains(".Confidence", StringComparison.Ordinal),
                "Recovery V1 decision source has no legacy ranking or DefaultOffset dependency.");
        }
        check(fixture.Reader.MemoryMatchesSnapshot(memoryBefore),
            "Entire synthetic run preserved every game-memory byte.");
        Console.WriteLine("[Recovery V1] Synthetic scenarios passed.\n");
    }

    private sealed class Fixture : IDisposable
    {
        public SyntheticMemoryReader Reader { get; } = new();
        private readonly IntPtr _anchor;
        private readonly Dictionary<int, IntPtr> _slots = [];
        public RecoveryIdentity SessionIdentity => new RecoverySession(Reader, Context()).Identity;

        public Fixture() => _anchor = Reader.AllocateBlock(0x100);

        public void Plant(int offset, int marker)
        {
            var pointer = Reader.AllocateBlock(0x20);
            _slots[offset] = pointer;
            Reader.WritePointer(_anchor + offset, pointer);
            Reader.Write(pointer, marker);
        }

        public void SetMarker(int offset, int marker) => Reader.Write(_slots[offset], marker);

        public RecoveryTargetSpec Target(string id, string? parent = null, bool applicable = true) =>
            RecoveryTargetSpec.Create(id, new RecoveryTargetScope("synthetic", "one"),
                "synthetic-bounded-pointer", ["zone"], parent, _anchor.ToInt64(),
                [StructuralHypothesis.PointerField], applicable);

        public static RecoveryContextSnapshot Context() => RecoveryContextSnapshot.Create(
            [new KeyValuePair<string, string>("zone", "fixture")]);

        public void Dispose() => Reader.Dispose();
    }

    private sealed class SyntheticDiscovery(bool incomplete = false, bool duplicate = false,
        Action? afterDiscover = null, bool unknown = false, bool failFirst = false) : IBlindDiscoveryStrategy
    {
        public string StrategyId => "synthetic-bounded-pointer";

        public DiscoveryOutcome Discover(IRecoveryReadOnlyMemory memory, BlindDiscoveryRequest request)
        {
            var found = ImmutableArray.CreateBuilder<DiscoveredCandidate>();
            foreach (int offset in new[] { 0x10, 0x20, 0x30 })
            {
                if (!memory.TryRead(request.AnchorAddress + offset, out long pointer))
                    return new DiscoveryOutcome(found.ToImmutable(), false, "Synthetic scan read failed.");
                if (pointer == 0) continue;
                var result = unknown ? RecoveryEvidenceResult.UNKNOWN :
                    failFirst && offset == 0x10 ? RecoveryEvidenceResult.FAIL : RecoveryEvidenceResult.PASS;
                found.Add(new DiscoveredCandidate($"slot-{offset:X}", offset, "bounded pointer scan",
                    [new RecoveryEvidenceRecord("readable-pointer", result,
                        "Pointer observation from bounded scan.", true, false)]));
            }
            if (duplicate)
                found.Add(new DiscoveredCandidate("duplicate-10", 0x10, "second synthetic observation",
                    [new RecoveryEvidenceRecord("readable-pointer", RecoveryEvidenceResult.PASS,
                        "Same field observed independently.", true, false)]));
            afterDiscover?.Invoke();
            return new DiscoveryOutcome(found.ToImmutable(), !incomplete);
        }
    }

    private sealed class SyntheticValidator(bool forceFail = false, bool incomplete = false,
        bool unknown = false) : IIndependentCandidateValidator
    {
        public IndependentValidationOutcome Validate(IRecoveryReadOnlyMemory memory,
            IndependentValidationRequest request)
        {
            if (incomplete) return new IndependentValidationOutcome([], false);
            if (unknown) return new IndependentValidationOutcome(
                [new RecoveryEvidenceRecord("semantic-marker", RecoveryEvidenceResult.UNKNOWN,
                    "Unknown synthetic semantics.", true, true)], true);
            if (!memory.TryRead(request.AnchorAddress + request.CandidateValue, out long pointer) ||
                !memory.TryRead(pointer, out int marker))
                return new IndependentValidationOutcome([], false, "Synthetic validation read failed.");
            bool valid = marker == 1 && !forceFail;
            return new IndependentValidationOutcome(
                [new RecoveryEvidenceRecord("semantic-marker",
                    valid ? RecoveryEvidenceResult.PASS : RecoveryEvidenceResult.FAIL,
                    valid ? "Marker matches fixture semantics." : "Marker fails fixture semantics.",
                    true, true)], true);
        }
    }
}
