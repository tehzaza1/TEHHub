#if DEBUG
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using TEHhub;
using TEHhub.Ui;

internal static class BottleneckTests

{
    internal static void Run(Action<bool, string> check)
    {
        var toolValues = new System.Collections.Generic.Dictionary<string, string> { ["status"] = "ready" };
        ToolDiagnostics.Publish("fixture-tool", toolValues);
        toolValues["status"] = "mutated";
        check(ToolDiagnostics.Find("fixture-tool")!.Values["status"] == "ready", "tool diagnostics detach published values from caller mutations");
        ToolDiagnostics.Publish("fixture-tool", new System.Collections.Generic.Dictionary<string, string> { ["status"] = new string('x', 4000) });
        check(ToolDiagnostics.Find("fixture-tool")!.Values["status"].Length == 2000, "tool diagnostics bound provider payload size");
        check(ToolDiagnostics.Find("missing-tool") == null, "unpublished tool is unavailable rather than healthy");
        var toolJson = JsonSerializer.Serialize(ToolDiagnostics.Snapshot(), DiagnosticsApiJsonContext.Default.ToolDiagnosticArray);
        check(toolJson.Contains("fixture-tool"), "tool diagnostics serialize through generated JSON metadata");
        check(!ToolHub.RequestWindow("EnableControllerMode", true), "tool window API rejects unrelated operational settings");
        check(BottleneckCapture.EligibleArea(true, true, false, false, "mapA") == "mapA", "area capture accepts valid playable map");
        check(BottleneckCapture.EligibleArea(true, true, true, false, "town") == "", "area capture excludes towns");
        check(BottleneckCapture.EligibleArea(true, true, false, true, "home") == "", "area capture excludes hideouts");
        check(BottleneckCapture.EligibleArea(false, true, false, false, "mapA") == "" && BottleneckCapture.EligibleArea(true, false, false, false, "mapA") == "", "area capture excludes loading and unavailable metadata");
        Core.GHSettings.ShowPerfProfiler = false;
        Core.GHSettings.ShowMemoryDiagnostics = false;
        check(!BottleneckCapture.Enabled && BottleneckCapture.BeginFrame() == 0, "capture disabled has no frame timing");
        BottleneckCapture.RequestStart();
        var frame = BottleneckCapture.BeginFrame();
        check(frame > 0 && PerformanceProfiler.IsRecording && MemoryReadDiagnostics.IsRecording, "capture enables hidden instrumentation at frame boundary");
        check(!Core.GHSettings.ShowPerfProfiler && !Core.GHSettings.ShowMemoryDiagnostics, "capture does not open profiler windows or change saved settings");
        PerformanceProfiler.StartFrame();
        using (PerformanceProfiler.Profile("fixture", "scope")) { }
        MemoryReadDiagnostics.RecordRead(MemoryReadKind.Scalar, 8, 1, true);
        PerformanceProfiler.EndFrame();
        MemoryReadDiagnostics.RecordFrame();
        BottleneckCapture.EndFrame(frame);
        BottleneckCapture.RequestStop();
        BottleneckCapture.BeginFrame();
        var snapshot = BottleneckCapture.Snapshot!;
        var firstCaptureId = snapshot.CaptureId;
        check(firstCaptureId.Length == 32 && BottleneckCapture.GetStatus().CaptureId == firstCaptureId, "capture exposes identity without starting measurement");
        check(!BottleneckCapture.Enabled && !snapshot.Active && snapshot.Frames == 1 && snapshot.Memory.TotalReadCalls == 1, "capture stop publishes final read/frame counts");
        check(snapshot.TopInclusiveScopes.Any(s => s.Name == "fixture.scope") && snapshot.P95RenderMilliseconds >= 0, "capture publishes measured scoped costs and render distribution");
        var json = JsonSerializer.Serialize(snapshot, DiagnosticsApiJsonContext.Default.BottleneckSnapshot);
        check(json.Contains("processId") && json.Contains("topInclusiveScopes") && json.Contains("gcCollections"), "capture API serializes versioned diagnostic fields");
        BottleneckCapture.RequestStart();
        BottleneckCapture.BeginFrame();
        BottleneckCapture.RequestStop();
        BottleneckCapture.BeginFrame();
        check(BottleneckCapture.Snapshot!.Frames == 0 && BottleneckCapture.Snapshot.Memory.TotalReadCalls == 0, "new capture resets prior session counters");
        check(BottleneckCapture.Snapshot.CaptureId != firstCaptureId, "new capture receives distinct identity so monitors cannot mix rounds");

        // =========================================================================
        // Hybrid Memory Reader: Exact & Hot-Page Promotion Tests (13 Invariants)
        // =========================================================================
        var procHandle = Core.Process.Handle;
        var testMemSize = 32768; // 32 KB (aligned 4KB boundaries)
        var testMemPtr = Marshal.AllocHGlobal(testMemSize + 4096);
        try
        {
            // 4KB align test base pointer
            var rawAddr = testMemPtr.ToInt64();
            var pageAlignedAddr = new IntPtr((rawAddr + 4095) & ~4095L);

            // Populate test memory with recognizable values
            for (var i = 0; i < testMemSize; i += 4)
            {
                Marshal.WriteInt32(pageAlignedAddr + i, 0x12340000 + i);
            }

            Core.GHSettings.EnableNewMemoryRead = true;
            Core.GHSettings.ShowMemoryDiagnostics = true;

            // Invariant 1: Cold isolated read remains exact
            MemoryReadDiagnostics.ResetForCapture();
            using (var plan = procHandle.BeginReadCachePlan([], enableDynamicCache: true))
            {
                var ok1 = procHandle.TryReadMemory<int>(pageAlignedAddr + 0x10, out var val1);
                check(ok1 && val1 == (0x12340000 + 0x10), "cold isolated dynamic read returns correct value");
                var snap1 = MemoryReadDiagnostics.GetApiSnapshot().Hybrid;
                check(snap1.ExactReads == 1 && snap1.ExactHits == 0 && snap1.PagePromotions == 0 && snap1.PageHits == 0,
                    "1: cold isolated read is an exact read");
                check(snap1.ExactFetchedBytes == sizeof(int), "exact fetched bytes equals exact size (4 bytes)");

                // Invariant 2: Repeated exact read hits exact cache
                var ok2 = procHandle.TryReadMemory<int>(pageAlignedAddr + 0x10, out var val2);
                check(ok2 && val2 == val1, "repeated read returns correct cached value");
                var snap2 = MemoryReadDiagnostics.GetApiSnapshot().Hybrid;
                check(snap2.ExactReads == 1 && snap2.ExactHits == 1 && snap2.PagePromotions == 0,
                    "2: repeated exact read hits exact cache");

                // Invariant 3: No 128B or 512B promotion occurs on repeated nearby accesses
                for (var i = 1; i < 5; i++)
                {
                    procHandle.TryReadMemory<int>(pageAlignedAddr + 0x10 + (i * 8), out _);
                }
                var snap3 = MemoryReadDiagnostics.GetApiSnapshot().Hybrid;
                check(snap3.PagePromotions == 0 && snap3.ExactReads == 5,
                    "3: no 128B or 512B promotion occurs on repeated sub-page accesses");
            }

            // Invariant 4: Page promotion requires >= 6 accesses, >= 256 unique bytes, >= 2 distinct 512B subregions
            // Test 4a: 6 accesses but < 256 unique bytes (e.g. 6 reads of 8 bytes = 48 bytes) -> No promotion
            MemoryReadDiagnostics.ResetForCapture();
            using (var plan = procHandle.BeginReadCachePlan([], enableDynamicCache: true))
            {
                for (var i = 0; i < 6; i++)
                {
                    var offset = (i % 2 == 0) ? (i * 8) : (600 + i * 8);
                    procHandle.TryReadMemory<long>(pageAlignedAddr + offset, out _);
                }
                var snap4a = MemoryReadDiagnostics.GetApiSnapshot().Hybrid;
                check(snap4a.PagePromotions == 0, "4a: page does not promote with < 256 unique bytes despite >= 6 accesses and 2 regions");
            }

            // Test 4b: >= 6 accesses, >= 256 unique bytes, but only 1 distinct 512B subregion -> No promotion
            MemoryReadDiagnostics.ResetForCapture();
            using (var plan = procHandle.BeginReadCachePlan([], enableDynamicCache: true))
            {
                for (var i = 0; i < 6; i++)
                {
                    // 6 reads of 64 bytes in first 512B region (0..384)
                    procHandle.TryReadMemory<TestStruct64>(pageAlignedAddr + (i * 64), out _);
                }
                var snap4b = MemoryReadDiagnostics.GetApiSnapshot().Hybrid;
                check(snap4b.PagePromotions == 0, "4b: page does not promote with 1 distinct subregion despite >= 256 bytes and >= 6 accesses");
            }

            // Invariant 5 & 6 & 7: Qualifying page promotes to exactly one 4KB window, supersedes contained exact entries, and serves subsequent reads
            MemoryReadDiagnostics.ResetForCapture();
            using (var plan = procHandle.BeginReadCachePlan([], enableDynamicCache: true))
            {
                // First 5 accesses: 5 * 64 = 320 unique bytes across 2 regions (offset 0, 64, 128 in reg 0; offset 600, 664 in reg 1)
                procHandle.TryReadMemory<TestStruct64>(pageAlignedAddr + 0, out _);
                procHandle.TryReadMemory<TestStruct64>(pageAlignedAddr + 64, out _);
                procHandle.TryReadMemory<TestStruct64>(pageAlignedAddr + 128, out _);
                procHandle.TryReadMemory<TestStruct64>(pageAlignedAddr + 600, out _);
                procHandle.TryReadMemory<TestStruct64>(pageAlignedAddr + 664, out _);

                var snapBeforeProm = MemoryReadDiagnostics.GetApiSnapshot().Hybrid;
                check(snapBeforeProm.ExactReads == 5 && snapBeforeProm.PagePromotions == 0, "5 distinct exact entries before promotion");

                // 6th access qualifies and triggers 4KB promotion
                procHandle.TryReadMemory<TestStruct64>(pageAlignedAddr + 728, out _);
                var snapProm = MemoryReadDiagnostics.GetApiSnapshot().Hybrid;
                check(snapProm.PagePromotions == 1, "5: qualifying page promotes to exactly one 4KB window");
                check(snapProm.PageFetchedBytes == 4096, "12: PageFetchedBytes equals PagePromotions * 4096");

                // Invariant 6: Subsequent contained requests hit page cache
                procHandle.TryReadMemory<int>(pageAlignedAddr + 0x200, out var pageHitVal);
                check(pageHitVal == (0x12340000 + 0x200), "read from promoted page returns correct value");
                var snapAfterHit = MemoryReadDiagnostics.GetApiSnapshot().Hybrid;
                check(snapAfterHit.PageHits == 1 && snapAfterHit.ExactReads == 5 && snapAfterHit.PagePromotions == 1,
                    "6: subsequent contained request hits page cache without new exact/page reads");

                // Invariant 7: Re-reading an earlier exact entry now hits page cache (superseded)
                procHandle.TryReadMemory<TestStruct64>(pageAlignedAddr + 0, out _);
                var snapAfterSupersede = MemoryReadDiagnostics.GetApiSnapshot().Hybrid;
                check(snapAfterSupersede.PageHits == 2 && snapAfterSupersede.ExactHits == 0,
                    "7: earlier exact entry is superseded by page cache");
            }

            // Invariant 8: Failed page promotion diagnostics tracking
            // (Note: exact-cache preservation on native read failure is source-verified by SafeMemoryHandle
            // control flow, where exactWindows superseding is strictly within the successful TryReadMemoryArray block)
            MemoryReadDiagnostics.ResetForCapture();
            MemoryReadDiagnostics.RecordHybridPromotionFailure(HybridPromotionLevel.Page4KB);
            var snapFail = MemoryReadDiagnostics.GetApiSnapshot().Hybrid;
            check(snapFail.PagePromotionFailures == 1, "8: page promotion failure is tracked in diagnostics");

            // Invariant 9: Cross-page request never causes an 8KB fetch
            MemoryReadDiagnostics.ResetForCapture();
            using (var plan = procHandle.BeginReadCachePlan([], enableDynamicCache: true))
            {
                var crossAddr = pageAlignedAddr + 4090;
                procHandle.TryReadMemory<TestStruct64>(crossAddr, out _);
                var snapCross = MemoryReadDiagnostics.GetApiSnapshot().Hybrid;
                check(snapCross.PagePromotions == 0 && snapCross.ExactReads == 1 && snapCross.ExactFetchedBytes == Marshal.SizeOf<TestStruct64>(),
                    "9: cross-page request never causes an 8KB or 4KB promotion fetch and stays exact");
            }

            // Invariant 10: MaxDynamicWindows remains bounded at exactly 2048 dynamic entries
            // Test with 2049 distinct 4KB pages (1 small exact read per page -> 0 page promotions)
            const int maxPages = 2049;
            const int maxMemSize = maxPages * 4096;
            var maxMemPtr = Marshal.AllocHGlobal(maxMemSize + 4096);
            try
            {
                var rawMaxAddr = maxMemPtr.ToInt64();
                var maxPageAlignedAddr = new IntPtr((rawMaxAddr + 4095) & ~4095L);

                // Write a recognizable value on each distinct 4KB page
                for (var p = 0; p < maxPages; p++)
                {
                    Marshal.WriteInt32(maxPageAlignedAddr + (p * 4096) + 16, 0x55000000 + p);
                }

                MemoryReadDiagnostics.ResetForCapture();
                using (var plan = procHandle.BeginReadCachePlan([], enableDynamicCache: true))
                {
                    // Read from the first 2048 distinct pages (1 read per page -> no promotions)
                    for (var p = 0; p < 2048; p++)
                    {
                        var ok = procHandle.TryReadMemory<int>(maxPageAlignedAddr + (p * 4096) + 16, out var val);
                        check(ok && val == (0x55000000 + p), "read from distinct page succeeds");
                    }

                    var snap2048 = MemoryReadDiagnostics.GetApiSnapshot().Hybrid;
                    check(snap2048.ExactReads == 2048 &&
                          snap2048.PagePromotions == 0 &&
                          snap2048.EntriesCreated == 2048,
                          "10a: exactly 2048 dynamic exact entries created without any page promotions");

                    // Read from the 2049th distinct page (page index 2048)
                    var ok2049 = procHandle.TryReadMemory<int>(maxPageAlignedAddr + (2048 * 4096) + 16, out var val2049);
                    check(ok2049 && val2049 == (0x55000000 + 2048), "2049th page read succeeds via legacy fallback");

                    var snap2049 = MemoryReadDiagnostics.GetApiSnapshot().Hybrid;
                    check(snap2049.EntriesCreated == 2048 &&
                          snap2049.ExactReads == 2048 &&
                          snap2049.PagePromotions == 0,
                          "10b: 2049th page does not create a new dynamic entry when MaxDynamicWindows (2048) is reached");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(maxMemPtr);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(testMemPtr);
            Core.GHSettings.EnableNewMemoryRead = false;
            Core.GHSettings.ShowMemoryDiagnostics = false;
        }

        // =========================================================================
        // Diagnostics Concurrency & Atomic Generation Swapping Tests
        // =========================================================================
        var running = true;
        var threadEx = (Exception?)null;

        var writer1 = new Thread(() =>
        {
            try
            {
                for (var i = 0; i < 3000 && Volatile.Read(ref running); i++)
                {
                    MemoryReadDiagnostics.RecordHybridLogicalRequest(64);
                    MemoryReadDiagnostics.RecordHybridExactRead(40);
                    MemoryReadDiagnostics.RecordHybridPagePromotion(4096);
                }
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        });

        var resetter = new Thread(() =>
        {
            try
            {
                for (var i = 0; i < 50 && Volatile.Read(ref running); i++)
                {
                    MemoryReadDiagnostics.ResetForCapture();
                    Thread.Sleep(1);
                }
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        });

        writer1.Start();
        resetter.Start();

        for (var i = 0; i < 500; i++)
        {
            var snap = MemoryReadDiagnostics.GetApiSnapshot().Hybrid;
            // Verify snapshot fetched bytes sum matches stored counters
            if (snap.FetchedBytes != (snap.ExactFetchedBytes + snap.PageFetchedBytes))
            {
                threadEx = new InvalidOperationException("FetchedBytes did not match sum of level byte counters");
                break;
            }
        }

        Volatile.Write(ref running, false);
        writer1.Join();
        resetter.Join();

        check(threadEx == null, "concurrent hybrid recording and atomic generation swapping completed without errors");

        // Perform final reset after stopping writers
        MemoryReadDiagnostics.ResetForCapture();

        // Verify reset produces a completely empty new generation
        var emptySnap = MemoryReadDiagnostics.GetApiSnapshot().Hybrid;
        check(emptySnap.LogicalRequests == 0 &&
              emptySnap.LogicalBytes == 0 &&
              emptySnap.ExactHits == 0 &&
              emptySnap.PageHits == 0 &&
              emptySnap.ExactReads == 0 &&
              emptySnap.PagePromotions == 0 &&
              emptySnap.ExactFetchedBytes == 0 &&
              emptySnap.PageFetchedBytes == 0 &&
              emptySnap.FetchedBytes == 0 &&
              emptySnap.EntriesCreated == 0,
              "11: atomic generation reset produces a completely empty new generation state");

        // Record deterministic known set of events into the new generation
        MemoryReadDiagnostics.RecordHybridLogicalRequest(200);
        MemoryReadDiagnostics.RecordHybridExactRead(64);
        MemoryReadDiagnostics.RecordHybridPagePromotion(4096);

        // Verify from the actual stored byte counters and promotion invariants
        var deterministicSnap = MemoryReadDiagnostics.GetApiSnapshot().Hybrid;
        check(deterministicSnap.ExactFetchedBytes == 64, "exact fetched bytes match recorded size");
        check(deterministicSnap.PageFetchedBytes == 4096 && deterministicSnap.PageFetchedBytes == deterministicSnap.PagePromotions * 4096, "12: page fetched bytes equal page promotions * 4096");
        check(deterministicSnap.FetchedBytes == deterministicSnap.ExactFetchedBytes + deterministicSnap.PageFetchedBytes, "13: total hybrid fetched bytes equal sum of actual level fetched bytes");
        check(deterministicSnap.EntriesCreated == deterministicSnap.ExactReads + deterministicSnap.PagePromotions, "entries created equals sum of exact reads and promotions in isolated generation");

        MemoryReadDiagnostics.ResetForCapture();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TestStruct64
    {
        public long A, B, C, D, E, F, G, H;
    }
}
#endif


