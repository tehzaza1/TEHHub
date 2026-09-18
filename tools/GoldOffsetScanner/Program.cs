namespace GoldOffsetScanner
{
    using System;
    using System.Diagnostics;
    using System.Linq;

    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine("================================================================================");
            Console.WriteLine("         PoE2 Value-Guided Differential Memory Scanner (Gold Discovery)         ");
            Console.WriteLine("================================================================================");

            var session = ScanSession.LoadOrCreate();

            if (args.Contains("--reset", StringComparer.OrdinalIgnoreCase))
            {
                session.Reset();
                Console.WriteLine("[Session] Reset complete. Previous scan candidates cleared.");
                return 0;
            }

            // Find target process
            int? pidArg = null;
            var nonFlagArgs = new List<string>();
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i].Equals("--pid", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedPid))
                {
                    pidArg = parsedPid;
                    i++; // skip pid val
                }
                else if (args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    // flag
                }
                else
                {
                    nonFlagArgs.Add(args[i]);
                }
            }

            var targetPid = pidArg ?? FindPoEProcessId();
            if (targetPid <= 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[Error] PathOfExile process not found. Please ensure Path of Exile 2 is running.");
                Console.ResetColor();
                return 1;
            }

            Console.WriteLine($"[Process] Target PathOfExile detected: PID {targetPid}");

            using var reader = NativeMemoryReader.Open(targetPid);
            Console.WriteLine($"[Process] Main Module Base: 0x{reader.MainModuleBase.ToInt64():X12} (Size: {reader.MainModuleSize / 1024 / 1024:N0} MB)");

            if (args.Contains("--report", StringComparer.OrdinalIgnoreCase))
            {
                PrintCandidateReport(session, reader);
                return 0;
            }

            if (args.Contains("--inspect-vector", StringComparer.OrdinalIgnoreCase))
            {
                Vector1D8Inspector.Inspect(reader);
                return 0;
            }

            if (args.Contains("--validate-chain", StringComparer.OrdinalIgnoreCase))
            {
                StableChainValidator.Validate(reader);
                return 0;
            }

            if (args.Contains("--dump-psd", StringComparer.OrdinalIgnoreCase))
            {
                var targetCandidate = session.Data.Candidates.FirstOrDefault(c => !c.IsUiTextCandidate);
                var goldAddr = targetCandidate?.Address ?? 0x04828ED049D0;
                PsdOffsetAnalyzer.Analyze(reader, goldAddr);
                return 0;
            }

            if (args.Contains("--scan-psd-range", StringComparer.OrdinalIgnoreCase))
            {
                var targetCandidate = session.Data.Candidates.FirstOrDefault(c => !c.IsUiTextCandidate);
                var goldAddr = targetCandidate?.Address ?? 0x04828ED049D0;
                PsdArrayScanner.ScanRange(reader, goldAddr);
                return 0;
            }

            if (args.Contains("--watch", StringComparer.OrdinalIgnoreCase))
            {
                LiveGoldWatcher.Watch(reader, 20);
                return 0;
            }

            // Check if user provided direct gold value as an argument
            long? targetGold = null;
            foreach (var arg in nonFlagArgs)
            {
                if (long.TryParse(arg.Replace(",", "").Trim(), out var parsedVal) && parsedVal >= 0)
                {
                    targetGold = parsedVal;
                    break;
                }
            }

            if (targetGold.HasValue)
            {
                ExecutePass(session, reader, targetGold.Value);
                PrintCandidateReport(session, reader);
                return 0;
            }

            // Interactive mode
            RunInteractiveLoop(session, reader);
            return 0;
        }

        private static void RunInteractiveLoop(ScanSession session, NativeMemoryReader reader)
        {
            Console.WriteLine("\n[Interactive Mode] Enter exact in-game Gold value to scan, 'report' to view candidates, 'reset' to clear session, or 'exit' to quit.\n");

            if (session.Data.Passes.Count > 0)
            {
                Console.WriteLine($"Current Session: {session.Data.Passes.Count} passes completed, {session.Data.Candidates.Count} active candidates.");
            }

            while (true)
            {
                Console.Write("\nGold Value > ");
                var input = Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(input))
                {
                    continue;
                }

                if (input.Equals("exit", StringComparison.OrdinalIgnoreCase) || input.Equals("q", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                if (input.Equals("reset", StringComparison.OrdinalIgnoreCase))
                {
                    session.Reset();
                    Console.WriteLine("[Session] Reset complete. Previous candidates cleared.");
                    continue;
                }

                if (input.Equals("report", StringComparison.OrdinalIgnoreCase))
                {
                    PrintCandidateReport(session, reader);
                    continue;
                }

                if (long.TryParse(input.Replace(",", ""), out var goldVal) && goldVal >= 0)
                {
                    ExecutePass(session, reader, goldVal);
                    PrintCandidateReport(session, reader);
                }
                else
                {
                    Console.WriteLine("Invalid numeric value. Please enter integer Gold value (e.g. 12345).");
                }
            }
        }

        private static void ExecutePass(ScanSession session, NativeMemoryReader reader, long goldVal)
        {
            session.ExecuteScanPass(reader, goldVal);
        }

        private static void PrintCandidateReport(ScanSession session, NativeMemoryReader reader)
        {
            var candidates = session.Data.Candidates;
            var passes = session.Data.Passes;

            Console.WriteLine("\n--------------------------------------------------------------------------------");
            Console.WriteLine($"                     CANDIDATE DISCOVERY REPORT                                 ");
            Console.WriteLine("--------------------------------------------------------------------------------");
            Console.WriteLine($"Total Passes Completed: {passes.Count}");
            foreach (var p in passes)
            {
                Console.WriteLine($"  Pass #{p.PassNumber}: Gold = {p.TargetGoldValue:N0} -> {p.CandidatesFound:N0} candidates");
            }
            Console.WriteLine($"Surviving Candidate Count: {candidates.Count:N0}");

            if (candidates.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("No candidates survived all scan passes. You may need to run 'reset' and start a new scan.");
                Console.ResetColor();
                return;
            }

            var nativeCandidates = candidates.Where(c => !c.IsUiTextCandidate).ToList();
            var uiCandidates = candidates.Where(c => c.IsUiTextCandidate).ToList();

            Console.WriteLine($"  -> Native Numeric Candidates: {nativeCandidates.Count:N0}");
            Console.WriteLine($"  -> UI Text Candidates:        {uiCandidates.Count:N0}");

            Console.WriteLine("\nResolving SDK Roots for Structure Context...");
            var sdkCtx = PointerEvaluator.ResolveSdkRoots(reader);
            if (sdkCtx.IsValid)
            {
                Console.WriteLine($"  SDK Roots: InGameState=0x{sdkCtx.InGameState:X12}, AreaInstance=0x{sdkCtx.AreaInstance:X12}, ServerData=0x{sdkCtx.ServerData:X12}, PlayerServerData=0x{sdkCtx.PlayerServerData:X12}");
            }
            else
            {
                Console.WriteLine("  Note: In-game state not active or roots not yet fully loaded.");
            }

            if (candidates.Count <= 25)
            {
                Console.WriteLine("\n================================================================================");
                Console.WriteLine("                         DETAILED SURVIVING CANDIDATES                          ");
                Console.WriteLine("================================================================================");

                var rank = 1;
                foreach (var c in candidates)
                {
                    c.PossibleStructureContext = PointerEvaluator.EvaluateCandidateContext(reader, sdkCtx, c.Address);

                    var tag = c.IsUiTextCandidate ? "[UI TEXT]" : "[NATIVE INT]";
                    var historyStr = string.Join(" -> ", c.ValueHistory.Select(v => $"{v:N0}"));

                    Console.ForegroundColor = c.IsUiTextCandidate ? ConsoleColor.Cyan : ConsoleColor.Green;
                    Console.WriteLine($"\n--- Candidate #{rank++}: {tag} 0x{c.Address:X12} ({c.Type}) ---");
                    Console.ResetColor();

                    Console.WriteLine($"  Current Value:       {c.CurrentValue:N0}");
                    Console.WriteLine($"  Observed History:    {historyStr} (Survived all {passes.Count} passes)");
                    Console.WriteLine($"  Structure Context:   {c.PossibleStructureContext}");
                    Console.WriteLine($"  Memory Region:       Base=0x{c.RegionBase:X12}, Size=0x{c.RegionSize:X} ({c.RegionProtect}, {c.RegionType})");
                    Console.WriteLine($"  Offset In Region:    +0x{c.OffsetInRegion:X}");

                    Console.WriteLine("  Surrounding Memory Hex Dump (+/- 0x80 bytes):");
                    var hexDump = reader.FormatHexDump(c.Address, 0x80);
                    Console.WriteLine(hexDump);

                    if (!c.IsUiTextCandidate)
                    {
                        PointerEvaluator.AnalyzePointerChains(reader, sdkCtx, c.Address);
                    }
                }
            }
            else
            {
                Console.WriteLine($"\nShowing top 10 of {candidates.Count:N0} candidates (perform more differential scan passes to narrow down):");
                for (var i = 0; i < Math.Min(10, candidates.Count); i++)
                {
                    var c = candidates[i];
                    var ctxStr = PointerEvaluator.EvaluateCandidateContext(reader, sdkCtx, c.Address);
                    var tag = c.IsUiTextCandidate ? "[UI TEXT]" : "[NATIVE]";
                    Console.WriteLine($"  #{i + 1:D2} {tag} 0x{c.Address:X12} ({c.Type,-10}) Val={c.CurrentValue,10:N0} | {ctxStr}");
                }
            }

            Console.WriteLine("--------------------------------------------------------------------------------");
        }

        private static int FindPoEProcessId()
        {
            var pList = Process.GetProcessesByName("PathOfExile");
            if (pList.Length > 0) return pList[0].Id;

            pList = Process.GetProcessesByName("PathOfExileSteam");
            if (pList.Length > 0) return pList[0].Id;

            pList = Process.GetProcessesByName("PathOfExile_x64");
            if (pList.Length > 0) return pList[0].Id;

            pList = Process.GetProcessesByName("PathOfExile_x64Steam");
            if (pList.Length > 0) return pList[0].Id;

            pList = Process.GetProcessesByName("PathOfExile2");
            if (pList.Length > 0) return pList[0].Id;

            return 0;
        }
    }
}
