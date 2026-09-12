namespace TEHhub.Ui;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ImGuiNET;
using L = TEHhub.Localization.OverlayLocalization;

internal enum RuntimeLogLevel { Info, Warning, Error }
internal sealed record RuntimeLogEntry(DateTime Time, RuntimeLogLevel Level, string Source, string Message);

/// <summary>Bounded console capture and asynchronous disk logging; rendering never writes files.</summary>
internal static class RuntimeLog
{
    private static readonly object Gate = new();
    private static readonly Queue<RuntimeLogEntry> Entries = new();
    private static readonly Channel<RuntimeLogEntry> Pending = Channel.CreateBounded<RuntimeLogEntry>(
        new BoundedChannelOptions(2048) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private static Task? writer;
    private static TextWriter? originalOut;
    private static TextWriter? originalError;
    private static long dropped;
    private static string diskStatus = "";
    private static bool info = true, warning = true, error = true, follow = true;
    private static string search = "";
    internal static string DirectoryPath => Path.Join(AppContext.BaseDirectory, "logs");

    internal static void Initialize()
    {
        if (writer != null) return;
        writer = Task.Run(WriteFiles);
        originalOut = Console.Out;
        originalError = Console.Error;
        Console.SetOut(new CaptureWriter(originalOut, RuntimeLogLevel.Info));
        Console.SetError(new CaptureWriter(originalError, RuntimeLogLevel.Error));
        Write(RuntimeLogLevel.Info, "TEHhub", "Session started; version " + typeof(Core).Assembly.GetName().Version?.ToString(3));
    }

    internal static void Stop()
    {
        Console.Out.Flush();
        Console.Error.Flush();
        if (originalOut != null) Console.SetOut(originalOut);
        if (originalError != null) Console.SetError(originalError);
        Pending.Writer.TryComplete();
        try { writer?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
    }

    internal static void Write(RuntimeLogLevel level, string source, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        source = string.IsNullOrWhiteSpace(source) ? "Unknown" : source[..Math.Min(200, source.Length)];
        var entry = new RuntimeLogEntry(DateTime.Now, level, source, message.Length > 16000 ? message[..16000] + " [truncated]" : message);
        lock (Gate)
        {
            if (Entries.Count >= 1000) Entries.Dequeue();
            Entries.Enqueue(entry);
        }
        if (!Pending.Writer.TryWrite(entry)) Interlocked.Increment(ref dropped);
    }

    internal static RuntimeLogEntry[] Snapshot() { lock (Gate) return Entries.ToArray(); }
    internal static void Clear() { lock (Gate) Entries.Clear(); }

    private static async Task WriteFiles()
    {
        StreamWriter? output = null;
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            // Retain the newest ten files belonging to this logger, never arbitrary user logs.
            foreach (var old in new DirectoryInfo(DirectoryPath).GetFiles("runtime-*.log").OrderByDescending(f => f.LastWriteTimeUtc).Skip(9))
                old.Delete();
            var session = $"runtime-{DateTime.UtcNow:yyyyMMddTHHmmssfffffffZ}-{Guid.NewGuid():N}";
            var segment = 0;
            long bytes = 0;
            output = new StreamWriter(Path.Join(DirectoryPath, session + "-0.log"), false, new UTF8Encoding(false)) { AutoFlush = true };
            await foreach (var entry in Pending.Reader.ReadAllAsync())
            {
                var line = $"{entry.Time:yyyy-MM-dd HH:mm:ss.fff} [{entry.Level}] [{entry.Source}] {entry.Message}";
                if (bytes > 5 * 1024 * 1024)
                {
                    await output.DisposeAsync();
                    segment = (segment + 1) % 3;
                    output = new StreamWriter(Path.Join(DirectoryPath, session + $"-{segment}.log"), false, new UTF8Encoding(false)) { AutoFlush = true };
                    bytes = 0;
                }
                await output.WriteLineAsync(line);
                bytes += Encoding.UTF8.GetByteCount(line) + 2;
            }
        }
        catch (Exception ex)
        {
            Volatile.Write(ref diskStatus, "Log file unavailable: " + ex.Message);
            // Drain without disk writes so a disk error cannot block producers or shutdown.
            await foreach (var _ in Pending.Reader.ReadAllAsync()) { }
        }
        finally { if (output != null) await output.DisposeAsync(); }
    }

    internal static void DrawContent()
    {
        ImGui.Checkbox("Info", ref info); ImGui.SameLine();
        ImGui.Checkbox("Warning", ref warning); ImGui.SameLine();
        ImGui.Checkbox("Error", ref error); ImGui.SameLine();
        ImGui.Checkbox(L.T("logs.follow", "Auto-scroll"), ref follow);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##LogSearch", L.T("logs.search", "Search logs"), ref search, 200);
        var visible = Snapshot().Where(e => (e.Level == RuntimeLogLevel.Info ? info : e.Level == RuntimeLogLevel.Warning ? warning : error)
            && (e.Message.Contains(search, StringComparison.OrdinalIgnoreCase) || e.Source.Contains(search, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (ImGui.Button(L.T("logs.clear", "Clear view"))) Clear();
        ImGui.SameLine();
        if (ImGui.Button(L.T("logs.copy", "Copy filtered logs")))
            ImGui.SetClipboardText(string.Join(Environment.NewLine, visible.Select(e => $"{e.Time:HH:mm:ss.fff} [{e.Level}] [{e.Source}] {e.Message}")));
        ImGui.TextDisabled(DirectoryPath);
        if (Volatile.Read(ref diskStatus).Length > 0) ImGui.TextWrapped(Volatile.Read(ref diskStatus));
        if (Interlocked.Read(ref dropped) > 0) ImGui.TextWrapped($"Disk log queue overflow: {Interlocked.Read(ref dropped)} entries dropped; recent entries remain in the viewer.");
        if (ImGui.BeginChild("LogEntries", Vector2.Zero, ImGuiChildFlags.Borders))
        {
            var atBottom = ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 4;
            foreach (var entry in visible)
            {
                var color = entry.Level == RuntimeLogLevel.Error ? new Vector4(1, 0.35f, 0.35f, 1)
                    : entry.Level == RuntimeLogLevel.Warning ? new Vector4(1, 0.8f, 0.3f, 1) : ImGuiTheme.TextMuted;
                ImGui.PushStyleColor(ImGuiCol.Text, color);
                ImGui.TextWrapped($"{entry.Time:HH:mm:ss.fff} [{entry.Level}] [{entry.Source}] {entry.Message}");
                ImGui.PopStyleColor();
            }
            if (follow && atBottom) ImGui.SetScrollHereY(1);
        }
        ImGui.EndChild();
    }

    private sealed class CaptureWriter(TextWriter original, RuntimeLogLevel level) : TextWriter
    {
        private readonly StringBuilder partial = new();
        public override Encoding Encoding => original.Encoding;
        public override void Write(char value)
        {
            lock (partial)
            {
                original.Write(value);
                Capture(value);
            }
        }
        private void Capture(char value)
        {
                if (value == '\n') Emit();
                else if (value != '\r')
                {
                    partial.Append(value);
                    if (partial.Length >= 16000) Emit();
                }
        }
        public override void Write(string? value)
        {
            if (value == null) return;
            lock (partial) { original.Write(value); foreach (var character in value) Capture(character); }
        }
        public override void WriteLine(string? value) { lock (partial) { Write(value); Write('\n'); } }
        public override void Flush() { lock (partial) { Emit(); original.Flush(); } }
        private void Emit()
        {
            if (partial.Length == 0) return;
            var message = partial.ToString();
            var source = level == RuntimeLogLevel.Error ? "Console.Error" : "Console";
            if (message.StartsWith('[') && message.IndexOf(']') is > 1 and < 200)
                source = message[1..message.IndexOf(']')];
            var severity = level;
            if (level == RuntimeLogLevel.Info)
            {
                if (Regex.IsMatch(message, @"(?:^|[\s\]])(?:error|exception|failed|threw)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) severity = RuntimeLogLevel.Error;
                else if (Regex.IsMatch(message, @"(?:^|[\s\]])(?:warning|warn)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) severity = RuntimeLogLevel.Warning;
            }
            RuntimeLog.Write(severity, source, message);
            partial.Clear();
        }
    }
}
