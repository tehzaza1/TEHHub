using System.Diagnostics;
using TEHhub.OffsetDoctor.Process;
using TEHhub.OffsetDoctor.Recovery;
using TEHhub.OffsetDoctor.Reporting;

Console.WriteLine("TEHhub.OffsetDoctor — PoE2 Read-Only Offset Health Scanner");

int? explicitPid = null;
int? expectedGold = null;
string? outputPath = null;

for (int i = 0; i < args.Length; i++)
{
    var arg = args[i];
    if (arg.Equals("validate-all", StringComparison.OrdinalIgnoreCase) || arg.Equals("validate", StringComparison.OrdinalIgnoreCase))
    {
        // Default validation command
    }
    else if (arg.Equals("--pid", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        if (int.TryParse(args[++i], out var pid)) explicitPid = pid;
    }
    else if (arg.Equals("--gold", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        if (int.TryParse(args[++i], out var gold)) expectedGold = gold;
    }
    else if (arg.Equals("--output", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        outputPath = args[++i];
    }
    else if (arg.Equals("--help", StringComparison.OrdinalIgnoreCase) || arg.Equals("-h", StringComparison.OrdinalIgnoreCase))
    {
        PrintUsage();
        return 0;
    }
}

var discovery = ProcessDiscovery.DiscoverTargetProcess(explicitPid);
if (discovery.Status == ProcessDiscoveryStatus.MultipleMatches)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("Multiple matching Path of Exile processes detected:");
    foreach (var p in discovery.MatchingProcesses)
    {
        Console.WriteLine($"  * PID {p.Id}: {p.ProcessName} ({p.MainWindowTitle})");
    }
    Console.ResetColor();
    Console.WriteLine("Please specify which process to attach to using --pid <pid>.");
    return 1;
}

if (discovery.Status != ProcessDiscoveryStatus.Success || discovery.SelectedProcess == null)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"Error: {discovery.ErrorMessage ?? "Could not find running Path of Exile process."}");
    Console.ResetColor();
    Console.WriteLine("Searched process names: " + string.Join(", ", ProcessDiscovery.KnownProcessNames));
    Console.WriteLine("Please launch Path of Exile 2 or specify PID using --pid <pid>.");
    return 1;
}

var targetProcess = discovery.SelectedProcess;
using var reader = new WindowsProcessMemoryReader(targetProcess.Id);

var engine = new OffsetRecoveryEngine();
var report = engine.RunValidation(reader, expectedGold);

ConsoleReportWriter.PrintReport(report);

if (!string.IsNullOrEmpty(outputPath))
{
    JsonReportExporter.ExportToFile(report, outputPath);
    Console.WriteLine($"Exported JSON report to: {outputPath}");
}

return report.BrokenCount == 0 ? 0 : 2;

static void PrintUsage()
{
    Console.WriteLine("Usage: TEHhub.OffsetDoctor [command] [options]");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  validate-all           Run full repository offset validation scan (default).");
    Console.WriteLine("  validate               Run full repository offset validation scan.");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --pid <pid>            Target a specific process ID.");
    Console.WriteLine("  --gold <amount>        Provide current inventory gold amount for exact semantic verification.");
    Console.WriteLine("  --output <path>        Path to write the JSON diagnostic report.");
    Console.WriteLine("  --help, -h             Show help information.");
}
