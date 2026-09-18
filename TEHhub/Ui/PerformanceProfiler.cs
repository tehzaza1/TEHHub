// <copyright file="PerformanceProfiler.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace TEHhub.Ui;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using Coroutine;
using CoroutineEvents;
using ImGuiNET;

/// <summary>
///     Hierarchical performance profiler for optimization and runtime diagnostic analysis.
/// </summary>
public static class PerformanceProfiler
{
    internal static bool IsRecording => Core.GHSettings.ShowPerfProfiler || BottleneckCapture.Enabled;
    internal static readonly double NsPerTick = 1000000000.0 / Stopwatch.Frequency;

    // Node registry & hierarchy storage
    private static readonly ConcurrentDictionary<(int ParentId, string ScopeKey), int> NodeLookup = new();
    private static readonly ConcurrentDictionary<int, ProfilerNode> Nodes = new();
    private static readonly List<ProfilerNode> AllNodes = new();
    private static readonly ConcurrentDictionary<(string NamespaceName, string MethodName), string> ProfileKeys = new();
    private static readonly List<int> RootNodeIds = new();
    private static readonly object HierarchyLock = new();
    private static int nextNodeId = 1;
    private static int currentSessionGeneration = 1;

    // Global session frame counter
    private static long totalFramesCaptured;

    // UI state & cached view models
    private static DateTime lastUpdate = DateTime.MinValue;
    private static List<ProfilerTreeNode> cachedTree = [];
    private static List<ProfilerFlatRow> cachedFlatRows = [];
    private static bool showCurrentFrameOnly = false;
    private static int viewMode = 0; // 0 = Tree View, 1 = Flat Hotspots

    // Thread-local call stack frame
    private struct ThreadScopeFrame
    {
        public int NodeId;
        public int Generation;
        public long StartTimestamp;
        public long StartAllocated;
        public long DirectChildTicks;
    }

    [ThreadStatic]
    private static ThreadScopeFrame[]? threadStack;
    [ThreadStatic]
    private static int threadStackDepth;

    internal static void InitializeCoroutines()
    {
        CoroutineHandler.Start(RenderWindow());
    }

    /// <summary>
    ///     Creates an allocation-free profiling scope for TEHhub's internal hot paths.
    /// </summary>
    internal static ProfileScope Measure(string namespaceName, string methodName)
    {
        if (!IsRecording)
        {
            return default;
        }

        var key = GetProfileKey(namespaceName, methodName);
        var gen = Volatile.Read(ref currentSessionGeneration);
        var stack = threadStack;
        var depth = threadStackDepth;

        // Prune any stale stack frames from previous session generations
        while (depth > 0 && stack != null && stack[depth - 1].Generation != gen)
        {
            depth--;
        }
        threadStackDepth = depth;

        var parentId = depth > 0 ? stack![depth - 1].NodeId : 0;
        var nodeId = GetOrCreateNode(parentId, key);

        if (stack == null || depth >= stack.Length)
        {
            Array.Resize(ref threadStack, Math.Max(32, (stack?.Length ?? 0) * 2));
            stack = threadStack;
        }

        stack[depth] = new ThreadScopeFrame
        {
            NodeId = nodeId,
            Generation = gen,
            StartTimestamp = Stopwatch.GetTimestamp(),
            StartAllocated = GC.GetAllocatedBytesForCurrentThread(),
            DirectChildTicks = 0,
        };
        threadStackDepth = depth + 1;

        return new ProfileScope(depth + 1, gen);
    }

    public static IDisposable? Profile(string namespaceName, string methodName)
    {
        if (!IsRecording)
        {
            return null;
        }

        var scope = Measure(namespaceName, methodName);
        return new ProfileDisposable(scope);
    }

