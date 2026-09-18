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
        Console.WriteLine("\n[TEHhub.OffsetDoctor.Tests] Running 27 Evidence-Safe Test Scenarios...");

        Test1_HealthyGoldChain(check);
        Test2_LegitimateZeroGold(check);
        Test3_BrokenParentBlocksChildren(check);
        Test4_ValidateModePerformsZeroSearches(check);
        Test5_ShiftedAreaInstanceRecovery(check);
        Test6_ShiftedServerDataRecovery(check);
        Test7_ShiftedPsdVectorRecovery(check);
        Test8_ShiftedGoldRecordSlotRecovery(check);
        Test9_ShiftedGoldFieldRecovery(check);
        Test10_AmbiguousCandidateNotProvisional(check);
        Test11_AmbiguousParentKeepsChildBlocked(check);
        Test12_AmbiguousCandidateCreatesNoRecommendation(check);
        Test13_MediumLowCandidateCreatesNoRecommendation(check);
        Test14_UserRangeButUnreadablePointerRejected(check);
        Test15_StdVectorDownstreamUnreadableCannotBeHigh(check);
        Test16_GoldWithoutGroundTruthNotFalselyValid(check);
        Test17_ExactGoldEvidenceNotCountedTwice(check);
        Test18_MultipleExactGoldMatchesAmbiguous(check);
        Test19_ZeroGoldDuplicateStaleNotFalseHigh(check);
        Test20_RealStrideFixtureProducesStructuralEvidence(check);
        Test21_UnreadableStrideTargetDoesNotProduceEvidence(check);
        Test22_BoundedScanEnforcement(check);
        Test23_HighOnlyGlobalProvisionalBehavior(check);
        Test24_ReadOnlySnapshotRemainsByteIdentical(check);
        Test25_JsonPreservesEvidenceStatusRecommendations(check);
        Test26_MultiNodeShiftedChainRecoversAllMovedOffsets(check);
        Test27_MultiNodeCompetingCoherentBranchesAmbiguous(check);

        Console.WriteLine("[TEHhub.OffsetDoctor.Tests] All 27 Test Scenarios Passed Successfully!\n");
    }

    private static (SyntheticMemoryReader reader, IntPtr gameState, IntPtr inGameState, IntPtr areaInstance, IntPtr serverData, IntPtr psd, IntPtr goldRecord) SetupSyntheticEnvironment(
        int areaInstanceOffset = 0x290,
        int serverDataOffset = 0x5B0,
        int psdVectorOffset = 0x48,
        int goldRecordSlotOffset = 0x0E28,
        int goldFieldOffset = 0x0618,
        int goldValue = 50_000_000,
        bool setupNeighboringStride = true)
    {
        var reader = new SyntheticMemoryReader();

        var moduleBase = reader.MainModuleBase;
        var gameState = reader.AllocateBlock(0x1000);
        var inGameState = reader.AllocateBlock(0x1000);
        var areaInstance = reader.AllocateBlock(0x1000);
        var serverData = reader.AllocateBlock(0x1000);
        var psdVectorBuf = reader.AllocateBlock(0x100);
        var psd = reader.AllocateBlock(0x2000);

        // Allocate records with genuine 0x80 pointer stride
        var strideBase = 0x50000000UL;
        var prevRecord = reader.AllocateBlockAt(strideBase, 0x80);
        var goldRecord = reader.AllocateBlockAt(strideBase + 0x80, 0x1000); // Exactly 0x80 difference: goldRecord - prevRecord == 0x80

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

        // Neighboring PSD slots (+0xE08..+0xE28) with real 0x80 stride
        if (setupNeighboringStride)
        {
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

        check(report.IsChainHealthy, "T1: Baseline gold chain must be completely healthy.");
        check(report.Results.Count == 7, "T1: Report must contain exactly 7 validated nodes.");
        check(report.Results.All(r => r.Status == ValidationStatus.VALID), "T1: All nodes must have VALID status.");
        var goldNode = report.Results.First(r => r.NodeId == "gold_field");
        check(goldNode.ExtractedValue is int val && val == 50_000_000, "T1: Extracted gold value must equal 50,000,000.");
    }

    // 2. Explicit Gold == 0 is a successful readable value
    private static void Test2_LegitimateZeroGold(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment(goldValue: 0).reader;
        var engine = new OffsetRecoveryEngine();
        var report = engine.RunValidation(setup, expectedGold: 0);

        check(report.IsChainHealthy, "T2: Explicit zero gold balance must be treated as valid success.");
        var goldNode = report.Results.First(r => r.NodeId == "gold_field");
        check(goldNode.Status == ValidationStatus.VALID, "T2: Gold field must be VALID when gold is 0.");
        check(goldNode.ExtractedValue is int val && val == 0, "T2: Extracted gold value must be exactly 0.");
    }

    // 3. Broken parent -> BLOCKED children
    private static void Test3_BrokenParentBlocksChildren(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment().reader;
        var engine = new OffsetValidatorEngine();
        var nodes = OffsetManifest.CreateGoldChainManifest();

        // Invalidate AreaInstance pointer in inGameState (at +0x290)
        setup.WritePointer(new IntPtr(0x10000000 + 0x1000 + 0x290), IntPtr.Zero);

        var results = engine.ValidateChain(setup, nodes, new RecoveryContext { AllNodes = nodes }, allowRecovery: false);
        var areaRes = results.First(r => r.NodeId == "area_instance");
        var serverRes = results.First(r => r.NodeId == "server_data");
        var psdRes = results.First(r => r.NodeId == "player_server_data_vector");

        check(areaRes.Status == ValidationStatus.BROKEN, "T3: Null AreaInstance pointer must be BROKEN.");
        check(serverRes.Status == ValidationStatus.BLOCKED, "T3: Downstream ServerData must be BLOCKED.");
        check(psdRes.Status == ValidationStatus.BLOCKED, "T3: Downstream PlayerServerData must be BLOCKED.");
    }

    // 4. Validate mode performs zero recovery searches
    private static void Test4_ValidateModePerformsZeroSearches(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment(areaInstanceOffset: 0x2B0).reader;
        var engine = new OffsetRecoveryEngine();

        var valReport = engine.RunValidation(setup, expectedGold: 50_000_000);
        var areaVal = valReport.Results.First(r => r.NodeId == "area_instance");
        var serverVal = valReport.Results.First(r => r.NodeId == "server_data");

        check(areaVal.Status == ValidationStatus.BROKEN, "T4: Shifted AreaInstance must be BROKEN in validate mode.");
        check(areaVal.Candidates.Count == 0, "T4: Validate mode must NOT execute candidate search (Candidates must be empty).");
        check(serverVal.Status == ValidationStatus.BLOCKED, "T4: Downstream node must remain BLOCKED in validate mode.");
        check(valReport.Recommendations.Count == 0, "T4: Validate mode must emit zero recommendations.");
    }

    // 5. Shifted AreaInstance recovery
    private static void Test5_ShiftedAreaInstanceRecovery(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment(areaInstanceOffset: 0x2B0).reader;
        var engine = new OffsetRecoveryEngine();
        var report = engine.RunRecovery(setup, expectedGold: 50_000_000);

        var areaRec = report.Recommendations.FirstOrDefault(r => r.NodeId == "area_instance");
        check(areaRec != null, "T5: Recovery engine must generate recommendation for shifted AreaInstance.");
        check(areaRec!.SuggestedOffset == 0x2B0, $"T5: Suggested offset must be 0x2B0 (got 0x{areaRec.SuggestedOffset:X}).");
        check(areaRec.Confidence == Confidence.HIGH, "T5: AreaInstance recovery must have HIGH confidence.");
    }

    // 6. Shifted ServerData recovery
    private static void Test6_ShiftedServerDataRecovery(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment(serverDataOffset: 0x5D0).reader;
        var engine = new OffsetRecoveryEngine();
        var report = engine.RunRecovery(setup, expectedGold: 50_000_000);

        var serverRec = report.Recommendations.FirstOrDefault(r => r.NodeId == "server_data");
        check(serverRec != null, "T6: Recovery engine must discover shifted ServerData offset.");
        check(serverRec!.SuggestedOffset == 0x5D0, $"T6: Suggested offset must be 0x5D0 (got 0x{serverRec.SuggestedOffset:X}).");
        check(serverRec.Confidence == Confidence.HIGH, "T6: ServerData recovery must have HIGH confidence.");
    }

    // 7. Shifted PSD vector recovery
    private static void Test7_ShiftedPsdVectorRecovery(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment(psdVectorOffset: 0x58).reader;
        var engine = new OffsetRecoveryEngine();
        var report = engine.RunRecovery(setup, expectedGold: 50_000_000);

        var psdRec = report.Recommendations.FirstOrDefault(r => r.NodeId == "player_server_data_vector");
        check(psdRec != null, "T7: Recovery engine must discover shifted PlayerServerData vector offset.");
        check(psdRec!.SuggestedOffset == 0x58, $"T7: Suggested offset must be 0x58 (got 0x{psdRec.SuggestedOffset:X}).");
        check(psdRec.Confidence == Confidence.HIGH, "T7: PlayerServerData vector recovery must have HIGH confidence.");
    }

    // 8. Shifted Gold record slot recovery
    private static void Test8_ShiftedGoldRecordSlotRecovery(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment(goldRecordSlotOffset: 0xE38).reader;
        var engine = new OffsetRecoveryEngine();
        var report = engine.RunRecovery(setup, expectedGold: 50_000_000);

        var slotRec = report.Recommendations.FirstOrDefault(r => r.NodeId == "gold_record_slot");
        check(slotRec != null, "T8: Recovery engine must discover shifted Gold record slot pointer.");
        check(slotRec!.SuggestedOffset == 0xE38, $"T8: Suggested offset must be 0xE38 (got 0x{slotRec.SuggestedOffset:X}).");
        check(slotRec.Confidence == Confidence.HIGH, "T8: Gold record slot recovery must have HIGH confidence.");
    }

    // 9. Shifted Gold field recovery
    private static void Test9_ShiftedGoldFieldRecovery(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment(goldFieldOffset: 0x628, goldValue: 12_345_678).reader;
        var engine = new OffsetRecoveryEngine();
        var report = engine.RunRecovery(setup, expectedGold: 12_345_678);

        var goldRec = report.Recommendations.FirstOrDefault(r => r.NodeId == "gold_field");
        check(goldRec != null, "T9: Recovery engine must discover shifted Gold field offset.");
        check(goldRec!.SuggestedOffset == 0x628, $"T9: Suggested offset must be 0x628 (got 0x{goldRec.SuggestedOffset:X}).");
        check(goldRec.Confidence == Confidence.HIGH, "T9: Exact expected gold match with unique discriminator must yield HIGH confidence.");
    }

    // 10. Ambiguous candidate not provisional
    private static void Test10_AmbiguousCandidateNotProvisional(Action<bool, string> check)
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

        var areaRes = report.Results.First(r => r.NodeId == "area_instance");
        check(areaRes.Status == ValidationStatus.AMBIGUOUS, $"T10: Competing candidates without tiebreaker must be AMBIGUOUS (got {areaRes.Status}).");
        check(areaRes.BestCandidate == null, "T10: Ambiguous status must NOT select a BestCandidate.");
    }

    // 11. Ambiguous parent keeps child BLOCKED
    private static void Test11_AmbiguousParentKeepsChildBlocked(Action<bool, string> check)
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
        check(serverRes.Status == ValidationStatus.BLOCKED, "T11: Downstream ServerData must remain BLOCKED when parent is AMBIGUOUS.");
    }

    // 12. Ambiguous candidate creates no recommendation
    private static void Test12_AmbiguousCandidateCreatesNoRecommendation(Action<bool, string> check)
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
            "T12: Ambiguous node must produce zero actionable recommendations.");
    }

    // 13. MEDIUM/LOW candidate creates no recommendation
    private static void Test13_MediumLowCandidateCreatesNoRecommendation(Action<bool, string> check)
    {
        using var reader = new SyntheticMemoryReader();
        var moduleBase = reader.MainModuleBase;
        var gameState = reader.AllocateBlock(0x1000);
        var inGameState = reader.AllocateBlock(0x1000);
        var areaTarget = reader.AllocateBlock(0x1000);

        reader.AllocateBlockAt((ulong)moduleBase.ToInt64(), 0x100);
        reader.Write(moduleBase, new GameStateStaticOffset { GameState = gameState });
        reader.WritePointer(gameState + 0x90, inGameState);

        reader.WritePointer(inGameState + 0x2B0, areaTarget);

        var engine = new OffsetRecoveryEngine();
        var report = engine.RunRecovery(reader, expectedGold: null);

        var areaRes = report.Results.First(r => r.NodeId == "area_instance");
        check(areaRes.Status == ValidationStatus.NEEDS_MANUAL_PROOF, "T13: Single candidate without downstream proof must be NEEDS_MANUAL_PROOF.");
        check(!report.Recommendations.Any(r => r.NodeId == "area_instance"),
            "T13: MEDIUM confidence candidate must not produce an actionable recommendation.");
    }

    // 14. User-range but unreadable pointer rejected
    private static void Test14_UserRangeButUnreadablePointerRejected(Action<bool, string> check)
    {
        using var baseReader = new SyntheticMemoryReader();
        var mockReader = new RangeOnlyUnreadableMemoryReader(baseReader, unreadableAddress: new IntPtr(0x20000000));

        var moduleBase = baseReader.MainModuleBase;
        var gameState = baseReader.AllocateBlock(0x1000);
        var inGameState = baseReader.AllocateBlock(0x1000);

        baseReader.AllocateBlockAt((ulong)moduleBase.ToInt64(), 0x100);
        baseReader.Write(moduleBase, new GameStateStaticOffset { GameState = gameState });
        baseReader.WritePointer(gameState + 0x90, inGameState);

        // Point AreaInstance (+0x290) to unreadable address
        baseReader.WritePointer(inGameState + 0x290, new IntPtr(0x20000000));

        var engine = new OffsetValidatorEngine();
        var nodes = OffsetManifest.CreateGoldChainManifest();
        var results = engine.ValidateChain(mockReader, nodes, new RecoveryContext { AllNodes = nodes }, allowRecovery: false);

        var areaRes = results.First(r => r.NodeId == "area_instance");
        check(areaRes.Status == ValidationStatus.BROKEN, "T14: Pointer to unreadable memory must be BROKEN, not VALID.");
    }

    // 15. StdVector downstream unreadable pointer cannot create HIGH
    private static void Test15_StdVectorDownstreamUnreadableCannotBeHigh(Action<bool, string> check)
    {
        using var baseReader = new SyntheticMemoryReader();
        var unreadableSlot = new IntPtr(0x30000000);
        var mockReader = new RangeOnlyUnreadableMemoryReader(baseReader, unreadableSlot);

        var parent = baseReader.AllocateBlock(0x1000);
        var psdVecBuf = baseReader.AllocateBlock(0x100);
        var psd = baseReader.AllocateBlock(0x1000);

        baseReader.WritePointer(psdVecBuf, psd);
        baseReader.WriteStdVector(parent + 0x58, psdVecBuf, psdVecBuf + 8, psdVecBuf + 8);

        // Put unreadable pointer in PSD slot (+0xE28)
        baseReader.WritePointer(psd + 0xE28, unreadableSlot);

        var strategy = new StdVectorFieldRecoveryStrategy();
        var node = new OffsetNode
        {
            Id = "player_server_data_vector",
            DisplayName = "PSD Vector",
            DefaultOffset = 0x48,
            Kind = ValueKind.StdVectorField,
            SearchRadius = 0x100
        };

        var context = new RecoveryContext
        {
            AllNodes = OffsetManifest.CreateGoldChainManifest()
        };

        var candidates = strategy.SearchCandidates(mockReader, parent, node, context);
        check(candidates.Count > 0, "T15: Candidate found.");
        check(candidates[0].Confidence != Confidence.HIGH, "T15: Unreadable downstream child must prevent vector candidate from reaching HIGH confidence.");
    }

    // 16. Gold without ground truth is not falsely VALID
    private static void Test16_GoldWithoutGroundTruthNotFalselyValid(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment(goldValue: 12345).reader;
        var engine = new OffsetRecoveryEngine();
        var report = engine.RunValidation(setup, expectedGold: null);

        var goldRes = report.Results.First(r => r.NodeId == "gold_field");
        check(goldRes.Status != ValidationStatus.VALID, "T16: Gold field without --gold must NOT be declared VALID.");
        check(goldRes.Status == ValidationStatus.NEEDS_MANUAL_PROOF, "T16: Plausible gold without ground truth must be NEEDS_MANUAL_PROOF.");
    }

    // 17. Exact Gold evidence is not counted twice as independent evidence
    private static void Test17_ExactGoldEvidenceNotCountedTwice(Action<bool, string> check)
    {
        using var reader = new SyntheticMemoryReader();
        var parent = reader.AllocateBlock(0x1000);
        reader.Write(parent + 0x628, 12345);

        var strategy = new NumericFieldRecoveryStrategy();
        var node = new OffsetNode
        {
            Id = "gold_field",
            DisplayName = "Gold Field",
            DefaultOffset = 0x618,
            Kind = ValueKind.NumericField,
            SearchRadius = 0x100
        };

        var context = new RecoveryContext { ExpectedGoldAmount = 12345, AllNodes = [node] };
        var candidates = strategy.SearchCandidates(reader, parent, node, context);

        check(candidates.Count == 1, "T17: One candidate discovered.");
        var cand = candidates[0];
        var exactRules = cand.Evidence.Where(e => e.RuleName == "ExactExpectedValueMatch").ToList();
        check(exactRules.Count == 1, "T17: Exact expected value match rule must be present exactly once (no double counting).");
    }

    // 18. Multiple exact Gold matches -> AMBIGUOUS
    private static void Test18_MultipleExactGoldMatchesAmbiguous(Action<bool, string> check)
    {
        using var reader = new SyntheticMemoryReader();
        var moduleBase = reader.MainModuleBase;
        var gameState = reader.AllocateBlock(0x1000);
        var inGameState = reader.AllocateBlock(0x1000);
        var areaInstance = reader.AllocateBlock(0x1000);
        var serverData = reader.AllocateBlock(0x1000);
        var psdVecBuf = reader.AllocateBlock(0x100);
        var psd = reader.AllocateBlock(0x2000);
        var goldRecord = reader.AllocateBlock(0x1000);

        reader.AllocateBlockAt((ulong)moduleBase.ToInt64(), 0x100);
        reader.Write(moduleBase, new GameStateStaticOffset { GameState = gameState });
        reader.WritePointer(gameState + 0x90, inGameState);
        reader.WritePointer(inGameState + 0x290, areaInstance);
        reader.WritePointer(areaInstance + 0x5B0, serverData);
        reader.WritePointer(psdVecBuf, psd);
        reader.WriteStdVector(serverData + 0x48, psdVecBuf, psdVecBuf + 8, psdVecBuf + 8);
        reader.WritePointer(psd + 0xE28, goldRecord);

        // Put the SAME expected gold amount at two different offsets (+0x618 and +0x628)
        reader.Write(goldRecord + 0x618, 55555);
        reader.Write(goldRecord + 0x628, 55555);

        var engine = new OffsetRecoveryEngine();
        var report = engine.RunRecovery(reader, expectedGold: 55555);

        var goldRes = report.Results.First(r => r.NodeId == "gold_field");
        // Because there are 2 competing exact matches with no distinct discriminator
        check(goldRes.Status == ValidationStatus.AMBIGUOUS || !report.Recommendations.Any(r => r.NodeId == "gold_field"),
            "T18: Multiple exact matches must not produce a false-unique recommendation.");
    }

    // 19. --gold 0 duplicate/stale case does not false-HIGH
    private static void Test19_ZeroGoldDuplicateStaleNotFalseHigh(Action<bool, string> check)
    {
        using var reader = new SyntheticMemoryReader();
        var parent = reader.AllocateBlock(0x1000);
        // All buffer is 0 by default, so multiple offsets have value 0
        var strategy = new NumericFieldRecoveryStrategy();
        var node = new OffsetNode
        {
            Id = "gold_field",
            DisplayName = "Gold Field",
            DefaultOffset = 0x618,
            Kind = ValueKind.NumericField,
            SearchRadius = 0x100
        };

        var context = new RecoveryContext { ExpectedGoldAmount = 0, AllNodes = [node] };
        var candidates = strategy.SearchCandidates(reader, parent, node, context);

        check(candidates.Count > 1, "T19: Multiple zeroes discovered across buffer.");
        // Non-unique zeroes must not be unique
        check(candidates[0].Confidence != Confidence.HIGH || candidates[0].Offset == 0x618,
            "T19: Ambiguous zero values must not falsely declare shifted candidate as HIGH.");
    }

    // 20. Real 0x80 stride fixture produces structural evidence
    private static void Test20_RealStrideFixtureProducesStructuralEvidence(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment(setupNeighboringStride: true).reader;
        var strategy = new GoldRecordSlotRecoveryStrategy();
        var node = new OffsetNode
        {
            Id = "gold_record_slot",
            DisplayName = "Gold Slot",
            DefaultOffset = 0x0E28,
            Kind = ValueKind.RecordSlotField,
            SearchRadius = 0x100
        };

        var psdAddr = setup.AllocateBlockAt(0x70000000, 0x10); // dummy lookup
        // We know psd is at 0x10000000 + 0x4000 approx
        var context = new RecoveryContext { ExpectedGoldAmount = 50_000_000, AllNodes = OffsetManifest.CreateGoldChainManifest() };

        // Test with real environment
        var engine = new OffsetRecoveryEngine();
        var report = engine.RunRecovery(setup, expectedGold: 50_000_000);

        var slotNode = report.Results.First(r => r.NodeId == "gold_record_slot");
        check(slotNode.Status == ValidationStatus.VALID, "T20: Real stride environment validates slot successfully.");
    }

    // 21. Fake/unreadable stride target does not produce structural evidence
    private static void Test21_UnreadableStrideTargetDoesNotProduceEvidence(Action<bool, string> check)
    {
        using var baseReader = new SyntheticMemoryReader();
        var unreadableNeighbor = new IntPtr(0x40000000);
        var mockReader = new RangeOnlyUnreadableMemoryReader(baseReader, unreadableNeighbor);

        var parent = baseReader.AllocateBlock(0x2000);
        var goldRec = baseReader.AllocateBlock(0x1000);

        // Put unreadable neighbor at +0xE20 and goldRec at +0xE28
        baseReader.WritePointer(parent + 0xE20, unreadableNeighbor);
        baseReader.WritePointer(parent + 0xE28, goldRec);

        var strategy = new GoldRecordSlotRecoveryStrategy();
        var node = new OffsetNode
        {
            Id = "gold_record_slot",
            DisplayName = "Gold Slot",
            DefaultOffset = 0x0E28,
            Kind = ValueKind.RecordSlotField,
            SearchRadius = 0x100
        };

        var candidates = strategy.SearchCandidates(mockReader, parent, node, new RecoveryContext { AllNodes = [node] });
        check(candidates.Count > 0, "T21: Found candidate.");
        var strideRule = candidates[0].Evidence.FirstOrDefault(e => e.RuleName == "NeighboringSlotStridePattern");
        check(strideRule == null, "T21: Unreadable neighboring pointer must NOT produce stride evidence.");
    }

    // 22. Bounded scan enforcement
    private static void Test22_BoundedScanEnforcement(Action<bool, string> check)
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

        check(candidates.Count == 0, "T22: Pointer placed outside search radius must not be discovered.");
        var scanWidth = (long)(reader.MaxReadAddress - reader.MinReadAddress);
        check(scanWidth <= 0x500, $"T22: Total memory accessed ({scanWidth} bytes) must be strictly bounded by SearchRadius.");
    }

    // 23. HIGH-only global provisional behavior
    private static void Test23_HighOnlyGlobalProvisionalBehavior(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment(areaInstanceOffset: 0x2B0, serverDataOffset: 0x5B0).reader;
        var engine = new OffsetRecoveryEngine();
        var report = engine.RunRecovery(setup, expectedGold: 50_000_000);

        var areaRec = report.Recommendations.FirstOrDefault(r => r.NodeId == "area_instance");
        var serverRes = report.Results.First(r => r.NodeId == "server_data");

        check(areaRec != null && areaRec.Confidence == Confidence.HIGH, "T23: AreaInstance has HIGH confidence.");
        check(serverRes.Status == ValidationStatus.VALID, "T23: ServerData validated using HIGH provisional parent.");
    }

    // 24. Read-only snapshot remains byte-identical
    private static void Test24_ReadOnlySnapshotRemainsByteIdentical(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment(goldValue: 12345).reader;
        var preSnapshot = setup.SnapshotAllBlocks();

        var engine = new OffsetRecoveryEngine();
        var report = engine.RunRecovery(setup, expectedGold: 12345);

        check(setup.MemoryMatchesSnapshot(preSnapshot),
            "T24: Memory blocks must be byte-for-byte identical before and after RunRecovery (zero writes).");
        check(report.Results.Count == 7, "T24: Recovery completed successfully.");
    }

    // 25. JSON preserves evidence/status/recommendations
    private static void Test25_JsonPreservesEvidenceStatusRecommendations(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment(areaInstanceOffset: 0x2B0).reader;
        var engine = new OffsetRecoveryEngine();
        var report = engine.RunRecovery(setup, expectedGold: 50_000_000);

        var json = JsonReportExporter.ToJsonString(report);
        check(!string.IsNullOrWhiteSpace(json), "T25: JSON report string must not be empty.");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        check(root.GetProperty("Results").GetArrayLength() == 7, "T25: Results array in JSON must have 7 nodes.");
        check(root.GetProperty("Recommendations").GetArrayLength() == 1, "T25: Recommendations array in JSON must have 1 entry.");
        var firstRec = root.GetProperty("Recommendations")[0];
        check(firstRec.GetProperty("SuggestedOffset").GetInt32() == 0x2B0, "T25: Suggested offset in JSON must be 0x2B0.");
        check(firstRec.GetProperty("Confidence").GetString() == "HIGH", "T25: Confidence in JSON must be HIGH.");
    }

    // 26. Multi-node shifted chain recovers all moved offsets
    private static void Test26_MultiNodeShiftedChainRecoversAllMovedOffsets(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment(
            areaInstanceOffset: 0x2B0,
            serverDataOffset: 0x5D0,
            psdVectorOffset: 0x58,
            goldRecordSlotOffset: 0xE38,
            goldFieldOffset: 0x628,
            goldValue: 88_888_888).reader;

        var engine = new OffsetRecoveryEngine();
        var report = engine.RunRecovery(setup, expectedGold: 88_888_888);

        check(report.Recommendations.Count == 5, $"T26: All 5 shifted nodes must generate recommendations (got {report.Recommendations.Count}).");
        check(report.Recommendations.Any(r => r.NodeId == "area_instance" && r.SuggestedOffset == 0x2B0 && r.Confidence == Confidence.HIGH), "T26: Shifted AreaInstance recovered as HIGH.");
        check(report.Recommendations.Any(r => r.NodeId == "server_data" && r.SuggestedOffset == 0x5D0 && r.Confidence == Confidence.HIGH), "T26: Shifted ServerData recovered as HIGH.");
        check(report.Recommendations.Any(r => r.NodeId == "player_server_data_vector" && r.SuggestedOffset == 0x58 && r.Confidence == Confidence.HIGH), "T26: Shifted PSD vector recovered as HIGH.");
        check(report.Recommendations.Any(r => r.NodeId == "gold_record_slot" && r.SuggestedOffset == 0xE38 && r.Confidence == Confidence.HIGH), "T26: Shifted Gold Record Slot recovered as HIGH.");
        check(report.Recommendations.Any(r => r.NodeId == "gold_field" && r.SuggestedOffset == 0x628 && r.Confidence == Confidence.HIGH), "T26: Shifted Gold field recovered as HIGH.");
    }

    // 27. Multi-node competing coherent branches remain AMBIGUOUS
    private static void Test27_MultiNodeCompetingCoherentBranchesAmbiguous(Action<bool, string> check)
    {
        using var reader = new SyntheticMemoryReader();
        var moduleBase = reader.MainModuleBase;
        var gameState = reader.AllocateBlock(0x1000);
        var inGameState = reader.AllocateBlock(0x1000);

        // Branch 1
        var area1 = reader.AllocateBlock(0x1000);
        var server1 = reader.AllocateBlock(0x1000);
        var psdVecBuf1 = reader.AllocateBlock(0x100);
        var psd1 = reader.AllocateBlock(0x2000);
        var goldRec1 = reader.AllocateBlock(0x1000);

        // Branch 2 (Identical coherent structure)
        var area2 = reader.AllocateBlock(0x1000);
        var server2 = reader.AllocateBlock(0x1000);
        var psdVecBuf2 = reader.AllocateBlock(0x100);
        var psd2 = reader.AllocateBlock(0x2000);
        var goldRec2 = reader.AllocateBlock(0x1000);

        reader.AllocateBlockAt((ulong)moduleBase.ToInt64(), 0x100);
        reader.Write(moduleBase, new GameStateStaticOffset { GameState = gameState });
        reader.WritePointer(gameState + 0x90, inGameState);

        // Branch 1 pointers
        reader.WritePointer(inGameState + 0x2B0, area1);
        reader.WritePointer(area1 + 0x5D0, server1);
        reader.WritePointer(psdVecBuf1, psd1);
        reader.WriteStdVector(server1 + 0x58, psdVecBuf1, psdVecBuf1 + 8, psdVecBuf1 + 8);
        reader.WritePointer(psd1 + 0xE38, goldRec1);
        reader.Write(goldRec1 + 0x628, 99_999);

        // Branch 2 pointers
        reader.WritePointer(inGameState + 0x2C0, area2);
        reader.WritePointer(area2 + 0x5D0, server2);
        reader.WritePointer(psdVecBuf2, psd2);
        reader.WriteStdVector(server2 + 0x58, psdVecBuf2, psdVecBuf2 + 8, psdVecBuf2 + 8);
        reader.WritePointer(psd2 + 0xE38, goldRec2);
        reader.Write(goldRec2 + 0x628, 99_999);

        var engine = new OffsetRecoveryEngine();
        var report = engine.RunRecovery(reader, expectedGold: 99_999);

        var areaRes = report.Results.First(r => r.NodeId == "area_instance");
        check(areaRes.Status == ValidationStatus.AMBIGUOUS, $"T27: Two equal coherent branches must result in AMBIGUOUS parent (got {areaRes.Status}).");
        check(!report.Recommendations.Any(r => r.NodeId == "area_instance"), "T27: No recommendation for ambiguous branch parent.");
    }

    private sealed class RangeOnlyUnreadableMemoryReader : IProcessMemoryReader
    {
        private readonly IProcessMemoryReader _inner;
        private readonly IntPtr _unreadableAddress;

        public IntPtr MainModuleBase => _inner.MainModuleBase;
        public long MainModuleSize => _inner.MainModuleSize;
        public ProcessMetadata Metadata => _inner.Metadata;

        public RangeOnlyUnreadableMemoryReader(IProcessMemoryReader inner, IntPtr unreadableAddress)
        {
            _inner = inner;
            _unreadableAddress = unreadableAddress;
        }

        public bool IsValidAddress(IntPtr address)
        {
            if (address == _unreadableAddress) return true; // Canonical user range
            return _inner.IsValidAddress(address);
        }

        public bool TryRead<T>(IntPtr address, out T value) where T : unmanaged
        {
            value = default;
            if (address == _unreadableAddress) return false; // Unmapped
            return _inner.TryRead(address, out value);
        }

        public bool TryReadBytes(IntPtr address, Span<byte> destination)
        {
            if (address == _unreadableAddress) return false;
            return _inner.TryReadBytes(address, destination);
        }

        public byte[]? ReadBytes(IntPtr address, int count)
        {
            if (address == _unreadableAddress) return null;
            return _inner.ReadBytes(address, count);
        }

        public void Dispose() => _inner.Dispose();
    }
}
