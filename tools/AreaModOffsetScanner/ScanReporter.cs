namespace AreaModOffsetScanner
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    /// <summary>
    ///     Formats multi-hypothesis scan results for console output and exports JSON/CSV artifacts.
    ///     Guarantees that all serialized data models contain only JSON-safe primitive types.
    /// </summary>
    public static class ScanReporter
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        public static void PrintConsoleReport(ScanSession.ScanResult result)
        {
            var m = result.Metadata;

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("==========================================================================================================");
            Console.WriteLine("                PoE2 AreaMods Multi-Hypothesis Memory Scanner (Read-Only)                                 ");
            Console.WriteLine("==========================================================================================================");
            Console.ResetColor();

            Console.WriteLine($" Target Process:               {m.ProcessName} (PID: {m.ProcessId})");
            Console.WriteLine($" Module Path:                  {m.MainModuleFileName}");
            Console.WriteLine($" ServerDataObject Address:     {m.ServerDataObjectAddressHex}");
            Console.WriteLine($" PlayerServerData Address:     {m.PlayerServerDataAddressHex}");
            Console.WriteLine($" Scanned Base Target:          {m.ScanTargetBaseName}");
            Console.WriteLine($" Scan Range:                   0x{m.StartOffset:X4} .. 0x{m.EndOffset:X4} (Step: {m.Step} bytes)");
            if (m.ExpectedUiModCount.HasValue)
            {
                Console.WriteLine($" Expected UI AreaMods (Ref):   ~{m.ExpectedUiModCount.Value} lines (Diagnostic reference only)");
            }
            Console.WriteLine($" Total Offsets Inspected:      {m.TotalOffsetsInspected}");
            Console.WriteLine($" Basic Valid Vectors:          {m.BasicStructurallyValidCount} (First <= Last <= End, user-mode ptrs)");
            Console.WriteLine($" Hyp A (Stride 0x40) Hits:     {m.HypothesisAPlausibleCount} candidates with ASCII mod names");
            Console.WriteLine($" Hyp B (Stride 0x08 Row) Hits: {m.HypothesisBPlausibleCount} candidates with ASCII mod names");
            Console.WriteLine($" Hyp C (Stride 0x08 Ptr) Hits: {m.HypothesisCPlausibleCount} candidates with ASCII mod names");
            Console.WriteLine($" Elapsed Time:                 {m.ElapsedMilliseconds} ms ({DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC)");
            Console.WriteLine("==========================================================================================================");
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine("[DIAGNOSTIC CRITERIA]");
            Console.WriteLine(" - Hyp A: vector<ModArrayStruct> (stride 0x40, ModsPtr at +0x28 -> StringPtr -> UTF-16)");
            Console.WriteLine(" - Hyp B: vector<IntPtr> direct Mods.dat row ptrs (stride 0x08, rowPtr -> StringPtr -> UTF-16)");
            Console.WriteLine(" - Hyp C: vector<IntPtr> ptrs to struct (stride 0x08, structPtr -> ModsPtr at +0x28 -> StringPtr -> UTF-16)");
            Console.WriteLine(" - ASCII Name: Strict printable ASCII identifier (letters/digits/_/-/./+), 3..100 chars");
            Console.ResetColor();
            Console.WriteLine("==========================================================================================================");

            if (result.RankedCandidates.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[INFO] No candidate vectors found in the scanned range.");
                Console.ResetColor();
                return;
            }

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"\nTop Candidate Memory Vectors (Showing up to 25 of {result.RankedCandidates.Count} evaluated):");
            Console.ResetColor();

            // Table Header
            Console.WriteLine("-------------------------------------------------------------------------------------------------------------------------");
            Console.WriteLine(string.Format(
                "{0,-4} | {1,-8} | {2,-7} | {3,-10} | {4,-14} | {5,-14} | {6,-14} | {7,-5} | {8}",
                "Rank", "Offset", "Struct", "Used(B)", "Hyp A (0x40)", "Hyp B (0x08-R)", "Hyp C (0x08-P)", "Best", "Special Tag / Failure Reason"));
            Console.WriteLine("-------------------------------------------------------------------------------------------------------------------------");

            var topCount = Math.Min(25, result.RankedCandidates.Count);
            for (var i = 0; i < topCount; i++)
            {
                var c = result.RankedCandidates[i];
                var validStr = c.IsBasicValid ? "VALID" : "INVALID";

                var aStr = c.HypothesisA.StrideDivisible
                    ? $"{c.HypothesisA.TotalElements,3}e|{c.HypothesisA.ReadableNameChainCount,2}r|{c.HypothesisA.AsciiIdentifierCount,2}a"
                    : "n/a (not % 0x40)";

                var bStr = c.HypothesisB.StrideDivisible
                    ? $"{c.HypothesisB.TotalElements,3}e|{c.HypothesisB.ReadableNameChainCount,2}r|{c.HypothesisB.AsciiIdentifierCount,2}a"
                    : "n/a (not % 0x08)";

                var cStr = c.HypothesisC.StrideDivisible
                    ? $"{c.HypothesisC.TotalElements,3}e|{c.HypothesisC.ReadableNameChainCount,2}r|{c.HypothesisC.AsciiIdentifierCount,2}a"
                    : "n/a (not % 0x08)";

                var notes = !string.IsNullOrEmpty(c.SpecialTag)
                    ? c.SpecialTag
                    : (!c.IsBasicValid ? c.StructuralFailureReason : (c.BestHypothesisAsciiCount > 0 ? $"Matched {c.BestHypothesisAsciiCount} ASCII names" : "no ASCII names"));

                if (c.IsBasicValid && c.BestHypothesisAsciiCount > 0)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                }
                else if (!c.IsBasicValid)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Gray;
                }

                Console.WriteLine(string.Format(
                    "{0,-4} | {1,-8} | {2,-7} | {3,10} | {4,-14} | {5,-14} | {6,-14} | {7,-5} | {8}",
                    $"#{i + 1}",
                    c.OffsetHex,
                    validStr,
                    $"{c.UsedBytes}B",
                    aStr,
                    bStr,
                    cStr,
                    c.BestHypothesisCode,
                    notes));

                Console.ResetColor();
            }
            Console.WriteLine("-------------------------------------------------------------------------------------------------------------------------");

            // Detailed sample inspection for top candidates
            var interesting = result.RankedCandidates
                .Where(c => c.BestHypothesisAsciiCount > 0 ||
                            c.HypothesisA.ReadableNameChainCount > 0 ||
                            c.HypothesisB.ReadableNameChainCount > 0 ||
                            c.HypothesisC.ReadableNameChainCount > 0 ||
                            c.IsSpecialOffset)
                .Take(6)
                .ToList();

            if (interesting.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("\nDetailed Candidate Hypothesis Breakdown & Samples:");
                Console.ResetColor();

                foreach (var c in interesting)
                {
                    Console.WriteLine($"\n>>> Candidate Offset: {c.OffsetHex} ({c.Offset}) {c.SpecialTag}");
                    Console.WriteLine($"    First = {c.First}, Last = {c.Last}, End = {c.End}");
                    Console.WriteLine($"    Basic Validity = {(c.IsBasicValid ? "VALID" : $"INVALID ({c.StructuralFailureReason})")}, UsedBytes: {c.UsedBytes} bytes, CapacityBytes: {c.CapacityBytes} bytes");

                    if (c.HypothesisA.StrideDivisible)
                    {
                        Console.WriteLine($"    [Hypothesis A - 0x40 ModArrayStruct]: Elements={c.HypothesisA.TotalElements}, Readable={c.HypothesisA.ReadableEntries}, ReadableChains={c.HypothesisA.ReadableNameChainCount}, ASCII Names={c.HypothesisA.AsciiIdentifierCount}");
                        PrintSamples("      Sample A", c.HypothesisA.SampleNames);
                    }

                    if (c.HypothesisB.StrideDivisible)
                    {
                        Console.WriteLine($"    [Hypothesis B - 0x08 Direct Row Ptr]: Elements={c.HypothesisB.TotalElements}, Readable={c.HypothesisB.ReadableEntries}, ReadableChains={c.HypothesisB.ReadableNameChainCount}, ASCII Names={c.HypothesisB.AsciiIdentifierCount}");
                        PrintSamples("      Sample B", c.HypothesisB.SampleNames);
                    }

                    if (c.HypothesisC.StrideDivisible)
                    {
                        Console.WriteLine($"    [Hypothesis C - 0x08 Ptr to Struct]:  Elements={c.HypothesisC.TotalElements}, Readable={c.HypothesisC.ReadableEntries}, ReadableChains={c.HypothesisC.ReadableNameChainCount}, ASCII Names={c.HypothesisC.AsciiIdentifierCount}");
                        PrintSamples("      Sample C", c.HypothesisC.SampleNames);
                    }
                }
            }
        }

        private static void PrintSamples(string prefix, List<string> samples)
        {
            if (samples.Count == 0)
            {
                Console.WriteLine($"{prefix}: (none)");
                return;
            }

            for (var i = 0; i < samples.Count; i++)
            {
                Console.WriteLine($"{prefix} [{i + 1}]: {samples[i]}");
            }
        }

        public static (string jsonPath, string csvPath) ExportFiles(ScanSession.ScanResult result, string? customBasePath = null)
        {
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var basePath = customBasePath ?? $"AreaModScan-Phase2-{timestamp}";

            var jsonPath = basePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? basePath : $"{basePath}.json";
            var csvPath = basePath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? basePath : $"{Path.ChangeExtension(basePath, null)}.csv";

            var jsonContent = JsonSerializer.Serialize(result, JsonOptions);
            File.WriteAllText(jsonPath, jsonContent, Encoding.UTF8);

            var sbCsv = new StringBuilder();
            sbCsv.AppendLine("Rank,OffsetHex,OffsetDec,IsBasicValid,FailureReason,UsedBytes,CapacityBytes,HypA_Elements,HypA_Chains,HypA_Ascii,HypB_Elements,HypB_Chains,HypB_Ascii,HypC_Elements,HypC_Chains,HypC_Ascii,BestHyp,SpecialTag,First,Last,End,HypA_Samples,HypB_Samples,HypC_Samples");

            for (var i = 0; i < result.RankedCandidates.Count; i++)
            {
                var c = result.RankedCandidates[i];
                var aSamples = string.Join(" | ", c.HypothesisA.SampleNames).Replace("\"", "\"\"");
                var bSamples = string.Join(" | ", c.HypothesisB.SampleNames).Replace("\"", "\"\"");
                var cSamples = string.Join(" | ", c.HypothesisC.SampleNames).Replace("\"", "\"\"");
                var failureClean = c.StructuralFailureReason.Replace("\"", "\"\"");

                sbCsv.AppendLine(string.Join(",",
                    i + 1,
                    c.OffsetHex,
                    c.Offset,
                    c.IsBasicValid,
                    $"\"{failureClean}\"",
                    c.UsedBytes,
                    c.CapacityBytes,
                    c.HypothesisA.TotalElements,
                    c.HypothesisA.ReadableNameChainCount,
                    c.HypothesisA.AsciiIdentifierCount,
                    c.HypothesisB.TotalElements,
                    c.HypothesisB.ReadableNameChainCount,
                    c.HypothesisB.AsciiIdentifierCount,
                    c.HypothesisC.TotalElements,
                    c.HypothesisC.ReadableNameChainCount,
                    c.HypothesisC.AsciiIdentifierCount,
                    c.BestHypothesisCode,
                    $"\"{c.SpecialTag}\"",
                    c.First,
                    c.Last,
                    c.End,
                    $"\"{aSamples}\"",
                    $"\"{bSamples}\"",
                    $"\"{cSamples}\""));
            }

            File.WriteAllText(csvPath, sbCsv.ToString(), Encoding.UTF8);

            return (jsonPath, csvPath);
        }
    }
}
