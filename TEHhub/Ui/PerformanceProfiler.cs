// <copyright file="PerformanceProfiler.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace TEHhub.Ui;

using System;
using System.Threading;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using Coroutine;
using CoroutineEvents;
using ImGuiNET;

/// <summary>
///     Performance profiler for optimization purposes.
/// </summary>
public static class PerformanceProfiler
{
    internal static readonly double NsPerTick = 1000000000.0 / Stopwatch.Frequency;
    private static readonly ConcurrentDictionary<string, ProfileData> ProfileData = new();
    private static readonly ConcurrentDictionary<string, double> CurrentFrameNs = new();
    private static readonly ConcurrentDictionary<string, int> CurrentFrameCounts = new();
    private static readonly ConcurrentDictionary<string, long> CurrentFrameAllocatedBytes = new();
    private static readonly ConcurrentDictionary<(string NamespaceName, string MethodName), string> ProfileKeys = new();
    
    private static DateTime lastUpdate = DateTime.MinValue;
    private static List<ProfileRow> cachedRows = [];
    private static bool showCurrentFrameOnly = false;

    internal static void InitializeCoroutines()
    {
        CoroutineHandler.Start(RenderWindow());
    }

    public static IDisposable? Profile(string namespaceName, string methodName)
    {
        if (!Core.GHSettings.ShowPerfProfiler)
        {
            return null;
        }

        return new ProfileDisposable(
            GetProfileKey(namespaceName, methodName),
            Stopwatch.GetTimestamp(),
            GC.GetAllocatedBytesForCurrentThread());
    }

    /// <summary>
    ///     Returns the profiler rows directly for local diagnostics tooling. This mirrors the
    ///     values shown in the UI without requiring a screenshot or clipboard operation.
    /// </summary>
    internal static PerformanceProfilerSnapshot GetApiSnapshot()
    {
        var rows = new List<PerformanceProfilerRow>(ProfileData.Count);
        foreach (var kvp in ProfileData.ToArray())
        {
            var data = kvp.Value;
            var key = kvp.Key;
            int count;
            double averageCallNs;
            double averageFrameNs;
            double averageAllocatedBytes;
            if (showCurrentFrameOnly)
            {
                if (!CurrentFrameNs.TryGetValue(key, out var currentFrameNs) || currentFrameNs == 0 ||
                    !CurrentFrameCounts.TryGetValue(key, out count) || count == 0)
                {
                    continue;
                }

                averageCallNs = currentFrameNs / count;
                averageFrameNs = currentFrameNs;
                CurrentFrameAllocatedBytes.TryGetValue(key, out var currentFrameAllocatedBytes);
                averageAllocatedBytes = (double)currentFrameAllocatedBytes / count;
            }
            else
            {
                count = data.Count;
                averageCallNs = data.AverageTicks * NsPerTick;
                averageFrameNs = data.AverageFrameNs;
                averageAllocatedBytes = data.AverageAllocatedBytes;
            }

            if (count == 0)
            {
                continue;
            }

            rows.Add(new PerformanceProfilerRow(
                key,
                count,
                averageCallNs,
                data.GetPercentileTicks(0.95) * NsPerTick,
                data.GetPercentileTicks(0.99) * NsPerTick,
                averageAllocatedBytes,
                averageFrameNs));
        }

        return new PerformanceProfilerSnapshot(
            Core.GHSettings.ShowPerfProfiler,
            showCurrentFrameOnly,
            rows.OrderByDescending(static row => row.AllocatedBytesPerCall).ToArray());
    }

    internal static void Reset()
    {
        ProfileData.Clear();
        CurrentFrameNs.Clear();
        CurrentFrameCounts.Clear();
        CurrentFrameAllocatedBytes.Clear();
        cachedRows = [];
        lastUpdate = DateTime.MinValue;
    }

    /// <summary>
    ///     Creates an allocation-free profiling scope for TEHhub's internal hot paths.
    /// </summary>
    internal static ProfileScope Measure(string namespaceName, string methodName)
    {
        if (!Core.GHSettings.ShowPerfProfiler)
        {
            return default;
        }

        return new ProfileScope(
            GetProfileKey(namespaceName, methodName),
            Stopwatch.GetTimestamp(),
            GC.GetAllocatedBytesForCurrentThread());
    }

