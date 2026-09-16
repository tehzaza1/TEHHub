using System.Diagnostics;
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
    Console.WriteLine(Path.Join(OffsetTryFix.DumpDirectory, "primary-root.latest.json"));
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
RootSearchArena.TestPe(reader, process.MainModule!.BaseAddress.ToInt64(), process.MainModule.ModuleMemorySize, Check);
Core.GHSettings.EnableNewMemoryRead = false;
Core.GHSettings.EnableOffsetTryFix = true;
#if DEBUG
BottleneckTests.Run(Check);
#endif
var clock = new ManualTime();
OffsetTryFix.Time = clock;

OffsetTryFix.ShouldPoll(new IntPtr(0x100000));
clock.Advance(11);

var memory = Marshal.AllocHGlobal(4096);
try
{
    Marshal.Copy(new byte[4096], 0, memory, 4096);
    Marshal.WriteInt32(memory, 11);
    Marshal.WriteInt32(memory + 4, 22);
    var swap = new RuntimeOffsetPatch(typeof(Pair),
        [new("First", 0, 4, 4), new("Second", 4, 0, 4)], 8);
    RuntimeOffsetRegistry.Install(swap);
    Check(reader.TryReadMemory<Pair>(memory, out var pair) && pair.First == 22 && pair.Second == 11,
        "Overlapping moves must use the original source snapshot.");
    Check(!reader.TryReadMemory<Pair>(IntPtr.Zero, out _), "Invalid read must fail.");
    RuntimeOffsetRegistry.Remove(typeof(Pair));
    Check(reader.ReadMemory<Pair>(memory).First == 11, "Revocation must restore the compiled layout.");
    RuntimeOffsetRegistry.Install(swap);
    Check(!reader.TryReadMemoryArray(memory, new Pair[1], out _), "An unverified native array stride must fail.");
    Check(!RuntimeOffsetRegistry.HasPatch<Pair>(), "An observed array must revoke a scalar patch immediately.");
    var rejected = false;
    try { RuntimeOffsetRegistry.Install(swap); }
    catch (InvalidOperationException) { rejected = true; }
    Check(rejected, "Array-used layouts must not be reinstalled.");

    var first = memory + 256;
    var second = memory + 1280;
    var firstOwner = first + 700;
    var secondOwner = second + 700;
    Marshal.WriteIntPtr(first + 24, firstOwner);
    Marshal.WriteIntPtr(second + 24, secondOwner);
    ProbeResult MakeProbe(bool candidate = true)
    {
        var probe = new ProbeResult
        {
            Name = "synthetic Chest", StructType = typeof(ChestOffsets), RootCount = 2,
            Roots = [OffsetHelperEngine.VerifyRoot(typeof(ChestOffsets), "first", first, firstOwner, "EntityPtr"),
                     OffsetHelperEngine.VerifyRoot(typeof(ChestOffsets), "second", second, secondOwner, "EntityPtr")],
        };
        probe.Verdict = probe.Roots.All(r => r.Verdict == OffsetHelperEngine.ProbeVerdict.Intact)
            ? OffsetHelperEngine.ProbeVerdict.Intact : OffsetHelperEngine.ProbeVerdict.Degraded;
        if (candidate) probe.Recoveries.AddRange(OffsetHelperEngine.FindOffsetRecoveries(typeof(ChestOffsets),
            [new("first", first, firstOwner), new("second", second, secondOwner)], "EntityPtr", new()));
        return probe;
    }

    void Observe(ProbeResult probe)
    {
        var sweep = new SweepResult { InGame = true };
        sweep.Probes.Add(probe);
        OffsetTryFix.Process(sweep, new OffsetRecoveryHints());
    }

    var probe = MakeProbe();
    Check(probe.Recoveries.Count == 1 && probe.Recoveries[0].CandidateOffset == 16,
        "The real bounded scanner must discover the moved header.");
    var patch = OffsetHelperEngine.CreateRuntimePatch(probe);
    Check(patch != null && OffsetHelperEngine.ValidateRuntimePatch(probe, patch, new()),
        "A moved header must match two independent owners.");
    var secondRoot = probe.Roots[1];
    probe.Roots[1] = probe.Roots[0];
    Check(!OffsetHelperEngine.ValidateRuntimePatch(probe, patch!, new()), "Duplicate owners are not independent evidence.");
    probe.Roots[1] = secondRoot;
    probe.Recoveries[0].AlternativeOffsets.Add(32);
    Check(OffsetHelperEngine.CreateRuntimePatch(probe) == null, "Ambiguous candidates must stay advisory.");
    probe.Recoveries[0].AlternativeOffsets.Clear();
    Observe(probe);
    Observe(probe);
    Check(!RuntimeOffsetRegistry.HasPatch<ChestOffsets>(), "Same-instant repeats must not activate a repair.");
    clock.Advance(6); Observe(probe);
    Check(!RuntimeOffsetRegistry.HasPatch<ChestOffsets>(), "Two confirmations are insufficient.");
    clock.Advance(6); Observe(probe);
    Check(RuntimeOffsetRegistry.HasPatch<ChestOffsets>(), "Three spaced confirmations should activate the repair.");
    Check(reader.ReadMemory<ChestOffsets>(first).Header.EntityPtr == firstOwner, "Consumer must read the recovered header.");
    Check(RuntimeOffsetRegistry.ComponentHeaderAddress("Chest", first) == first + 16,
        "ComponentBase owner validation must use the same recovered header.");
    var latest = Path.Join(OffsetTryFix.DumpDirectory, typeof(ChestOffsets).FullName + ".latest.json");
    using (var report = JsonDocument.Parse(File.ReadAllText(latest)))
    {
        Check(report.RootElement.GetProperty("Action").GetString() == "activated", "Activation evidence must be saved.");
        Check(report.RootElement.GetProperty("RecoveredEvidence").GetArrayLength() > 0, "Dump must include repaired validation evidence.");
    }

    Marshal.WriteIntPtr(first + 24, IntPtr.Zero);
    clock.Advance(6); Observe(probe);
    Check(!RuntimeOffsetRegistry.HasPatch<ChestOffsets>(), "Fresh native failures must revoke a repair.");
    using (var report = JsonDocument.Parse(File.ReadAllText(latest)))
        Check(report.RootElement.GetProperty("Action").GetString() == "revoked-validation-failed", "Latest dump must mark rollback.");
    Marshal.WriteIntPtr(first + 24, firstOwner);
    for (var i = 0; i < 3; i++) { clock.Advance(6); Observe(probe); }
    Check(RuntimeOffsetRegistry.HasPatch<ChestOffsets>(), "A repair must be reconfirmable after rollback.");
    Core.GHSettings.EnableOffsetTryFix = false;
    OffsetTryFix.ShouldPoll(new IntPtr(0x100000));
    Check(!RuntimeOffsetRegistry.HasPatch<ChestOffsets>(), "Disabling must withdraw active projections.");
    Check(File.Exists(latest), "Disabling must preserve historical dumps.");

    Core.GHSettings.EnableOffsetTryFix = true;
    OffsetTryFix.ShouldPoll(new IntPtr(0x100000));
    for (var i = 0; i < 3; i++) { clock.Advance(6); Observe(probe); }
    Check(RuntimeOffsetRegistry.HasPatch<ChestOffsets>(), "Re-enabling must require fresh confirmations.");
    clock.Advance(6);
    Observe(new ProbeResult { Name = probe.Name, StructType = typeof(ChestOffsets), Verdict = OffsetHelperEngine.ProbeVerdict.NoRoot });
    Check(!RuntimeOffsetRegistry.HasPatch<ChestOffsets>(), "No live roots must suspend an active recovery.");
    for (var i = 0; i < 3; i++) { clock.Advance(6); Observe(probe); }
    OffsetTryFix.ShouldPoll(new IntPtr(0x200000));
    Check(!RuntimeOffsetRegistry.HasPatch<ChestOffsets>(), "An area change must suspend recovery before settling.");
    clock.Advance(11);
    var broken = MakeProbe(candidate: false);
    for (var i = 0; i < 3; i++) { clock.Advance(6); Observe(broken); }
    Check(RuntimeOffsetRegistry.IsComponentBlocked("Chest"), "Repeated independent anchor failures must suspend component access.");
    Marshal.WriteIntPtr(first + 8, firstOwner);
    Marshal.WriteIntPtr(second + 8, secondOwner);
    clock.Advance(6); Observe(MakeProbe(candidate: false));
    Check(!RuntimeOffsetRegistry.IsComponentBlocked("Chest"), "Healthy original anchors must restore component access.");
    Check(File.Exists(Path.Join(OffsetTryFix.DumpDirectory, "coverage.latest.json")), "Declared-layout coverage must be exported.");
    Console.WriteLine($"PASS: {assertions} assertions; real memory projection, recovery validation, timing, rollback, toggle, quarantine and dumps.");
}
finally
{
    OffsetTryFix.Disable();
    Marshal.FreeHGlobal(memory);
}

[StructLayout(LayoutKind.Explicit, Size = 8)]
struct Pair
{
    [FieldOffset(0)] public int First;
    [FieldOffset(4)] public int Second;
}

sealed class ManualTime : TimeProvider
{
    private DateTimeOffset value = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => value;
    public void Advance(int seconds) => value = value.AddSeconds(seconds);
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


