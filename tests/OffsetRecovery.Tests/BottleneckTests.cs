#if DEBUG
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using TEHhub;
using TEHhub.Ui;
using TEHhub.Utils;

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
        // Hybrid Memory Reader: PageLocalityTracker Lazy Bitmap Unit Tests
        // =========================================================================
        check(typeof(SafeMemoryHandle.ReadCachePlan.PageLocalityTracker).IsValueType, "0: PageLocalityTracker is a mutable value type (struct)");

        // 1. < 6 accesses: no promotion qualification, bitmap remains unmaterialized
        var t1 = new SafeMemoryHandle.ReadCachePlan.PageLocalityTracker();
        for (var i = 0; i < 5; i++)
        {
            t1.RecordAccess(i * 64, 64);
        }
        check(t1.AccessCount == 5 && !t1.IsBitmapMaterialized, "1: < 6 accesses keeps tracker bitmap unmaterialized");
        check(t1.DistinctMediumRegions == 1, "1: distinct medium regions is 1");

        // 2. >= 6 accesses but total requested bytes < 256: fast rejection keeps bitmap unmaterialized
        var t2 = new SafeMemoryHandle.ReadCachePlan.PageLocalityTracker();
        for (var i = 0; i < 6; i++)
        {
            var off = (i % 2 == 0) ? (i * 8) : (600 + i * 8);
            t2.RecordAccess(off, 8); // 6 * 8 = 48 bytes total
        }
        check(t2.AccessCount == 6 && t2.DistinctMediumRegions == 2, "2: 6 accesses across 2 regions recorded");
        check(t2.GetUniqueByteCount() < 256 && !t2.IsBitmapMaterialized, "2: fast rejection avoids bitmap materialization when total bytes < 256");

        // 3. >= 6 accesses, >= 256 requested bytes, but heavy overlap (< 256 unique): bitmap materialized, exact count accurate, no qualification
        var t3 = new SafeMemoryHandle.ReadCachePlan.PageLocalityTracker();
        for (var i = 0; i < 6; i++)
        {
            // 3 accesses at offset 100 (size 50), 3 accesses at offset 600 (size 50) => total requested = 300 bytes, unique = 100 bytes
            var off = (i % 2 == 0) ? 100 : 600;
            t3.RecordAccess(off, 50);
        }
        check(t3.AccessCount == 6 && t3.DistinctMediumRegions == 2, "3: 6 accesses across 2 regions with overlap");
        var u3 = t3.GetUniqueByteCount();
        check(u3 == 100 && t3.IsBitmapMaterialized, "3: bitmap materialized on evaluation and accurately returns 100 unique bytes");
        check(u3 < 256, "3: overlapping accesses reject promotion");

        // 4. >= 6 accesses, >= 256 unique bytes, but only 1 distinct medium region
        var t4 = new SafeMemoryHandle.ReadCachePlan.PageLocalityTracker();
        for (var i = 0; i < 6; i++)
        {
            t4.RecordAccess(i * 50, 50); // 0..300 in region 0
        }
        check(t4.AccessCount == 6 && t4.DistinctMediumRegions == 1, "4: 6 accesses in single 512B region");
        // SafeMemoryHandle short-circuits on DistinctMediumRegions >= 2 before calling GetUniqueByteCount()
        check(!t4.IsBitmapMaterialized, "4: single region page tracker retains unmaterialized bitmap prior to GetUniqueByteCount()");

        // 5. >= 6 accesses, >= 256 unique bytes, >= 2 medium regions: qualifies on exactly the 6th access
        var t5 = new SafeMemoryHandle.ReadCachePlan.PageLocalityTracker();
        t5.RecordAccess(0, 64);
        t5.RecordAccess(64, 64);
        t5.RecordAccess(128, 64);
        t5.RecordAccess(600, 64);
        t5.RecordAccess(664, 64);
        check(t5.AccessCount == 5 && !t5.IsBitmapMaterialized, "5a: 5 accesses do not qualify and bitmap is unmaterialized");
        t5.RecordAccess(728, 64);
        check(t5.AccessCount == 6 && t5.DistinctMediumRegions == 2, "5b: 6th access satisfies access count and region dispersion");
        check(t5.GetUniqueByteCount() == 384 && t5.IsBitmapMaterialized, "5c: 6th access materializes bitmap and returns exactly 384 unique bytes");

        // 6. Equivalence testing: compare PageLocalityTracker against ReferencePageTracker across deterministic sequences
        var rng = new Random(4242);
        var allEquivalencePassed = true;
        for (var seq = 0; seq < 200; seq++)
        {
            var tracker = new SafeMemoryHandle.ReadCachePlan.PageLocalityTracker();
            var refTracker = new ReferencePageTracker();
            var accessCount = rng.Next(1, 25);
            for (var a = 0; a < accessCount; a++)
            {
                var offset = rng.Next(0, 4000);
                var maxLen = Math.Min(128, 4096 - offset);
                var size = rng.Next(1, maxLen + 1);

                tracker.RecordAccess(offset, size);
                refTracker.RecordAccess(offset, size);

                if (tracker.AccessCount != refTracker.AccessCount ||
                    tracker.DistinctMediumRegions != refTracker.DistinctMediumRegions)
                {
                    allEquivalencePassed = false;
                    break;
                }

                // Check qualification equivalence
                var refQualifies = refTracker.Qualifies();
                var trackerQualifies = tracker.AccessCount >= 6 &&
                                       tracker.DistinctMediumRegions >= 2 &&
                                       tracker.GetUniqueByteCount() >= 256;

                if (refQualifies != trackerQualifies)
                {
                    allEquivalencePassed = false;
                    break;
                }

                if (tracker.IsBitmapMaterialized && tracker.GetUniqueByteCount() != refTracker.GetUniqueByteCount())
                {
                    allEquivalencePassed = false;
                    break;
                }
            }

            if (!allEquivalencePassed)
            {
                break;
            }
        }
        check(allEquivalencePassed, "6: PageLocalityTracker matches ReferencePageTracker across 200 pseudo-random deterministic sequences");

        // 7. Direct dictionary in-place ref mutation semantics
        var dictTest = new Dictionary<long, SafeMemoryHandle.ReadCachePlan.PageLocalityTracker>();
        ref var initRef = ref CollectionsMarshal.GetValueRefOrAddDefault(dictTest, 0x1000, out var initExists);
        check(!initExists && dictTest.Count == 1, "7a: GetValueRefOrAddDefault creates 1 entry");
        initRef.RecordAccess(0, 64);
        check(dictTest[0x1000].AccessCount == 1 && dictTest[0x1000].DistinctMediumRegions == 1, "7b: in-place ref mutation updates dictionary value without writeback");
        ref var existingRef = ref CollectionsMarshal.GetValueRefOrNullRef(dictTest, 0x1000);
        check(!Unsafe.IsNullRef(ref existingRef), "7c: GetValueRefOrNullRef finds existing entry");
        existingRef.RecordAccess(600, 64);
        check(dictTest[0x1000].AccessCount == 2 && dictTest[0x1000].DistinctMediumRegions == 2, "7d: second ref access mutates same dictionary entry in place");

        // 8. 7th+ access without prior materialization and subsequent overlapping access accounting
        var t8 = new SafeMemoryHandle.ReadCachePlan.PageLocalityTracker();
        for (var i = 0; i < 6; i++)
        {
            t8.RecordAccess(i * 10, 10);
        }
        check(t8.AccessCount == 6 && !t8.IsBitmapMaterialized, "8a: 6 small accesses keep bitmap unmaterialized");
        t8.RecordAccess(100, 50); // 7th access triggers materialization so no range is lost
        check(t8.AccessCount == 7 && t8.IsBitmapMaterialized, "8b: 7th access materializes bitmap");
        check(t8.GetUniqueByteCount() == 110, "8c: unique byte count accurately preserves all 7 ranges (60 + 50 = 110)");
        t8.RecordAccess(120, 50); // 8th access overlaps partly [120..170) vs [100..150)
        check(t8.AccessCount == 8 && t8.GetUniqueByteCount() == 130, "8d: 8th access with overlap updates unique bytes accurately (110 + 20 = 130)");

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

            // Invariant 1: Cold isolated read remains exact and does not materialize bitmap
            MemoryReadDiagnostics.ResetForCapture();
            using (var plan = procHandle.BeginReadCachePlan([], enableDynamicCache: true))
            {
                var ok1 = procHandle.TryReadMemory<int>(pageAlignedAddr + 0x10, out var val1);
                check(ok1 && val1 == (0x12340000 + 0x10), "cold isolated dynamic read returns correct value");
                var snap1 = MemoryReadDiagnostics.GetApiSnapshot().Hybrid;
                check(snap1.ExactReads == 1 && snap1.ExactHits == 0 && snap1.PagePromotions == 0 && snap1.PageHits == 0,
                    "1: cold isolated read is an exact read");
                check(snap1.ExactFetchedBytes == sizeof(int), "exact fetched bytes equals exact size (4 bytes)");
                check(snap1.PageTrackersCreated == 1 && snap1.PageBitmapsMaterialized == 0, "cold isolated read creates tracker without materializing bitmap");

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
                check(snap3.PageBitmapsMaterialized == 0, "sub-page accesses with < 6 reads do not materialize bitmap");
            }

            // Invariant 4: Page promotion requires >= 6 accesses, >= 256 unique bytes, >= 2 distinct 512B subregions
            // Test 4a: 6 accesses but < 256 unique bytes (e.g. 6 reads of 8 bytes = 48 bytes) -> No promotion & fast-reject keeps bitmap unmaterialized
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
                check(snap4a.PageBitmapsMaterialized == 0, "4a: fast rejection avoids bitmap materialization");
            }

            // Test 4b: >= 6 accesses, >= 256 unique bytes, but only 1 distinct 512B subregion -> No promotion & region short-circuit keeps bitmap unmaterialized
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
                check(snap4b.PageBitmapsMaterialized == 0, "4b: single-region check short-circuits before bitmap materialization");
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
                check(snapBeforeProm.PageBitmapsMaterialized == 0, "bitmap remains unmaterialized during first 5 accesses");

                // 6th access qualifies and triggers 4KB promotion
                procHandle.TryReadMemory<TestStruct64>(pageAlignedAddr + 728, out _);
                var snapProm = MemoryReadDiagnostics.GetApiSnapshot().Hybrid;
                check(snapProm.PagePromotions == 1, "5: qualifying page promotes to exactly one 4KB window");
                check(snapProm.PageFetchedBytes == 4096, "12: PageFetchedBytes equals PagePromotions * 4096");
                check(snapProm.PageBitmapsMaterialized == 1, "qualifying 6th access materializes bitmap");

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
                check(snapCross.PageBitmapsMaterialized == 0, "cross-page request does not materialize bitmap");
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

                // Write recognizable values on each distinct 4KB page
                for (var p = 0; p < maxPages; p++)
                {
                    Marshal.WriteInt32(maxPageAlignedAddr + (p * 4096) + 16, 0x55000000 + p);
                    Marshal.WriteInt32(maxPageAlignedAddr + (p * 4096) + 32, 0x77000000 + p);
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
                          snap2048.EntriesCreated == 2048 &&
                          snap2048.PageTrackersCreated == 2048,
                          "10a: exactly 2048 dynamic exact entries and 2048 page trackers created without promotions");

                    var okPage0Before = plan.TryGetTrackedPageAccessCount(maxPageAlignedAddr.ToInt64(), out var countPage0Before);
                    check(okPage0Before && countPage0Before == 1, "10b: page 0 tracker initially recorded 1 access");

                    // Read from the 2049th distinct page (page index 2048)
                    var ok2049 = procHandle.TryReadMemory<int>(maxPageAlignedAddr + (2048 * 4096) + 16, out var val2049);
                    check(ok2049 && val2049 == (0x55000000 + 2048), "2049th page read succeeds via legacy fallback");

                    var snap2049 = MemoryReadDiagnostics.GetApiSnapshot().Hybrid;
                    check(snap2049.EntriesCreated == 2048 &&
                          snap2049.ExactReads == 2048 &&
                          snap2049.PagePromotions == 0 &&
                          snap2049.PageTrackersCreated == 2048,
                          "10c: 2049th page does not create a new dynamic entry or tracker when limit (2048) is reached");
                    var okPage2048 = plan.TryGetTrackedPageAccessCount(maxPageAlignedAddr.ToInt64() + (2048 * 4096), out _);
                    check(!okPage2048, "10d: 2049th page is not inserted into pageTrackers dictionary");

                    // Read a DIFFERENT address in page index 0 (offset 32) that does not exist in exact cache, while at tracking capacity
                    var okMutate = procHandle.TryReadMemory<int>(maxPageAlignedAddr + 32, out var valMutate);
                    check(okMutate && valMutate == (0x77000000 + 0), "10e: read different address in tracked page 0 succeeds");

                    var snapMutate = MemoryReadDiagnostics.GetApiSnapshot().Hybrid;
                    check(snapMutate.PageTrackersCreated == 2048,
                          "10f: reading different address in tracked page at capacity does not allocate a new tracker entry");

                    var okPage0After = plan.TryGetTrackedPageAccessCount(maxPageAlignedAddr.ToInt64(), out var countPage0After);
                    check(okPage0After && countPage0After == 2,
                          "10g: existing page 0 tracker was mutated in place at capacity (AccessCount advanced from 1 to 2)");
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
                    MemoryReadDiagnostics.RecordHybridPageTrackerCreated();
                    MemoryReadDiagnostics.RecordHybridPageBitmapMaterialized();
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
              emptySnap.EntriesCreated == 0 &&
              emptySnap.PageTrackersCreated == 0 &&
              emptySnap.PageBitmapsMaterialized == 0,
              "11: atomic generation reset produces a completely empty new generation state");

        // Record deterministic known set of events into the new generation
        MemoryReadDiagnostics.RecordHybridLogicalRequest(200);
        MemoryReadDiagnostics.RecordHybridExactRead(64);
        MemoryReadDiagnostics.RecordHybridPagePromotion(4096);
        MemoryReadDiagnostics.RecordHybridPageTrackerCreated();
        MemoryReadDiagnostics.RecordHybridPageBitmapMaterialized();

        // Verify from the actual stored byte counters and promotion invariants
        var deterministicSnap = MemoryReadDiagnostics.GetApiSnapshot().Hybrid;
        check(deterministicSnap.ExactFetchedBytes == 64, "exact fetched bytes match recorded size");
        check(deterministicSnap.PageFetchedBytes == 4096 && deterministicSnap.PageFetchedBytes == deterministicSnap.PagePromotions * 4096, "12: page fetched bytes equal page promotions * 4096");
        check(deterministicSnap.FetchedBytes == deterministicSnap.ExactFetchedBytes + deterministicSnap.PageFetchedBytes, "13: total hybrid fetched bytes equal sum of actual level fetched bytes");
        check(deterministicSnap.EntriesCreated == deterministicSnap.ExactReads + deterministicSnap.PagePromotions, "entries created equals sum of exact reads and promotions in isolated generation");
        check(deterministicSnap.PageTrackersCreated == 1, "trackers created matches recorded count");
        check(deterministicSnap.PageBitmapsMaterialized == 1, "bitmaps materialized matches recorded count");

        MemoryReadDiagnostics.ResetForCapture();

        // =========================================================================
        // Animated Component Optimization & Lifecycle Tests
        // =========================================================================
        var animComponent = new TEHhub.RemoteObjects.Components.Animated(IntPtr.Zero);
        check(!animComponent.RequiresPerFrameRefresh, "Animated component opts out of per-frame refresh (RequiresPerFrameRefresh == false)");

        var diesAfterTimeComponent = new TEHhub.RemoteObjects.Components.DiesAfterTime(IntPtr.Zero);
        check(!diesAfterTimeComponent.RequiresPerFrameRefresh, "DiesAfterTime component opts out of per-frame refresh");

        var lifeComponent = new TEHhub.RemoteObjects.Components.Life(IntPtr.Zero);
        check(lifeComponent.RequiresPerFrameRefresh, "Life component retains per-frame refresh (RequiresPerFrameRefresh == true)");

        var testEntity = new TEHhub.RemoteObjects.States.InGameStateObjects.Entity();
        var snapshotAddrs = new List<IntPtr>();
        testEntity.AppendFrameSnapshotAddresses(snapshotAddrs);
        check(snapshotAddrs.Count == 0, "Empty entity snapshot addresses is empty");

        // Verify Animated initial read & address-change update via synthetic memory
        var synthAnimBlock = Marshal.AllocHGlobal(8192);
        try
        {
            Marshal.Copy(new byte[8192], 0, synthAnimBlock, 8192);

            var anim1Ptr = synthAnimBlock + 0x0000;
            var anim2Ptr = synthAnimBlock + 0x0500;
            var ent1Ptr = synthAnimBlock + 0x0A00;
            var ent2Ptr = synthAnimBlock + 0x0C00;
            var det1Ptr = synthAnimBlock + 0x0E00;
            var det2Ptr = synthAnimBlock + 0x1000;
            var modelInfo1Ptr = synthAnimBlock + 0x1200;
            var modelInfo2Ptr = synthAnimBlock + 0x1300;
            var fileRec1Ptr = synthAnimBlock + 0x1400;
            var fileRec2Ptr = synthAnimBlock + 0x1500;
            var heapStr1 = synthAnimBlock + 0x1600;
            var heapStr2 = synthAnimBlock + 0x1700;
            var heapStr3 = synthAnimBlock + 0x1800;
            var heapStr4 = synthAnimBlock + 0x1900;
            var owner1 = synthAnimBlock + 0x1E00;
            var owner2 = synthAnimBlock + 0x1F00;

            static void WriteStdWString(IntPtr dest, string text, IntPtr heapBuffer)
            {
                var bytes = System.Text.Encoding.Unicode.GetBytes(text);
                Marshal.Copy(bytes, 0, heapBuffer, bytes.Length);
                var wstr = new TEHhub.Offsets.Natives.StdWString
                {
                    Buffer = heapBuffer,
                    ReservedBytes = IntPtr.Zero,
                    Length = text.Length,
                    Capacity = text.Length,
                };
                Marshal.StructureToPtr(wstr, dest, false);
            }

            // EntityDetails 1 & 2 (StdWString at +0x08)
            WriteStdWString(det1Ptr + 0x08, "Metadata/Terrain/Doodads/FlagA", heapStr1);
            WriteStdWString(det2Ptr + 0x08, "Metadata/Terrain/Doodads/FlagB", heapStr2);

            // EntityOffsets 1 & 2 (ItemBase.EntityDetailsPtr at +0x08, Id at +0x88)
            Marshal.WriteIntPtr(ent1Ptr + 0x08, det1Ptr);
            Marshal.WriteInt32(ent1Ptr + 0x88, 12345);
            Marshal.WriteIntPtr(ent2Ptr + 0x08, det2Ptr);
            Marshal.WriteInt32(ent2Ptr + 0x88, 67890);

            // FileInfoValueStruct 1 & 2 (StdWString Name at +0x08)
            WriteStdWString(fileRec1Ptr + 0x08, "Metadata/Models/flag_a.ao@111", heapStr3);
            WriteStdWString(fileRec2Ptr + 0x08, "Metadata/Models/flag_b.ao@222", heapStr4);

            // AnimatedModelInfoOffsets 1 & 2 (ModelFileRecordPtr at +0x18)
            Marshal.WriteIntPtr(modelInfo1Ptr + 0x18, fileRec1Ptr);
            Marshal.WriteIntPtr(modelInfo2Ptr + 0x18, fileRec2Ptr);

            // AnimatedOffsets 1 (Header.EntityPtr at +0x08, AnimatedEntityPtr at +0x0280, ModelInfoPtr at +0x0358)
            Marshal.WriteIntPtr(anim1Ptr + 0x08, owner1);
            Marshal.WriteIntPtr(anim1Ptr + 0x0280, ent1Ptr);
            Marshal.WriteIntPtr(anim1Ptr + 0x0358, modelInfo1Ptr);

            // AnimatedOffsets 2
            Marshal.WriteIntPtr(anim2Ptr + 0x08, owner2);
            Marshal.WriteIntPtr(anim2Ptr + 0x0280, ent2Ptr);
            Marshal.WriteIntPtr(anim2Ptr + 0x0358, modelInfo2Ptr);

            // 1. Test initial data read
            var animated = new TEHhub.RemoteObjects.Components.Animated(anim1Ptr);
            check(animated.Path == "Metadata/Terrain/Doodads/FlagA", "Animated initial read resolves Path correctly");
            check(animated.Id == 12345, "Animated initial read resolves Id correctly");
            check(animated.ModelPath == "Metadata/Models/flag_a.ao", "Animated initial read resolves ModelPath correctly");
            check(animated.IsParentValid(owner1), "Animated initial read validates parent owner correctly");

            // 2. Test steady-state no-op: calling RefreshDataNow on unchanged address does not alter state
            animated.RefreshDataNow();
            check(animated.Path == "Metadata/Terrain/Doodads/FlagA" && animated.Id == 12345, "Steady-state refresh retains valid populated state without corruption");

            // 3. Test address-change update
            animated.Address = anim2Ptr;
            check(animated.Path == "Metadata/Terrain/Doodads/FlagB", "Animated address change updates Path correctly");
            check(animated.Id == 67890, "Animated address change updates Id correctly");
            check(animated.ModelPath == "Metadata/Models/flag_b.ao", "Animated address change updates ModelPath correctly");
            check(animated.IsParentValid(owner2), "Animated address change validates new parent owner correctly");
        }
        finally
        {
            Marshal.FreeHGlobal(synthAnimBlock);
        }

        // =========================================================================
        // Buffs Component Zero-Allocation & Refresh Correctness Tests
        // =========================================================================
        TEHhub.RemoteObjects.Components.Buffs.ClearStaticCaches();
        var synthBuffsBlock = Marshal.AllocHGlobal(65536);
        try
        {
            Marshal.Copy(new byte[65536], 0, synthBuffsBlock, 65536);

            var buffsComponentPtr = synthBuffsBlock + 0x0000;
            var ptrArray1 = synthBuffsBlock + 0x0300; // Array of IntPtrs to StatusEffects (2 buffs)
            var ptrArray2 = synthBuffsBlock + 0x0400; // Array of IntPtrs for single buff
            var se1Ptr = synthBuffsBlock + 0x0600;    // StatusEffect 1: grace_period
            var se2Ptr = synthBuffsBlock + 0x0700;    // StatusEffect 2: quick
            var buffDef1 = synthBuffsBlock + 0x0A00;  // BuffDefinition 1
            var buffDef2 = synthBuffsBlock + 0x0B00;  // BuffDefinition 2
            var buffDef3 = synthBuffsBlock + 0x0C00;  // BuffDefinition 3 (Flask)
            var buffDef4 = synthBuffsBlock + 0x0D00;  // BuffDefinition 4 (Alternative grace_period)
            var nameStr1 = synthBuffsBlock + 0x0E00;  // Unicode "grace_period\0"
            var nameStr2 = synthBuffsBlock + 0x0F00;  // Unicode "quick\0"
            var nameStr3 = synthBuffsBlock + 0x1000;  // Unicode "flask_quick\0"

            static void WriteUnicodeZ(IntPtr dest, string text)
            {
                var bytes = System.Text.Encoding.Unicode.GetBytes(text + "\0");
                Marshal.Copy(bytes, 0, dest, bytes.Length);
            }

            WriteUnicodeZ(nameStr1, "grace_period");
            WriteUnicodeZ(nameStr2, "quick");
            WriteUnicodeZ(nameStr3, "flask_quick");

            // BuffDefinitions 1, 2, 3 & 4 (NamePtr at +0x00, BuffType at +0x67)
            Marshal.WriteIntPtr(buffDef1 + 0x00, nameStr1);
            Marshal.WriteByte(buffDef1 + 0x67, 0); // BuffType = 0
            Marshal.WriteIntPtr(buffDef2 + 0x00, nameStr2);
            Marshal.WriteByte(buffDef2 + 0x67, 0);
            Marshal.WriteIntPtr(buffDef3 + 0x00, nameStr3);
            Marshal.WriteByte(buffDef3 + 0x67, 4); // BuffType = 4 (Flask)
            Marshal.WriteIntPtr(buffDef4 + 0x00, nameStr1); // Same name "grace_period", different address
            Marshal.WriteByte(buffDef4 + 0x67, 0);

            // StatusEffectStruct 1 ("grace_period")
            Marshal.WriteIntPtr(se1Ptr + 0x08, buffDef1);
            Marshal.StructureToPtr(10.0f, se1Ptr + 0x18, false); // TotalTime
            Marshal.StructureToPtr(8.5f, se1Ptr + 0x1C, false);  // TimeLeft
            Marshal.WriteInt32(se1Ptr + 0x28, 100);              // SourceEntityId = 100
            Marshal.WriteInt32(se1Ptr + 0x2C, 0);                // RawStage = 0
            Marshal.WriteInt16(se1Ptr + 0x40, 1);                // Charges = 1
            Marshal.WriteInt16(se1Ptr + 0x42, -1);               // FlaskSlot = -1
            Marshal.WriteInt16(se1Ptr + 0x48, 0);                // Effectiveness = 0
            Marshal.WriteInt32(se1Ptr + 0x4A, 0);                // UnknownIdAndEquipmentInfo = 0

            // StatusEffectStruct 2 ("quick")
            Marshal.WriteIntPtr(se2Ptr + 0x08, buffDef2);
            Marshal.StructureToPtr(5.0f, se2Ptr + 0x18, false); // TotalTime
            Marshal.StructureToPtr(3.0f, se2Ptr + 0x1C, false); // TimeLeft
            Marshal.WriteInt32(se2Ptr + 0x28, 100);             // SourceEntityId = 100
            Marshal.WriteInt32(se2Ptr + 0x2C, 0);
            Marshal.WriteInt16(se2Ptr + 0x40, 1);                // Charges = 1
            Marshal.WriteInt16(se2Ptr + 0x42, -1);
            Marshal.WriteInt16(se2Ptr + 0x48, 0);
            Marshal.WriteInt32(se2Ptr + 0x4A, 0);

            // ptrArray1: holds [se1Ptr, se2Ptr]
            Marshal.WriteIntPtr(ptrArray1 + 0, se1Ptr);
            Marshal.WriteIntPtr(ptrArray1 + IntPtr.Size, se2Ptr);

            // ptrArray2: holds [se1Ptr]
            Marshal.WriteIntPtr(ptrArray2 + 0, se1Ptr);

            // BuffsOffsets StatusEffectPtr at +0x160 (StdVector: First, Last, End)
            var vec2 = new TEHhub.Offsets.Natives.StdVector
            {
                First = ptrArray1,
                Last = ptrArray1 + (2 * IntPtr.Size),
                End = ptrArray1 + (2 * IntPtr.Size),
            };
            Marshal.StructureToPtr(vec2, buffsComponentPtr + 0x160, false);

            var buffs = new TEHhub.RemoteObjects.Components.Buffs(buffsComponentPtr);

            // 1. Initial refresh with 2 buffs
            buffs.RefreshDataNow();
            check(buffs.FastStatusEffects.ContainsKey("grace_period"), "New buff 'grace_period' appears correctly");
            check(buffs.FastStatusEffects.ContainsKey("quick"), "New buff 'quick' appears correctly");
            check(buffs.FastStatusEffects.Count == 2, "FastStatusEffects count matches active count (2)");
            check(Math.Abs(buffs.FastStatusEffects["grace_period"].TimeLeft - 8.5f) < 0.01f, "StatusEffect TimeLeft parsed accurately");

            // 2. Comprehensive field-by-field mutation tests (verifies no stale data in StatusEffectStruct)
            // 2a. TotalTime mutation
            Marshal.StructureToPtr(25.0f, se1Ptr + 0x18, false);
            buffs.RefreshDataNow();
            check(Math.Abs(buffs.FastStatusEffects["grace_period"].TotalTime - 25.0f) < 0.01f, "StatusEffectStruct.TotalTime updated correctly");

            // 2b. TimeLeft mutation
            Marshal.StructureToPtr(4.2f, se1Ptr + 0x1C, false);
            buffs.RefreshDataNow();
            check(Math.Abs(buffs.FastStatusEffects["grace_period"].TimeLeft - 4.2f) < 0.01f, "StatusEffectStruct.TimeLeft updated correctly");

            // 2c. SourceEntityId mutation
            Marshal.WriteInt32(se1Ptr + 0x28, 99999);
            buffs.RefreshDataNow();
            check(buffs.FastStatusEffects["grace_period"].SourceEntityId == 99999, "StatusEffectStruct.SourceEntityId updated correctly");

            // 2d. RawStage mutation
            Marshal.WriteInt32(se1Ptr + 0x2C, 7);
            buffs.RefreshDataNow();
            check(buffs.FastStatusEffects["grace_period"].RawStage == 7, "StatusEffectStruct.RawStage updated correctly");

            // 2e. Charges mutation
            Marshal.WriteInt16(se1Ptr + 0x40, 5);
            buffs.RefreshDataNow();
            check(buffs.FastStatusEffects["grace_period"].Charges == 5, "StatusEffectStruct.Charges updated correctly");

            // 2f. Effectiveness mutation
            Marshal.WriteInt16(se1Ptr + 0x48, 65);
            buffs.RefreshDataNow();
            check(buffs.FastStatusEffects["grace_period"].Effectiveness == 65, "StatusEffectStruct.Effectiveness updated correctly");

            // 2g. UnknownIdAndEquipmentInfo mutation (equipment info bits changed, skillGemId kept 0)
            Marshal.WriteInt32(se1Ptr + 0x4A, 0x00001234);
            buffs.RefreshDataNow();
            check(buffs.FastStatusEffects["grace_period"].UnknownIdAndEquipmentInfo == 0x00001234, "StatusEffectStruct.UnknownIdAndEquipmentInfo updated correctly");

            // 2h. BuffDefinationPtr mutation
            Marshal.WriteIntPtr(se1Ptr + 0x08, buffDef4);
            buffs.RefreshDataNow();
            check(buffs.FastStatusEffects["grace_period"].BuffDefinationPtr == buffDef4, "StatusEffectStruct.BuffDefinationPtr updated correctly");

            // Restore se1 fields to clean test baseline
            Marshal.WriteIntPtr(se1Ptr + 0x08, buffDef1);
            Marshal.StructureToPtr(10.0f, se1Ptr + 0x18, false);
            Marshal.StructureToPtr(8.5f, se1Ptr + 0x1C, false);
            Marshal.WriteInt32(se1Ptr + 0x28, 100);
            Marshal.WriteInt32(se1Ptr + 0x2C, 0);
            Marshal.WriteInt16(se1Ptr + 0x40, 1);
            Marshal.WriteInt16(se1Ptr + 0x48, 0);
            Marshal.WriteInt32(se1Ptr + 0x4A, 0);
            buffs.RefreshDataNow();

            // 3. Removed buff disappears correctly on next update
            var vec1 = new TEHhub.Offsets.Natives.StdVector
            {
                First = ptrArray2,
                Last = ptrArray2 + IntPtr.Size,
                End = ptrArray2 + IntPtr.Size,
            };
            Marshal.StructureToPtr(vec1, buffsComponentPtr + 0x160, false);
            buffs.RefreshDataNow();
            check(buffs.FastStatusEffects.ContainsKey("grace_period"), "Remaining buff persists");
            check(!buffs.FastStatusEffects.ContainsKey("quick"), "Removed buff 'quick' disappears correctly on next update");
            check(buffs.FastStatusEffects.Count == 1, "FastStatusEffects count drops to 1");

            // 4. Empty buff vector clears previous state correctly
            var vec0 = new TEHhub.Offsets.Natives.StdVector
            {
                First = ptrArray1,
                Last = ptrArray1,
                End = ptrArray1,
            };
            Marshal.StructureToPtr(vec0, buffsComponentPtr + 0x160, false);
            buffs.RefreshDataNow();
            check(buffs.FastStatusEffects.IsEmpty, "Empty buff vector clears previous state correctly");
            check(buffs.FastStatusEffects.Count == 0, "FastStatusEffects count is 0 when empty");

            // 5. Multiple consecutive refreshes do not retain stale entries
            Marshal.StructureToPtr(vec2, buffsComponentPtr + 0x160, false);
            for (var r = 0; r < 5; r++)
            {
                buffs.RefreshDataNow();
            }
            check(buffs.FastStatusEffects.Count == 2 && buffs.FastStatusEffects.ContainsKey("grace_period") && buffs.FastStatusEffects.ContainsKey("quick"), "Multiple consecutive refreshes maintain correct state");

            // 6. Skill gem buff key formatting and flask slot tracking
            Marshal.WriteIntPtr(se2Ptr + 0x08, buffDef3);
            Marshal.WriteInt32(se2Ptr + 0x28, 0);          // SourceEntityId matches playerId (0 in test environment)
            Marshal.WriteInt16(se2Ptr + 0x42, 2);          // FlaskSlot = 2
            Marshal.WriteInt32(se2Ptr + 0x4A, 0x00AB0000); // SkillGemUnknownId = 0xAB
            buffs.RefreshDataNow();
            check(buffs.FastStatusEffects.ContainsKey("flask_quick_AB"), "Skill gem buff produces identical formatted key 'flask_quick_AB'");
            check(!buffs.FastStatusEffects.ContainsKey("quick"), "Old buff replaced by 'flask_quick_AB'");
            check(buffs.FlaskActive[2] == true, "FlaskActive[2] is set for flask type buff");
            check(buffs.FastStatusEffects["flask_quick_AB"].FlaskSlot == 2, "StatusEffectStruct.FlaskSlot stored correctly in FastStatusEffects");

            // 6b. Explicit FlaskSlot mutation test
            Marshal.WriteInt16(se2Ptr + 0x42, 3); // Mutate FlaskSlot from 2 to 3
            buffs.RefreshDataNow();
            check(buffs.FastStatusEffects["flask_quick_AB"].FlaskSlot == 3, "StatusEffectStruct.FlaskSlot updated correctly on mutation");
            check(buffs.FlaskActive[3] == true && buffs.FlaskActive[2] == false, "FlaskActive updated to slot 3 on mutation");

            // 7. Duplicate buffs stacking: charges are merged and max TimeLeft is preserved
            Marshal.WriteIntPtr(se2Ptr + 0x08, buffDef1); // Both se1 and se2 point to buffDef1 ("grace_period")
            Marshal.WriteInt32(se2Ptr + 0x4A, 0);         // No skill gem suffix
            Marshal.StructureToPtr(3.0f, se1Ptr + 0x1C, false);
            Marshal.WriteInt16(se1Ptr + 0x40, 2); // Charges = 2
            Marshal.StructureToPtr(7.5f, se2Ptr + 0x1C, false);
            Marshal.WriteInt16(se2Ptr + 0x40, 3); // Charges = 3
            buffs.RefreshDataNow();
            check(buffs.FastStatusEffects.Count == 1, "Duplicate buffs merged to single key 'grace_period'");
            check(buffs.FastStatusEffects["grace_period"].Charges == 5, "Duplicate buff charges summed (2 + 3 = 5)");
            check(Math.Abs(buffs.FastStatusEffects["grace_period"].TimeLeft - 7.5f) < 0.01f, "Duplicate buff preserves max TimeLeft (7.5f)");

            // Restore se2 for zero-allocation test
            Marshal.WriteIntPtr(se2Ptr + 0x08, buffDef2);
            Marshal.StructureToPtr(5.0f, se2Ptr + 0x18, false);
            Marshal.StructureToPtr(3.0f, se2Ptr + 0x1C, false);
            Marshal.WriteInt16(se2Ptr + 0x40, 1);
            Marshal.WriteInt16(se2Ptr + 0x42, -1);
            Marshal.WriteInt32(se2Ptr + 0x4A, 0);
            buffs.RefreshDataNow();

            // 8. Zero allocation in steady-state test
            buffs.RefreshDataNow();
            buffs.RefreshDataNow();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            buffs.RefreshDataNow(); // Warm up GC thread-local allocation context after full collection

            var allocSingleBefore = GC.GetAllocatedBytesForCurrentThread();
            buffs.RefreshDataNow();
            var allocSingle = GC.GetAllocatedBytesForCurrentThread() - allocSingleBefore;
            check(allocSingle <= 8, $"Buffs.UpdateData steady-state single call allocation is <= 8 bytes (measured: {allocSingle} B)");

            var allocBefore = GC.GetAllocatedBytesForCurrentThread();
            for (var r = 0; r < 100; r++)
            {
                buffs.RefreshDataNow();
            }
            var allocAfter = GC.GetAllocatedBytesForCurrentThread();
            var allocPerCall = (allocAfter - allocBefore) / 100;
            check(allocPerCall <= 8, $"Buffs.UpdateData steady-state average allocation is <= 8 bytes (measured: {allocPerCall} B/call)");

            // 8b. Deterministic Fast vs Legacy Live Reference Compatibility Tests
            var liveTestBuffs = new TEHhub.RemoteObjects.Components.Buffs(buffsComponentPtr);
            var legacyField = typeof(TEHhub.RemoteObjects.Components.Buffs).GetField("legacyStatusEffects", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            check(legacyField != null, "0a: legacyStatusEffects private field located via reflection");
            check(legacyField!.GetValue(liveTestBuffs) == null, "0b: legacyStatusEffects is genuinely null upon Buffs creation");

            Marshal.StructureToPtr(vec2, buffsComponentPtr + 0x160, false);
            Marshal.StructureToPtr(8.5f, se1Ptr + 0x1C, false);
            Marshal.WriteIntPtr(se2Ptr + 0x08, buffDef2);
            Marshal.WriteInt32(se2Ptr + 0x4A, 0);
            Marshal.WriteInt16(se1Ptr + 0x40, 1);
            Marshal.WriteInt16(se2Ptr + 0x40, 1);
            liveTestBuffs.RefreshDataNow();
            liveTestBuffs.RefreshDataNow();
            check(legacyField.GetValue(liveTestBuffs) == null, "0c: legacyStatusEffects remains null across multiple RefreshDataNow calls before getter access");

            // 0d. Before activation: zero legacy synchronization overhead in fast path
            var unactivatedAllocBefore = GC.GetAllocatedBytesForCurrentThread();
            for (var r = 0; r < 50; r++)
            {
                liveTestBuffs.RefreshDataNow();
                var c = liveTestBuffs.FastStatusEffects.Count;
            }
            var unactivatedAlloc = GC.GetAllocatedBytesForCurrentThread() - unactivatedAllocBefore;
            check(unactivatedAlloc <= 8, $"0d: Before activation, fast path has 0 legacy sync overhead ({unactivatedAlloc} B)");

            // Cache legacy reference on first access
            var cachedLegacyReference = liveTestBuffs.StatusEffects;
            var activatedFieldValue = legacyField.GetValue(liveTestBuffs);
            check(activatedFieldValue != null, "0e: legacyStatusEffects is non-null after first StatusEffects getter access");
            check(object.ReferenceEquals(cachedLegacyReference, activatedFieldValue), "0f: StatusEffects getter returned exactly the allocated private field instance");
            check(cachedLegacyReference.Count == 2 && cachedLegacyReference.ContainsKey("grace_period") && cachedLegacyReference.ContainsKey("quick"), "Cached legacy reference populated on activation");

            // 1. TimeLeft changes (WITHOUT touching liveTestBuffs.StatusEffects getter)
            Marshal.StructureToPtr(3.14f, se1Ptr + 0x1C, false);
            liveTestBuffs.RefreshDataNow();
            check(object.ReferenceEquals(cachedLegacyReference, liveTestBuffs.StatusEffects), "1a: ReferenceEquals holds after refresh");
            check(Math.Abs(cachedLegacyReference["grace_period"].TimeLeft - 3.14f) < 0.01f, "1b: Cached legacy reference observes TimeLeft change without re-calling getter");

            // 2 & 3. Buff disappears and new buff appears (WITHOUT touching getter)
            Marshal.StructureToPtr(vec1, buffsComponentPtr + 0x160, false);
            liveTestBuffs.RefreshDataNow();
            check(cachedLegacyReference.Count == 1 && !cachedLegacyReference.ContainsKey("quick"), "3: Cached legacy reference observes buff disappearance without re-calling getter");
            Marshal.StructureToPtr(vec2, buffsComponentPtr + 0x160, false);
            liveTestBuffs.RefreshDataNow();
            check(cachedLegacyReference.Count == 2 && cachedLegacyReference.ContainsKey("quick"), "2: Cached legacy reference observes new buff appearance without re-calling getter");

            // 4. Empty vector clears it (WITHOUT touching getter)
            Marshal.StructureToPtr(vec0, buffsComponentPtr + 0x160, false);
            liveTestBuffs.RefreshDataNow();
            check(cachedLegacyReference.IsEmpty && cachedLegacyReference.Count == 0, "4: Cached legacy reference is cleared on empty vector without re-calling getter");

            // 5. Duplicate stack merge (WITHOUT touching getter)
            Marshal.StructureToPtr(vec2, buffsComponentPtr + 0x160, false);
            Marshal.WriteIntPtr(se2Ptr + 0x08, buffDef1);
            Marshal.WriteInt32(se2Ptr + 0x4A, 0);
            Marshal.WriteInt16(se1Ptr + 0x40, 2);
            Marshal.WriteInt16(se2Ptr + 0x40, 3);
            liveTestBuffs.RefreshDataNow();
            check(cachedLegacyReference.Count == 1 && cachedLegacyReference["grace_period"].Charges == 5, "5: Cached legacy reference reflects duplicate stack merge (2+3=5) without re-calling getter");

            // 6. AddSyntheticStatusEffect changes visible through cached legacy reference (WITHOUT touching getter)
            var synthStruct = new TEHhub.Offsets.Objects.Components.StatusEffectStruct
            {
                Charges = 10,
                TotalTime = 60f,
                TimeLeft = 45f,
                BuffDefinationPtr = buffDef1
            };
            liveTestBuffs.AddSyntheticStatusEffect("synthetic_aura", synthStruct);
            check(cachedLegacyReference.ContainsKey("synthetic_aura") && cachedLegacyReference["synthetic_aura"].Charges == 10, "6: AddSyntheticStatusEffect is immediately visible in cached legacy reference without re-calling getter");

            // 7. ReferenceEquals(firstGetterResult, laterGetterResult) remains true
            var laterGetterResult = liveTestBuffs.StatusEffects;
            check(object.ReferenceEquals(cachedLegacyReference, laterGetterResult), "7: ReferenceEquals(firstGetterResult, laterGetterResult) is true for component lifetime");

            // 9. Comprehensive Apples-to-Apples Microbenchmark: OLD vs PROPOSED across 2, 10, 25, 50 Buffs
            // Workloads:
            //   A. Unchanged values (steady state)
            //   B. TimeLeft changes every refresh
            //   C. One buff removed / replaced periodically
            //   D. Duplicate buff names requiring stack merge
            const int BenchIterations = 1000;
            int[] testBuffCounts = [2, 10, 25, 50];

            Console.WriteLine("\n=========================================================================================");
            Console.WriteLine("                BUFFS REFRESH PIPELINE BENCHMARK: OLD vs PROPOSED");
            Console.WriteLine("=========================================================================================");

            foreach (var count in testBuffCounts)
            {
                var keys = new string[count];
                var altKeys = new string[count];
                var dupKeys = new string[count];
                var structs = new TEHhub.Offsets.Objects.Components.StatusEffectStruct[count];

                for (var i = 0; i < count; i++)
                {
                    keys[i] = string.Intern($"buff_effect_{i}");
                    altKeys[i] = string.Intern($"buff_effect_alt_{i}");
                    dupKeys[i] = string.Intern($"buff_effect_{i / 2}"); // 50% duplicate keys for stack merge workload
                    structs[i] = new TEHhub.Offsets.Objects.Components.StatusEffectStruct
                    {
                        BuffDefinationPtr = new IntPtr(0x1000 + (i * 0x100)),
                        TotalTime = 30.0f,
                        TimeLeft = 15.0f,
                        SourceEntityId = 0,
                        RawStage = (uint)i,
                        Charges = 1,
                        FlaskSlot = -1,
                        Effectiveness = 0,
                        UnknownIdAndEquipmentInfo = 0,
                    };
                }

                // Helper delegates for benchmark
                static void RunOld(
                    System.Collections.Concurrent.ConcurrentDictionary<string, TEHhub.Offsets.Objects.Components.StatusEffectStruct> dict,
                    string[] kList,
                    TEHhub.Offsets.Objects.Components.StatusEffectStruct[] sList,
                    int n)
                {
                    dict.Clear();
                    for (var b = 0; b < n; b++)
                    {
                        dict.AddOrUpdate(
                            kList[b],
                            static (_, incoming) => incoming,
                            static (_, oldValue, incoming) =>
                            {
                                var incomingStacks = incoming.Charges > 0 ? incoming.Charges : (short)1;
                                incoming.Charges = (short)(oldValue.Charges + incomingStacks);
                                incoming.TimeLeft = Math.Max(oldValue.TimeLeft, incoming.TimeLeft);
                                return incoming;
                            },
                            sList[b]);
                    }
                }

                static void RunProposed(
                    Dictionary<string, TEHhub.Offsets.Objects.Components.StatusEffectStruct> dict,
                    string[] kList,
                    TEHhub.Offsets.Objects.Components.StatusEffectStruct[] sList,
                    int n)
                {
                    dict.Clear();
                    for (var b = 0; b < n; b++)
                    {
                        ref var entry = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(dict, kList[b], out var exists);
                        if (exists)
                        {
                            var incomingStacks = sList[b].Charges > 0 ? sList[b].Charges : (short)1;
                            var s = sList[b];
                            s.Charges = (short)(entry.Charges + incomingStacks);
                            s.TimeLeft = Math.Max(entry.TimeLeft, s.TimeLeft);
                            entry = s;
                        }
                        else
                        {
                            entry = sList[b];
                        }
                    }
                }

                static void SyncLegacy(
                    System.Collections.Concurrent.ConcurrentDictionary<string, TEHhub.Offsets.Objects.Components.StatusEffectStruct> target,
                    Dictionary<string, TEHhub.Offsets.Objects.Components.StatusEffectStruct> source)
                {
                    foreach (var kv in source)
                    {
                        target[kv.Key] = kv.Value;
                    }

                    if (target.Count != source.Count)
                    {
                        foreach (var key in target.Keys)
                        {
                            if (!source.ContainsKey(key))
                            {
                                target.TryRemove(key, out _);
                            }
                        }
                    }
                }

                var oldDict = new System.Collections.Concurrent.ConcurrentDictionary<string, TEHhub.Offsets.Objects.Components.StatusEffectStruct>();
                var propDict = new Dictionary<string, TEHhub.Offsets.Objects.Components.StatusEffectStruct>(count, StringComparer.Ordinal);
                var legacyCompatDict = new System.Collections.Concurrent.ConcurrentDictionary<string, TEHhub.Offsets.Objects.Components.StatusEffectStruct>(StringComparer.Ordinal);

                // --- WORKLOAD A: Unchanged Steady State ---
                for (var w = 0; w < 50; w++) { RunOld(oldDict, keys, structs, count); RunProposed(propDict, keys, structs, count); }
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                var aOldBefore = GC.GetAllocatedBytesForCurrentThread();
                var swAOld = Stopwatch.StartNew();
                for (var i = 0; i < BenchIterations; i++) RunOld(oldDict, keys, structs, count);
                swAOld.Stop();
                var aOldAlloc = (GC.GetAllocatedBytesForCurrentThread() - aOldBefore) / BenchIterations;
                var aOldNs = swAOld.Elapsed.TotalNanoseconds / BenchIterations;

                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                var aPropBefore = GC.GetAllocatedBytesForCurrentThread();
                var swAProp = Stopwatch.StartNew();
                for (var i = 0; i < BenchIterations; i++) RunProposed(propDict, keys, structs, count);
                swAProp.Stop();
                var aPropAlloc = (GC.GetAllocatedBytesForCurrentThread() - aPropBefore) / BenchIterations;
                var aPropNs = swAProp.Elapsed.TotalNanoseconds / BenchIterations;

                // --- WORKLOAD B1: Single Buff TimeLeft Changes Every Iteration ---
                for (var w = 0; w < 50; w++) { RunOld(oldDict, keys, structs, count); RunProposed(propDict, keys, structs, count); }
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                var b1OldBefore = GC.GetAllocatedBytesForCurrentThread();
                var swB1Old = Stopwatch.StartNew();
                for (var i = 0; i < BenchIterations; i++)
                {
                    structs[0].TimeLeft = 15.0f - (i * 0.01f);
                    RunOld(oldDict, keys, structs, count);
                }
                swB1Old.Stop();
                var b1OldAlloc = (GC.GetAllocatedBytesForCurrentThread() - b1OldBefore) / BenchIterations;
                var b1OldNs = swB1Old.Elapsed.TotalNanoseconds / BenchIterations;

                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                var b1PropBefore = GC.GetAllocatedBytesForCurrentThread();
                var swB1Prop = Stopwatch.StartNew();
                for (var i = 0; i < BenchIterations; i++)
                {
                    structs[0].TimeLeft = 15.0f - (i * 0.01f);
                    RunProposed(propDict, keys, structs, count);
                }
                swB1Prop.Stop();
                var b1PropAlloc = (GC.GetAllocatedBytesForCurrentThread() - b1PropBefore) / BenchIterations;
                var b1PropNs = swB1Prop.Elapsed.TotalNanoseconds / BenchIterations;

                // --- WORKLOAD B2: ALL Active Buffs Change TimeLeft Every Iteration ---
                for (var w = 0; w < 50; w++) { RunOld(oldDict, keys, structs, count); RunProposed(propDict, keys, structs, count); }
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                var b2OldBefore = GC.GetAllocatedBytesForCurrentThread();
                var swB2Old = Stopwatch.StartNew();
                for (var i = 0; i < BenchIterations; i++)
                {
                    for (var b = 0; b < count; b++)
                    {
                        structs[b].TimeLeft = 15.0f - ((i + b) * 0.01f);
                    }
                    RunOld(oldDict, keys, structs, count);
                }
                swB2Old.Stop();
                var b2OldAlloc = (GC.GetAllocatedBytesForCurrentThread() - b2OldBefore) / BenchIterations;
                var b2OldNs = swB2Old.Elapsed.TotalNanoseconds / BenchIterations;

                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                var b2PropBefore = GC.GetAllocatedBytesForCurrentThread();
                var swB2Prop = Stopwatch.StartNew();
                for (var i = 0; i < BenchIterations; i++)
                {
                    for (var b = 0; b < count; b++)
                    {
                        structs[b].TimeLeft = 15.0f - ((i + b) * 0.01f);
                    }
                    RunProposed(propDict, keys, structs, count);
                }
                swB2Prop.Stop();
                var b2PropAlloc = (GC.GetAllocatedBytesForCurrentThread() - b2PropBefore) / BenchIterations;
                var b2PropNs = swB2Prop.Elapsed.TotalNanoseconds / BenchIterations;

                // --- WORKLOAD C: Periodic Buff Replacement (Key Swap) ---
                for (var w = 0; w < 50; w++) { RunOld(oldDict, keys, structs, count); RunProposed(propDict, keys, structs, count); }
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                var cOldBefore = GC.GetAllocatedBytesForCurrentThread();
                var swCOld = Stopwatch.StartNew();
                for (var i = 0; i < BenchIterations; i++)
                {
                    var kList = (i % 10 == 0) ? altKeys : keys;
                    RunOld(oldDict, kList, structs, count);
                }
                swCOld.Stop();
                var cOldAlloc = (GC.GetAllocatedBytesForCurrentThread() - cOldBefore) / BenchIterations;
                var cOldNs = swCOld.Elapsed.TotalNanoseconds / BenchIterations;

                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                var cPropBefore = GC.GetAllocatedBytesForCurrentThread();
                var swCProp = Stopwatch.StartNew();
                for (var i = 0; i < BenchIterations; i++)
                {
                    var kList = (i % 10 == 0) ? altKeys : keys;
                    RunProposed(propDict, kList, structs, count);
                }
                swCProp.Stop();
                var cPropAlloc = (GC.GetAllocatedBytesForCurrentThread() - cPropBefore) / BenchIterations;
                var cPropNs = swCProp.Elapsed.TotalNanoseconds / BenchIterations;

                // --- WORKLOAD D: Duplicate Buff Names (Stack Merge) ---
                for (var w = 0; w < 50; w++) { RunOld(oldDict, dupKeys, structs, count); RunProposed(propDict, dupKeys, structs, count); }
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                var dOldBefore = GC.GetAllocatedBytesForCurrentThread();
                var swDOld = Stopwatch.StartNew();
                for (var i = 0; i < BenchIterations; i++)
                {
                    RunOld(oldDict, dupKeys, structs, count);
                }
                swDOld.Stop();
                var dOldAlloc = (GC.GetAllocatedBytesForCurrentThread() - dOldBefore) / BenchIterations;
                var dOldNs = swDOld.Elapsed.TotalNanoseconds / BenchIterations;

                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                var dPropBefore = GC.GetAllocatedBytesForCurrentThread();
                var swDProp = Stopwatch.StartNew();
                for (var i = 0; i < BenchIterations; i++)
                {
                    RunProposed(propDict, dupKeys, structs, count);
                }
                swDProp.Stop();
                var dPropAlloc = (GC.GetAllocatedBytesForCurrentThread() - dPropBefore) / BenchIterations;
                var dPropNs = swDProp.Elapsed.TotalNanoseconds / BenchIterations;

                // --- DIAGNOSTIC: Legacy Materialization Cost (when external caller requests StatusEffects) ---
                for (var w = 0; w < 50; w++) { SyncLegacy(legacyCompatDict, propDict); }
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                var legacySyncBefore = GC.GetAllocatedBytesForCurrentThread();
                var swLegacySync = Stopwatch.StartNew();
                for (var i = 0; i < BenchIterations; i++)
                {
                    SyncLegacy(legacyCompatDict, propDict);
                }
                swLegacySync.Stop();
                var legacySyncAlloc = (GC.GetAllocatedBytesForCurrentThread() - legacySyncBefore) / BenchIterations;
                var legacySyncNs = swLegacySync.Elapsed.TotalNanoseconds / BenchIterations;

                Console.WriteLine($"\n[Buff Count: {count,2} Buffs | Iterations: {BenchIterations}]");
                Console.WriteLine($"  Workload A  (Unchanged):    OLD = {aOldNs,7:F1} ns ({aOldAlloc,4} B) | PROPOSED = {aPropNs,7:F1} ns ({aPropAlloc,4} B) -> Alloc Saved: {(aOldAlloc - aPropAlloc),4} B ({(aOldAlloc > 0 ? (aOldAlloc - aPropAlloc) * 100.0 / aOldAlloc : 0):F0}%)");
                Console.WriteLine($"  Workload B1 (1 Buff Chg):   OLD = {b1OldNs,7:F1} ns ({b1OldAlloc,4} B) | PROPOSED = {b1PropNs,7:F1} ns ({b1PropAlloc,4} B) -> Alloc Saved: {(b1OldAlloc - b1PropAlloc),4} B ({(b1OldAlloc > 0 ? (b1OldAlloc - b1PropAlloc) * 100.0 / b1OldAlloc : 0):F0}%)");
                Console.WriteLine($"  Workload B2 (ALL Buffs Chg):OLD = {b2OldNs,7:F1} ns ({b2OldAlloc,4} B) | PROPOSED = {b2PropNs,7:F1} ns ({b2PropAlloc,4} B) -> Alloc Saved: {(b2OldAlloc - b2PropAlloc),4} B ({(b2OldAlloc > 0 ? (b2OldAlloc - b2PropAlloc) * 100.0 / b2OldAlloc : 0):F0}%)");
                Console.WriteLine($"  Workload C  (10% Key Swap): OLD = {cOldNs,7:F1} ns ({cOldAlloc,4} B) | PROPOSED = {cPropNs,7:F1} ns ({cPropAlloc,4} B) -> Alloc Saved: {(cOldAlloc - cPropAlloc),4} B ({(cOldAlloc > 0 ? (cOldAlloc - cPropAlloc) * 100.0 / cOldAlloc : 0):F0}%)");
                Console.WriteLine($"  Workload D  (Stack Merge):  OLD = {dOldNs,7:F1} ns ({dOldAlloc,4} B) | PROPOSED = {dPropNs,7:F1} ns ({dPropAlloc,4} B) -> Alloc Saved: {(dOldAlloc - dPropAlloc),4} B ({(dOldAlloc > 0 ? (dOldAlloc - dPropAlloc) * 100.0 / dOldAlloc : 0):F0}%)");
                Console.WriteLine($"  Legacy Snapshot Sync (Diag):{legacySyncNs,7:F1} ns ({legacySyncAlloc,4} B) [Lazy on-demand only, 0 cost on fast path]");
            }
            Console.WriteLine("=========================================================================================\n");
        }
        finally
        {
            Marshal.FreeHGlobal(synthBuffsBlock);
            TEHhub.RemoteObjects.Components.Buffs.ClearStaticCaches();
        }

        // =========================================================================
        // Hierarchical Performance Profiler Invariants & Hierarchy Tests (14 Invariants)
        // =========================================================================
        PerformanceProfiler.Reset();
        Core.GHSettings.ShowPerfProfiler = true;

        // 1. Simple parent -> child hierarchy (A -> B)
        PerformanceProfiler.Reset();
        PerformanceProfiler.StartFrame();
        using (PerformanceProfiler.Measure("Test", "ParentA"))
        {
            using (PerformanceProfiler.Measure("Test", "ChildB"))
            {
            }
        }
        PerformanceProfiler.EndFrame();
        var tree1 = PerformanceProfiler.GetTreeSnapshot();
        check(tree1.Count == 1 && tree1[0].Name == "Test.ParentA", "1a: Single root node created for ParentA");
        check(tree1[0].Children.Count == 1 && tree1[0].Children[0].Name == "Test.ChildB", "1b: ChildB is attached as child of ParentA");

        // 2. Parent -> child -> grandchild hierarchy (A -> B -> C)
        PerformanceProfiler.Reset();
        PerformanceProfiler.StartFrame();
        using (PerformanceProfiler.Measure("Test", "A"))
        {
            using (PerformanceProfiler.Measure("Test", "B"))
            {
                using (PerformanceProfiler.Measure("Test", "C"))
                {
                }
            }
        }
        PerformanceProfiler.EndFrame();
        var tree2 = PerformanceProfiler.GetTreeSnapshot();
        check(tree2.Count == 1 && tree2[0].Name == "Test.A", "2a: Root is A");
        check(tree2[0].Children.Count == 1 && tree2[0].Children[0].Name == "Test.B", "2b: A's child is B");
        check(tree2[0].Children[0].Children.Count == 1 && tree2[0].Children[0].Children[0].Name == "Test.C", "2c: B's child is C (grandchild of A)");

        // 3. Two sibling children (A -> B, A -> C)
        PerformanceProfiler.Reset();
        PerformanceProfiler.StartFrame();
        using (PerformanceProfiler.Measure("Test", "A"))
        {
            using (PerformanceProfiler.Measure("Test", "B")) { }
            using (PerformanceProfiler.Measure("Test", "C")) { }
        }
        PerformanceProfiler.EndFrame();
        var tree3 = PerformanceProfiler.GetTreeSnapshot();
        check(tree3.Count == 1 && tree3[0].Children.Count == 2, "3a: A has exactly 2 children");
        check(tree3[0].Children.Any(c => c.Name == "Test.B") && tree3[0].Children.Any(c => c.Name == "Test.C"), "3b: A has both B and C as sibling children");

        // 4. Same scope name under two different parents (P1 -> CommonChild, P2 -> CommonChild)
        PerformanceProfiler.Reset();
        PerformanceProfiler.StartFrame();
        using (PerformanceProfiler.Measure("Test", "P1"))
        {
            using (PerformanceProfiler.Measure("Test", "CommonChild")) { }
        }
        using (PerformanceProfiler.Measure("Test", "P2"))
        {
            using (PerformanceProfiler.Measure("Test", "CommonChild")) { }
        }
        PerformanceProfiler.EndFrame();
        var tree4 = PerformanceProfiler.GetTreeSnapshot();
        var p1Node = tree4.FirstOrDefault(r => r.Name == "Test.P1");
        var p2Node = tree4.FirstOrDefault(r => r.Name == "Test.P2");
        check(p1Node != null && p2Node != null, "4a: Both P1 and P2 exist as separate root nodes");
        check(p1Node!.Children.Count == 1 && p1Node.Children[0].Name == "Test.CommonChild", "4b: P1 has CommonChild");
        check(p2Node!.Children.Count == 1 && p2Node.Children[0].Name == "Test.CommonChild", "4c: P2 has CommonChild");
        check(p1Node.Children[0].Id != p2Node.Children[0].Id, "4d: CommonChild under P1 and P2 have distinct node identities in tree view");
        var flatSnap4 = PerformanceProfiler.GetApiSnapshot();
        var flatCommon = flatSnap4.Rows.FirstOrDefault(r => r.Name == "Test.CommonChild");
        check(flatCommon != null && flatCommon.Count == 2, "4e: Flat hotspot view aggregates CommonChild calls across different parents");

        // 5. Repeated calls in one frame
        PerformanceProfiler.Reset();
        PerformanceProfiler.StartFrame();
        for (var i = 0; i < 5; i++)
        {
            using (PerformanceProfiler.Measure("Test", "LoopParent"))
            {
                using (PerformanceProfiler.Measure("Test", "LoopChild")) { }
            }
        }
        PerformanceProfiler.EndFrame();
        var tree5 = PerformanceProfiler.GetTreeSnapshot();
        check(tree5.Count == 1 && tree5[0].Count == 5, "5a: Parent has 5 calls in 1 frame");
        check(tree5[0].Children.Count == 1 && tree5[0].Children[0].Count == 5, "5b: Child has 5 calls in 1 frame");
        check(Math.Abs(tree5[0].CallsPerFrame - 5.0) < 0.001, "5c: Parent CallsPerFrame is 5.0");

        // 6. Same hierarchy over multiple frames
        PerformanceProfiler.Reset();
        for (var f = 0; f < 10; f++)
        {
            PerformanceProfiler.StartFrame();
            using (PerformanceProfiler.Measure("Test", "MultiFrameParent"))
            {
                using (PerformanceProfiler.Measure("Test", "MultiFrameChild")) { }
            }
            PerformanceProfiler.EndFrame();
        }
        var tree6 = PerformanceProfiler.GetTreeSnapshot();
        check(PerformanceProfiler.TotalFramesCaptured == 10, "6a: 10 frames recorded");
        check(tree6.Count == 1 && tree6[0].Count == 10 && Math.Abs(tree6[0].CallsPerFrame - 1.0) < 0.001, "6b: Parent has 10 calls over 10 frames (1.0 call/frame)");
        check(tree6[0].Children.Count == 1 && tree6[0].Children[0].Count == 10, "6c: Child has 10 calls over 10 frames");

        // 7. Self time excludes direct child inclusive time exactly within reasonable timer tolerance
        PerformanceProfiler.Reset();
        PerformanceProfiler.StartFrame();
        using (PerformanceProfiler.Measure("Test", "WorkParent"))
        {
            var start = Stopwatch.GetTimestamp();
            while (Stopwatch.GetTimestamp() - start < 1000) { }

            using (PerformanceProfiler.Measure("Test", "WorkChild"))
            {
                var startChild = Stopwatch.GetTimestamp();
                while (Stopwatch.GetTimestamp() - startChild < 2000) { }
            }
        }
        PerformanceProfiler.EndFrame();
        var tree7 = PerformanceProfiler.GetTreeSnapshot();
        var wp = tree7[0];
        var wc = wp.Children[0];
        check(wp.InclusiveAvgCallNs > wc.InclusiveAvgCallNs, "7a: Parent inclusive time exceeds child inclusive time");
        check(wp.SelfAvgCallNs > 0, "7b: Parent has positive self time from its own work");
        check(Math.Abs(wp.InclusiveAvgCallNs - (wp.SelfAvgCallNs + wc.InclusiveAvgCallNs)) < 50.0, "7c: Parent Inclusive equals Parent Self + Child Inclusive (within timer precision)");

        // 8. No double subtraction for grandchildren
        PerformanceProfiler.Reset();
        PerformanceProfiler.StartFrame();
        using (PerformanceProfiler.Measure("Test", "GParent"))
        {
            var s1 = Stopwatch.GetTimestamp(); while (Stopwatch.GetTimestamp() - s1 < 500) { }
            using (PerformanceProfiler.Measure("Test", "GChild"))
            {
                var s2 = Stopwatch.GetTimestamp(); while (Stopwatch.GetTimestamp() - s2 < 500) { }
                using (PerformanceProfiler.Measure("Test", "GGrandChild"))
                {
                    var s3 = Stopwatch.GetTimestamp(); while (Stopwatch.GetTimestamp() - s3 < 500) { }
                }
            }
        }
        PerformanceProfiler.EndFrame();
        var tree8 = PerformanceProfiler.GetTreeSnapshot();
        var gp = tree8[0];
        var gc = gp.Children[0];
        var ggc = gc.Children[0];
        var sumSelf = gp.SelfAvgCallNs + gc.SelfAvgCallNs + ggc.SelfAvgCallNs;
        check(Math.Abs(gp.InclusiveAvgCallNs - sumSelf) < 50.0, "8: Sum of all self times (gp + gc + ggc) equals root inclusive time without double subtraction");

        // 9. Calls/frame calculation over uneven frames
        PerformanceProfiler.Reset();
        // Frame 1: 3 calls
        PerformanceProfiler.StartFrame();
        for (var i = 0; i < 3; i++) { using (PerformanceProfiler.Measure("Test", "Uneven")) { } }
        PerformanceProfiler.EndFrame();
        // Frame 2: 1 call
        PerformanceProfiler.StartFrame();
        using (PerformanceProfiler.Measure("Test", "Uneven")) { }
        PerformanceProfiler.EndFrame();
        // Frame 3: 0 calls
        PerformanceProfiler.StartFrame();
        PerformanceProfiler.EndFrame();
        // Frame 4: 4 calls
        PerformanceProfiler.StartFrame();
        for (var i = 0; i < 4; i++) { using (PerformanceProfiler.Measure("Test", "Uneven")) { } }
        PerformanceProfiler.EndFrame();
        var tree9 = PerformanceProfiler.GetTreeSnapshot();
        check(tree9[0].Count == 8 && Math.Abs(tree9[0].CallsPerFrame - 2.0) < 0.001, "9: 8 calls across 4 frames computes exactly 2.0 Calls/Frame");

        // 10. Avg(Frame) arithmetic sanity
        check(Math.Abs(tree9[0].InclusiveAvgFrameNs - (tree9[0].CallsPerFrame * tree9[0].InclusiveAvgCallNs)) < 0.001, "10a: Tree node satisfies InclusiveAvgFrame == CallsPerFrame * InclusiveAvgCall");
        var flatSnap10 = PerformanceProfiler.GetApiSnapshot();
        var flatRow10 = flatSnap10.Rows.First(r => r.Name == "Test.Uneven");
        var flatAvgFrameFromCall = (flatRow10.Count * flatRow10.AvgCallNanoseconds) / 4.0;
        check(Math.Abs(flatRow10.AvgFrameNanoseconds - flatAvgFrameFromCall) < 0.001, "10b: Flat row satisfies AvgFrameNanoseconds == (Count * AvgCall) / Frames");

        // 11. Reset removes old hierarchy/state
        PerformanceProfiler.Reset();
        check(PerformanceProfiler.TotalFramesCaptured == 0, "11a: Reset sets TotalFramesCaptured to 0");
        check(PerformanceProfiler.GetTreeSnapshot().Count == 0, "11b: Reset clears tree snapshot nodes");
        check(PerformanceProfiler.GetApiSnapshot().Rows.Length == 0, "11c: Reset clears flat snapshot rows");

        // 12. Current Frame Only does not show stale accumulated data
        PerformanceProfiler.Reset();
        PerformanceProfiler.StartFrame();
        using (PerformanceProfiler.Measure("Test", "Frame1Only")) { }
        PerformanceProfiler.EndFrame();

        PerformanceProfiler.StartFrame();
        using (PerformanceProfiler.Measure("Test", "Frame2Only")) { }
        var curFrameTree = PerformanceProfiler.GetTreeSnapshot(currentFrameOnly: true);
        check(curFrameTree.Any(r => r.Name == "Test.Frame2Only"), "12a: Current frame tree contains Frame2Only");
        check(!curFrameTree.Any(r => r.Name == "Test.Frame1Only"), "12b: Current frame tree excludes Frame1Only from prior frame");
        PerformanceProfiler.EndFrame();

        // 13. Recursive/re-entrant scope safety
        PerformanceProfiler.Reset();
        PerformanceProfiler.StartFrame();
        void RecursiveCall(int depth)
        {
            using (PerformanceProfiler.Measure("Test", "RecursiveScope"))
            {
                if (depth > 1)
                {
                    RecursiveCall(depth - 1);
                }
            }
        }
        RecursiveCall(5);
        PerformanceProfiler.EndFrame();
        var tree13 = PerformanceProfiler.GetTreeSnapshot();
        check(tree13.Count == 1 && tree13[0].Name == "Test.RecursiveScope", "13a: Recursive root is created safely");
        var cur = tree13[0];
        var recurseDepth = 1;
        while (cur.Children.Count > 0)
        {
            recurseDepth++;
            cur = cur.Children[0];
        }
        check(recurseDepth == 5, "13b: 5 levels of recursion recorded without stack corruption");

        // 14. Profiler-disabled path remains valid and zero-cost
        Core.GHSettings.ShowPerfProfiler = false;
        BottleneckCapture.RequestStop();
        PerformanceProfiler.Reset();
        check(!PerformanceProfiler.IsRecording, "14a: Profiler is disabled");
        using (PerformanceProfiler.Measure("Disabled", "Scope"))
        {
        }
        check(PerformanceProfiler.GetTreeSnapshot().Count == 0, "14b: No nodes created when profiler is disabled");

        // =========================================================================
        // Profiler Overhead Benchmark: Disabled vs Flat vs Nested Scopes
        // =========================================================================
        const int ProfilerBenchIterations = 100_000;
        Console.WriteLine("\n=========================================================================================");
        Console.WriteLine("           PERFORMANCE PROFILER OVERHEAD BENCHMARK (100,000 Scope Invocations)");
        Console.WriteLine("=========================================================================================");

        // 1. Profiler Disabled
        Core.GHSettings.ShowPerfProfiler = false;
        BottleneckCapture.RequestStop();
        for (var w = 0; w < 1000; w++) { using (PerformanceProfiler.Measure("Bench", "Scope")) { } }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var disabledAllocBefore = GC.GetAllocatedBytesForCurrentThread();
        var swDisabled = Stopwatch.StartNew();
        for (var i = 0; i < ProfilerBenchIterations; i++)
        {
            using (PerformanceProfiler.Measure("Bench", "Scope")) { }
        }
        swDisabled.Stop();
        var disabledAlloc = (double)(GC.GetAllocatedBytesForCurrentThread() - disabledAllocBefore) / ProfilerBenchIterations;
        var disabledNs = swDisabled.Elapsed.TotalNanoseconds / ProfilerBenchIterations;

        // 2. Profiler Enabled - Flat / Single Scope
        Core.GHSettings.ShowPerfProfiler = true;
        PerformanceProfiler.Reset();
        PerformanceProfiler.StartFrame();
        for (var w = 0; w < 1000; w++) { using (PerformanceProfiler.Measure("Bench", "FlatScope")) { } }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var flatAllocBefore = GC.GetAllocatedBytesForCurrentThread();
        var swFlat = Stopwatch.StartNew();
        for (var i = 0; i < ProfilerBenchIterations; i++)
        {
            using (PerformanceProfiler.Measure("Bench", "FlatScope")) { }
        }
        swFlat.Stop();
        var flatAlloc = (double)(GC.GetAllocatedBytesForCurrentThread() - flatAllocBefore) / ProfilerBenchIterations;
        var flatNs = swFlat.Elapsed.TotalNanoseconds / ProfilerBenchIterations;

        // 3. Profiler Enabled - Nested Scopes (Depth 3: Root -> Child -> GrandChild)
        PerformanceProfiler.Reset();
        for (var w = 0; w < 1000; w++)
        {
            using (PerformanceProfiler.Measure("Bench", "Root"))
            {
                using (PerformanceProfiler.Measure("Bench", "Child"))
                {
                    using (PerformanceProfiler.Measure("Bench", "GrandChild")) { }
                }
            }
        }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var nestedAllocBefore = GC.GetAllocatedBytesForCurrentThread();
        var swNested = Stopwatch.StartNew();
        for (var i = 0; i < ProfilerBenchIterations / 3; i++)
        {
            using (PerformanceProfiler.Measure("Bench", "Root"))
            {
                using (PerformanceProfiler.Measure("Bench", "Child"))
                {
                    using (PerformanceProfiler.Measure("Bench", "GrandChild")) { }
                }
            }
        }
        swNested.Stop();
        var nestedTotalScopes = (ProfilerBenchIterations / 3) * 3;
        var nestedAlloc = (double)(GC.GetAllocatedBytesForCurrentThread() - nestedAllocBefore) / nestedTotalScopes;
        var nestedNs = swNested.Elapsed.TotalNanoseconds / nestedTotalScopes;
        PerformanceProfiler.EndFrame();

        Console.WriteLine($"  1. Profiler Disabled:            {disabledNs,6:F1} ns/scope, {disabledAlloc:F1} B/scope");
        Console.WriteLine($"  2. Profiler Enabled (Flat):      {flatNs,6:F1} ns/scope, {flatAlloc:F1} B/scope");
        Console.WriteLine($"  3. Profiler Enabled (Nested D3): {nestedNs,6:F1} ns/scope, {nestedAlloc:F1} B/scope");
        Console.WriteLine("=========================================================================================\n");
    }

    private sealed class ReferencePageTracker
    {
        private readonly bool[] bytes = new bool[4096];
        private int mediumRegionMask;

        public int AccessCount { get; private set; }

        public int DistinctMediumRegions => System.Numerics.BitOperations.PopCount((uint)this.mediumRegionMask);

        public void RecordAccess(int offset, int size)
        {
            this.AccessCount++;
            var startMed = Math.Clamp(offset / 512, 0, 7);
            var endMed = Math.Clamp((offset + size - 1) / 512, 0, 7);
            for (var m = startMed; m <= endMed; m++)
            {
                this.mediumRegionMask |= 1 << m;
            }

            var startByte = Math.Clamp(offset, 0, 4096);
            var endByte = Math.Clamp(offset + size, 0, 4096);
            for (var b = startByte; b < endByte; b++)
            {
                this.bytes[b] = true;
            }
        }

        public int GetUniqueByteCount()
        {
            var count = 0;
            for (var i = 0; i < 4096; i++)
            {
                if (this.bytes[i])
                {
                    count++;
                }
            }

            return count;
        }

        public bool Qualifies()
        {
            return this.AccessCount >= 6 &&
                   this.DistinctMediumRegions >= 2 &&
                   this.GetUniqueByteCount() >= 256;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TestStruct64
    {
        public long A, B, C, D, E, F, G, H;
    }
}
#endif


