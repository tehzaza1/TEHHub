using System.Diagnostics;
using System.Text.Json;
using TEHhub;
using TEHhub.Ui;

internal static class BottleneckTests
{
    internal static void Run(Action<bool, string> check)
    {
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
