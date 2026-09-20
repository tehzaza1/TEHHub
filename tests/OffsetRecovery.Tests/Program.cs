using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using TEHhub;
using TEHhub.Ui;
using TEHhub.Utils;
using TEHhub.Offsets;
using TEHhub.Offsets.Natives;
using TEHhub.Offsets.Objects;
using TEHhub.Offsets.Objects.Components;
using TEHhub.Offsets.Objects.States;
using TEHhub.Offsets.Objects.States.InGameState;
using TEHhub.RemoteObjects;
using TEHhub.RemoteObjects.States.InGameStateObjects;
using TEHhub.RemoteObjects.Components;
using TEHhub.RemoteObjects.UiElement;

// Exercises the real read-only process-memory reader against allocations in this test process.
// No game process is opened or modified. No external test packages are needed.
// The explicit --research-live mode is separate and opens only the supplied PoE process for reading.
if (args.Length == 1 && args[0] == "--log-smoke")
{
    RuntimeLogTests.Smoke();
    return;
}
if (args.Length == 1 && args[0] == "--ai-review-smoke")
{
    // Synthetic scanner evidence tests the actual localhost transport and strict decision parser.
    // It does not measure real-game recovery or feed labels/expected answers to the model.
    var fixture = new PrimaryRootResearchReport { Status = "synthetic missing-descendant fixture", ModuleBase = 0x100000, GameSha256 = "SYNTHETIC-NOT-A-GAME" };
    var observation = new PrimaryRootSearchResult { Complete = true, Status = "structural only" };
    observation.StructuralRoots.Add(new(0x101000, 0x200000, 0x40, 13, [0x300000]));
    observation.Rejections.Add("player/UI descendant evidence unavailable", 1);
    fixture.Observations.Add(observation);
    var review = await OffsetAiResearch.Review(fixture, OffsetAiResearch.DefaultModel, CancellationToken.None);
    Console.WriteLine(review.Status);
    if (review.Decision == null) throw new InvalidOperationException("Local AI smoke failed: " + review.Status);
    Console.WriteLine($"{review.Decision.Decision} {review.Decision.CandidateId}: {review.Decision.NextProbe}; {review.Decision.Reason}");
    return;
}

if (args.Length >= 1 && args[0] == "--scan-spirit")
{
    var targetValue = args.Length >= 2 ? int.Parse(args[1]) : 433;
    var proc = Process.GetProcessesByName("PathOfExile").Concat(Process.GetProcessesByName("PathOfExileSteam")).FirstOrDefault();
    if (proc == null) { Console.WriteLine("Game process not found!"); return; }
    Console.WriteLine($"Found PoE Process: {proc.ProcessName} (PID {proc.Id})");
    var baseAddress = proc.MainModule?.BaseAddress ?? IntPtr.Zero;
    var procSize = proc.MainModule?.ModuleMemorySize ?? 0;
    using var handle = new SafeMemoryHandle(proc.Id);
    var patterns = PatternFinder.Find(handle, baseAddress, procSize);
    if (!patterns.TryGetValue("Game States", out var gsOffset))
    {
        Console.WriteLine("Could not find Game States pattern!");
        return;
    }
    var offsetDataValue = handle.ReadMemory<int>(baseAddress + gsOffset);
    var gameStatesAddr = baseAddress + gsOffset + offsetDataValue + 0x04;
    Console.WriteLine($"Game States Addr: 0x{gameStatesAddr.ToInt64():X}");
    
    var staticObj = handle.ReadMemory<GameStateStaticOffset>(gameStatesAddr);
    Console.WriteLine($"StaticObj GameState: 0x{staticObj.GameState.ToInt64():X}");
    var gameStateData = handle.ReadMemory<GameStateOffset>(staticObj.GameState);
    var inGameStatePtr = gameStateData.States[4].X;
    Console.WriteLine($"InGameStatePtr: 0x{inGameStatePtr.ToInt64():X}");
    
    var inGameData = handle.ReadMemory<InGameStateOffset>(inGameStatePtr);
    var areaInstancePtr = inGameData.AreaInstanceData;
    Console.WriteLine($"AreaInstancePtr: 0x{areaInstancePtr.ToInt64():X}");
    
    var areaData = handle.ReadMemory<AreaInstanceOffsets>(areaInstancePtr);
    var playerPtr = areaData.PlayerInfo.LocalPlayerPtr;
    Console.WriteLine($"LocalPlayerPtr: 0x{playerPtr.ToInt64():X}");
    
    var entityData = handle.ReadMemory<EntityOffsets>(playerPtr);
    var compMap = handle.ReadStdVector<IntPtr>(entityData.ItemBase.ComponentListPtr);
    Console.WriteLine($"Components in Player: {compMap.Length}");
    
    IntPtr lifeCompAddress = IntPtr.Zero;
    for (int i = 0; i < compMap.Length; i++)
    {
        var compPtr = compMap[i];
        if (compPtr != IntPtr.Zero && SafeMemoryHandle.IsValidAddress(compPtr))
        {
            var header = handle.ReadMemory<ComponentHeader>(compPtr);
            if (header.EntityPtr == playerPtr)
            {
                // check if it's life component by health at 0x1b0
                var h = handle.ReadMemory<VitalStruct>(compPtr + 0x1b0);
                if (h.PtrToLifeComponent == compPtr)
                {
                    lifeCompAddress = compPtr;
                    Console.WriteLine($"Found Life Component at 0x{lifeCompAddress.ToInt64():X} (Comp #{i})!");
                    break;
                }
            }
        }
    }
    
    if (lifeCompAddress == IntPtr.Zero)
    {
        Console.WriteLine("Could not find Life component!");
        return;
    }
    
    Console.WriteLine($"\n--- Testing SpiritEntry Vector Reading ---");
    var lifeOffset = handle.ReadMemory<LifeOffset>(lifeCompAddress);
    var spiritEntries = handle.ReadStdVector<SpiritEntry>(lifeOffset.SpiritList);
    Console.WriteLine($"Found {spiritEntries.Length} Spirit entries:");
    for (int i = 0; i < spiritEntries.Length; i++)
    {
        var e = spiritEntries[i];
        var unreserved = e.Total - e.ReservedFlat;
        Console.WriteLine($"  Entry [{i}]: Total={e.Total}, ReservedFlat={e.ReservedFlat}, ReservedPercent={e.ReservedPercent}, Unreserved={unreserved}, PtrToLife=0x{e.PtrToLifeComponent.ToInt64():X}");
    }
    return;
}

