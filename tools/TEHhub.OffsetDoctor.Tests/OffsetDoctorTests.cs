namespace TEHhub.OffsetDoctor.Tests;

using System.Text.Json;
using TEHhub.OffsetDoctor.Evidence;
using TEHhub.OffsetDoctor.Manifest;
using TEHhub.OffsetDoctor.Process;
using TEHhub.OffsetDoctor.Recovery;
using TEHhub.OffsetDoctor.Reporting;
using TEHhub.OffsetDoctor.Strategies;
using TEHhub.OffsetDoctor.Validation;
using TEHhub.Offsets.Objects;

public static class OffsetDoctorTests
{
    public static void RunAll(Action<bool, string> check)
    {
        Console.WriteLine("\n[TEHhub.OffsetDoctor.Tests] Running 16 Evidence-Safe Test Scenarios...");

        Test1_HealthyGoldChain(check);
        Test2_LegitimateZeroGold(check);
        Test3_NullParentBlocksDownstream(check);
        Test4_ShiftedOffsetsRecoverInRecoverMode(check);
        Test5_ValidateModeNeverSearches(check);
        Test6_AmbiguousCandidatesNeverAppliedProvisionally(check);
        Test7_AmbiguousParentKeepsChildBlocked(check);
        Test8_AmbiguousResultProducesNoRecommendation(check);
        Test9_MediumLowCandidateProducesNoRecommendation(check);
        Test10_UnreadableUserRangePointerRejected(check);
        Test11_MovedGoldWithoutExpectedNotValid(check);
        Test12_ExactGoldMatchProducesHighConfidence(check);
        Test13_RecoveryScansStayBounded(check);
        Test14_ProvisionalDownstreamUsesHighCandidatesOnly(check);
        Test15_RecoveryPerformsNoMemoryWrites(check);
        Test16_JsonPreservesStatusAndEvidence(check);

        Console.WriteLine("[TEHhub.OffsetDoctor.Tests] All 16 Test Scenarios Passed Successfully!\n");
    }

    private static (SyntheticMemoryReader reader, IntPtr gameState, IntPtr inGameState, IntPtr areaInstance, IntPtr serverData, IntPtr psd, IntPtr goldRecord) SetupSyntheticEnvironment(
        int areaInstanceOffset = 0x290,
        int serverDataOffset = 0x5B0,
        int psdVectorOffset = 0x48,
        int goldRecordSlotOffset = 0x0E28,
        int goldFieldOffset = 0x0618,
        int goldValue = 50_000_000,
        bool setupNeighboringPsdSlots = true)
    {
        var reader = new SyntheticMemoryReader();

        var moduleBase = reader.MainModuleBase;
        var gameState = reader.AllocateBlock(0x1000);
        var inGameState = reader.AllocateBlock(0x1000);
        var areaInstance = reader.AllocateBlock(0x1000);
        var serverData = reader.AllocateBlock(0x1000);
        var psdVectorBuf = reader.AllocateBlock(0x100);
        var psd = reader.AllocateBlock(0x2000);
        var goldRecord = reader.AllocateBlock(0x1000);

        // Root GameState
        reader.AllocateBlockAt((ulong)moduleBase.ToInt64(), 0x100);
        reader.Write(moduleBase, new GameStateStaticOffset { GameState = gameState });

        // InGameState: gameState + 0x90 -> inGameState
        reader.WritePointer(gameState + 0x90, inGameState);

        // AreaInstance: inGameState + areaInstanceOffset -> areaInstance
        reader.WritePointer(inGameState + areaInstanceOffset, areaInstance);

        // ServerData: areaInstance + serverDataOffset -> serverData
        reader.WritePointer(areaInstance + serverDataOffset, serverData);

        // PlayerServerData vector: serverData + psdVectorOffset -> [psdVectorBuf, psdVectorBuf + 8, psdVectorBuf + 8]
        reader.WritePointer(psdVectorBuf, psd);
        reader.WriteStdVector(serverData + psdVectorOffset, psdVectorBuf, psdVectorBuf + 8, psdVectorBuf + 8);

        // Neighboring PSD slots (+0xE08, +0xE10, +0xE18, +0xE20) with 0x80 stride if requested
        if (setupNeighboringPsdSlots)
        {
            var prevRecord = reader.AllocateBlock(0x1000);
            reader.WritePointer(psd + goldRecordSlotOffset - 8, prevRecord);
        }

        // Gold record slot: psd + goldRecordSlotOffset -> goldRecord
        reader.WritePointer(psd + goldRecordSlotOffset, goldRecord);

        // Gold amount: goldRecord + goldFieldOffset -> goldValue
        reader.Write(goldRecord + goldFieldOffset, goldValue);

        return (reader, gameState, inGameState, areaInstance, serverData, psd, goldRecord);
    }

