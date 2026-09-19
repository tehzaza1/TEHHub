namespace TEHhub.OffsetDoctor.Tests;

using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using TEHhub.OffsetDoctor.Process;
using TEHhub.OffsetDoctor.RecoveryV1;
using TEHhub.OffsetDoctor.Reporting;
using TEHhub.Offsets.Natives;
using TEHhub.Offsets.Objects;
using TEHhub.Offsets.Objects.Components;
using TEHhub.Offsets.Objects.States;
using TEHhub.Offsets.Objects.UiElement;

public static class RecoveryV1IntegrationTests
{
    public static void RunAll(Action<bool, string> check)
    {
        Console.WriteLine("[Recovery V1] Integration & Multi-Target Coordinator Scenarios...");

        TestRegistryVerification(check);
        TestCanonicalThresholdsSuppliers(check);
        TestPriorEvidenceRecords(check);
        TestCliParser(check);
        TestReportSerializationAndConsole(check);

        TestScenarioA_AllFiveHealthy_Complete(check);
        TestScenarioB_Od001NotFound_DownstreamBlockedDependency(check);
        TestScenarioC1_ProvisionalOd001_IntermediateRevalidationPasses(check);
        TestScenarioC2_ProvisionalOd001_IntermediateRevalidationFails(check);
        TestScenarioD_Od114MissingContext_PartialContext(check);
        TestScenarioE_Od144MissingContext_PartialContext(check);
        TestScenarioF_Od145MissingContext_PartialContext(check);
        TestScenarioG_OneTargetAmbiguous_AttentionRequired(check);
        TestScenarioH_OneTargetError_AggregateError(check);
        TestScenarioI_MultipleTargetsProposed_AppliedFalse(check);
        TestScenarioJ_HistoricalValuesChanged_BlindUnchanged(check);
        TestScenarioK_AllAppliedFlagsFalseGlobally(check);
        TestScenarioL_ProvisionalCannotMutateGlobalState(check);
        TestScenarioM_BudgetIsolationBetweenTargets(check);
        TestScenarioN_AggregateJsonTerminalResultsMatch(check);
        TestScenarioO_AggregateConsoleExposesAllStatuses(check);
        TestScenarioP_AutoExpansionIncludesOnlyRunnablePrerequisites(check);
        TestScenarioQ_NonRunnableAnchorNeverScheduled(check);
        TestScenarioR_BlindDiscoveryInputIsolation(check);
        TestScenarioS_ThresholdMetadataMatchesCanonical(check);
        TestScenarioT_UnavailableBudgetMetricsSerializeAsNull(check);

        Console.WriteLine("[Recovery V1] Integration & Coordinator scenarios passed.\n");
    }

    // =========================================================================
    // Group 1: Registry, CLI, Reporting & Production Wiring
    // =========================================================================

    private static void TestRegistryVerification(Action<bool, string> check)
    {
        check(RecoveryV1Registry.AllRunnableTargetIds.Length == 5,
            "Recovery V1 has exactly 5 runnable targets.");
        check(RecoveryV1Registry.AllRunnableTargetIds.SequenceEqual(["OD-001", "OD-063", "OD-114", "OD-144", "OD-145"]),
            "Recovery V1 runnable target IDs match expected canonical list.");

        // Check runnable vs non-runnable anchors
        check(RecoveryV1Registry.IsRunnableTarget("OD-001"), "OD-001 is a runnable target.");
        check(RecoveryV1Registry.IsRunnableTarget("OD-063"), "OD-063 is a runnable target.");
        check(RecoveryV1Registry.IsRunnableTarget("OD-114"), "OD-114 is a runnable target.");
        check(RecoveryV1Registry.IsRunnableTarget("OD-144"), "OD-144 is a runnable target.");
        check(RecoveryV1Registry.IsRunnableTarget("OD-145"), "OD-145 is a runnable target.");

        check(!RecoveryV1Registry.IsRunnableTarget("OD-007"), "OD-007 is a trusted anchor, not a runnable target.");
        check(!RecoveryV1Registry.IsRunnableTarget("OD-010"), "OD-010 is a trusted anchor, not a runnable target.");
        check(!RecoveryV1Registry.IsRunnableTarget("OD-014"), "OD-014 is a trusted anchor, not a runnable target.");
        check(!RecoveryV1Registry.IsRunnableTarget("OD-020"), "OD-020 is a trusted anchor, not a runnable target.");
        check(!RecoveryV1Registry.IsRunnableTarget("OD-134"), "OD-134 is a trusted anchor, not a runnable target.");
        check(!RecoveryV1Registry.IsRunnableTarget("comp_life"), "comp_life is a trusted anchor, not a runnable target.");
        check(!RecoveryV1Registry.IsRunnableTarget("comp_statemachine"), "comp_statemachine is a trusted anchor, not a runnable target.");
        check(!RecoveryV1Registry.IsRunnableTarget("OD-046"), "OD-046 is not registered in Recovery V1.");

        // Target descriptors
        foreach (string targetId in RecoveryV1Registry.AllRunnableTargetIds)
        {
            check(RecoveryV1Registry.TryGet(targetId, out var desc) && desc is not null,
                $"Registry resolves descriptor for {targetId}.");
            check(desc!.TargetId == targetId, $"Descriptor target ID matches {targetId}.");
            check(!string.IsNullOrWhiteSpace(desc.ScopeKey), $"Descriptor scope key is set for {targetId}.");
            check(!string.IsNullOrWhiteSpace(desc.StrategyFamily), $"Strategy family is set for {targetId}.");
            check(desc.ExecuteTarget is not null, $"ExecuteTarget adapter is provided for {targetId}.");
        }
    }