if (args.Length >= 1 && args[0] == "--scan-icons")
{
    var proc = Process.GetProcessesByName("PathOfExile").Concat(Process.GetProcessesByName("PathOfExileSteam")).FirstOrDefault();
    if (proc == null) { Console.WriteLine("Game process not found!"); return; }
    Console.WriteLine($"Found PoE Process: {proc.ProcessName} (PID {proc.Id})");
    var baseAddress = proc.MainModule?.BaseAddress ?? IntPtr.Zero;
    var procSize = proc.MainModule?.ModuleMemorySize ?? 0;
    using var handle = new SafeMemoryHandle(proc.Id);
    typeof(GameProcess).GetProperty(nameof(GameProcess.Handle))!.SetValue(Core.Process, handle);
    typeof(GameProcess).GetProperty("Information", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Core.Process, proc);
    var patterns = PatternFinder.Find(handle, baseAddress, procSize);
    if (!patterns.TryGetValue("Game States", out var gsOffset))
    {
        Console.WriteLine("Could not find Game States pattern!");
        return;
    }
    var offsetDataValue = handle.ReadMemory<int>(baseAddress + gsOffset);
    var gameStatesAddr = baseAddress + gsOffset + offsetDataValue + 0x04;
    var staticObj = handle.ReadMemory<GameStateStaticOffset>(gameStatesAddr);
    var gameStateData = handle.ReadMemory<GameStateOffset>(staticObj.GameState);
    var inGameStatePtr = gameStateData.States[4].X;
    var inGameData = handle.ReadMemory<InGameStateOffset>(inGameStatePtr);
    var areaInstancePtr = inGameData.AreaInstanceData;
    var areaData = handle.ReadMemory<AreaInstanceOffsets>(areaInstancePtr);
    
    Console.WriteLine($"\n=== SCANNING LIVE ENTITIES IN CURRENT AREA ===");
    var awakeMap = areaData.Entities.AwakeEntities;
    var sleepingMap = areaData.Entities.SleepingEntities;
    
    var iconNameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    var markerDetails = new List<string>();

    var methodUpdateData = typeof(RemoteObjectBase).GetMethod("UpdateData", BindingFlags.Instance | BindingFlags.NonPublic);

    void InspectMap(StdMap map, string mapName)
    {
        handle.ReadStdMapBatched<EntityNodeKey, EntityNodeValue>(map, 100000, false, (key, value) =>
        {
            if (value.EntityPtr == IntPtr.Zero || !SafeMemoryHandle.IsValidAddress(value.EntityPtr)) return true;
            var ent = new Entity(value.EntityPtr);
            methodUpdateData?.Invoke(ent, new object[] { true });

            if (string.IsNullOrEmpty(ent.Path)) return true;

            string? iconName = null;
            int iconState = -1;
            if (ent.TryGetComponent<MinimapIcon>(out var miniComp) && miniComp != null)
            {
                methodUpdateData?.Invoke(miniComp, new object[] { true });
                iconName = miniComp.IconName;
                iconState = miniComp.State;
            }

            string? modelPath = null;
            if (ent.TryGetComponent<Animated>(out var animComp) && animComp != null)
            {
                methodUpdateData?.Invoke(animComp, new object[] { true });
                modelPath = animComp.ModelPath;
            }

            if (!string.IsNullOrEmpty(iconName))
            {
                iconNameCounts[iconName] = iconNameCounts.GetValueOrDefault(iconName, 0) + 1;
                markerDetails.Add($"[{mapName}] Path='{ent.Path}', IconName='{iconName}', State={iconState}, Model='{modelPath}'");
            }
            else if (ent.Path.Contains("Expedition", StringComparison.OrdinalIgnoreCase) || ent.Path.Contains("Marker", StringComparison.OrdinalIgnoreCase))
            {
                markerDetails.Add($"[{mapName}] (No IconName) Path='{ent.Path}', Model='{modelPath}'");
            }

            return true;
        });
    }

    InspectMap(awakeMap, "Awake");
    InspectMap(sleepingMap, "Sleeping");

    Console.WriteLine($"\n--- Discovered MinimapIcon Names in Area ({iconNameCounts.Count} types) ---");
    foreach (var (k, v) in iconNameCounts.OrderBy(x => x.Key))
    {
        Console.WriteLine($"  {k}: {v} occurrences");
    }

    Console.WriteLine($"\n--- Expedition / Marker Entities Detail ({markerDetails.Count}) ---");
    foreach (var d in markerDetails)
    {
        Console.WriteLine($"  {d}");
    }

    // Now scan memory pages for all strings matching RewardChest*
    Console.WriteLine($"\n=== SCANNING PROCESS MEMORY FOR ALL RewardChest* AND Expedition* ICONS IN DAT TABLES ===");
    var discoveredRewardChests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var discoveredExpeditionIcons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    MemoryScannerHelper.ScanProcess(proc.Id, handle, discoveredRewardChests, discoveredExpeditionIcons);

    Console.WriteLine($"\n--- All Discovered 'RewardChest*' Strings in Game Memory ({discoveredRewardChests.Count}) ---");
    foreach (var s in discoveredRewardChests.OrderBy(x => x))
    {
        Console.WriteLine($"  {s}");
    }

    Console.WriteLine($"\n--- All Discovered 'Expedition*' Strings in Game Memory ({discoveredExpeditionIcons.Count}) ---");
    foreach (var s in discoveredExpeditionIcons.OrderBy(x => x))
    {
        Console.WriteLine($"  {s}");
    }

    return;
}


