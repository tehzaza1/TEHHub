namespace TEHhub.OffsetDoctor.Reporting;

using TEHhub.OffsetDoctor.Manifest;
using TEHhub.OffsetDoctor.Process;
using TEHhub.OffsetDoctor.Validation;
using TEHhub.OffsetDoctor.Watch;

public static class ConsoleWatchWriter
{
    public static void PrintHeader(
        ProcessMetadata processMetadata,
        TimeSpan interval,
        TimeSpan duration,
        IReadOnlyList<WatchTargetInfo> targets,
        ValidationGroundTruth? groundTruth)
    {
        Console.WriteLine("================================================================================");
        Console.WriteLine("  TEHHub Offset Doctor — Realtime State-Dependent Watch Mode V1");
        Console.WriteLine("================================================================================");
        Console.WriteLine($"Target Process: {processMetadata.ProcessName} (PID {processMetadata.ProcessId})");
        Console.WriteLine($"Watch Interval: {interval.TotalMilliseconds:F0} ms | Duration: {duration.TotalSeconds:F0} s | Watched Targets: {targets.Count}");

        if (groundTruth != null && (groundTruth.ExpectedGold.HasValue || groundTruth.ExpectedHpCurrent.HasValue ||
            groundTruth.ExpectedMpCurrent.HasValue || groundTruth.ExpectedEsCurrent.HasValue))
        {
            var gtParts = new List<string>();
            if (groundTruth.ExpectedGold.HasValue) gtParts.Add($"Gold={groundTruth.ExpectedGold.Value:N0}");
            if (groundTruth.ExpectedHpCurrent.HasValue) gtParts.Add($"HP={groundTruth.ExpectedHpCurrent.Value}");
            if (groundTruth.ExpectedMpCurrent.HasValue) gtParts.Add($"MP={groundTruth.ExpectedMpCurrent.Value}");
            if (groundTruth.ExpectedEsCurrent.HasValue) gtParts.Add($"ES={groundTruth.ExpectedEsCurrent.Value}");
            Console.WriteLine($"Ground Truth: {string.Join(", ", gtParts)}");
        }

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("Watched Targets & User-Action Hints:");
        foreach (var t in targets)
        {
            Console.WriteLine($"  * {t.DisplayName,-38} -> {t.UserActionHint}");
        }
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine("Watching for runtime state transitions (Press Ctrl+C to finish early)...");
        Console.WriteLine("--------------------------------------------------------------------------------");
    }

    public static void PrintTransition(string transitionMessage)
    {
        if (transitionMessage.Contains("VALID"))
        {
            Console.ForegroundColor = ConsoleColor.Green;
        }
        else if (transitionMessage.Contains("BROKEN"))
        {
            Console.ForegroundColor = ConsoleColor.Red;
        }
        else if (transitionMessage.Contains("active") || transitionMessage.Contains("readable"))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
        }

        Console.WriteLine(transitionMessage);
        Console.ResetColor();
    }

    public static void PrintSummary(WatchReport report)
    {
        Console.WriteLine();
        Console.WriteLine("================================================================================");
        Console.WriteLine("  TEHHub Offset Doctor — Watch Session Summary");
        Console.WriteLine("================================================================================");
        Console.WriteLine($"Total Duration: {report.Duration.TotalSeconds:F1} s | Total State Transitions: {report.TotalTransitions}");
        Console.WriteLine();

        Console.WriteLine($"{"Target",-38} {"Best Status",-12} {"Latest",-12} {"Action Observed",-16} {"Recommendation"}");
        Console.WriteLine(new string('-', 100));

        foreach (var s in report.TargetSummaries)
        {
            Console.Write($"{s.DisplayName,-38} ");

            PrintStatus(s.BestStatus);
            Console.Write(" ");
            PrintStatus(s.LatestStatus);
            Console.Write(" ");

            if (s.PlayerActionObserved)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write($"{"YES",-16} ");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"{"NO (inactive)",-16} ");
            }
            Console.ResetColor();

            Console.WriteLine(s.Recommendation);

            if (!string.IsNullOrEmpty(s.ObservedPointerOrValue))
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"    Observed: {s.ObservedPointerOrValue}");
                Console.ResetColor();
            }
            if (!string.IsNullOrEmpty(s.Reason) && (s.LatestStatus == ValidationStatus.BROKEN || !s.PlayerActionObserved))
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"    Note: {s.Reason}");
                Console.ResetColor();
            }
        }

        Console.WriteLine(new string('-', 100));
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("NOTICE: OffsetDoctor operates strictly in READ-ONLY mode. Zero memory writes performed.");
        Console.ResetColor();
        Console.WriteLine("================================================================================");
    }

    private static void PrintStatus(ValidationStatus? status)
    {
        var text = (status?.ToString() ?? "NONE").PadRight(12);
        switch (status)
        {
            case ValidationStatus.VALID:
                Console.ForegroundColor = ConsoleColor.Green;
                break;
            case ValidationStatus.UNVERIFIED:
                Console.ForegroundColor = ConsoleColor.Yellow;
                break;
            case ValidationStatus.BROKEN:
                Console.ForegroundColor = ConsoleColor.Red;
                break;
            case ValidationStatus.BLOCKED:
                Console.ForegroundColor = ConsoleColor.Magenta;
                break;
            default:
                Console.ForegroundColor = ConsoleColor.Gray;
                break;
        }
        Console.Write(text);
        Console.ResetColor();
    }
}
