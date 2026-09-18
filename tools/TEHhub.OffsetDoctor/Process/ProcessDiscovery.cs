namespace TEHhub.OffsetDoctor.Process;

using System.Diagnostics;

public enum ProcessDiscoveryStatus
{
    Success,
    MultipleMatches,
    NotFound
}

public sealed class ProcessDiscoveryResult
{
    public ProcessDiscoveryStatus Status { get; init; }
    public Process? SelectedProcess { get; init; }
    public List<Process> MatchingProcesses { get; init; } = [];
    public string? ErrorMessage { get; init; }
}

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

    public static ProcessDiscoveryResult DiscoverTargetProcess(int? explicitPid = null)
    {
        if (explicitPid.HasValue)
        {
            try
            {
                var proc = System.Diagnostics.Process.GetProcessById(explicitPid.Value);
                if (!proc.HasExited)
                {
                    return new ProcessDiscoveryResult
                    {
                        Status = ProcessDiscoveryStatus.Success,
                        SelectedProcess = proc,
                        MatchingProcesses = [proc]
                    };
                }
            }
            catch (Exception ex)
            {
                return new ProcessDiscoveryResult
                {
                    Status = ProcessDiscoveryStatus.NotFound,
                    ErrorMessage = $"Could not access process with PID {explicitPid.Value}: {ex.Message}"
                };
            }

            return new ProcessDiscoveryResult
            {
                Status = ProcessDiscoveryStatus.NotFound,
                ErrorMessage = $"Process with PID {explicitPid.Value} has already exited."
            };
        }

        var matching = FindAllMatchingProcesses();
        if (matching.Count == 0)
        {
            return new ProcessDiscoveryResult
            {
                Status = ProcessDiscoveryStatus.NotFound,
                ErrorMessage = "No running Path of Exile 2 process was discovered."
            };
        }

        if (matching.Count == 1)
        {
            return new ProcessDiscoveryResult
            {
                Status = ProcessDiscoveryStatus.Success,
                SelectedProcess = matching[0],
                MatchingProcesses = matching
            };
        }

        return new ProcessDiscoveryResult
        {
            Status = ProcessDiscoveryStatus.MultipleMatches,
            MatchingProcesses = matching,
            ErrorMessage = $"Multiple ({matching.Count}) matching Path of Exile processes found. Please specify target using --pid <pid>."
        };
    }

    public static Process? FindTargetProcess(int? explicitPid = null)
    {
        var result = DiscoverTargetProcess(explicitPid);
        return result.Status == ProcessDiscoveryStatus.Success ? result.SelectedProcess : null;
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
