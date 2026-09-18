namespace TEHhub.OffsetDoctor.Reporting;

using TEHhub.OffsetDoctor.Baseline;

public static class ConsoleBaselineWriter
{
    public static void PrintCaptureSummary(BaselineSnapshot snapshot, string outputPath)
    {
        Console.WriteLine("================================================================================");
        Console.WriteLine("  TEHHub Offset Doctor — Baseline Snapshot Captured");
        Console.WriteLine("================================================================================");
        Console.WriteLine($"Baseline saved: {outputPath}");
        Console.WriteLine($"Process: {snapshot.ProcessName} (PID {snapshot.ProcessId}) | ModuleBase: {snapshot.ModuleBase}");
        Console.WriteLine($"Timestamp: {snapshot.TimestampUtc:yyyy-MM-dd HH:mm:ss} UTC | Total Manifest Nodes: {snapshot.Summary.TotalNodesCount}");
        Console.WriteLine();
        Console.WriteLine("Summary Status Counts:");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  VALID .............. {snapshot.Summary.ValidCount}");
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"  BROKEN ............. {snapshot.Summary.BrokenCount}");
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine($"  BLOCKED ............ {snapshot.Summary.BlockedCount}");
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  UNVERIFIED ......... {snapshot.Summary.UnverifiedCount}");
        Console.ResetColor();
        Console.WriteLine("--------------------------------------------------------------------------------");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("NOTICE: OffsetDoctor operates strictly in READ-ONLY mode. Zero memory writes performed.");
        Console.ResetColor();
        Console.WriteLine("================================================================================");
    }

    public static void PrintComparison(BaselineComparisonResult result, string baselinePath)
    {
        Console.WriteLine("================================================================================");
        Console.WriteLine("  TEHHub Offset Doctor — Baseline Comparison Report");
        Console.WriteLine("================================================================================");
        Console.WriteLine($"Baseline File:   {baselinePath} ({result.Baseline.TimestampUtc:yyyy-MM-dd HH:mm:ss} UTC, {result.Baseline.ProcessName})");
        Console.WriteLine($"Current Runtime: {result.CurrentReport.ProcessMetadata.ProcessName} (PID {result.CurrentReport.ProcessMetadata.ProcessId})");
        Console.WriteLine();

        Console.WriteLine($"Comparison Delta Summary: {result.CriticalCount} Critical, {result.WarningCount} Warnings, {result.InformationalCount} Informational");
        Console.WriteLine(new string('-', 80));

        if (result.CriticalCount > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[CRITICAL REGRESSIONS]");
            foreach (var d in result.Deltas.Where(d => d.Severity == DeltaSeverity.Critical))
            {
                Console.WriteLine($"  * {d.DisplayName,-36} {d.Description}");
            }
            Console.ResetColor();
            Console.WriteLine();
        }

        if (result.WarningCount > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("[WARNINGS]");
            foreach (var d in result.Deltas.Where(d => d.Severity == DeltaSeverity.Warning))
            {
                Console.WriteLine($"  * {d.DisplayName,-36} {d.Description}");
            }
            Console.ResetColor();
            Console.WriteLine();
        }

        if (result.InformationalCount > 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("[INFORMATIONAL CHANGES]");
            foreach (var d in result.Deltas.Where(d => d.Severity == DeltaSeverity.Informational))
            {
                Console.WriteLine($"  * {d.DisplayName,-36} {d.Description}");
            }
            Console.ResetColor();
            Console.WriteLine();
        }

        Console.WriteLine(new string('-', 80));
        if (!result.HasCriticalRegressions)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("No critical baseline regressions detected.");
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"WARNING: {result.CriticalCount} critical regression(s) detected compared to baseline.");
            Console.ResetColor();
        }

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("NOTICE: OffsetDoctor operates strictly in READ-ONLY mode. Zero memory writes performed.");
        Console.ResetColor();
        Console.WriteLine("================================================================================");
    }
}