    private static IEnumerator<Wait> RenderWindow()
    {
        while (true)
        {
            yield return new Wait(TEHhubEvents.OnPostRender);
            if (!Core.GHSettings.ShowPerfProfiler)
            {
                continue;
            }

            ImGui.SetNextWindowSize(new Vector2(700, 500), ImGuiCond.FirstUseEver);
            if (ImGui.Begin("Performance Profiler", ref Core.GHSettings.ShowPerfProfiler, ImGuiWindowFlags.MenuBar))
            {
                if (ImGui.BeginMenuBar())
                {
                    if (ImGui.MenuItem("Reset"))
                    {
                        Reset();
                    }
                    ImGui.Checkbox("Current Frame Only", ref showCurrentFrameOnly);
                    ImGui.EndMenuBar();
                }
                
                EndFrame();
                
                var now = DateTime.Now;
                if ((now - lastUpdate).TotalMilliseconds >= 500 || cachedRows.Count == 0)
                {
                    lastUpdate = now;
                    var currentProfileData = ProfileData.ToList();
                    var tempRows = new List<ProfileRow>();
                    foreach (var kvp in currentProfileData)
                    {
                        var key = kvp.Key;
                        var pd = kvp.Value;
                        int count;
                        double avgPerCallNs;
                        double avgPerFrameNs;
                        double avgAllocatedBytes;
                        if (showCurrentFrameOnly)
                        {
                            if (!CurrentFrameNs.TryGetValue(key, out double currentFrameContrib) || currentFrameContrib == 0) continue;
                            if (!CurrentFrameCounts.TryGetValue(key, out int currentFrameCount) || currentFrameCount == 0) continue;
                            count = currentFrameCount;
                            avgPerCallNs = currentFrameContrib / currentFrameCount;
                            avgPerFrameNs = currentFrameContrib;
                            CurrentFrameAllocatedBytes.TryGetValue(key, out long currentFrameAllocatedBytes);
                            avgAllocatedBytes = (double)currentFrameAllocatedBytes / currentFrameCount;
                        }
                        else
                        {
                            count = pd.Count;
                            avgPerCallNs = pd.AverageTicks * NsPerTick;
                            avgPerFrameNs = pd.AverageFrameNs;
                            avgAllocatedBytes = pd.AverageAllocatedBytes;
                        }
                        tempRows.Add(new ProfileRow(
                            key,
                            count,
                            avgPerCallNs,
                            pd.GetPercentileTicks(0.95) * NsPerTick,
                            pd.GetPercentileTicks(0.99) * NsPerTick,
                            avgAllocatedBytes,
                            avgPerFrameNs));
                    }
                    cachedRows = tempRows;
                }
                if (ImGui.BeginTable("profilerTable", 7,
                        ImGuiTableFlags.Sortable | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp,
                        ImGui.GetContentRegionAvail()))
                {
                    ImGui.TableSetupColumn("Count");
                    ImGui.TableSetupColumn("Name");
                    ImGui.TableSetupColumn("Avg (Call)");
                    ImGui.TableSetupColumn("P95 (Call)");
                    ImGui.TableSetupColumn("P99 (Call)");
                    ImGui.TableSetupColumn("Alloc (Call)", ImGuiTableColumnFlags.DefaultSort);
                    ImGui.TableSetupColumn("Avg (Frame)");
                    
                    ImGui.TableSetupScrollFreeze(0, 1);
                    ImGui.TableHeadersRow();

                    var sortSpecs = ImGui.TableGetSortSpecs();
                    List<ProfileRow> sortedRows = cachedRows.OrderByDescending(r => r.AvgAllocatedBytes).ToList();
                    if (sortSpecs.SpecsCount > 0)
                    {
                        var spec = sortSpecs.Specs;
                        int col = spec.ColumnIndex;
                        bool ascending = spec.SortDirection == ImGuiSortDirection.Ascending;
                        
                        sortedRows = col switch
                        {
                            0 => ascending ? cachedRows.OrderBy(r => r.Count).ToList() : cachedRows.OrderByDescending(r => r.Count).ToList(), // Count
                            1 => ascending ? cachedRows.OrderBy(r => r.Name).ToList() : cachedRows.OrderByDescending(r => r.Name).ToList(), // Name
                            2 => ascending ? cachedRows.OrderBy(r => r.AvgPerCallNs).ToList() : cachedRows.OrderByDescending(r => r.AvgPerCallNs).ToList(), // Avg (Call)
                            3 => ascending ? cachedRows.OrderBy(r => r.P95PerCallNs).ToList() : cachedRows.OrderByDescending(r => r.P95PerCallNs).ToList(), // P95 (Call)
                            4 => ascending ? cachedRows.OrderBy(r => r.P99PerCallNs).ToList() : cachedRows.OrderByDescending(r => r.P99PerCallNs).ToList(), // P99 (Call)
                            5 => ascending ? cachedRows.OrderBy(r => r.AvgAllocatedBytes).ToList() : cachedRows.OrderByDescending(r => r.AvgAllocatedBytes).ToList(), // Alloc (Call)
                            6 => ascending ? cachedRows.OrderBy(r => r.AvgPerFrameNs).ToList() : cachedRows.OrderByDescending(r => r.AvgPerFrameNs).ToList(), // Avg (Frame)
                            _ => cachedRows.OrderByDescending(r => r.AvgAllocatedBytes).ToList()
                        };
                    }

                    foreach (var row in sortedRows)
                    {
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGui.Text(row.Count.ToString());

                        ImGui.TableNextColumn();
                        ImGui.Text(row.Name);

                        ImGui.TableNextColumn();
                        ImGui.Text(FormatTime(row.AvgPerCallNs));

                        ImGui.TableNextColumn();
                        ImGui.Text(FormatTime(row.P95PerCallNs));

                        ImGui.TableNextColumn();
                        ImGui.Text(FormatTime(row.P99PerCallNs));

                        ImGui.TableNextColumn();
                        ImGui.Text(FormatBytes(row.AvgAllocatedBytes));

                        ImGui.TableNextColumn();
                        ImGui.Text(FormatTime(row.AvgPerFrameNs));
                    }

                    ImGui.EndTable();
                }
            }
            ImGui.End();
        }
    }
        
