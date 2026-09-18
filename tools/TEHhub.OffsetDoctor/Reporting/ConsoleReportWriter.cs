namespace TEHhub.OffsetDoctor.Reporting;

using TEHhub.OffsetDoctor.Evidence;
using TEHhub.OffsetDoctor.Manifest;
using TEHhub.OffsetDoctor.Recovery;

public static class ConsoleReportWriter
{
    public static void PrintReport(OffsetDoctorReport report)
    {
        Console.WriteLine("================================================================================");
        Console.WriteLine("                     TEHhub Offset Doctor — Validate All                        ");
        Console.WriteLine("================================================================================");

        Console.WriteLine("Process");
        Console.WriteLine($"  PID ............... {report.ProcessMetadata.ProcessId}");
        Console.WriteLine($"  Name .............. {report.ProcessMetadata.ProcessName}");
        Console.WriteLine($"  Version ........... {(!string.IsNullOrEmpty(report.ProcessMetadata.FileVersion) ? report.ProcessMetadata.FileVersion : "N/A")}");
        Console.WriteLine($"  Module Base ....... 0x{report.ProcessMetadata.ModuleBase.ToInt64():X}");
        Console.WriteLine($"  Elevated .......... {report.ProcessMetadata.IsElevated}");
        Console.WriteLine($"  Timestamp (UTC) ... {report.TimestampUtc:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine();

        // Group by Category
        var categories = report.Results
            .GroupBy(r => r.Category)
            .OrderBy(g => GetCategoryOrder(g.Key));

        foreach (var group in categories)
        {
            Console.WriteLine(group.Key);
            foreach (var res in group)
            {
                var nameDisplay = res.NodeDisplayName;
                if (res.ConfiguredOffset != 0 && res.Category != "Static Roots")
                {
                    nameDisplay = $"{res.NodeDisplayName} (+0x{res.ConfiguredOffset:X})";
                }

                int dotsCount = Math.Max(2, 45 - nameDisplay.Length);
                var dots = new string('.', dotsCount);

                Console.Write($"  {nameDisplay} {dots} ");
                var prevColor = Console.ForegroundColor;
                Console.ForegroundColor = GetStatusColor(res.Status);
                Console.Write($"{res.Status}");
                Console.ForegroundColor = prevColor;

                if (res.ExtractedValue != null && res.Status == ValidationStatus.VALID)
                {
                    Console.Write($" [{res.ExtractedValue}]");
                }
                Console.WriteLine();

                // Detailed evidence on non-VALID status or notes
                if (res.Status != ValidationStatus.VALID)
                {
                    if (!string.IsNullOrEmpty(res.ErrorMessage))
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.WriteLine($"      Note: {res.ErrorMessage}");
                        Console.ForegroundColor = prevColor;
                    }
                    foreach (var ev in res.Evidence.Where(e => !e.Passed))
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"      FAIL: {ev.Description}");
                        Console.ForegroundColor = prevColor;
                    }
                }
            }
            Console.WriteLine();
        }

        Console.WriteLine("--------------------------------------------------------------------------------");
        Console.WriteLine("Summary");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  VALID ............. {report.ValidCount}");
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"  BROKEN ............ {report.BrokenCount}");
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.WriteLine($"  BLOCKED ........... {report.BlockedCount}");
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  UNVERIFIED ........ {report.UnverifiedCount}");
        Console.ResetColor();
        Console.WriteLine($"  Total Nodes ....... {report.TotalNodesCount}");
        Console.WriteLine("--------------------------------------------------------------------------------");
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("NOTICE: OffsetDoctor operates strictly in READ-ONLY mode. Zero memory writes performed.");
        Console.ResetColor();
        Console.WriteLine("================================================================================");
    }

    private static int GetCategoryOrder(string category) => category switch
    {
        "Static Roots" => 1,
        "Game States" => 2,
        "Area / Server Data" => 3,
        "ServerData & Inventory" => 4,
        "Player & Components" => 5,
        "UI Elements" => 6,
        "Area Loading State" => 7,
        _ => 99
    };

    private static ConsoleColor GetStatusColor(ValidationStatus status) => status switch
    {
        ValidationStatus.VALID => ConsoleColor.Green,
        ValidationStatus.UNVERIFIED => ConsoleColor.Yellow,
        ValidationStatus.BROKEN => ConsoleColor.Red,
        ValidationStatus.BLOCKED => ConsoleColor.DarkYellow,
        _ => ConsoleColor.Gray
    };
}