    // 1. Healthy current chain
    private static void Test1_HealthyGoldChain(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment().reader;
        var engine = new OffsetRecoveryEngine();
        var report = engine.RunValidation(setup, expectedGold: 50_000_000);

        check(report.IsChainHealthy, "Scenario 1: Baseline gold chain must be completely healthy.");
        check(report.Results.Count == 7, "Scenario 1: Report must contain exactly 7 validated nodes.");
        check(report.Results.All(r => r.Status == ValidationStatus.VALID), "Scenario 1: All nodes must have VALID status.");
        var goldNode = report.Results.First(r => r.NodeId == "gold_field");
        check(goldNode.ExtractedValue is int val && val == 50_000_000, "Scenario 1: Extracted gold value must equal 50,000,000.");
    }

    // 2. Explicit Gold == 0
    private static void Test2_LegitimateZeroGold(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment(goldValue: 0).reader;
        var engine = new OffsetRecoveryEngine();
        var report = engine.RunValidation(setup, expectedGold: 0);

        check(report.IsChainHealthy, "Scenario 2: Zero gold balance must be treated as valid success, not failure.");
        var goldNode = report.Results.First(r => r.NodeId == "gold_field");
        check(goldNode.Status == ValidationStatus.VALID, "Scenario 2: Gold field must be VALID when gold is 0.");
        check(goldNode.ExtractedValue is int val && val == 0, "Scenario 2: Extracted gold value must be exactly 0.");
    }

    // 3. Broken parent -> downstream BLOCKED
    private static void Test3_NullParentBlocksDownstream(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment().reader;
        var engine = new OffsetValidatorEngine();
        var nodes = OffsetManifest.CreateGoldChainManifest();

        // Clear AreaInstance pointer in inGameState (at +0x290)
        setup.WritePointer(new IntPtr(0x10000000 + 0x1000 + 0x290), IntPtr.Zero);

        var results = engine.ValidateChain(setup, nodes, new RecoveryContext { AllNodes = nodes }, allowRecovery: false);
        var areaRes = results.First(r => r.NodeId == "area_instance");
        var serverRes = results.First(r => r.NodeId == "server_data");
        var psdRes = results.First(r => r.NodeId == "player_server_data_vector");

        check(areaRes.Status == ValidationStatus.BROKEN, "Scenario 3: Null AreaInstance pointer must be BROKEN.");
        check(serverRes.Status == ValidationStatus.BLOCKED, "Scenario 3: Downstream ServerData must be BLOCKED (not broken).");
        check(psdRes.Status == ValidationStatus.BLOCKED, "Scenario 3: Downstream PlayerServerData must be BLOCKED.");
    }

    // 4. Shifted offsets recover in recover mode
    private static void Test4_ShiftedOffsetsRecoverInRecoverMode(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment(areaInstanceOffset: 0x2B0).reader;
        var engine = new OffsetRecoveryEngine();
        var report = engine.RunRecovery(setup, expectedGold: 50_000_000);

        var areaRec = report.Recommendations.FirstOrDefault(r => r.NodeId == "area_instance");
        check(areaRec != null, "Scenario 4: Recovery engine must generate recommendation for shifted AreaInstance.");
        check(areaRec!.SuggestedOffset == 0x2B0, $"Scenario 4: Suggested offset must be 0x2B0 (got 0x{areaRec.SuggestedOffset:X}).");
        check(areaRec.Confidence == Confidence.HIGH, "Scenario 4: AreaInstance recovery must have HIGH confidence via downstream validation.");
    }

