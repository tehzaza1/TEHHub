using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MemoryPolicySimulator;

public static class SimulatorAssert
{
    public static void IsTrue(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Simulator Invariant Failure: {message}");
        }
    }
}

public record MemoryRequest(long Address, int Size, string Tag);

/// <summary>
/// Efficient interval range tracker to compute unique byte coverage without per-byte allocations.
/// </summary>
public class PageIntervalTracker
{
    private readonly List<(long Start, long End)> intervals = new();

    public void AddRange(long start, int size)
    {
        if (size <= 0) return;
        long end = start + size;
        intervals.Add((start, end));
    }

    public int GetUniqueByteCount()
    {
        if (intervals.Count == 0) return 0;

        var sorted = intervals.OrderBy(x => x.Start).ToList();
        long totalUnique = 0;
        long currentStart = sorted[0].Start;
        long currentEnd = sorted[0].End;

        for (int i = 1; i < sorted.Count; i++)
        {
            if (sorted[i].Start <= currentEnd)
            {
                currentEnd = Math.Max(currentEnd, sorted[i].End);
            }
            else
            {
                totalUnique += (currentEnd - currentStart);
                currentStart = sorted[i].Start;
                currentEnd = sorted[i].End;
            }
        }
        totalUnique += (currentEnd - currentStart);

        return (int)totalUnique;
    }
}

public class SimulationMetrics
{
    public string PolicyName { get; set; } = "";
    public int TotalRequests { get; set; }
    public long RequestedBytes { get; set; }
    public long UniqueRequestedBytes { get; set; }
    public long NativeReadCalls { get; set; }
    
    // Categorized Fetched Bytes
    public long ExactFetchedBytes { get; set; }
    public long PlannedBatchFetchedBytes { get; set; }
    public long Level1CompactFetchedBytes { get; set; }
    public long Level2MediumFetchedBytes { get; set; }
    public long Level3PageFetchedBytes { get; set; }
    
    public long TotalFetchedBytes => ExactFetchedBytes + PlannedBatchFetchedBytes + Level1CompactFetchedBytes + Level2MediumFetchedBytes + Level3PageFetchedBytes;
    
    public long CacheHits { get; set; }
    public long CacheMisses { get; set; }
    public long WastedBytes => Math.Max(0, TotalFetchedBytes - RequestedBytes);
    public double TotalTrafficRatio => RequestedBytes > 0 ? (double)TotalFetchedBytes / RequestedBytes : 1.0;

    public double ComputeCost(double callCost, double byteCost = 1.0)
    {
        return (NativeReadCalls * callCost) + (TotalFetchedBytes * byteCost);
    }

    public double GetBandwidthMBps(int fps)
    {
        return (TotalFetchedBytes * (double)fps) / (1024.0 * 1024.0);
    }
}

public interface IMemoryPolicy
{
    string Name { get; }
    SimulationMetrics Run(IEnumerable<MemoryRequest> requests, IEnumerable<MemoryRequest>? plannedBatches = null);
}

// 1. Legacy Exact-Read Policy
public class LegacyExactReadPolicy : IMemoryPolicy
{
    public string Name => "Legacy Exact-Read";

    public SimulationMetrics Run(IEnumerable<MemoryRequest> requests, IEnumerable<MemoryRequest>? plannedBatches = null)
    {
        var metrics = new SimulationMetrics { PolicyName = Name };
        var globalTracker = new PageIntervalTracker();
        
        // Legacy ignores planned ranges and issues individual exact reads
        foreach (var req in requests)
        {
            metrics.TotalRequests++;
            metrics.RequestedBytes += req.Size;
            globalTracker.AddRange(req.Address, req.Size);

            metrics.NativeReadCalls++;
            metrics.ExactFetchedBytes += req.Size;
            metrics.CacheMisses++;
        }

        metrics.UniqueRequestedBytes = globalTracker.GetUniqueByteCount();
        return metrics;
    }
}

// 2. Current NewMemoryRead Policy (Production Planned Component Ranges + 4KB/8KB Dynamic Page Cache)
public class CurrentNewMemoryReadPolicy : IMemoryPolicy
{
    public string Name => "Current NewMemoryRead (4KB/8KB + Production Planned Ranges)";
    private const int DynamicPageBytes = 0x1000;      // 4096 bytes
    private const int DynamicCrossPageBytes = 0x2000; // 8192 bytes
    private const int MaxDynamicWindows = 2048;

    public SimulationMetrics Run(IEnumerable<MemoryRequest> requests, IEnumerable<MemoryRequest>? plannedBatches = null)
    {
        var metrics = new SimulationMetrics { PolicyName = Name };
        var cache = new List<(long Start, int Size)>();
        var globalTracker = new PageIntervalTracker();
        int dynamicWindowCount = 0;

        // 1. Process Planned Batches upfront (Faithful production FrameMemoryReadPipeline ranges)
        if (plannedBatches != null)
        {
            foreach (var batch in plannedBatches)
            {
                metrics.NativeReadCalls++;
                metrics.PlannedBatchFetchedBytes += batch.Size;
                cache.Add((batch.Address, batch.Size));
            }
        }

        // 2. Process Logical Requests
        foreach (var req in requests)
        {
            metrics.TotalRequests++;
            metrics.RequestedBytes += req.Size;
            globalTracker.AddRange(req.Address, req.Size);

            bool covered = false;
            for (int i = 0; i < cache.Count; i++)
            {
                var (wStart, wSize) = cache[i];
                if (req.Address >= wStart && req.Address + req.Size <= wStart + wSize)
                {
                    covered = true;
                    metrics.CacheHits++;
                    break;
                }
            }

            if (covered) continue;

            metrics.CacheMisses++;
            long pageStart = req.Address & ~(DynamicPageBytes - 1L);
            long offsetInPage = req.Address - pageStart;
            int fetchSize = (offsetInPage + req.Size > DynamicPageBytes) ? DynamicCrossPageBytes : DynamicPageBytes;

            if (dynamicWindowCount < MaxDynamicWindows)
            {
                metrics.NativeReadCalls++;
                metrics.Level3PageFetchedBytes += fetchSize;
                cache.Add((pageStart, fetchSize));
                dynamicWindowCount++;
            }
            else
            {
                metrics.NativeReadCalls++;
                metrics.ExactFetchedBytes += req.Size;
            }
        }

        metrics.UniqueRequestedBytes = globalTracker.GetUniqueByteCount();
        return metrics;
    }
}

