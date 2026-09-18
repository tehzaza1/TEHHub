namespace AreaModOffsetScanner
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Linq;

    internal static class Program
    {
        // Target process names specifically restricted to Path of Exile 2 executables
        private static readonly string[] TargetProcessNames =
        {
            "PathofExile2Steam",
            "PathofExile2_x64Steam",
            "PathofExile2_x64",
            "PathofExile2",
            "PathOfExile"
        };

        public static int Main(string[] args)
        {
            Console.Title = "PoE2 AreaMod Offset Scanner - Phase 2 (Multi-Hypothesis)";

            var parsedArgs = ParseCommandLine(args);
            if (parsedArgs.ShowHelp)
            {
                PrintHelp();
                return 0;
            }

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("================================================================================");
            Console.WriteLine("    PoE2 Standalone AreaMods Scanner - Phase 2 (Multi-Hypothesis & Verify)      ");
            Console.WriteLine("================================================================================");
            Console.ResetColor();

            // 1. Locate target process
            var pid = parsedArgs.ProcessId ?? SelectTargetProcess(parsedArgs.NonInteractive);
            if (pid <= 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[ERROR] No valid Path of Exile 2 process selected. Exiting.");
                Console.ResetColor();
                return 1;
            }

            NativeMemoryReader reader;
            try
            {
                reader = NativeMemoryReader.Open(pid);
                Console.WriteLine($"[+] Attached read-only to {reader.ProcessName} (PID: {reader.ProcessId})");
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[ERROR] Failed to attach to process {pid}: {ex.Message}");
                Console.ResetColor();
                return 1;
            }

            using (reader)
            {
                // 2. Obtain ServerDataObject address
                var serverDataAddress = parsedArgs.ServerDataObjectAddress;
                if (serverDataAddress == IntPtr.Zero)
                {
                    serverDataAddress = PromptForAddress();
                }

                if (serverDataAddress == IntPtr.Zero)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[ERROR] Invalid ServerDataObject address. Exiting.");
                    Console.ResetColor();
                    return 1;
                }

                // 3. Derive playerServerDataAddress
                Console.WriteLine("\n[+] Deriving playerServerDataAddress from ServerDataObject...");
                var derivation = PointerDerivation.DerivePlayerServerData(reader, serverDataAddress);
                Console.WriteLine(derivation.DerivationLog);

                if (!derivation.IsValid)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[FATAL] Pointer derivation failed: {derivation.FailureReason}");
                    Console.WriteLine("Stopping scan. No heuristic guessing will be performed.");
                    Console.ResetColor();
                    return 1;
                }

                var playerServerDataAddress = derivation.PlayerServerDataAddress;

                // 4. Live Verification of PlayerServerData using PlayerInventories (+0x320)
                Console.WriteLine("\n[+] Verifying PlayerServerData base via known PlayerInventories vector (+0x320)...");
                var verification = InventoryVerifier.Verify(reader, playerServerDataAddress);
                Console.WriteLine(verification.Log);

                // 5. Optional ground truth UI count
                var expectedUiCount = parsedArgs.ExpectedUiModCount;
                if (!expectedUiCount.HasValue && !parsedArgs.NonInteractive)
                {
                    expectedUiCount = PromptForExpectedUiMods();
                }

                // 6. Execute Primary Scan on PlayerServerData
                Console.WriteLine($"\n[+] Scanning PlayerServerData memory from 0x{parsedArgs.StartOffset:X4} to 0x{parsedArgs.EndOffset:X4} (step {parsedArgs.Step} bytes)...");
                var scanResult = ScanSession.Execute(
                    reader,
                    playerServerDataAddress,
                    playerServerDataAddress,
                    serverDataAddress,
                    targetBaseName: "PlayerServerData",
                    parsedArgs.StartOffset,
                    parsedArgs.EndOffset,
                    parsedArgs.Step,
                    expectedUiCount);

                // 7. Report results
                ScanReporter.PrintConsoleReport(scanResult);

                // 8. Export JSON and CSV files
                var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                var playerBase = parsedArgs.OutputPath != null ? $"{parsedArgs.OutputPath}-PlayerServerData" : $"AreaModScan-Phase2-PlayerServerData-{timestamp}";
                var (jsonPath, csvPath) = ScanReporter.ExportFiles(scanResult, playerBase);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("\n[+] PlayerServerData Scan artifacts exported successfully:");
                Console.WriteLine($"    JSON Report: {Path.GetFullPath(jsonPath)}");
                Console.WriteLine($"    CSV Table:   {Path.GetFullPath(csvPath)}");
                Console.ResetColor();

                // 9. If requested, also scan ServerDataObject directly
                if (parsedArgs.ScanServerDataDirectly)
                {
                    Console.WriteLine($"\n[+] Scanning ServerDataObject memory directly from 0x0000 to 0x1000 (step {parsedArgs.Step} bytes)...");
                    var sdScanResult = ScanSession.Execute(
                        reader,
                        serverDataAddress,
                        playerServerDataAddress,
                        serverDataAddress,
                        targetBaseName: "ServerDataObject",
                        0x0000,
                        0x1000,
                        parsedArgs.Step,
                        expectedUiCount);

                    ScanReporter.PrintConsoleReport(sdScanResult);
                    var sdBase = parsedArgs.OutputPath != null ? $"{parsedArgs.OutputPath}-ServerDataObject" : $"AreaModScan-Phase2-ServerDataObject-{timestamp}";
                    var (sdJson, sdCsv) = ScanReporter.ExportFiles(sdScanResult, sdBase);
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("\n[+] ServerDataObject Scan artifacts exported:");
                    Console.WriteLine($"    JSON Report: {Path.GetFullPath(sdJson)}");
                    Console.WriteLine($"    CSV Table:   {Path.GetFullPath(sdCsv)}");
                    Console.ResetColor();
                }

                Console.WriteLine("\nScan complete. Press Enter to exit...");
                if (!parsedArgs.NonInteractive)
                {
                    Console.ReadLine();
                }
            }

            return 0;
        }

        private static int SelectTargetProcess(bool nonInteractive)
        {
            var processes = Process.GetProcesses()
                .Where(p => TargetProcessNames.Any(name => p.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(p => p.Id)
                .ToList();

            if (processes.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[!] No running Path of Exile 2 processes automatically detected.");
                Console.ResetColor();

                if (nonInteractive)
                {
                    return -1;
                }

                Console.Write("Enter PoE2 Process ID (PID) manually: ");
                var rawPid = Console.ReadLine()?.Trim();
                if (int.TryParse(rawPid, out var manualPid))
                {
                    return manualPid;
                }

                return -1;
            }

            if (processes.Count == 1)
            {
                var p = processes[0];
                Console.WriteLine($"[+] Auto-selected PoE2 process: {p.ProcessName} (PID: {p.Id}, Window: \"{p.MainWindowTitle}\")");
                return p.Id;
            }

            Console.WriteLine($"[?] Multiple matching PoE2 processes detected ({processes.Count}):");
            for (var i = 0; i < processes.Count; i++)
            {
                var p = processes[i];
                Console.WriteLine($"  [{i + 1}] PID: {p.Id,-6} | {p.ProcessName,-20} | Window: \"{p.MainWindowTitle}\"");
            }

            if (nonInteractive)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[ERROR] Multiple PoE2 processes detected in non-interactive mode. Please specify target process with --pid <PID>.");
                Console.ResetColor();
                return -1;
            }

            while (true)
            {
                Console.Write($"Select process [1-{processes.Count}] or enter PID: ");
                var input = Console.ReadLine()?.Trim();
                if (int.TryParse(input, out var choice))
                {
                    if (choice >= 1 && choice <= processes.Count)
                    {
                        return processes[choice - 1].Id;
                    }

                    if (processes.Any(p => p.Id == choice))
                    {
                        return choice;
                    }
                }

                Console.WriteLine("Invalid selection. Try again.");
            }
        }

        private static IntPtr PromptForAddress()
        {
            while (true)
            {
                Console.WriteLine("\nEnter ServerDataObject address (from TEH debug window, e.g. 0x5FB96A52000):");
                Console.Write("Address: ");
                var raw = Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(raw))
                {
                    return IntPtr.Zero;
                }

                if (TryParseHexAddress(raw, out var address))
                {
                    return address;
                }

                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Invalid hex address format. Example valid input: 5FB96A52000 or 0x5FB96A52000");
                Console.ResetColor();
            }
        }

        private static int? PromptForExpectedUiMods()
        {
            Console.Write("\n(Optional) Enter expected visible UI AreaMod line count (e.g. 30, or press Enter to skip): ");
            var input = Console.ReadLine()?.Trim();
            if (int.TryParse(input, out var count) && count >= 0)
            {
                return count;
            }

            return null;
        }

        public static bool TryParseHexAddress(string input, out IntPtr address)
        {
            address = IntPtr.Zero;
            if (string.IsNullOrWhiteSpace(input))
            {
                return false;
            }

            var clean = input.Trim();
            if (clean.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                clean = clean[2..];
            }

            if (long.TryParse(clean, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var val))
            {
                address = new IntPtr(val);
                return true;
            }

            return false;
        }

        private static int ParseOffset(string input, int defaultVal)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return defaultVal;
            }

            var clean = input.Trim();
            if (clean.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(clean[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hexVal))
                {
                    return hexVal;
                }
            }
            else if (int.TryParse(clean, out var decVal))
            {
                return decVal;
            }

            return defaultVal;
        }

        private sealed class CommandLineArgs
        {
            public int? ProcessId { get; set; }
            public IntPtr ServerDataObjectAddress { get; set; }
            public int StartOffset { get; set; } = 0x0000;
            public int EndOffset { get; set; } = 0x4000;
            public int Step { get; set; } = 8;
            public int? ExpectedUiModCount { get; set; }
            public string? OutputPath { get; set; }
            public bool ScanServerDataDirectly { get; set; }
            public bool NonInteractive { get; set; }
            public bool ShowHelp { get; set; }
        }

        private static CommandLineArgs ParseCommandLine(string[] args)
        {
            var result = new CommandLineArgs();

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg.Equals("--help", StringComparison.OrdinalIgnoreCase) || arg.Equals("-h", StringComparison.OrdinalIgnoreCase))
                {
                    result.ShowHelp = true;
                    return result;
                }

                if (arg.Equals("--non-interactive", StringComparison.OrdinalIgnoreCase) || arg.Equals("--batch", StringComparison.OrdinalIgnoreCase))
                {
                    result.NonInteractive = true;
                    continue;
                }

                if (arg.Equals("--scan-server-data", StringComparison.OrdinalIgnoreCase) || arg.Equals("--both", StringComparison.OrdinalIgnoreCase))
                {
                    result.ScanServerDataDirectly = true;
                    continue;
                }

                if (i + 1 < args.Length)
                {
                    var next = args[i + 1];
                    switch (arg.ToLowerInvariant())
                    {
                        case "--pid":
                        case "-p":
                            if (int.TryParse(next, out var p))
                            {
                                result.ProcessId = p;
                                i++;
                            }
                            break;

                        case "--address":
                        case "-a":
                            if (TryParseHexAddress(next, out var addr))
                            {
                                result.ServerDataObjectAddress = addr;
                                i++;
                            }
                            break;

                        case "--start":
                            result.StartOffset = ParseOffset(next, 0x0000);
                            i++;
                            break;

                        case "--end":
                            result.EndOffset = ParseOffset(next, 0x4000);
                            i++;
                            break;

                        case "--step":
                            if (int.TryParse(next, out var st) && st > 0)
                            {
                                result.Step = st;
                                i++;
                            }
                            break;

                        case "--expected":
                        case "-e":
                            if (int.TryParse(next, out var exp))
                            {
                                result.ExpectedUiModCount = exp;
                                i++;
                            }
                            break;

                        case "--out":
                        case "-o":
                            result.OutputPath = next;
                            i++;
                            break;
                    }
                }
            }

            return result;
        }

        private static void PrintHelp()
        {
            Console.WriteLine(@"
PoE2 AreaMods Memory Offset Scanner - Phase 2 (Multi-Hypothesis & Live Verification)

Usage:
  AreaModOffsetScanner.exe [options]

Options:
  --pid, -p <PID>         Target PoE2 process ID (optional; auto-detected if omitted)
  --address, -a <HEX>     ServerDataObject address (e.g. 0x5FB96A52000)
  --start <HEX/DEC>       Start offset from playerServerData (default: 0x0000)
  --end <HEX/DEC>         End offset from playerServerData (default: 0x4000)
  --step <INT>            Scan step in bytes (default: 8)
  --expected, -e <INT>    Expected UI AreaMod lines for comparison (e.g. 30)
  --scan-server-data      Also scan ServerDataObject directly (0x0000..0x1000)
  --out, -o <PATH>        Custom base output filename for JSON and CSV exports
  --non-interactive       Run in batch/non-interactive mode without prompts
  --help, -h              Show this help message

Example:
  dotnet run --project tools\AreaModOffsetScanner\AreaModOffsetScanner.csproj -- -a 0x5FB96A52000 -e 30 --scan-server-data
");
        }
    }
}