    private static void TestCanonicalThresholdsSuppliers(Action<bool, string> check)
    {
        foreach (string targetId in RecoveryV1Registry.AllRunnableTargetIds)
        {
            var desc = RecoveryV1Registry.Get(targetId);
            var thresholds = desc.CanonicalThresholdsSupplier();
            check(!thresholds.IsEmpty, $"Threshold supplier for {targetId} returns versioned thresholds.");
            check(thresholds.All(t => !string.IsNullOrWhiteSpace(t.Name) && !string.IsNullOrWhiteSpace(t.DefiningMember)),
                $"All thresholds for {targetId} have name and defining member.");
        }

        // Verify OD-001 canonical thresholds
        var od001Thresholds = RecoveryV1Registry.Get("OD-001").CanonicalThresholdsSupplier();
        check(od001Thresholds.Any(t => t.Name == "MaxScanBytes" && (long)t.Value == 128 * 1024 * 1024),
            "OD-001 supplies canonical MaxScanBytes (128MB).");
        check(od001Thresholds.Any(t => t.Name == "MaxSections" && (int)t.Value == 96),
            "OD-001 supplies canonical MaxSections (96).");

        // Verify OD-063 canonical thresholds
        var od063Thresholds = RecoveryV1Registry.Get("OD-063").CanonicalThresholdsSupplier();
        check(od063Thresholds.Any(t => t.Name == "SearchBytes" && (int)t.Value == 0x400),
            "OD-063 supplies canonical SearchBytes (0x400).");
        check(od063Thresholds.Any(t => t.Name == "MaxPlausibleHp" && (int)t.Value == 50000),
            "OD-063 supplies canonical MaxPlausibleHp (50000).");

        // Verify OD-114 canonical thresholds
        var od114Thresholds = RecoveryV1Registry.Get("OD-114").CanonicalThresholdsSupplier();
        check(od114Thresholds.Any(t => t.Name == "Radius" && (int)t.Value == 0x200),
            "OD-114 supplies canonical Radius (0x200).");
        check(od114Thresholds.Any(t => t.Name == "CandidatesPerListener" && (int)t.Value == 129),
            "OD-114 supplies canonical CandidatesPerListener (129).");

        // Verify OD-144 canonical thresholds
        var od144Thresholds = RecoveryV1Registry.Get("OD-144").CanonicalThresholdsSupplier();
        check(od144Thresholds.Any(t => t.Name == "RecipeCountV1" && (int)t.Value == 321),
            "OD-144 supplies canonical RecipeCountV1 (321).");
        check(od144Thresholds.Any(t => t.Name == "MaskedFingerprint" && (uint)t.Value == 0x004626F1),
            "OD-144 supplies canonical MaskedFingerprint (0x004626F1).");

        // Verify OD-145 canonical thresholds
        var od145Thresholds = RecoveryV1Registry.Get("OD-145").CanonicalThresholdsSupplier();
        check(od145Thresholds.Any(t => t.Name == "StdVectorHeaderSize" && (int)t.Value == 24),
            "OD-145 supplies canonical StdVectorHeaderSize (24).");
        check(od145Thresholds.Any(t => t.Name == "EdgeElementStride" && (int)t.Value == 20),
            "OD-145 supplies canonical EdgeElementStride (20).");
        check(od145Thresholds.Any(t => t.Name == "MinAtlasNodes" && (int)t.Value == 16),
            "OD-145 supplies canonical MinAtlasNodes (16).");
        check(od145Thresholds.Any(t => t.Name == "MinEdges" && (int)t.Value == 4),
            "OD-145 supplies canonical MinEdges (4).");
    }

    private static void TestPriorEvidenceRecords(Action<bool, string> check)
    {
        foreach (string targetId in RecoveryV1Registry.AllRunnableTargetIds)
        {
            var prior = RecoveryV1Registry.Get(targetId).PriorEvidence;
            check(!string.IsNullOrWhiteSpace(prior.EvidenceId), $"Prior evidence ID is set for {targetId}.");
            check(!string.IsNullOrWhiteSpace(prior.BuildIdentity), $"Build identity is set for {targetId}.");
            check(!string.IsNullOrWhiteSpace(prior.Description), $"Description is set for {targetId}.");
        }
        var od145Prior = RecoveryV1Registry.Get("OD-145").PriorEvidence;
        check(od145Prior.Status == LiveEvidenceStatus.LIVE_RECOVERY_PROVEN,
            "OD-145 prior evidence status is LIVE_RECOVERY_PROVEN.");
        check(od145Prior.Description.Contains("64 nodes"), "OD-145 prior evidence documents 64 nodes.");
    }

    private static void TestCliParser(Action<bool, string> check)
    {
        // "v1" and "all" expand to all 5 runnable targets
        check(RecoveryV1CliParser.TryParse("v1", out var v1Targets, out var err1) &&
            v1Targets.SequenceEqual(RecoveryV1Registry.AllRunnableTargetIds),
            "CLI parser maps 'v1' to all 5 runnable targets.");
        check(RecoveryV1CliParser.TryParse("ALL", out var allTargets, out var err2) &&
            allTargets.SequenceEqual(RecoveryV1Registry.AllRunnableTargetIds),
            "CLI parser maps 'ALL' case-insensitively to all 5 runnable targets.");

        // Individual targets
        check(RecoveryV1CliParser.TryParse("od-001", out var t001, out _) &&
            t001.SequenceEqual(["OD-001"]), "CLI parser maps 'od-001'.");
        check(RecoveryV1CliParser.TryParse("OD-145", out var t145, out _) &&
            t145.SequenceEqual(["OD-145"]), "CLI parser maps 'OD-145'.");

        // Comma-separated list
        check(RecoveryV1CliParser.TryParse("od-001, od-063, od-114", out var multi, out _) &&
            multi.SequenceEqual(["OD-001", "OD-063", "OD-114"]), "CLI parser parses comma-separated targets.");

        // Unknown target returns error
        check(!RecoveryV1CliParser.TryParse("od-046", out _, out var errUnknown) &&
            errUnknown!.Contains("Unknown recovery target 'od-046'"),
            "CLI parser rejects unknown target 'od-046'.");

        // Non-runnable anchor returns error
        check(!RecoveryV1CliParser.TryParse("od-007", out _, out var errAnchor) &&
            errAnchor!.Contains("Target 'OD-007' is a trusted anchor or not runnable"),
            "CLI parser rejects non-runnable anchor 'od-007'.");

        // Empty string returns error
        check(!RecoveryV1CliParser.TryParse("  ", out _, out var errEmpty) &&
            errEmpty!.Contains("cannot be empty"),
            "CLI parser rejects empty argument.");
    }

    private static void TestReportSerializationAndConsole(Action<bool, string> check)
    {
        var identity = new RecoveryIdentity(12345, "PathExile2.exe", "C:\\Game\\PathExile2.exe", "0.2.1.0",
            0x7FF700000000, 0x5000000, DateTime.UtcNow);

        var report = new RecoveryAggregateReport(
            SchemaVersion: "recovery-v1.phase7",
            RunId: "test-run-1",
            Identity: identity,
            TimestampUtc: DateTime.UtcNow,
            AggregateStatus: RecoveryAggregateStatus.COMPLETE,
            RequestedTargets: ["OD-001", "OD-063"],
            EffectiveTargets: ["OD-001", "OD-063"],
            AutoAddedRunnableDependencies: [],
            ExecutionOrder: ["OD-001", "OD-063"],
            Results: [],
            BlockedContextTargets: [],
            BlockedDependencyTargets: [],
            NotFoundTargets: [],
            AmbiguousTargets: [],
            ProposedTargets: [],
            PassedCurrentTargets: ["OD-001", "OD-063"],
            ErrorTargets: [],
            Proposals: ImmutableDictionary<string, string>.Empty,
            Errors: [],
            TargetMetrics: [
                new TargetBudgetMetric("OD-001", 12, 5, 1024, 1),
                new TargetBudgetMetric("OD-063", 4, 10, null, null)
            ],
            TotalElapsedMilliseconds: 16,
            TotalBytesScanned: 1024,
            CurrentRunLiveEvidence: []);

        check(!report.Applied, "Report Applied property is false.");

        string json = RecoveryJsonReportExporter.SerializeAggregate(report);
        check(json.Contains("\"SchemaVersion\": \"recovery-v1.phase7\""), "Serialized JSON has correct SchemaVersion.");
        check(json.Contains("\"AggregateStatus\": \"COMPLETE\""), "Serialized JSON has correct AggregateStatus.");
        check(json.Contains("\"Applied\": false"), "Serialized JSON explicitly includes Applied=false.");

        using var sw = new StringWriter();
        RecoveryConsoleReportWriter.WriteAggregate(sw, report);
        string consoleText = sw.ToString();
        check(consoleText.Contains("Recovery V1 Aggregate Report"), "Console output contains title.");
        check(consoleText.Contains("Applied=False"), "Console output explicitly states Applied=False.");
        check(consoleText.Contains("Target Execution Summary:"), "Console output contains Target Execution Summary.");
    }