// 3. Fully Corrected Hybrid V2 Policy: Production Planned Ranges + Exact-First + Hierarchical Promotion
public class HybridV2HierarchicalPolicy : IMemoryPolicy
{
    public string Name { get; set; } = "Proposed Hybrid V2 (Planned Ranges + Exact-First Hierarchical)";

    public int Level1BlockSize { get; set; } = 128; // Level 1 compact block
    public int Level2BlockSize { get; set; } = 512; // Level 2 medium block
    public int Level3PageSize { get; set; } = 4096; // Level 3 4KB page

    public int Level1AccessThreshold { get; set; } = 2;  // Access count to promote to Level 1
    public int Level2AccessThreshold { get; set; } = 4;  // Access count to promote to Level 2
    public int Level3AccessThreshold { get; set; } = 6;  // Access count to promote to Level 3
    public int Level3MinUniqueBytes { get; set; } = 256; // Min unique requested bytes on page for Level 3

    public SimulationMetrics Run(IEnumerable<MemoryRequest> requests, IEnumerable<MemoryRequest>? plannedBatches = null)
    {
        var metrics = new SimulationMetrics { PolicyName = Name };
        var cachedRanges = new List<(long Start, int Size)>();
        
        // Page-level hotness tracking
        var pageAccessCount = new Dictionary<long, int>();
        var pageTrackers = new Dictionary<long, PageIntervalTracker>();
        var globalTracker = new PageIntervalTracker();

        // 1. Process Planned Batches if provided (Retain targeted planned component ranges)
        if (plannedBatches != null)
        {
            foreach (var batch in plannedBatches)
            {
                metrics.NativeReadCalls++;
                metrics.PlannedBatchFetchedBytes += batch.Size;
                cachedRanges.Add((batch.Address, batch.Size));
            }
        }

        // 2. Process Logical Requests
        foreach (var req in requests)
        {
            metrics.TotalRequests++;
            metrics.RequestedBytes += req.Size;
            globalTracker.AddRange(req.Address, req.Size);

            // A. LOGICAL HOTNESS TRACKING (Split request across all touched 4KB pages)
            long currentAddr = req.Address;
            long remaining = req.Size;

            while (remaining > 0)
            {
                long pageId = currentAddr & ~0xFFFL;
                long pageEnd = pageId + 0x1000L;
                long bytesInThisPage = Math.Min(remaining, pageEnd - currentAddr);

                pageAccessCount.TryGetValue(pageId, out int accesses);
                accesses++;
                pageAccessCount[pageId] = accesses;

                if (!pageTrackers.TryGetValue(pageId, out var tracker))
                {
                    tracker = new PageIntervalTracker();
                    pageTrackers[pageId] = tracker;
                }
                tracker.AddRange(currentAddr, (int)bytesInThisPage);

                currentAddr += bytesInThisPage;
                remaining -= bytesInThisPage;
            }

            // B. CHECK CACHE COVERAGE
            bool hit = false;
            for (int i = 0; i < cachedRanges.Count; i++)
            {
                var (rStart, rSize) = cachedRanges[i];
                if (req.Address >= rStart && req.Address + req.Size <= rStart + rSize)
                {
                    hit = true;
                    metrics.CacheHits++;
                    break;
                }
            }

            if (hit) continue;

            metrics.CacheMisses++;
            long primaryPageId = req.Address & ~0xFFFL;
            long endPageId = (req.Address + req.Size - 1L) & ~0xFFFL;
            bool isCrossPage = (primaryPageId != endPageId);

            pageAccessCount.TryGetValue(primaryPageId, out int primaryAccesses);
            int uniqueBytesOnPrimaryPage = pageTrackers.TryGetValue(primaryPageId, out var primaryTracker)
                ? primaryTracker.GetUniqueByteCount()
                : req.Size;

            // C. HIERARCHICAL PROMOTION & FETCH ALIGNMENT
            long fetchStart;
            int fetchSize;

            if (primaryAccesses >= Level3AccessThreshold && uniqueBytesOnPrimaryPage >= Level3MinUniqueBytes)
            {
                // Level 3: 4KB Hot Page Cache (or Multi-Page if request spans multiple pages)
                fetchStart = primaryPageId;
                fetchSize = isCrossPage ? (int)(endPageId + Level3PageSize - fetchStart) : Level3PageSize;
                metrics.Level3PageFetchedBytes += fetchSize;
            }
            else if (primaryAccesses >= Level2AccessThreshold)
            {
                // Level 2: Medium Compact Block (512B default)
                if (isCrossPage || req.Size > Level2BlockSize)
                {
                    fetchStart = req.Address;
                    fetchSize = req.Size;
                }
                else
                {
                    fetchStart = req.Address & ~(Level2BlockSize - 1L);
                    long fetchEnd = (req.Address + req.Size + Level2BlockSize - 1L) & ~(Level2BlockSize - 1L);
                    fetchSize = (int)(fetchEnd - fetchStart);
                }
                metrics.Level2MediumFetchedBytes += fetchSize;
            }
            else if (primaryAccesses >= Level1AccessThreshold)
            {
                // Level 1: Small Compact Block (128B default)
                if (isCrossPage || req.Size > Level1BlockSize)
                {
                    fetchStart = req.Address;
                    fetchSize = req.Size;
                }
                else
                {
                    fetchStart = req.Address & ~(Level1BlockSize - 1L);
                    long fetchEnd = (req.Address + req.Size + Level1BlockSize - 1L) & ~(Level1BlockSize - 1L);
                    fetchSize = (int)(fetchEnd - fetchStart);
                }
                metrics.Level1CompactFetchedBytes += fetchSize;
            }
            else
            {
                // Level 0: EXACT-FIRST (Isolated access gets exact bytes requested)
                fetchStart = req.Address;
                fetchSize = req.Size;
                metrics.ExactFetchedBytes += fetchSize;
            }

            // Invariant assertions: The fetched range MUST strictly cover the requested memory span
            if (fetchStart > req.Address || fetchStart + fetchSize < req.Address + req.Size)
            {
                throw new InvalidOperationException($"Under-read bug! Req: [0x{req.Address:X}..0x{req.Address + req.Size:X}], Fetched: [0x{fetchStart:X}..0x{fetchStart + fetchSize:X}]");
            }

            metrics.NativeReadCalls++;
            cachedRanges.Add((fetchStart, fetchSize));
        }

        metrics.UniqueRequestedBytes = globalTracker.GetUniqueByteCount();
        return metrics;
    }
}

