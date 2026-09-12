using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TEHhub.Utils;
using TEHhub.Ui;

internal static class RootSearchArena
{
    internal static void InspectKnownRoot(SafeMemoryHandle reader, long imageBase, int imageSize)
    {
        var pattern = TEHhub.Offsets.StaticOffsetsPatterns.Patterns[0];
        foreach (var region in PrimaryRootResearch.ReadSections(new RealMemory(reader), imageBase, imageSize).Where(r => r.Executable))
        {
            for (var offset = 0; offset < region.Length; offset += 65536)
            {
                var code = new byte[Math.Min(65536 + pattern.Data.Length, region.Length - offset)];
                if (!reader.TryReadMemoryArray(new IntPtr(region.Start + offset), code, out _)) continue;
                for (var i = 0; i + pattern.Data.Length <= code.Length && i < 65536; i++)
                {
                    if (code[i] != pattern.Data[0] || code[i + 1] != pattern.Data[1] || code[i + 2] != pattern.Data[2]) continue;
                    var matches = true;
                    for (var j = 3; j < pattern.Data.Length; j++) if (pattern.Mask[j] && code[i + j] != pattern.Data[j]) { matches = false; break; }
                    if (!matches) continue;
                    var at = region.Start + offset + i + pattern.BytesToSkip;
                    var slot = at + 4 + reader.ReadMemory<int>(new IntPtr(at));
                    var manager = reader.ReadMemory<IntPtr>(new IntPtr(slot));
                    var state = reader.ReadMemory<TEHhub.Offsets.Objects.GameStateOffset>(manager);
                    Console.WriteLine($"KNOWN PATTERN diagnostic: slot=0x{slot:X}; manager=0x{manager:X}; first=0x{reader.ReadMemory<IntPtr>(manager):X}; active={state.CurrentStatePtr}");
                    for (var n = 0; n < 13; n++) Console.WriteLine($"  state {n}: {state.States[n]}");
                    var ig = state.States[4].X;
                    var area = reader.ReadMemory<IntPtr>(ig + 0x290);
                    var player = area == IntPtr.Zero ? IntPtr.Zero : reader.ReadMemory<IntPtr>(area + 0x5D0);
                    Console.WriteLine($"  known inGame=0x{ig:X}; area=0x{area:X}; areaFirst=0x{reader.ReadMemory<IntPtr>(area):X}; player=0x{player:X}; level={reader.ReadMemory<byte>(area + 0xBC)}");
                    for (var n = 0; n < 2; n++) Console.WriteLine($"  active {n}=0x{reader.ReadMemory<IntPtr>(state.CurrentStatePtr.First + n * 16):X}");
                }
            }
        }
    }

    internal static void TestPe(SafeMemoryHandle reader, long imageBase, int imageSize, Action<bool, string> check)
    {
        var sections = PrimaryRootResearch.ReadSections(new RealMemory(reader), imageBase, imageSize);
        check(sections.Any(s => s.Executable) && sections.Any(s => !s.Executable), "parse actual test-process PE sections with raw reader");
        var header = new byte[4096];
        reader.TryReadMemoryArray(new IntPtr(imageBase), header, out _);
        for (var mutation = 0; mutation < 5; mutation++)
        {
            var bad = (byte[])header.Clone();
            var pe = BinaryPrimitives.ReadInt32LittleEndian(bad.AsSpan(0x3C));
            switch (mutation)
            {
                case 0: bad[0] = 0; break;
                case 1: BinaryPrimitives.WriteInt32LittleEndian(bad.AsSpan(0x3C), int.MaxValue); break;
                case 2: BinaryPrimitives.WriteUInt16LittleEndian(bad.AsSpan(pe + 6), 65535); break;
                case 3: BinaryPrimitives.WriteUInt16LittleEndian(bad.AsSpan(pe + 20), 65535); break;
                case 4: BinaryPrimitives.WriteInt32LittleEndian(bad.AsSpan(pe + 24 + 56), int.MaxValue); break;
            }

            var rejected = false;
            try { PrimaryRootResearch.ReadSections(new HeaderMemory(imageBase, bad), imageBase, imageSize); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected, $"reject malformed PE mutation {mutation}");
        }
    }