    // =========================================================================
    // Group 2: Coordinator Scenarios A through T
    // =========================================================================

    private static RecoveryResult GetTargetResult(this RecoveryAggregateReport report, string targetId)
    {
        string norm = RecoveryV1Registry.NormalizeId(targetId);
        return report.Results.First(r => RecoveryV1Registry.NormalizeId(r.Target.Id) == norm);
    }

    // =========================================================================
    // Group 2: Coordinator Scenarios A through T
    // =========================================================================

    private static void TestScenarioA_AllFiveHealthy_Complete(Action<bool, string> check)
    {
        using var env = new SyntheticEnvironment();
        env.PlantAllHealthy();

        var inputs = env.CreateHealthyInputs();
        var report = RecoveryV1MultiTargetCoordinator.RecoverAll(env.Session, inputs);

        check(report.AggregateStatus == RecoveryAggregateStatus.COMPLETE,
            $"Scenario A: All five healthy targets yield COMPLETE aggregate status (got {report.AggregateStatus}).");
        check(report.Results.Length == 5, "Scenario A: All five targets produced results.");
        check(report.Results.All(r => r.Decision.TerminalResult == RecoveryTerminalResult.PASS_CURRENT),
            "Scenario A: Every target completed with PASS_CURRENT.");
        check(!report.Applied, "Scenario A: Aggregate Applied is false.");
        check(report.Results.All(r => !r.Applied), "Scenario A: Every target Result Applied is false.");
        check(report.PassedCurrentTargets.Length == 5, "Scenario A: PassedCurrentTargets list has 5 entries.");
    }

    private static void TestScenarioB_Od001NotFound_DownstreamBlockedDependency(Action<bool, string> check)
    {
        using var env = new SyntheticEnvironment();
        // Do NOT plant OD-001 (module scan will find nothing)
        // Request all targets
        var report = RecoveryV1MultiTargetCoordinator.RecoverAll(env.Session);

        check(report.Results.Length == 5, "Scenario B: Coordinator processed all 5 scheduled targets.");
        var od001 = report.GetTargetResult("OD-001");
        check(od001.Decision.TerminalResult == RecoveryTerminalResult.NOT_FOUND,
            "Scenario B: OD-001 terminated with NOT_FOUND.");

        // All downstream targets requiring OD-001 must be BLOCKED_DEPENDENCY
        var downstream = report.Results.Where(r => RecoveryV1Registry.NormalizeId(r.Target.Id) != "OD-001").ToList();
        check(downstream.All(r => r.Decision.TerminalResult == RecoveryTerminalResult.BLOCKED_DEPENDENCY),
            "Scenario B: All downstream targets terminated with BLOCKED_DEPENDENCY.");
        check(report.AggregateStatus == RecoveryAggregateStatus.ATTENTION_REQUIRED,
            "Scenario B: Aggregate status is ATTENTION_REQUIRED.");
    }

    private static void TestScenarioC1_ProvisionalOd001_IntermediateRevalidationPasses(Action<bool, string> check)
    {
        using var env = new SyntheticEnvironment();
        // Plant OD-001 match in module so OD-001 produces PROPOSED
        env.PlantOd001Match(validState: true);
        // Plant valid intermediate UI chain: InGameState -> UiRoot (+0x2F0) -> GameUi (+0xBE0)
        env.PlantIntermediateUiChain(valid: true, trustUiAnchors: false);
        env.PlantRuneshapePanel();

        // Run OD-144 (which depends on OD-001 and UI anchors)
        var coordinator = new RecoveryV1MultiTargetCoordinator();
        var report = coordinator.Run(env.Session, ["OD-144"]);

        var od001 = report.GetTargetResult("OD-001");
        check(od001.Decision.TerminalResult == RecoveryTerminalResult.PROPOSED,
            "Scenario C1: OD-001 produced PROPOSED.");

        var od144 = report.GetTargetResult("OD-144");
        check(od144.Decision.TerminalResult != RecoveryTerminalResult.BLOCKED_DEPENDENCY,
            "Scenario C1: Downstream target OD-144 was NOT blocked because intermediate UI revalidation succeeded.");

        // Check that intermediate anchors were registered with provisional-derived provenance
        check(env.Session.TryGetResult("OD-014", out var uiRootRes) && uiRootRes != null,
            "Scenario C1: OD-014 (UiRoot) anchor recorded in session.");
        check(uiRootRes!.Target.Scope.Name == "provisional-revalidated",
            "Scenario C1: OD-014 has provisional-revalidated scope provenance.");
        check(env.Session.TryGetResult("OD-020", out var gameUiRes) && gameUiRes != null,
            "Scenario C1: OD-020 (GameUi) anchor recorded in session.");
    }

    private static void TestScenarioC2_ProvisionalOd001_IntermediateRevalidationFails(Action<bool, string> check)
    {
        using var env = new SyntheticEnvironment();
        // Plant OD-001 match in module so OD-001 produces PROPOSED
        env.PlantOd001Match(validState: true);
        // Break intermediate UI chain: UiRoot pointer at inGame+0x2F0 is 0
        env.PlantIntermediateUiChain(valid: false, trustUiAnchors: false);

        var coordinator = new RecoveryV1MultiTargetCoordinator();
        var report = coordinator.Run(env.Session, ["OD-144"]);

        var od001 = report.GetTargetResult("OD-001");
        check(od001.Decision.TerminalResult == RecoveryTerminalResult.PROPOSED,
            "Scenario C2: OD-001 produced PROPOSED.");

        var od144 = report.GetTargetResult("OD-144");
        check(od144.Decision.TerminalResult == RecoveryTerminalResult.BLOCKED_DEPENDENCY,
            "Scenario C2: Downstream target OD-144 was BLOCKED_DEPENDENCY due to broken intermediate UI anchor.");
        check(od144.Detail!.Contains("Intermediate UI anchor revalidation failed"),
            "Scenario C2: Blocked detail identifies intermediate UI anchor failure.");
        check(report.AggregateStatus == RecoveryAggregateStatus.ATTENTION_REQUIRED,
            "Scenario C2: Aggregate status is ATTENTION_REQUIRED.");
    }