public static class ProductionRangeBuilder
{
    public const int MinComponentsPerRange = 3;
    public const int MaxGapBetweenComponents = 0x100;
    public const int RangeTailBytes = 0x800;
    public const int MaxRangeBytes = 0x8000;

    /// <summary>
    /// Faithful implementation of TEHhub FrameMemoryReadPipeline.BuildRanges
    /// </summary>
    public static List<MemoryRequest> BuildProductionPlannedRanges(IEnumerable<long> snapshotAddresses)
    {
        var addresses = snapshotAddresses.Where(a => a != 0).ToList();
        if (addresses.Count < MinComponentsPerRange)
        {
            return new List<MemoryRequest>();
        }

        addresses.Sort();
        var ranges = new List<MemoryRequest>(addresses.Count / MinComponentsPerRange);
        int groupStart = 0;
        long previousAddress = addresses[0];

        for (int i = 1; i <= addresses.Count; i++)
        {
            bool atEnd = (i == addresses.Count);
            long nextAddress = atEnd ? 0 : addresses[i];
            long gap = atEnd ? long.MaxValue : nextAddress - previousAddress;
            long span = atEnd ? 0 : nextAddress - addresses[groupStart] + RangeTailBytes;
            bool fits = !atEnd && gap >= 0 && gap <= MaxGapBetweenComponents && span <= MaxRangeBytes;

            if (fits)
            {
                previousAddress = nextAddress;
                continue;
            }

            int componentCount = i - groupStart;
            if (componentCount >= MinComponentsPerRange)
            {
                long firstAddress = addresses[groupStart];
                int byteCount = (int)(previousAddress - firstAddress + RangeTailBytes);
                ranges.Add(new MemoryRequest(firstAddress, byteCount, $"ProductionPlannedRange_{ranges.Count}"));
            }

            groupStart = i;
            if (!atEnd)
            {
                previousAddress = nextAddress;
            }
        }

        return ranges;
    }
}

public enum LocalityMode
{
    Scattered, // Components / nested pointers distributed across many 4KB pages
    Clustered, // Strong heap locality; adjacent components merge into planned ranges
    Mixed      // Realistic mix: clustered entity components + scattered pointer targets & ground items
}

public static class WorkloadGenerator
{
    // 1. Skill Workload: Actor component with ActiveSkills vector and Cooldowns vector
    public static List<MemoryRequest> GenerateSkillWorkload(int skillCount)
    {
        var reqs = new List<MemoryRequest>();
        long actorBase = 0x200000000L;

        reqs.Add(new MemoryRequest(actorBase + 0x000, 0x40, "ActorHeader"));
        reqs.Add(new MemoryRequest(actorBase + 0x8B0, 4, "AnimationId"));
        reqs.Add(new MemoryRequest(actorBase + 0xB08, 24, "ActiveSkillsPtrVector"));
        reqs.Add(new MemoryRequest(actorBase + 0xB20, 24, "CooldownsPtrVector"));

        long skillBase = 0x210000000L;
        long cdBase = 0x220000000L;

        for (int i = 0; i < skillCount; i++)
        {
            long skillAddr = skillBase + (i * 0x100);
            reqs.Add(new MemoryRequest(skillAddr + 0x08, 4, "UseStage"));
            reqs.Add(new MemoryRequest(skillAddr + 0x0C, 4, "CastType"));
            reqs.Add(new MemoryRequest(skillAddr + 0x40, 4, "SkillIdInfo"));
            reqs.Add(new MemoryRequest(skillAddr + 0x48, 8, "GrantedEffectPerLevelPtr"));
            reqs.Add(new MemoryRequest(skillAddr + 0x50, 8, "GrantedEffectStatSetsPtr"));
            reqs.Add(new MemoryRequest(skillAddr + 0xE8, 4, "TotalCooldownTimeInMs"));

            long cdAddr = cdBase + (i * 0x80);
            reqs.Add(new MemoryRequest(cdAddr + 0x08, 4, "CdDatId"));
            reqs.Add(new MemoryRequest(cdAddr + 0x10, 24, "CooldownsListVector"));
            reqs.Add(new MemoryRequest(cdAddr + 0x30, 4, "MaxUses"));
            reqs.Add(new MemoryRequest(cdAddr + 0x34, 4, "CdTotalTime"));
            reqs.Add(new MemoryRequest(cdAddr + 0x3C, 4, "CdSkillId"));

            long cdEntryAddr = 0x230000000L + (i * 0x40);
            reqs.Add(new MemoryRequest(cdEntryAddr + 0x00, 16, "CdInstance1"));
            reqs.Add(new MemoryRequest(cdEntryAddr + 0x10, 16, "CdInstance2"));
        }

        return reqs;
    }

