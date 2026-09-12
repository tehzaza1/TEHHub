namespace TEHhub.Utils
{
    using System;
    using System.Buffers.Binary;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Text;
    using System.Threading;

    internal readonly record struct RootRegion(long Start, int Length, bool Executable)
    {
        internal bool Contains(long address, int size) => size >= 0 && address >= Start && address - Start <= (long)Length - size;
    }

    internal interface IRootSearchMemory
    {
        bool Read(long address, Span<byte> destination);
    }

    internal sealed record PrimaryRootCandidate(long GlobalSlot, long Manager, int TableOffset,
        int StateIndex, long InGameState, long Area, long Player, long Ui, uint AreaHash);

    internal sealed record PrimaryRootHypothesis(long GlobalSlot, long Manager, int TableOffset,
        int PopulatedStates, long[] ActiveStates);

    internal sealed class PrimaryRootSearchResult
    {
        public string Status { get; set; } = "searching";
        public bool Complete { get; set; }
        public int Reads { get; set; }
        public long CodeBytes { get; set; }
        public int ProposedSlots { get; set; }
        public List<PrimaryRootCandidate> Candidates { get; set; } = new();
        public Dictionary<string, int> Rejections { get; set; } = new();
        public List<PrimaryRootHypothesis> StructuralRoots { get; set; } = new();
    }

    /// <summary>
    /// Read-only root discovery. RIP byte motifs only propose globals; graph contracts decide.
    /// Unknown layouts, incomplete scans and indistinguishable valid graphs must abstain.
    /// </summary>
    internal sealed class PrimaryRootSearch
    {
        private readonly IRootSearchMemory memory;
        private readonly RootRegion[] regions;
        private readonly CancellationToken cancellation;
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        private readonly int maxReads;
        private readonly long maxCodeBytes;
        private readonly TimeSpan timeout;
        private int reads;
        private long codeBytes;
        private readonly Dictionary<string, int> rejections = new();
        private readonly List<PrimaryRootHypothesis> structuralRoots = new();

        private bool Reject(string reason)
        {
            rejections[reason] = rejections.GetValueOrDefault(reason) + 1;
            return false;
        }

        internal PrimaryRootSearch(IRootSearchMemory memory, RootRegion[] regions, CancellationToken cancellation = default,
            int maxReads = 250000, long maxCodeBytes = 128 * 1024 * 1024, TimeSpan? timeout = null)
        {
            this.memory = memory;
            this.regions = regions;
            this.cancellation = cancellation;
            this.maxReads = maxReads;
            this.maxCodeBytes = maxCodeBytes;
            this.timeout = timeout ?? TimeSpan.FromSeconds(15);
        }

        private void Budget()
        {
            cancellation.ThrowIfCancellationRequested();
            if (reads >= maxReads || elapsed.Elapsed > timeout) throw new SearchLimitException();
        }

        private bool Read(long address, Span<byte> bytes)
        {
            Budget();
            reads++;
            return address >= 0x10000 && address <= 0x7FFFFFFFFFFF - bytes.Length && memory.Read(address, bytes);
        }

        private bool Ptr(long address, out long value)
        {
            Span<byte> b = stackalloc byte[8];
            var ok = Read(address, b);
            value = ok ? BinaryPrimitives.ReadInt64LittleEndian(b) : 0;
            return ok && value >= 0x10000 && value < 0x7FFFFFFFFFFF && (value & 7) == 0;
        }

        private bool Vtable(long address) => Ptr(address, out var table) &&
            regions.Any(r => !r.Executable && r.Contains(table, 8)) && Ptr(table, out var function) &&
            regions.Any(r => r.Executable && r.Contains(function, 1));

        private bool SharedState(long address, out long state)
        {
            var pair = new byte[16];
            state = 0;
            if (!Read(address, pair)) return false;
            state = BinaryPrimitives.ReadInt64LittleEndian(pair);
            var control = BinaryPrimitives.ReadInt64LittleEndian(pair.AsSpan(8));
            if (state == 0 && control == 0) return true;
            // Current live build uses embedded shared-state objects, verified alongside
            // the configured signature. Different ownership layouts require a new contract.
            return state >= 0x10000 && control == state - 16 && Vtable(state) && Vtable(control);
        }

        private bool Vector(long address, int stride, int min, int max, out long first, out int count)
        {
            first = 0; count = 0;
            Span<byte> b = stackalloc byte[24];
            if (!Read(address, b)) return false;
            first = BinaryPrimitives.ReadInt64LittleEndian(b);
            var last = BinaryPrimitives.ReadInt64LittleEndian(b[8..]);
            var end = BinaryPrimitives.ReadInt64LittleEndian(b[16..]);
            if (first < 0x10000 || (first & 7) != 0 || last < first || end < last || end > 0x7FFFFFFFFFFF ||
                (last - first) % stride != 0 || (last - first) / stride > max || end - first > (long)stride * max) return false;
            count = (int)((last - first) / stride);
            return count >= min;
        }

        private bool Player(long player)
        {
            if (!Vtable(player) || !Ptr(player + 8, out var details) ||
                !Vector(player + 0x10, 8, 1, 128, out var components, out _) ||
                !Ptr(components, out var component) || !Vtable(component) ||
                !Ptr(component + 8, out var owner) || owner != player) return Reject("player/component identity");
            Span<byte> text = stackalloc byte[32];
            if (!Read(details + 8, text)) return Reject("player string unreadable");
            var length = BinaryPrimitives.ReadInt64LittleEndian(text[16..]);
            var capacity = BinaryPrimitives.ReadInt64LittleEndian(text[24..]);
            if (length < 20 || length > 256 || capacity < length || capacity > 4096) return Reject("player string extent");
            var pathAddress = BinaryPrimitives.ReadInt64LittleEndian(text);
            var path = new byte[(int)length * 2];
            return Read(pathAddress, path) && Encoding.Unicode.GetString(path).StartsWith("Metadata/Characters/", StringComparison.Ordinal);
        }

        private bool Ui(long ui)
        {
            if (!Vtable(ui) || !Ptr(ui + 8, out var self) || self != ui ||
                !Vector(ui + 0x10, 16, 1, 4096, out var children, out _ ) ||
                !Ptr(children, out var child) || child == ui || !Vtable(child) ||
                !Ptr(child + 8, out var childSelf) || childSelf != child ||
                !Ptr(child + 0xB8, out var parent) || parent != ui) return false;
            return true;
        }

        internal bool Validate(PrimaryRootCandidate candidate)
        {
            return Discover(candidate.GlobalSlot).Contains(candidate);
        }

        private List<PrimaryRootCandidate> Discover(long slot)
        {
            var result = new List<PrimaryRootCandidate>();
            if (!Ptr(slot, out var manager)) { Reject("global not a supported pointer"); return result; }
            if (!Vtable(manager)) { Reject("manager vtable"); return result; }
            if (!Vector(manager + 0x10, 16, 1, 13, out var active, out var activeCount))
                { Reject("active state vector"); return result; }
            var bestPopulation = 0;
            var conflictingTable = false;
            var structural = new List<PrimaryRootHypothesis>();
            for (var offset = 0x10; offset <= 0x90; offset += 8)
            {
                var states = new long[13];
                var valid = true;
                var validObjects = 0;
                var damagedIdentity = false;
                for (var i = 0; i < states.Length; i++)
                {
                    if (!SharedState(manager + offset + i * 16, out states[i]))
                    {
                        valid = false;
                        if (states[i] != 0 && Vtable(states[i])) damagedIdentity = true;
                    }
                    else if (states[i] != 0) validObjects++;
                }

                var population = states.Count(s => s != 0);
                // A damaged full table must not be "recovered" by sliding the window
                // until the bad entry drops off one edge and is replaced by padding.
                if (!valid && damagedIdentity && validObjects >= 12) conflictingTable = true;
                if (!valid || population < 12) { Reject("state table"); continue; }
                if (states.Where(s => s != 0).Distinct().Count() != population)
                {
                    conflictingTable = true; Reject("duplicate state ownership"); continue;
                }
                if (population < bestPopulation) continue;
                if (population > bestPopulation) { bestPopulation = population; result.Clear(); structural.Clear(); }
                var activeStates = new HashSet<long>();
                for (var i = 0; i < activeCount; i++)
                {
                    if (!SharedState(active + i * 16, out var state) || state == 0 || !states.Contains(state)) { valid = false; break; }
                    activeStates.Add(state);
                }

                if (!valid) { Reject("active state membership"); continue; }
                structural.Add(new(slot, manager, offset, population, activeStates.Order().ToArray()));
                for (var i = 0; i < states.Length; i++)
                {
                    var state = states[i];
                    if (!activeStates.Contains(state)) continue;
                    if (!Ptr(state + 0x290, out var area) || !Vtable(area) ||
                        !Ptr(area + 0x5D0, out var player) || !Player(player)) { Reject("in-world area/player contract"); continue; }
                    var identity = new byte[4];
                    var level = new byte[1];
                    if (!Read(area + 0x114, identity) || !Read(area + 0xBC, level) || level[0] is < 1 or > 100) continue;
                    // Both documented input modes are considered without fixed child-index paths.
                    var uis = new HashSet<long>();
                    foreach (var hostOffset in new[] { 0x2F0, 0x318 })
                    {
                        if (!Ptr(state + hostOffset, out var host)) continue;
                        foreach (var uiOffset in new[] { 0xBE0, 0xBE8, 0x340 })
                            if (Ptr(host + uiOffset, out var ui) && Ui(ui)) uis.Add(ui);
                    }

                    // Multiple UI entry points belong to the same root hypothesis.
                    if (uis.Count != 0) result.Add(new(slot, manager, offset, i, state, area, player, uis.Min(),
                        BinaryPrimitives.ReadUInt32LittleEndian(identity)));
                }
            }

            if (conflictingTable) return new();
            structuralRoots.AddRange(structural);
            return result;
        }

        internal PrimaryRootSearchResult Run()
        {
            var result = new PrimaryRootSearchResult();
            try
            {
                if (!regions.Any(r => r.Executable && r.Length > 0) || !regions.Any(r => !r.Executable && r.Length > 0))
                    throw new SearchLimitException();
                var slots = new HashSet<long>();
                foreach (var region in regions.Where(r => r.Executable))
                {
                    for (var offset = 0; offset < region.Length; offset += 16384)
                    {
                        Budget();
                        var size = Math.Min(16384 + 6, region.Length - offset);
                        if (codeBytes + size > maxCodeBytes) throw new SearchLimitException();
                        var code = new byte[size];
                        if (!Read(region.Start + offset, code)) throw new SearchLimitException();
                        codeBytes += size;
                        for (var p = 0; p + 7 <= code.Length && p < 16384; p++)
                        {
                            if (code[p] != 0x48 || (code[p + 1] != 0x39 && code[p + 1] != 0x8B) ||
                                (code[p + 2] & 0xC7) != 5) continue;
                            var target = region.Start + offset + p + 7 + BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(p + 3));
                            if ((target & 7) != 0 || !regions.Any(r => !r.Executable && r.Contains(target, 8))) continue;
                            slots.Add(target);
                            if (slots.Count > 4096) throw new SearchLimitException();
                        }
                    }
                }

                result.ProposedSlots = slots.Count;
                foreach (var slot in slots.Order()) result.Candidates.AddRange(Discover(slot));
                result.Complete = true;
                result.StructuralRoots = new(structuralRoots);
                // Aliases to the same object remain ambiguous: only a unique global is exportable.
                result.Status = result.Candidates.Count switch
                {
                    0 when result.StructuralRoots.Count != 0 => "structural root hypotheses found; descendants unavailable/unverified; abstained",
                    0 => "no supported root; descendants blocked",
                    1 => "unique candidate; temporal confirmation required",
                    _ => "ambiguous; abstained",
                };
            }
            catch (SearchLimitException) { result.Status = "incomplete: read/time/code/candidate budget or unreadable code; abstained"; }
            catch (OperationCanceledException) { result.Status = "cancelled; abstained"; }
            if (!result.Complete) result.Candidates.Clear();
            result.Reads = reads;
            result.Rejections = new(rejections);
            result.CodeBytes = codeBytes;
            return result;
        }

        private sealed class SearchLimitException : Exception { }
    }
}