    internal static void RecordScopeExit(int expectedDepth, int expectedGeneration)
    {
        var depth = threadStackDepth;
        if (depth == 0)
        {
            return;
        }

        // If the session reset while this scope was active, discard sample safely and clean up stack
        if (expectedGeneration != Volatile.Read(ref currentSessionGeneration))
        {
            if (depth >= expectedDepth && threadStack != null && threadStack[expectedDepth - 1].Generation == expectedGeneration)
            {
                threadStackDepth = expectedDepth - 1;
            }

            return;
        }

        if (depth != expectedDepth)
        {
            return;
        }

        var newDepth = depth - 1;
        threadStackDepth = newDepth;
        ref var frame = ref threadStack![newDepth];

        if (frame.Generation != expectedGeneration)
        {
            return;
        }

        var endTimestamp = Stopwatch.GetTimestamp();
        var endAllocated = GC.GetAllocatedBytesForCurrentThread();

        var inclusiveTicks = Math.Max(0, endTimestamp - frame.StartTimestamp);
        var selfTicks = Math.Max(0, inclusiveTicks - frame.DirectChildTicks);
        var allocatedBytes = Math.Max(0, endAllocated - frame.StartAllocated);

        if (Nodes.TryGetValue(frame.NodeId, out var node))
        {
            node.AddSample(inclusiveTicks, selfTicks, allocatedBytes);
        }

        if (newDepth > 0 && threadStack[newDepth - 1].Generation == expectedGeneration)
        {
            threadStack[newDepth - 1].DirectChildTicks += inclusiveTicks;
        }
    }

    public static void StartFrame()
    {
        if (!IsRecording)
        {
            return;
        }

        lock (HierarchyLock)
        {
            for (var i = 0; i < AllNodes.Count; i++)
            {
                AllNodes[i].ResetCurrentFrame();
            }
        }
    }

    public static void EndFrame()
    {
        if (!IsRecording)
        {
            return;
        }

        Interlocked.Increment(ref totalFramesCaptured);
    }

    internal static void Reset()
    {
        lock (HierarchyLock)
        {
            Interlocked.Increment(ref currentSessionGeneration);
            NodeLookup.Clear();
            Nodes.Clear();
            AllNodes.Clear();
            ProfileKeys.Clear();
            RootNodeIds.Clear();
            nextNodeId = 1;
            Volatile.Write(ref totalFramesCaptured, 0);
            cachedTree = [];
            cachedFlatRows = [];
            lastUpdate = DateTime.MinValue;
        }
    }

    internal static long TotalFramesCaptured => Volatile.Read(ref totalFramesCaptured);

    /// <summary>
    ///     Returns a snapshot of the call tree for unit testing and diagnostic inspection.
    /// </summary>
    internal static List<ProfilerTreeNode> GetTreeSnapshot(bool currentFrameOnly = false)
    {
        return BuildTreeSnapshot(currentFrameOnly);
    }

    /// <summary>
    ///     Returns flat profiler rows for local diagnostics tooling and automated captures.
    ///     Aggregates identical scope names across all parent locations.
    /// </summary>
    internal static PerformanceProfilerSnapshot GetApiSnapshot(bool? currentFrameOnly = null)
    {
        var curFrame = currentFrameOnly ?? showCurrentFrameOnly;
        var frames = Math.Max(1, Volatile.Read(ref totalFramesCaptured));
        var flatRows = BuildFlatRows(curFrame, frames);
        var rows = new List<PerformanceProfilerRow>(flatRows.Count);

        foreach (var r in flatRows)
        {
            rows.Add(new PerformanceProfilerRow(
                r.Name,
                r.Count,
                r.InclusiveAvgCallNs,
                r.P95CallNs,
                r.P99CallNs,
                r.AvgAllocatedBytes,
                r.InclusiveAvgFrameNs));
        }

        return new PerformanceProfilerSnapshot(
            IsRecording,
            curFrame,
            rows.OrderByDescending(static row => row.AvgFrameNanoseconds).ToArray());
    }