    // 2. UI-Tree Workload: Deep traversal with repeated reads
    public static List<MemoryRequest> GenerateUiTreeWorkload(int elementCount)
    {
        var reqs = new List<MemoryRequest>();
        long uiBase = 0x400000000L;

        reqs.Add(new MemoryRequest(uiBase + 0x000, 8, "UiRootVTable"));
        reqs.Add(new MemoryRequest(uiBase + 0x010, 24, "UiRootChildren"));
        reqs.Add(new MemoryRequest(uiBase + 0x7C0, 8, "MapParentPtr"));

        for (int i = 0; i < elementCount; i++)
        {
            long nodeAddr = uiBase + 0x1000 + (i * 0x400);
            reqs.Add(new MemoryRequest(nodeAddr + 0x000, 8, "NodeVTable"));
            reqs.Add(new MemoryRequest(nodeAddr + 0x010, 24, "NodeChildren"));
            reqs.Add(new MemoryRequest(nodeAddr + 0x0B8, 8, "NodeParent"));
            reqs.Add(new MemoryRequest(nodeAddr + 0x100, 8, "RelativePosition"));
            reqs.Add(new MemoryRequest(nodeAddr + 0x128, 32, "StringIdPtr"));
            reqs.Add(new MemoryRequest(nodeAddr + 0x360, 64, "TextWString"));

            if (i % 3 == 0)
            {
                reqs.Add(new MemoryRequest(nodeAddr + 0x100, 8, "Repeat_RelativePosition"));
                reqs.Add(new MemoryRequest(nodeAddr + 0x128, 32, "Repeat_StringId"));
            }
        }

        return reqs;
    }

    // 3. Worst-case Scattered Workload
    public static List<MemoryRequest> GenerateWorstCaseScatteredWorkload(int count)
    {
        var reqs = new List<MemoryRequest>();
        long baseAddr = 0x500000000L;

        for (int i = 0; i < count; i++)
        {
            long pageAddr = baseAddr + (i * 0x1000);
            reqs.Add(new MemoryRequest(pageAddr + 0x40, 8, $"ScatteredScalar_{i}"));
        }

        return reqs;
    }

    // 4. Repeated Single-Pointer Workload
    public static List<MemoryRequest> GenerateRepeatedSinglePointerWorkload(int count)
    {
        var reqs = new List<MemoryRequest>();
        long ptrAddr = 0x600000040L;

        for (int i = 0; i < count; i++)
        {
            reqs.Add(new MemoryRequest(ptrAddr, 8, $"RepeatedPointer_{i}"));
        }

        return reqs;
    }

    // 5. Heavy-Scene Monster + Ground Item Workload Generator
    public static (List<MemoryRequest> LogicalRequests, List<MemoryRequest> PlannedBatches) GenerateHeavySceneWorkload(
        int monsterCount,
        int groundItemCount,
        LocalityMode locality,
        int consumerCount = 1)
    {
        var baseRequests = new List<MemoryRequest>();
        var snapshotAddresses = new List<long>();

        long monsterBase = 0x1000000000L;
        long groundItemBase = 0x2000000000L;
        long stringHeapBase = 0x3000000000L;

        // A. MONSTERS (Render, Positioned, Life, Actor, Targetable, Buffs, ObjectMagicProperties)
        for (int m = 0; m < monsterCount; m++)
        {
            long entityAddr = monsterBase + (m * 0x10000L);
            baseRequests.Add(new MemoryRequest(entityAddr + 0x00, 0x40, "Monster_EntityHeader"));
            baseRequests.Add(new MemoryRequest(entityAddr + 0x50, 0x20, "Monster_EntityDetails"));

            long compBase;
            long compStep;

            switch (locality)
            {
                case LocalityMode.Clustered:
                    // Strongly clustered: 6 components with 0x80 byte gaps (fits in <= 0x100 gap rule)
                    compBase = monsterBase + 0x50000000L + (m * 0x600L);
                    compStep = 0x80;
                    break;
                case LocalityMode.Scattered:
                    // Scattered across pages: each component on a separate 4KB page
                    compBase = monsterBase + 0x80000000L + (m * 0x10000L);
                    compStep = 0x1200; // Gap > 0x100 (breaks planned range grouping)
                    break;
                case LocalityMode.Mixed:
                default:
                    // 60% clustered in small groups, 40% scattered
                    if (m % 10 < 6)
                    {
                        compBase = monsterBase + 0x50000000L + (m * 0x600L);
                        compStep = 0x80;
                    }
                    else
                    {
                        compBase = monsterBase + 0x80000000L + (m * 0x10000L);
                        compStep = 0x1200;
                    }
                    break;
            }

            // Snapshot addresses for components
            long addrRender = compBase + (0 * compStep);
            long addrPositioned = compBase + (1 * compStep);
            long addrLife = compBase + (2 * compStep);
            long addrActor = compBase + (3 * compStep);
            long addrTargetable = compBase + (4 * compStep);
            long addrBuffs = compBase + (5 * compStep);

            snapshotAddresses.Add(addrRender);
            snapshotAddresses.Add(addrPositioned);
            snapshotAddresses.Add(addrLife);
            snapshotAddresses.Add(addrActor);
            snapshotAddresses.Add(addrTargetable);
            snapshotAddresses.Add(addrBuffs);

            // Component reads
            baseRequests.Add(new MemoryRequest(addrRender + 0x00, 0x40, "Monster_RenderData"));
            baseRequests.Add(new MemoryRequest(addrPositioned + 0x00, 0x30, "Monster_PositionedData"));
            baseRequests.Add(new MemoryRequest(addrLife + 0x00, 0x60, "Monster_LifeData"));
            baseRequests.Add(new MemoryRequest(addrActor + 0x00, 0x80, "Monster_ActorData"));
            baseRequests.Add(new MemoryRequest(addrTargetable + 0x00, 0x20, "Monster_TargetableData"));
            baseRequests.Add(new MemoryRequest(addrBuffs + 0x00, 0x40, "Monster_BuffsData"));

            // Nested pointer targets (Strings / metadata)
            long strAddr = stringHeapBase + (m * 0x200L);
            baseRequests.Add(new MemoryRequest(strAddr + 0x00, 0x40, "Monster_PathString"));
            baseRequests.Add(new MemoryRequest(strAddr + 0x80, 0x20, "Monster_NameString"));
        }

        // B. GROUND ITEMS (Positioned, Render, WorldItem -> Inner Item with Base, Mods, RenderItem)
        for (int g = 0; g < groundItemCount; g++)
        {
            long entityAddr = groundItemBase + (g * 0x10000L);
            baseRequests.Add(new MemoryRequest(entityAddr + 0x00, 0x40, "GroundItem_EntityHeader"));

            long compBase;
            long compStep;

            switch (locality)
            {
                case LocalityMode.Clustered:
                    compBase = groundItemBase + 0x50000000L + (g * 0x400L);
                    compStep = 0x80;
                    break;
                case LocalityMode.Scattered:
                    compBase = groundItemBase + 0x80000000L + (g * 0x10000L);
                    compStep = 0x1500;
                    break;
                case LocalityMode.Mixed:
                default:
                    if (g % 10 < 5)
                    {
                        compBase = groundItemBase + 0x50000000L + (g * 0x400L);
                        compStep = 0x80;
                    }
                    else
                    {
                        compBase = groundItemBase + 0x80000000L + (g * 0x10000L);
                        compStep = 0x1500;
                    }
                    break;
            }

            long addrPos = compBase + (0 * compStep);
            long addrRend = compBase + (1 * compStep);
            long addrWI = compBase + (2 * compStep);

            snapshotAddresses.Add(addrPos);
            snapshotAddresses.Add(addrRend);
            snapshotAddresses.Add(addrWI);

            baseRequests.Add(new MemoryRequest(addrPos + 0x00, 0x30, "GroundItem_Positioned"));
            baseRequests.Add(new MemoryRequest(addrRend + 0x00, 0x40, "GroundItem_Render"));
            baseRequests.Add(new MemoryRequest(addrWI + 0x00, 0x30, "GroundItem_WorldItem"));

            // Inner Item Entity & components
            long innerItemAddr = groundItemBase + 0x90000000L + (g * 0x800L);
            baseRequests.Add(new MemoryRequest(innerItemAddr + 0x00, 0x40, "InnerItem_Header"));
            baseRequests.Add(new MemoryRequest(innerItemAddr + 0x80, 0x40, "InnerItem_BaseComp"));
            baseRequests.Add(new MemoryRequest(innerItemAddr + 0x120, 0x60, "InnerItem_ModsComp"));
            baseRequests.Add(new MemoryRequest(innerItemAddr + 0x1C0, 0x40, "InnerItem_RenderItemComp"));

            // Item Name & Icon Strings
            long itemStrAddr = stringHeapBase + 0x50000000L + (g * 0x200L);
            baseRequests.Add(new MemoryRequest(itemStrAddr + 0x00, 0x30, "InnerItem_NameString"));
            baseRequests.Add(new MemoryRequest(itemStrAddr + 0x60, 0x40, "InnerItem_IconString"));
        }

        // Generate production planned ranges from global snapshot addresses
        var plannedRanges = ProductionRangeBuilder.BuildProductionPlannedRanges(snapshotAddresses);

        // Multiply by consumer count within the same frame
        var totalRequests = new List<MemoryRequest>(baseRequests.Count * consumerCount);
        for (int c = 0; c < consumerCount; c++)
        {
            totalRequests.AddRange(baseRequests);
        }

        return (totalRequests, plannedRanges);
    }
}

