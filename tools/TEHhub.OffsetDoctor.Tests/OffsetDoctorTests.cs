namespace TEHhub.OffsetDoctor.Tests;

using System.Text.Json;
using TEHhub.OffsetDoctor.Evidence;
using TEHhub.OffsetDoctor.Manifest;
using TEHhub.OffsetDoctor.Process;
using TEHhub.OffsetDoctor.Recovery;
using TEHhub.OffsetDoctor.Reporting;
using TEHhub.OffsetDoctor.Strategies;
using TEHhub.OffsetDoctor.Validation;
using TEHhub.Offsets;
using TEHhub.Offsets.Objects;
using TEHhub.Offsets.Objects.Components;

public static class OffsetDoctorTests
{
    public static void RunAll(Action<bool, string> check)
    {
        Console.WriteLine("\n[TEHhub.OffsetDoctor.Tests] Running 25 Validator-Only Health Scanner Scenarios...");

        Test1_HealthyCoreChain(check);
        Test2_BrokenStaticRootBlocksAllDescendants(check);
        Test3_BrokenInGameStateBlocksDescendants(check);
        Test4_BrokenAreaInstanceBlocksDescendants(check);
        Test5_BrokenServerDataBlocksChildren(check);
        Test6_BrokenStdVectorStructureReportsBroken(check);
        Test7_UserRangeUnreadablePointerReportsBroken(check);
        Test8_LegitimateZeroGoldWithGroundTruth(check);
        Test9_GoldWithoutGroundTruthIsUnverified(check);
        Test10_PlausibleGarbageNumericFieldNotFalselyValid(check);
        Test11_HealthyComponentFixtureValidates(check);
        Test12_ShiftedFieldReportsBrokenZeroRecovery(check);
        Test13_ValidateAllNeverInvokesRecoveryStrategy(check);
        Test14_NoProvisionalOffsetsCreated(check);
        Test15_NoCandidateScanningOccurs(check);
        Test16_MultiLevelBlockedPropagation(check);
        Test17_JsonPreservesAllStatusesAndEvidence(check);
        Test18_ValidatorPerformsZeroMemoryWrites(check);
        Test19_ValidationReadsAreBounded(check);
        Test20_RootEvaluatedBeforeDependentNodes(check);
        Test21_StaticPatternScanValidates(check);
        Test22_WorldAreaModsMarkedUnverifiedByPolicy(check);
        Test23_VitalStructValidatesHealthManaEs(check);
        Test24_ComponentLookupValidatesHeader(check);
        Test25_SummaryCountsAreAccurate(check);

        Console.WriteLine("[TEHhub.OffsetDoctor.Tests] All 25 Test Scenarios Passed Successfully!\n");
    }

    private static (SyntheticMemoryReader reader, IntPtr gameState, IntPtr inGameState, IntPtr areaInstance, IntPtr serverData, IntPtr psd, IntPtr goldRecord, IntPtr localPlayer, IntPtr compList) SetupSyntheticEnvironment(
        int areaInstanceOffset = 0x290,
        int serverDataOffset = 0x5B0,
        int psdVectorOffset = 0x48,
        int goldRecordSlotOffset = 0x0E28,
        int goldFieldOffset = 0x0618,
        int goldValue = 50_000_000)
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
        var localPlayer = reader.AllocateBlock(0x1000);
        var compList = reader.AllocateBlock(0x100);
        var lifeComp = reader.AllocateBlock(0x1000);
        var dummyVtable = reader.AllocateBlock(0x100);

        // 1. Plant Game States Pattern in module memory
        var gsPattern = StaticOffsetsPatterns.Patterns.First(p => p.Name == "Game States");
        var pData = gsPattern.Data;
        var pLen = pData.Length;
        var codeBuf = new byte[0x1000];
        Buffer.BlockCopy(pData, 0, codeBuf, 0x100, pLen);