if (args.Length == 2 && args[0] == "--research-live")
{
    using var game = Process.GetProcessById(int.Parse(args[1]));
    if (!game.ProcessName.StartsWith("PathOfExile", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Live research requires the explicit PathOfExile process id.");
    var module = game.MainModule ?? throw new InvalidOperationException("No game module.");
    using var handle = new SafeMemoryHandle(game.Id);
    if (handle.IsInvalid) throw new InvalidOperationException("Read-only game access denied.");
    RootSearchArena.InspectKnownRoot(handle, module.BaseAddress.ToInt64(), module.ModuleMemorySize);
    var report = await PrimaryRootResearch.Run(handle, module.BaseAddress.ToInt64(), module.ModuleMemorySize,
        module.FileName, module.FileVersionInfo.FileVersion ?? string.Empty, game.Id, CancellationToken.None);
    Console.WriteLine($"LIVE RESEARCH: {report.Status}; confirmed={report.Confirmed}; elapsed={report.ElapsedMs}ms");
    foreach (var observation in report.Observations)
    {
        Console.WriteLine($"complete={observation.Complete}; code={observation.CodeBytes}; reads={observation.Reads}; slots={observation.ProposedSlots}; structural={observation.StructuralRoots.Count}; candidates={observation.Candidates.Count}");
        foreach (var rejection in observation.Rejections) Console.WriteLine($"  {rejection.Key}: {rejection.Value}");
    }
    Console.WriteLine(Path.Join(PrimaryRootResearch.DumpDirectory, "primary-root.latest.json"));
    return;
}

var assertions = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    assertions++;
}

using var process = Process.GetCurrentProcess();
RootSearchArena.Run(Check);
RuntimeLogTests.Run(Check);
OffsetAiTests.Run(Check);
using var reader = new SafeMemoryHandle(process.Id);
typeof(GameProcess).GetProperty(nameof(GameProcess.Handle))!.SetValue(Core.Process, reader);
typeof(GameProcess).GetProperty("Information", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Core.Process, process);
OffsetRecovery.Tests.GoldTests.Run(Check);
OffsetRecovery.Tests.DvNavigationTests.Run(Check);
RootSearchArena.TestPe(reader, process.MainModule!.BaseAddress.ToInt64(), process.MainModule.ModuleMemorySize, Check);
Core.GHSettings.EnableNewMemoryRead = false;
#if DEBUG
BottleneckTests.Run(Check);
#endif

var memory = Marshal.AllocHGlobal(4096);
try
{
    Marshal.Copy(new byte[4096], 0, memory, 4096);
    var first = memory + 256;
    var second = memory + 1280;
    var firstOwner = first + 700;
    var secondOwner = second + 700;
    Marshal.WriteIntPtr(first + 24, firstOwner);
    Marshal.WriteIntPtr(second + 24, secondOwner);

    var recoveries = OffsetHelperEngine.FindOffsetRecoveries(typeof(ChestOffsets),
        [new("first", first, firstOwner), new("second", second, secondOwner)], "EntityPtr", new());
    Check(recoveries.Count == 1 && recoveries[0].CandidateOffset == 16,
        "The read-only scanner must discover the moved header suggestion without runtime mutation.");
    Check(reader.ReadMemory<ChestOffsets>(first).Header.EntityPtr == IntPtr.Zero,
        "SafeMemoryHandle must read using the compiled layout only.");
    Check(!reader.TryReadMemory<ChestOffsets>(IntPtr.Zero, out _),
        "Invalid read must fail.");

    var text8 = "ABCDEFGH";
    var bytes8 = System.Text.Encoding.Unicode.GetBytes(text8);
    var inlineWString8 = new StdWString
    {
        Buffer = new IntPtr(BitConverter.ToInt64(bytes8, 0)),
        ReservedBytes = new IntPtr(BitConverter.ToInt64(bytes8, 8)),
        Length = 8,
        Capacity = 8,
    };
    Check(
        reader.ReadStdWString(inlineWString8) == text8,
        "Inline StdWString at full 8-character capacity must decode correctly.");

    var inlineWString7 = new StdWString
    {
        Buffer = new IntPtr(BitConverter.ToInt64(bytes8, 0)),
        ReservedBytes = new IntPtr(BitConverter.ToInt64(bytes8, 8)),
        Length = 7,
        Capacity = 8,
    };
    Check(
        reader.ReadStdWString(inlineWString7) == "ABCDEFG",
        "Inline StdWString shorter than 8 characters must decode correctly.");

    // Deterministic concurrent atomic pattern claim tests
    for (var round = 0; round < 20; round++)
    {
        var singleFlag = new int[1];
        var singleWinners = 0;

        Parallel.For(0, 10000, _ =>
        {
            if (PatternFinder.TryClaimPattern(singleFlag, 0))
            {
                Interlocked.Increment(ref singleWinners);
            }
        });

        Check(singleWinners == 1, "Exactly one worker must win the atomic claim for a single pattern index.");
        Check(singleFlag[0] == 1, "Claimed pattern flag must be 1.");
    }

    for (var round = 0; round < 20; round++)
    {
        var patternCount = 6;
        var multiFlags = new int[patternCount];
        var multiWinners = new int[patternCount];

        Parallel.For(0, 20000, i =>
        {
            var targetIndex = i % patternCount;
            if (PatternFinder.TryClaimPattern(multiFlags, targetIndex))
            {
                Interlocked.Increment(ref multiWinners[targetIndex]);
            }
        });

        for (var p = 0; p < patternCount; p++)
        {
            Check(multiWinners[p] == 1, $"Pattern {p} must be claimed exactly once across concurrent workers.");
            Check(multiFlags[p] == 1, $"Pattern {p} flag must be 1.");
        }
    }

    // End-to-end synthetic multi-chunk scan with boundary straddling
    var synthSize = 200_000;
    var synthMemory = Marshal.AllocHGlobal(synthSize);
    try
    {
        var synthBytes = new byte[synthSize];
        Array.Fill(synthBytes, (byte)0x90);

        var patterns = StaticOffsetsPatterns.Patterns;
        // Terrain Rotation Selector is a 21-byte prefix of Terrain Rotator Helper, so the
        // synthetic buffer intentionally contains two valid selector match sites (at offset 90000
        // and offset 120000). Parallel first-claim scheduling may legitimately select either one.
        var placedOffsets = new Dictionary<string, int>
        {
            ["Game States"] = 100,
            ["File Root"] = 2000,
            ["AreaChangeCounter"] = 83996, // straddles chunk boundary (MaxBytesObject = 84000)
            ["Terrain Rotator Helper"] = 120000,
            ["Terrain Rotation Selector"] = 90000,
            ["GameCullSize"] = 150000,
        };
        Check(placedOffsets.Count == patterns.Length, "Must have placed offsets for all patterns.");

        for (var p = 0; p < patterns.Length; p++)
        {
            var pat = patterns[p];
            var baseOff = placedOffsets[pat.Name];
            for (var b = 0; b < pat.Data.Length; b++)
            {
                synthBytes[baseOff + b] = pat.Mask[b] ? pat.Data[b] : (byte)0x77;
            }
        }

        Marshal.Copy(synthBytes, 0, synthMemory, synthSize);

        var discovered = PatternFinder.Find(reader, synthMemory, synthSize);
        Check(discovered.Count == patterns.Length, "All synthetic patterns must be discovered.");
        for (var p = 0; p < patterns.Length; p++)
        {
            var pat = patterns[p];
            Check(discovered.TryGetValue(pat.Name, out var foundOffset), $"Discovered map must contain {pat.Name}.");

            if (pat.Name == "Terrain Rotation Selector")
            {
                var standaloneExpected = placedOffsets["Terrain Rotation Selector"] + pat.BytesToSkip;
                var helperPrefixExpected = placedOffsets["Terrain Rotator Helper"] + pat.BytesToSkip;
                Check(
                    foundOffset == standaloneExpected || foundOffset == helperPrefixExpected,
                    $"Terrain Rotation Selector offset {foundOffset} must resolve to one of its two valid synthetic byte matches ({standaloneExpected} or {helperPrefixExpected}).");
            }
            else
            {
                var expectedOffset = placedOffsets[pat.Name] + pat.BytesToSkip;
                Check(foundOffset == expectedOffset, $"Pattern {pat.Name} offset {foundOffset} must equal expected {expectedOffset}.");
            }
        }
    }
    finally
    {
        Marshal.FreeHGlobal(synthMemory);
    }

    // ---------------------------------------------------------------------
    // OffsetHelper V2 Practical Diagnostics & WorldData +0x98 Union Tests
    // ---------------------------------------------------------------------
    var v2Report = OffsetHelperV2Engine.RunPracticalDiagnostics();
    Check(v2Report != null, "OffsetHelperV2Engine.RunPracticalDiagnostics must return a valid report.");
    Check(v2Report!.Probes.Count > 0, "V2 report must contain practical diagnostic probes.");
    Check(double.IsFinite(v2Report.ElapsedMilliseconds) && v2Report.ElapsedMilliseconds >= 0,
        "V2 report must contain a finite non-negative measured diagnostic runtime.");
    Check(v2Report.Probes.Any(p => p.Name == "Stash Inventory Context"),
        "V2 report must include an explicit stash-context probe rather than folding stash into generic inventory sampling.");
    Check((int)TEHhub.RemoteEnums.InventoryName.StashInventoryId == 27,
        "InventoryName.StashInventoryId must remain mapped to Inventories.dat id 27.");

    // PoE2 interaction-panel SDK states are independent booleans. A stash or vendor can keep
    // Inventory open at the same time, so consumers must not decode a combined numeric mode.
    var stashPanels = GameUiPanelDetector.ClassifyTitles("Inventory", "Stash", null);
    Check(stashPanels.InventoryOpen && stashPanels.StashOpen && !stashPanels.VendorOpen,
        "Stash detection must independently report Inventory=true, Stash=true, Vendor=false.");
    var vendorPanels = GameUiPanelDetector.ClassifyTitles("Inventory", null, "Buy or Sell");
    Check(vendorPanels.InventoryOpen && !vendorPanels.StashOpen && vendorPanels.VendorOpen,
        "Vendor detection must independently report Inventory=true, Stash=false, Vendor=true.");
    var inventoryPanels = GameUiPanelDetector.ClassifyTitles("Inventory", null, null);
    Check(inventoryPanels.InventoryOpen && !inventoryPanels.StashOpen && !inventoryPanels.VendorOpen,
        "Inventory-only detection must not be promoted to stash or vendor.");
    var closedPanels = GameUiPanelDetector.ClassifyTitles(null, null, "Buy or Sell");
    Check(!closedPanels.InventoryOpen && !closedPanels.StashOpen && !closedPanels.VendorOpen,
        "A detached vendor label without the inventory companion panel must not report Vendor=true.");
    var materializedHiddenStash = GameUiPanelDetector.ClassifyTitles(
        "Inventory",
        "Stash",
        null,
        inventoryPanelVisible: false,
        stashPanelVisible: false);
    Check(!materializedHiddenStash.InventoryOpen && !materializedHiddenStash.StashOpen,
        "Materialized title text in hidden PoE2 panel trees must not report inventory or stash open.");

    var gameUiType = typeof(ImportantUiElements);
    Check(gameUiType.GetProperty(nameof(ImportantUiElements.IsInventoryOpen))?.PropertyType == typeof(bool),
        "GameUi must expose IsInventoryOpen as a separate boolean.");
    Check(gameUiType.GetProperty(nameof(ImportantUiElements.IsStashOpen))?.PropertyType == typeof(bool),
        "GameUi must expose IsStashOpen as a separate boolean.");
    Check(gameUiType.GetProperty(nameof(ImportantUiElements.IsVendorOpen))?.PropertyType == typeof(bool),
        "GameUi must expose IsVendorOpen as a separate boolean.");

    var stability = new StashSnapshotStabilityGate();
    Check(!stability.Observe("tab=Map;revision=1", 1_000),
        "The first stash observation must remain Loading.");
    Check(!stability.Observe("tab=Map;revision=1", 1_050),
        "Rapid duplicate reads must not bypass the stash stability window.");
    Check(stability.Observe("tab=Map;revision=1", 1_100),
        "An unchanged stash observation after the stability window must become Ready.");
    Check(!stability.Observe("tab=Map;revision=2", 1_200),
        "A changed stash inventory revision must return to Loading.");
    stability.Reset();
    Check(!stability.Observe("tab=Map;revision=2", 1_400),
        "Reset must require stability to be established again.");

    var readyEmptyInventory = new InventorySnapshot(
        InventorySnapshotState.Ready,
        TEHhub.RemoteEnums.InventoryName.StashInventoryId,
        new IntPtr(0x10000),
        12,
        12,
        7,
        Array.Empty<InventorySnapshotItem>(),
        123,
        "synthetic ready-empty");
    Check(readyEmptyInventory.IsEmpty,
        "A Ready inventory with zero items must be distinguishable as empty.");
    var loadingEmptyInventory = readyEmptyInventory with { State = InventorySnapshotState.Loading };
    Check(!loadingEmptyInventory.IsEmpty,
        "A Loading inventory with zero returned items must not be treated as empty.");

    var tierInfo = new StashTierInfo("XV", 170, new IntPtr(0x20000));
    Check(tierInfo.Name == "XV" && tierInfo.Count == 170 && tierInfo.UiAddress != IntPtr.Zero,
        "Waystone Tier SDK entries must keep their label, displayed count, and clickable UI address separate.");
    Check(typeof(StashSnapshot).GetProperty(nameof(StashSnapshot.Tiers)) != null,
        "Stash snapshots must expose Waystone Tier counts independently from selected-tab inventory items.");
    Check(typeof(StashSnapshot).GetProperty(nameof(StashSnapshot.IsAllTabsListOpen))?.PropertyType == typeof(bool),
        "Stash snapshots must expose the all-tabs list as a separate visibility state.");
    Check(typeof(StashSnapshot).GetProperty(nameof(StashSnapshot.TopTabBarUiAddress))?.PropertyType == typeof(IntPtr),
        "Stash snapshots must expose the top tab-bar container as the Ctrl+scroll hover target.");

    var topTabs = new[]
    {
        new StashTabInfo("Orb", new IntPtr(0x21000), true),
        new StashTabInfo("Map", new IntPtr(0x22000), false),
    };
    var allTabs = new[]
    {
        new StashTabInfo("Orb", new IntPtr(0x31000), false, AllTabsIndex: 0),
        new StashTabInfo("Map", new IntPtr(0x32000), false, AllTabsIndex: 1),
        new StashTabInfo("Overflow", new IntPtr(0x33000), false, AllTabsIndex: 2),
    };
    var mergedTabs = StashUiElement.MergeAllTabsFallbacks(topTabs, allTabs, "Orb");
    var mergedMap = mergedTabs.Single(tab => tab.Name == "Map");
    Check(mergedMap.UiAddress == new IntPtr(0x22000) &&
          mergedMap.FallbackUiAddress == new IntPtr(0x32000) &&
          mergedMap.AllTabsIndex == 1,
        "Stash tab SDK must preserve the top-tab address and attach the matching all-tabs row and order as fallback.");
    var overflowOnly = mergedTabs.Single(tab => tab.Name == "Overflow");
    Check(overflowOnly.UiAddress == IntPtr.Zero &&
          overflowOnly.FallbackUiAddress == new IntPtr(0x33000) &&
          overflowOnly.AllTabsIndex == 2,
        "Tabs materialized only in an open all-tabs list must remain selectable and ordered without pretending to have a top-tab control.");

    var closedListTabs = StashUiElement.MergeAllTabsFallbacks(topTabs, allTabs, "Orb", allTabsListOpen: false);
    var closedListMap = closedListTabs.Single(tab => tab.Name == "Map");
    var closedListOverflow = closedListTabs.Single(tab => tab.Name == "Overflow");
    Check(closedListMap.UiAddress == new IntPtr(0x22000) &&
          closedListMap.FallbackUiAddress == IntPtr.Zero &&
          closedListMap.AllTabsIndex == 1,
        "A closed all-tabs list must preserve top-tab input and tab order while suppressing its hidden row address.");
    Check(closedListOverflow.UiAddress == IntPtr.Zero &&
          closedListOverflow.FallbackUiAddress == IntPtr.Zero &&
          closedListOverflow.AllTabsIndex == 2,
        "A closed all-tabs list must never expose its hidden list-only row as clickable.");

    // WorldData +0x98 Union Audit Verification
    var worldAreaDetailsOffset = Marshal.OffsetOf<WorldDataOffset>(nameof(WorldDataOffset.WorldAreaDetailsPtr)).ToInt32();
    var cameraStructOffset = Marshal.OffsetOf<WorldDataOffset>(nameof(WorldDataOffset.CameraStructurePtr)).ToInt32();
    Check(worldAreaDetailsOffset == 0x98, "WorldDataOffset.WorldAreaDetailsPtr must be at 0x98.");
    Check(cameraStructOffset == 0x98, "WorldDataOffset.CameraStructurePtr must overlap at 0x98 in LayoutKind.Explicit union.");

    var cameraCodePtrOffset = Marshal.OffsetOf<CameraStructure>(nameof(CameraStructure.CodePtr)).ToInt32();
    var cameraMatrixOffset = Marshal.OffsetOf<CameraStructure>(nameof(CameraStructure.WorldToScreenMatrix)).ToInt32();
    Check(cameraCodePtrOffset == 0x00, "CameraStructure.CodePtr must be at offset 0x00.");
    Check(cameraMatrixOffset == 0x108, "CameraStructure.WorldToScreenMatrix must be at offset 0x108.");

    var effectiveMatrixOffset = cameraStructOffset + cameraMatrixOffset;
    Check(effectiveMatrixOffset == 0x1A0, "Effective WorldToScreenMatrix offset within WorldData must be 0x1A0 (0x98 + 0x108).");

    // Camera semantic validation must use the same raw matrix that OH2 reads from live WorldData.
    var identityProjection = Matrix4x4.Identity;
    Check(OffsetHelperV2Engine.IsFiniteMatrix(identityProjection),
        "OH2 raw camera validator must accept a finite identity matrix.");
    Check(
        OffsetHelperV2Engine.TryProjectWithMatrix(
            identityProjection,
            worldX: 0,
            worldY: 0,
            worldZ: 0,
            width: 200,
            height: 100,
            out var identityScreen,
            out _),
        "OH2 raw camera projection must project a finite identity-matrix sample.");
    Check(Math.Abs(identityScreen.X - 100f) < 0.001f && Math.Abs(identityScreen.Y - 50f) < 0.001f,
        "Identity raw camera projection must land at the client center.");

    var nonFiniteProjection = Matrix4x4.Identity;
    nonFiniteProjection.M23 = float.NaN;
    Check(!OffsetHelperV2Engine.IsFiniteMatrix(nonFiniteProjection),
        "OH2 raw camera validator must reject NaN in any matrix element.");

    var zeroWProjection = Matrix4x4.Identity;
    zeroWProjection.M44 = 0;
    Check(
        !OffsetHelperV2Engine.TryProjectWithMatrix(
            zeroWProjection,
            worldX: 0,
            worldY: 0,
            worldZ: 0,
            width: 200,
            height: 100,
            out _,
            out _),
        "OH2 raw camera projection must reject a near-zero clip W.");

    var areaDetailsRowOffset = Marshal.OffsetOf<WorldAreaDetailsStruct>(nameof(WorldAreaDetailsStruct.WorldAreaDetailsRowPtr)).ToInt32();
    Check(areaDetailsRowOffset == 0x98, "WorldAreaDetailsStruct.WorldAreaDetailsRowPtr must be at offset 0x98.");

    // AreaInstanceOffsets structural validation
    Check(Marshal.OffsetOf<AreaInstanceOffsets>(nameof(AreaInstanceOffsets.CurrentAreaLevel)).ToInt32() == 0x0BC,
        "AreaInstanceOffsets.CurrentAreaLevel must be at 0x0BC.");
    Check(Marshal.OffsetOf<AreaInstanceOffsets>(nameof(AreaInstanceOffsets.CurrentAreaHash)).ToInt32() == 0x114,
        "AreaInstanceOffsets.CurrentAreaHash must be at 0x114.");
    Check(Marshal.OffsetOf<AreaInstanceOffsets>(nameof(AreaInstanceOffsets.PlayerInfo)).ToInt32() == 0x5B0,
        "AreaInstanceOffsets.PlayerInfo must be at 0x5B0.");
    Check(Marshal.OffsetOf<AreaInstanceOffsets>(nameof(AreaInstanceOffsets.Entities)).ToInt32() == 0x6F0,
        "AreaInstanceOffsets.Entities must be at 0x6F0.");

    // AwakeEntities raw-walk layout assumptions used by OH2.
    Check(Unsafe.SizeOf<StdMapNode<EntityNodeKey, EntityNodeValue>>() == 0x30,
        "AwakeEntities StdMap node must remain 0x30 bytes.");
    Check(Marshal.SizeOf<EntityNodeKey>() == 0x08,
        "AwakeEntities EntityNodeKey must remain 8 bytes.");
    Check(Marshal.SizeOf<EntityNodeValue>() == 0x08,
        "AwakeEntities EntityNodeValue must remain 8 bytes.");

    // OH2 raw LocalPlayer component-map assumptions.
    Check(Marshal.OffsetOf<ItemStruct>(nameof(ItemStruct.EntityDetailsPtr)).ToInt32() == 0x08,
        "ItemStruct.EntityDetailsPtr must remain at +0x08.");
    Check(Marshal.OffsetOf<ItemStruct>(nameof(ItemStruct.ComponentListPtr)).ToInt32() == 0x10,
        "ItemStruct.ComponentListPtr must remain at +0x10.");
    Check(Marshal.OffsetOf<EntityDetails>(nameof(EntityDetails.ComponentLookUpPtr)).ToInt32() == 0x28,
        "EntityDetails.ComponentLookUpPtr must remain at +0x28.");
    Check(Marshal.OffsetOf<ComponentLookUpStruct>(nameof(ComponentLookUpStruct.ComponentsNameAndIndex)).ToInt32() == 0x28,
        "ComponentLookUpStruct.ComponentsNameAndIndex must remain at +0x28.");
    Check(Marshal.SizeOf<ComponentNameAndIndexStruct>() == 0x10,
        "ComponentNameAndIndexStruct must remain 0x10 bytes.");
    Check(Marshal.OffsetOf<ComponentNameAndIndexStruct>(nameof(ComponentNameAndIndexStruct.Index)).ToInt32() == 0x08,
        "ComponentNameAndIndexStruct.Index must remain at +0x08.");
    Check(Marshal.OffsetOf<RenderOffsets>(nameof(RenderOffsets.Header)).ToInt32() == 0x00 &&
          Marshal.OffsetOf<LifeOffset>(nameof(LifeOffset.Header)).ToInt32() == 0x00 &&
          Marshal.OffsetOf<PositionedOffsets>(nameof(PositionedOffsets.Header)).ToInt32() == 0x00 &&
          Marshal.OffsetOf<ActorOffset>(nameof(ActorOffset.Header)).ToInt32() == 0x00,
        "Practical component layouts must keep ComponentHeader at +0x00.");

    // InGameStateOffset structural validation
    Check(Marshal.OffsetOf<InGameStateOffset>(nameof(InGameStateOffset.AreaInstanceData)).ToInt32() == 0x290,
        "InGameStateOffset.AreaInstanceData must be at 0x290.");
    Check(Marshal.OffsetOf<InGameStateOffset>(nameof(InGameStateOffset.WorldData)).ToInt32() == 0x368,
        "InGameStateOffset.WorldData must be at 0x368.");
    Check(Marshal.OffsetOf<InGameStateOffset>(nameof(InGameStateOffset.UiRootStructPtr)).ToInt32() == 0x2F0,
        "InGameStateOffset.UiRootStructPtr must be at 0x2F0.");

    // AreaLoadingState structural validation used by OH2's lightweight transition sampler.
    Check(Marshal.OffsetOf<AreaLoadingStateOffset>(nameof(AreaLoadingStateOffset.IsLoading)).ToInt32() == 0x770,
        "AreaLoadingStateOffset.IsLoading must be at 0x770.");
    Check(Marshal.OffsetOf<AreaLoadingStateOffset>(nameof(AreaLoadingStateOffset.TotalLoadingScreenTimeMs)).ToInt32() == 0xEC0,
        "AreaLoadingStateOffset.TotalLoadingScreenTimeMs must be at 0xEC0.");
    Check(Marshal.OffsetOf<AreaLoadingStateOffset>(nameof(AreaLoadingStateOffset.CurrentAreaDetailsPtr)).ToInt32() == 0xF40,
        "AreaLoadingStateOffset.CurrentAreaDetailsPtr must be at 0xF40.");

    // ServerData structural validation
    Check(Marshal.OffsetOf<ServerDataOffsets>(nameof(ServerDataOffsets.PlayerServerDataPtr)).ToInt32() == 0x48,
        "ServerDataOffsets.PlayerServerDataPtr must be at 0x48.");
    Check(Marshal.OffsetOf<ServerDataStructure>(nameof(ServerDataStructure.PlayerInventories)).ToInt32() == 0x320,
        "ServerDataStructure.PlayerInventories must be at 0x320.");

    // Inventory structural validation
    Check(Marshal.OffsetOf<InventoryStruct>(nameof(InventoryStruct.TotalBoxes)).ToInt32() == 0x150,
        "InventoryStruct.TotalBoxes must be at 0x150.");
    Check(Marshal.OffsetOf<InventoryStruct>(nameof(InventoryStruct.ItemList)).ToInt32() == 0x170,
        "InventoryStruct.ItemList must be at 0x170.");
    Check(Marshal.OffsetOf<InventoryStruct>(nameof(InventoryStruct.ServerRequestCounter)).ToInt32() == 0x1E8,
        "InventoryStruct.ServerRequestCounter must be at 0x1E8.");

    // OH2 session transition evidence must distinguish a static healthy sample from an observed loading cycle.
    OffsetHelperV2Engine.ResetSessionEvidence();
    var evidenceT0 = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);
    OffsetHelperV2Engine.RecordSessionEvidenceSample(
        isLoading: 0,
        areaInstance: 0x100000,
        areaHash: 0x11111111,
        localPlayer: 0x200000,
        worldData: 0x300000,
        timestampUtc: evidenceT0);

    var evidence = OffsetHelperV2Engine.GetSessionEvidence();
    Check(evidence.Samples == 1 && evidence.LoadingSamples == 1,
        "OH2 session evidence must count the first static sample.");
    Check(evidence.SawLoadingIdle && !evidence.SawLoadingActive,
        "OH2 session evidence must record idle loading state without inventing an active loading observation.");
    Check(!evidence.HasCompleteLoadingCycle && !evidence.HasAreaTransition,
        "One static sample must not qualify as a loading cycle or area transition.");

    OffsetHelperV2Engine.RecordSessionEvidenceSample(
        isLoading: 1,
        areaInstance: 0x100000,
        areaHash: 0x11111111,
        localPlayer: 0x200000,
        worldData: 0x300000,
        timestampUtc: evidenceT0.AddSeconds(1));

    evidence = OffsetHelperV2Engine.GetSessionEvidence();
    Check(evidence.LoadingEnterTransitions == 1 && evidence.LoadingExitTransitions == 0,
        "OH2 session evidence must record the 0->1 loading transition exactly once.");
    Check(evidence.SawLoadingActive && !evidence.HasCompleteLoadingCycle,
        "Seeing loading active is not a complete loading cycle until a 1->0 transition is observed.");

    OffsetHelperV2Engine.RecordSessionEvidenceSample(
        isLoading: 0,
        areaInstance: 0x110000,
        areaHash: 0x22222222,
        localPlayer: 0x210000,
        worldData: 0x310000,
        timestampUtc: evidenceT0.AddSeconds(2));

    evidence = OffsetHelperV2Engine.GetSessionEvidence();
    Check(evidence.LoadingEnterTransitions == 1 && evidence.LoadingExitTransitions == 1,
        "OH2 session evidence must record one complete 0->1->0 loading cycle.");
    Check(evidence.HasCompleteLoadingCycle,
        "OH2 session evidence must identify a complete loading cycle only after both transition directions are observed.");
    Check(evidence.AreaInstanceChanges == 1 && evidence.AreaHashChanges == 1 && evidence.HasAreaTransition,
        "OH2 session evidence must record the post-load area pointer/hash replacement.");
    Check(evidence.LocalPlayerChanges == 1 && evidence.WorldDataChanges == 1,
        "OH2 session evidence must record post-load LocalPlayer and WorldData replacement evidence.");

    OffsetHelperV2Engine.ResetSessionEvidence();
    evidence = OffsetHelperV2Engine.GetSessionEvidence();
    Check(evidence.Samples == 0 && evidence.LoadingEnterTransitions == 0 && evidence.AreaInstanceChanges == 0,
        "ResetSessionEvidence must clear all OH2 temporal evidence.");
    Check(evidence.ProcessId == 0 && evidence.ProcessBase == 0,
        "ResetSessionEvidence must clear the process identity boundary.");

    // Transition evidence from a previous game process must never carry into a new attachment.
    OffsetHelperV2Engine.ObserveSessionProcessIdentity(processId: 1001, processBase: 0x7FF600000000);
    OffsetHelperV2Engine.RecordSessionEvidenceSample(
        isLoading: 0,
        areaInstance: 0x120000,
        areaHash: 0x33333333,
        localPlayer: 0x220000,
        worldData: 0x320000,
        timestampUtc: evidenceT0.AddSeconds(3));

    evidence = OffsetHelperV2Engine.GetSessionEvidence();
    Check(evidence.ProcessId == 1001 && evidence.ProcessBase == 0x7FF600000000,
        "OH2 session evidence must bind to the active process identity.");
    Check(evidence.Samples == 1,
        "Binding the same process identity must preserve samples captured in that process.");

    OffsetHelperV2Engine.ObserveSessionProcessIdentity(processId: 1001, processBase: 0x7FF600000000);
    evidence = OffsetHelperV2Engine.GetSessionEvidence();
    Check(evidence.Samples == 1,
        "Re-observing the same process identity must not reset OH2 session evidence.");

    OffsetHelperV2Engine.ObserveSessionProcessIdentity(processId: 2002, processBase: 0x7FF700000000);
    evidence = OffsetHelperV2Engine.GetSessionEvidence();
    Check(evidence.ProcessId == 2002 && evidence.ProcessBase == 0x7FF700000000,
        "OH2 session evidence must update its process identity after a reattach.");
    Check(evidence.Samples == 0 && evidence.LoadingEnterTransitions == 0 &&
          evidence.LoadingExitTransitions == 0 && evidence.AreaInstanceChanges == 0,
        "OH2 must discard temporal evidence from the previous game process after a reattach.");

    OffsetHelperV2Engine.ResetSessionEvidence();

    Console.WriteLine($"PASS: {assertions} assertions; read-only offset scanner and memory verification intact.");
}
finally
{
    Marshal.FreeHGlobal(memory);
}