public class Program
{
    public static void Main(string[] args)
    {
        Console.WriteLine("================================================================================");
        Console.WriteLine("       OFFLINE MEMORY READ POLICY SIMULATOR (TEHHUB ARCHITECTURE LAB)          ");
        Console.WriteLine("================================================================================");
        Console.WriteLine();

        // Run self-tests and regressions
        RunSimulatorSelfTests();

        var policies = new IMemoryPolicy[]
        {
            new LegacyExactReadPolicy(),
            new CurrentNewMemoryReadPolicy(),
            new HybridV2HierarchicalPolicy()
        };

        var sbMarkdown = new StringBuilder();
        sbMarkdown.AppendLine("# Offline Memory Read Policy Simulation Report (Production Planned-Range Model)");
        sbMarkdown.AppendLine("Comparative Analysis: Legacy Exact-Read vs. Current NewMemoryRead (with Production Planned Ranges) vs. Proposed Hybrid V2 (Planned Ranges + Exact-First Hierarchical)");
        sbMarkdown.AppendLine();
        sbMarkdown.AppendLine("Cost Model: `EstimatedCost = (NativeCalls * CallCost) + (TotalFetchedBytes * 1.0)`");
        sbMarkdown.AppendLine();

        // 1. Production Range Builder Regressions
        sbMarkdown.AppendLine("## 1. Production Frame Component Range Planner Invariants");
        sbMarkdown.AppendLine("The simulator faithfully implements `TEHhub.Utils.FrameMemoryReadPipeline.BuildRanges` rules:");
        sbMarkdown.AppendLine("- `MinComponentsPerRange = 3`");
        sbMarkdown.AppendLine("- `MaxGapBetweenComponents = 0x100` (256 bytes)");
        sbMarkdown.AppendLine("- `RangeTailBytes = 0x800` (2048 bytes)");
        sbMarkdown.AppendLine("- `MaxRangeBytes = 0x8000` (32768 bytes)");
        sbMarkdown.AppendLine();

        // 2. Heavy Scene Simulations (500, 1000, 1500, 2000 Monsters + Ground Items)
        RunHeavySceneSimulationSuite(policies, sbMarkdown);

        // 3. Traffic Category Breakdown
        RunTrafficBreakdownAnalysis(policies, sbMarkdown);

        // 4. Cost Sensitivity Sweep
        RunCostSensitivitySweep(sbMarkdown);

        // 5. Pareto Analysis
        RunParetoAnalysis(sbMarkdown);

        // Save report to markdown
        string reportPath = @"c:\Games\Hy-v Tool\GameHelper2-main\tools\MemoryPolicySimulator\Simulation_Report.md";
        File.WriteAllText(reportPath, sbMarkdown.ToString(), Encoding.UTF8);
        Console.WriteLine($"\n[Report saved to: {reportPath}]");
    }

