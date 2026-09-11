// <copyright file="PerformanceProfiler.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace GameHelper.Ui;

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
    ///     Creates an allocation-free profiling scope for GameHelper's internal hot paths.
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
            yield return new Wait(GameHelperEvents.OnPostRender);
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
                        ProfileData.Clear();
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
    private long sumTicks;
    private long sumAllocatedBytes;
    private double sumFrameNs;
    private readonly ConcurrentQueue<long> recentTicks = new();
    private readonly ConcurrentQueue<long> recentAllocatedBytes = new();
    private readonly ConcurrentQueue<double> recentFrameNs = new();
    public int Count => totalCount;

    public double AverageTicks => !recentTicks.IsEmpty ? (double)sumTicks / recentTicks.Count : 0.0;
    public double AverageAllocatedBytes => !recentAllocatedBytes.IsEmpty ? (double)sumAllocatedBytes / recentAllocatedBytes.Count : 0.0;
    public double AverageFrameNs => !recentFrameNs.IsEmpty ? sumFrameNs / recentFrameNs.Count : 0.0;

    public void AddSample(long ticks, long allocatedBytes)
    {
        Interlocked.Increment(ref totalCount);
        recentTicks.Enqueue(ticks);
        recentAllocatedBytes.Enqueue(allocatedBytes);
        Interlocked.Add(ref sumTicks, ticks);
        Interlocked.Add(ref sumAllocatedBytes, allocatedBytes);
        while (recentTicks.Count > WindowSize)
        {
            if (recentTicks.TryDequeue(out var old))
            {
                Interlocked.Add(ref sumTicks, -old);
            }
        }

        while (recentAllocatedBytes.Count > WindowSize)
        {
            if (recentAllocatedBytes.TryDequeue(out var old))
            {
                Interlocked.Add(ref sumAllocatedBytes, -old);
            }
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
        // F-182: atomic add - was raw `+=`, racy on parallel ProfileDisposable.Dispose
        // calls that update CurrentFrameNs from worker threads. Interlocked has no
        // double Add overload, so use the standard CompareExchange loop pattern.
        InterlockedAddDouble(ref sumFrameNs, ns);
        while (recentFrameNs.Count > WindowSize)
        {
            if (recentFrameNs.TryDequeue(out double old))
            {
                InterlockedAddDouble(ref sumFrameNs, -old);
            }
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
