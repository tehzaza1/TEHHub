namespace AreaModOffsetScanner
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    /// <summary>
    ///     Formats scan results for console output and exports JSON/CSV artifacts.
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
            Console.WriteLine("                            PoE2 AreaMods Memory Candidate Scanner (Read-Only)                            ");
            Console.WriteLine("==========================================================================================================");
            Console.ResetColor();

            Console.WriteLine($" Target Process:               {m.ProcessName} (PID: {m.ProcessId})");
            Console.WriteLine($" Module Path:                  {m.MainModuleFileName}");
            Console.WriteLine($" ServerDataObject Address:     {m.ServerDataObjectAddressHex}");
            Console.WriteLine($" PlayerServerData Address:     {m.PlayerServerDataAddressHex}");
            Console.WriteLine($" Scan Range:                   0x{m.StartOffset:X4} .. 0x{m.EndOffset:X4} (Step: {m.Step} bytes, 0x40 stride)");
            if (m.ExpectedUiModCount.HasValue)
            {
                Console.WriteLine($" Expected UI AreaMods (Ref):   ~{m.ExpectedUiModCount.Value} lines (Diagnostic reference only)");
            }
            Console.WriteLine($" Total Offsets Inspected:      {m.TotalOffsetsInspected}");
            Console.WriteLine($" Structurally Valid Vectors:   {m.StructurallyValidCount} (count > 0)");
            Console.WriteLine($" Structurally Invalid (Plaus): {m.StructurallyInvalidPlausibleCount}");
            Console.WriteLine($" Deep Inspected Vectors:       {m.DeepInspectedCandidatesCount}");
            Console.WriteLine($" Candidates w/ Readable Chain: {m.CandidatesWithReadableNameChainsCount}");
            Console.WriteLine($" Candidates w/ Plausible Name: {m.CandidatesWithPlausibleNamesCount}");
            Console.WriteLine($" Elapsed Time:                 {m.ElapsedMilliseconds} ms ({DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC)");
            Console.WriteLine("==========================================================================================================");
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine("[DIAGNOSTIC NOTE] Readable Name Chains indicate that ModArrayStruct.ModsPtr was dereferenced to a readable");
            Console.WriteLine("                  string. This is a heuristic readability diagnostic, NOT proof of a Mods.dat record.");
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
            Console.WriteLine("----------------------------------------------------------------------------------------------------------");
            Console.WriteLine(string.Format(
                "{0,-4} | {1,-8} | {2,-11} | {3,-6} | {4,-7} | {5,-8} | {6,-9} | {7,-8} | {8}",
                "Rank", "Offset", "Structure", "Elems", "ModsPtr", "Readable", "Plausible", "UI Diff", "Special Tag / Notes"));
            Console.WriteLine("----------------------------------------------------------------------------------------------------------");

            var topCount = Math.Min(25, result.RankedCandidates.Count);
            for (var i = 0; i < topCount; i++)
            {
                var c = result.RankedCandidates[i];
                var validStr = c.IsStructurallyValid ? "VALID" : "INVALID";
                var uiDiffStr = m.ExpectedUiModCount.HasValue
                    ? $"{c.PlausibleRawNameCount - m.ExpectedUiModCount.Value:+0;-0;0}"
                    : "N/A";

                var notes = !string.IsNullOrEmpty(c.SpecialTag)
                    ? c.SpecialTag
                    : (!c.IsStructurallyValid ? c.StructuralFailureReason : (c.PlausibleRawNameCount > 0 ? $"{c.SampleRawNames.Count} samples" : "empty"));

                if (c.IsStructurallyValid && c.PlausibleRawNameCount > 0)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                }
                else if (!c.IsStructurallyValid)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Gray;
                }

                Console.WriteLine(string.Format(
                    "{0,-4} | {1,-8} | {2,-11} | {3,6} | {4,7} | {5,8} | {6,9} | {7,8} | {8}",
                    $"#{i + 1}",
                    c.OffsetHex,
                    validStr,
                    c.ElementCount,
                    c.PlausibleModsPtrCount,
                    c.ReadableNameChainCount,
                    c.PlausibleRawNameCount,
                    uiDiffStr,
                    notes));

                Console.ResetColor();
            }
            Console.WriteLine("----------------------------------------------------------------------------------------------------------");

            // Detailed sample inspection for top candidates
            var interesting = result.RankedCandidates
                .Where(c => c.ReadableNameChainCount > 0 || c.IsSpecialOffset)
                .Take(5)
                .ToList();

            if (interesting.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("\nDetailed Candidate Mod Samples:");
                Console.ResetColor();

                foreach (var c in interesting)
                {
                    Console.WriteLine($"\n>>> Candidate Offset: {c.OffsetHex} ({c.Offset}) {c.SpecialTag}");
                    Console.WriteLine($"    First = {c.First}, Last = {c.Last}, End = {c.End}");
                    Console.WriteLine($"    Structure = {(c.IsStructurallyValid ? "VALID" : $"INVALID ({c.StructuralFailureReason})")}");
                    Console.WriteLine($"    Elements = {c.ElementCount} (Capacity: {c.CapacityCount}), Readable: {c.ReadableEntries}, Readable Chains: {c.ReadableNameChainCount}, Plausible Names: {c.PlausibleRawNameCount}");

                    if (c.SampleRawNames.Count > 0)
                    {
                        Console.WriteLine("    Sample RawNames:");
                        for (var sIdx = 0; sIdx < c.SampleRawNames.Count; sIdx++)
                        {
                            Console.WriteLine($"      [{sIdx + 1}] {c.SampleRawNames[sIdx]}");
                        }
                    }
                    else
                    {
                        Console.WriteLine("    Sample RawNames: (none)");
                    }
                }
            }
        }

        public static (string jsonPath, string csvPath) ExportFiles(ScanSession.ScanResult result, string? customBasePath = null)
        {
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var basePath = customBasePath ?? $"AreaModScan-{timestamp}";

            var jsonPath = basePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? basePath : $"{basePath}.json";
            var csvPath = basePath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? basePath : $"{Path.ChangeExtension(basePath, null)}.csv";

            var jsonContent = JsonSerializer.Serialize(result, JsonOptions);
            File.WriteAllText(jsonPath, jsonContent, Encoding.UTF8);

            var sbCsv = new StringBuilder();
            sbCsv.AppendLine("Rank,OffsetHex,OffsetDec,IsStructurallyValid,StructuralFailureReason,ElementCount,CapacityCount,AttemptedEntries,ReadableEntries,PlausibleModsPtrCount,ReadableNameChainCount,PlausibleRawNameCount,SpecialTag,First,Last,End,SampleNames");

            for (var i = 0; i < result.RankedCandidates.Count; i++)
            {
                var c = result.RankedCandidates[i];
                var samplesJoined = string.Join(" | ", c.SampleRawNames).Replace("\"", "\"\"");
                var failureClean = c.StructuralFailureReason.Replace("\"", "\"\"");

                sbCsv.AppendLine(string.Join(",",
                    i + 1,
                    c.OffsetHex,
                    c.Offset,
                    c.IsStructurallyValid,
                    $"\"{failureClean}\"",
                    c.ElementCount,
                    c.CapacityCount,
                    c.AttemptedEntries,
                    c.ReadableEntries,
                    c.PlausibleModsPtrCount,
                    c.ReadableNameChainCount,
                    c.PlausibleRawNameCount,
                    $"\"{c.SpecialTag}\"",
                    c.First,
                    c.Last,
                    c.End,
                    $"\"{samplesJoined}\""));
            }

            File.WriteAllText(csvPath, sbCsv.ToString(), Encoding.UTF8);

            return (jsonPath, csvPath);
        }
    }
}