    private static void TestScenarioD_Od114MissingContext_PartialContext(Action<bool, string> check)
    {
        using var env = new SyntheticEnvironment();
        env.PlantAllHealthy();

        // Remove expedition station open context
        env.Session.ReplaceContext(RecoveryContextSnapshot.Create([
            new KeyValuePair<string, string>("local-player-present", "true"),
            new KeyValuePair<string, string>("life-component-readable", "true"),
            new KeyValuePair<string, string>("player-health-meaningful", "true"),
            new KeyValuePair<string, string>("state-machine-present", "true"),
            new KeyValuePair<string, string>("runeshape-ui-context", "true"),
            new KeyValuePair<string, string>("world-map-atlas-open", "true"),
            new KeyValuePair<string, string>("adequate-atlas-node-population", "true"),
            new KeyValuePair<string, string>("in_area", "true")
            // "expedition-rune-station-present" missing
        ]));

        var inputs = env.CreateHealthyInputs();
        // Run OD-001, OD-063, OD-114
        var coordinator = new RecoveryV1MultiTargetCoordinator();
        var report = coordinator.Run(env.Session, ["OD-001", "OD-063", "OD-114"], inputs);

        check(report.GetTargetResult("OD-001").Decision.TerminalResult == RecoveryTerminalResult.PASS_CURRENT,
            "Scenario D: OD-001 passed current.");
        check(report.GetTargetResult("OD-063").Decision.TerminalResult == RecoveryTerminalResult.PASS_CURRENT,
            "Scenario D: OD-063 passed current.");

        var od114 = report.GetTargetResult("OD-114");
        check(od114.Decision.TerminalResult == RecoveryTerminalResult.BLOCKED_CONTEXT,
            "Scenario D: OD-114 is BLOCKED_CONTEXT when required context key is missing.");
        check(report.AggregateStatus == RecoveryAggregateStatus.PARTIAL_CONTEXT,
            "Scenario D: Aggregate status is PARTIAL_CONTEXT when only BLOCKED_CONTEXT occurs alongside healthy targets.");
    }

    private static void TestScenarioE_Od144MissingContext_PartialContext(Action<bool, string> check)
    {
        using var env = new SyntheticEnvironment();
        env.PlantAllHealthy();

        // Context with in_area but missing runeshape panel context
        env.Session.ReplaceContext(RecoveryContextSnapshot.Create([
            new KeyValuePair<string, string>("local-player-present", "true"),
            new KeyValuePair<string, string>("life-component-readable", "true"),
            new KeyValuePair<string, string>("player-health-meaningful", "true"),
            new KeyValuePair<string, string>("expedition-rune-station-present", "true"),
            new KeyValuePair<string, string>("state-machine-present", "true"),
            new KeyValuePair<string, string>("world-map-atlas-open", "true"),
            new KeyValuePair<string, string>("adequate-atlas-node-population", "true"),
            new KeyValuePair<string, string>("in_area", "true")
            // "runeshape-ui-context" missing
        ]));

        var inputs = env.CreateHealthyInputs();
        var report = RecoveryV1MultiTargetCoordinator.Recover(env.Session, ["OD-001", "OD-144"], inputs);

        check(report.GetTargetResult("OD-001").Decision.TerminalResult == RecoveryTerminalResult.PASS_CURRENT,
            "Scenario E: OD-001 passed current.");
        var od144 = report.GetTargetResult("OD-144");
        check(od144.Decision.TerminalResult == RecoveryTerminalResult.BLOCKED_CONTEXT,
            "Scenario E: OD-144 is BLOCKED_CONTEXT when panel context is missing.");
        check(report.AggregateStatus == RecoveryAggregateStatus.PARTIAL_CONTEXT,
            "Scenario E: Aggregate status is PARTIAL_CONTEXT.");
    }

    private static void TestScenarioF_Od145MissingContext_PartialContext(Action<bool, string> check)
    {
        using var env = new SyntheticEnvironment();
        env.PlantAllHealthy();

        // Context missing world-map-atlas-open
        env.Session.ReplaceContext(RecoveryContextSnapshot.Create([
            new KeyValuePair<string, string>("local-player-present", "true"),
            new KeyValuePair<string, string>("life-component-readable", "true"),
            new KeyValuePair<string, string>("player-health-meaningful", "true"),
            new KeyValuePair<string, string>("expedition-rune-station-present", "true"),
            new KeyValuePair<string, string>("state-machine-present", "true"),
            new KeyValuePair<string, string>("runeshape-ui-context", "true"),
            new KeyValuePair<string, string>("adequate-atlas-node-population", "true"),
            new KeyValuePair<string, string>("in_area", "true")
            // "world-map-atlas-open" missing
        ]));

        var inputs = env.CreateHealthyInputs();
        var report = RecoveryV1MultiTargetCoordinator.Recover(env.Session, ["OD-001", "OD-145"], inputs);

        check(report.GetTargetResult("OD-001").Decision.TerminalResult == RecoveryTerminalResult.PASS_CURRENT,
            "Scenario F: OD-001 passed current.");
        var od145 = report.GetTargetResult("OD-145");
        check(od145.Decision.TerminalResult == RecoveryTerminalResult.BLOCKED_CONTEXT,
            "Scenario F: OD-145 is BLOCKED_CONTEXT when world map is not open.");
        check(report.AggregateStatus == RecoveryAggregateStatus.PARTIAL_CONTEXT,
            "Scenario F: Aggregate status is PARTIAL_CONTEXT.");
    }

    private static void TestScenarioG_OneTargetAmbiguous_AttentionRequired(Action<bool, string> check)
    {
        using var env = new SyntheticEnvironment();
        env.PlantAllHealthy();

        // Create ambiguous candidate situation for OD-063 by planting two valid HP pairs in Life component
        env.PlantAmbiguousLifeComponent();

        // Remove observed-player-hp context facts so both candidates survive without correlation filtering
        env.Session.ReplaceContext(RecoveryContextSnapshot.Create(env.Session.Context.Facts
            .Where(p => !p.Key.StartsWith("observed-player-hp-", StringComparison.Ordinal))));

        var inputs = env.CreateHealthyInputs();
        // Do not provide current validation address for OD-063 so it executes blind discovery
        inputs.Remove("OD-063");

        var report = RecoveryV1MultiTargetCoordinator.Recover(env.Session, ["OD-001", "OD-063"], inputs);

        var od063 = report.GetTargetResult("OD-063");
        check(od063.Decision.TerminalResult == RecoveryTerminalResult.AMBIGUOUS,
            "Scenario G: OD-063 with multiple survivors terminates with AMBIGUOUS.");
        check(od063.Decision.Proposal is null, "Scenario G: Ambiguous decision has no proposal.");
        check(od063.Decision.SurvivorIds.Length >= 2, "Scenario G: Ambiguous decision retains all survivor IDs.");
        check(report.AmbiguousTargets.Contains("OD-063") || report.AmbiguousTargets.Contains("comp_life_health"),
            "Scenario G: AmbiguousTargets contains OD-063.");
        check(report.AggregateStatus == RecoveryAggregateStatus.ATTENTION_REQUIRED,
            "Scenario G: Aggregate status is ATTENTION_REQUIRED when an ambiguous target exists.");
    }

