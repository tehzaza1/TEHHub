// <copyright file="MemoryReadDiagnostics.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace GameHelper.Ui;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using Coroutine;
using CoroutineEvents;
using ImGuiNET;

/// <summary>
///     Diagnostic window that aggregates failed process-memory reads so a human can tell
///     whether they are transient torn reads (the game mutating data while we read it) or a
///     real offset/struct-layout breakage.
///     <para>
///     The decisive signal is per-address recurrence: a torn read lands on whatever happened
///     to be in freed/reallocated memory, so failures spread across <b>many distinct</b>
///     addresses each seen only once or twice. A wrong offset reads the <b>same</b> bad field
///     every frame, so a small number of addresses rack up huge repeat counts. Compare the
///     "Unique" and "Max/Addr" columns: high Unique + low Max/Addr ⇒ races; low Unique + high
///     Max/Addr ⇒ likely structural.
///     </para>
///     Recording is gated on <see cref="Settings.State.ShowMemoryDiagnostics"/> and costs
///     nothing (no stack walk, no bookkeeping) when the window is closed.
/// </summary>
public static class MemoryReadDiagnostics
{
    private const int MaxTrackedAddressesPerKey = 1024;
    private static readonly ConcurrentDictionary<string, FailureStat> Stats = new();
    private static readonly ConcurrentDictionary<string, ReadRegionStat> ReadRegions = new();
    private static readonly AsyncLocal<ReadRegionContext?> CurrentReadRegion = new();

    private static DateTime lastUpdate = DateTime.MinValue;
    private static List<DiagnosticRow> cachedRows = [];
    private static string lastActionMessage = string.Empty;
    private static long totalReadCalls;
    private static long totalReadBytes;
    private static long totalReadTicks;
    private static long totalReadFailures;
    private static long scalarReadCalls;
    private static long bufferReadCalls;
    private static long totalFrames;
    private static long firstReadTimestamp;
    private static long previousReadCalls;
    private static long previousReadBytes;
    private static long previousReadTicks;
    private static long previousReadFailures;
    private static long previousRateTimestamp = Stopwatch.GetTimestamp();
    private static ReadRateSnapshot cachedReadRate;

    /// <summary>
    ///     Starts the window render coroutine.
    /// </summary>
    internal static void InitializeCoroutines()
    {
        CoroutineHandler.Start(RenderWindow());
    }

    /// <summary>
    ///     Records a failed memory read. Callers must check
    ///     <see cref="Settings.State.ShowMemoryDiagnostics"/> before computing the (relatively
    ///     expensive) caller string, so this method assumes recording is wanted.
    /// </summary>
    /// <param name="typeName">name of the type that failed to read.</param>
    /// <param name="caller">"Assembly!Type.Method" of the originating call site.</param>
    /// <param name="address">the address that failed to read.</param>
    public static void RecordFailure(string typeName, string caller, long address)
    {
        var key = $"{caller}  ({typeName})";
        var stat = Stats.GetOrAdd(key, _ => new FailureStat());
        Interlocked.Increment(ref stat.Total);
        stat.LastTicks = Environment.TickCount64;

        // Bound memory: once we've seen enough distinct addresses, only keep counting the
        // ones we already track. The capped set is still plenty to expose recurrence.
        if (stat.Addresses.Count < MaxTrackedAddressesPerKey || stat.Addresses.ContainsKey(address))
        {
            stat.Addresses.AddOrUpdate(address, 1, (_, c) => c + 1);
        }
        else
        {
            Interlocked.Increment(ref stat.UntrackedAddressHits);
        }
    }

