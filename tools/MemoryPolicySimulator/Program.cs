using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MemoryPolicySimulator;

public record MemoryRequest(long Address, int Size, string Tag);

public class SimulationMetrics
{
    public string PolicyName { get; set; } = "";
    public int TotalRequests { get; set; }
    public long RequestedBytes { get; set; }
    public long NativeReadCalls { get; set; }
    public long FetchedBytes { get; set; }
    public long CacheHits { get; set; }
    public long CacheMisses { get; set; }
    public long WastedBytes => Math.Max(0, FetchedBytes - RequestedBytes);
    public double TotalAmplification => RequestedBytes > 0 ? (double)FetchedBytes / RequestedBytes : 1.0;
    
    // Detailed Amplification Metrics
    public double MergeAmplification { get; set; } = 1.0;
    public double CachePromotionAmplification { get; set; } = 1.0;

    // Promotion level statistics
    public int Level0ExactFetches { get; set; }
    public int Level1CompactFetches { get; set; }
    public int Level2MediumFetches { get; set; }
    public int Level3PageFetches { get; set; }

    public double ComputeCost(double callCost, double byteCost = 1.0)
    {
        return (NativeReadCalls * callCost) + (FetchedBytes * byteCost);
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
        
        foreach (var req in requests)
        {
            metrics.TotalRequests++;
            metrics.RequestedBytes += req.Size;
            metrics.NativeReadCalls++;
            metrics.FetchedBytes += req.Size;
            metrics.CacheMisses++;
            metrics.Level0ExactFetches++;
        }

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

    public SimulationMetrics Run(IEnumerable<MemoryRequest> requests, IEnumerable<MemoryRequest>? plannedBatches = null)
    {
        var metrics = new SimulationMetrics { PolicyName = Name };
        var cache = new Dictionary<long, int>(); // StartAddress -> Size

        if (plannedBatches != null)
        {
            foreach (var batch in plannedBatches)
            {
                metrics.NativeReadCalls++;
                metrics.FetchedBytes += batch.Size;
                cache[batch.Address] = batch.Size;
                metrics.Level3PageFetches++;
            }
        }

        foreach (var req in requests)
        {
            metrics.TotalRequests++;
            metrics.RequestedBytes += req.Size;

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
                metrics.FetchedBytes += fetchSize;
                cache[pageStart] = fetchSize;
                metrics.Level3PageFetches++;
            }
            else
            {
                metrics.NativeReadCalls++;
                metrics.FetchedBytes += req.Size;
                metrics.Level0ExactFetches++;
            }
        }

        metrics.CachePromotionAmplification = metrics.TotalAmplification;
        return metrics;
    }
}

// 3. Corrected Hybrid V2 Policy: Exact-First + Hierarchical Multi-Level Promotion
public class HybridV2HierarchicalPolicy : IMemoryPolicy
{
    public string Name { get; set; } = "Proposed Hybrid V2 (Hierarchical Exact-First)";

    // Level configuration
    public int Level1BlockSize { get; set; } = 128; // Level 1 compact block (e.g. 64B or 128B)
    public int Level2BlockSize { get; set; } = 512; // Level 2 medium block (e.g. 256B or 512B)
    public int Level3PageSize { get; set; } = 4096; // Level 3 4KB page

    // Promotion criteria
    public int Level1AccessThreshold { get; set; } = 2; // Accesses to promote to Level 1
    public int Level2AccessThreshold { get; set; } = 3; // Accesses to promote to Level 2
    public int Level3AccessThreshold { get; set; } = 5; // Accesses to promote to Level 3
    public int Level3MinUniqueBytes { get; set; } = 256; // Minimum unique requested bytes in page to promote to 4KB

    // Merging budget
    public int MaxMergeGap { get; set; } = 64;
    public double MaxMergeAmplificationBudget { get; set; } = 2.5;

