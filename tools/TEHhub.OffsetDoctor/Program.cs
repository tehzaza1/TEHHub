using System.Diagnostics;
using System.Collections.Immutable;
using TEHhub.OffsetDoctor.Baseline;
using TEHhub.OffsetDoctor.Process;
using TEHhub.OffsetDoctor.Reporting;
using TEHhub.OffsetDoctor.RecoveryV1;
using TEHhub.OffsetDoctor.Validation;
using TEHhub.OffsetDoctor.Watch;

Console.WriteLine("TEHhub.OffsetDoctor — PoE2 Read-Only Offset Health Scanner");

var mode = ProgramMode.ValidateAll;
int? explicitPid = null;
int? expectedGold = null;
int? expectedHpCurrent = null;
int? expectedHpTotal = null;
int? expectedMpCurrent = null;
int? expectedMpTotal = null;
int? expectedEsCurrent = null;
int? expectedEsTotal = null;
int intervalMs = 500;
int durationSec = 120;
string targetFilter = "all";
string? outputPath = null;
string? inputPath = null;
long? currentRecoveryAddress = null;
var historicalRecoveryAddresses = new List<long>();
var requestedRecoveryTargets = RecoveryV1Registry.AllRunnableTargetIds;

for (int i = 0; i < args.Length; i++)
{
    var arg = args[i];
    if (arg.Equals("baseline", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        var subCmd = args[++i];
        if (subCmd.Equals("capture", StringComparison.OrdinalIgnoreCase))
        {
            mode = ProgramMode.BaselineCapture;
        }
        else if (subCmd.Equals("compare", StringComparison.OrdinalIgnoreCase))
        {
            mode = ProgramMode.BaselineCompare;
        }
    }
    else if (arg.Equals("watch", StringComparison.OrdinalIgnoreCase))
    {
        mode = ProgramMode.Watch;
    }
    else if (arg.Equals("recover", StringComparison.OrdinalIgnoreCase))
    {
        if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
        {
            var rawTarget = args[++i];
            if (!RecoveryV1CliParser.TryParse(rawTarget, out var targets, out var error))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error: {error}");
                Console.ResetColor();
                return 1;
            }
            requestedRecoveryTargets = targets;
        }
        else
        {
            requestedRecoveryTargets = RecoveryV1Registry.AllRunnableTargetIds;
        }
        mode = ProgramMode.RecoverV1;
    }
    else if (arg.Equals("validate-all", StringComparison.OrdinalIgnoreCase) || arg.Equals("validate", StringComparison.OrdinalIgnoreCase))
    {
        mode = ProgramMode.ValidateAll;
    }
    else if (arg.Equals("--input", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        inputPath = args[++i];
    }
    else if (arg.Equals("--output", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        outputPath = args[++i];
    }
    else if (arg.Equals("--current-address", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        currentRecoveryAddress = ParseAddress(args[++i]);
    }
    else if (arg.Equals("--historical-address", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        historicalRecoveryAddresses.Add(ParseAddress(args[++i]));
    }
    else if (arg.Equals("--pid", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        if (int.TryParse(args[++i], out var pid)) explicitPid = pid;
    }
    else if (arg.Equals("--gold", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        if (int.TryParse(args[++i], out var gold)) expectedGold = gold;
    }
    else if (arg.Equals("--hp", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        if (int.TryParse(args[++i], out var hp)) expectedHpCurrent = hp;
    }
    else if (arg.Equals("--hp-max", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        if (int.TryParse(args[++i], out var hpMax)) expectedHpTotal = hpMax;
    }
    else if (arg.Equals("--mp", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        if (int.TryParse(args[++i], out var mp)) expectedMpCurrent = mp;
    }
    else if (arg.Equals("--mp-max", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        if (int.TryParse(args[++i], out var mpMax)) expectedMpTotal = mpMax;
    }
    else if (arg.Equals("--es", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        if (int.TryParse(args[++i], out var es)) expectedEsCurrent = es;
    }
    else if (arg.Equals("--es-max", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        if (int.TryParse(args[++i], out var esMax)) expectedEsTotal = esMax;
    }
    else if (arg.Equals("--interval-ms", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        if (int.TryParse(args[++i], out var interval) && interval > 0) intervalMs = interval;
    }
    else if (arg.Equals("--duration-sec", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        if (int.TryParse(args[++i], out var duration) && duration > 0) durationSec = duration;
    }
    else if (arg.Equals("--target", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        targetFilter = args[++i];
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

var groundTruth = new ValidationGroundTruth
{
    ExpectedGold = expectedGold,
    ExpectedHpCurrent = expectedHpCurrent,
    ExpectedHpTotal = expectedHpTotal,
    ExpectedMpCurrent = expectedMpCurrent,
    ExpectedMpTotal = expectedMpTotal,
    ExpectedEsCurrent = expectedEsCurrent,
    ExpectedEsTotal = expectedEsTotal
};

switch (mode)
{
    case ProgramMode.RecoverV1:
    {
        var session = new RecoverySession(reader, RecoveryContextSnapshot.Create([]));
        var inputs = new Dictionary<string, TargetExecutionInput>();
        if (currentRecoveryAddress.HasValue || historicalRecoveryAddresses.Count > 0)
        {
            inputs["OD-001"] = new TargetExecutionInput(
                currentRecoveryAddress.HasValue ? new CurrentValidationInput(CurrentNumericValue: currentRecoveryAddress.Value) : null,
                historicalRecoveryAddresses.Count > 0 ? new PostDecisionComparisonInput(ConfiguredNumericValue: currentRecoveryAddress, HistoricalNumericValues: historicalRecoveryAddresses.ToImmutableArray()) : null);
        }

        var report = RecoveryV1MultiTargetCoordinator.Recover(session, requestedRecoveryTargets, inputs);
        RecoveryConsoleReportWriter.WriteAggregate(Console.Out, report);
        if (!string.IsNullOrEmpty(outputPath))
        {
            File.WriteAllText(outputPath, RecoveryJsonReportExporter.SerializeAggregate(report));
            Console.WriteLine($"Exported aggregate JSON report to: {outputPath}");
        }
        return report.AggregateStatus == RecoveryAggregateStatus.ERROR ? 2 : 0;
    }
    case ProgramMode.BaselineCapture:
    {
        var capturePath = outputPath ?? "offsetdoctor-baseline.json";
        var captureEngine = new BaselineCaptureEngine();
        var snapshot = captureEngine.Capture(reader, groundTruth);
        BaselineSnapshot.SaveToFile(snapshot, capturePath);
        ConsoleBaselineWriter.PrintCaptureSummary(snapshot, capturePath);
        return 0;
    }

    case ProgramMode.BaselineCompare:
    {
        var compareInputPath = inputPath ?? outputPath ?? "offsetdoctor-baseline.json";
        if (!File.Exists(compareInputPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: Baseline file not found: {compareInputPath}");
            Console.ResetColor();
            Console.WriteLine("Capture a baseline first using: TEHhub.OffsetDoctor.exe baseline capture --output <file>");
            return 1;
        }

        var baseline = BaselineSnapshot.LoadFromFile(compareInputPath);
        var validationRunner = new OffsetDoctorValidationRunner();
        var report = validationRunner.RunValidation(reader, groundTruth);

        var comparisonEngine = new BaselineComparisonEngine();
        var comparisonResult = comparisonEngine.Compare(baseline, report);
        ConsoleBaselineWriter.PrintComparison(comparisonResult, compareInputPath);

        return comparisonResult.HasCriticalRegressions ? 2 : 0;
    }

    case ProgramMode.Watch:
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var watchTargets = WatchTargetInfo.ResolveTargets(targetFilter);
        var interval = TimeSpan.FromMilliseconds(intervalMs);
        var duration = TimeSpan.FromSeconds(durationSec);

        ConsoleWatchWriter.PrintHeader(reader.Metadata, interval, duration, watchTargets, groundTruth);

        var watchEngine = new OffsetWatchEngine();
        var watchReport = watchEngine.RunWatch(
            reader,
            groundTruth,
            interval,
            duration,
            targetFilter,
            onTransition: ConsoleWatchWriter.PrintTransition,
            cancellationToken: cts.Token);

        ConsoleWatchWriter.PrintSummary(watchReport);

        if (!string.IsNullOrEmpty(outputPath))
        {
            JsonReportExporter.ExportToFile(watchReport, outputPath);
            Console.WriteLine($"Exported JSON watch report to: {outputPath}");
        }

        return 0;
    }

    case ProgramMode.ValidateAll:
    default:
    {
        var runner = new OffsetDoctorValidationRunner();
        var report = runner.RunValidation(reader, groundTruth);

        ConsoleReportWriter.PrintReport(report);

        if (!string.IsNullOrEmpty(outputPath))
        {
            JsonReportExporter.ExportToFile(report, outputPath);
            Console.WriteLine($"Exported JSON report to: {outputPath}");
        }

        return report.BrokenCount == 0 ? 0 : 2;
    }
}

static long ParseAddress(string text) => text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
    ? Convert.ToInt64(text[2..], 16) : long.Parse(text);

static void PrintUsage()
{
    Console.WriteLine("Usage: TEHhub.OffsetDoctor [command] [options]");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  validate-all                  Run full repository offset validation scan (default).");
    Console.WriteLine("  validate                      Run full repository offset validation scan.");
    Console.WriteLine("  watch                         Run realtime state-dependent watch mode.");
    Console.WriteLine("  recover <target>              Run read-only OffsetDoctor Recovery V1.");
    Console.WriteLine("                                Targets: od-001, od-063, od-114, od-144, od-145, v1, all");
    Console.WriteLine("                                Or comma-separated list (e.g. recover od-001,od-063)");
    Console.WriteLine("  baseline capture              Capture a baseline snapshot of current known-good offsets.");
    Console.WriteLine("  baseline compare              Compare live game offsets against a captured baseline snapshot.");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --input <path>                Input baseline snapshot file path for compare mode.");
    Console.WriteLine("  --output <path>               Output file path for baseline capture, validation, or recovery JSON report.");
    Console.WriteLine("  --pid <pid>                   Target a specific process ID.");
    Console.WriteLine("  --current-address <address>   Validate a configured OD-001 absolute address first (hex or decimal).");
    Console.WriteLine("  --historical-address <addr>  Add an address for post-decision comparison only (repeatable).");
    Console.WriteLine("  --gold <amount>               Provide current inventory gold amount for exact semantic verification.");
    Console.WriteLine("  --hp <current>                Provide current player Health amount.");
    Console.WriteLine("  --hp-max <total>              Provide total player Health amount.");
    Console.WriteLine("  --mp <current>                Provide current player Mana amount.");
    Console.WriteLine("  --mp-max <total>              Provide total player Mana amount.");
    Console.WriteLine("  --es <current>                Provide current player Energy Shield amount.");
    Console.WriteLine("  --es-max <total>              Provide total player Energy Shield amount.");
    Console.WriteLine("  --interval-ms <ms>            Watch polling interval in milliseconds (default: 500).");
    Console.WriteLine("  --duration-sec <sec>          Watch total duration in seconds (default: 120).");
    Console.WriteLine("  --target <name>               Watch targets filter: all, ui, loading, buffs, or specific node name (default: all).");
    Console.WriteLine("  --help, -h                    Show help information.");
}

enum ProgramMode
{
    ValidateAll,
    Watch,
    BaselineCapture,
    BaselineCompare,
    RecoverV1
}