    /// <summary>
    ///     Records the cost of one native ReadProcessMemory call while diagnostics are enabled.
    ///     This deliberately avoids caller discovery so successful hot-path reads remain cheap
    ///     enough to measure without substantially changing the workload.
    /// </summary>
    internal static void RecordRead(MemoryReadKind kind, long requestedBytes, long elapsedTicks, bool succeeded)
    {
        if (Volatile.Read(ref firstReadTimestamp) == 0)
        {
            Interlocked.CompareExchange(ref firstReadTimestamp, Stopwatch.GetTimestamp(), 0);
        }

        Interlocked.Increment(ref totalReadCalls);
        Interlocked.Add(ref totalReadBytes, requestedBytes);
        Interlocked.Add(ref totalReadTicks, elapsedTicks);
        if (!succeeded)
        {
            Interlocked.Increment(ref totalReadFailures);
        }

        if (kind == MemoryReadKind.Scalar)
        {
            Interlocked.Increment(ref scalarReadCalls);
        }
        else
        {
            Interlocked.Increment(ref bufferReadCalls);
        }

        // Attribute the read to the current logical operation. AsyncLocal flows into the
        // Parallel/Task workers used by entity and UI traversal, unlike a global before/after
        // counter which also captured unrelated work running at the same time.
        for (var region = CurrentReadRegion.Value; region != null; region = region.Parent)
        {
            Interlocked.Increment(ref region.ReadCalls);
            Interlocked.Add(ref region.RequestedBytes, requestedBytes);
        }
    }

    /// <summary>
    ///     Records one completed overlay frame while memory diagnostics are enabled.
    /// </summary>
    internal static void RecordFrame()
    {
        if (Core.GHSettings.ShowMemoryDiagnostics)
        {
            Interlocked.Increment(ref totalFrames);
        }
    }

    /// <summary>
    ///     Measures how many process-memory calls and requested bytes happen inside a
    ///     high-level operation. Unlike caller stack walking, this adds bookkeeping only
    ///     once at region entry and exit rather than once per native read.
    /// </summary>
    public static MemoryReadRegionScope MeasureRegion(string name)
    {
        if (!Core.GHSettings.ShowMemoryDiagnostics)
        {
            return default;
        }

        var context = new ReadRegionContext(name, CurrentReadRegion.Value);
        CurrentReadRegion.Value = context;
        return new MemoryReadRegionScope(context);
    }

    internal static void CompleteRegion(ReadRegionContext context)
    {
        if (ReferenceEquals(CurrentReadRegion.Value, context))
        {
            CurrentReadRegion.Value = context.Parent;
        }

        var stat = ReadRegions.GetOrAdd(context.Name, static _ => new ReadRegionStat());
        Interlocked.Increment(ref stat.Invocations);
        Interlocked.Add(ref stat.ReadCalls, Interlocked.Read(ref context.ReadCalls));
        Interlocked.Add(ref stat.RequestedBytes, Interlocked.Read(ref context.RequestedBytes));
    }