static class MemoryScannerHelper
{
    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORY_BASIC_INFORMATION64
    {
        public ulong BaseAddress;
        public ulong AllocationBase;
        public uint AllocationProtect;
        public uint __alignment1;
        public ulong RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
        public uint __alignment2;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern int VirtualQueryEx(IntPtr hProcess, IntPtr lpAddress, out MEMORY_BASIC_INFORMATION64 lpBuffer, uint dwLength);

    public static void ScanProcess(int pid, SafeMemoryHandle handle, HashSet<string> discoveredRewardChests, HashSet<string> discoveredExpeditionIcons)
    {
        IntPtr hProc = OpenProcess(0x0410 /* PROCESS_VM_READ | PROCESS_QUERY_INFORMATION */, false, pid);
        if (hProc == IntPtr.Zero)
        {
            hProc = OpenProcess(0x1010 /* PROCESS_VM_READ | PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        }
        if (hProc == IntPtr.Zero)
        {
            Console.WriteLine($"[ScanProcess] OpenProcess failed with error: {Marshal.GetLastWin32Error()}");
            return;
        }

        try
        {
            ulong currentAddr = 0x10000;
            byte[] searchBuf = new byte[4 * 1024 * 1024]; // 4MB chunk
            long totalScannedMb = 0;

            while (currentAddr < 0x7FFFFFFF0000UL)
            {
                var queryRes = VirtualQueryEx(hProc, new IntPtr((long)currentAddr), out var memInfo, (uint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION64>());
                if (queryRes == 0 || memInfo.RegionSize == 0)
                {
                    break;
                }

                // Check if committed (MEM_COMMIT = 0x1000) and not NOACCESS (0x01) and not GUARD (0x100)
                bool readable = (memInfo.State == 0x1000) && ((memInfo.Protect & 0x01) == 0) && ((memInfo.Protect & 0x100) == 0);
                if (readable && memInfo.RegionSize <= 512 * 1024 * 1024)
                {
                    totalScannedMb += (long)(memInfo.RegionSize / (1024 * 1024));
                    ulong regionEnd = memInfo.BaseAddress + memInfo.RegionSize;
                    ulong scanPos = memInfo.BaseAddress;

                while (scanPos < regionEnd)
                {
                    int readSize = (int)Math.Min((ulong)searchBuf.Length, regionEnd - scanPos);
                    if (handle.TryReadMemoryArray(new IntPtr((long)scanPos), searchBuf, readSize, out _))
                    {
                        // 1. UTF-16 "RewardChest"
                        byte[] patChestU16 = System.Text.Encoding.Unicode.GetBytes("RewardChest");
                        for (int bi = 0; bi <= readSize - patChestU16.Length; bi += 2)
                        {
                            if (searchBuf[bi] == patChestU16[0] && searchBuf[bi + 1] == patChestU16[1])
                            {
                                bool match = true;
                                for (int pi = 2; pi < patChestU16.Length; pi++)
                                {
                                    if (searchBuf[bi + pi] != patChestU16[pi]) { match = false; break; }
                                }
                                if (match)
                                {
                                    int end = bi;
                                    while (end + 1 < readSize && (searchBuf[end] != 0 || searchBuf[end + 1] != 0))
                                    {
                                        char c = (char)(searchBuf[end] | (searchBuf[end + 1] << 8));
                                        if (!char.IsLetterOrDigit(c) && c != '_') break;
                                        end += 2;
                                    }
                                    if (end > bi)
                                    {
                                        string s = System.Text.Encoding.Unicode.GetString(searchBuf, bi, end - bi);
                                        if (s.StartsWith("RewardChest", StringComparison.OrdinalIgnoreCase) && s.Length <= 50)
                                            discoveredRewardChests.Add(s);
                                    }
                                }
                            }
                        }

                        // 2. UTF-8 / ASCII "RewardChest"
                        byte[] patChestAscii = System.Text.Encoding.ASCII.GetBytes("RewardChest");
                        for (int bi = 0; bi <= readSize - patChestAscii.Length; bi++)
                        {
                            if (searchBuf[bi] == patChestAscii[0])
                            {
                                bool match = true;
                                for (int pi = 1; pi < patChestAscii.Length; pi++)
                                {
                                    if (searchBuf[bi + pi] != patChestAscii[pi]) { match = false; break; }
                                }
                                if (match)
                                {
                                    int end = bi;
                                    while (end < readSize && searchBuf[end] != 0)
                                    {
                                        char c = (char)searchBuf[end];
                                        if (!char.IsLetterOrDigit(c) && c != '_') break;
                                        end++;
                                    }
                                    if (end > bi)
                                    {
                                        string s = System.Text.Encoding.ASCII.GetString(searchBuf, bi, end - bi);
                                        if (s.StartsWith("RewardChest", StringComparison.OrdinalIgnoreCase) && s.Length <= 50)
                                            discoveredRewardChests.Add(s);
                                    }
                                }
                            }
                        }

                        // 3. UTF-16 "Expedition"
                        byte[] patExpU16 = System.Text.Encoding.Unicode.GetBytes("Expedition");
                        for (int bi = 0; bi <= readSize - patExpU16.Length; bi += 2)
                        {
                            if (searchBuf[bi] == patExpU16[0] && searchBuf[bi + 1] == patExpU16[1])
                            {
                                bool match = true;
                                for (int pi = 2; pi < patExpU16.Length; pi++)
                                {
                                    if (searchBuf[bi + pi] != patExpU16[pi]) { match = false; break; }
                                }
                                if (match)
                                {
                                    int end = bi;
                                    while (end + 1 < readSize && (searchBuf[end] != 0 || searchBuf[end + 1] != 0))
                                    {
                                        char c = (char)(searchBuf[end] | (searchBuf[end + 1] << 8));
                                        if (!char.IsLetterOrDigit(c) && c != '_') break;
                                        end += 2;
                                    }
                                    if (end > bi)
                                    {
                                        string s = System.Text.Encoding.Unicode.GetString(searchBuf, bi, end - bi);
                                        if (s.StartsWith("Expedition", StringComparison.OrdinalIgnoreCase) && s.Length <= 50)
                                            discoveredExpeditionIcons.Add(s);
                                    }
                                }
                            }
                        }
                    }

                    scanPos += (ulong)readSize;
                }
            }

            currentAddr = memInfo.BaseAddress + memInfo.RegionSize;
        }
    }
    finally
    {
        CloseHandle(hProc);
    }
}
}
