#if DEBUG
namespace TEHhub.Ui;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using TEHhub.Offsets.Objects.UiElement;

/// <summary>
/// A deliberately small, read-only differential scanner for Expedition research.  This is not a
/// process-wide scanner: it examines bounded windows rooted only at live TEHhub game/UI anchors.
/// A result is a candidate until it survives independent captures; it is never an offset claim.
/// </summary>
internal static class ExpeditionOffsetScanner
{
    private const int MaxPlacedBombs = 16;
    private const int MaxMilliseconds = 100;
    private const int MaxTotalBytes = 1024 * 1024;
    private const int MaxRegions = 512;
    private const int MaxUiNodes = 512;
    private const int MaxUiDepth = 6;
    private const int MaxUiChildren = 256;
    private const int MaxCandidates = 16384;
    private const int StateWindowBytes = 4096;
    private const int UiWindowBytes = 1024;

    private static readonly object StateGate = new();
    private static readonly Dictionary<CandidateKey, CandidateState> Candidates = new();
    private static readonly List<int> Observations = new();
    private static PendingScan? pending;
    private static ExpeditionOffsetScanSnapshot? snapshot;

    internal static ExpeditionOffsetScanSnapshot? Snapshot => Volatile.Read(ref snapshot);

    /// <summary>Discards candidate state before beginning a new, independent observed-value sequence.</summary>
    internal static void Reset()
    {
        lock (StateGate)
        {
            Candidates.Clear();
            Observations.Clear();
            Volatile.Write(ref snapshot, null);
        }
    }

    /// <summary>Called by the render path. No target-process read occurs unless a request is queued.</summary>
    internal static void Collect()
    {
        var request = Interlocked.Exchange(ref pending, null);
        if (request == null) return;
        ExpeditionOffsetScanSnapshot result;
        try { result = Capture(request.ObservedPlacedBombs); }
        catch (Exception error) { result = Empty(request.ObservedPlacedBombs, "scan failed: " + error.GetType().Name); }
        Volatile.Write(ref snapshot, result);
        request.Completion.SetResult(result);
    }

    internal static Task<ExpeditionOffsetScanSnapshot>? Queue(int observedPlacedBombs)
    {
        if (observedPlacedBombs < 0 || observedPlacedBombs > MaxPlacedBombs) return null;
        var completion = new TaskCompletionSource<ExpeditionOffsetScanSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new PendingScan(observedPlacedBombs, completion);
        return Interlocked.CompareExchange(ref pending, request, null) == null ? completion.Task : null;
    }

    private static ExpeditionOffsetScanSnapshot Capture(int expected)
    {
        var game = Core.States.InGameStateObject;
        if (game == null || game.Address == IntPtr.Zero) return Empty(expected, "in-game state unavailable");

        var timer = Stopwatch.StartNew();
        var regions = BuildRegions(game, timer, out var discoveryTruncated);
        var bytesScanned = 0;
        var coverageTruncated = discoveryTruncated;
        var values = new Dictionary<CandidateKey, int>();
        foreach (var region in regions)
        {
            if (timer.ElapsedMilliseconds >= MaxMilliseconds || bytesScanned + region.Bytes > MaxTotalBytes)
            {
                coverageTruncated = true;
                break;
            }

            var buffer = new byte[region.Bytes];
            if (!Core.Process.Handle.TryReadMemoryArray(region.BaseAddress, buffer, out _)) continue;
            bytesScanned += buffer.Length;
            for (var offset = 0; offset <= buffer.Length - sizeof(int); offset += sizeof(int))
            {
                if (BitConverter.ToInt32(buffer, offset) != expected) continue;
                var key = new CandidateKey(region.Kind, region.BaseAddress.ToInt64(), offset);
                values[key] = expected;
            }
        }

        lock (StateGate)
        {
            if (Observations.Count == 0)
            {
                var limited = 0;
                foreach (var value in values)
                {
                    if (limited++ >= MaxCandidates) { coverageTruncated = true; break; }
                    Candidates[value.Key] = new CandidateState(new List<int> { value.Value });
                }
            }
            else
            {
                var remove = new List<CandidateKey>();
                foreach (var candidate in Candidates)
                {
                    if (values.TryGetValue(candidate.Key, out var value) && value == expected)
                        candidate.Value.Values.Add(value);
                    else
                        remove.Add(candidate.Key);
                }
                foreach (var key in remove) Candidates.Remove(key);
            }
            Observations.Add(expected);
            var output = new List<ExpeditionOffsetCandidate>(Candidates.Count);
            foreach (var candidate in Candidates)
                output.Add(new ExpeditionOffsetCandidate("candidate", candidate.Key.Kind,
                    "0x" + candidate.Key.BaseAddress.ToString("X"), candidate.Key.Offset,
                    "0x" + (candidate.Key.BaseAddress + candidate.Key.Offset).ToString("X"), candidate.Value.Values.ToArray()));
            return new ExpeditionOffsetScanSnapshot(DateTime.UtcNow, "completed", expected, Observations.ToArray(),
                regions.Count, bytesScanned, coverageTruncated, output);
        }
    }