    private static IEnumerator<Wait> RenderWindow()
    {
        while (true)
        {
            yield return new Wait(GameHelperEvents.OnPostRender);
            if (!Core.GHSettings.ShowMemoryDiagnostics)
            {
                continue;
            }

            ImGui.SetNextWindowSize(new Vector2(900, 500), ImGuiCond.FirstUseEver);
            if (ImGui.Begin("Memory Read Diagnostics", ref Core.GHSettings.ShowMemoryDiagnostics, ImGuiWindowFlags.MenuBar))
            {
                RefreshRowsThrottled();

                if (ImGui.BeginMenuBar())
                {
                    if (ImGui.MenuItem("Reset"))
                    {
                        Stats.Clear();
                        ReadRegions.Clear();
                        ResetReadMetrics();
                        cachedRows = [];
                        lastActionMessage = string.Empty;
                    }

                    if (ImGui.MenuItem("Copy to Clipboard"))
                    {
                        CopyReportToClipboard();
                    }

                    if (ImGui.MenuItem("Dump to File"))
                    {
                        DumpReportToFile();
                    }

                    ImGui.EndMenuBar();
                }

                ImGui.TextWrapped(
                    "Aggregates failed memory reads (including the ones normally silenced). " +
                    "Many Unique addresses each with low Max/Addr => transient torn reads (races). " +
                    "Few Unique addresses with high Max/Addr (same address failing every frame) => likely wrong offset/struct.");
                ImGui.Separator();

                long grandTotal = cachedRows.Sum(r => r.Total);
                ImGui.Text($"Distinct call sites: {cachedRows.Count}    Total failed reads: {grandTotal}");
                if (!string.IsNullOrEmpty(lastActionMessage))
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled($"|  {lastActionMessage}");
                }

                ImGui.Text(
                    $"Recent: {cachedReadRate.CallsPerSecond:N0} calls/s  |  " +
                    $"{cachedReadRate.MebibytesPerSecond:F2} MiB/s  |  " +
                    $"{cachedReadRate.MicrosecondsPerCall:F2} us/call  |  " +
                    $"fail {cachedReadRate.FailuresPerSecond:N0}/s");
                ImGui.Text(
                    $"Session ({cachedReadRate.SessionSeconds:F1}s): {cachedReadRate.TotalCalls:N0} calls  |  " +
                    $"{cachedReadRate.AverageCallsPerSecond:N0} calls/s  |  " +
                    $"{cachedReadRate.AverageMebibytesPerSecond:F2} MiB/s  |  " +
                    $"{cachedReadRate.AverageMicrosecondsPerCall:F2} us/call  |  " +
                    $"fail {cachedReadRate.TotalFailures:N0}");
                ImGui.Text(
                    $"Frames: {cachedReadRate.TotalFrames:N0}  |  " +
                    $"{cachedReadRate.AverageFramesPerSecond:F1} frames/s  |  " +
                    $"{cachedReadRate.AverageCallsPerFrame:N0} reads/frame");
                ImGui.TextDisabled(
                    $"Breakdown: Scalar {cachedReadRate.ScalarCalls:N0}    Buffer/array {cachedReadRate.BufferCalls:N0}    " +
                    $"Requested: {cachedReadRate.TotalMebibytes:F2} MiB");

                if (ImGui.BeginTable("memDiagTable", 6,
                        ImGuiTableFlags.Sortable | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Borders |
                        ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable,
                        ImGui.GetContentRegionAvail()))
                {
                    ImGui.TableSetupColumn("Caller (Type)", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn("Total", ImGuiTableColumnFlags.WidthFixed, 70);
                    ImGui.TableSetupColumn("Unique", ImGuiTableColumnFlags.WidthFixed, 70);
                    ImGui.TableSetupColumn("Max/Addr", ImGuiTableColumnFlags.WidthFixed, 80);
                    ImGui.TableSetupColumn("Last seen", ImGuiTableColumnFlags.WidthFixed, 90);
                    ImGui.TableSetupColumn("Verdict", ImGuiTableColumnFlags.WidthFixed, 140);

                    ImGui.TableSetupScrollFreeze(0, 1);
                    ImGui.TableHeadersRow();

                    var sorted = SortRows(cachedRows);
                    foreach (var row in sorted)
                    {
                        ImGui.TableNextRow();

                        ImGui.TableNextColumn();
                        ImGui.Text(row.Name);
                        if (ImGui.IsItemHovered() && row.TopAddresses.Count > 0)
                        {
                            ImGui.BeginTooltip();
                            ImGui.Text("Top failing addresses (count) — full list in Copy/Dump:");
                            foreach (var (addr, cnt) in row.TopAddresses.Take(15))
                            {
                                ImGui.Text($"  0x{addr:X}  ×{cnt}{DecodeHint(addr)}");
                            }

                            if (row.TopAddresses.Count > 15)
                            {
                                ImGui.Text($"  … {row.TopAddresses.Count - 15} more");
                            }

                            if (row.UntrackedHits > 0)
                            {
                                ImGui.Text($"  (+{row.UntrackedHits} on untracked addresses)");
                            }

                            ImGui.EndTooltip();
                        }

                        ImGui.TableNextColumn();
                        ImGui.Text(row.Total.ToString());

                        ImGui.TableNextColumn();
                        ImGui.Text(row.UniqueAddresses.ToString());

                        ImGui.TableNextColumn();
                        ImGui.Text(row.MaxPerAddress.ToString());

                        ImGui.TableNextColumn();
                        ImGui.Text(row.SecondsSinceLast < 1 ? "now" : $"{row.SecondsSinceLast:F0}s ago");

                        ImGui.TableNextColumn();
                        ImGui.TextColored(row.VerdictColor, row.Verdict);
                    }

                    ImGui.EndTable();
                }
            }

            ImGui.End();
        }
    }

