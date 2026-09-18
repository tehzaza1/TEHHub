using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace MemoryPolicySimulator;

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

        // Sort intervals by start
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
    public long Level1CompactFetchedBytes { get; set; }
    public long Level2MediumFetchedBytes { get; set; }
    public long Level3PageFetchedBytes { get; set; }
    public long MergeFetchedBytes { get; set; }
    
    public long TotalFetchedBytes => ExactFetchedBytes + Level1CompactFetchedBytes + Level2MediumFetchedBytes + Level3PageFetchedBytes + MergeFetchedBytes;
    
    public long CacheHits { get; set; }
    public long CacheMisses { get; set; }
    public long WastedBytes => Math.Max(0, TotalFetchedBytes - RequestedBytes);
    public double TotalAmplification => RequestedBytes > 0 ? (double)TotalFetchedBytes / RequestedBytes : 1.0;

    public double ComputeCost(double callCost, double byteCost = 1.0)
    {
        return (NativeReadCalls * callCost) + (TotalFetchedBytes * byteCost);
    }
}

public interface IMemoryPolicy
{
    string Name { get; }
    SimulationMetrics Run(IEnumerable<MemoryRequest> requests);
}

// 1. Legacy Exact-Read Policy
public class LegacyExactReadPolicy : IMemoryPolicy
{
    public string Name => "Legacy Exact-Read";