    private sealed class RealMemory(SafeMemoryHandle reader) : IRootSearchMemory
    {
        public bool Read(long address, Span<byte> target)
        {
            var bytes = new byte[target.Length];
            if (!reader.TryReadMemoryArray(new IntPtr(address), bytes, out _)) return false;
            bytes.CopyTo(target); return true;
        }
    }

    private sealed class HeaderMemory(long imageBase, byte[] header) : IRootSearchMemory
    {
        public bool Read(long at, Span<byte> target)
        {
            if (at != imageBase || target.Length != header.Length) return false;
            header.CopyTo(target); return true;
        }
    }

    internal static void Run(Action<bool, string> check)
    {
        var trials = new List<ArenaTrial>();
        // Fixed seeds make every failure reproducible; acceptance never receives ground truth.
        for (var seed = 1; seed <= 60; seed++)
        {
            var arena = new Arena(seed);
            var expected = arena.Graph(0x30 + (seed % 5) * 16, seed % 13, seed % 2 == 0);
            var originalComponent = arena.Component;
            var originalChild = arena.Child;
            var originalActive = arena.Active;
            var originalPath = arena.Path;
            arena.Reference(expected.GlobalSlot, 16382); // Split instruction across read chunks.
            arena.Reference(expected.GlobalSlot, 40); // Duplicate instruction must not duplicate the root.
            for (var n = 0; n < 30; n++) arena.Fake(n);
            // Almost-valid worlds accompany the genuine root, so a greedy first-match
            // implementation fails even when every executable reference looks convincing.
            for (var n = 0; n < 8; n++) arena.Decoy(n);
            var result = arena.Search().Run();
            trials.Add(new(seed, "genuine among eight deep decoys and thirty shallow decoys", 1, expected, result));
            check(result.Complete && result.Candidates.Count == 1 && result.Candidates[0] == expected,
                $"arena seed {seed}: ASLR/noise/table shifts/reordered states/controller/chunk boundary");

            switch (seed % 10)
            {
                case 0: arena.Put(expected.Player + 8, 0); break;
                case 1: arena.Put(originalComponent + 8, expected.Player + 8); break;
                case 2: arena.Put(originalChild + 0xB8, originalChild); break;
                case 3: arena.Put(originalActive, originalChild); break;
                case 4: arena.DuplicateFirst(expected); break;
                case 5: arena.Put(expected.Ui + 0x18, long.MaxValue); break;
                case 6: arena.Put(expected.Player + 0x18, 1); break;
                case 7: arena.Put(expected.Ui + 8, expected.Ui + 8); break;
                case 8: arena.Put(expected.Area + 0x5D0, 0); break;
                case 9: arena.Put(originalPath, 0); break;
            }

            var broken = arena.Search().Run();
            trials.Add(new(seed, $"no-answer; mutation {seed % 10}", 0, null, broken));
            check(broken.Complete && broken.Candidates.Count == 0, $"arena seed {seed}: reject a one-invariant decoy");
        }

        {
            var arena = new Arena(71);
            var first = arena.Graph(0x50, 4, false);
            arena.Reference(first.GlobalSlot, 80);
            var second = arena.Graph(0x70, 9, true);
            arena.Reference(second.GlobalSlot, 120);
            var result = arena.Search().Run();
            check(result.Complete && result.Candidates.Count == 2 && result.Status.Contains("ambiguous"),
                "two fully valid disconnected worlds must abstain, regardless of scores");
            var alias = first.GlobalSlot + 8;
            arena.Put(alias, first.Manager);
            arena.Reference(alias, 160);
            check(arena.Search().Run().Candidates.Count == 3, "global aliases must not silently authorize the wrong slot");
        }

        {
            var arena = new Arena(72);
            var expected = arena.Graph(0x50, 4, false);
            arena.Reference(expected.GlobalSlot, 100);
            var search = arena.Search();
            check(search.Validate(expected), "fresh graph must validate");
            arena.Put32(expected.Area + 0x114, 123456);
            check(!search.Validate(expected), "area identity mutation must invalidate temporal confirmation");
            check(!arena.Search(maxReads: 3).Run().Complete, "read exhaustion cannot be reported as a successful search");
            check(!arena.Search(maxCode: 32).Run().Complete, "code budget exhaustion must abstain");
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            check(!arena.Search(token: cancel.Token).Run().Complete, "cancellation must abstain");
            arena.HoleStart = arena.Code + 16384;
            check(!arena.Search().Run().Complete, "unreadable executable pages must prevent a uniqueness claim");
        }

        {
            var arena = new Arena(73);
            for (var i = 0; i < 100; i++) arena.Fake(i);
            var result = arena.Search().Run();
            check(result.Complete && result.Candidates.Count == 0, "noise-only world must have no answer");
        }

        {
            var arena = new Arena(74);
            var expected = arena.Graph(0x50, 4, true);
            arena.Reference(expected.GlobalSlot, 200);
            // One uninstantiated state is normal in the live menu; preserve shared ownership.
            arena.Put(expected.Manager + 0x50 + 2 * 16, 0);
            arena.Put(expected.Manager + 0x50 + 2 * 16 + 8, 0);
            check(arena.Search().Run().Candidates.Single() == expected, "one lazy null state must not hide a valid in-world root");
            arena.Put(expected.Area + 0x5D0, 0);
            var missingPlayer = arena.Search().Run();
            check(missingPlayer.Candidates.Count == 0 && missingPlayer.StructuralRoots.Count == 1,
                "valid parent with unavailable player must remain a structural hypothesis, never a confirmed root");
            arena.Put(expected.GlobalSlot, 0);
            var missingHead = arena.Search().Run();
            check(missingHead.Candidates.Count == 0 && missingHead.StructuralRoots.Count == 0 && missingHead.Status.Contains("blocked"),
                "missing global head blocks descendants even though every child allocation remains readable");
        }

        {
            var arena = new Arena(75);
            var expected = arena.Graph(0x50, 4, false);
            arena.Reference(expected.GlobalSlot, 220);
            arena.Put(expected.Manager + expected.TableOffset + 8, expected.Player);
            check(arena.Search().Run().Candidates.Count == 0,
                "a broken ownership pair at the table edge must not be hidden by sliding into adjacent padding");
        }

        File.WriteAllText(Path.Join(AppContext.BaseDirectory, "root-arena-results.json"),
            JsonSerializer.Serialize(trials, ArenaJsonContext.Default.ListArenaTrial));
    }