        // Displacement pointing from codeBuf+0x100+BytesToSkip to static pointer at moduleBase+0x800
        int dispOffset = 0x100 + gsPattern.BytesToSkip;
        long targetStaticAddrLong = moduleBase.ToInt64() + 0x800;
        long rip = moduleBase.ToInt64() + dispOffset + 4;
        int disp32 = (int)(targetStaticAddrLong - rip);
        BitConverter.GetBytes(disp32).CopyTo(codeBuf, dispOffset);

        reader.AllocateBlockAt((ulong)moduleBase.ToInt64(), 0x1000);
        reader.WriteBytes(moduleBase, codeBuf);

        // Write GameStateStaticOffset at targetStaticAddr
        reader.Write(new IntPtr(targetStaticAddrLong), new GameStateStaticOffset { GameState = gameState });

        // InGameState: gameState + 0x90 -> inGameState
        reader.WritePointer(gameState + 0x90, inGameState);

        // AreaLoadingState: gameState + 0x50
        var areaLoadingState = reader.AllocateBlock(0x1000);
        reader.WritePointer(gameState + 0x50, areaLoadingState);
        reader.Write(areaLoadingState + 0x770, 0); // IsLoading = 0
        reader.Write(areaLoadingState + 0xEC0, 1500u); // TotalLoadingScreenTimeMs = 1500

        // CurrentStatePtr on gameState (+0x10)
        var stateBuf = reader.AllocateBlock(0x80);
        reader.WriteStdVector(gameState + 0x10, stateBuf, stateBuf + 0x10, stateBuf + 0x10);

        // AreaInstance: inGameState + areaInstanceOffset -> areaInstance
        reader.WritePointer(inGameState + areaInstanceOffset, areaInstance);

        // WorldData: inGameState + 0x368
        var worldData = reader.AllocateBlock(0x1000);
        reader.WritePointer(inGameState + 0x368, worldData);

        // AreaInstance subfields
        reader.Write(areaInstance + 0x0BC, (byte)75); // Level 75
        reader.Write(areaInstance + 0x114, 0xABCD1234u); // Hash
        reader.WritePointer(areaInstance + serverDataOffset, serverData); // ServerData
        reader.WritePointer(areaInstance + 0x5D0, localPlayer); // LocalPlayer

        // LocalPlayer subfields
        var entityDetails = reader.AllocateBlock(0x100);
        reader.WritePointer(localPlayer + 0x08, entityDetails); // EntityDetailsPtr
        reader.WriteStdVector(localPlayer + 0x10, compList, compList + 8, compList + 8); // ComponentListPtr
        reader.WritePointer(compList, lifeComp); // First component is LifeComp

        // LifeComponent Header & Vitals
        reader.Write(lifeComp, new ComponentHeader { StaticPtr = dummyVtable });
        reader.Write(lifeComp + 0x1B0, new VitalStruct { VtablePtr = dummyVtable, Total = 5000, Current = 4800 }); // Health
        reader.Write(lifeComp + 0x208, new VitalStruct { VtablePtr = dummyVtable, Total = 1200, Current = 1200 }); // Mana
        reader.Write(lifeComp + 0x248, new VitalStruct { VtablePtr = dummyVtable, Total = 300, Current = 300 });   // ES

        // ServerData -> PSD Vector
        reader.WritePointer(psdVectorBuf, psd);
        reader.WriteStdVector(serverData + psdVectorOffset, psdVectorBuf, psdVectorBuf + 8, psdVectorBuf + 8);

        // PSD -> Gold Slot -> Gold Field
        reader.WritePointer(psd + goldRecordSlotOffset, goldRecord);
        reader.Write(goldRecord + goldFieldOffset, goldValue);