    private static void RefreshRowsThrottled()
    {
        var now = DateTime.Now;
        if ((now - lastUpdate).TotalMilliseconds < 500)
        {
            return;
        }

        lastUpdate = now;
        RefreshReadRateSnapshot();
        var nowTicks = Environment.TickCount64;
        var rows = new List<DiagnosticRow>(Stats.Count);
        foreach (var kvp in Stats.ToArray())
        {
            var stat = kvp.Value;
            var snapshot = stat.Addresses.ToArray();
            var unique = snapshot.Length;
            var maxPerAddress = unique > 0 ? snapshot.Max(a => a.Value) : 0;
            var top = snapshot
                .OrderByDescending(a => a.Value)
                .Take(64)
                .Select(a => (a.Key, a.Value))
                .ToList();

            rows.Add(new DiagnosticRow(
                kvp.Key,
                Interlocked.Read(ref stat.Total),
                unique,
                maxPerAddress,
                Interlocked.Read(ref stat.UntrackedAddressHits),
                Math.Max(0, (nowTicks - stat.LastTicks) / 1000.0),
                top));
        }

        cachedRows = rows;
    }

    private static void RefreshReadRateSnapshot()
    {
        var nowTimestamp = Stopwatch.GetTimestamp();
        var elapsedSeconds = (nowTimestamp - previousRateTimestamp) / (double)Stopwatch.Frequency;
        if (elapsedSeconds <= 0)
        {
            return;
        }

        var calls = Interlocked.Read(ref totalReadCalls);
        var bytes = Interlocked.Read(ref totalReadBytes);
        var ticks = Interlocked.Read(ref totalReadTicks);
        var failures = Interlocked.Read(ref totalReadFailures);
        var frames = Interlocked.Read(ref totalFrames);
        var callsDelta = calls - previousReadCalls;
        var bytesDelta = bytes - previousReadBytes;
        var ticksDelta = ticks - previousReadTicks;
        var failuresDelta = failures - previousReadFailures;
        var firstTimestamp = Volatile.Read(ref firstReadTimestamp);
        var sessionSeconds = firstTimestamp == 0
            ? 0
            : Math.Max(0, (nowTimestamp - firstTimestamp) / (double)Stopwatch.Frequency);

        cachedReadRate = new ReadRateSnapshot(
            calls,
            Interlocked.Read(ref scalarReadCalls),
            Interlocked.Read(ref bufferReadCalls),
            bytes / 1048576.0,
            callsDelta / elapsedSeconds,
            bytesDelta / 1048576.0 / elapsedSeconds,
            callsDelta > 0 ? ticksDelta * 1_000_000.0 / Stopwatch.Frequency / callsDelta : 0,
            failuresDelta / elapsedSeconds,
            sessionSeconds,
            sessionSeconds > 0 ? calls / sessionSeconds : 0,
            sessionSeconds > 0 ? bytes / 1048576.0 / sessionSeconds : 0,
            calls > 0 ? ticks * 1_000_000.0 / Stopwatch.Frequency / calls : 0,
            failures,
            frames,
            sessionSeconds > 0 ? frames / sessionSeconds : 0,
            frames > 0 ? (double)calls / frames : 0);

        previousReadCalls = calls;
        previousReadBytes = bytes;
        previousReadTicks = ticks;
        previousReadFailures = failures;
        previousRateTimestamp = nowTimestamp;
    }

    private static void ResetReadMetrics()
    {
        Interlocked.Exchange(ref totalReadCalls, 0);
        Interlocked.Exchange(ref totalReadBytes, 0);
        Interlocked.Exchange(ref totalReadTicks, 0);
        Interlocked.Exchange(ref totalReadFailures, 0);
        Interlocked.Exchange(ref scalarReadCalls, 0);
        Interlocked.Exchange(ref bufferReadCalls, 0);
        Interlocked.Exchange(ref totalFrames, 0);
        Interlocked.Exchange(ref firstReadTimestamp, 0);
        previousReadCalls = 0;
        previousReadBytes = 0;
        previousReadTicks = 0;
        previousReadFailures = 0;
        previousRateTimestamp = Stopwatch.GetTimestamp();
        cachedReadRate = default;
    }