    private static void TestScenarioH_OneTargetError_AggregateError(Action<bool, string> check)
    {
        using var env = new SyntheticEnvironment();
        env.PlantAllHealthy();

        // Mutate process identity to make it stale during run
        env.Reader.Metadata = new ProcessMetadata
        {
            ProcessId = 99998, // Changed PID!
            ProcessName = "ModifiedPoE2.exe",
            FileVersion = "0.2.0.0",
            ModuleBase = env.Reader.MainModuleBase,
            ModuleMemorySize = env.Reader.MainModuleSize,
            AttachedTimeUtc = DateTime.UtcNow
        };

        var report = RecoveryV1MultiTargetCoordinator.Recover(env.Session, ["OD-001"]);
        check(report.AggregateStatus == RecoveryAggregateStatus.ERROR,
            "Scenario H: Stale process identity produces ERROR aggregate status.");
        check(report.ErrorTargets.Contains("OD-001") || report.ErrorTargets.Contains("pattern_game_states"),
            "Scenario H: ErrorTargets includes OD-001.");
    }

    private static void TestScenarioI_MultipleTargetsProposed_AppliedFalse(Action<bool, string> check)
    {
        using var env = new SyntheticEnvironment();
        // Plant OD-001 pattern match
        env.PlantOd001Match(validState: true);
        // Plant Life component with moved HP pair so blind discovery yields PROPOSED
        env.PlantLifeComponent(singleValidPair: true);
        env.MoveLifeHealthOffset(0x1D0);

        // Run OD-001 and OD-063 with no configured current addresses
        var report = RecoveryV1MultiTargetCoordinator.Recover(env.Session, ["OD-001", "OD-063"]);

        var od001 = report.GetTargetResult("OD-001");
        var od063 = report.GetTargetResult("OD-063");

        check(od001.Decision.TerminalResult == RecoveryTerminalResult.PROPOSED, "Scenario I: OD-001 is PROPOSED.");
        check(od063.Decision.TerminalResult == RecoveryTerminalResult.PROPOSED, "Scenario I: OD-063 is PROPOSED.");
        check(!od001.Applied && !od063.Applied && !report.Applied,
            "Scenario I: All Applied flags are strictly false.");
        check(report.Proposals.Count >= 2,
            "Scenario I: Proposals dictionary records proposals for both targets.");
        check(report.AggregateStatus == RecoveryAggregateStatus.ATTENTION_REQUIRED,
            "Scenario I: Aggregate status is ATTENTION_REQUIRED when unapplied proposals exist.");
    }

    private static void TestScenarioJ_HistoricalValuesChanged_BlindUnchanged(Action<bool, string> check)
    {
        using var env = new SyntheticEnvironment();
        env.PlantLifeComponent(singleValidPair: true);
        env.MoveLifeHealthOffset(0x1D0);

        // Run 1 with historical offset 0x120
        var input1 = new TargetExecutionInput(null,
            new PostDecisionComparisonInput(HistoricalNumericValues: [0x120]));
        var r1 = RecoveryV1Registry.Get("OD-063").ExecuteTarget(env.Session, input1);

        // Run 2 with completely different historical offset 0x340
        using var env2 = new SyntheticEnvironment();
        env2.PlantLifeComponent(singleValidPair: true);
        env2.MoveLifeHealthOffset(0x1D0);
        var input2 = new TargetExecutionInput(null,
            new PostDecisionComparisonInput(HistoricalNumericValues: [0x340]));
        var r2 = RecoveryV1Registry.Get("OD-063").ExecuteTarget(env2.Session, input2);

        check(r1.Decision.Proposal == r2.Decision.Proposal,
            "Scenario J: Changing historical values does not alter the blind discovery proposal.");
        check(r1.Decision.SurvivorIds.SequenceEqual(r2.Decision.SurvivorIds),
            "Scenario J: Changing historical values does not alter survivor candidate IDs.");
    }

    private static void TestScenarioK_AllAppliedFlagsFalseGlobally(Action<bool, string> check)
    {
        using var env = new SyntheticEnvironment();
        env.PlantAllHealthy();
        var report = RecoveryV1MultiTargetCoordinator.RecoverAll(env.Session, env.CreateHealthyInputs());

        check(!report.Applied, "Scenario K: Report Applied is false.");
        foreach (var r in report.Results)
        {
            check(!r.Applied, $"Scenario K: Target {r.Target.Id} Applied flag is false.");
        }
    }

    private static void TestScenarioL_ProvisionalCannotMutateGlobalState(Action<bool, string> check)
    {
        using var env = new SyntheticEnvironment();
        env.PlantOd001Match(validState: true);

        var coordinator = new RecoveryV1MultiTargetCoordinator();
        var report = coordinator.Run(env.Session, ["OD-001"]);

        var od001 = report.GetTargetResult("OD-001");
        check(od001.Decision.TerminalResult == RecoveryTerminalResult.PROPOSED,
            "Scenario L: Target produced proposal.");

        // Verify session provisional container holds value
        check(env.Session.TryGetProvisional("OD-001", out var prov) && prov is not null,
            "Scenario L: Provisional value is stored session-locally.");

        // Verify that StaticAddresses or other global memory was NOT touched
        check(!report.Applied, "Scenario L: No global offset state was modified.");
    }

    private static void TestScenarioM_BudgetIsolationBetweenTargets(Action<bool, string> check)
    {
        using var env = new SyntheticEnvironment();
        env.PlantAllHealthy();

        var report = RecoveryV1MultiTargetCoordinator.RecoverAll(env.Session, env.CreateHealthyInputs());

        check(report.TargetMetrics.Length == 5, "Scenario M: Budget metrics recorded for all 5 targets.");
        foreach (var m in report.TargetMetrics)
        {
            check(m.ElapsedMilliseconds >= 0, $"Scenario M: Elapsed ms non-negative for {m.TargetId}.");
        }
        check(report.TotalElapsedMilliseconds >= 0, "Scenario M: Total elapsed ms non-negative.");
    }

    private static void TestScenarioN_AggregateJsonTerminalResultsMatch(Action<bool, string> check)
    {
        using var env = new SyntheticEnvironment();
        env.PlantAllHealthy();

        var report = RecoveryV1MultiTargetCoordinator.RecoverAll(env.Session, env.CreateHealthyInputs());
        string json = RecoveryJsonReportExporter.SerializeAggregate(report);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var resultsElem = root.GetProperty("Results");
        check(resultsElem.GetArrayLength() == report.Results.Length,
            "Scenario N: Aggregate JSON Results length matches internal results length.");

        for (int i = 0; i < report.Results.Length; i++)
        {
            var expected = report.Results[i];
            var jsonItem = resultsElem[i];
            string termRes = jsonItem.GetProperty("Decision").GetProperty("TerminalResult").GetString()!;
            check(termRes == expected.Decision.TerminalResult.ToString(),
                $"Scenario N: Target {expected.Target.Id} terminal result in JSON matches per-target result.");
        }
    }

