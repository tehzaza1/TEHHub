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
    public double Amplification => RequestedBytes > 0 ? (double)FetchedBytes / RequestedBytes : 1.0;
    
    // Cost model: CallCost = 1000 (Syscall/NtReadVirtualMemory overhead), ByteCost = 1 (Bandwidth overhead)
    public const double CallCost = 1000.0;
    public const double ByteCost = 1.0;
    public double EstimatedCost => (NativeReadCalls * CallCost) + (FetchedBytes * ByteCost);
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
            
            // Legacy issues 1 exact native read per request
            metrics.NativeReadCalls++;
            metrics.FetchedBytes += req.Size;
            metrics.CacheMisses++;
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

        // 1. Process pre-planned component batches if any
        if (plannedBatches != null)
        {
            foreach (var batch in plannedBatches)
            {
                metrics.NativeReadCalls++;
                metrics.FetchedBytes += batch.Size;
                cache[batch.Address] = batch.Size;
            }
        }

        // 2. Process runtime requests
        foreach (var req in requests)
        {
            metrics.TotalRequests++;
            metrics.RequestedBytes += req.Size;

            // Check if covered by existing cache window
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

            // Cache miss: dynamically allocate 4KB page or 8KB cross-page
            metrics.CacheMisses++;
            long pageStart = req.Address & ~(DynamicPageBytes - 1L);
            long offsetInPage = req.Address - pageStart;
            int fetchSize = (offsetInPage + req.Size > DynamicPageBytes) ? DynamicCrossPageBytes : DynamicPageBytes;

            if (cache.Count < MaxDynamicWindows)
            {
                metrics.NativeReadCalls++;
                metrics.FetchedBytes += fetchSize;
                cache[pageStart] = fetchSize;
            }
            else
            {
                // Fallback to exact read if dynamic window table full
                metrics.NativeReadCalls++;
                metrics.FetchedBytes += req.Size;
            }
        }

        return metrics;
    }
}

// 3. Proposed Hybrid V2 Policy
public class HybridV2Policy : IMemoryPolicy
{
    public string Name => "Proposed Hybrid V2";

    public int CompactBlockSize { get; set; } = 256;         // Default 256B compact blocks
    public int LocalityPromotionThreshold { get; set; } = 2; // Accesses before promoting to 4KB
    public int MaxMergeGap { get; set; } = 64;               // Max gap to merge
    public double MaxAmplificationBudget { get; set; } = 4.0;// Amplification budget limit

    public SimulationMetrics Run(IEnumerable<MemoryRequest> requests, IEnumerable<MemoryRequest>? plannedBatches = null)
    {
        var metrics = new SimulationMetrics { PolicyName = Name };
        var cachedRanges = new List<(long Start, int Size)>();
        var pageAccessCounts = new Dictionary<long, int>(); // 4KB Page -> Access Count

        // Process requests
        foreach (var req in requests)
        {
            metrics.TotalRequests++;
            metrics.RequestedBytes += req.Size;

            // Check existing cached ranges
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
            pageAccessCounts.TryGetValue(pageId, out int accesses);
            pageAccessCounts[pageId] = accesses + 1;

            int fetchSize;
            long fetchStart;

            if (accesses + 1 >= LocalityPromotionThreshold)
            {
                // Proven locality: Promote to 4KB page
                fetchStart = pageId;
                fetchSize = 4096;
            }
            else
            {
                // Sparse / first access: Use compact power-of-two block (e.g. 128/256/512B)
                fetchSize = Math.Max(CompactBlockSize, (int)RoundUpPowerOfTwo(req.Size));
                fetchStart = req.Address & ~(fetchSize - 1L);
            }

            metrics.NativeReadCalls++;
            metrics.FetchedBytes += fetchSize;
            cachedRanges.Add((fetchStart, fetchSize));
        }

        return metrics;
    }

    private static long RoundUpPowerOfTwo(long v)
    {
        if (v <= 0) return 1;
        v--;
        v |= v >> 1;
        v |= v >> 2;
        v |= v >> 4;
        v |= v >> 8;
        v |= v >> 16;
        v |= v >> 32;
        return v + 1;
    }
}

public static class WorkloadGenerator
{
    private static readonly Random Rand = new(42); // Deterministic seed

