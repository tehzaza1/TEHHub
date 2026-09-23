// <copyright file="BotActionLog.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Systems
{
    using System;
    using System.IO;
    using System.Text;
    using System.Text.Json;
    using System.Text.RegularExpressions;

    /// <summary>
    /// Writes opt-in, line-oriented records of bot decisions and injected inputs beside the plugin.
    /// </summary>
    internal static class BotActionLog
    {
        private static readonly object Gate = new();
        private static readonly Regex NumberPattern = new(@"-?\d+(?:\.\d+)?", RegexOptions.Compiled);
        private static volatile bool enabled;
        private static StreamWriter? writer;
        private static string logDirectory = string.Empty;
        private static string status = "Action logging is off.";
        private static string mode = string.Empty;
        private static string action = string.Empty;
        private static string lastModeActionKey = string.Empty;

        public static bool Enabled => enabled;

        public static string LogDirectory
        {
            get
            {
                lock (Gate)
                {
                    return logDirectory;
                }
            }
        }

        public static string Status
        {
            get
            {
                lock (Gate)
                {
                    return status;
                }
            }
        }

        /// <summary>Starts or stops a new per-session JSONL file inside the plugin directory.</summary>
        public static void Configure(bool enabled, string pluginDirectory)
        {
            lock (Gate)
            {
                try
                {
                    logDirectory = Path.Combine(Path.GetFullPath(pluginDirectory), "Logs");
                }
                catch
                {
                    logDirectory = string.Empty;
                }

                if (!enabled)
                {
                    BotActionLog.enabled = false;
                    if (writer != null)
                    {
                        WriteLocked("logging", "Logging disabled by setting.");
                        CloseWriterLocked();
                    }

                    status = "Action logging is off.";
                    lastModeActionKey = string.Empty;
                    return;
                }

                if (writer != null)
                {
                    return;
                }

                BotActionLog.enabled = false;

                try
                {
                    Directory.CreateDirectory(logDirectory);

                    string fileName = $"bot-actions-{DateTime.Now:yyyyMMdd-HHmmss-fff}.jsonl";
                    string path = Path.Combine(logDirectory, fileName);
                    writer = new StreamWriter(
                        new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite),
                        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                    {
                        AutoFlush = true,
                    };
                    BotActionLog.enabled = true;
                    status = $"Writing {fileName}";
                    lastModeActionKey = string.Empty;
                    WriteLocked("logging", "Logging enabled.");
                }
                catch (Exception ex)
                {
                    CloseWriterLocked();
                    status = $"Action logging unavailable: {ex.Message}";
                }
            }
        }

        /// <summary>Records a single non-tick action or event. Calls are safe from input worker threads.</summary>
        public static void Write(string eventType, string detail)
        {
            if (!enabled)
            {
                return;
            }

            lock (Gate)
            {
                WriteLocked(eventType, detail);
            }
        }

        /// <summary>Updates the action context and records only when the bot's decision changes.</summary>
        public static void ObserveAction(string currentMode, string currentAction)
        {
            if (!enabled)
            {
                return;
            }

            lock (Gate)
            {
                mode = currentMode ?? string.Empty;
                action = currentAction ?? string.Empty;

                // Numeric values in status text (HP, distance, percentages) can change every tick.
                // Normalize them for comparison so the log records decision transitions, not frames.
                string key = $"{mode}|{NumberPattern.Replace(action, "#")}";
                if (!string.Equals(key, lastModeActionKey, StringComparison.Ordinal))
                {
                    lastModeActionKey = key;
                    WriteLocked("bot.decision", action.Length == 0 ? mode : $"{mode}: {action}");
                }
            }
        }

        /// <summary>Closes the current file cleanly during plugin shutdown.</summary>
        public static void Close()
        {
            lock (Gate)
            {
                enabled = false;
                if (writer != null)
                {
                    WriteLocked("logging", "Plugin session ended.");
                    CloseWriterLocked();
                }
            }
        }

        private static void WriteLocked(string eventType, string detail)
        {
            if (writer == null)
            {
                return;
            }

            try
            {
                var entry = new
                {
                    timestampUtc = DateTimeOffset.UtcNow,
                    eventType,
                    detail = Truncate(detail),
                    mode = Truncate(mode),
                    action = Truncate(action),
                };
                writer.WriteLine(JsonSerializer.Serialize(entry));
            }
            catch (Exception ex)
            {
                enabled = false;
                CloseWriterLocked();
                status = $"Action logging stopped after a file error: {ex.Message}";
            }
        }

        private static void CloseWriterLocked()
        {
            try
            {
                writer?.Dispose();
            }
            catch
            {
                // Logging is best effort; disposal failures must not affect bot control.
            }

            writer = null;
        }

        private static string Truncate(string? value) =>
            string.IsNullOrEmpty(value) ? string.Empty : value.Length <= 1200 ? value : value[..1200];
    }
}