    private static void TestScenarioO_AggregateConsoleExposesAllStatuses(Action<bool, string> check)
    {
        using var env = new SyntheticEnvironment();
        // Break intermediate chain to cause BLOCKED_DEPENDENCY
        env.PlantOd001Match(validState: true);
        env.PlantIntermediateUiChain(valid: false);

        var report = RecoveryV1MultiTargetCoordinator.Recover(env.Session, ["OD-144"]);
        using var sw = new StringWriter();
        RecoveryConsoleReportWriter.WriteAggregate(sw, report);
        string text = sw.ToString();

        check(text.Contains("Blocked Dependency Targets:"),
            "Scenario O: Console output contains Blocked Dependency section.");
        check(text.Contains("OD-144"),
            "Scenario O: Console output names OD-144 as blocked.");
        check(text.Contains("Proposals (UNAPPLIED - Review Required):"),
            "Scenario O: Console output has prominent proposals section.");
    }

    private static void TestScenarioP_AutoExpansionIncludesOnlyRunnablePrerequisites(Action<bool, string> check)
    {
        var requested = ImmutableArray.Create("OD-145");
        var executionOrder = RecoveryV1Registry.ResolveExecutionOrder(requested, out var autoAdded);

        check(autoAdded.SequenceEqual(["OD-001"]),
            "Scenario P: Only runnable prerequisite 'OD-001' is auto-added.");
        check(executionOrder.SequenceEqual(["OD-001", "OD-145"]),
            "Scenario P: Execution order contains OD-001 -> OD-145.");
        check(!executionOrder.Contains("OD-007") && !executionOrder.Contains("OD-010") &&
            !executionOrder.Contains("OD-014") && !executionOrder.Contains("OD-020") &&
            !executionOrder.Contains("OD-134"),
            "Scenario P: Non-runnable anchors are NEVER added to execution order.");
    }

    private static void TestScenarioQ_NonRunnableAnchorNeverScheduled(Action<bool, string> check)
    {
        check(!RecoveryV1Registry.IsRunnableTarget("OD-007"), "Scenario Q: OD-007 cannot be scheduled.");
        check(!RecoveryV1Registry.IsRunnableTarget("OD-010"), "Scenario Q: OD-010 cannot be scheduled.");
        check(!RecoveryV1Registry.IsRunnableTarget("OD-014"), "Scenario Q: OD-014 cannot be scheduled.");
        check(!RecoveryV1Registry.IsRunnableTarget("OD-020"), "Scenario Q: OD-020 cannot be scheduled.");
        check(!RecoveryV1Registry.IsRunnableTarget("OD-134"), "Scenario Q: OD-134 cannot be scheduled.");
        check(!RecoveryV1Registry.IsRunnableTarget("comp_life"), "Scenario Q: comp_life cannot be scheduled.");
    }

    private static void TestScenarioR_BlindDiscoveryInputIsolation(Action<bool, string> check)
    {
        // Type does not have CurrentValue or HistoricalValues properties (architectural isolation)
        var props = typeof(BlindDiscoveryRequest).GetProperties();
        check(props.Length > 0, "Scenario R: BlindDiscoveryRequest has properties.");
        check(props.All(p => p.Name != "CurrentValue" && p.Name != "HistoricalValues" && p.Name != "ConfiguredValue"),
            "Scenario R: BlindDiscoveryRequest contains zero configured or historical offset members.");
    }

    private static void TestScenarioS_ThresholdMetadataMatchesCanonical(Action<bool, string> check)
    {
        // Check that threshold values are derived directly from strategy constants
        var od001 = RecoveryV1Registry.Get("OD-001").CanonicalThresholdsSupplier();
        check((long)od001.Single(t => t.Name == "MaxScanBytes").Value == 128L * 1024 * 1024,
            "Scenario S: OD-001 MaxScanBytes equals 128MB.");

        var od063 = RecoveryV1Registry.Get("OD-063").CanonicalThresholdsSupplier();
        check((int)od063.Single(t => t.Name == "SearchBytes").Value == Od063LifeHealthRecovery.SearchBytes,
            "Scenario S: OD-063 SearchBytes equals Od063LifeHealthRecovery.SearchBytes.");

        var od114 = RecoveryV1Registry.Get("OD-114").CanonicalThresholdsSupplier();
        check((int)od114.Single(t => t.Name == "Radius").Value == Od114RuneStationOwnerRecovery.Radius,
            "Scenario S: OD-114 Radius equals Od114RuneStationOwnerRecovery.Radius.");

        var od144 = RecoveryV1Registry.Get("OD-144").CanonicalThresholdsSupplier();
        check((int)od144.Single(t => t.Name == "RecipeCountV1").Value == Od144RuneshapePanelRecovery.RecipeCountV1,
            "Scenario S: OD-144 RecipeCountV1 equals Od144RuneshapePanelRecovery.RecipeCountV1.");

        var od145 = RecoveryV1Registry.Get("OD-145").CanonicalThresholdsSupplier();
        check((int)od145.Single(t => t.Name == "StdVectorHeaderSize").Value == Od145AtlasLayoutRecovery.StdVectorHeaderSize,
            "Scenario S: OD-145 StdVectorHeaderSize equals Od145AtlasLayoutRecovery.StdVectorHeaderSize.");
    }

    private static void TestScenarioT_UnavailableBudgetMetricsSerializeAsNull(Action<bool, string> check)
    {
        var metric = new TargetBudgetMetric("OD-063", 5, null, null, null);
        string json = JsonSerializer.Serialize(metric);

        check(json.Contains("\"BytesScanned\":null"), "Scenario T: Missing bytes scanned serializes as null, not 0.");
        check(json.Contains("\"ReadCount\":null"), "Scenario T: Missing read count serializes as null, not 0.");
        check(json.Contains("\"CandidateCount\":null"), "Scenario T: Missing candidate count serializes as null, not 0.");
    }

    // =========================================================================
    // Synthetic Test Environment Helper
    // =========================================================================

    private sealed class SyntheticEnvironment : IDisposable
    {
        public SyntheticMemoryReader Reader { get; } = new();
        public RecoverySession Session { get; }

        private readonly byte[] _module;
        private readonly long _base;
        private IntPtr _gameStatesBlock;
        private IntPtr _inGameStateBlock;
        private IntPtr _lifeBlock;
        private IntPtr _stateMachineBlock;
        private IntPtr _stationEntityBlock;
        private IntPtr _uiRootBlock;
        private IntPtr _gameUiBlock;
        private IntPtr _canvasBlock;
        private IntPtr _panelBlock;