    private static int GetOrCreateNode(int parentId, string scopeKey)
    {
        if (NodeLookup.TryGetValue((parentId, scopeKey), out var existingId))
        {
            return existingId;
        }

        lock (HierarchyLock)
        {
            if (NodeLookup.TryGetValue((parentId, scopeKey), out existingId))
            {
                return existingId;
            }

            var id = nextNodeId++;
            var node = new ProfilerNode(id, parentId, scopeKey);
            Nodes[id] = node;
            NodeLookup[(parentId, scopeKey)] = id;
            AllNodes.Add(node);

            if (parentId == 0)
            {
                RootNodeIds.Add(id);
            }
            else if (Nodes.TryGetValue(parentId, out var parentNode))
            {
                lock (parentNode.Children)
                {
                    parentNode.Children.Add(id);
                }
            }

            return id;
        }
    }

    private static List<ProfilerTreeNode> BuildTreeSnapshot(bool currentFrameOnly)
    {
        var frames = Math.Max(1, Volatile.Read(ref totalFramesCaptured));
        var result = new List<ProfilerTreeNode>();

        lock (HierarchyLock)
        {
            foreach (var rootId in RootNodeIds)
            {
                if (Nodes.TryGetValue(rootId, out var rootNode))
                {
                    var treeNode = BuildTreeNode(rootNode, frames, currentFrameOnly, 0);
                    if (treeNode != null)
                    {
                        result.Add(treeNode);
                    }
                }
            }
        }

        return result.OrderByDescending(static r => r.InclusiveAvgFrameNs).ToList();
    }

    private static ProfilerTreeNode? BuildTreeNode(ProfilerNode node, long frames, bool currentFrameOnly, int depth)
    {
        int count;
        double callsPerFrame;
        double incAvgCallNs;
        double incAvgFrameNs;
        double selfAvgCallNs;
        double selfAvgFrameNs;
        double avgAllocBytes;

        if (currentFrameOnly)
        {
            count = node.CurrentFrameCount;
            callsPerFrame = count;
            incAvgCallNs = count > 0 ? (node.CurrentFrameInclusiveTicks * NsPerTick) / count : 0.0;
            incAvgFrameNs = node.CurrentFrameInclusiveTicks * NsPerTick;
            selfAvgCallNs = count > 0 ? (node.CurrentFrameSelfTicks * NsPerTick) / count : 0.0;
            selfAvgFrameNs = node.CurrentFrameSelfTicks * NsPerTick;
            avgAllocBytes = count > 0 ? (double)node.CurrentFrameAllocatedBytes / count : 0.0;
        }
        else
        {
            count = node.TotalCount;
            callsPerFrame = (double)count / frames;
            incAvgCallNs = count > 0 ? (node.SessionSumInclusiveTicks * NsPerTick) / count : 0.0;
            incAvgFrameNs = (node.SessionSumInclusiveTicks * NsPerTick) / frames;
            selfAvgCallNs = count > 0 ? (node.SessionSumSelfTicks * NsPerTick) / count : 0.0;
            selfAvgFrameNs = (node.SessionSumSelfTicks * NsPerTick) / frames;
            avgAllocBytes = count > 0 ? (double)node.SessionSumAllocatedBytes / count : 0.0;
        }

        List<int> childIds;
        lock (node.Children)
        {
            childIds = node.Children.ToList();
        }

        var activeChildren = new List<ProfilerTreeNode>();
        foreach (var childId in childIds)
        {
            if (Nodes.TryGetValue(childId, out var childNode))
            {
                var childTreeNode = BuildTreeNode(childNode, frames, currentFrameOnly, depth + 1);
                if (childTreeNode != null)
                {
                    activeChildren.Add(childTreeNode);
                }
            }
        }

        if (currentFrameOnly && count == 0 && activeChildren.Count == 0)
        {
            return null;
        }

        if (!currentFrameOnly && count == 0 && activeChildren.Count == 0)
        {
            return null;
        }

        var treeNode = new ProfilerTreeNode
        {
            Id = node.Id,
            ParentId = node.ParentId,
            Name = node.ScopeKey,
            DisplayName = node.DisplayName,
            Depth = depth,
            Count = count,
            CallsPerFrame = callsPerFrame,
            InclusiveAvgCallNs = incAvgCallNs,
            InclusiveAvgFrameNs = incAvgFrameNs,
            SelfAvgCallNs = selfAvgCallNs,
            SelfAvgFrameNs = selfAvgFrameNs,
            P95CallNs = currentFrameOnly ? double.NaN : node.GetPercentileTicks(0.95) * NsPerTick,
            P99CallNs = currentFrameOnly ? double.NaN : node.GetPercentileTicks(0.99) * NsPerTick,
            AvgAllocatedBytes = avgAllocBytes,
        };

        activeChildren.Sort(static (a, b) => b.InclusiveAvgFrameNs.CompareTo(a.InclusiveAvgFrameNs));
        treeNode.Children.AddRange(activeChildren);
        return treeNode;
    }