    public static void StartFrame()
    {
        if (!Core.GHSettings.ShowPerfProfiler)
        {
            return;
        }
            
        CurrentFrameNs.Clear();
        CurrentFrameCounts.Clear();
        CurrentFrameAllocatedBytes.Clear();
    }
        
    public static void EndFrame()
    {
        if (!Core.GHSettings.ShowPerfProfiler)
        {
            return;
        }

        // Add frame samples for each profiled method
        foreach (var kvp in CurrentFrameNs)
        {
            var key = kvp.Key;
            var frameNs = kvp.Value;
            ProfileData.GetOrAdd(key, static _ => new ProfileData()).AddFrameSample(frameNs);
        }
    }

    internal static void RecordSample(string methodName, long startTimestamp, long startAllocatedBytes)
    {
        var elapsedTicks = Stopwatch.GetTimestamp() - startTimestamp;
        var elapsedNs = elapsedTicks * NsPerTick;
        var allocatedBytes = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - startAllocatedBytes);

        ProfileData.GetOrAdd(methodName, static _ => new ProfileData())
            .AddSample(elapsedTicks, allocatedBytes);
        CurrentFrameNs.AddOrUpdate(
            methodName,
            static (_, value) => value,
            static (_, existing, value) => existing + value,
            elapsedNs);
        CurrentFrameCounts.AddOrUpdate(methodName, 1, static (_, existing) => existing + 1);
        CurrentFrameAllocatedBytes.AddOrUpdate(
            methodName,
            static (_, value) => value,
            static (_, existing, value) => existing + value,
            allocatedBytes);
    }

    private static string GetProfileKey(string namespaceName, string methodName) =>
        ProfileKeys.GetOrAdd(
            (namespaceName, methodName),
            static key => string.Concat(key.NamespaceName, ".", key.MethodName));
        
    private static string FormatTime(double ns)
    {
        return ns switch
        {
            >= 1000000000.0 => $"{ns / 1000000000.0:F2} s",
            >= 1000000.0 => $"{ns / 1000000.0:F2} ms",
            >= 1000.0 => $"{ns / 1000.0:F2} us",
            _ => $"{ns:F2} ns"
        };
    }

    private static string FormatBytes(double bytes)
    {
        return bytes switch
        {
            >= 1048576.0 => $"{bytes / 1048576.0:F2} MiB",
            >= 1024.0 => $"{bytes / 1024.0:F2} KiB",
            _ => $"{bytes:F0} B"
        };
    }

    internal readonly struct ProfileScope : IDisposable
    {
        private readonly string? methodName;
        private readonly long startTimestamp;
        private readonly long startAllocatedBytes;

        internal ProfileScope(string methodName, long startTimestamp, long startAllocatedBytes)
        {
            this.methodName = methodName;
            this.startTimestamp = startTimestamp;
            this.startAllocatedBytes = startAllocatedBytes;
        }

        public void Dispose()
        {
            if (this.methodName != null)
            {
                RecordSample(this.methodName, this.startTimestamp, this.startAllocatedBytes);
            }
        }
    }
}