    private sealed class Arena : IRootSearchMemory
    {
        internal long Code;
        private readonly long data;
        private readonly long heap;
        private readonly byte[] code = new byte[32775];
        private readonly byte[] globals = new byte[65536];
        private readonly byte[] objects = new byte[2 * 1024 * 1024];
        private int used;
        private int slots;
        private readonly Random random;
        internal long Component, Child, Active, Path;
        internal long HoleStart;

        internal Arena(int seed)
        {
            random = new Random(seed);
            Code = 0x140000000L + seed * 0x100000L;
            data = Code + 0x20000;
            heap = 0x5000000000L + seed * 0x1000000L;
            random.NextBytes(code);
            random.NextBytes(globals);
            random.NextBytes(objects);
            Put(data, Code + 256); // vtable -> executable function, not merely readable memory.
        }

        private long Alloc(int length = 4096)
        {
            var address = heap + used;
            Array.Clear(objects, used, length);
            used += (length + 7) & ~7;
            return address;
        }

        private void Obj(long address) => Put(address, data);
        private void Vec(long at, long first, int count, int stride)
        {
            Put(at, first); Put(at + 8, first + count * stride); Put(at + 16, first + count * stride);
        }

        internal PrimaryRootCandidate Graph(int tableOffset, int index, bool controller)
        {
            var slot = data + 0x100 + slots++ * 16;
            var manager = Alloc(); Obj(manager); Put(slot, manager);
            var states = new long[13];
            for (var i = 0; i < 13; i++)
            {
                var control = Alloc(); Obj(control); states[i] = control + 16; Obj(states[i]);
                Put(manager + tableOffset + i * 16, states[i]); Put(manager + tableOffset + i * 16 + 8, control);
            }
            var state = states[index];
            Active = Alloc(); Put(Active, state); Put(Active + 8, state - 16); Vec(manager + 0x10, Active, 1, 16);
            var area = Alloc(); Obj(area); Put(state + 0x290, area); Put32(area + 0x114, 9876);
            objects[(int)(area + 0xBC - heap)] = 80;
            var player = Alloc(); Obj(player); Put(area + 0x5D0, player);
            var details = Alloc(); Put(player + 8, details);
            var path = "Metadata/Characters/TestPlayer" + random.Next();
            Path = Alloc(); Encoding.Unicode.GetBytes(path).CopyTo(objects, (int)(Path - heap));
            Put(details + 8, Path); Put(details + 24, path.Length); Put(details + 32, path.Length);
            var components = Alloc(); Component = Alloc(); Obj(Component); Put(Component + 8, player);
            Put(components, Component); Vec(player + 0x10, components, 1, 8);
            var host = Alloc(); Put(state + (controller ? 0x318 : 0x2F0), host);
            var ui = Alloc(); Obj(ui); Put(ui + 8, ui); Put(host + (controller ? 0xBE8 : 0xBE0), ui);
            Child = Alloc(); Obj(Child); Put(Child + 8, Child); Put(Child + 0xB8, ui);
            var children = Alloc(); Put(children, Child); Vec(ui + 0x10, children, 1, 16);
            return new(slot, manager, tableOffset, index, state, area, player, ui, 9876);
        }