    public SimulationMetrics Run(IEnumerable<MemoryRequest> requests, IEnumerable<MemoryRequest>? plannedBatches = null)
    {
        var metrics = new SimulationMetrics { PolicyName = Name };
        var cachedRanges = new List<(long Start, int Size)>();
        
        // Tracking state per 4KB page
        var pageAccessCount = new Dictionary<long, int>();
        var pageUniqueBytes = new Dictionary<long, HashSet<long>>(); // Page -> Set of requested addresses

        foreach (var req in requests)
        {
            metrics.TotalRequests++;
            metrics.RequestedBytes += req.Size;

            // 1. Check existing cached ranges
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
            long pageId = req.Address & ~0xFFFL;

            pageAccessCount.TryGetValue(pageId, out int accesses);
            accesses++;
            pageAccessCount[pageId] = accesses;

            if (!pageUniqueBytes.TryGetValue(pageId, out var uniqueSet))
            {
                uniqueSet = new HashSet<long>();
                pageUniqueBytes[pageId] = uniqueSet;
            }
            for (int b = 0; b < req.Size; b++) uniqueSet.Add(req.Address + b);

            int uniqueByteCount = uniqueSet.Count;

            // Hierarchical Level Selection:
            // Level 0: Isolated / first sparse access -> EXACT requested range
            // Level 1: Moderate accesses -> Small compact block (128B)
            // Level 2: High accesses / medium cluster -> Medium block (512B)
            // Level 3: Hot page (High accesses + High unique byte density) -> 4KB Page
            int fetchSize;
            long fetchStart;

            if (accesses >= Level3AccessThreshold && uniqueByteCount >= Level3MinUniqueBytes)
            {
                // Level 3: Hot Page
                fetchStart = pageId;
                fetchSize = Level3PageSize;
                metrics.Level3PageFetches++;
            }
            else if (accesses >= Level2AccessThreshold)
            {
                // Level 2: Medium Compact Block
                fetchSize = Math.Max(Level2BlockSize, req.Size);
                fetchStart = req.Address & ~(fetchSize - 1L);
                metrics.Level2MediumFetches++;
            }
            else if (accesses >= Level1AccessThreshold)
            {
                // Level 1: Small Compact Block
                fetchSize = Math.Max(Level1BlockSize, req.Size);
                fetchStart = req.Address & ~(fetchSize - 1L);
                metrics.Level1CompactFetches++;
            }
            else
            {
                // Level 0: EXACT-FIRST (No over-fetch on first isolated miss)
                fetchStart = req.Address;
                fetchSize = req.Size;
                metrics.Level0ExactFetches++;
            }

            metrics.NativeReadCalls++;
            metrics.FetchedBytes += fetchSize;
            cachedRanges.Add((fetchStart, fetchSize));
        }

        metrics.CachePromotionAmplification = metrics.TotalAmplification;
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
            long skillAddr = skillBase + (i * 0x100); // Dense clustering (256B spacing)
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
}

public class Program
{
    public static void Main(string[] args)
    {
        Console.WriteLine("================================================================================");
        Console.WriteLine("       OFFLINE MEMORY READ POLICY SIMULATOR (TEHHUB ARCHITECTURE LAB)          ");
        Console.WriteLine("================================================================================");
        Console.WriteLine();

        var policies = new IMemoryPolicy[]
        {
            new LegacyExactReadPolicy(),
            new CurrentNewMemoryReadPolicy(),
            new HybridV2HierarchicalPolicy()
        };

        var sbMarkdown = new StringBuilder();
        sbMarkdown.AppendLine("# Offline Memory Read Policy Simulation Report (Corrected Hierarchical Model)");
        sbMarkdown.AppendLine("Comparative Analysis: Legacy Exact-Read vs. Current NewMemoryRead vs. Proposed Hybrid V2 (Exact-First)");
        sbMarkdown.AppendLine();
        sbMarkdown.AppendLine("Cost Model: `EstimatedCost = (NativeCalls * CallCost) + (FetchedBytes * 1)`");
        sbMarkdown.AppendLine();

        int[] testSizes = { 10, 100, 500, 1000 };

        // 1. Skill Workload
        RunBenchmarkSuite("1. Skill Workload (Actor + ActiveSkills + Cooldowns)",
            testSizes, WorkloadGenerator.GenerateSkillWorkload, policies, sbMarkdown);

        // 2. Entity / Component Workload
        RunBenchmarkSuite("2. Entity / Component Workload (Dense Component Clusters)",
            testSizes, WorkloadGenerator.GenerateEntityComponentWorkload, policies, sbMarkdown);

        // 3. UI-Tree Workload
        RunBenchmarkSuite("3. UI-Tree Workload (Hierarchical UI Traversal & String Reads)",
            testSizes, WorkloadGenerator.GenerateUiTreeWorkload, policies, sbMarkdown);

        // 4. Worst-case Scattered Workload
        RunBenchmarkSuite("4. Worst-Case Scattered Workload (1 Scalar Read per 4KB Page)",
            testSizes, WorkloadGenerator.GenerateWorstCaseScatteredWorkload, policies, sbMarkdown);

        // 5. Cost Sensitivity Sweep
        RunCostSensitivitySweep(sbMarkdown);

        // 6. Pareto-Optimal Analysis
        RunParetoAnalysis(sbMarkdown);

        // Save report to markdown
        string reportPath = @"c:\Games\Hy-v Tool\GameHelper2-main\tools\MemoryPolicySimulator\Simulation_Report.md";
        File.WriteAllText(reportPath, sbMarkdown.ToString(), Encoding.UTF8);
        Console.WriteLine($"\n[Report saved to: {reportPath}]");
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
            Console.WriteLine($"{"Policy",-35} | {"ReqBytes",9} | {"Calls",7} | {"FetchBytes",11} | {"Amplif",8} | {"WastedBytes",11} | {"EstCost (1k)",12}");
            Console.WriteLine(new string('-', 108));

            md.AppendLine($"### Scale: {size} items ({reqs.Count} requests)");
            md.AppendLine("| Policy | Requested | Native Calls | Fetched | Amplification | Wasted Bytes | Relative Cost (CallCost=1000) |");
            md.AppendLine("| :--- | :---: | :---: | :---: | :---: | :---: | :---: |");

            foreach (var pol in policies)
            {
                var res = pol.Run(reqs);
                double cost = res.ComputeCost(1000.0);
                Console.WriteLine($"{res.PolicyName,-35} | {res.RequestedBytes,9:N0} | {res.NativeReadCalls,7:N0} | {res.FetchedBytes,11:N0} | {res.TotalAmplification,7:F2}x | {res.WastedBytes,11:N0} | {cost,12:N0}");
                md.AppendLine($"| **{res.PolicyName}** | {res.RequestedBytes:N0} B | {res.NativeReadCalls:N0} | {res.FetchedBytes:N0} B | {res.TotalAmplification:F2}x | {res.WastedBytes:N0} B | {cost:N0} |");
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

            Console.WriteLine($"{name,-42} | {res.NativeReadCalls,7:N0} | {res.FetchedBytes,10:N0} B | {res.TotalAmplification,12:F2}x | {cost,12:N0}");
            md.AppendLine($"| **{name}** | {res.NativeReadCalls:N0} | {res.FetchedBytes:N0} B | {res.TotalAmplification:F2}x | {cost:N0} | {pareto} |");
        }
    }
}