    /// <summary>
    ///     Builds a tab-separated, shareable text snapshot of the current table.
    /// </summary>
    private static string BuildReport()
    {
        var rows = cachedRows.OrderByDescending(r => r.Total).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"# Memory Read Diagnostics — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(
            $"# Recent: " +
            $"{cachedReadRate.CallsPerSecond:F0} calls/s, " +
            $"{cachedReadRate.MebibytesPerSecond:F2} MiB/s, " +
            $"{cachedReadRate.MicrosecondsPerCall:F2} us/call, " +
            $"{cachedReadRate.FailuresPerSecond:F0} failures/s");
        sb.AppendLine(
            $"# Session ({cachedReadRate.SessionSeconds:F1}s): {cachedReadRate.TotalCalls} calls, " +
            $"{cachedReadRate.AverageCallsPerSecond:F0} calls/s, " +
            $"{cachedReadRate.AverageMebibytesPerSecond:F2} MiB/s, " +
            $"{cachedReadRate.AverageMicrosecondsPerCall:F2} us/call, " +
            $"{cachedReadRate.TotalFailures} failures");
        sb.AppendLine(
            $"# Frames: {cachedReadRate.TotalFrames}, " +
            $"{cachedReadRate.AverageFramesPerSecond:F1} frames/s, " +
            $"{cachedReadRate.AverageCallsPerFrame:F0} reads/frame");
        sb.AppendLine(
            $"# Scalar calls: {cachedReadRate.ScalarCalls}, Buffer/array calls: {cachedReadRate.BufferCalls}, " +
            $"Total requested: {cachedReadRate.TotalMebibytes:F2} MiB");
        sb.AppendLine("# Read regions (regions can overlap):");
        sb.AppendLine("# Region\tInvocations\tTotalReads\tAvgReads/Invocation\tRequestedMiB");
        foreach (var entry in ReadRegions.OrderByDescending(static entry => Interlocked.Read(ref entry.Value.ReadCalls)))
        {
            var invocations = Interlocked.Read(ref entry.Value.Invocations);
            var calls = Interlocked.Read(ref entry.Value.ReadCalls);
            var bytes = Interlocked.Read(ref entry.Value.RequestedBytes);
            var averageCalls = invocations > 0 ? (double)calls / invocations : 0;
            sb.AppendLine($"# {entry.Key}\t{invocations}\t{calls}\t{averageCalls:F1}\t{bytes / 1048576.0:F2}");
        }

        sb.AppendLine($"# Distinct call sites: {rows.Count}, Total failed reads: {rows.Sum(r => r.Total)}");
        sb.AppendLine("# Verdict guide: high Unique + low Max/Addr => races; low Unique + high Max/Addr => likely structural.");
        sb.AppendLine("Total\tUnique\tMax/Addr\tVerdict\tLastSeen(s)\tCaller(Type)\tTopAddresses");
        foreach (var r in rows)
        {
            // Compact inline list (first 8) keeps the table row scannable; full list is below.
            var top = string.Join(" ", r.TopAddresses.Take(8).Select(a => $"0x{a.Addr:X}(x{a.Count})"));
            if (r.UntrackedHits > 0)
            {
                top += $" (+{r.UntrackedHits} untracked)";
            }

            sb.AppendLine($"{r.Total}\t{r.UniqueAddresses}\t{r.MaxPerAddress}\t{r.Verdict}\t{r.SecondsSinceLast:F0}\t{r.Name}\t{top}");
        }