    // 5. Validate mode never searches
    private static void Test5_ValidateModeNeverSearches(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment(areaInstanceOffset: 0x2B0).reader;
        var engine = new OffsetRecoveryEngine();

        // 1. RunValidation must NOT search candidates and must NOT recover
        var valReport = engine.RunValidation(setup, expectedGold: 50_000_000);
        var areaVal = valReport.Results.First(r => r.NodeId == "area_instance");
        var serverVal = valReport.Results.First(r => r.NodeId == "server_data");

        check(areaVal.Status == ValidationStatus.BROKEN, "Scenario 5: Shifted AreaInstance must be BROKEN in validate mode.");
        check(areaVal.Candidates.Count == 0, "Scenario 5: Validate mode must NOT execute candidate search (Candidates must be empty).");
        check(serverVal.Status == ValidationStatus.BLOCKED, "Scenario 5: Downstream node must remain BLOCKED in validate mode.");
        check(valReport.Recommendations.Count == 0, "Scenario 5: Validate mode must emit zero recommendations.");

        // 2. RunRecovery on same layout recovers successfully
        var recReport = engine.RunRecovery(setup, expectedGold: 50_000_000);
        var areaRec = recReport.Recommendations.FirstOrDefault(r => r.NodeId == "area_instance");
        check(areaRec != null && areaRec.SuggestedOffset == 0x2B0, "Scenario 5: RunRecovery on same layout finds and recommends +0x2B0.");
    }

    // 6. Ambiguous candidates are never applied provisionally
    private static void Test6_AmbiguousCandidatesNeverAppliedProvisionally(Action<bool, string> check)
    {
        using var reader = new SyntheticMemoryReader();
        var moduleBase = reader.MainModuleBase;
        var gameState = reader.AllocateBlock(0x1000);
        var inGameState = reader.AllocateBlock(0x1000);
        var areaTarget1 = reader.AllocateBlock(0x1000);
        var areaTarget2 = reader.AllocateBlock(0x1000);

        reader.AllocateBlockAt((ulong)moduleBase.ToInt64(), 0x100);
        reader.Write(moduleBase, new GameStateStaticOffset { GameState = gameState });
        reader.WritePointer(gameState + 0x90, inGameState);

        // Place two equal plausible pointers with NO downstream distinguishing data
        reader.WritePointer(inGameState + 0x210, areaTarget1);
        reader.WritePointer(inGameState + 0x230, areaTarget2);

        var engine = new OffsetRecoveryEngine();
        var report = engine.RunRecovery(reader, expectedGold: null);

        var areaRes = report.Results.First(r => r.NodeId == "area_instance");
        check(areaRes.Status == ValidationStatus.AMBIGUOUS, $"Scenario 6: Tied candidates must result in AMBIGUOUS status (got {areaRes.Status}).");
        check(areaRes.BestCandidate == null, "Scenario 6: Ambiguous status must NOT select a BestCandidate.");
    }

    // 7. Ambiguous parent keeps child BLOCKED
    private static void Test7_AmbiguousParentKeepsChildBlocked(Action<bool, string> check)
    {
        using var reader = new SyntheticMemoryReader();
        var moduleBase = reader.MainModuleBase;
        var gameState = reader.AllocateBlock(0x1000);
        var inGameState = reader.AllocateBlock(0x1000);
        var areaTarget1 = reader.AllocateBlock(0x1000);
        var areaTarget2 = reader.AllocateBlock(0x1000);

        reader.AllocateBlockAt((ulong)moduleBase.ToInt64(), 0x100);
        reader.Write(moduleBase, new GameStateStaticOffset { GameState = gameState });
        reader.WritePointer(gameState + 0x90, inGameState);

        reader.WritePointer(inGameState + 0x210, areaTarget1);
        reader.WritePointer(inGameState + 0x230, areaTarget2);

        var engine = new OffsetRecoveryEngine();
        var report = engine.RunRecovery(reader, expectedGold: null);

        var serverRes = report.Results.First(r => r.NodeId == "server_data");
        check(serverRes.Status == ValidationStatus.BLOCKED, "Scenario 7: Downstream ServerData must remain BLOCKED when parent is AMBIGUOUS.");
    }