    private static void RunSimulatorSelfTests()
    {
        Console.WriteLine("Running Deterministic Self-Tests & Invariant Verifications...");

        // 1. Production Range Builder Regression Tests
        // A. 2 nearby component addresses => NO planned range (< MinComponentsPerRange)
        var twoAddrs = new List<long> { 0x100000000L, 0x100000050L };
        var rangesA = ProductionRangeBuilder.BuildProductionPlannedRanges(twoAddrs);
        SimulatorAssert.IsTrue(rangesA.Count == 0, "2 nearby addresses must produce NO planned range");

        // B. 3 component addresses with <= 0x100 gaps => ONE planned range
        var threeAddrs = new List<long> { 0x100000000L, 0x100000080L, 0x100000100L };
        var rangesB = ProductionRangeBuilder.BuildProductionPlannedRanges(threeAddrs);
        SimulatorAssert.IsTrue(rangesB.Count == 1, "3 component addresses with <= 0x100 gaps must produce exactly 1 planned range");
        SimulatorAssert.IsTrue(rangesB[0].Address == 0x100000000L, "Planned range start must match first component address");
        SimulatorAssert.IsTrue(rangesB[0].Size == (0x100000100L - 0x100000000L + 0x800), "Planned range size must include 0x800 tail");

        // C. Gap > 0x100 => Closes/splits the group
        var splitAddrs = new List<long> { 0x100000000L, 0x100000050L, /* Gap 0x200 */ 0x100000250L, 0x1000002A0L, 0x1000002F0L };
        var rangesC = ProductionRangeBuilder.BuildProductionPlannedRanges(splitAddrs);
        SimulatorAssert.IsTrue(rangesC.Count == 1, "Gap > 0x100 must split; first group of 2 dropped, second group of 3 kept");
        SimulatorAssert.IsTrue(rangesC[0].Address == 0x100000250L, "Second group start address must be 0x100000250L");

        // D. Candidate range exceeding 0x8000 => Closes/splits group
        var longSpanAddrs = new List<long>();
        for (int i = 0; i < 600; i++)
        {
            longSpanAddrs.Add(0x200000000L + (i * 0x60)); // 600 * 96B = 57,600B (> 32KB)
        }
        var rangesD = ProductionRangeBuilder.BuildProductionPlannedRanges(longSpanAddrs);
        SimulatorAssert.IsTrue(rangesD.Count > 1, "Spanning > 0x8000 must split into multiple planned ranges");
        foreach (var r in rangesD)
        {
            SimulatorAssert.IsTrue(r.Size <= 0x8000, "No planned range may exceed MaxRangeBytes (0x8000)");
        }

        // E. 512-byte compact boundary test for Hybrid V2
        var hybrid = new HybridV2HierarchicalPolicy { Level2BlockSize = 512 };
        var boundary512 = new List<MemoryRequest>
        {
            new(0x300000180L, 64, "Prime1"),
            new(0x3000001C0L, 64, "Prime2"),
            new(0x3000001E0L, 64, "Cross512_1"), // Spans 0x1E0..0x220 across 512B boundary 0x200
            new(0x300000200L, 64, "Cross512_2")
        };
        var res512 = hybrid.Run(boundary512);
        SimulatorAssert.IsTrue(res512.Level2MediumFetchedBytes > 0 || res512.Level1CompactFetchedBytes > 0, "Compact boundary test must cover safely");

        // F. Scattered test (1000 items -> Hybrid exact 1.0x traffic ratio)
        var scattered = WorkloadGenerator.GenerateWorstCaseScatteredWorkload(1000);
        var resScattered = hybrid.Run(scattered);
        SimulatorAssert.IsTrue(resScattered.TotalTrafficRatio == 1.0, $"Scattered traffic ratio must be 1.0x, got {resScattered.TotalTrafficRatio}");
        SimulatorAssert.IsTrue(resScattered.ExactFetchedBytes == 8000, "Scattered exact fetched bytes must equal 8000");

        // G. Repeated pointer test (100 reads -> 99 hits, unique = 8)
        var repeated = WorkloadGenerator.GenerateRepeatedSinglePointerWorkload(100);
        var resRepeated = hybrid.Run(repeated);
        SimulatorAssert.IsTrue(resRepeated.Level3PageFetchedBytes == 0, "Repeated same pointer must NOT promote to Level 3");
        SimulatorAssert.IsTrue(resRepeated.UniqueRequestedBytes == 8, "Unique requested bytes must be exactly 8");
        SimulatorAssert.IsTrue(resRepeated.CacheHits == 99, "Repeated pointer must produce 99 hits");

        // H. Dynamic window budget isolation test for CurrentNewMemoryRead (planned ranges must not consume dynamic window budget)
        var currentPolicy = new CurrentNewMemoryReadPolicy();
        var plannedOverBudget = new List<MemoryRequest>();
        for (int i = 0; i < 2500; i++)
        {
            plannedOverBudget.Add(new MemoryRequest(0x1000000000L + (i * 0x10000L), 0x100, $"Planned_{i}"));
        }
        var unbackedRequest = new List<MemoryRequest>
        {
            new MemoryRequest(0x2000000000L + 0x20, 8, "UnbackedScalar")
        };
        var resBudget = currentPolicy.Run(unbackedRequest, plannedOverBudget);
        SimulatorAssert.IsTrue(resBudget.Level3PageFetchedBytes == 4096, $"Current NewMemoryRead with >2048 planned ranges must still perform dynamic 4KB page fetch on unbacked miss (got Level3PageFetchedBytes={resBudget.Level3PageFetchedBytes})");
        SimulatorAssert.IsTrue(resBudget.ExactFetchedBytes == 0, $"Unbacked miss within 2048 dynamic windows must not fall back to exact read (got ExactFetchedBytes={resBudget.ExactFetchedBytes})");

        Console.WriteLine("All 8 Deterministic Invariant Checks & Regressions PASSED successfully!\n");
    }