    private static List<ProfilerFlatRow> BuildFlatRows(bool currentFrameOnly, long frames)
    {
        List<ProfilerNode> nodesSnapshot;
        lock (HierarchyLock)
        {
            nodesSnapshot = new List<ProfilerNode>(AllNodes);
        }

        var groups = nodesSnapshot.GroupBy(static n => n.ScopeKey);
        var rows = new List<ProfilerFlatRow>();

        foreach (var g in groups)
        {
            var key = g.Key;
            int count;
            double callsPerFrame;
            double incAvgCallNs;
            double incAvgFrameNs;
            double selfAvgCallNs;
            double selfAvgFrameNs;
            double avgAllocBytes;

            if (currentFrameOnly)
            {
                count = g.Sum(static n => n.CurrentFrameCount);
                if (count == 0) continue;
                var totalFrameIncTicks = g.Sum(static n => n.CurrentFrameInclusiveTicks);
                var totalFrameSelfTicks = g.Sum(static n => n.CurrentFrameSelfTicks);
                var totalFrameAlloc = g.Sum(static n => n.CurrentFrameAllocatedBytes);

                callsPerFrame = count;
                incAvgCallNs = (totalFrameIncTicks * NsPerTick) / count;
                incAvgFrameNs = totalFrameIncTicks * NsPerTick;
                selfAvgCallNs = (totalFrameSelfTicks * NsPerTick) / count;
                selfAvgFrameNs = totalFrameSelfTicks * NsPerTick;
                avgAllocBytes = (double)totalFrameAlloc / count;
            }
            else
            {
                count = g.Sum(static n => n.TotalCount);
                if (count == 0) continue;
                var totalIncTicks = g.Sum(static n => n.SessionSumInclusiveTicks);
                var totalSelfTicks = g.Sum(static n => n.SessionSumSelfTicks);
                var totalAlloc = g.Sum(static n => n.SessionSumAllocatedBytes);

                callsPerFrame = (double)count / frames;
                incAvgCallNs = (totalIncTicks * NsPerTick) / count;
                incAvgFrameNs = (totalIncTicks * NsPerTick) / frames;
                selfAvgCallNs = (totalSelfTicks * NsPerTick) / count;
                selfAvgFrameNs = (totalSelfTicks * NsPerTick) / frames;
                avgAllocBytes = (double)totalAlloc / count;
            }

            double p95Ns, p99Ns;
            if (currentFrameOnly)
            {
                p95Ns = double.NaN;
                p99Ns = double.NaN;
            }
            else
            {
                // Merged bounded sample population from all nodes in this group
                var combinedSamples = g.SelectMany(static n => n.GetRecentSamples()).ToArray();
                if (combinedSamples.Length > 0)
                {
                    Array.Sort(combinedSamples);
                    var idx95 = Math.Clamp((int)Math.Ceiling(combinedSamples.Length * 0.95) - 1, 0, combinedSamples.Length - 1);
                    var idx99 = Math.Clamp((int)Math.Ceiling(combinedSamples.Length * 0.99) - 1, 0, combinedSamples.Length - 1);
                    p95Ns = combinedSamples[idx95] * NsPerTick;
                    p99Ns = combinedSamples[idx99] * NsPerTick;
                }
                else
                {
                    p95Ns = 0.0;
                    p99Ns = 0.0;
                }
            }

            rows.Add(new ProfilerFlatRow(
                key,
                count,
                callsPerFrame,
                incAvgCallNs,
                incAvgFrameNs,
                selfAvgCallNs,
                selfAvgFrameNs,
                p95Ns,
                p99Ns,
                avgAllocBytes));
        }

        return rows;
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

            ImGui.SetNextWindowSize(new Vector2(880, 520), ImGuiCond.FirstUseEver);
            if (ImGui.Begin("Performance Profiler", ref Core.GHSettings.ShowPerfProfiler, ImGuiWindowFlags.MenuBar))
            {
                if (ImGui.BeginMenuBar())
                {
                    if (ImGui.MenuItem("Reset"))
                    {
                        Reset();
                    }

                    ImGui.SameLine();
                    ImGui.Checkbox("Current Frame Only", ref showCurrentFrameOnly);

                    ImGui.SameLine();
                    ImGui.RadioButton("Tree View", ref viewMode, 0);

                    ImGui.SameLine();
                    ImGui.RadioButton("Flat Hotspots", ref viewMode, 1);

                    ImGui.EndMenuBar();
                }

                var now = DateTime.Now;
                var frames = Math.Max(1, Volatile.Read(ref totalFramesCaptured));
                if ((now - lastUpdate).TotalMilliseconds >= 500 || (cachedTree.Count == 0 && cachedFlatRows.Count == 0))
                {
                    lastUpdate = now;
                    cachedTree = BuildTreeSnapshot(showCurrentFrameOnly);
                    cachedFlatRows = BuildFlatRows(showCurrentFrameOnly, frames);
                }

                if (viewMode == 0)
                {
                    RenderTreeView();
                }
                else
                {
                    RenderFlatHotspotsView();
                }
            }

            ImGui.End();
        }
    }

    private static void RenderTreeView()
    {
        var tableFlags = ImGuiTableFlags.Resizable |
                         ImGuiTableFlags.ScrollX |
                         ImGuiTableFlags.ScrollY |
                         ImGuiTableFlags.Borders |
                         ImGuiTableFlags.RowBg;

        if (ImGui.BeginTable("profilerTreeTable", 9, tableFlags, ImGui.GetContentRegionAvail()))
        {
            ImGui.TableSetupColumn("Scope", ImGuiTableColumnFlags.WidthFixed, 320f);
            ImGui.TableSetupColumn("Inclusive Avg/F", ImGuiTableColumnFlags.WidthFixed, 100f);
            ImGui.TableSetupColumn("Self Avg/F", ImGuiTableColumnFlags.WidthFixed, 100f);
            ImGui.TableSetupColumn("Calls/F", ImGuiTableColumnFlags.WidthFixed, 75f);
            ImGui.TableSetupColumn("Count", ImGuiTableColumnFlags.WidthFixed, 85f);
            ImGui.TableSetupColumn("Avg (Call)", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableSetupColumn("P95 (Call)", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableSetupColumn("P99 (Call)", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableSetupColumn("Alloc (Call)", ImGuiTableColumnFlags.WidthFixed, 90f);

            ImGui.TableSetupScrollFreeze(1, 1);
            ImGui.TableHeadersRow();

            foreach (var node in cachedTree)
            {
                RenderTreeRow(node);
            }

            ImGui.EndTable();
        }
    }

    private static void RenderTreeRow(ProfilerTreeNode node)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();

        var hasChildren = node.Children.Count > 0;
        var flags = ImGuiTreeNodeFlags.SpanFullWidth | ImGuiTreeNodeFlags.OpenOnArrow;
        if (!hasChildren)
        {
            flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;
        }

        var isOpen = ImGui.TreeNodeEx($"##Node_{node.Id}", flags, node.DisplayName);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(node.Name);
        }

        ImGui.TableNextColumn();
        ImGui.Text(FormatTime(node.InclusiveAvgFrameNs));

        ImGui.TableNextColumn();
        ImGui.Text(FormatTime(node.SelfAvgFrameNs));

        ImGui.TableNextColumn();
        ImGui.Text(node.CallsPerFrame >= 10.0 ? $"{node.CallsPerFrame:F1}" : $"{node.CallsPerFrame:F2}");

        ImGui.TableNextColumn();
        ImGui.Text($"{node.Count:N0}");

        ImGui.TableNextColumn();
        ImGui.Text(FormatTime(node.InclusiveAvgCallNs));

        ImGui.TableNextColumn();
        ImGui.Text(FormatTime(node.P95CallNs));

        ImGui.TableNextColumn();
        ImGui.Text(FormatTime(node.P99CallNs));

        ImGui.TableNextColumn();
        ImGui.Text(FormatBytes(node.AvgAllocatedBytes));

        if (hasChildren && isOpen)
        {
            foreach (var child in node.Children)
            {
                RenderTreeRow(child);
            }

            ImGui.TreePop();
        }
    }

    private static void RenderFlatHotspotsView()
    {
        var tableFlags = ImGuiTableFlags.Sortable |
                         ImGuiTableFlags.Resizable |
                         ImGuiTableFlags.ScrollX |
                         ImGuiTableFlags.ScrollY |
                         ImGuiTableFlags.Borders |
                         ImGuiTableFlags.RowBg;

        if (ImGui.BeginTable("profilerFlatTable", 9, tableFlags, ImGui.GetContentRegionAvail()))
        {
            ImGui.TableSetupColumn("Scope", ImGuiTableColumnFlags.WidthFixed, 320f);
            ImGui.TableSetupColumn("Inclusive Avg/F", ImGuiTableColumnFlags.WidthFixed, 100f);
            ImGui.TableSetupColumn("Self Avg/F", ImGuiTableColumnFlags.WidthFixed, 100f);
            ImGui.TableSetupColumn("Calls/F", ImGuiTableColumnFlags.WidthFixed, 75f);
            ImGui.TableSetupColumn("Count", ImGuiTableColumnFlags.WidthFixed, 85f);
            ImGui.TableSetupColumn("Avg (Call)", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableSetupColumn("P95 (Call)", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableSetupColumn("P99 (Call)", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableSetupColumn("Alloc (Call)", ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.WidthFixed, 90f);

            ImGui.TableSetupScrollFreeze(1, 1);
            ImGui.TableHeadersRow();

            var sortSpecs = ImGui.TableGetSortSpecs();
            var sortedRows = cachedFlatRows.OrderByDescending(static r => r.AvgAllocatedBytes).ToList();
            if (sortSpecs.SpecsCount > 0)
            {
                var spec = sortSpecs.Specs;
                int col = spec.ColumnIndex;
                bool ascending = spec.SortDirection == ImGuiSortDirection.Ascending;

                sortedRows = col switch
                {
                    0 => ascending ? cachedFlatRows.OrderBy(static r => r.Name).ToList() : cachedFlatRows.OrderByDescending(static r => r.Name).ToList(),
                    1 => ascending ? cachedFlatRows.OrderBy(static r => r.InclusiveAvgFrameNs).ToList() : cachedFlatRows.OrderByDescending(static r => r.InclusiveAvgFrameNs).ToList(),
                    2 => ascending ? cachedFlatRows.OrderBy(static r => r.SelfAvgFrameNs).ToList() : cachedFlatRows.OrderByDescending(static r => r.SelfAvgFrameNs).ToList(),
                    3 => ascending ? cachedFlatRows.OrderBy(static r => r.CallsPerFrame).ToList() : cachedFlatRows.OrderByDescending(static r => r.CallsPerFrame).ToList(),
                    4 => ascending ? cachedFlatRows.OrderBy(static r => r.Count).ToList() : cachedFlatRows.OrderByDescending(static r => r.Count).ToList(),
                    5 => ascending ? cachedFlatRows.OrderBy(static r => r.InclusiveAvgCallNs).ToList() : cachedFlatRows.OrderByDescending(static r => r.InclusiveAvgCallNs).ToList(),
                    6 => ascending ? cachedFlatRows.OrderBy(static r => r.P95CallNs).ToList() : cachedFlatRows.OrderByDescending(static r => r.P95CallNs).ToList(),
                    7 => ascending ? cachedFlatRows.OrderBy(static r => r.P99CallNs).ToList() : cachedFlatRows.OrderByDescending(static r => r.P99CallNs).ToList(),
                    8 => ascending ? cachedFlatRows.OrderBy(static r => r.AvgAllocatedBytes).ToList() : cachedFlatRows.OrderByDescending(static r => r.AvgAllocatedBytes).ToList(),
                    _ => cachedFlatRows.OrderByDescending(static r => r.AvgAllocatedBytes).ToList(),
                };
            }

            foreach (var row in sortedRows)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.Text(row.Name);
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(row.Name);
                }

                ImGui.TableNextColumn();
                ImGui.Text(FormatTime(row.InclusiveAvgFrameNs));

                ImGui.TableNextColumn();
                ImGui.Text(FormatTime(row.SelfAvgFrameNs));

                ImGui.TableNextColumn();
                ImGui.Text(row.CallsPerFrame >= 10.0 ? $"{row.CallsPerFrame:F1}" : $"{row.CallsPerFrame:F2}");

                ImGui.TableNextColumn();
                ImGui.Text($"{row.Count:N0}");

                ImGui.TableNextColumn();
                ImGui.Text(FormatTime(row.InclusiveAvgCallNs));

                ImGui.TableNextColumn();
                ImGui.Text(FormatTime(row.P95CallNs));

                ImGui.TableNextColumn();
                ImGui.Text(FormatTime(row.P99CallNs));

                ImGui.TableNextColumn();
                ImGui.Text(FormatBytes(row.AvgAllocatedBytes));
            }

            ImGui.EndTable();
        }
    }

    private static string GetProfileKey(string namespaceName, string methodName) =>
        ProfileKeys.GetOrAdd(
            (namespaceName, methodName),
            static key => string.Concat(key.NamespaceName, ".", key.MethodName));

    private static string FormatTime(double ns)
    {
        if (double.IsNaN(ns))
        {
            return "N/A";
        }

        return ns switch
        {
            >= 1000000000.0 => $"{ns / 1000000000.0:F2} s",
            >= 1000000.0 => $"{ns / 1000000.0:F2} ms",
            >= 1000.0 => $"{ns / 1000.0:F2} us",
            _ => $"{ns:F2} ns",
        };
    }

    private static string FormatBytes(double bytes)
    {
        return bytes switch
        {
            >= 1048576.0 => $"{bytes / 1048576.0:F2} MiB",
            >= 1024.0 => $"{bytes / 1024.0:F2} KiB",
            _ => $"{bytes:F0} B",
        };
    }

    internal readonly struct ProfileScope : IDisposable
    {
        private readonly int depth;
        private readonly int generation;

        internal ProfileScope(int depth, int generation)
        {
            this.depth = depth;
            this.generation = generation;
        }

        public void Dispose()
        {
            if (this.depth > 0)
            {
                RecordScopeExit(this.depth, this.generation);
            }
        }
    }

    internal sealed class ProfileDisposable : IDisposable
    {
        private ProfileScope scope;
        private bool disposed;

        internal ProfileDisposable(ProfileScope scope)
        {
            this.scope = scope;
        }

        public void Dispose()
        {
            if (!this.disposed)
            {
                this.disposed = true;
                this.scope.Dispose();
            }
        }
    }
}

internal class ProfilerNode
{
    public int Id { get; }
    public int ParentId { get; }
    public string ScopeKey { get; }
    public string DisplayName { get; }

    public readonly List<int> Children = new();

    private int totalCount;
    private long sessionSumInclusiveTicks;
    private long sessionSumSelfTicks;
    private long sessionSumAllocatedBytes;

    private const int WindowSize = 100;
    private readonly ConcurrentQueue<long> recentInclusiveTicks = new();

    private int currentFrameCount;
    private long currentFrameInclusiveTicks;
    private long currentFrameSelfTicks;
    private long currentFrameAllocatedBytes;

    public ProfilerNode(int id, int parentId, string scopeKey)
    {
        this.Id = id;
        this.ParentId = parentId;
        this.ScopeKey = scopeKey;
        this.DisplayName = scopeKey;
    }

    public int TotalCount => Volatile.Read(ref this.totalCount);
    public long SessionSumInclusiveTicks => Volatile.Read(ref this.sessionSumInclusiveTicks);
    public long SessionSumSelfTicks => Volatile.Read(ref this.sessionSumSelfTicks);
    public long SessionSumAllocatedBytes => Volatile.Read(ref this.sessionSumAllocatedBytes);

    public int CurrentFrameCount => Volatile.Read(ref this.currentFrameCount);
    public long CurrentFrameInclusiveTicks => Volatile.Read(ref this.currentFrameInclusiveTicks);
    public long CurrentFrameSelfTicks => Volatile.Read(ref this.currentFrameSelfTicks);
    public long CurrentFrameAllocatedBytes => Volatile.Read(ref this.currentFrameAllocatedBytes);

    public void AddSample(long inclusiveTicks, long selfTicks, long allocatedBytes)
    {
        Interlocked.Increment(ref this.totalCount);
        Interlocked.Add(ref this.sessionSumInclusiveTicks, inclusiveTicks);
        Interlocked.Add(ref this.sessionSumSelfTicks, selfTicks);
        Interlocked.Add(ref this.sessionSumAllocatedBytes, allocatedBytes);

        Interlocked.Increment(ref this.currentFrameCount);
        Interlocked.Add(ref this.currentFrameInclusiveTicks, inclusiveTicks);
        Interlocked.Add(ref this.currentFrameSelfTicks, selfTicks);
        Interlocked.Add(ref this.currentFrameAllocatedBytes, allocatedBytes);

        this.recentInclusiveTicks.Enqueue(inclusiveTicks);
        while (this.recentInclusiveTicks.Count > WindowSize)
        {
            this.recentInclusiveTicks.TryDequeue(out _);
        }
    }

    public void ResetCurrentFrame()
    {
        Volatile.Write(ref this.currentFrameCount, 0);
        Volatile.Write(ref this.currentFrameInclusiveTicks, 0);
        Volatile.Write(ref this.currentFrameSelfTicks, 0);
        Volatile.Write(ref this.currentFrameAllocatedBytes, 0);
    }

    public long[] GetRecentSamples() => this.recentInclusiveTicks.ToArray();

    public long GetPercentileTicks(double percentile)
    {
        var samples = this.recentInclusiveTicks.ToArray();
        if (samples.Length == 0)
        {
            return 0;
        }

        Array.Sort(samples);
        var index = Math.Clamp((int)Math.Ceiling(samples.Length * percentile) - 1, 0, samples.Length - 1);
        return samples[index];
    }
}

internal sealed class ProfilerTreeNode
{
    public int Id { get; init; }
    public int ParentId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public int Depth { get; init; }

    public int Count { get; init; }
    public double CallsPerFrame { get; init; }
    public double InclusiveAvgCallNs { get; init; }
    public double InclusiveAvgFrameNs { get; init; }
    public double SelfAvgCallNs { get; init; }
    public double SelfAvgFrameNs { get; init; }
    public double P95CallNs { get; init; }
    public double P99CallNs { get; init; }
    public double AvgAllocatedBytes { get; init; }

    public List<ProfilerTreeNode> Children { get; } = new();
}

internal sealed class ProfilerFlatRow(
    string name,
    int count,
    double callsPerFrame,
    double inclusiveAvgCallNs,
    double inclusiveAvgFrameNs,
    double selfAvgCallNs,
    double selfAvgFrameNs,
    double p95CallNs,
    double p99CallNs,
    double avgAllocatedBytes)
{
    public string Name { get; } = name;
    public int Count { get; } = count;
    public double CallsPerFrame { get; } = callsPerFrame;
    public double InclusiveAvgCallNs { get; } = inclusiveAvgCallNs;
    public double InclusiveAvgFrameNs { get; } = inclusiveAvgFrameNs;
    public double SelfAvgCallNs { get; } = selfAvgCallNs;
    public double SelfAvgFrameNs { get; } = selfAvgFrameNs;
    public double P95CallNs { get; } = p95CallNs;
    public double P99CallNs { get; } = p99CallNs;
    public double AvgAllocatedBytes { get; } = avgAllocatedBytes;
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