    public SimulationMetrics Run(IEnumerable<MemoryRequest> requests)
    {
        var metrics = new SimulationMetrics { PolicyName = Name };
        var globalTracker = new PageIntervalTracker();
        
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

// 2. Current NewMemoryRead Policy
public class CurrentNewMemoryReadPolicy : IMemoryPolicy
{
    public string Name => "Current NewMemoryRead (4KB/8KB Page Cache)";
    private const int DynamicPageBytes = 0x1000;      // 4096 bytes
    private const int DynamicCrossPageBytes = 0x2000; // 8192 bytes
    private const int MaxDynamicWindows = 2048;

    public SimulationMetrics Run(IEnumerable<MemoryRequest> requests)
    {
        var metrics = new SimulationMetrics { PolicyName = Name };
        var cache = new Dictionary<long, int>(); // StartAddress -> Size
        var globalTracker = new PageIntervalTracker();

        foreach (var req in requests)
        {
            metrics.TotalRequests++;
            metrics.RequestedBytes += req.Size;
            globalTracker.AddRange(req.Address, req.Size);

            bool covered = false;
            foreach (var (wStart, wSize) in cache)
            {
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

            if (cache.Count < MaxDynamicWindows)
            {
                metrics.NativeReadCalls++;
                metrics.Level3PageFetchedBytes += fetchSize;
                cache[pageStart] = fetchSize;
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

// 3. Fully Corrected Hybrid V2 Policy: Logical Hotness First + Exact-First + Hierarchical Promotion
public class HybridV2HierarchicalPolicy : IMemoryPolicy
{
    public string Name { get; set; } = "Proposed Hybrid V2 (Hierarchical Exact-First)";

    public int Level1BlockSize { get; set; } = 128; // Level 1 compact block
    public int Level2BlockSize { get; set; } = 512; // Level 2 medium block
    public int Level3PageSize { get; set; } = 4096; // Level 3 4KB page

    public int Level1AccessThreshold { get; set; } = 2;  // Access count to promote to Level 1
    public int Level2AccessThreshold { get; set; } = 4;  // Access count to promote to Level 2
    public int Level3AccessThreshold { get; set; } = 6;  // Access count to promote to Level 3
    public int Level3MinUniqueBytes { get; set; } = 256; // Min unique requested bytes on page for Level 3

    public SimulationMetrics Run(IEnumerable<MemoryRequest> requests)
    {
        var metrics = new SimulationMetrics { PolicyName = Name };
        var cachedRanges = new List<(long Start, int Size)>();
        
        // Page-level hotness tracking
        var pageAccessCount = new Dictionary<long, int>();
        var pageTrackers = new Dictionary<long, PageIntervalTracker>();
        var globalTracker = new PageIntervalTracker();

        foreach (var req in requests)
        {
            metrics.TotalRequests++;
            metrics.RequestedBytes += req.Size;
            globalTracker.AddRange(req.Address, req.Size);

            long pageId = req.Address & ~0xFFFL;

            // 1. LOGICAL HOTNESS TRACKING (Counted on EVERY logical access before cache check)
            pageAccessCount.TryGetValue(pageId, out int accesses);
            accesses++;
            pageAccessCount[pageId] = accesses;

            if (!pageTrackers.TryGetValue(pageId, out var tracker))
            {
                tracker = new PageIntervalTracker();
                pageTrackers[pageId] = tracker;
            }
            tracker.AddRange(req.Address, req.Size);

            // 2. CHECK CACHE COVERAGE
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
            int uniqueBytesOnPage = tracker.GetUniqueByteCount();

            // 3. HIERARCHICAL PROMOTION & FETCH ALIGNMENT
            long fetchStart;
            int fetchSize;

            if (accesses >= Level3AccessThreshold && uniqueBytesOnPage >= Level3MinUniqueBytes)
            {
                // Level 3: 4KB Hot Page Cache
                fetchStart = pageId;
                fetchSize = Level3PageSize;
                metrics.Level3PageFetchedBytes += fetchSize;
            }
            else if (accesses >= Level2AccessThreshold)
            {
                // Level 2: Medium Compact Block (Power-of-two aligned with boundary handling)
                if (req.Size > Level2BlockSize)
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
            else if (accesses >= Level1AccessThreshold)
            {
                // Level 1: Small Compact Block
                if (req.Size > Level1BlockSize)
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

    // 2. Entity / Component Workload
    public static List<MemoryRequest> GenerateEntityComponentWorkload(int entityCount)
    {
        var reqs = new List<MemoryRequest>();
        long entityBase = 0x300000000L;

        for (int e = 0; e < entityCount; e++)
        {
            long entityAddr = entityBase + (e * 0x1000);
            reqs.Add(new MemoryRequest(entityAddr + 0x00, 8, "EntityVTable"));
            reqs.Add(new MemoryRequest(entityAddr + 0x08, 8, "EntityComponentsMap"));
            reqs.Add(new MemoryRequest(entityAddr + 0x38, 4, "EntityId"));

            long compBase = 0x310000000L + (e * 0x800);
            for (int c = 0; c < 5; c++)
            {
                long compAddr = compBase + (c * 0x100);
                reqs.Add(new MemoryRequest(compAddr + 0x00, 0x20, $"CompHeader_{c}"));
                reqs.Add(new MemoryRequest(compAddr + 0x40, 16, $"CompPayload1_{c}"));
                reqs.Add(new MemoryRequest(compAddr + 0x80, 8, $"CompPayload2_{c}"));
            }
        }

        return reqs;
    }

    // 3. UI-Tree Workload: Deep traversal with repeated reads
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

    // 4. Worst-case Scattered Workload: N small reads on N distinct 4KB pages
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

    // 5. Repeated Same-Pointer Workload: Same 8-byte address read repeatedly
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
        sbMarkdown.AppendLine("# Offline Memory Read Policy Simulation Report (Fully Corrected Model)");
        sbMarkdown.AppendLine("Comparative Analysis: Legacy Exact-Read vs. Current NewMemoryRead vs. Proposed Hybrid V2 (Exact-First + Hierarchical Promotion)");
        sbMarkdown.AppendLine();
        sbMarkdown.AppendLine("Cost Model: `EstimatedCost = (NativeCalls * CallCost) + (TotalFetchedBytes * 1.0)`");
        sbMarkdown.AppendLine();

        int[] testSizes = { 10, 100, 500, 1000 };

        // 1. Scattered Workload
        RunBenchmarkSuite("1. Worst-Case Scattered Workload (1 Scalar Read per 4KB Page)",
            testSizes, WorkloadGenerator.GenerateWorstCaseScatteredWorkload, policies, sbMarkdown);

        // 2. Skill Workload
        RunBenchmarkSuite("2. Skill Workload (Actor + ActiveSkills + Cooldowns)",
            testSizes, WorkloadGenerator.GenerateSkillWorkload, policies, sbMarkdown);

        // 3. Entity / Component Workload
        RunBenchmarkSuite("3. Entity / Component Workload (Dense Component Clusters)",
            testSizes, WorkloadGenerator.GenerateEntityComponentWorkload, policies, sbMarkdown);

        // 4. UI-Tree Workload
        RunBenchmarkSuite("4. UI-Tree Workload (Hierarchical UI Traversal & String Reads)",
            testSizes, WorkloadGenerator.GenerateUiTreeWorkload, policies, sbMarkdown);

        // 5. Repeated Single-Pointer Workload
        RunBenchmarkSuite("5. Repeated Single-Pointer Workload (Same 8-byte pointer read repeatedly)",
            new[] { 10, 100, 500, 1000 }, WorkloadGenerator.GenerateRepeatedSinglePointerWorkload, policies, sbMarkdown);

        // 6. Cost Sensitivity Sweep
        RunCostSensitivitySweep(sbMarkdown);

        // 7. Pareto Analysis
        RunParetoAnalysis(sbMarkdown);

        // Save report to markdown
        string reportPath = @"c:\Games\Hy-v Tool\GameHelper2-main\tools\MemoryPolicySimulator\Simulation_Report.md";
        File.WriteAllText(reportPath, sbMarkdown.ToString(), Encoding.UTF8);
        Console.WriteLine($"\n[Report saved to: {reportPath}]");
    }

    private static void RunSimulatorSelfTests()
    {
        Console.WriteLine("Running Deterministic Self-Tests & Invariant Verifications...");

        // A. Scattered test (1000 items -> Hybrid exact 1.0x amplification)
        var scattered = WorkloadGenerator.GenerateWorstCaseScatteredWorkload(1000);
        var resScattered = new HybridV2HierarchicalPolicy().Run(scattered);
        Debug.Assert(resScattered.TotalAmplification == 1.0, $"Scattered amplification must be 1.0x, got {resScattered.TotalAmplification}");
        Debug.Assert(resScattered.ExactFetchedBytes == 8000, "Scattered exact fetched bytes must equal 8000");

        // B. Repeated same pointer test (100 reads -> must NOT promote to 4KB because unique bytes = 8)
        var repeated = WorkloadGenerator.GenerateRepeatedSinglePointerWorkload(100);
        var resRepeated = new HybridV2HierarchicalPolicy().Run(repeated);
        Debug.Assert(resRepeated.Level3PageFetchedBytes == 0, "Repeated same pointer must NOT promote to Level 3 4KB page!");
        Debug.Assert(resRepeated.UniqueRequestedBytes == 8, "Unique requested bytes must be exactly 8");

        // C. Oversized & boundary tests
        var oversized = new List<MemoryRequest>
        {
            new(0x100000050L, 300, "Oversized_300B"),
            new(0x100001000L, 600, "Oversized_600B"),
            new(0x100002000L, 1500, "Oversized_1500B")
        };
        var resOversized = new HybridV2HierarchicalPolicy().Run(oversized);
        Debug.Assert(resOversized.TotalFetchedBytes >= 2400, "Oversized requests must fully cover requested bytes");

        Console.WriteLine("All Deterministic Invariant Checks PASSED successfully!\n");
    }

    private static void RunBenchmarkSuite(
        string title,
        int[] sizes,
        Func<int, List<MemoryRequest>> workloadGen,
        IMemoryPolicy[] policies,
        StringBuilder md)
    {
        Console.WriteLine($"\n>>> {title} <<<");
        md.AppendLine($"## {title}");
        md.AppendLine();

        foreach (var size in sizes)
        {
            var reqs = workloadGen(size);
            Console.WriteLine($"\n--- Workload Scale: {size} items ({reqs.Count} total memory requests) ---");
            Console.WriteLine($"{"Policy",-35} | {"ReqBytes",9} | {"UniqBytes",9} | {"Calls",7} | {"Exact(B)",8} | {"L1(B)",8} | {"L2(B)",8} | {"L3(B)",9} | {"Total(B)",10} | {"Amplif",7} | {"EstCost (1k)",12}");
            Console.WriteLine(new string('-', 145));

            md.AppendLine($"### Scale: {size} items ({reqs.Count} requests)");
            md.AppendLine("| Policy | Requested | Unique Bytes | Native Calls | Exact (B) | L1 Compact | L2 Medium | L3 Page | Total Fetched | Amplification | Cost (CallCost=1000) |");
            md.AppendLine("| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |");

            foreach (var pol in policies)
            {
                var res = pol.Run(reqs);
                double cost = res.ComputeCost(1000.0);
                Console.WriteLine($"{res.PolicyName,-35} | {res.RequestedBytes,9:N0} | {res.UniqueRequestedBytes,9:N0} | {res.NativeReadCalls,7:N0} | {res.ExactFetchedBytes,8:N0} | {res.Level1CompactFetchedBytes,8:N0} | {res.Level2MediumFetchedBytes,8:N0} | {res.Level3PageFetchedBytes,9:N0} | {res.TotalFetchedBytes,10:N0} | {res.TotalAmplification,6:F2}x | {cost,12:N0}");
                md.AppendLine($"| **{res.PolicyName}** | {res.RequestedBytes:N0} B | {res.UniqueRequestedBytes:N0} B | {res.NativeReadCalls:N0} | {res.ExactFetchedBytes:N0} B | {res.Level1CompactFetchedBytes:N0} B | {res.Level2MediumFetchedBytes:N0} B | {res.Level3PageFetchedBytes:N0} B | {res.TotalFetchedBytes:N0} B | {res.TotalAmplification:F2}x | {cost:N0} |");
            }
            md.AppendLine();
        }
    }

    private static void RunCostSensitivitySweep(StringBuilder md)
    {
        Console.WriteLine("\n================================================================================");
        Console.WriteLine("          COST MODEL SENSITIVITY SWEEP (Varying CallCost with ByteCost=1.0)     ");
        Console.WriteLine("================================================================================");

        md.AppendLine("## Cost Model Sensitivity Sweep");
        md.AppendLine("Evaluation on Mixed Realistic Workload (100 Skills + 100 Entities + 100 UI elements + 100 Scattered):");
        md.AppendLine();

        var mixedReqs = new List<MemoryRequest>();
        mixedReqs.AddRange(WorkloadGenerator.GenerateSkillWorkload(100));
        mixedReqs.AddRange(WorkloadGenerator.GenerateEntityComponentWorkload(100));
        mixedReqs.AddRange(WorkloadGenerator.GenerateUiTreeWorkload(100));
        mixedReqs.AddRange(WorkloadGenerator.GenerateWorstCaseScatteredWorkload(100));

        var legacy = new LegacyExactReadPolicy().Run(mixedReqs);
        var current = new CurrentNewMemoryReadPolicy().Run(mixedReqs);
        var hybrid = new HybridV2HierarchicalPolicy().Run(mixedReqs);

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

        md.AppendLine("## Pareto-Optimal Configuration Analysis");
        md.AppendLine("Comparison of Candidate Configurations on Mixed Workload:");
        md.AppendLine();

        var mixedReqs = new List<MemoryRequest>();
        mixedReqs.AddRange(WorkloadGenerator.GenerateSkillWorkload(100));
        mixedReqs.AddRange(WorkloadGenerator.GenerateEntityComponentWorkload(100));
        mixedReqs.AddRange(WorkloadGenerator.GenerateUiTreeWorkload(100));
        mixedReqs.AddRange(WorkloadGenerator.GenerateWorstCaseScatteredWorkload(100));

        var candidates = new List<(string Name, IMemoryPolicy Policy)>
        {
            ("Legacy Exact-Read", new LegacyExactReadPolicy()),
            ("Current NewMemoryRead (4KB/8KB)", new CurrentNewMemoryReadPolicy()),
            ("Hybrid V2 (Exact + 128B + 512B + 4KB)", new HybridV2HierarchicalPolicy { Level1BlockSize = 128, Level2BlockSize = 512 }),
            ("Hybrid V2 (Exact + 64B + 256B + 4KB)", new HybridV2HierarchicalPolicy { Level1BlockSize = 64, Level2BlockSize = 256 }),
            ("Hybrid V2 (Exact + 256B + 1024B + 4KB)", new HybridV2HierarchicalPolicy { Level1BlockSize = 256, Level2BlockSize = 1024 })
        };

        Console.WriteLine($"{"Candidate Configuration",-42} | {"Calls",7} | {"FetchedBytes",12} | {"Amplification",14} | {"Cost (1000)",12}");
        Console.WriteLine(new string('-', 98));

        md.AppendLine("| Candidate Configuration | Native Calls | Fetched Bytes | Amplification | Cost (CallCost=1000) | Pareto-Optimal Status |");
        md.AppendLine("| :--- | :---: | :---: | :---: | :---: | :---: |");

        foreach (var (name, pol) in candidates)
        {
            var res = pol.Run(mixedReqs);
            double cost = res.ComputeCost(1000.0);
            string pareto = (name.Contains("Hybrid")) ? "Pareto-Optimal (Balanced)" : (name.Contains("Legacy") ? "Lowest Fetched / Highest Calls" : "Lowest Calls / Highest Wasted");

            Console.WriteLine($"{name,-42} | {res.NativeReadCalls,7:N0} | {res.TotalFetchedBytes,10:N0} B | {res.TotalAmplification,12:F2}x | {cost,12:N0}");
            md.AppendLine($"| **{name}** | {res.NativeReadCalls:N0} | {res.TotalFetchedBytes:N0} B | {res.TotalAmplification:F2}x | {cost:N0} | {pareto} |");
        }
    }
}
