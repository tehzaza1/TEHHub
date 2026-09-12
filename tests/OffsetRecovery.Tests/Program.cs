using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using TEHhub;
using TEHhub.Ui;
using TEHhub.Utils;
using TEHhub.Offsets.Objects.Components;

// Exercises the real read-only process-memory reader against allocations in this test process.
// No game process is opened or modified. No external test packages are needed.
// The explicit --research-live mode is separate and opens only the supplied PoE process for reading.
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
OffsetAiTests.Run(Check);
using var reader = new SafeMemoryHandle(process.Id);
typeof(GameProcess).GetProperty(nameof(GameProcess.Handle))!.SetValue(Core.Process, reader);
typeof(GameProcess).GetProperty("Information", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Core.Process, process);
RootSearchArena.TestPe(reader, process.MainModule!.BaseAddress.ToInt64(), process.MainModule.ModuleMemorySize, Check);
Core.GHSettings.EnableNewMemoryRead = false;
Core.GHSettings.EnableOffsetTryFix = true;
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
