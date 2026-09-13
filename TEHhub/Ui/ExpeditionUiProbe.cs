#if DEBUG
namespace TEHhub.Ui;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using TEHhub.Offsets.Objects.UiElement;

/// <summary>
/// On-demand, bounded UI-tree evidence capture for Expedition research.
/// It only follows the game's declared UiElement children vector and is collected on the render thread.
/// </summary>
internal static class ExpeditionUiProbe
{
    private const int MaxNodes = 4096;
    private const int MaxDepth = 12;
    private const int MaxMilliseconds = 150;
    private const int MaxChildrenPerNode = 512;

    private static TaskCompletionSource<ExpeditionUiSnapshot>? pending;
    private static ExpeditionUiSnapshot? snapshot;

    /// <summary>Queues one capture, or returns null when a capture is already pending.</summary>
    internal static Task<ExpeditionUiSnapshot>? Request()
    {
        var request = new TaskCompletionSource<ExpeditionUiSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        return Interlocked.CompareExchange(ref pending, request, null) == null ? request.Task : null;
    }

    /// <summary>Returns the most recently completed capture.</summary>
    internal static ExpeditionUiSnapshot? Snapshot => Volatile.Read(ref snapshot);

    /// <summary>Called from the render-frame path. It performs no reads without a queued request.</summary>
    internal static void Collect()
    {
        var request = Interlocked.Exchange(ref pending, null);
        if (request == null) return;

        ExpeditionUiSnapshot result;
        try { result = Capture(); }
        catch (Exception error)
        {
            result = new ExpeditionUiSnapshot(DateTime.UtcNow, "capture failed: " + error.GetType().Name, 0, false,
                new List<ExpeditionUiNode>());
        }

        Volatile.Write(ref snapshot, result);
        request.SetResult(result);
    }

    private static ExpeditionUiSnapshot Capture()
    {
        var root = Core.States.InGameStateObject.GameUi.Address;
        if (root == IntPtr.Zero)
            return new ExpeditionUiSnapshot(DateTime.UtcNow, "game UI unavailable", 0, false, new List<ExpeditionUiNode>());

        var reader = Core.Process.Handle;
        var timer = Stopwatch.StartNew();
        var queue = new Queue<PendingNode>();
        var visited = new HashSet<IntPtr>();
        var scanned = new List<CapturedNode>();
        var truncated = false;
        queue.Enqueue(new PendingNode(root, "root", null, 0));

        while (queue.Count > 0)
        {
            if (scanned.Count >= MaxNodes || timer.ElapsedMilliseconds >= MaxMilliseconds)
            {
                truncated = true;
                break;
            }

            var current = queue.Dequeue();
            if (current.Address == IntPtr.Zero || !visited.Add(current.Address)) continue;
            if (!reader.TryReadMemory<UiElementBaseOffset>(current.Address, out var data) || data.Self != current.Address) continue;

            var visible = (data.Flags & 0x800) != 0;
            scanned.Add(new CapturedNode(
                new ExpeditionUiNode(
                    current.Path,
                    current.ParentPath,
                    "0x" + current.Address.ToInt64().ToString("X"),
                    data.Flags,
                    visible,
                    new ExpeditionUiVector(data.RelativePosition.X, data.RelativePosition.Y),
                    new ExpeditionUiVector(data.UnscaledSize.X, data.UnscaledSize.Y),
                    ReadSaneStringId(data)),
                current.ParentPath));

            var childCount = data.ChildrensPtr.TotalElements(IntPtr.Size);
            if (current.Depth >= MaxDepth || childCount <= 0) continue;
            if (childCount > MaxChildrenPerNode || data.ChildrensPtr.First == IntPtr.Zero)
            {
                truncated = true;
                continue;
            }

            var children = new IntPtr[(int)childCount];
            if (!reader.TryReadMemoryArray(data.ChildrensPtr.First, children, out _)) continue;
            for (var index = 0; index < children.Length; index++)
                queue.Enqueue(new PendingNode(children[index], current.Path + "." + index, current.Path, current.Depth + 1));
        }

        var retainedPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in scanned)
        {
            if (!node.Value.Visible) continue;
            for (var path = node.Value.Path; path != null; path = FindParentPath(scanned, path))
                retainedPaths.Add(path);
        }

        var output = new List<ExpeditionUiNode>();
        foreach (var node in scanned)
            if (retainedPaths.Contains(node.Value.Path)) output.Add(node.Value);

        return new ExpeditionUiSnapshot(DateTime.UtcNow, "completed", scanned.Count, truncated, output);
    }

    private static string? FindParentPath(List<CapturedNode> nodes, string path)
    {
        foreach (var node in nodes)
            if (string.Equals(node.Value.Path, path, StringComparison.Ordinal)) return node.ParentPath;
        return null;
    }

    private static string? ReadSaneStringId(UiElementBaseOffset data)
    {
        if (data.StringIdPtr.Length <= 0 || data.StringIdPtr.Length > 128 ||
            data.StringIdPtr.Capacity < data.StringIdPtr.Length || data.StringIdPtr.Capacity > 4096)
            return null;
        try { return Core.Process.Handle.ReadStdWString(data.StringIdPtr); }
        catch { return null; }
    }

    private sealed record PendingNode(IntPtr Address, string Path, string? ParentPath, int Depth);
    private sealed record CapturedNode(ExpeditionUiNode Value, string? ParentPath);
}

/// <summary>JSON-friendly result of one bounded Expedition UI-tree capture.</summary>
internal sealed record ExpeditionUiSnapshot(DateTime Utc, string Status, int ScannedNodes, bool Truncated,
    List<ExpeditionUiNode> Nodes);

/// <summary>A visible UI node, or an ancestor retained to show its position in the tree.</summary>
internal sealed record ExpeditionUiNode(string Path, string? ParentPath, string Address, uint Flags, bool Visible,
    ExpeditionUiVector LocalPosition, ExpeditionUiVector LocalSize, string? StringId);

/// <summary>Unscaled local UI coordinates as stored by the game.</summary>
internal sealed record ExpeditionUiVector(float X, float Y);
#endif
