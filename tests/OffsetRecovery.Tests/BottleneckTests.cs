#if DEBUG
using System.Diagnostics;
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

        // Hybrid Memory Reader diagnostics atomic generation-state & invariant tests
        // 1 & 2: Start recording threads calling complete RecordHybrid* methods while resetting concurrently
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
                    MemoryReadDiagnostics.RecordHybridCompactPromotion(128);
                    MemoryReadDiagnostics.RecordHybridMediumPromotion(512);
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
            if (snap.FetchedBytes != (snap.ExactFetchedBytes + snap.CompactFetchedBytes + snap.MediumFetchedBytes + snap.PageFetchedBytes))
            {
                threadEx = new InvalidOperationException("FetchedBytes did not match sum of level byte counters");
                break;
            }
        }

        Volatile.Write(ref running, false);
        writer1.Join();
        resetter.Join();

        check(threadEx == null, "concurrent hybrid recording and atomic generation swapping completed without errors");

        // 4: Perform final reset after stopping writers
        MemoryReadDiagnostics.ResetForCapture();

        // 7: Verify reset produces a completely empty new generation
        var emptySnap = MemoryReadDiagnostics.GetApiSnapshot().Hybrid;
        check(emptySnap.LogicalRequests == 0 &&
              emptySnap.LogicalBytes == 0 &&
              emptySnap.ExactHits == 0 &&
              emptySnap.CompactHits == 0 &&
              emptySnap.MediumHits == 0 &&
              emptySnap.PageHits == 0 &&
              emptySnap.ExactReads == 0 &&
              emptySnap.CompactPromotions == 0 &&
              emptySnap.MediumPromotions == 0 &&
              emptySnap.PagePromotions == 0 &&
              emptySnap.ExactFetchedBytes == 0 &&
              emptySnap.CompactFetchedBytes == 0 &&
              emptySnap.MediumFetchedBytes == 0 &&
              emptySnap.PageFetchedBytes == 0 &&
              emptySnap.FetchedBytes == 0 &&
              emptySnap.EntriesCreated == 0,
              "atomic generation reset produces a completely empty new generation state");

        // 5: Record deterministic known set of events into the new generation
        MemoryReadDiagnostics.RecordHybridLogicalRequest(200);
        MemoryReadDiagnostics.RecordHybridExactRead(64);
        MemoryReadDiagnostics.RecordHybridCompactPromotion(128);
        MemoryReadDiagnostics.RecordHybridMediumPromotion(512);
        MemoryReadDiagnostics.RecordHybridPagePromotion(4096);

        // 6: Verify from the actual stored byte counters and promotion invariants
        var deterministicSnap = MemoryReadDiagnostics.GetApiSnapshot().Hybrid;
        check(deterministicSnap.ExactFetchedBytes == 64, "exact fetched bytes match recorded size");
        check(deterministicSnap.CompactFetchedBytes == 128 && deterministicSnap.CompactFetchedBytes == deterministicSnap.CompactPromotions * 128, "compact fetched bytes equal compact promotions * 128");
        check(deterministicSnap.MediumFetchedBytes == 512 && deterministicSnap.MediumFetchedBytes == deterministicSnap.MediumPromotions * 512, "medium fetched bytes equal medium promotions * 512");
        check(deterministicSnap.PageFetchedBytes == 4096 && deterministicSnap.PageFetchedBytes == deterministicSnap.PagePromotions * 4096, "page fetched bytes equal page promotions * 4096");
        check(deterministicSnap.FetchedBytes == deterministicSnap.ExactFetchedBytes + deterministicSnap.CompactFetchedBytes + deterministicSnap.MediumFetchedBytes + deterministicSnap.PageFetchedBytes, "total hybrid fetched bytes equal sum of actual level fetched bytes");
        check(deterministicSnap.EntriesCreated == deterministicSnap.ExactReads + deterministicSnap.CompactPromotions + deterministicSnap.MediumPromotions + deterministicSnap.PagePromotions, "entries created equals sum of exact reads and promotions in isolated generation");

        MemoryReadDiagnostics.ResetForCapture();
    }
}
#endif

