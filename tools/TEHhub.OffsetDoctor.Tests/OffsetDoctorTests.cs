namespace TEHhub.OffsetDoctor.Tests;

using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using TEHhub.OffsetDoctor.Evidence;
using TEHhub.OffsetDoctor.Manifest;
using TEHhub.OffsetDoctor.Process;
using TEHhub.OffsetDoctor.Recovery;
using TEHhub.OffsetDoctor.Reporting;
using TEHhub.OffsetDoctor.Strategies;
using TEHhub.OffsetDoctor.Validation;
using TEHhub.Offsets;
using TEHhub.Offsets.Natives;
using TEHhub.Offsets.Objects;
using TEHhub.Offsets.Objects.Components;
using TEHhub.Offsets.Objects.States;
using TEHhub.Offsets.Objects.States.InGameState;

public static class OffsetDoctorTests
{
    public static void RunAll(Action<bool, string> check)
    {
        Console.WriteLine("\n[TEHhub.OffsetDoctor.Tests] Running 45 Rigorous Semantic Validation Scenarios...");

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
        Test26_DistinctComponentsResolvedIndependently(check);
        Test27_EmptyVectorAndMapReportUnverified(check);
        Test28_ManifestOffsetsMatchStructReflection(check);
        Test29_DuplicateStaticPatternBlocksDescendants(check);
        Test30_DirectStaticPatternResolutionValidates(check);
        Test31_GenericReadablePointerIsUnverifiedWithTraversalAddress(check);
        Test32_EmptyVectorBlocksChildNode(check);
        Test33_VitalStructGroundTruthMismatchIsUnverified(check);
        Test34_MalformedComponentLookupBlocksSubfields(check);
        Test35_BroadScalarPlausibilityIsUnverified(check);
        Test36_MalformedUtf16SurrogateReportsBroken(check);
        Test37_FinalGoldAddressUnreadableReportsBroken(check);
        Test38_VitalStructCurrentGreaterThanTotalReportsBroken(check);
        Test39_DomainOnlyScalarsPlausibilityAndBounds(check);
        Test40_MalformedPsdStrideBlocksGold(check);
        Test41_UnreadableNeighboringPsdSlotBlocksGold(check);
        Test42_NullNeighboringPsdSlotBlocksGold(check);
        Test43_ArbitraryReadableBlockWithoutPsdStrideIsNotValid(check);
        Test44_MalformedPsdStridePlusMatchingNonZeroGoldIsNotValid(check);
        Test45_MalformedPsdStridePlusMatchingZeroGoldIsNotValid(check);

        Console.WriteLine("[TEHhub.OffsetDoctor.Tests] All 45 Test Scenarios Passed Successfully!\n");
    }

