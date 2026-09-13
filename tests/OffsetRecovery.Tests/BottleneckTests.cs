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
    }
}