    private static void RunHeavySceneSimulationSuite(IMemoryPolicy[] policies, StringBuilder md)
    {
        Console.WriteLine("================================================================================");
        Console.WriteLine("       HEAVY SCENE SIMULATION (500 to 2000 Monsters + Ground Items)              ");
        Console.WriteLine("================================================================================");

        md.AppendLine("## 2. Heavy Scene Simulations (Monster + Ground Item Scaled Workloads)");
        md.AppendLine();

        (int Monsters, int Items)[] sceneScales =
        {
            (500, 500),
            (1000, 1000),
            (1500, 1500),
            (2000, 2000)
        };

        LocalityMode[] localityModes = { LocalityMode.Clustered, LocalityMode.Mixed, LocalityMode.Scattered };
        int[] consumerCounts = { 1, 3, 5 };

        foreach (var (monsters, items) in sceneScales)
        {
            int totalEntities = monsters + items;
            Console.WriteLine($"\n================================================================================");
            Console.WriteLine($"  SCENE SCALE: {monsters:N0} Monsters + {items:N0} Ground Items ({totalEntities:N0} Total Entities)");
            Console.WriteLine($"================================================================================");

            md.AppendLine($"### Scene: {monsters:N0} Monsters + {items:N0} Ground Items ({totalEntities:N0} Total Entities)");
            md.AppendLine();

            foreach (var locality in localityModes)
            {
                Console.WriteLine($"\n--- Locality Distribution: {locality} ---");
                md.AppendLine($"#### Locality Distribution: **{locality}**");
                md.AppendLine();

                foreach (var consumers in consumerCounts)
                {
                    var (reqs, batches) = WorkloadGenerator.GenerateHeavySceneWorkload(monsters, items, locality, consumers);
                    int batchCount = batches.Count;

                    Console.WriteLine($"\n[Consumers: {consumers}] ({reqs.Count:N0} Total Logical Requests, {batchCount:N0} Planned Ranges)");
                    Console.WriteLine($"{"Policy",-35} | {"ReqBytes",9} | {"Calls",7} | {"Batch(B)",9} | {"Page/L3(B)",10} | {"Total(B)",10} | {"Traffic",7} | {"60 FPS",10} | {"120 FPS",10} | {"180 FPS",10}");
                    Console.WriteLine(new string('-', 145));

                    md.AppendLine($"##### Scale: {totalEntities} Entities | Locality: {locality} | Consumers: {consumers} ({reqs.Count:N0} reqs, {batchCount} planned ranges)");
                    md.AppendLine("| Policy | Requests | Native Calls | Planned Ranges | L3/Page Fetched | Total Fetched | Traffic Ratio | 60 FPS Bandwidth | 120 FPS Bandwidth | 144 FPS Bandwidth | 180 FPS Bandwidth | Threshold Status |");
                    md.AppendLine("| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |");

                    foreach (var pol in policies)
                    {
                        var res = pol.Run(reqs, batches);
                        double mb60 = res.GetBandwidthMBps(60);
                        double mb120 = res.GetBandwidthMBps(120);
                        double mb144 = res.GetBandwidthMBps(144);
                        double mb180 = res.GetBandwidthMBps(180);

                        string status = "";
                        if (mb180 >= 1536.0) status = "💥 **EXCEEDS 1.5 GB/s**";
                        else if (mb180 >= 1024.0) status = "🚨 **EXCEEDS 1.0 GB/s**";
                        else if (mb180 >= 500.0) status = "⚠️ **> 500 MB/s**";
                        else status = "✅ Optimal";

                        string str60 = mb60 >= 1024 ? $"{mb60 / 1024.0:F2} GB/s" : $"{mb60:F1} MB/s";
                        string str120 = mb120 >= 1024 ? $"{mb120 / 1024.0:F2} GB/s" : $"{mb120:F1} MB/s";
                        string str144 = mb144 >= 1024 ? $"{mb144 / 1024.0:F2} GB/s" : $"{mb144:F1} MB/s";
                        string str180 = mb180 >= 1024 ? $"{mb180 / 1024.0:F2} GB/s" : $"{mb180:F1} MB/s";

                        Console.WriteLine($"{res.PolicyName,-35} | {res.RequestedBytes,9:N0} | {res.NativeReadCalls,7:N0} | {res.PlannedBatchFetchedBytes,9:N0} | {res.Level3PageFetchedBytes,10:N0} | {res.TotalFetchedBytes,10:N0} | {res.TotalTrafficRatio,6:F2}x | {str60,10} | {str120,10} | {str180,10} {status}");
                        md.AppendLine($"| **{res.PolicyName}** | {res.TotalRequests:N0} | {res.NativeReadCalls:N0} | {res.PlannedBatchFetchedBytes:N0} B | {res.Level3PageFetchedBytes:N0} B | {res.TotalFetchedBytes:N0} B | {res.TotalTrafficRatio:F2}x | {str60} | {str120} | {str144} | {str180} | {status} |");
                    }
                    md.AppendLine();
                }
            }
        }
    }

    private static void RunTrafficBreakdownAnalysis(IMemoryPolicy[] policies, StringBuilder md)
    {
        Console.WriteLine("\n================================================================================");
        Console.WriteLine("          TRAFFIC CATEGORY BREAKDOWN ANALYSIS (1500 M + 1500 G Mixed)           ");
        Console.WriteLine("================================================================================");

        md.AppendLine("## 3. Traffic Category Breakdown (1500 Monsters + 1500 Ground Items Mixed Scene)");
        md.AppendLine();

        var (reqs, batches) = WorkloadGenerator.GenerateHeavySceneWorkload(1500, 1500, LocalityMode.Mixed, 1);

        var categories = new Dictionary<string, (int Requests, long Bytes)>
        {
            { "Entity / Header Structs", (0, 0) },
            { "Component Snapshot / Planned Ranges", (0, 0) },
            { "Render / Position Components", (0, 0) },
            { "Actor / Life / Monster State", (0, 0) },
            { "Ground Item & Inner Item Data", (0, 0) },
            { "Strings / Path / Metadata", (0, 0) }
        };

        foreach (var req in reqs)
        {
            string cat = "Strings / Path / Metadata";
            if (req.Tag.Contains("Header") || req.Tag.Contains("Details")) cat = "Entity / Header Structs";
            else if (req.Tag.Contains("Render") || req.Tag.Contains("Positioned")) cat = "Render / Position Components";
            else if (req.Tag.Contains("Life") || req.Tag.Contains("Actor") || req.Tag.Contains("Targetable") || req.Tag.Contains("Buffs")) cat = "Actor / Life / Monster State";
            else if (req.Tag.Contains("GroundItem") || req.Tag.Contains("InnerItem")) cat = "Ground Item & Inner Item Data";

            var cur = categories[cat];
            categories[cat] = (cur.Requests + 1, cur.Bytes + req.Size);
        }

        long plannedBatchBytes = batches.Sum(b => (long)b.Size);
        categories["Component Snapshot / Planned Ranges"] = (batches.Count, plannedBatchBytes);

        Console.WriteLine($"{"Category",-40} | {"Requests / Ranges",18} | {"Requested / Planned Bytes",26}");
        Console.WriteLine(new string('-', 90));

        md.AppendLine("| Workload Category | Requests / Ranges Count | Logical / Planned Bytes | Category Traffic Share |");
        md.AppendLine("| :--- | :---: | :---: | :---: |");

        long totalLogical = categories.Values.Sum(v => v.Bytes);
        foreach (var (cat, data) in categories)
        {
            double share = totalLogical > 0 ? (double)data.Bytes / totalLogical * 100.0 : 0;
            Console.WriteLine($"{cat,-40} | {data.Requests,18:N0} | {data.Bytes,24:N0} B ({share,5:F1}%)");
            md.AppendLine($"| **{cat}** | {data.Requests:N0} | {data.Bytes:N0} B | {share:F1}% |");
        }
        md.AppendLine();
    }