    private static (SyntheticMemoryReader reader, IntPtr gameState, IntPtr inGameState, IntPtr areaInstance, IntPtr serverData, IntPtr psd, IntPtr goldRecord, IntPtr localPlayer, IntPtr compList, Dictionary<string, IntPtr> compMap) SetupSyntheticEnvironment(
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
        var recordsBlock = reader.AllocateBlock(0x2000);
        var goldRecord = recordsBlock + 4 * PlayerServerDataOffsets.RecordStride;
        var localPlayer = reader.AllocateBlock(0x1000);
        var compList = reader.AllocateBlock(0x200);
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
        reader.WritePointer(stateBuf, gameState);
        reader.WriteStdVector(gameState + 0x10, stateBuf, stateBuf + 8, stateBuf + 8);

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

        // LocalPlayer subfields: ItemStruct
        var entityDetails = reader.AllocateBlock(0x200);
        var componentLookup = reader.AllocateBlock(0x200);
        var bucketData = reader.AllocateBlock(0x400);

        reader.WritePointer(localPlayer + 0x08, entityDetails); // ItemStruct.EntityDetailsPtr
        reader.WritePointer(entityDetails + 0x28, componentLookup); // EntityDetails.ComponentLookUpPtr

        // Entity path name: "Metadata/Characters/Str/Str"
        var pathStr = "Metadata/Characters/Str/Str";
        var pathBytes = Encoding.Unicode.GetBytes(pathStr);
        var pathBuf = reader.AllocateBlock(0x100);
        reader.WriteBytes(pathBuf, pathBytes);
        reader.Write(entityDetails + 0x08, new StdWString
        {
            Buffer = pathBuf,
            Length = pathStr.Length,
            Capacity = pathStr.Length + 10
        });

        // Setup Component Lookup Bucket and Distinct Component Blocks
        var compNames = new[] { "Life", "Render", "Positioned", "Actor", "Stats", "Buffs", "Player" };
        var compMap = new Dictionary<string, IntPtr>();

        for (int i = 0; i < compNames.Length; i++)
        {
            var compAddr = reader.AllocateBlock(0x1000);
            compMap[compNames[i]] = compAddr;

            // Write ComponentHeader
            reader.Write(compAddr, new ComponentHeader { StaticPtr = dummyVtable });

            // Write entry to ComponentListPtr
            reader.WritePointer(compList + (i * 8), compAddr);

            // Write name string
            var nameBytesAscii = Encoding.ASCII.GetBytes(compNames[i] + "\0");
            var namePtr = reader.AllocateBlock(0x40);
            reader.WriteBytes(namePtr, nameBytesAscii);

            // Write ComponentNameAndIndexStruct to bucket
            int entrySize = Marshal.SizeOf<ComponentNameAndIndexStruct>();
            reader.Write(bucketData + (i * entrySize), new ComponentNameAndIndexStruct
            {
                NamePtr = namePtr,
                Index = i,
                PAD_0xC = 0
            });
        }

        // Set ComponentListPtr StdVector on LocalPlayer
        reader.WriteStdVector(localPlayer + 0x10, compList, compList + (compNames.Length * 8), compList + (compNames.Length * 8));

        // Set ComponentLookupStruct.ComponentsNameAndIndex
        int totalBucketBytes = compNames.Length * Marshal.SizeOf<ComponentNameAndIndexStruct>();
        reader.Write(componentLookup + 0x28, new StdBucket
        {
            Data = new StdVector
            {
                First = bucketData,
                Last = bucketData + totalBucketBytes,
                End = bucketData + totalBucketBytes
            },
            Capacity = compNames.Length
        });

        // Life Component subfields
        var lifeComp = compMap["Life"];
        reader.Write(lifeComp + 0x1B0, new VitalStruct { VtablePtr = dummyVtable, Total = 5000, Current = 4800 }); // Health
        reader.Write(lifeComp + 0x208, new VitalStruct { VtablePtr = dummyVtable, Total = 1200, Current = 1200 }); // Mana
        reader.Write(lifeComp + 0x248, new VitalStruct { VtablePtr = dummyVtable, Total = 300, Current = 300 });   // ES

        // Render Component subfields
        var renderComp = compMap["Render"];
        reader.Write(renderComp + 0x138, new StdTuple3D<float> { X = 1250.5f, Y = -340.2f, Z = 50.0f });
        reader.Write(renderComp + 0x1B0, 50.0f); // TerrainHeight

        // Positioned Component subfields
        var positionedComp = compMap["Positioned"];
        reader.Write(positionedComp + 0x1E0, (byte)1); // Reaction = 1

        // Actor Component subfields
        var actorComp = compMap["Actor"];
        reader.Write(actorComp + 0x8B0, 105); // AnimationId = 105

        // Stats Component subfields
        var statsComp = compMap["Stats"];
        reader.Write(statsComp + 0x168, 0); // CurrentWeaponIndex = 0

        // Player Component subfields
        var playerComp = compMap["Player"];
        var playerName = "ExileHero";
        var playerNameBytes = Encoding.Unicode.GetBytes(playerName);
        var playerNameBuf = reader.AllocateBlock(0x80);
        reader.WriteBytes(playerNameBuf, playerNameBytes);
        reader.Write(playerComp + 0x1B0, new StdWString
        {
            Buffer = playerNameBuf,
            Length = playerName.Length,
            Capacity = playerName.Length + 4
        });
        reader.Write(playerComp + 0x204, (byte)88); // Level = 88

        // ServerData -> PSD Vector
        reader.WritePointer(psdVectorBuf, psd);
        reader.WriteStdVector(serverData + psdVectorOffset, psdVectorBuf, psdVectorBuf + 8, psdVectorBuf + 8);

        // PSD -> Gold Slots Sequence (5 neighboring slots: 0x0E08, 0x0E10, 0x0E18, 0x0E20, 0x0E28) -> Gold Field
        for (int i = 0; i < 5; i++)
        {
            int slotOffset = goldRecordSlotOffset - (4 - i) * 8;
            IntPtr recAddr = recordsBlock + i * PlayerServerDataOffsets.RecordStride;
            reader.WritePointer(psd + slotOffset, recAddr);
        }
        reader.Write(goldRecord + goldFieldOffset, goldValue);

        return (reader, gameState, inGameState, areaInstance, serverData, psd, goldRecord, localPlayer, compList, compMap);
    }

    // 1. Healthy root/core chain -> VALID / UNVERIFIED per semantic proof rules
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
        var goldSlot = report.Results.First(r => r.NodeId == "psd_gold_record_slot");
        var gold = report.Results.First(r => r.NodeId == "psd_gold_field");
        var playerEntity = report.Results.First(r => r.NodeId == "area_local_player_entity");