internal class ProfileData
{
    private const int WindowSize = 100;
    private int totalCount;
    private long sessionSumTicks;
    private long sessionSumAllocatedBytes;
    private double sessionSumFrameNs;
    private int sessionFrameCount;
    private readonly ConcurrentQueue<long> recentTicks = new();
    private readonly ConcurrentQueue<long> recentAllocatedBytes = new();
    private readonly ConcurrentQueue<double> recentFrameNs = new();
    public int Count => totalCount;

    // Average columns intentionally cover the whole capture session. Only percentile
    // calculations use the bounded recent window, so a long capture is not represented
    // by the last few calls alone.
    public double AverageTicks => totalCount > 0 ? (double)sessionSumTicks / totalCount : 0.0;
    public double AverageAllocatedBytes => totalCount > 0 ? (double)sessionSumAllocatedBytes / totalCount : 0.0;
    public double AverageFrameNs => sessionFrameCount > 0 ? sessionSumFrameNs / sessionFrameCount : 0.0;

    public void AddSample(long ticks, long allocatedBytes)
    {
        Interlocked.Increment(ref totalCount);
        recentTicks.Enqueue(ticks);
        recentAllocatedBytes.Enqueue(allocatedBytes);
        Interlocked.Add(ref sessionSumTicks, ticks);
        Interlocked.Add(ref sessionSumAllocatedBytes, allocatedBytes);
        while (recentTicks.Count > WindowSize)
        {
            recentTicks.TryDequeue(out _);
        }

        while (recentAllocatedBytes.Count > WindowSize)
        {
            recentAllocatedBytes.TryDequeue(out _);
        }
    }

    public long GetPercentileTicks(double percentile)
    {
        var samples = recentTicks.ToArray();
        if (samples.Length == 0)
        {
            return 0;
        }

        Array.Sort(samples);
        var index = Math.Clamp((int)Math.Ceiling(samples.Length * percentile) - 1, 0, samples.Length - 1);
        return samples[index];
    }

    public void AddFrameSample(double ns)
    {
        recentFrameNs.Enqueue(ns);
        Interlocked.Increment(ref sessionFrameCount);
        // F-182: atomic add - was raw `+=`, racy on parallel ProfileDisposable.Dispose
        // calls that update CurrentFrameNs from worker threads. Interlocked has no
        // double Add overload, so use the standard CompareExchange loop pattern.
        InterlockedAddDouble(ref sessionSumFrameNs, ns);
        while (recentFrameNs.Count > WindowSize)
        {
            recentFrameNs.TryDequeue(out _);
        }
    }

    private static double InterlockedAddDouble(ref double location, double value)
    {
        double current, computed;
        do
        {
            current = location;
            computed = current + value;
        }
        while (Interlocked.CompareExchange(ref location, computed, current) != current);
        return computed;
    }
}

internal class ProfileRow(
    string name,
    int count,
    double avgPerCallNs,
    double p95PerCallNs,
    double p99PerCallNs,
    double avgAllocatedBytes,
    double avgPerFrameNs)
{
    public string Name { get; } = name;
    public int Count { get; } = count;
    public double AvgPerCallNs { get; } = avgPerCallNs;
    public double P95PerCallNs { get; } = p95PerCallNs;
    public double P99PerCallNs { get; } = p99PerCallNs;
    public double AvgAllocatedBytes { get; } = avgAllocatedBytes;
    public double AvgPerFrameNs { get; } = avgPerFrameNs;
}

internal sealed record PerformanceProfilerSnapshot(
    bool Enabled,
    bool CurrentFrameOnly,
    PerformanceProfilerRow[] Rows);

internal sealed record PerformanceProfilerRow(
    string Name,
    int Count,
    double AvgCallNanoseconds,
    double P95CallNanoseconds,
    double P99CallNanoseconds,
    double AllocatedBytesPerCall,
    double AvgFrameNanoseconds);

internal sealed class ProfileDisposable(
    string methodName,
    long startTimestamp,
    long startAllocatedBytes) : IDisposable
{
    public void Dispose()
    {
        PerformanceProfiler.RecordSample(methodName, startTimestamp, startAllocatedBytes);
    }
}