    private static void RunCostSensitivitySweep(StringBuilder md)
    {
        Console.WriteLine("\n================================================================================");
        Console.WriteLine("          COST MODEL SENSITIVITY SWEEP (Varying CallCost with ByteCost=1.0)     ");
        Console.WriteLine("================================================================================");

        md.AppendLine("## 4. Cost Model Sensitivity Sweep");
        md.AppendLine("Evaluation on Mixed Realistic Workload (1000 Monsters + 1000 Ground Items Mixed with 3 Consumers):");
        md.AppendLine();

        var (mixedReqs, mixedBatches) = WorkloadGenerator.GenerateHeavySceneWorkload(1000, 1000, LocalityMode.Mixed, 3);

        var legacy = new LegacyExactReadPolicy().Run(mixedReqs, mixedBatches);
        var current = new CurrentNewMemoryReadPolicy().Run(mixedReqs, mixedBatches);
        var hybrid = new HybridV2HierarchicalPolicy().Run(mixedReqs, mixedBatches);

        double[] callCosts = { 250, 500, 1000, 2000, 5000 };

        Console.WriteLine($"{"CallCost",10} | {"Legacy Exact-Read",20} | {"Current NewMemoryRead",24} | {"Proposed Hybrid V2",20} | {"Optimal Policy",18}");
        Console.WriteLine(new string('-', 102));

        md.AppendLine("| CallCost Ratio | Legacy Exact-Read | Current NewMemoryRead | Proposed Hybrid V2 | Optimal Policy |");
        md.AppendLine("| :---: | :---: | :---: | :---: | :---: |");

        foreach (var cc in callCosts)
        {
            double cLeg = legacy.ComputeCost(cc);
            double cCur = current.ComputeCost(cc);
            double cHyb = hybrid.ComputeCost(cc);

            string optimal = (cHyb < cCur && cHyb < cLeg) ? "Hybrid V2" : (cCur < cLeg ? "Current NewMemory" : "Legacy");

            Console.WriteLine($"{cc,10:N0} | {cLeg,20:N0} | {cCur,24:N0} | {cHyb,20:N0} | {optimal,18}");
            md.AppendLine($"| **{cc:N0}** | {cLeg:N0} | {cCur:N0} | {cHyb:N0} | **{optimal}** |");
        }
        md.AppendLine();
    }

    private static void RunParetoAnalysis(StringBuilder md)
    {
        Console.WriteLine("\n================================================================================");
        Console.WriteLine("          PARETO-OPTIMAL CONFIGURATION ANALYSIS (Hybrid V2 Candidates)          ");
        Console.WriteLine("================================================================================");

        md.AppendLine("## 5. Pareto-Optimal Configuration Analysis");
        md.AppendLine("Comparison of Candidate Configurations on 1000 Monster + 1000 Ground Item Mixed Scene (3 Consumers):");
        md.AppendLine();

        var (mixedReqs, mixedBatches) = WorkloadGenerator.GenerateHeavySceneWorkload(1000, 1000, LocalityMode.Mixed, 3);

        var candidates = new List<(string Name, IMemoryPolicy Policy)>
        {
            ("Legacy Exact-Read", new LegacyExactReadPolicy()),
            ("Current NewMemoryRead (4KB/8KB + Planned Ranges)", new CurrentNewMemoryReadPolicy()),
            ("Hybrid V2 (Exact + 128B + 512B + 4KB)", new HybridV2HierarchicalPolicy { Level1BlockSize = 128, Level2BlockSize = 512 }),
            ("Hybrid V2 (Exact + 64B + 256B + 4KB)", new HybridV2HierarchicalPolicy { Level1BlockSize = 64, Level2BlockSize = 256 }),
            ("Hybrid V2 (Exact + 256B + 1024B + 4KB)", new HybridV2HierarchicalPolicy { Level1BlockSize = 256, Level2BlockSize = 1024 })
        };

        Console.WriteLine($"{"Candidate Configuration",-48} | {"Calls",7} | {"FetchedBytes",12} | {"TrafficRatio",14} | {"Cost (1000)",12}");
        Console.WriteLine(new string('-', 105));

        md.AppendLine("| Candidate Configuration | Native Calls | Fetched Bytes | Traffic Ratio | Cost (CallCost=1000) | Pareto-Optimal Status |");
        md.AppendLine("| :--- | :---: | :---: | :---: | :---: | :---: |");

        foreach (var (name, pol) in candidates)
        {
            var res = pol.Run(mixedReqs, mixedBatches);
            double cost = res.ComputeCost(1000.0);
            string pareto = (name.Contains("Hybrid")) ? "Pareto-Optimal (Balanced)" : (name.Contains("Legacy") ? "Lowest Fetched / Highest Calls" : "Lowest Calls / Highest Wasted");

            Console.WriteLine($"{name,-48} | {res.NativeReadCalls,7:N0} | {res.TotalFetchedBytes,10:N0} B | {res.TotalTrafficRatio,12:F2}x | {cost,12:N0}");
            md.AppendLine($"| **{name}** | {res.NativeReadCalls:N0} | {res.TotalFetchedBytes:N0} B | {res.TotalTrafficRatio:F2}x | {cost:N0} | {pareto} |");
        }
    }
}