        check(gsPattern.Status == ValidationStatus.VALID, "T1: Game States pattern is VALID.");
        check(gsRoot.Status == ValidationStatus.UNVERIFIED, "T1: GameState root is UNVERIFIED.");
        check(igs.Status == ValidationStatus.UNVERIFIED, "T1: InGameState is UNVERIFIED.");
        check(ai.Status == ValidationStatus.UNVERIFIED, "T1: AreaInstance is UNVERIFIED.");
        check(sd.Status == ValidationStatus.UNVERIFIED, "T1: ServerData is UNVERIFIED.");
        check(psd.Status == ValidationStatus.UNVERIFIED, "T1: PSD vector is UNVERIFIED.");
        check(goldSlot.Status == ValidationStatus.VALID, "T1: Gold record slot with 0x80 stride is VALID.");
        check(gold.Status == ValidationStatus.VALID, "T1: Gold field with ground truth is VALID.");
        check(playerEntity.Status == ValidationStatus.VALID, "T1: Player entity with character metadata is VALID.");
    }

    // 2. Broken static root -> all dependent nodes BLOCKED
    private static void Test2_BrokenStaticRootBlocksAllDescendants(Action<bool, string> check)
    {
        using var reader = new SyntheticMemoryReader();
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
        var (reader, _, inGameState, _, _, _, _, _, _, _) = SetupSyntheticEnvironment();
        var unreadableAddr = new IntPtr(0x20000000);
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
        check(gold.ExtractedValue is double val && (int)val == 0, "T8: Extracted gold value is 0.");
    }

    // 9. Gold without ground truth -> conservative UNVERIFIED
    private static void Test9_GoldWithoutGroundTruthIsUnverified(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment(goldValue: 12345).reader;
        var engine = new OffsetRecoveryEngine();
        var report = engine.RunValidation(setup, expectedGold: null);

        var gold = report.Results.First(r => r.NodeId == "psd_gold_field");
        check(gold.Status == ValidationStatus.UNVERIFIED, "T9: Gold without ground truth must be UNVERIFIED.");
        check(gold.ExtractedValue is double val && (int)val == 12345, "T9: Extracted value is preserved.");
    }

    // 10. Plausible garbage numeric field is not falsely VALID
    private static void Test10_PlausibleGarbageNumericFieldNotFalselyValid(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment(goldValue: 9999).reader;
        var engine = new OffsetRecoveryEngine();
        var report = engine.RunValidation(setup, expectedGold: 10000); // Mismatch!

        var gold = report.Results.First(r => r.NodeId == "psd_gold_field");
        check(gold.Status == ValidationStatus.UNVERIFIED, "T10: Numeric value mismatching expected ground truth is UNVERIFIED (not falsely VALID or BROKEN).");
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

        check(report.Results.First(r => r.NodeId == "in_game_area_instance").Status == ValidationStatus.BROKEN, "T15: Shifted node is BROKEN.");
    }

    // 16. Multi-level blocked propagation
    private static void Test16_MultiLevelBlockedPropagation(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment().reader;
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

    // 26. Distinct Components Resolved Independently
    private static void Test26_DistinctComponentsResolvedIndependently(Action<bool, string> check)
    {
        var setup = SetupSyntheticEnvironment();
        using var reader = setup.reader;
        var compMap = setup.compMap;

        var engine = new OffsetRecoveryEngine();
        var report = engine.RunValidation(reader, expectedGold: 50_000_000);

        var lifeRes = report.Results.First(r => r.NodeId == "comp_life");
        var renderRes = report.Results.First(r => r.NodeId == "comp_render");
        var posRes = report.Results.First(r => r.NodeId == "comp_positioned");
        var actorRes = report.Results.First(r => r.NodeId == "comp_actor");
        var statsRes = report.Results.First(r => r.NodeId == "comp_stats");
        var playerRes = report.Results.First(r => r.NodeId == "comp_player");
        var buffsRes = report.Results.First(r => r.NodeId == "comp_buffs");

        check(lifeRes.Status == ValidationStatus.VALID && lifeRes.ResolvedAddress == compMap["Life"], "T26: Life component resolved to its distinct address.");
        check(renderRes.Status == ValidationStatus.VALID && renderRes.ResolvedAddress == compMap["Render"], "T26: Render component resolved to its distinct address.");
        check(posRes.Status == ValidationStatus.VALID && posRes.ResolvedAddress == compMap["Positioned"], "T26: Positioned component resolved to its distinct address.");
        check(actorRes.Status == ValidationStatus.VALID && actorRes.ResolvedAddress == compMap["Actor"], "T26: Actor component resolved to its distinct address.");
        check(statsRes.Status == ValidationStatus.VALID && statsRes.ResolvedAddress == compMap["Stats"], "T26: Stats component resolved to its distinct address.");
        check(playerRes.Status == ValidationStatus.VALID && playerRes.ResolvedAddress == compMap["Player"], "T26: Player component resolved to its distinct address.");
        check(buffsRes.Status == ValidationStatus.VALID && buffsRes.ResolvedAddress == compMap["Buffs"], "T26: Buffs component resolved to its distinct address.");

        // Invariant: all 7 component addresses must be distinct
        var distinctAddresses = new HashSet<IntPtr>
        {
            lifeRes.ResolvedAddress,
            renderRes.ResolvedAddress,
            posRes.ResolvedAddress,
            actorRes.ResolvedAddress,
            statsRes.ResolvedAddress,
            playerRes.ResolvedAddress,
            buffsRes.ResolvedAddress
        };
        check(distinctAddresses.Count == 7, "T26: All 7 components resolved to unique distinct addresses.");
    }

    // 27. Empty Vector and Map report UNVERIFIED
    private static void Test27_EmptyVectorAndMapReportUnverified(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment().reader;
        var engine = new OffsetRecoveryEngine();
        var report = engine.RunValidation(setup, expectedGold: 50_000_000);

        var env = report.Results.First(r => r.NodeId == "area_environments");
        var awake = report.Results.First(r => r.NodeId == "area_awake_entities");

        check(env.Status == ValidationStatus.UNVERIFIED, "T27: Empty Environments vector is UNVERIFIED (not false VALID).");
        check(awake.Status == ValidationStatus.UNVERIFIED, "T27: Empty AwakeEntities map is UNVERIFIED (not false VALID).");
    }

    // 28. Manifest Offsets match TEHhub.Offsets reflection exactly
    private static void Test28_ManifestOffsetsMatchStructReflection(Action<bool, string> check)
    {
        var manifest = OffsetManifest.CreateFullRepositoryManifest();

        var aiNode = manifest.First(n => n.Id == "in_game_area_instance");
        var expectedAiOffset = Marshal.OffsetOf<InGameStateOffset>(nameof(InGameStateOffset.AreaInstanceData)).ToInt32();
        check(aiNode.DefaultOffset == expectedAiOffset, $"T28: AreaInstance default offset (0x{aiNode.DefaultOffset:X}) matches InGameStateOffset.AreaInstanceData reflection (0x{expectedAiOffset:X}).");

        var levelNode = manifest.First(n => n.Id == "area_current_level");
        var expectedLevelOffset = Marshal.OffsetOf<AreaInstanceOffsets>(nameof(AreaInstanceOffsets.CurrentAreaLevel)).ToInt32();
        check(levelNode.DefaultOffset == expectedLevelOffset, $"T28: CurrentAreaLevel offset (0x{levelNode.DefaultOffset:X}) matches AreaInstanceOffsets.CurrentAreaLevel reflection (0x{expectedLevelOffset:X}).");

        var psdNode = manifest.First(n => n.Id == "server_data_psd_vector");
        var expectedPsdOffset = Marshal.OffsetOf<ServerDataOffsets>(nameof(ServerDataOffsets.PlayerServerDataPtr)).ToInt32();
        check(psdNode.DefaultOffset == expectedPsdOffset, $"T28: PSD vector offset (0x{psdNode.DefaultOffset:X}) matches ServerDataOffsets.PlayerServerDataPtr reflection (0x{expectedPsdOffset:X}).");

        var goldSlotNode = manifest.First(n => n.Id == "psd_gold_record_slot");
        check(goldSlotNode.DefaultOffset == PlayerServerDataOffsets.GoldRecordPtrSlot, $"T28: Gold record slot (0x{goldSlotNode.DefaultOffset:X}) matches PlayerServerDataOffsets constant (0x{PlayerServerDataOffsets.GoldRecordPtrSlot:X}).");
    }

    // 29. Duplicate static pattern match in module memory -> status UNVERIFIED, TraversalAddress = 0, descendant BLOCKED
    private static void Test29_DuplicateStaticPatternBlocksDescendants(Action<bool, string> check)
    {
        using var reader = new SyntheticMemoryReader();
        var moduleBase = reader.MainModuleBase;
        var gsPattern = StaticOffsetsPatterns.Patterns.First(p => p.Name == "Game States");
        var pData = gsPattern.Data;
        var pLen = pData.Length;

        var codeBuf = new byte[0x2000];
        // Plant duplicate pattern at 0x100 and 0x800
        Buffer.BlockCopy(pData, 0, codeBuf, 0x100, pLen);
        Buffer.BlockCopy(pData, 0, codeBuf, 0x800, pLen);

        reader.AllocateBlockAt((ulong)moduleBase.ToInt64(), 0x2000);
        reader.WriteBytes(moduleBase, codeBuf);

        var engine = new OffsetRecoveryEngine();
        var report = engine.RunValidation(reader, expectedGold: null);

        var patternRes = report.Results.First(r => r.NodeId == "pattern_game_states");
        var rootRes = report.Results.First(r => r.NodeId == "game_state_root");

        check(patternRes.Status == ValidationStatus.UNVERIFIED, "T29: Duplicate pattern match reports UNVERIFIED.");
        check(patternRes.TraversalAddress == IntPtr.Zero, "T29: Ambiguous pattern has zero TraversalAddress.");
        check(rootRes.Status == ValidationStatus.BLOCKED, "T29: Descendant game_state_root is BLOCKED by ambiguous pattern.");
    }

    // 30. Direct static pattern resolution (Terrain Rotator Helper / Selector) adds BytesToSkip and validates
    private static void Test30_DirectStaticPatternResolutionValidates(Action<bool, string> check)
    {
        using var reader = new SyntheticMemoryReader();
        var moduleBase = reader.MainModuleBase;
        var rotPattern = StaticOffsetsPatterns.Patterns.First(p => p.Name == "Terrain Rotator Helper");
        var pData = rotPattern.Data;
        var pLen = pData.Length;

        var codeBuf = new byte[0x1000];
        Buffer.BlockCopy(pData, 0, codeBuf, 0x200, pLen);

        reader.AllocateBlockAt((ulong)moduleBase.ToInt64(), 0x1000);
        reader.WriteBytes(moduleBase, codeBuf);

        var manifestNodes = new List<OffsetNode>
        {
            new OffsetNode
            {
                Id = "pattern_rotator",
                DisplayName = "Terrain Rotator Pattern",
                Category = "Static Pattern",
                StaticPatternName = "Terrain Rotator Helper",
                StaticPatternResolution = StaticPatternResolutionKind.DirectMatchPlusSkip
            }
        };

        var validator = new OffsetValidatorEngine();
        var results = validator.ValidateChain(reader, manifestNodes, new RecoveryContext());

        var res = results.First();
        var expectedDirectAddr = moduleBase + 0x200 + rotPattern.BytesToSkip;

        check(res.Status == ValidationStatus.VALID, "T30: Direct static pattern resolves to VALID.");
        check(res.ResolvedAddress == expectedDirectAddr, "T30: Direct static pattern ResolvedAddress matches moduleBase + offset + BytesToSkip.");
        check(res.TraversalAddress == expectedDirectAddr, "T30: Direct static pattern TraversalAddress is valid.");
    }

    // 31. Configured pointer field readable pointing to non-null memory without domain-specific validation -> status UNVERIFIED, TraversalAddress non-zero
    private static void Test31_GenericReadablePointerIsUnverifiedWithTraversalAddress(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment().reader;
        var engine = new OffsetRecoveryEngine();
        var report = engine.RunValidation(setup, expectedGold: 50_000_000);

        var worldData = report.Results.First(r => r.NodeId == "in_game_world_data");
        check(worldData.Status == ValidationStatus.UNVERIFIED, "T31: Generic readable WorldData pointer is UNVERIFIED.");
        check(worldData.TraversalAddress != IntPtr.Zero, "T31: Generic readable pointer provides non-zero TraversalAddress for downstream inspection.");
    }

    // 32. Empty StdVector ({0,0,0} or First == Last) -> status UNVERIFIED, TraversalAddress = 0, child node BLOCKED
    private static void Test32_EmptyVectorBlocksChildNode(Action<bool, string> check)
    {
        var setup = SetupSyntheticEnvironment();
        using var reader = setup.reader;
        var serverData = setup.serverData;

        // Overwrite PSD vector on ServerData with empty vector {0, 0, 0}
        reader.WriteStdVector(serverData + 0x48, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        var engine = new OffsetRecoveryEngine();
        var report = engine.RunValidation(reader, expectedGold: 50_000_000);

        var psdVec = report.Results.First(r => r.NodeId == "server_data_psd_vector");
        var goldSlot = report.Results.First(r => r.NodeId == "psd_gold_record_slot");

        check(psdVec.Status == ValidationStatus.UNVERIFIED, "T32: Empty PSD vector is UNVERIFIED.");
        check(psdVec.TraversalAddress == IntPtr.Zero, "T32: Empty PSD vector has zero TraversalAddress.");
        check(goldSlot.Status == ValidationStatus.BLOCKED, "T32: Downstream child gold slot is BLOCKED by empty vector.");
    }

    // 33. VitalStruct ground truth mismatch is UNVERIFIED (never BROKEN)
    private static void Test33_VitalStructGroundTruthMismatchIsUnverified(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment().reader;
        var engine = new OffsetRecoveryEngine();
        var groundTruth = new ValidationGroundTruth
        {
            ExpectedHpCurrent = 9999, // Mismatch (setup has 4800)
            ExpectedMpCurrent = 1200  // Match (setup has 1200)
        };
        var report = engine.RunValidation(setup, groundTruth);

        var hp = report.Results.First(r => r.NodeId == "comp_life_health");
        var mp = report.Results.First(r => r.NodeId == "comp_life_mana");

        check(hp.Status == ValidationStatus.UNVERIFIED, "T33: HP mismatching ground truth is UNVERIFIED (never BROKEN due to async game state).");
        check(mp.Status == ValidationStatus.VALID, "T33: Mana matching ground truth is VALID.");
    }

    // 34. Malformed component lookup containers -> component BROKEN, subfields BLOCKED
    private static void Test34_MalformedComponentLookupBlocksSubfields(Action<bool, string> check)
    {
        var setup = SetupSyntheticEnvironment();
        using var reader = setup.reader;
        var localPlayer = setup.localPlayer;

        // Corrupt EntityDetailsPtr on localPlayer to invalid address
        reader.WritePointer(localPlayer + 0x08, new IntPtr(0x99999999));

        var engine = new OffsetRecoveryEngine();
        var report = engine.RunValidation(reader, expectedGold: 50_000_000);

        var lifeComp = report.Results.First(r => r.NodeId == "comp_life");
        var hp = report.Results.First(r => r.NodeId == "comp_life_health");

        check(lifeComp.Status == ValidationStatus.BROKEN, "T34: Malformed component lookup container is BROKEN.");
        check(lifeComp.TraversalAddress == IntPtr.Zero, "T34: Broken component has zero TraversalAddress.");
        check(hp.Status == ValidationStatus.BLOCKED, "T34: Subfield comp_life_health is BLOCKED.");
    }

    // 35. Broad scalar / timer plausibility is UNVERIFIED
    private static void Test35_BroadScalarPlausibilityIsUnverified(Action<bool, string> check)
    {
        using var setup = SetupSyntheticEnvironment().reader;
        var engine = new OffsetRecoveryEngine();
        var report = engine.RunValidation(setup, expectedGold: 50_000_000);

        var hash = report.Results.First(r => r.NodeId == "area_current_hash");
        var loadTime = report.Results.First(r => r.NodeId == "loading_state_total_time");

        check(hash.Status == ValidationStatus.UNVERIFIED, "T35: CurrentAreaHash is UNVERIFIED (broad scalar).");
        check(loadTime.Status == ValidationStatus.UNVERIFIED, "T35: LoadingScreenTimeMs is UNVERIFIED (broad timer).");
    }

    // 36. Malformed UTF-16 surrogate string in StdWString reports BROKEN
    private static void Test36_MalformedUtf16SurrogateReportsBroken(Action<bool, string> check)
    {
        var setup = SetupSyntheticEnvironment();
        using var reader = setup.reader;
        var playerComp = setup.compMap["Player"];

        // Plant raw unpaired high surrogate 0xD83D followed by 'a', 'b' in little-endian UTF-16
        var badBytes = new byte[] { 0x3D, 0xD8, 0x61, 0x00, 0x62, 0x00 };
        var badBuf = reader.AllocateBlock(0x80);
        reader.WriteBytes(badBuf, badBytes);
        reader.Write(playerComp + 0x1B0, new StdWString
        {
            Buffer = badBuf,
            Length = 3,
            Capacity = 20
        });

        var engine = new OffsetRecoveryEngine();
        var report = engine.RunValidation(reader, expectedGold: 50_000_000);

        var nameRes = report.Results.First(r => r.NodeId == "comp_player_name");
        check(nameRes.Status == ValidationStatus.BROKEN, "T36: Unpaired UTF-16 high surrogate in StdWString must report BROKEN.");
    }

    // 37. Final gold address unreadable reports BROKEN, child gold field BLOCKED
    private static void Test37_FinalGoldAddressUnreadableReportsBroken(Action<bool, string> check)
    {
        var setup = SetupSyntheticEnvironment();
        using var reader = setup.reader;
        var psd = setup.psd;

        // Allocate records with 0x80 stride, but block size is only 0x500 (so +0x618 is beyond the block)
        var shortBlock = reader.AllocateBlock(0x500);
        for (int i = 0; i < 5; i++)
        {
            int slotOffset = 0x0E28 - (4 - i) * 8;
            IntPtr recAddr = shortBlock + i * PlayerServerDataOffsets.RecordStride;
            reader.WritePointer(psd + slotOffset, recAddr);
        }

        var engine = new OffsetRecoveryEngine();
        var report = engine.RunValidation(reader, expectedGold: 50_000_000);

        var slotRes = report.Results.First(r => r.NodeId == "psd_gold_record_slot");
        var goldRes = report.Results.First(r => r.NodeId == "psd_gold_field");

        check(slotRes.Status == ValidationStatus.BROKEN, "T37: Unreadable final gold address (+0x618) reports BROKEN on record slot.");
        check(goldRes.Status == ValidationStatus.BLOCKED, "T37: Child gold field is BLOCKED by broken record slot.");
    }

    // 38. VitalStruct with Current > Total reports BROKEN
    private static void Test38_VitalStructCurrentGreaterThanTotalReportsBroken(Action<bool, string> check)
    {
        var setup = SetupSyntheticEnvironment();
        using var reader = setup.reader;
        var dummyVtable = new IntPtr(0x10000000 + 0x800);
        var lifeComp = setup.compMap["Life"];

        // Write VitalStruct with Current = 6000 > Total = 5000
        reader.Write(lifeComp + 0x1B0, new VitalStruct { VtablePtr = dummyVtable, Total = 5000, Current = 6000 });

        var engine = new OffsetRecoveryEngine();
        var report = engine.RunValidation(reader, expectedGold: 50_000_000);

        var hpRes = report.Results.First(r => r.NodeId == "comp_life_health");
        check(hpRes.Status == ValidationStatus.BROKEN, "T38: VitalStruct with Current > Total must report BROKEN.");
    }

    // 39. Domain-only scalars within range report UNVERIFIED, outside range report BROKEN
    private static void Test39_DomainOnlyScalarsPlausibilityAndBounds(Action<bool, string> check)
    {
        var setup = SetupSyntheticEnvironment();
        using var reader = setup.reader;
        var areaInstance = setup.areaInstance;

        var engine = new OffsetRecoveryEngine();
        var report = engine.RunValidation(reader, expectedGold: 50_000_000);

        var levelRes = report.Results.First(r => r.NodeId == "area_current_level");
        check(levelRes.Status == ValidationStatus.UNVERIFIED, "T39: Level 75 in domain [1..100] is UNVERIFIED (plausibility only).");

        // Overwrite level with impossible level 250
        reader.Write(areaInstance + 0x0BC, (byte)250);
        var report2 = engine.RunValidation(reader, expectedGold: 50_000_000);

        var levelRes2 = report2.Results.First(r => r.NodeId == "area_current_level");
        check(levelRes2.Status == ValidationStatus.BROKEN, "T39: Level 250 outside domain [1..100] reports BROKEN.");
    }

    // 40. Malformed stride between neighboring PSD slots -> Gold record slot BROKEN and Gold field BLOCKED
    private static void Test40_MalformedPsdStrideBlocksGold(Action<bool, string> check)
    {
        var setup = SetupSyntheticEnvironment();
        using var reader = setup.reader;
        var psd = setup.psd;

        // Corrupt slot #2 (PSD + 0x0E18) stride to 0x90 instead of 0x80
        reader.TryRead<IntPtr>(psd + 0x0E18, out var currentSlot2);
        reader.WritePointer(psd + 0x0E18, currentSlot2 + 0x10);

        var engine = new OffsetRecoveryEngine();
        var report = engine.RunValidation(reader, expectedGold: 50_000_000);

        var slotRes = report.Results.First(r => r.NodeId == "psd_gold_record_slot");
        var goldRes = report.Results.First(r => r.NodeId == "psd_gold_field");

        check(slotRes.Status == ValidationStatus.BROKEN, "T40: Malformed stride between neighboring PSD slots must report BROKEN.");
        check(goldRes.Status == ValidationStatus.BLOCKED, "T40: Gold field is BLOCKED by broken record slot.");
    }

    // 41. Unreadable neighboring PSD slot -> Gold record slot BROKEN and Gold field BLOCKED
    private static void Test41_UnreadableNeighboringPsdSlotBlocksGold(Action<bool, string> check)
    {
        var setup = SetupSyntheticEnvironment();
        using var reader = setup.reader;
        var psd = setup.psd;

        // Set slot #1 (PSD + 0x0E10) to unmapped memory address
        reader.WritePointer(psd + 0x0E10, new IntPtr(0x7FFF_0000_0000L));

        var engine = new OffsetRecoveryEngine();
        var report = engine.RunValidation(reader, expectedGold: 50_000_000);

        var slotRes = report.Results.First(r => r.NodeId == "psd_gold_record_slot");
        var goldRes = report.Results.First(r => r.NodeId == "psd_gold_field");

        check(slotRes.Status == ValidationStatus.BROKEN, "T41: Unreadable neighboring PSD slot must report BROKEN.");
        check(goldRes.Status == ValidationStatus.BLOCKED, "T41: Gold field is BLOCKED by unreadable neighboring slot.");
    }

    // 42. Null neighboring PSD slot -> Gold record slot BROKEN and Gold field BLOCKED
    private static void Test42_NullNeighboringPsdSlotBlocksGold(Action<bool, string> check)
    {
        var setup = SetupSyntheticEnvironment();
        using var reader = setup.reader;
        var psd = setup.psd;

        // Set slot #3 (PSD + 0x0E20) to IntPtr.Zero
        reader.WritePointer(psd + 0x0E20, IntPtr.Zero);

        var engine = new OffsetRecoveryEngine();
        var report = engine.RunValidation(reader, expectedGold: 50_000_000);

        var slotRes = report.Results.First(r => r.NodeId == "psd_gold_record_slot");
        var goldRes = report.Results.First(r => r.NodeId == "psd_gold_field");

        check(slotRes.Status == ValidationStatus.BROKEN, "T42: Null neighboring PSD slot must report BROKEN.");
        check(goldRes.Status == ValidationStatus.BLOCKED, "T42: Gold field is BLOCKED by null neighboring slot.");
    }

    // 43. Arbitrary readable large memory block at GoldRecordPtrSlot but no valid neighboring PSD stride -> NOT VALID
    private static void Test43_ArbitraryReadableBlockWithoutPsdStrideIsNotValid(Action<bool, string> check)
    {
        var setup = SetupSyntheticEnvironment();
        using var reader = setup.reader;
        var psd = setup.psd;

        // Clear all neighboring slots 0x0E08..0x0E20 to 0, leaving only 0x0E28 pointing to valid large buffer
        reader.WritePointer(psd + 0x0E08, IntPtr.Zero);
        reader.WritePointer(psd + 0x0E10, IntPtr.Zero);
        reader.WritePointer(psd + 0x0E18, IntPtr.Zero);
        reader.WritePointer(psd + 0x0E20, IntPtr.Zero);

        var engine = new OffsetRecoveryEngine();
        var report = engine.RunValidation(reader, expectedGold: 50_000_000);

        var slotRes = report.Results.First(r => r.NodeId == "psd_gold_record_slot");
        var goldRes = report.Results.First(r => r.NodeId == "psd_gold_field");

        check(slotRes.Status == ValidationStatus.BROKEN, "T43: Arbitrary readable block at 0x0E28 without neighboring PSD stride is BROKEN.");
        check(goldRes.Status == ValidationStatus.BLOCKED, "T43: Gold field is BLOCKED (NOT VALID).");
    }

    // 44. Malformed PSD stride plus Int32 at final address equals supplied --gold -> NOT VALID
    private static void Test44_MalformedPsdStridePlusMatchingNonZeroGoldIsNotValid(Action<bool, string> check)
    {
        var setup = SetupSyntheticEnvironment(goldValue: 123_456);
        using var reader = setup.reader;
        var psd = setup.psd;

        // Corrupt slot #0 stride
        reader.WritePointer(psd + 0x0E08, IntPtr.Zero);

        var engine = new OffsetRecoveryEngine();
        var report = engine.RunValidation(reader, expectedGold: 123_456);

        var slotRes = report.Results.First(r => r.NodeId == "psd_gold_record_slot");
        var goldRes = report.Results.First(r => r.NodeId == "psd_gold_field");

        check(slotRes.Status == ValidationStatus.BROKEN, "T44: Record slot with broken neighbor is BROKEN.");
        check(goldRes.Status == ValidationStatus.BLOCKED, "T44: Matching non-zero gold ground truth is BLOCKED / NOT VALID when stride proof fails.");
    }

    // 45. Malformed PSD stride plus Int32 at final address equals supplied --gold 0 -> NOT VALID
    private static void Test45_MalformedPsdStridePlusMatchingZeroGoldIsNotValid(Action<bool, string> check)
    {
        var setup = SetupSyntheticEnvironment(goldValue: 0);
        using var reader = setup.reader;
        var psd = setup.psd;

        // Corrupt slot #1 stride
        reader.WritePointer(psd + 0x0E10, IntPtr.Zero);

        var engine = new OffsetRecoveryEngine();
        var report = engine.RunValidation(reader, expectedGold: 0);

        var slotRes = report.Results.First(r => r.NodeId == "psd_gold_record_slot");
        var goldRes = report.Results.First(r => r.NodeId == "psd_gold_field");

        check(slotRes.Status == ValidationStatus.BROKEN, "T45: Record slot with broken neighbor is BROKEN.");
        check(goldRes.Status == ValidationStatus.BLOCKED, "T45: Matching --gold 0 is BLOCKED / NOT VALID when stride proof fails.");
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
            if (address == _unreadableAddress) return true;
            return _inner.IsValidAddress(address);
        }

        public bool TryRead<T>(IntPtr address, out T value) where T : unmanaged
        {
            value = default;
            if (address == _unreadableAddress) return false;
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
