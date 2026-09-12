namespace TEHhub.Ui;

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;

internal static class BottleneckCapture
{
    private static int command, enabled, count, index;
    private static long started, published, frames;
    private static readonly double[] Samples = new double[4096];
    private static readonly Process Current = Process.GetCurrentProcess();
    private static double initialCpu;
    private static readonly int[] InitialGc = new int[3];
    private static BottleneckSnapshot? snapshot;
    private static string captureId = "";
    internal static CaptureStatus GetStatus() => new(Current.Id, Build, typeof(Core).Assembly.GetName().Version?.ToString(3) ?? "unknown", Enabled, Volatile.Read(ref captureId));
    internal static bool Enabled => Volatile.Read(ref enabled) != 0;
    internal static BottleneckSnapshot? Snapshot => Volatile.Read(ref snapshot);
    internal static void RequestStart() => Interlocked.Exchange(ref command, 1);
    internal static void RequestStop() => Interlocked.Exchange(ref command, 2);
    internal static long BeginFrame()
    {
        var requested = Interlocked.Exchange(ref command, 0);
        if (requested == 1)
        {
            PerformanceProfiler.Reset();
            MemoryReadDiagnostics.ResetForCapture();
            started = published = Stopwatch.GetTimestamp();
            Volatile.Write(ref captureId, Guid.NewGuid().ToString("N"));
            frames = count = index = 0;
            initialCpu = Current.TotalProcessorTime.TotalMilliseconds;
            for (var g = 0; g < 3; g++) InitialGc[g] = GC.CollectionCount(g);
            Volatile.Write(ref snapshot, null);
            Volatile.Write(ref enabled, 1);
        }
        else if (requested == 2 && Enabled) { Publish(false); Volatile.Write(ref enabled, 0); }
        return Enabled ? Stopwatch.GetTimestamp() : 0;
    }
    internal static void EndFrame(long timestamp)
    {
        if (timestamp == 0 || !Enabled) return;
        var now = Stopwatch.GetTimestamp();
        Samples[index] = (now - timestamp) * 1000.0 / Stopwatch.Frequency;
        index = (index + 1) % Samples.Length;
        count = Math.Min(count + 1, Samples.Length);
        frames++;
        if ((now - started) / (double)Stopwatch.Frequency >= 120) { Publish(false); Volatile.Write(ref enabled, 0); }
        else if ((now - published) / (double)Stopwatch.Frequency >= 1) { Publish(true); published = now; }
    }
    private static void Publish(bool active)
    {
        var seconds = Math.Max(0.001, (Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency);
        var sorted = Samples.Take(count).Order().ToArray();
        double Percentile(double p) => count == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(count * p) - 1, 0, count - 1)];
        Current.Refresh();
        var area = Core.States.InGameStateObject.CurrentAreaInstance;
        Volatile.Write(ref snapshot, new(Current.Id, typeof(Core).Assembly.GetName().Version?.ToString(3) ?? "unknown", Build,
            DateTime.UtcNow, captureId, Core.States.GameCurrentState.ToString(), area.AreaHash, area.AwakeEntities.Count, area.SleepingEntities.Count,
            active, seconds, frames, count, count == 0 ? 0 : sorted.Average(), Percentile(0.95), Percentile(0.99),
            (Current.TotalProcessorTime.TotalMilliseconds - initialCpu) / (seconds * 1000 * Environment.ProcessorCount) * 100,
            Current.WorkingSet64, Current.PrivateMemorySize64, GC.GetTotalMemory(false),
            Enumerable.Range(0, 3).Select(g => GC.CollectionCount(g) - InitialGc[g]).ToArray(),
            MemoryReadDiagnostics.GetApiSnapshot(), PerformanceProfiler.GetApiSnapshot().Rows.OrderByDescending(r => r.AvgFrameNanoseconds).Take(30).ToArray()));
    }
    private static string Build =>
#if DEBUG
        "Debug";
#else
        "Release";
#endif
}

internal sealed record BottleneckSnapshot(int ProcessId, string Version, string Build, DateTime WhenUtc,
    string CaptureId, string GameState, string AreaHash, int AwakeEntityCount, int SleepingEntityCount,
    bool Active, double ElapsedSeconds, long Frames, int RecentRenderSamples, double AverageRenderMilliseconds,
    double P95RenderMilliseconds, double P99RenderMilliseconds, double ProcessCpuPercentAllCores,
    long WorkingSetBytes, long PrivateBytes, long ManagedBytes, int[] GcCollections,
    MemoryDiagnosticsSnapshot Memory, PerformanceProfilerRow[] TopInclusiveScopes);

internal sealed record CaptureStatus(int ProcessId, string Build, string Version, bool Active, string CaptureId);
