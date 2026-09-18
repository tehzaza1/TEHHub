namespace TEHhub.OffsetDoctor.Reporting;

using TEHhub.OffsetDoctor.Evidence;
using TEHhub.OffsetDoctor.Manifest;
using TEHhub.OffsetDoctor.Recovery;

public static class ConsoleReportWriter
{
    public static void PrintReport(OffsetDoctorReport report)
    {
        Console.WriteLine("================================================================================");
        Console.WriteLine("                   TEHhub OffsetDoctor Diagnostic Report                        ");
        Console.WriteLine("================================================================================");
        Console.WriteLine($"Target Process : {report.ProcessMetadata.ProcessName} (PID: {report.ProcessMetadata.ProcessId})");
        Console.WriteLine($"File Version   : {report.ProcessMetadata.FileVersion}");
        Console.WriteLine($"Module Base    : 0x{report.ProcessMetadata.ModuleBase.ToInt64():X}");
        Console.WriteLine($"Elevated       : {report.ProcessMetadata.IsElevated}");
        Console.WriteLine($"Timestamp (UTC): {report.TimestampUtc:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"Chain Status   : {(report.IsChainHealthy ? "HEALTHY (ALL NODES VALID)" : "DEGRADED / BROKEN")}");
        Console.WriteLine("--------------------------------------------------------------------------------");
        Console.WriteLine("NODE VALIDATION PIPELINE:");
        Console.WriteLine("--------------------------------------------------------------------------------");

        for (int i = 0; i < report.Results.Count; i++)
        {
            var res = report.Results[i];
            var indent = new string(' ', i * 2);
            var statusColor = GetStatusColor(res.Status);

            var prevColor = Console.ForegroundColor;
            Console.Write($"{indent}[");
            Console.ForegroundColor = statusColor;
            Console.Write($"{res.Status,-15}");
            Console.ForegroundColor = prevColor;
            Console.Write($"] {res.NodeDisplayName} (Offset: +0x{res.ConfiguredOffset:X})");

            if (res.IsProvisional)
            {
                Console.Write(" [PROVISIONAL PARENT]");
            }

            if (res.ExtractedValue != null)
            {
                Console.Write($" -> Value: {res.ExtractedValue}");
            }

            Console.WriteLine();

            if (res.ErrorMessage != null && res.Status != ValidationStatus.VALID)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"{indent}    Note: {res.ErrorMessage}");
                Console.ForegroundColor = prevColor;
            }

            if (res.Candidates.Count > 0)
            {
                Console.WriteLine($"{indent}    Candidates ({res.Candidates.Count} found):");
                foreach (var c in res.Candidates.Take(3))
                {
                    var confColor = c.Confidence switch
                    {
                        Confidence.HIGH => ConsoleColor.Green,
                        Confidence.MEDIUM => ConsoleColor.Yellow,
                        _ => ConsoleColor.Red
                    };

                    Console.Write($"{indent}      * +0x{c.Offset:X} (Delta: {c.Offset - res.ConfiguredOffset:+0;-0;0}) | Score: {c.Score} | Conf: ");
                    Console.ForegroundColor = confColor;
                    Console.Write($"{c.Confidence}");
                    Console.ForegroundColor = prevColor;
                    if (c.DownstreamEvidenceSummary != null)
                    {
                        Console.Write($" | {c.DownstreamEvidenceSummary}");
                    }
                    Console.WriteLine();
                }
            }
        }

        Console.WriteLine("--------------------------------------------------------------------------------");

        if (report.Recommendations.Count > 0)
        {
            Console.WriteLine("ACTIONABLE OFFSET RECOMMENDATIONS:");
            Console.WriteLine("--------------------------------------------------------------------------------");
            Console.WriteLine($"{"Node",-25} | {"Current",-8} | {"Suggested",-9} | {"Delta",-8} | {"Conf",-6} | {"Score",-5} | Source");
            Console.WriteLine(new string('-', 80));

            foreach (var rec in report.Recommendations)
            {
                Console.WriteLine($"{rec.NodeId,-25} | +0x{rec.CurrentOffset,-6:X} | +0x{rec.SuggestedOffset,-7:X} | {rec.Delta,+6:+0;-0;0} | {rec.Confidence,-6} | {rec.Score,-5} | {rec.SourceLocation}");
            }
            Console.WriteLine("--------------------------------------------------------------------------------");
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("NOTICE: OffsetDoctor operates strictly in READ-ONLY mode. Source files and process memory were not modified.");
        Console.ResetColor();
        Console.WriteLine("================================================================================");
    }

    private static ConsoleColor GetStatusColor(ValidationStatus status) => status switch
    {
        ValidationStatus.VALID => ConsoleColor.Green,
        ValidationStatus.CANDIDATE_FOUND => ConsoleColor.Cyan,
        ValidationStatus.AMBIGUOUS => ConsoleColor.Yellow,
        ValidationStatus.NEEDS_MANUAL_PROOF => ConsoleColor.Magenta,
        ValidationStatus.BROKEN => ConsoleColor.Red,
        ValidationStatus.BLOCKED => ConsoleColor.DarkYellow,
        _ => ConsoleColor.Gray
    };
}
