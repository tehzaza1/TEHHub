namespace TEHhub.Ui
{
    using System;
    using System.Buffers.Binary;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using TEHhub.Utils;
    using ImGuiNET;

    internal static class PrimaryRootResearch
    {
        internal static string DumpDirectory => Path.Join(AppContext.BaseDirectory, "configs", "offset-recovery");
        private static CancellationTokenSource? cancellation;
        private static Task<PrimaryRootResearchReport>? running;
        private static SafeMemoryHandle? attached;
        private static string status = "idle";
        private static string localModel = OffsetAiResearch.DefaultModel;
        private static bool requestAi;
        internal static Dictionary<string, string> GetToolStatus() => new()
        {
            ["status"] = status, ["running"] = (running != null).ToString(),
            ["model"] = localModel, ["aiRequested"] = requestAi.ToString()
        };

        internal static void Tick()
        {
            if (running == null) return;
            if (!ReferenceEquals(attached, Core.Process.Handle) || attached.IsClosed)
                cancellation?.Cancel();
            if (!running.IsCompleted) return;
            try { status = running.GetAwaiter().GetResult().Status; }
            catch (Exception ex) { status = "research failed: " + ex.Message; }
            running = null;
            cancellation?.Dispose();
            cancellation = null;
        }

        internal static void Draw()
        {
            ImGui.TextWrapped("Primary-root research: " + status);
            if (running != null)
            {
                if (ImGui.Button("Cancel primary-root research")) cancellation?.Cancel();
            }
            else if (DrawStartButtons())
            {
                try
                {
                    attached = Core.Process.Handle;
                    if (attached == null || attached.IsInvalid || attached.IsClosed) { status = "attach to game first"; return; }
                    var module = Core.Process.Information.MainModule ?? throw new InvalidOperationException("No main module.");
                    var reader = attached;
                    var baseAddress = module.BaseAddress.ToInt64();
                    var size = module.ModuleMemorySize;
                    var path = module.FileName;
                    var version = module.FileVersionInfo.FileVersion ?? string.Empty;
                    var processId = Core.Process.Information.Id;
                    cancellation = new CancellationTokenSource();
                    var token = cancellation.Token;
                    status = "scanning in background; up to three complete observations";
                    var useAi = requestAi;
                    var model = localModel;
                    running = Task.Run(async () =>
                    {
                        var report = await Run(reader, baseAddress, size, path, version, processId, token);
                        if (useAi)
                        {
                            token.ThrowIfCancellationRequested();
                            var review = await OffsetAiResearch.Review(report, model, token);
                            report.Status += "; " + review.Status;
                            if (review.Decision != null)
                                report.Status += $"; {review.Decision.Decision} {review.Decision.CandidateId}: {review.Decision.NextProbe} — {review.Decision.Reason}";
                        }

                        return report;
                    });
                }
                catch (Exception ex) { status = ex.Message; }
            }

            ImGui.TextWrapped("Exports a confirmed candidate and chain; primary roots are not automatically installed. Stay in-world. Unsupported layouts or multiple valid roots cause abstention.");
        }

        private static bool DrawStartButtons()
        {
            requestAi = false;
            if (ImGui.Button("Research primary root (read-only)")) return true;
            ImGui.InputText("Local Ollama model", ref localModel, 200);
            if (ImGui.Button("AI-assisted root research (local)"))
            {
                requestAi = true;
                return true;
            }

            ImGui.TextWrapped("Local AI reviews scanner evidence and recommends the next probe. No invented offsets are accepted; review is saved, not installed. Start Ollama on localhost:11434 first.");
            return false;
        }

        internal static async Task<PrimaryRootResearchReport> Run(SafeMemoryHandle reader, long moduleBase, int moduleSize,
            string modulePath, string version, int processId, CancellationToken token)
        {
            var lease = false;
            var watch = Stopwatch.StartNew();
            var report = new PrimaryRootResearchReport
            {
                WhenUtc = DateTime.UtcNow, ModuleBase = moduleBase, ModuleSize = moduleSize,
                ModulePath = modulePath, GameVersion = version, ProcessId = processId,
            };
            try
            {
                reader.DangerousAddRef(ref lease);
                var memory = new LiveMemory(reader);
                var regions = ReadSections(memory, moduleBase, moduleSize);
                using (var file = File.OpenRead(modulePath)) report.GameSha256 = Convert.ToHexString(SHA256.HashData(file));
                PrimaryRootCandidate? expected = null;
                for (var n = 0; n < 3; n++)
                {
                    token.ThrowIfCancellationRequested();
                    var result = new PrimaryRootSearch(memory, regions, token).Run();
                    report.Observations.Add(result);
                    if (!result.Complete || result.Candidates.Count != 1)
                    {
                        report.Status = result.Status;
                        break;
                    }

                    var candidate = result.Candidates[0];
                    if (expected != null && candidate != expected)
                    {
                        report.Status = "graph/area identity changed; abstained";
                        break;
                    }

                    expected = candidate;
                    if (n == 2)
                    {
                        report.Confirmed = true;
                        report.Status = "confirmed across three complete scans; exported for review";
                        report.GlobalSlotRva = candidate.GlobalSlot - moduleBase;
                    }
                    else await Task.Delay(1000, token);
                }

                token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) { report.Confirmed = false; report.Status = "cancelled; abstained"; }
            catch (Exception ex) { report.Confirmed = false; report.Status = "research failed; abstained: " + ex.Message; }
            finally { if (lease) reader.DangerousRelease(); }
            report.ElapsedMs = watch.ElapsedMilliseconds;
            Directory.CreateDirectory(DumpDirectory);
            var json = JsonSerializer.Serialize(report, PrimaryRootResearchJsonContext.Default.PrimaryRootResearchReport);
            var path = Path.Join(DumpDirectory, $"primary-root.{DateTime.UtcNow:yyyyMMddTHHmmssfffffffZ}.{Guid.NewGuid():N}.json");
            File.WriteAllText(path, json);
            var notes = new StringBuilder("// Primary-root research; review against the matching game SHA-256.\n");
            notes.AppendLine("// " + report.Status);
            if (report.Confirmed)
            {
                var root = report.Observations[^1].Candidates[0];
                notes.AppendLine($"// Global slot RVA: 0x{report.GlobalSlotRva:X} (module-relative; do not hardcode session addresses)");
                notes.AppendLine($"// manager = ReadPtr(moduleBase + 0x{report.GlobalSlotRva:X});");
                notes.AppendLine($"// inGame = ReadPtr(manager + 0x{root.TableOffset:X} + {root.StateIndex} * 0x10);");
            }
            else notes.AppendLine("// NOT CONFIRMED: do not apply structural hypotheses as recovered offsets.");
            File.WriteAllText(Path.ChangeExtension(path, ".cs.txt"), notes.ToString());
            var latest = Path.Join(DumpDirectory, "primary-root.latest.json");
            File.WriteAllText(latest + ".tmp", json);
            File.Move(latest + ".tmp", latest, overwrite: true);
            File.WriteAllText(Path.Join(DumpDirectory, "primary-root.latest.cs.txt"), notes.ToString());
            return report;
        }

        internal static RootRegion[] ReadSections(IRootSearchMemory memory, long moduleBase, int moduleSize)
        {
            var header = new byte[4096];
            if (moduleSize < header.Length || moduleSize > 1024 * 1024 * 1024 || !memory.Read(moduleBase, header) ||
                BinaryPrimitives.ReadUInt16LittleEndian(header) != 0x5A4D) throw new InvalidDataException("Invalid DOS image.");
            var pe = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0x3C));
            if (pe < 64 || pe > 2048 || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(pe)) != 0x4550 ||
                BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(pe + 4)) != 0x8664)
                throw new InvalidDataException("Unsupported PE image.");
            var count = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(pe + 6));
            var optional = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(pe + 20));
            if (optional < 64 || optional > 512 || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(pe + 24)) != 0x20B ||
                BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(pe + 24 + 56)) != moduleSize)
                throw new InvalidDataException("Invalid image extent.");
            var start = pe + 24 + optional;
            if (count == 0 || count > 96 || start + count * 40 > header.Length) throw new InvalidDataException("Invalid section table.");
            var regions = new List<RootRegion>();
            for (var i = 0; i < count; i++)
            {
                var section = header.AsSpan(start + i * 40, 40);
                var length = BinaryPrimitives.ReadInt32LittleEndian(section[8..]);
                var rva = BinaryPrimitives.ReadInt32LittleEndian(section[12..]);
                var flags = BinaryPrimitives.ReadUInt32LittleEndian(section[36..]);
                if (length < 0 || ((flags & 0x20000000) != 0 && (flags & 0x40000000) == 0))
                    throw new InvalidDataException("Unsupported executable extent/access.");
                if (length == 0) continue;
                if (rva < 4096 || rva > moduleSize - length) throw new InvalidDataException("Invalid section extent.");
                if (regions.Exists(r => moduleBase + rva < r.Start + r.Length && r.Start < moduleBase + rva + length))
                    throw new InvalidDataException("Overlapping section extents.");
                if ((flags & 0x40000000) != 0) regions.Add(new(moduleBase + rva, length, (flags & 0x20000000) != 0));
            }

            if (!regions.Exists(r => r.Executable) || !regions.Exists(r => !r.Executable))
                throw new InvalidDataException("No supported code/data sections.");
            return regions.ToArray();
        }

        private sealed class LiveMemory(SafeMemoryHandle reader) : IRootSearchMemory
        {
            public bool Read(long address, Span<byte> destination)
            {
                var bytes = new byte[destination.Length];
                if (!reader.TryReadMemoryArray(new IntPtr(address), bytes, out _)) return false;
                bytes.CopyTo(destination);
                return true;
            }
        }
    }

    internal sealed class PrimaryRootResearchReport
    {
        public DateTime WhenUtc { get; set; }
        public string Status { get; set; } = "no supported root";
        public bool Confirmed { get; set; }
        public int ProcessId { get; set; }
        public long ModuleBase { get; set; }
        public int ModuleSize { get; set; }
        public string ModulePath { get; set; } = string.Empty;
        public string GameVersion { get; set; } = string.Empty;
        public string GameSha256 { get; set; } = string.Empty;
        public long GlobalSlotRva { get; set; }
        public long ElapsedMs { get; set; }
        public List<PrimaryRootSearchResult> Observations { get; set; } = new();
    }

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(PrimaryRootResearchReport))]
    internal partial class PrimaryRootResearchJsonContext : JsonSerializerContext { }
}