        internal void Fake(int index)
        {
            var slot = data + 0x4000 + index * 8;
            Put(slot, Alloc(256));
            Reference(slot, 1000 + index * 16);
        }

        internal void Decoy(int index)
        {
            var graph = Graph(0x30 + index % 5 * 16, index % 13, index % 2 == 0);
            Reference(graph.GlobalSlot, 5000 + index * 32);
            switch (index)
            {
                case 0: Put(Component + 8, Child); break;
                case 1: Put(Child + 0xB8, Child); break;
                case 2: Put(graph.Ui + 8, graph.Player); break;
                case 3: Put(Active, Child); break;
                case 4: DuplicateFirst(graph); break;
                case 5: Put(graph.Area + 0x5D0, 0); break;
                case 6: Put(Path, 0); break;
                case 7: Put(graph.Player + 0x18, long.MaxValue); break;
            }
        }

        internal void Reference(long slot, int offset)
        {
            code[offset] = 0x48; code[offset + 1] = 0x39; code[offset + 2] = 0x2D;
            BinaryPrimitives.WriteInt32LittleEndian(code.AsSpan(offset + 3), checked((int)(slot - (Code + offset + 7))));
        }

        internal void Put(long at, long value)
        {
            if (at >= heap) BinaryPrimitives.WriteInt64LittleEndian(objects.AsSpan((int)(at - heap)), value);
            else BinaryPrimitives.WriteInt64LittleEndian(globals.AsSpan((int)(at - data)), value);
        }

        internal void Put32(long at, int value) => BinaryPrimitives.WriteInt32LittleEndian(objects.AsSpan((int)(at - heap)), value);
        internal long StateAt(long at) => BinaryPrimitives.ReadInt64LittleEndian(objects.AsSpan((int)(at - heap)));
        internal void DuplicateFirst(PrimaryRootCandidate graph)
        {
            var second = StateAt(graph.Manager + graph.TableOffset + 16);
            Put(graph.Manager + graph.TableOffset, second);
            Put(graph.Manager + graph.TableOffset + 8, second - 16);
        }
        public bool Read(long at, Span<byte> destination)
        {
            if (HoleStart != 0 && at <= HoleStart && at + destination.Length > HoleStart) return false;
            foreach (var (start, bytes) in new[] { (Code, code), (data, globals), (heap, objects) })
            {
                if (at >= start && at - start <= bytes.Length - destination.Length)
                {
                    bytes.AsSpan((int)(at - start), destination.Length).CopyTo(destination); return true;
                }
            }

            return false;
        }

        internal PrimaryRootSearch Search(int maxReads = 250000, long maxCode = 128 * 1024 * 1024, CancellationToken token = default) =>
            new(this, [new(Code, code.Length, true), new(data, globals.Length, false)], token, maxReads, maxCode);
    }
}

internal sealed record ArenaTrial(int Seed, string Scenario, int ExpectedCandidates,
    PrimaryRootCandidate? GroundTruth, PrimaryRootSearchResult Observed);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<ArenaTrial>))]
internal partial class ArenaJsonContext : JsonSerializerContext { }