    // 8. Ambiguous result produces no actionable recommendation
    private static void Test8_AmbiguousResultProducesNoRecommendation(Action<bool, string> check)
    {
        using var reader = new SyntheticMemoryReader();
        var moduleBase = reader.MainModuleBase;
        var gameState = reader.AllocateBlock(0x1000);
        var inGameState = reader.AllocateBlock(0x1000);
        var areaTarget1 = reader.AllocateBlock(0x1000);
        var areaTarget2 = reader.AllocateBlock(0x1000);

        reader.AllocateBlockAt((ulong)moduleBase.ToInt64(), 0x100);
        reader.Write(moduleBase, new GameStateStaticOffset { GameState = gameState });
        reader.WritePointer(gameState + 0x90, inGameState);

        reader.WritePointer(inGameState + 0x210, areaTarget1);
        reader.WritePointer(inGameState + 0x230, areaTarget2);

        var engine = new OffsetRecoveryEngine();
        var report = engine.RunRecovery(reader, expectedGold: null);

        check(!report.Recommendations.Any(r => r.NodeId == "area_instance"),
            "Scenario 8: Ambiguous node must produce zero actionable recommendations.");
    }

    // 9. MEDIUM/LOW candidate produces no actionable recommendation
    private static void Test9_MediumLowCandidateProducesNoRecommendation(Action<bool, string> check)
    {
        using var reader = new SyntheticMemoryReader();
        var moduleBase = reader.MainModuleBase;
        var gameState = reader.AllocateBlock(0x1000);
        var inGameState = reader.AllocateBlock(0x1000);
        var areaTarget = reader.AllocateBlock(0x1000); // no downstream child in target

        reader.AllocateBlockAt((ulong)moduleBase.ToInt64(), 0x100);
        reader.Write(moduleBase, new GameStateStaticOffset { GameState = gameState });
        reader.WritePointer(gameState + 0x90, inGameState);

        // Single pointer at +0x2B0, but with NO downstream child -> Score 50, Confidence MEDIUM
        reader.WritePointer(inGameState + 0x2B0, areaTarget);

        var engine = new OffsetRecoveryEngine();
        var report = engine.RunRecovery(reader, expectedGold: null);

        var areaRes = report.Results.First(r => r.NodeId == "area_instance");
        check(areaRes.Status == ValidationStatus.NEEDS_MANUAL_PROOF, "Scenario 9: Single candidate with medium confidence must be NEEDS_MANUAL_PROOF.");
        check(!report.Recommendations.Any(r => r.NodeId == "area_instance"),
            "Scenario 9: NEEDS_MANUAL_PROOF / MEDIUM confidence must not produce an actionable recommendation.");
    }

    // 10. User-range-but-unreadable pointer is rejected
    private static void Test10_UnreadableUserRangePointerRejected(Action<bool, string> check)
    {
        using var reader = new SyntheticMemoryReader();
        var moduleBase = reader.MainModuleBase;
        var gameState = reader.AllocateBlock(0x1000);
        var inGameState = reader.AllocateBlock(0x1000);

        reader.AllocateBlockAt((ulong)moduleBase.ToInt64(), 0x100);
        reader.Write(moduleBase, new GameStateStaticOffset { GameState = gameState });
        reader.WritePointer(gameState + 0x90, inGameState);

        // Point AreaInstance (+0x290) to an unallocated address in user space (e.g. 0x30000000)
        var unmappedAddr = new IntPtr(0x30000000);
        reader.WritePointer(inGameState + 0x290, unmappedAddr);

        var engine = new OffsetValidatorEngine();
        var nodes = OffsetManifest.CreateGoldChainManifest();
        var results = engine.ValidateChain(reader, nodes, new RecoveryContext { AllNodes = nodes }, allowRecovery: false);

        var areaRes = results.First(r => r.NodeId == "area_instance");
        check(areaRes.Status == ValidationStatus.BROKEN, "Scenario 10: Pointer to unreadable/unmapped memory must be BROKEN, not VALID.");
    }