        sb.AppendLine();
        sb.AppendLine("# ===== Top failing addresses per call site (value decoded as string/float when printable) =====");
        foreach (var r in rows)
        {
            sb.AppendLine();
            sb.AppendLine($"## {r.Name}  —  total {r.Total}, unique {r.UniqueAddresses}, max/addr {r.MaxPerAddress}, verdict {r.Verdict}");
            foreach (var a in r.TopAddresses)
            {
                sb.AppendLine($"  0x{a.Addr:X}\tx{a.Count}{DecodeHint(a.Addr)}");
            }

            if (r.UntrackedHits > 0)
            {
                sb.AppendLine($"  (+{r.UntrackedHits} hits on addresses beyond the {64}-address tracking cap)");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Renders a failing value's plausible interpretations (UTF-16 / ASCII text, or a
    ///     float/double) so a human can recognise it as data rather than a pointer. Returns an
    ///     empty string when nothing decodes cleanly.
    /// </summary>
    /// <param name="value">the raw 64-bit value that was (mis)used as an address.</param>
    /// <returns>a short " [hint]" suffix, or empty.</returns>
    private static string DecodeHint(long value)
    {
        var bytes = BitConverter.GetBytes(value);
        var hints = new List<string>();

        var utf16 = TryDecodeText(bytes, unicode: true);
        if (utf16 != null)
        {
            hints.Add($"utf16:\"{utf16}\"");
        }

        var ascii = TryDecodeText(bytes, unicode: false);
        if (ascii != null)
        {
            hints.Add($"ascii:\"{ascii}\"");
        }

        var f32 = BitConverter.ToSingle(bytes, 0);
        if (IsCleanFloat(f32))
        {
            hints.Add($"f32:{f32:g6}");
        }

        var f64 = BitConverter.Int64BitsToDouble(value);
        if (IsCleanFloat(f64))
        {
            hints.Add($"f64:{f64:g6}");
        }

        return hints.Count > 0 ? "  [" + string.Join(" ", hints) + "]" : string.Empty;
    }

    private static string? TryDecodeText(byte[] bytes, bool unicode)
    {
        var step = unicode ? 2 : 1;
        var chars = new List<char>();
        for (var i = 0; i + step - 1 < bytes.Length; i += step)
        {
            if (unicode && bytes[i + 1] != 0)
            {
                return null;
            }

            var b = bytes[i];
            if (b == 0)
            {
                break;
            }

            if (b < 0x20 || b > 0x7E)
            {
                return null;
            }

            chars.Add((char)b);
        }

        return chars.Count >= 2 ? new string(chars.ToArray()) : null;
    }

    private static bool IsCleanFloat(double f)
    {
        if (double.IsNaN(f) || double.IsInfinity(f) || f == 0)
        {
            return false;
        }

        var abs = Math.Abs(f);
        return abs is >= 1e-3 and <= 1e9;
    }

    private static void CopyReportToClipboard()
    {
        if (cachedRows.Count == 0 && cachedReadRate.TotalCalls == 0)
        {
            lastActionMessage = "Nothing to copy.";
            return;
        }

        try
        {
            ImGui.SetClipboardText(BuildReport());
            lastActionMessage = "Copied to clipboard.";
        }
        catch (Exception ex)
        {
            lastActionMessage = $"Copy failed: {ex.Message}";
        }
    }

    private static void DumpReportToFile()
    {
        if (cachedRows.Count == 0 && cachedReadRate.TotalCalls == 0)
        {
            lastActionMessage = "Nothing to dump.";
            return;
        }

        try
        {
            var fileName = $"memory_diagnostics_{DateTime.Now:yyyyMMdd_HHmmss}.tsv";
            var path = Path.Combine(AppContext.BaseDirectory, fileName);
            File.WriteAllText(path, BuildReport());
            lastActionMessage = $"Saved: {path}";
            Console.WriteLine($"[MemoryReadDiagnostics] Dumped table to {path}");
        }
        catch (Exception ex)
        {
            lastActionMessage = $"Save failed: {ex.Message}";
        }
    }

    private static List<DiagnosticRow> SortRows(List<DiagnosticRow> rows)
    {
        var sortSpecs = ImGui.TableGetSortSpecs();
        if (sortSpecs.SpecsCount == 0)
        {
            return rows.OrderByDescending(r => r.Total).ToList();
        }

        var spec = sortSpecs.Specs;
        var asc = spec.SortDirection == ImGuiSortDirection.Ascending;
        return spec.ColumnIndex switch
        {
            0 => asc ? rows.OrderBy(r => r.Name).ToList() : rows.OrderByDescending(r => r.Name).ToList(),
            1 => asc ? rows.OrderBy(r => r.Total).ToList() : rows.OrderByDescending(r => r.Total).ToList(),
            2 => asc ? rows.OrderBy(r => r.UniqueAddresses).ToList() : rows.OrderByDescending(r => r.UniqueAddresses).ToList(),
            3 => asc ? rows.OrderBy(r => r.MaxPerAddress).ToList() : rows.OrderByDescending(r => r.MaxPerAddress).ToList(),
            4 => asc ? rows.OrderBy(r => r.SecondsSinceLast).ToList() : rows.OrderByDescending(r => r.SecondsSinceLast).ToList(),
            _ => rows.OrderByDescending(r => r.Total).ToList(),
        };
    }

    private sealed class FailureStat
    {
        public long Total;
        public long UntrackedAddressHits;
        public long LastTicks;
        public readonly ConcurrentDictionary<long, int> Addresses = new();
    }

    private sealed class ReadRegionStat
    {
        public long Invocations;
        public long ReadCalls;
        public long RequestedBytes;
    }

    internal sealed class ReadRegionContext
    {
        internal ReadRegionContext(string name, ReadRegionContext? parent)
        {
            this.Name = name;
            this.Parent = parent;
        }

        internal string Name { get; }

        internal ReadRegionContext? Parent { get; }

        internal long ReadCalls;

        internal long RequestedBytes;
    }
}

/// <summary>
///     Allocation-free scope returned by <see cref="MemoryReadDiagnostics.MeasureRegion"/>.
/// </summary>
public readonly struct MemoryReadRegionScope : IDisposable
{
    private readonly MemoryReadDiagnostics.ReadRegionContext? context;

    internal MemoryReadRegionScope(MemoryReadDiagnostics.ReadRegionContext context)
    {
        this.context = context;
    }

    public void Dispose()
    {
        if (this.context != null)
        {
            MemoryReadDiagnostics.CompleteRegion(this.context);
        }
    }
}

internal enum MemoryReadKind
{
    Scalar,
    Buffer,
}

internal readonly record struct ReadRateSnapshot(
    long TotalCalls,
    long ScalarCalls,
    long BufferCalls,
    double TotalMebibytes,
    double CallsPerSecond,
    double MebibytesPerSecond,
    double MicrosecondsPerCall,
    double FailuresPerSecond,
    double SessionSeconds,
    double AverageCallsPerSecond,
    double AverageMebibytesPerSecond,
    double AverageMicrosecondsPerCall,
    long TotalFailures,
    long TotalFrames,
    double AverageFramesPerSecond,
    double AverageCallsPerFrame);

/// <summary>
///     A snapshot row for the diagnostics table.
/// </summary>
internal sealed class DiagnosticRow
{
    // A single address seen this many times is the threshold above which "the same memory
    // location keeps failing" stops looking like a coincidence of freed memory.
    private const int StructuralRepeatThreshold = 50;

    public DiagnosticRow(string name, long total, int uniqueAddresses, int maxPerAddress, long untrackedHits, double secondsSinceLast, List<(long, int)> topAddresses)
    {
        this.Name = name;
        this.Total = total;
        this.UniqueAddresses = uniqueAddresses;
        this.MaxPerAddress = maxPerAddress;
        this.UntrackedHits = untrackedHits;
        this.SecondsSinceLast = secondsSinceLast;
        this.TopAddresses = topAddresses;

        // The most-repeated address (TopAddresses is sorted by count desc). A repeated address
        // only points at a wrong offset if it's a *plausible* pointer; a repeated null or
        // out-of-range value is just a not-yet-populated field read during load.
        var dominant = topAddresses.Count > 0 ? topAddresses[0].Item1 : 0L;
        var dominantIsPlausible = GameHelper.Utils.SafeMemoryHandle.IsValidAddress(new IntPtr(dominant));
        var repeatsHeavily = maxPerAddress >= StructuralRepeatThreshold && uniqueAddresses <= 8;

        if (total < 20)
        {
            this.Verdict = "too few";
            this.VerdictColor = new Vector4(0.6f, 0.6f, 0.6f, 1f);
        }
        else if (repeatsHeavily && dominantIsPlausible)
        {
            this.Verdict = "likely structural";
            this.VerdictColor = new Vector4(1f, 0.4f, 0.4f, 1f);
        }
        else if (repeatsHeavily)
        {
            // Same null/out-of-range value read over and over: a field that wasn't ready yet
            // (typical during area load), not a layout error.
            this.Verdict = "null/not-ready";
            this.VerdictColor = new Vector4(0.6f, 0.8f, 1f, 1f);
        }
        else if (uniqueAddresses >= total / 2.0)
        {
            this.Verdict = "races (varied)";
            this.VerdictColor = new Vector4(0.4f, 0.9f, 0.4f, 1f);
        }
        else
        {
            this.Verdict = "mixed";
            this.VerdictColor = new Vector4(1f, 0.85f, 0.4f, 1f);
        }
    }

    public string Name { get; }

    public long Total { get; }

    public int UniqueAddresses { get; }

    public int MaxPerAddress { get; }

    public long UntrackedHits { get; }

    public double SecondsSinceLast { get; }

    public List<(long Addr, int Count)> TopAddresses { get; }

    public string Verdict { get; }

    public Vector4 VerdictColor { get; }
}
