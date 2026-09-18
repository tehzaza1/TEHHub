namespace TEHhub.OffsetDoctor.Watch;

using System.Diagnostics;
using TEHhub.OffsetDoctor.Process;
using TEHhub.OffsetDoctor.Recovery;
using TEHhub.OffsetDoctor.Validation;

public sealed class OffsetWatchEngine
{
    private readonly OffsetRecoveryEngine _recoveryEngine = new();

    public WatchReport RunWatch(
        IProcessMemoryReader reader,
        ValidationGroundTruth? groundTruth,
        TimeSpan interval,
        TimeSpan duration,
        string? targetFilter = "all",
        Action<string>? onTransition = null,
        CancellationToken cancellationToken = default)
    {
        var targetInfos = WatchTargetInfo.ResolveTargets(targetFilter);
        var targetStates = targetInfos.Select(t => new WatchTargetState(t)).ToList();
        var targetMap = targetStates.ToDictionary(s => s.NodeId, s => s);

        var transitionLog = new List<string>();
        var startTime = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();

        // Initial scan
        var initialReport = _recoveryEngine.RunValidation(reader, groundTruth);
        foreach (var result in initialReport.Results)
        {
            if (targetMap.TryGetValue(result.NodeId, out var state))
            {
                var msg = state.Update(result, sw.Elapsed);
                if (msg != null)
                {
                    transitionLog.Add(msg);
                    onTransition?.Invoke(msg);
                }
            }
        }

        // Loop ticks
        while (sw.Elapsed < duration && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                var delayMs = (int)interval.TotalMilliseconds;
                if (delayMs > 0)
                {
                    Thread.Sleep(delayMs);
                }
            }
            catch (ThreadInterruptedException)
            {
                break;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var elapsed = sw.Elapsed;
            var report = _recoveryEngine.RunValidation(reader, groundTruth);

            foreach (var result in report.Results)
            {
                if (targetMap.TryGetValue(result.NodeId, out var state))
                {
                    var msg = state.Update(result, elapsed);
                    if (msg != null)
                    {
                        transitionLog.Add(msg);
                        onTransition?.Invoke(msg);
                    }
                }
            }
        }

        sw.Stop();
        var endTime = DateTime.UtcNow;

        var summaries = targetStates.Select(s => new WatchTargetSummary
        {
            NodeId = s.NodeId,
            DisplayName = s.DisplayName,
            Category = s.TargetInfo.Category,
            FirstStatus = s.FirstObservedStatus,
            BestStatus = s.BestObservedStatus,
            LatestStatus = s.LatestObservedStatus,
            ObservedPointerOrValue = s.LastRawValue ?? (s.LastResolvedAddress != IntPtr.Zero ? $"0x{s.LastResolvedAddress.ToInt64():X}" : null),
            Reason = s.LastReason,
            PlayerActionObserved = s.PlayerActionObserved,
            Recommendation = s.Recommendation,
            StrongestEvidence = s.StrongestEvidence
        }).ToList();

        return new WatchReport
        {
            ProcessMetadata = reader.Metadata,
            StartTimeUtc = startTime,
            EndTimeUtc = endTime,
            IntervalMs = (int)interval.TotalMilliseconds,
            TargetFilter = targetFilter ?? "all",
            GroundTruth = groundTruth,
            TargetSummaries = summaries,
            TransitionLog = transitionLog
        };
    }
}