    // 11. Moved Gold field + stale plausible old value without --gold is NOT VALID
    private static void Test11_MovedGoldWithoutExpectedNotValid(Action<bool, string> check)
    {
        // Moved gold to +0x628 with 999_999, stale +0x618 contains 0
        using var setup = SetupSyntheticEnvironment(goldFieldOffset: 0x628, goldValue: 999_999).reader;
        var engine = new OffsetRecoveryEngine();

        // Run WITHOUT expected gold
        var report = engine.RunRecovery(setup, expectedGold: null);
        var goldRes = report.Results.First(r => r.NodeId == "gold_field");

        check(goldRes.Status != ValidationStatus.VALID, "Scenario 11: Unverified numeric value must NOT be marked VALID without --gold.");
        check(goldRes.Status == ValidationStatus.NEEDS_MANUAL_PROOF || goldRes.Status == ValidationStatus.AMBIGUOUS,
            $"Scenario 11: Plausible gold without --gold must be NEEDS_MANUAL_PROOF or AMBIGUOUS (got {goldRes.Status}).");
        check(!report.Recommendations.Any(r => r.NodeId == "gold_field"),
            "Scenario 11: Must NOT produce a HIGH-confidence automatic recommendation without expected gold match.");
    }

    // 12. Exact --gold match can make moved Gold field HIGH when evidence requirements are met
    private static void Test12_ExactGoldMatchProducesHighConfidence(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment(goldFieldOffset: 0x628, goldValue: 77_777_777).reader;
        var engine = new OffsetRecoveryEngine();

        // Run WITH exact expected gold
        var report = engine.RunRecovery(setup, expectedGold: 77_777_777);
        var goldRec = report.Recommendations.FirstOrDefault(r => r.NodeId == "gold_field");

        check(goldRec != null, "Scenario 12: Exact gold match must produce recommendation.");
        check(goldRec!.SuggestedOffset == 0x628, $"Scenario 12: Recommended offset must be 0x628 (got 0x{goldRec.SuggestedOffset:X}).");
        check(goldRec.Confidence == Confidence.HIGH, "Scenario 12: Exact gold match must have HIGH confidence.");
    }

    // 13. Recovery scans stay bounded
    private static void Test13_RecoveryScansStayBounded(Action<bool, string> check)
    {
        using var reader = new SyntheticMemoryReader();
        var parent = reader.AllocateBlock(0x2000);
        var target = reader.AllocateBlock(0x100);

        // Place pointer far outside search radius
        reader.WritePointer(parent + 0x1000, target);

        var strategy = new PointerFieldRecoveryStrategy();
        var node = new OffsetNode
        {
            Id = "area_instance",
            DisplayName = "AreaInstance",
            DefaultOffset = 0x290,
            Kind = ValueKind.PointerField,
            SearchRadius = 0x200
        };

        reader.ResetMetrics();
        var candidates = strategy.SearchCandidates(reader, parent, node, new RecoveryContext { AllNodes = [node] });

        check(candidates.Count == 0, "Scenario 13: Pointer placed outside search radius must not be discovered.");
        var scanWidth = (long)(reader.MaxReadAddress - reader.MinReadAddress);
        check(scanWidth <= 0x500, $"Scenario 13: Total memory accessed ({scanWidth} bytes) must be strictly bounded by SearchRadius.");
    }