    private static List<ScanRegion> BuildRegions(TEHhub.RemoteObjects.States.InGameState game, Stopwatch timer, out bool truncated)
    {
        var result = new List<ScanRegion>();
        var bases = new HashSet<IntPtr>();
        truncated = false;
        Add("in-game-state", game.Address, StateWindowBytes);
        Add("game-ui-manager", game.GameUi.Address, StateWindowBytes);
        if (game.UiRootAddress != IntPtr.Zero) Add("ui-root", game.UiRootAddress, UiWindowBytes);

        if (game.CurrentAreaInstance != null)
        {
            Add("area-instance", game.CurrentAreaInstance.Address, StateWindowBytes);
            foreach (var entity in game.CurrentAreaInstance.AwakeEntities.Values)
            {
                if (entity != null && entity.Path != null &&
                    (entity.Path.Contains("Detonator", StringComparison.OrdinalIgnoreCase) ||
                     entity.Path.Contains("Explosive", StringComparison.OrdinalIgnoreCase) ||
                     entity.Path.Contains("Placement", StringComparison.OrdinalIgnoreCase)))
                {
                    var segs = entity.Path.Split('/');
                    var entityName = segs.Length > 0 ? segs[^1] : "expedition";
                    Add("entity-" + entityName, entity.Address, StateWindowBytes);
                    foreach (var pair in entity.GetComponentAddressPairs())
                    {
                        if (pair.Value != IntPtr.Zero) Add("comp-" + pair.Key, pair.Value, 512);
                    }
                }
            }
        }

        var queue = new Queue<(IntPtr Address, int Depth)>();
        var rootUi = game.GameUi.Address != IntPtr.Zero ? game.GameUi.Address : game.UiRootAddress;
        if (rootUi != IntPtr.Zero) queue.Enqueue((rootUi, 0));
        var visited = new HashSet<IntPtr>();
        while (queue.Count > 0)
        {
            if (timer.ElapsedMilliseconds >= MaxMilliseconds || visited.Count >= MaxUiNodes || result.Count >= MaxRegions)
            {
                truncated = true;
                break;
            }
            var current = queue.Dequeue();
            if (current.Address == IntPtr.Zero || !visited.Add(current.Address)) continue;
            if (!Core.Process.Handle.TryReadMemory<UiElementBaseOffset>(current.Address, out var element) || element.Self != current.Address) continue;
            Add("ui-element", current.Address, UiWindowBytes);
            var count = element.ChildrensPtr.TotalElements(IntPtr.Size);
            if (current.Depth >= MaxUiDepth || count <= 0 || element.ChildrensPtr.First == IntPtr.Zero) continue;
            var takeCount = (int)Math.Min(count, MaxUiChildren);
            var children = new IntPtr[takeCount];
            if (!Core.Process.Handle.TryReadMemoryArray(element.ChildrensPtr.First, children, out _)) continue;
            foreach (var child in children) queue.Enqueue((child, current.Depth + 1));
        }
        return result;

        void Add(string kind, IntPtr address, int bytes)
        {
            if (address == IntPtr.Zero || !bases.Add(address) || result.Count >= MaxRegions) return;
            result.Add(new ScanRegion(kind, address, bytes));
        }
    }

    private static ExpeditionOffsetScanSnapshot Empty(int expected, string status) => new(DateTime.UtcNow, status, expected,
        Array.Empty<int>(), 0, 0, false, new List<ExpeditionOffsetCandidate>());

    private sealed record ScanRegion(string Kind, IntPtr BaseAddress, int Bytes);
    private sealed record CandidateKey(string Kind, long BaseAddress, int Offset);
    private sealed record CandidateState(List<int> Values);
    private sealed record PendingScan(int ObservedPlacedBombs, TaskCompletionSource<ExpeditionOffsetScanSnapshot> Completion);
}

internal sealed record ExpeditionOffsetScanSnapshot(DateTime Utc, string Status, int ObservedPlacedBombs,
    int[] ObservationSequence, int RegionsScanned, int BytesScanned, bool CoverageTruncated,
    List<ExpeditionOffsetCandidate> Candidates);

internal sealed record ExpeditionOffsetCandidate(string Status, string Structure, string BaseAddress, int Offset,
    string Address, int[] ValueSequence);
#endif
