#if DEBUG
namespace TEHhub.Ui;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

/// <summary>Bounded cached diagnostics shared by core tools and plugins; publishing performs no game reads.</summary>
public static class ToolDiagnostics
{
    private static readonly ConcurrentDictionary<string, ToolDiagnostic> Entries = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    public static void Publish(string id, IReadOnlyDictionary<string, string> values)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 100) throw new ArgumentException("Invalid tool ID.", nameof(id));
        var copy = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in values.Take(64))
            copy[entry.Key[..Math.Min(entry.Key.Length, 100)]] =
                (entry.Value ?? "")[..Math.Min(entry.Value?.Length ?? 0, 2000)];
        lock (Gate)
        {
            if (!Entries.ContainsKey(id) && Entries.Count >= 256) return;
            Entries[id] = new(id, DateTime.UtcNow, copy);
        }
    }

    internal static ToolDiagnostic[] Snapshot() => Entries.Values.OrderBy(x => x.Id).ToArray();
    internal static ToolDiagnostic? Find(string id) => Entries.TryGetValue(id, out var value) ? value : null;
}

public sealed record ToolDiagnostic(string Id, DateTime UpdatedUtc, Dictionary<string, string> Values);

#endif
