#if DEBUG
namespace TEHhub.Ui;

using System;
using System.Threading;
using System.Threading.Tasks;
using TEHhub.RemoteObjects.Components;

/// <summary>
/// On-demand, read-only capture of one live Expedition Rune Station by entity ID.
/// The request is collected on the render thread so API callers never read game memory directly.
/// </summary>
internal static class RuneStationProbe
{
    private static ProbeRequest? pending;
    private static RuneStationProbeSnapshot? snapshot;

    internal static Task<RuneStationProbeSnapshot>? Request(uint entityId)
    {
        var request = new ProbeRequest(entityId);
        return Interlocked.CompareExchange(ref pending, request, null) == null ? request.Completion.Task : null;
    }

    internal static RuneStationProbeSnapshot? Snapshot => Volatile.Read(ref snapshot);

    internal static void Collect()
    {
        var request = Interlocked.Exchange(ref pending, null);
        if (request == null) return;

        RuneStationProbeSnapshot result;
        try
        {
            var area = Core.States.InGameStateObject?.CurrentAreaInstance;
            if (area == null || area.Address == IntPtr.Zero)
            {
                result = new RuneStationProbeSnapshot(DateTime.UtcNow, request.EntityId, "no active area", string.Empty, string.Empty);
            }
            else
            {
                var entity = default(TEHhub.RemoteObjects.States.InGameStateObjects.Entity);
                foreach (var candidate in area.AwakeEntities.Values)
                {
                    if (candidate != null && candidate.Id == request.EntityId)
                    {
                        entity = candidate;
                        break;
                    }
                }

                if (entity == null)
                {
                    result = new RuneStationProbeSnapshot(DateTime.UtcNow, request.EntityId, "entity is not awake in this area", string.Empty, string.Empty);
                }
                else if (!entity.TryGetComponent<StateMachine>(out var stateMachine, shouldCache: false))
                {
                    result = new RuneStationProbeSnapshot(DateTime.UtcNow, request.EntityId, "entity has no StateMachine", entity.Path, string.Empty);
                }
                else
                {
                    result = new RuneStationProbeSnapshot(
                        DateTime.UtcNow,
                        request.EntityId,
                        "completed",
                        entity.Path,
                        stateMachine.CaptureRuneStationDiagnostic());
                }
            }
        }
        catch (Exception ex)
        {
            result = new RuneStationProbeSnapshot(DateTime.UtcNow, request.EntityId, "capture failed: " + ex.GetType().Name, string.Empty, string.Empty);
        }

        Volatile.Write(ref snapshot, result);
        request.Completion.SetResult(result);
    }

    private sealed class ProbeRequest(uint entityId)
    {
        internal uint EntityId { get; } = entityId;
        internal TaskCompletionSource<RuneStationProbeSnapshot> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

internal sealed record RuneStationProbeSnapshot(DateTime Utc, uint EntityId, string Status, string Path, string Diagnostic);
#endif
