using System.Diagnostics;
using TEHhub.OffsetDoctor.Process;
using TEHhub.OffsetDoctor.Recovery;
using TEHhub.OffsetDoctor.Reporting;

Console.WriteLine("TEHhub.OffsetDoctor - PoE2 Read-Only Offset Diagnostic & Recovery Tool");

string command = "recover";
int? explicitPid = null;
int? expectedGold = null;
string? outputPath = null;

for (int i = 0; i < args.Length; i++)
{
    var arg = args[i];
    if (arg.Equals("validate", StringComparison.OrdinalIgnoreCase))
    {
        command = "validate";
    }
    else if (arg.Equals("recover", StringComparison.OrdinalIgnoreCase))
    {
        command = "recover";
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

var targetProcess = ProcessDiscovery.FindTargetProcess(explicitPid);
if (targetProcess == null)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("Error: Could not find running Path of Exile process.");
    Console.ResetColor();
    Console.WriteLine("Searched process names: " + string.Join(", ", ProcessDiscovery.KnownProcessNames));
    Console.WriteLine("Please launch Path of Exile 2 or specify PID using --pid <pid>.");
    return 1;
}

Console.WriteLine($"Found Target Process: {targetProcess.ProcessName} (PID: {targetProcess.Id})");

using var reader = new WindowsProcessMemoryReader(targetProcess.Id);
var engine = new OffsetRecoveryEngine();

OffsetDoctorReport report = string.Equals(command, "validate", StringComparison.OrdinalIgnoreCase)
    ? engine.RunValidation(reader, expectedGold)
    : engine.RunRecovery(reader, expectedGold);

ConsoleReportWriter.PrintReport(report);

if (!string.IsNullOrEmpty(outputPath))
{
    JsonReportExporter.ExportToFile(report, outputPath);
    Console.WriteLine($"Exported JSON report to: {outputPath}");
}

return report.IsChainHealthy ? 0 : 2;

static void PrintUsage()
{
    Console.WriteLine("Usage: TEHhub.OffsetDoctor [command] [options]");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  validate               Validate the compiled offset chain without searching for candidates.");
    Console.WriteLine("  recover                Validate and perform bounded recovery searches for broken offsets (default).");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --pid <pid>            Target a specific process ID.");
    Console.WriteLine("  --gold <amount>        Provide your current inventory gold amount for exact matching.");
    Console.WriteLine("  --output <path>        Path to write the JSON diagnostic report.");
    Console.WriteLine("  --help, -h             Show help information.");
}
