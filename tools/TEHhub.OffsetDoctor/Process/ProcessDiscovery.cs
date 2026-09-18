namespace TEHhub.OffsetDoctor.Process;

using System.Diagnostics;

public static class ProcessDiscovery
{
    public static readonly string[] KnownProcessNames =
    [
        "PathOfExile",
        "PathOfExileSteam",
        "PathOfExile_x64",
        "PathOfExile_x64Steam",
        "PathOfExile2",
        "PathOfExile2Steam",
        "PathOfExile2_x64",
        "PathOfExile2_x64Steam",
        "PathOfExileEGS"
    ];

    public static Process? FindTargetProcess(int? explicitPid = null)
    {
        if (explicitPid.HasValue)
        {
            try
            {
                var proc = System.Diagnostics.Process.GetProcessById(explicitPid.Value);
                if (!proc.HasExited)
                {
                    return proc;
                }
            }
            catch
            {
                return null;
            }
        }

        var allProcesses = System.Diagnostics.Process.GetProcesses();
        foreach (var knownName in KnownProcessNames)
        {
            var match = allProcesses.FirstOrDefault(p =>
                string.Equals(p.ProcessName, knownName, StringComparison.OrdinalIgnoreCase) && !p.HasExited);
            if (match != null)
            {
                return match;
            }
        }

        return null;
    }

    public static List<Process> FindAllMatchingProcesses()
    {
        var results = new List<Process>();
        var allProcesses = System.Diagnostics.Process.GetProcesses();

        foreach (var p in allProcesses)
        {
            try
            {
                if (!p.HasExited && KnownProcessNames.Any(kn => string.Equals(kn, p.ProcessName, StringComparison.OrdinalIgnoreCase)))
                {
                    results.Add(p);
                }
            }
            catch
            {
                // Process might have exited or access denied
            }
        }

        return results;
    }
}