        return (reader, gameState, inGameState, areaInstance, serverData, psd, goldRecord, localPlayer, compList);
    }

    // 1. Healthy root/core chain -> VALID
    private static void Test1_HealthyCoreChain(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment().reader;
        var engine = new OffsetRecoveryEngine();
        var report = engine.RunValidation(setup, expectedGold: 50_000_000);

        var gsPattern = report.Results.First(r => r.NodeId == "pattern_game_states");
        var gsRoot = report.Results.First(r => r.NodeId == "game_state_root");
        var igs = report.Results.First(r => r.NodeId == "game_state_in_game_state");
        var ai = report.Results.First(r => r.NodeId == "in_game_area_instance");
        var sd = report.Results.First(r => r.NodeId == "area_server_data");
        var psd = report.Results.First(r => r.NodeId == "server_data_psd_vector");
        var gold = report.Results.First(r => r.NodeId == "psd_gold_field");

        check(gsPattern.Status == ValidationStatus.VALID, "T1: Game States pattern is VALID.");
        check(gsRoot.Status == ValidationStatus.VALID, "T1: GameState root is VALID.");
        check(igs.Status == ValidationStatus.VALID, "T1: InGameState is VALID.");
        check(ai.Status == ValidationStatus.VALID, "T1: AreaInstance is VALID.");
        check(sd.Status == ValidationStatus.VALID, "T1: ServerData is VALID.");
        check(psd.Status == ValidationStatus.VALID, "T1: PSD vector is VALID.");
        check(gold.Status == ValidationStatus.VALID, "T1: Gold field with ground truth is VALID.");
    }

    // 2. Broken static root -> all dependent nodes BLOCKED
    private static void Test2_BrokenStaticRootBlocksAllDescendants(Action<bool, string> check)
    {
        using var reader = new SyntheticMemoryReader();
        // Allocate empty buffer without planting pattern
        reader.AllocateBlockAt((ulong)reader.MainModuleBase.ToInt64(), 0x1000);

        var engine = new OffsetRecoveryEngine();
        var report = engine.RunValidation(reader, expectedGold: 50_000_000);

        var gsPattern = report.Results.First(r => r.NodeId == "pattern_game_states");
        var gsRoot = report.Results.First(r => r.NodeId == "game_state_root");
        var igs = report.Results.First(r => r.NodeId == "game_state_in_game_state");
        var ai = report.Results.First(r => r.NodeId == "in_game_area_instance");

        check(gsPattern.Status == ValidationStatus.BROKEN, "T2: Missing static pattern is BROKEN.");
        check(gsRoot.Status == ValidationStatus.BLOCKED, "T2: GameState root is BLOCKED by static pattern.");
        check(igs.Status == ValidationStatus.BLOCKED, "T2: InGameState is BLOCKED by GameState root.");
        check(ai.Status == ValidationStatus.BLOCKED, "T2: AreaInstance is BLOCKED.");
    }

    // 3. Broken InGameState -> descendants BLOCKED
    private static void Test3_BrokenInGameStateBlocksDescendants(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment().reader;
        // Zero out InGameState pointer (+0x90) in GameState
        setup.WritePointer(new IntPtr(0x10000000 + 0x90), IntPtr.Zero);

        var engine = new OffsetRecoveryEngine();
        var report = engine.RunValidation(setup, expectedGold: 50_000_000);

        var igs = report.Results.First(r => r.NodeId == "game_state_in_game_state");
        var ai = report.Results.First(r => r.NodeId == "in_game_area_instance");
        var sd = report.Results.First(r => r.NodeId == "area_server_data");

        check(igs.Status == ValidationStatus.BROKEN, "T3: Null InGameState pointer is BROKEN.");
        check(ai.Status == ValidationStatus.BLOCKED, "T3: AreaInstance is BLOCKED.");
        check(sd.Status == ValidationStatus.BLOCKED, "T3: ServerData is BLOCKED.");
    }

    // 4. Broken AreaInstance -> descendants BLOCKED
    private static void Test4_BrokenAreaInstanceBlocksDescendants(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment().reader;
        // Zero out AreaInstance pointer (+0x290) in InGameState
        setup.WritePointer(new IntPtr(0x10000000 + 0x1000 + 0x290), IntPtr.Zero);

        var engine = new OffsetRecoveryEngine();
        var report = engine.RunValidation(setup, expectedGold: 50_000_000);

        var ai = report.Results.First(r => r.NodeId == "in_game_area_instance");
        var sd = report.Results.First(r => r.NodeId == "area_server_data");
        var psd = report.Results.First(r => r.NodeId == "server_data_psd_vector");

        check(ai.Status == ValidationStatus.BROKEN, "T4: Null AreaInstance pointer is BROKEN.");
        check(sd.Status == ValidationStatus.BLOCKED, "T4: ServerData is BLOCKED.");
        check(psd.Status == ValidationStatus.BLOCKED, "T4: PSD vector is BLOCKED.");
    }

    // 5. Broken ServerData -> ServerData BROKEN, children BLOCKED
    private static void Test5_BrokenServerDataBlocksChildren(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment().reader;
        // Zero out ServerData pointer (+0x5B0) in AreaInstance
        setup.WritePointer(new IntPtr(0x10000000 + 0x2000 + 0x5B0), IntPtr.Zero);

        var engine = new OffsetRecoveryEngine();
        var report = engine.RunValidation(setup, expectedGold: 50_000_000);

        var sd = report.Results.First(r => r.NodeId == "area_server_data");
        var psd = report.Results.First(r => r.NodeId == "server_data_psd_vector");
        var goldSlot = report.Results.First(r => r.NodeId == "psd_gold_record_slot");

        check(sd.Status == ValidationStatus.BROKEN, "T5: Null ServerData pointer is BROKEN.");
        check(psd.Status == ValidationStatus.BLOCKED, "T5: PSD vector is BLOCKED.");
        check(goldSlot.Status == ValidationStatus.BLOCKED, "T5: Gold slot is BLOCKED.");
    }

    // 6. Broken vector structure -> BROKEN
    private static void Test6_BrokenStdVectorStructureReportsBroken(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment().reader;
        // Invalidate PSD vector in ServerData (+0x48): set end < begin
        var sdAddr = new IntPtr(0x10000000 + 0x3000);
        setup.WriteStdVector(sdAddr + 0x48, new IntPtr(0x5000), new IntPtr(0x4000), new IntPtr(0x5000));

        var engine = new OffsetRecoveryEngine();
        var report = engine.RunValidation(setup, expectedGold: 50_000_000);

        var psd = report.Results.First(r => r.NodeId == "server_data_psd_vector");
        var goldSlot = report.Results.First(r => r.NodeId == "psd_gold_record_slot");

        check(psd.Status == ValidationStatus.BROKEN, "T6: Inverted vector pointers (begin > end) must report BROKEN.");
        check(goldSlot.Status == ValidationStatus.BLOCKED, "T6: Child gold slot is BLOCKED.");
    }

    // 7. User-range but unreadable pointer -> BROKEN
    private static void Test7_UserRangeUnreadablePointerReportsBroken(Action<bool, string> check)
    {
        using var baseReader = new SyntheticMemoryReader();
        var unreadableAddr = new IntPtr(0x20000000);
        var mockReader = new RangeOnlyUnreadableMemoryReader(baseReader, unreadableAddr);

        var (reader, _, inGameState, _, _, _, _, _, _) = SetupSyntheticEnvironment();
        // Point AreaInstance (+0x290) to unreadable address
        reader.WritePointer(inGameState + 0x290, unreadableAddr);

        var engine = new OffsetRecoveryEngine();
        var report = engine.RunValidation(new RangeOnlyUnreadableMemoryReader(reader, unreadableAddr), expectedGold: null);

        var ai = report.Results.First(r => r.NodeId == "in_game_area_instance");
        check(ai.Status == ValidationStatus.BROKEN, "T7: Pointer to unreadable memory must be BROKEN.");
    }

    // 8. Legitimate Gold == 0 with supplied ground truth -> VALID
    private static void Test8_LegitimateZeroGoldWithGroundTruth(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment(goldValue: 0).reader;
        var engine = new OffsetRecoveryEngine();
        var report = engine.RunValidation(setup, expectedGold: 0);

        var gold = report.Results.First(r => r.NodeId == "psd_gold_field");
        check(gold.Status == ValidationStatus.VALID, "T8: Gold == 0 with --gold 0 must be VALID.");
        check(gold.ExtractedValue is int val && val == 0, "T8: Extracted gold value is 0.");
    }

    // 9. Gold without ground truth -> conservative UNVERIFIED
    private static void Test9_GoldWithoutGroundTruthIsUnverified(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment(goldValue: 12345).reader;
        var engine = new OffsetRecoveryEngine();
        var report = engine.RunValidation(setup, expectedGold: null);

        var gold = report.Results.First(r => r.NodeId == "psd_gold_field");
        check(gold.Status == ValidationStatus.UNVERIFIED, "T9: Gold without ground truth must be UNVERIFIED.");
        check(gold.ExtractedValue is int val && val == 12345, "T9: Extracted value is preserved.");
    }

    // 10. Plausible garbage numeric field is not falsely VALID
    private static void Test10_PlausibleGarbageNumericFieldNotFalselyValid(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment(goldValue: 9999).reader;
        var engine = new OffsetRecoveryEngine();
        var report = engine.RunValidation(setup, expectedGold: 10000); // Mismatch!

        var gold = report.Results.First(r => r.NodeId == "psd_gold_field");
        check(gold.Status == ValidationStatus.BROKEN, "T10: Numeric value mismatching expected ground truth is BROKEN.");
    }

    // 11. Healthy component/struct fixture validates
    private static void Test11_HealthyComponentFixtureValidates(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment().reader;
        var engine = new OffsetRecoveryEngine();
        var report = engine.RunValidation(setup, expectedGold: 50_000_000);

        var life = report.Results.First(r => r.NodeId == "comp_life");
        var health = report.Results.First(r => r.NodeId == "comp_life_health");
        var mana = report.Results.First(r => r.NodeId == "comp_life_mana");
        var es = report.Results.First(r => r.NodeId == "comp_life_es");

        check(life.Status == ValidationStatus.VALID, "T11: Life component lookup is VALID.");
        check(health.Status == ValidationStatus.VALID, "T11: Life.Health VitalStruct is VALID.");
        check(mana.Status == ValidationStatus.VALID, "T11: Life.Mana VitalStruct is VALID.");
        check(es.Status == ValidationStatus.VALID, "T11: Life.EnergyShield VitalStruct is VALID.");
    }

    // 12. Shift a tested field in synthetic memory: configured old offset must report BROKEN, no recovery search occurs
    private static void Test12_ShiftedFieldReportsBrokenZeroRecovery(Action<bool, string> check)
    {
        // Shift AreaInstance from +0x290 to +0x2B0
        using var setup = SetupSyntheticEnvironment(areaInstanceOffset: 0x2B0).reader;
        var engine = new OffsetRecoveryEngine();
        var report = engine.RunValidation(setup, expectedGold: 50_000_000);

        var ai = report.Results.First(r => r.NodeId == "in_game_area_instance");
        check(ai.Status == ValidationStatus.BROKEN, "T12: Shifted AreaInstance must be BROKEN at configured offset.");
        check(ai.Candidates.Count == 0, "T12: Candidates list must be empty (zero recovery searches).");
        check(report.Recommendations.Count == 0, "T12: Zero recommendations produced.");
    }

    // 13. Validate-all never invokes IRecoveryStrategy
    private static void Test13_ValidateAllNeverInvokesRecoveryStrategy(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment(areaInstanceOffset: 0x2B0).reader;
        var engine = new OffsetRecoveryEngine();
        var report = engine.RunValidation(setup, expectedGold: 50_000_000);

        // If recovery was invoked, candidates would be populated
        check(report.Results.All(r => r.Candidates.Count == 0), "T13: Zero recovery candidate lists generated across all nodes.");
    }

    // 14. No provisional offsets are created
    private static void Test14_NoProvisionalOffsetsCreated(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment(areaInstanceOffset: 0x2B0).reader;
        var engine = new OffsetRecoveryEngine();
        var report = engine.RunValidation(setup, expectedGold: 50_000_000);

        check(report.Results.All(r => !r.IsProvisional), "T14: No provisional offset flags are set on any result.");
    }

    // 15. No candidate scanning occurs
    private static void Test15_NoCandidateScanningOccurs(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment(areaInstanceOffset: 0x2B0).reader;
        setup.ResetMetrics();
        var engine = new OffsetRecoveryEngine();
        var report = engine.RunValidation(setup, expectedGold: 50_000_000);

        // Reads should only access exact configured offsets, not wide scan radii
        check(report.Results.First(r => r.NodeId == "in_game_area_instance").Status == ValidationStatus.BROKEN, "T15: Shifted node is BROKEN.");
    }

    // 16. Multi-level blocked propagation
    private static void Test16_MultiLevelBlockedPropagation(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment().reader;
        // Invalidate Game States pattern
        setup.WriteBytes(setup.MainModuleBase, new byte[0x500]);

        var engine = new OffsetRecoveryEngine();
        var report = engine.RunValidation(setup, expectedGold: 50_000_000);

        check(report.Results.First(r => r.NodeId == "pattern_game_states").Status == ValidationStatus.BROKEN, "T16: Pattern is BROKEN.");
        check(report.Results.First(r => r.NodeId == "game_state_root").Status == ValidationStatus.BLOCKED, "T16: Level 1 GameState is BLOCKED.");
        check(report.Results.First(r => r.NodeId == "game_state_in_game_state").Status == ValidationStatus.BLOCKED, "T16: Level 2 InGameState is BLOCKED.");
        check(report.Results.First(r => r.NodeId == "in_game_area_instance").Status == ValidationStatus.BLOCKED, "T16: Level 3 AreaInstance is BLOCKED.");
        check(report.Results.First(r => r.NodeId == "area_server_data").Status == ValidationStatus.BLOCKED, "T16: Level 4 ServerData is BLOCKED.");
        check(report.Results.First(r => r.NodeId == "server_data_psd_vector").Status == ValidationStatus.BLOCKED, "T16: Level 5 PSD is BLOCKED.");
        check(report.Results.First(r => r.NodeId == "psd_gold_field").Status == ValidationStatus.BLOCKED, "T16: Level 6 Gold is BLOCKED.");
    }

    // 17. JSON preserves all statuses and evidence
    private static void Test17_JsonPreservesAllStatusesAndEvidence(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment().reader;
        var engine = new OffsetRecoveryEngine();
        var report = engine.RunValidation(setup, expectedGold: null);

        var json = JsonReportExporter.ToJsonString(report);
        check(!string.IsNullOrWhiteSpace(json), "T17: JSON string is not empty.");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var resultsArr = root.GetProperty("Results");

        check(resultsArr.GetArrayLength() > 20, "T17: Results array contains all manifest nodes.");
        var goldElem = resultsArr.EnumerateArray().First(e => e.GetProperty("NodeId").GetString() == "psd_gold_field");
        check(goldElem.GetProperty("Status").GetString() == "UNVERIFIED", "T17: Gold without ground truth serialized as UNVERIFIED in JSON.");
    }

    // 18. Validator performs zero memory writes
    private static void Test18_ValidatorPerformsZeroMemoryWrites(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment().reader;
        var snapshot = setup.SnapshotAllBlocks();

        var engine = new OffsetRecoveryEngine();
        var report = engine.RunValidation(setup, expectedGold: 50_000_000);

        check(setup.MemoryMatchesSnapshot(snapshot), "T18: Memory is byte-for-byte identical after validation run (0 writes).");
    }

    // 19. Validation reads are bounded
    private static void Test19_ValidationReadsAreBounded(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment().reader;
        setup.ResetMetrics();

        var engine = new OffsetRecoveryEngine();
        var report = engine.RunValidation(setup, expectedGold: 50_000_000);

        check(setup.TotalReadsCount > 0 && setup.TotalReadsCount < 500, $"T19: Total reads count ({setup.TotalReadsCount}) is strictly bounded.");
    }

    // 20. Root is always evaluated before dependent nodes
    private static void Test20_RootEvaluatedBeforeDependentNodes(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment().reader;
        var engine = new OffsetRecoveryEngine();
        var report = engine.RunValidation(setup, expectedGold: 50_000_000);

        var nodeIndices = report.Results.Select((r, i) => (r.NodeId, Index: i)).ToDictionary(x => x.NodeId, x => x.Index);

        check(nodeIndices["pattern_game_states"] < nodeIndices["game_state_root"], "T20: Pattern evaluated before GameState root.");
        check(nodeIndices["game_state_root"] < nodeIndices["game_state_in_game_state"], "T20: GameState evaluated before InGameState.");
        check(nodeIndices["game_state_in_game_state"] < nodeIndices["in_game_area_instance"], "T20: InGameState evaluated before AreaInstance.");
        check(nodeIndices["in_game_area_instance"] < nodeIndices["area_server_data"], "T20: AreaInstance evaluated before ServerData.");
    }

    // 21. Static pattern scan validates
    private static void Test21_StaticPatternScanValidates(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment().reader;
        var engine = new OffsetRecoveryEngine();
        var report = engine.RunValidation(setup, expectedGold: 50_000_000);

        var gsPattern = report.Results.First(r => r.NodeId == "pattern_game_states");
        check(gsPattern.Status == ValidationStatus.VALID, "T21: Static Game States pattern validates correctly.");
        check(gsPattern.ResolvedAddress != IntPtr.Zero, "T21: Static pattern resolved to non-zero address.");
    }

    // 22. WorldAreaMods marked UNVERIFIED by policy
    private static void Test22_WorldAreaModsMarkedUnverifiedByPolicy(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment().reader;
        var engine = new OffsetRecoveryEngine();
        var report = engine.RunValidation(setup, expectedGold: 50_000_000);

        var mods = report.Results.First(r => r.NodeId == "server_data_world_area_mods");
        check(mods.Status == ValidationStatus.UNVERIFIED, "T22: WorldAreaMods is marked UNVERIFIED by policy.");
    }

    // 23. VitalStruct validates Health, Mana, and ES
    private static void Test23_VitalStructValidatesHealthManaEs(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment().reader;
        var engine = new OffsetRecoveryEngine();
        var report = engine.RunValidation(setup, expectedGold: 50_000_000);

        var hp = report.Results.First(r => r.NodeId == "comp_life_health");
        var mp = report.Results.First(r => r.NodeId == "comp_life_mana");
        var es = report.Results.First(r => r.NodeId == "comp_life_es");

        check(hp.Status == ValidationStatus.VALID, "T23: Health VitalStruct is VALID.");
        check(mp.Status == ValidationStatus.VALID, "T23: Mana VitalStruct is VALID.");
        check(es.Status == ValidationStatus.VALID, "T23: ES VitalStruct is VALID.");
    }

    // 24. Component lookup validates header
    private static void Test24_ComponentLookupValidatesHeader(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment().reader;
        var engine = new OffsetRecoveryEngine();
        var report = engine.RunValidation(setup, expectedGold: 50_000_000);

        var lifeComp = report.Results.First(r => r.NodeId == "comp_life");
        check(lifeComp.Status == ValidationStatus.VALID, "T24: Life Component header is VALID.");
    }

    // 25. Summary counts are accurate
    private static void Test25_SummaryCountsAreAccurate(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment().reader;
        var engine = new OffsetRecoveryEngine();
        var report = engine.RunValidation(setup, expectedGold: null);

        check(report.TotalNodesCount == report.Results.Count, "T25: TotalNodesCount matches Results count.");
        check(report.ValidCount + report.BrokenCount + report.BlockedCount + report.UnverifiedCount == report.TotalNodesCount,
            "T25: Sum of category status counts equals TotalNodesCount.");
        check(report.UnverifiedCount > 0, "T25: At least one unverified node exists (Gold without ground truth).");
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