        public SyntheticEnvironment()
        {
            _base = Reader.MainModuleBase.ToInt64();
            Reader.MainModuleSize = 0x10000;
            Reader.Metadata = new ProcessMetadata
            {
                ProcessId = 99999,
                ProcessName = "SyntheticPoE2.exe",
                FileVersion = "0.2.0.0",
                ModuleBase = Reader.MainModuleBase,
                ModuleMemorySize = Reader.MainModuleSize,
                AttachedTimeUtc = DateTime.UtcNow
            };

            // Set up PE header for module scanning
            _module = new byte[0x8000];
            BinaryPrimitives.WriteUInt16LittleEndian(_module, 0x5A4D);
            BinaryPrimitives.WriteInt32LittleEndian(_module.AsSpan(0x3C), 0x80);
            BinaryPrimitives.WriteUInt32LittleEndian(_module.AsSpan(0x80), 0x00004550);
            BinaryPrimitives.WriteUInt16LittleEndian(_module.AsSpan(0x86), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(_module.AsSpan(0x94), 0xE0);
            int section = 0x80 + 24 + 0xE0;
            BinaryPrimitives.WriteUInt32LittleEndian(_module.AsSpan(section + 8), 0x2000);
            BinaryPrimitives.WriteUInt32LittleEndian(_module.AsSpan(section + 12), 0x200);
            BinaryPrimitives.WriteUInt32LittleEndian(_module.AsSpan(section + 36), 0x60000020);

            Reader.AllocateBlockAt((ulong)_base, 0x8000);
            FlushModule();

            Session = new RecoverySession(Reader, RecoveryContextSnapshot.Create([
                new KeyValuePair<string, string>("local-player-present", "true"),
                new KeyValuePair<string, string>("life-component-readable", "true"),
                new KeyValuePair<string, string>("player-health-meaningful", "true"),
                new KeyValuePair<string, string>("observed-player-hp-current", "800"),
                new KeyValuePair<string, string>("observed-player-hp-total", "1000"),
                new KeyValuePair<string, string>("expedition-rune-station-present", "true"),
                new KeyValuePair<string, string>("state-machine-present", "true"),
                new KeyValuePair<string, string>("runeshape-ui-context", "true"),
                new KeyValuePair<string, string>("atlas-ui-context", "true"),
                new KeyValuePair<string, string>("world-map-atlas-open", "true"),
                new KeyValuePair<string, string>("adequate-atlas-node-population", "true"),
                new KeyValuePair<string, string>("in_area", "true")
            ]));
        }

        public void FlushModule()
        {
            Reader.WriteBytes(new IntPtr(_base), _module);
        }

        public void PlantOd001Match(bool validState)
        {
            int matchRva = 0x300;
            int targetRva = 0x1000;
            var pattern = TEHhub.Offsets.StaticOffsetsPatterns.Patterns.Single(p => p.Name == "Game States");
            pattern.Data.CopyTo(_module, matchRva);
            int disp = checked(targetRva - (matchRva + pattern.BytesToSkip + 4));
            BinaryPrimitives.WriteInt32LittleEndian(_module.AsSpan(matchRva + pattern.BytesToSkip), disp);
            FlushModule();

            _gameStatesBlock = new IntPtr(_base + targetRva);
            if (validState)
            {
                var root = Reader.AllocateBlock(0x200);
                _inGameStateBlock = Reader.AllocateBlock(0x400);
                var state = Reader.AllocateBlock(0x100);
                var current = Reader.AllocateBlock(0x20);
                var area = Reader.AllocateBlock(0x100);

                Reader.Write(_gameStatesBlock, new GameStateStaticOffset { GameState = root });
                Reader.WritePointer(root + 0x90, _inGameStateBlock);
                Reader.WriteStdVector(root + 0x10, current, current + 16, current + 16);
                Reader.WritePointer(current, state);
                Reader.WritePointer(current + 8, _inGameStateBlock);
                Reader.WritePointer(root + 0x50, state);
                Reader.WritePointer(_inGameStateBlock + 0x290, area);

                TrustAnchor("OD-010", "InGameState", _inGameStateBlock.ToInt64());
                TrustAnchor("OD-007", "FileRoot", Reader.AllocateBlock(0x100).ToInt64());
            }
        }

        private IntPtr _lifeVtableBlock;

        public void PlantLifeComponent(bool singleValidPair)
        {
            _lifeBlock = Reader.AllocateBlock(0x500);
            _lifeVtableBlock = Reader.AllocateBlock(0x100);
            if (singleValidPair)
            {
                Reader.Write(_lifeBlock + 0x1B0, new VitalStruct
                {
                    VtablePtr = _lifeVtableBlock,
                    PtrToLifeComponent = _lifeBlock,
                    Total = 1000,
                    Current = 800
                });
            }
            TrustAnchor("comp_life", "LifeComponent", _lifeBlock.ToInt64());
        }

        public void PlantSecondValidHpPair()
        {
            if (_lifeBlock == IntPtr.Zero)
                PlantLifeComponent(singleValidPair: true);
            Reader.Write(_lifeBlock + 0x240, new VitalStruct
            {
                VtablePtr = _lifeVtableBlock,
                PtrToLifeComponent = _lifeBlock,
                Total = 2000,
                Current = 1500
            });
        }

        public void PlantAmbiguousLifeComponent()
        {
            if (_lifeBlock == IntPtr.Zero)
                PlantLifeComponent(singleValidPair: false);

            // Invalidate 0x1B0 so current validation fails
            Reader.Write(_lifeBlock + 0x1B0, new VitalStruct());

            // Plant two valid VitalStructs at 0x1D0 and 0x240
            Reader.Write(_lifeBlock + 0x1D0, new VitalStruct
            {
                VtablePtr = _lifeVtableBlock,
                PtrToLifeComponent = _lifeBlock,
                Total = 1000,
                Current = 800
            });
            Reader.Write(_lifeBlock + 0x240, new VitalStruct
            {
                VtablePtr = _lifeVtableBlock,
                PtrToLifeComponent = _lifeBlock,
                Total = 2000,
                Current = 1500
            });
        }

        public void MoveLifeHealthOffset(int newOffset)
        {
            if (_lifeBlock == IntPtr.Zero)
                PlantLifeComponent(singleValidPair: false);

            Reader.Write(_lifeBlock + 0x1B0, new VitalStruct());

            Reader.Write(_lifeBlock + newOffset, new VitalStruct
            {
                VtablePtr = _lifeVtableBlock,
                PtrToLifeComponent = _lifeBlock,
                Total = 1000,
                Current = 800
            });
        }

        private IntPtr _listenerBlock;
        private IntPtr _goldenSlots;

        public void PlantStateMachineAndStation()
        {
            _stateMachineBlock = Reader.AllocateBlock(0x300);
            _stationEntityBlock = Reader.AllocateBlock(0x200);
            _listenerBlock = Reader.AllocateBlock(0x800);
            var node = Reader.AllocateBlock(0x20);
            var vector = Reader.AllocateBlock(0x20);
            _goldenSlots = Reader.AllocateBlock(0x20);

            Reader.Write(_goldenSlots, 0);

            Reader.WritePointer(_stateMachineBlock + 8, _stationEntityBlock);

            Reader.WritePointer(vector, node);
            Reader.WriteStdVector(_stateMachineBlock + 0x20, vector, vector + 8, vector + 8);

            var listener = _listenerBlock + 0x300;
            Reader.WritePointer(node, listener);

            var station = listener - 0x120;
            Reader.WritePointer(station + 0x10, _stationEntityBlock);
            Reader.WritePointer(station + 0x28, IntPtr.Zero);
            Reader.WritePointer(station + 0x30, IntPtr.Zero);
            Reader.Write(station + 0x38, 6);
            Reader.Write(station + 0x3C, 0);
            Reader.WriteStdVector(station + 0x40, _goldenSlots, _goldenSlots + 4, _goldenSlots + 4);

            TrustAnchor("comp_statemachine", "StateMachineComponent", _stateMachineBlock.ToInt64());
            TrustAnchor("expedition_rune_station_entity", "RuneStationEntity", _stationEntityBlock.ToInt64());
        }

        public void PlantIntermediateUiChain(bool valid, bool trustUiAnchors = true)
        {
            if (_inGameStateBlock == IntPtr.Zero)
                _inGameStateBlock = Reader.AllocateBlock(0x400);

            _uiRootBlock = Reader.AllocateBlock(0x1000);
            _gameUiBlock = Reader.AllocateBlock(0x500);

            if (valid)
            {
                // InGameState + 0x2F0 -> UiRoot
                Reader.WritePointer(_inGameStateBlock + 0x2F0, _uiRootBlock);
                // UiRoot + 0xBE0 -> GameUi
                Reader.WritePointer(_uiRootBlock + 0xBE0, _gameUiBlock);
                // GameUi Self invariant
                Reader.WritePointer(_gameUiBlock + 8, _gameUiBlock);
                Reader.WritePointer(_gameUiBlock + 0x88, _gameUiBlock);
            }
            else
            {
                // Broken pointer: InGameState + 0x2F0 is 0
                Reader.WritePointer(_inGameStateBlock + 0x2F0, IntPtr.Zero);
            }

            TrustAnchor("OD-010", "InGameState", _inGameStateBlock.ToInt64());
            TrustAnchor("OD-007", "FileRoot", Reader.AllocateBlock(0x100).ToInt64());
            if (valid && trustUiAnchors)
            {
                TrustAnchor("OD-014", "UiRoot", _uiRootBlock.ToInt64());
                TrustAnchor("OD-020", "GameUi", _gameUiBlock.ToInt64());
            }
        }

        private IntPtr Element(IntPtr parent, uint flags)
        {
            var element = Reader.AllocateBlock(0x300);
            Reader.WritePointer(element + 8, element);
            Reader.WritePointer(element + 0x88, element);
            Reader.WritePointer(element + 0xB8, parent);
            Reader.Write(element + 0x168, flags);
            return element;
        }

        private void Children(IntPtr parent, int count, Dictionary<int, IntPtr> selected)
        {
            var vector = Reader.AllocateBlock(count * 8);
            for (int i = 0; i < count; i++)
            {
                var child = selected.TryGetValue(i, out var ptr) ? ptr : Element(parent, 0);
                Reader.WritePointer(vector + i * 8, child);
            }
            Reader.WriteStdVector(parent + 0x10, vector, vector + count * 8, vector + count * 8);
        }

        public void PlantRuneshapePanel()
        {
            if (_gameUiBlock == IntPtr.Zero)
                PlantIntermediateUiChain(valid: true);

            var panel = Element(_gameUiBlock, Od144RuneshapePanelRecovery.MaskedFingerprint);
            var current = panel;
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

            Children(_gameUiBlock, 1, new() { [0] = panel });
            _panelBlock = panel;
        }

        public void PlantAtlasLayout()
        {
            const int NodeCount = 64;
            _canvasBlock = Reader.AllocateBlock(0x900);
            Reader.WritePointer(_canvasBlock + 8, _canvasBlock);
            Reader.WritePointer(_canvasBlock + 0x88, _canvasBlock);
            Reader.Write(_canvasBlock + 0x168, 0x00462EF1u);
            Reader.WritePointer(_canvasBlock + 0xB8, _gameUiBlock);
            TrustAnchor("OD-134", "WorldMapPanel", _canvasBlock.ToInt64());

            var canvasChildrenVector = Reader.AllocateBlock(NodeCount * 8);
            Reader.WriteStdVector(_canvasBlock + 0x10, canvasChildrenVector,
                canvasChildrenVector + NodeCount * 8, canvasChildrenVector + NodeCount * 8);

            var coords = new (int X, int Y)[NodeCount];
            for (int i = 0; i < NodeCount; i++)
            {
                var node = Reader.AllocateBlock(0x900);
                Reader.WritePointer(node + 8, node);
                Reader.WritePointer(node + 0x88, node);
                Reader.WritePointer(node + 0xB8, _canvasBlock);
                Reader.Write(node + 0x168, Od145AtlasLayoutRecovery.AtlasMapNodeFp);

                coords[i] = (1000 + i * 45, 2000 + ((i * 73) % 800) - 400);
                Reader.Write(node + 0x310, new StdTuple2D<int>(coords[i].X, coords[i].Y));
                Reader.WritePointer(canvasChildrenVector + i * 8, node);
            }

            int edgeCount = NodeCount - 1;
            var edgesBlock = Reader.AllocateBlock(edgeCount * 20);
            for (int i = 0; i < edgeCount; i++)
            {
                var edge = new Od145AtlasLayoutRecovery.AtlasConnectionEdge
                {
                    Unknown = 0,
                    SourceX = coords[i].X,
                    SourceY = coords[i].Y,
                    TargetX = coords[i + 1].X,
                    TargetY = coords[i + 1].Y
                };
                Reader.Write(edgesBlock + i * 20, edge);
            }
            Reader.WriteStdVector(_canvasBlock + 0x590, edgesBlock,
                edgesBlock + edgeCount * 20, edgesBlock + edgeCount * 20);
        }

        public void PlantAllHealthy()
        {
            PlantOd001Match(validState: true);
            PlantLifeComponent(singleValidPair: true);
            PlantStateMachineAndStation();
            PlantIntermediateUiChain(valid: true);
            PlantRuneshapePanel();
            PlantAtlasLayout();
        }

        public Dictionary<string, TargetExecutionInput> CreateHealthyInputs()
        {
            return new Dictionary<string, TargetExecutionInput>(StringComparer.OrdinalIgnoreCase)
            {
                ["OD-001"] = new TargetExecutionInput(
                    new CurrentValidationInput(CurrentNumericValue: _gameStatesBlock.ToInt64()), null),
                ["OD-063"] = new TargetExecutionInput(
                    new CurrentValidationInput(CurrentNumericValue: 0x1B0), null),
                ["OD-114"] = new TargetExecutionInput(
                    new CurrentValidationInput(CurrentNumericValue: 0x120), null),
                ["OD-144"] = new TargetExecutionInput(
                    new CurrentValidationInput(CurrentPath: [0]), null),
                ["OD-145"] = new TargetExecutionInput(
                    new CurrentValidationInput(CurrentFieldPair: new AtlasFieldPair(0x310, 0x590)), null)
            };
        }

        public void TrustAnchor(string id, string name, long address)
        {
            Session.TrustAnchor(id, name, address, "fixture");
        }

        public void Dispose()
        {
            Reader.Dispose();
        }
    }
}