    // 1. Skill Workload: Actor component with ActiveSkills vector and Cooldowns vector
    public static List<MemoryRequest> GenerateSkillWorkload(int skillCount)
    {
        var reqs = new List<MemoryRequest>();
        long actorBase = 0x200000000L;

        // Read Actor component fields
        reqs.Add(new MemoryRequest(actorBase + 0x000, 0x40, "ActorHeader"));
        reqs.Add(new MemoryRequest(actorBase + 0x8B0, 4, "AnimationId"));
        reqs.Add(new MemoryRequest(actorBase + 0xB08, 24, "ActiveSkillsPtrVector"));
        reqs.Add(new MemoryRequest(actorBase + 0xB20, 24, "CooldownsPtrVector"));

        long skillBase = 0x210000000L;
        long cdBase = 0x220000000L;

        for (int i = 0; i < skillCount; i++)
        {
            long skillAddr = skillBase + (i * 0x200); // 512 bytes spacing
            // ActiveSkillStructure -> ActiveSkillDetails
            reqs.Add(new MemoryRequest(skillAddr + 0x08, 4, "UseStage"));
            reqs.Add(new MemoryRequest(skillAddr + 0x0C, 4, "CastType"));
            reqs.Add(new MemoryRequest(skillAddr + 0x40, 4, "SkillIdInfo"));
            reqs.Add(new MemoryRequest(skillAddr + 0x48, 8, "GrantedEffectPerLevelPtr"));
            reqs.Add(new MemoryRequest(skillAddr + 0x50, 8, "GrantedEffectStatSetsPtr"));
            reqs.Add(new MemoryRequest(skillAddr + 0xE8, 4, "TotalCooldownTimeInMs"));

            // Read Cooldown struct
            long cdAddr = cdBase + (i * 0x80);
            reqs.Add(new MemoryRequest(cdAddr + 0x08, 4, "CdDatId"));
            reqs.Add(new MemoryRequest(cdAddr + 0x10, 24, "CooldownsListVector"));
            reqs.Add(new MemoryRequest(cdAddr + 0x30, 4, "MaxUses"));
            reqs.Add(new MemoryRequest(cdAddr + 0x34, 4, "CdTotalTime"));
            reqs.Add(new MemoryRequest(cdAddr + 0x3C, 4, "CdSkillId"));

            // Active Cooldown entries (2 entries per active skill)
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
            long entityAddr = entityBase + (e * 0x1000); // Entities on separate pages
            reqs.Add(new MemoryRequest(entityAddr + 0x00, 8, "EntityVTable"));
            reqs.Add(new MemoryRequest(entityAddr + 0x08, 8, "EntityComponentsMap"));
            reqs.Add(new MemoryRequest(entityAddr + 0x38, 4, "EntityId"));

            // Read 5 dense components per entity (Positioned, Render, Life, ObjectMagicProperties, Pathfinding)
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

        // UiRoot
        reqs.Add(new MemoryRequest(uiBase + 0x000, 8, "UiRootVTable"));
        reqs.Add(new MemoryRequest(uiBase + 0x010, 24, "UiRootChildren"));
        reqs.Add(new MemoryRequest(uiBase + 0x7C0, 8, "MapParentPtr"));

        // Traverse hierarchy [6, 2, 3, 0, 1] + sibling checks
        for (int i = 0; i < elementCount; i++)
        {
            long nodeAddr = uiBase + 0x1000 + (i * 0x400);
            reqs.Add(new MemoryRequest(nodeAddr + 0x000, 8, "NodeVTable"));
            reqs.Add(new MemoryRequest(nodeAddr + 0x010, 24, "NodeChildren"));
            reqs.Add(new MemoryRequest(nodeAddr + 0x0B8, 8, "NodeParent"));
            reqs.Add(new MemoryRequest(nodeAddr + 0x100, 8, "RelativePosition"));
            reqs.Add(new MemoryRequest(nodeAddr + 0x128, 32, "StringIdPtr"));
            reqs.Add(new MemoryRequest(nodeAddr + 0x360, 64, "TextWString"));

            // Repeat query on parent
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
            long pageAddr = baseAddr + (i * 0x1000); // Exactly 1 read per 4KB page!
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
            new HybridV2Policy()
        };

        var sbMarkdown = new StringBuilder();
        sbMarkdown.AppendLine("# Offline Memory Read Policy Simulation Report");
        sbMarkdown.AppendLine("Comparative Analysis: Legacy Exact-Read vs. Current NewMemoryRead vs. Proposed Hybrid V2");
        sbMarkdown.AppendLine();
        sbMarkdown.AppendLine("Cost Model: `EstimatedCost = (NativeCalls * 1000) + (FetchedBytes * 1)`");
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

        // 5. Parameter Sensitivity Analysis for Hybrid V2
        RunHybridSensitivityAnalysis(sbMarkdown);

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
            Console.WriteLine($"{"Policy",-35} | {"ReqBytes",9} | {"Calls",7} | {"FetchBytes",11} | {"Amplif",8} | {"WastedBytes",11} | {"EstCost",10}");
            Console.WriteLine(new string('-', 105));

            md.AppendLine($"### Scale: {size} items ({reqs.Count} requests)");
            md.AppendLine("| Policy | Requested | Native Calls | Fetched | Amplification | Wasted Bytes | Relative Cost |");
            md.AppendLine("| :--- | :---: | :---: | :---: | :---: | :---: | :---: |");

            foreach (var pol in policies)
            {
                var res = pol.Run(reqs);
                Console.WriteLine($"{res.PolicyName,-35} | {res.RequestedBytes,9:N0} | {res.NativeReadCalls,7:N0} | {res.FetchedBytes,11:N0} | {res.Amplification,7:F2}x | {res.WastedBytes,11:N0} | {res.EstimatedCost,10:N0}");
                md.AppendLine($"| **{res.PolicyName}** | {res.RequestedBytes:N0} B | {res.NativeReadCalls:N0} | {res.FetchedBytes:N0} B | {res.Amplification:F2}x | {res.WastedBytes:N0} B | {res.EstimatedCost:N0} |");
            }
            md.AppendLine();
        }
    }