    // 14. Provisional downstream validation uses HIGH candidates only
    private static void Test14_ProvisionalDownstreamUsesHighCandidatesOnly(Action<bool, string> check)
    {
        // 1. High candidate (AreaInstance +0x2B0 with valid downstream ServerData at +0x5B0)
        using var setupHigh = SetupSyntheticEnvironment(areaInstanceOffset: 0x2B0, serverDataOffset: 0x5B0).reader;
        var engine = new OffsetRecoveryEngine();
        var reportHigh = engine.RunRecovery(setupHigh, expectedGold: 50_000_000);

        var areaHighRec = reportHigh.Recommendations.FirstOrDefault(r => r.NodeId == "area_instance");
        var serverHigh = reportHigh.Results.First(r => r.NodeId == "server_data");

        check(areaHighRec != null && areaHighRec.Confidence == Confidence.HIGH,
            "Scenario 14: AreaInstance with valid downstream proof must have HIGH confidence.");
        check(serverHigh.Status == ValidationStatus.VALID,
            "Scenario 14: Downstream ServerData must be VALID when parent has HIGH candidate.");

        // 2. Weak candidate (AreaInstance +0x2B0 with NO downstream child -> MEDIUM confidence)
        using var readerWeak = new SyntheticMemoryReader();
        var moduleBase = readerWeak.MainModuleBase;
        var gameState = readerWeak.AllocateBlock(0x1000);
        var inGameState = readerWeak.AllocateBlock(0x1000);
        var emptyArea = readerWeak.AllocateBlock(0x1000); // no server data inside

        readerWeak.AllocateBlockAt((ulong)moduleBase.ToInt64(), 0x100);
        readerWeak.Write(moduleBase, new GameStateStaticOffset { GameState = gameState });
        readerWeak.WritePointer(gameState + 0x90, inGameState);
        readerWeak.WritePointer(inGameState + 0x2B0, emptyArea);

        var reportWeak = engine.RunRecovery(readerWeak, expectedGold: null);
        var areaWeak = reportWeak.Results.First(r => r.NodeId == "area_instance");
        var serverWeak = reportWeak.Results.First(r => r.NodeId == "server_data");

        check(areaWeak.Status == ValidationStatus.NEEDS_MANUAL_PROOF,
            "Scenario 14: AreaInstance without downstream proof must remain NEEDS_MANUAL_PROOF.");
        check(serverWeak.Status == ValidationStatus.BLOCKED,
            "Scenario 14: Downstream ServerData must remain BLOCKED when parent candidate is only MEDIUM.");
    }

    // 15. Recovery performs no memory writes
    private static void Test15_RecoveryPerformsNoMemoryWrites(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment(goldValue: 12345).reader;
        var preSnapshot = setup.SnapshotAllBlocks();

        var engine = new OffsetRecoveryEngine();
        var report = engine.RunRecovery(setup, expectedGold: 12345);

        check(setup.MemoryMatchesSnapshot(preSnapshot),
            "Scenario 15: Memory blocks must be byte-for-byte identical before and after RunRecovery (zero writes).");
        check(report.Results.Count == 7, "Scenario 15: Recovery completed successfully.");
    }

    // 16. JSON preserves status/evidence/recommendations correctly
    private static void Test16_JsonPreservesStatusAndEvidence(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment(areaInstanceOffset: 0x2B0).reader;
        var engine = new OffsetRecoveryEngine();
        var report = engine.RunRecovery(setup, expectedGold: 50_000_000);

        var json = JsonReportExporter.ToJsonString(report);
        check(!string.IsNullOrWhiteSpace(json), "Scenario 16: JSON report string must not be empty.");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        check(root.GetProperty("Results").GetArrayLength() == 7, "Scenario 16: Results array in JSON must have 7 nodes.");
        check(root.GetProperty("Recommendations").GetArrayLength() == 1, "Scenario 16: Recommendations array in JSON must have 1 entry.");
        var firstRec = root.GetProperty("Recommendations")[0];
        check(firstRec.GetProperty("SuggestedOffset").GetInt32() == 0x2B0, "Scenario 16: Suggested offset in JSON must be 0x2B0.");
        check(firstRec.GetProperty("Confidence").GetString() == "HIGH", "Scenario 16: Confidence in JSON must be HIGH.");
    }
}
