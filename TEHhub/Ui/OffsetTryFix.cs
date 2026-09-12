namespace TEHhub.Ui
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.InteropServices;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using TEHhub.Utils;

    /// <summary>Conservative session-only recovery coordinator. Dumps never authorize future sessions.</summary>
    internal static class OffsetTryFix
    {
        private sealed class Entry
        {
            internal RuntimeOffsetPatch? Active;
            internal string Candidate = string.Empty;
            internal int Confirmations;
            internal int FailureConfirmations;
            internal DateTime LastObservation;
            internal string Status = "waiting";
        }

        private static readonly Dictionary<Type, Entry> Entries = new();
        private static SafeMemoryHandle? session;
        private static DateTime lastPoll;
        private static DateTime settleUntil;
        private static IntPtr area;
        private static bool enabledLastTick;
        internal static TimeProvider Time { get; set; } = TimeProvider.System;
        private static DateTime Now => Time.GetUtcNow().UtcDateTime;
        private static readonly Dictionary<RuntimeOffsetPatch, OffsetRecoveryReport> Reports = new();
        internal static string DumpDirectory => Path.Join(AppContext.BaseDirectory, "configs", "offset-recovery");
        internal static string LastDumpError { get; private set; } = string.Empty;
        internal static IEnumerable<string> Status => Entries.Select(e => $"{e.Key.Name}: {e.Value.Status}");

        internal static void Disable(string reason = "disabled")
        {
            foreach (var entry in Entries.Where(e => e.Value.Active != null))
            {
                Dump(entry.Value.Active!, reason, null);
            }

            RuntimeOffsetRegistry.Reset();
            Entries.Clear();
            Reports.Clear();
            lastPoll = Now;
        }

        internal static bool ShouldPoll(IntPtr currentArea)
        {
            if (enabledLastTick && !Core.GHSettings.EnableOffsetTryFix)
            {
                Disable();
            }

            enabledLastTick = Core.GHSettings.EnableOffsetTryFix;
            if (!ReferenceEquals(session, Core.Process.Handle))
            {
                Disable("session-changed");
                OffsetHelperEngine.ResetSession();
                session = Core.Process.Handle;
                area = IntPtr.Zero;
            }

            if (currentArea != area)
            {
                area = currentArea;
                settleUntil = Now.AddSeconds(3);
                foreach (var entry in Entries.Values)
                {
                    entry.Confirmations = 0;
                    if (entry.Active != null)
                    {
                        RuntimeOffsetRegistry.Remove(entry.Active.StructType);
                        Dump(entry.Active, "suspended-area-change", null);
                        entry.Active = null;
                        entry.Status = "area changed; reconfirm before use";
                    }
                }
            }

            if (!Core.GHSettings.EnableOffsetTryFix || currentArea == IntPtr.Zero ||
                Core.Process.Handle == null || Core.Process.Handle.IsInvalid ||
                Now < settleUntil || Now - lastPoll < TimeSpan.FromSeconds(10))
            {
                return false;
            }

            lastPoll = Now;
            return true;
        }

        internal static void Process(SweepResult sweep, OffsetRecoveryHints hints)
        {
            if (!Core.GHSettings.EnableOffsetTryFix || !sweep.InGame || Now < settleUntil)
            {
                return;
            }

            foreach (var probe in sweep.Probes.Where(p => p.StructType != null && !p.Unmapped))
            {
                var type = probe.StructType!;
                if (!Entries.TryGetValue(type, out var entry))
                {
                    Entries[type] = entry = new Entry();
                }

                if (Now - entry.LastObservation < TimeSpan.FromSeconds(5))
                {
                    continue;
                }

                entry.LastObservation = Now;
                if (probe.Roots.Count == 0 || probe.Verdict == OffsetHelperEngine.ProbeVerdict.NoRoot)
                {
                    entry.Confirmations = 0;
                    if (entry.Active != null)
                    {
                        RuntimeOffsetRegistry.Remove(type);
                        Dump(entry.Active, "suspended-no-live-root", probe);
                        entry.Active = null;
                    }

                    entry.Status = "no live root; no recovery applied";
                    continue;
                }

                if (entry.Active != null)
                {
                    if (OffsetHelperEngine.ValidateRuntimePatch(probe, entry.Active, hints))
                    {
                        entry.Status = "recovered; revalidated";
                        continue;
                    }

                    // Revoke immediately; do not keep serving an unproven native layout.
                    RuntimeOffsetRegistry.Remove(type);
                    Dump(entry.Active, "revoked-validation-failed", probe);
                    entry.Active = null;
                    entry.Confirmations = 0;
                    entry.Status = "recovery revoked; original layout restored";
                    continue;
                }

                if (probe.Verdict != OffsetHelperEngine.ProbeVerdict.Degraded)
                {
                    entry.FailureConfirmations = 0;
                    if (probe.Verdict == OffsetHelperEngine.ProbeVerdict.Intact) SetBlocked(type, false);
                    entry.Confirmations = 0;
                    entry.Status = probe.Verdict == OffsetHelperEngine.ProbeVerdict.Intact
                        ? "original anchors pass" : "no decisive semantic anchors";
                    continue;
                }

                var patch = OffsetHelperEngine.CreateRuntimePatch(probe);
                if (patch == null || !OffsetHelperEngine.ValidateRuntimePatch(probe, patch, hints))
                {
                    entry.Confirmations = 0;
                    entry.Status = "cannot safely auto-repair; inspect recovery suggestions / fallback";
                    if (probe.Roots.Select(r => r.Owner).Distinct().Count() >= 2 &&
                        probe.Roots.All(r => r.ReadOk && r.Fields.Any(f => f.Status == OffsetHelperEngine.FieldStatus.Fail &&
                            f.Kind is OffsetHelperEngine.FieldKind.OwnerPtr or OffsetHelperEngine.FieldKind.Vector or OffsetHelperEngine.FieldKind.Map)))
                    {
                        if (++entry.FailureConfirmations >= 3 && type != typeof(TEHhub.Offsets.Objects.Components.ComponentHeader))
                        {
                            SetBlocked(type, true);
                            entry.Status = "confirmed broken; component access suspended; retrying";
                        }
                    }
                    else entry.FailureConfirmations = 0;
                    continue;
                }

                var signature = string.Join(";", patch.Fields.Select(f => $"{f.Name}:{f.RecoveredOffset}"));
                if (entry.Candidate != signature)
                {
                    entry.Candidate = signature;
                    entry.Confirmations = 0;
                }

                entry.Status = $"candidate confirmed {++entry.Confirmations}/3";
                if (entry.Confirmations < 3)
                {
                    continue;
                }

                // Persist evidence first. A failed dump must not produce an undocumented repair.
                if (Dump(patch, "validated-candidate", probe, hints) &&
                    OffsetHelperEngine.ValidateRuntimePatch(probe, patch, hints))
                {
                    RuntimeOffsetRegistry.Install(patch);
                    entry.Active = patch;
                    if (Dump(patch, "activated", probe, hints))
                    {
                        SetBlocked(type, false);
                        entry.Status = "recovered; saved; monitoring";
                    }
                    else
                    {
                        RuntimeOffsetRegistry.Remove(type);
                        entry.Active = null;
                        entry.Confirmations = 0;
                        entry.Status = "activation withdrawn: could not persist active status";
                    }
                }
            }

            DumpCoverage(sweep);
        }

        private static void SetBlocked(Type type, bool blocked)
        {
            foreach (var component in OffsetHelperEngine.ComponentOffsetTypes.Where(c => c.Value == type))
            {
                RuntimeOffsetRegistry.SetComponentBlocked(component.Key, blocked);
            }
        }

        private static bool Dump(RuntimeOffsetPatch patch, string action, ProbeResult? probe, OffsetRecoveryHints? hints = null)
        {
            try
            {
                Directory.CreateDirectory(DumpDirectory);
                Reports.TryGetValue(patch, out var previous);
                var module = previous == null ? Core.Process.Information?.MainModule : null;
                var moduleFile = module?.FileName;
                var file = moduleFile == null ? null : new FileInfo(moduleFile);
                var report = new OffsetRecoveryReport
                {
                    WhenUtc = Now,
                    Action = action,
                    Struct = patch.StructType.FullName ?? patch.StructType.Name,
                    GameModule = moduleFile ?? string.Empty,
                    GameVersion = module?.FileVersionInfo.FileVersion ?? string.Empty,
                    GameFileLength = file?.Length ?? 0,
                    GameLastWriteUtc = file?.LastWriteTimeUtc ?? default,
                    GameSha256 = previous?.GameSha256 ?? (moduleFile == null ? string.Empty : HashFile(moduleFile)),
                    Fields = patch.Fields.Select(f => new OffsetRecoveryReportField
                    {
                        Name = f.Name, OriginalOffset = f.OriginalOffset, RecoveredOffset = f.RecoveredOffset,
                        NativeType = patch.StructType.GetField(f.Name)?.FieldType.FullName ?? "unknown",
                    }).ToArray(),
                    Evidence = probe?.Roots.SelectMany(r => r.Fields.Where(f => f.Status is OffsetHelperEngine.FieldStatus.Pass or OffsetHelperEngine.FieldStatus.Fail)
                        .Select(f => $"{r.Label}: {f.Name}: {f.Status}: {f.Reason}")).ToArray() ?? [],
                    RecoveredEvidence = hints == null || probe == null ? [] : probe.Roots.SelectMany(r =>
                        OffsetHelperEngine.VerifyRoot(patch.StructType, r.Label, r.Address, r.Owner, "EntityPtr", r.UseRecoveryHints ? hints : null, patch).Fields
                            .Where(f => f.Status is OffsetHelperEngine.FieldStatus.Pass or OffsetHelperEngine.FieldStatus.Fail)
                            .Select(f => $"{r.Label}: {f.Name}: {f.Status}: {f.Reason}")).ToArray(),
                };
                if (previous != null)
                {
                    report.GameModule = previous.GameModule;
                    report.GameVersion = previous.GameVersion;
                    report.GameFileLength = previous.GameFileLength;
                    report.GameLastWriteUtc = previous.GameLastWriteUtc;
                    if (probe == null)
                    {
                        report.Evidence = previous.Evidence;
                        report.RecoveredEvidence = previous.RecoveredEvidence;
                    }
                }

                Reports[patch] = report;
                var json = JsonSerializer.Serialize(report, OffsetRecoveryJsonContext.Default.OffsetRecoveryReport);
                var stem = patch.StructType.FullName ?? patch.StructType.Name;
                // Unique immutable event files preserve previous sessions and rollback history.
                var eventName = $"{stem}.{Now:yyyyMMddTHHmmssfffffffZ}.{action}.{Guid.NewGuid():N}";
                File.WriteAllText(Path.Join(DumpDirectory, eventName + ".json"), json);
                var code = new StringBuilder($"// {action}; {report.WhenUtc:O}; {report.Struct}\n// Game {report.GameVersion}; bytes={report.GameFileLength}; modified={report.GameLastWriteUtc:O}\n// Review against the matching game build before applying.\n");
                foreach (var field in patch.Fields)
                {
                    code.AppendLine($"// {field.Name}: 0x{field.OriginalOffset:X} -> 0x{field.RecoveredOffset:X}");
                    code.AppendLine($"// [FieldOffset(0x{field.RecoveredOffset:X})] // {field.Name} (retain the existing declaration)");
                }

                File.WriteAllText(Path.Join(DumpDirectory, eventName + ".cs.txt"), code.ToString());
                WriteLatest(Path.Join(DumpDirectory, stem + ".latest.json"), json);
                WriteLatest(Path.Join(DumpDirectory, stem + ".latest.cs.txt"), code.ToString());
                LastDumpError = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                LastDumpError = ex.Message;
                Console.WriteLine($"[OffsetTryFix.Dump] {ex.Message}");
                return false;
            }
        }

        private static string HashFile(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }

        private static void WriteLatest(string path, string content)
        {
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, content);
            File.Move(temporary, path, overwrite: true);
        }

        private static void DumpCoverage(SweepResult sweep)
        {
            try
            {
                Directory.CreateDirectory(DumpDirectory);
                var coverage = typeof(TEHhub.Offsets.Objects.Components.ComponentHeader).Assembly.GetTypes()
                    .Where(t => t.IsValueType && !t.IsEnum)
                    .Select(t => new OffsetRecoveryCoverage
                    {
                        Struct = t.FullName ?? t.Name,
                        Status = Entries.TryGetValue(t, out var entry) ? entry.Status : "no live probe / semantic repair contract",
                        Fields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                            .Where(f => f.GetCustomAttribute<FieldOffsetAttribute>() != null)
                            .Select(f => $"{f.Name}: 0x{f.GetCustomAttribute<FieldOffsetAttribute>()!.Value:X}").ToArray(),
                    }).Where(c => c.Fields.Length != 0).OrderBy(c => c.Struct).ToArray();
                WriteLatest(Path.Join(DumpDirectory, "coverage.latest.json"),
                    JsonSerializer.Serialize(coverage, OffsetRecoveryJsonContext.Default.OffsetRecoveryCoverageArray));
            }
            catch (Exception ex)
            {
                LastDumpError = ex.Message;
            }
        }
    }

    internal sealed class OffsetRecoveryReport
    {
        public DateTime WhenUtc { get; set; }
        public string Action { get; set; } = string.Empty;
        public string Struct { get; set; } = string.Empty;
        public string GameModule { get; set; } = string.Empty;
        public string GameVersion { get; set; } = string.Empty;
        public long GameFileLength { get; set; }
        public DateTime GameLastWriteUtc { get; set; }
        public string GameSha256 { get; set; } = string.Empty;
        public OffsetRecoveryReportField[] Fields { get; set; } = [];
        public string[] Evidence { get; set; } = [];
        public string[] RecoveredEvidence { get; set; } = [];
    }

    internal sealed class OffsetRecoveryReportField
    {
        public string Name { get; set; } = string.Empty;
        public string NativeType { get; set; } = string.Empty;
        public int OriginalOffset { get; set; }
        public int RecoveredOffset { get; set; }
    }

    internal sealed class OffsetRecoveryCoverage
    {
        public string Struct { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string[] Fields { get; set; } = [];
    }

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(OffsetRecoveryReport))]
    [JsonSerializable(typeof(OffsetRecoveryCoverage[]))]
    internal partial class OffsetRecoveryJsonContext : JsonSerializerContext { }
}