    private static void RunHybridSensitivityAnalysis(StringBuilder md)
    {
        Console.WriteLine("\n================================================================================");
        Console.WriteLine("          HYBRID V2 PARAMETER SENSITIVITY & TUNING ANALYSIS                     ");
        Console.WriteLine("================================================================================");

        md.AppendLine("## Hybrid V2 Parameter Sensitivity & Tuning Analysis");
        md.AppendLine("Evaluation on Mixed Realistic Workload (100 Skills + 100 Entities + 100 UI elements):");
        md.AppendLine();

        var mixedReqs = new List<MemoryRequest>();
        mixedReqs.AddRange(WorkloadGenerator.GenerateSkillWorkload(100));
        mixedReqs.AddRange(WorkloadGenerator.GenerateEntityComponentWorkload(100));
        mixedReqs.AddRange(WorkloadGenerator.GenerateUiTreeWorkload(100));
        mixedReqs.AddRange(WorkloadGenerator.GenerateWorstCaseScatteredWorkload(100));

        int[] compactSizes = { 64, 128, 256, 512, 1024, 4096 };
        int[] promotionThresholds = { 1, 2, 3, 4 };

        Console.WriteLine($"{"CompactSize",12} | {"PromotionThresh",16} | {"Calls",7} | {"FetchedBytes",12} | {"Amplification",14} | {"EstCost",12}");
        Console.WriteLine(new string('-', 85));

        md.AppendLine("| Compact Block Size | Promotion Threshold | Native Calls | Fetched Bytes | Amplification | Estimated Cost |");
        md.AppendLine("| :---: | :---: | :---: | :---: | :---: | :---: |");

        foreach (var cs in compactSizes)
        {
            foreach (var pt in promotionThresholds)
            {
                var pol = new HybridV2Policy
                {
                    CompactBlockSize = cs,
                    LocalityPromotionThreshold = pt
                };
                var res = pol.Run(mixedReqs);
                Console.WriteLine($"{cs,10} B | {pt,16} | {res.NativeReadCalls,7:N0} | {res.FetchedBytes,10:N0} B | {res.Amplification,12:F2}x | {res.EstimatedCost,12:N0}");
                md.AppendLine($"| {cs} B | {pt} | {res.NativeReadCalls:N0} | {res.FetchedBytes:N0} B | {res.Amplification:F2}x | {res.EstimatedCost:N0} |");
            }
        }
    }
}
