using System.Buffers.Binary;
using System.Text.Json;
using TEHhub.OffsetDoctor.Process;
using TEHhub.OffsetDoctor.RecoveryV1;
using TEHhub.OffsetDoctor.Reporting;
using TEHhub.Offsets;
using TEHhub.Offsets.Objects;

public static class Od001RecoveryTests
{
    private static readonly Pattern Pattern = StaticOffsetsPatterns.Patterns.Single(p => p.Name == "Game States");

    public static void RunAll(Action<bool, string> check)
    {
        using var fixture = new Fixture();
        long valid = fixture.Plant(0x300, 0x1000, true);
        var before = fixture.Reader.SnapshotAllBlocks();
        RecoveryResult current = fixture.Run(valid);
        check(current.Decision.TerminalResult == RecoveryTerminalResult.PASS_CURRENT &&
            current.Discovery.Scan is null && current.CurrentValidation!.Evidence.Count(e => e.Result == RecoveryEvidenceResult.PASS) >= 6,
            "OD-001 valid current value returns PASS_CURRENT without pattern discovery.");

        RecoveryResult unique = fixture.Run(0x1234, [0x5678]);
        check(fixture.Reader.MemoryMatchesSnapshot(before), "OD-001 recovery performs no synthetic memory writes.");
        check(unique.Decision.TerminalResult == RecoveryTerminalResult.PROPOSED &&
            unique.Decision.Proposal == valid && unique.Discovery.Scan?.RawMatchCount == 1 &&
            unique.Discovery.Scan.BytesScanned == 0x600 &&
            unique.Discovery.CandidateLedger.Single().ValidationEvidence.Count(e => e.Independent && e.Result == RecoveryEvidenceResult.PASS) >= 6,
            "OD-001 moved pattern yields one independently validated proposal and scan bounds.");
        check(unique.PostResultComparison!.CurrentValue == 0x1234 &&
            unique.Decision.Proposal != unique.PostResultComparison.CurrentValue && !unique.Applied,
            "OD-001 comparison is post-decision and Applied=false.");
        using (var document = JsonDocument.Parse(RecoveryJsonReportExporter.Serialize(new RecoveryReport(
            "recovery-v1.phase2", fixture.SessionIdentity, DateTime.UtcNow, [unique]))))
            check(document.RootElement.GetProperty("Results")[0].GetProperty("Discovery").GetProperty("Scan")
                .GetProperty("RawMatchCount").GetInt32() == 1,
                "OD-001 JSON includes raw match count and scan evidence.");
        using (var writer = new StringWriter())
        {
            RecoveryConsoleReportWriter.Write(writer, new RecoveryReport("recovery-v1.phase2",
                fixture.SessionIdentity, DateTime.UtcNow, [unique]));
            check(writer.ToString().Contains("raw matches") && writer.ToString().Contains("Applied=false") &&
                writer.ToString().Contains("stable-repeat-read"),
                "OD-001 console report includes scan, independent validation, and Applied=false.");
        }

        fixture.Plant(0x400, 0x1100, false);
        RecoveryResult oneOfTwo = fixture.Run(0x1234);
        check(oneOfTwo.Decision.TerminalResult == RecoveryTerminalResult.PROPOSED &&
            oneOfTwo.Discovery.Scan?.RawMatchCount == 2 &&
            oneOfTwo.Discovery.CandidateLedger.Count(c => c.Disposition == CandidateDisposition.Rejected) == 1,
            "Two raw matches with one semantically invalid GameState propose only the valid target.");
        fixture.MakeValid(0x1100);
        RecoveryResult ambiguous = fixture.Run(0x1234);
        check(ambiguous.Decision.TerminalResult == RecoveryTerminalResult.AMBIGUOUS &&
            ambiguous.Decision.SurvivorIds.Length == 2 && ambiguous.Decision.Proposal is null,
            "Two independently valid OD-001 targets remain ambiguous.");

        using (var absent = new Fixture())
        {
            var result = absent.Run();
            check(result.Decision.TerminalResult == RecoveryTerminalResult.NOT_FOUND &&
                result.Discovery.Scan?.RawMatchCount == 0,
                "Complete module scan without pattern is NOT_FOUND.");
        }
        using (var malformed = new Fixture())
        {
            malformed.Plant(0x300, 0x1000, false, int.MaxValue);
            var result = malformed.Run();
            check(result.Decision.TerminalResult == RecoveryTerminalResult.NOT_FOUND &&
                result.Discovery.Scan?.RawMatchCount == 1 &&
                result.Discovery.CandidateLedger.Single().RejectionReasons.Any(r => r.Predicate == "rip-disp32-module-target"),
                "Malformed RIP target is retained and rejected with a reason.");
        }
        using (var incomplete = new Fixture(sectionLength: 0x2000, allocation: 0x1000))
        {
            var result = incomplete.Run();
            check(result.Decision.TerminalResult == RecoveryTerminalResult.ERROR &&
                result.Detail!.Contains("read failed"), "Incomplete module read is ERROR.");
        }
        using (var budget = new Fixture())
        {
            var session = new RecoverySession(budget.Reader, RecoveryContextSnapshot.Create([]));
            var result = Od001GameStatesRecovery.Run(session, maxScanBytes: 0x100);
            check(result.Decision.TerminalResult == RecoveryTerminalResult.ERROR &&
                result.Detail!.Contains("budget"), "Scan budget exhaustion is ERROR, not NOT_FOUND.");
        }
        using (var stale = new Fixture())
        {
            var session = new RecoverySession(stale.Reader, RecoveryContextSnapshot.Create([]));
            stale.Reader.Metadata = new ProcessMetadata
            {
                ProcessId = 99999, ProcessName = "SyntheticPoE2.exe", FileVersion = "changed-build",
                ModuleBase = stale.Reader.MainModuleBase, ModuleMemorySize = stale.Reader.MainModuleSize,
                AttachedTimeUtc = session.Identity.AttachedTimeUtc
            };
            var result = Od001GameStatesRecovery.Run(session);
            check(result.Decision.TerminalResult == RecoveryTerminalResult.ERROR &&
                result.Detail!.Contains("stale"), "Stale OD-001 process/build identity is ERROR.");
        }
        using (var history = new Fixture())
        {
            history.Plant(0x300, 0x1000, true);
            var a = history.Run(0x1234, [0x5678]);
            var b = history.Run(0x2345, [0x6789]);
            check(a.Discovery.CandidateLedger.Select(c => (c.Id, c.Value, c.Disposition,
                    string.Join(";", c.ValidationEvidence.Select(e => $"{e.Predicate}:{e.Result}"))))
                    .SequenceEqual(b.Discovery.CandidateLedger.Select(c => (c.Id, c.Value, c.Disposition,
                    string.Join(";", c.ValidationEvidence.Select(e => $"{e.Predicate}:{e.Result}"))))) &&
                a.Discovery.Scan?.RawMatchCount == b.Discovery.Scan?.RawMatchCount &&
                a.Discovery.Scan?.BytesScanned == b.Discovery.Scan?.BytesScanned &&
                a.Decision.TerminalResult == b.Decision.TerminalResult &&
                a.Decision.Proposal == b.Decision.Proposal &&
                a.Decision.EvidenceDigest == b.Decision.EvidenceDigest &&
                a.PostResultComparison != b.PostResultComparison,
                "Current/historical values do not change raw matches, ordered candidates, validation, or frozen decision.");
        }
        using (var equivalent = new Fixture())
        {
            equivalent.Plant(0x300, 0x1000, true);
            equivalent.Plant(0x400, 0x1000, true);
            var result = equivalent.Run();
            check(result.Decision.TerminalResult == RecoveryTerminalResult.PROPOSED &&
                result.Discovery.Scan?.RawMatchCount == 2 &&
                result.Discovery.CandidateLedger.Count(c => c.Disposition == CandidateDisposition.Equivalent) == 1 &&
                result.Discovery.EliminationStages.Single(s => s.Stage == EliminationStage.Equivalence)
                    .EquivalenceRule!.Contains("Exact candidate value"),
                "Equivalent resolved targets deduplicate by exact value while retaining both raw observations.");
        }
        Console.WriteLine("[OD-001] Synthetic scenarios passed.\n");
    }

    private sealed class Fixture : IDisposable
    {
        public SyntheticMemoryReader Reader { get; } = new();
        private readonly byte[] _module;
        private readonly long _base;
        public RecoveryIdentity SessionIdentity => new RecoverySession(Reader, RecoveryContextSnapshot.Create([])).Identity;

        public Fixture(int sectionLength = 0x600, int allocation = 0x2000)
        {
            _base = Reader.MainModuleBase.ToInt64();
            Reader.MainModuleSize = 0x3000;
            Reader.Metadata = new ProcessMetadata
            {
                ProcessId = 99999, ProcessName = "SyntheticPoE2.exe", FileVersion = "0.2.0.0",
                ModuleBase = Reader.MainModuleBase, ModuleMemorySize = Reader.MainModuleSize,
                AttachedTimeUtc = DateTime.UtcNow
            };
            _module = new byte[allocation];
            BinaryPrimitives.WriteUInt16LittleEndian(_module, 0x5A4D);
            BinaryPrimitives.WriteInt32LittleEndian(_module.AsSpan(0x3C), 0x80);
            BinaryPrimitives.WriteUInt32LittleEndian(_module.AsSpan(0x80), 0x00004550);
            BinaryPrimitives.WriteUInt16LittleEndian(_module.AsSpan(0x86), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(_module.AsSpan(0x94), 0xE0);
            int section = 0x80 + 24 + 0xE0;
            BinaryPrimitives.WriteUInt32LittleEndian(_module.AsSpan(section + 8), (uint)sectionLength);
            BinaryPrimitives.WriteUInt32LittleEndian(_module.AsSpan(section + 12), 0x200);
            BinaryPrimitives.WriteUInt32LittleEndian(_module.AsSpan(section + 36), 0x60000020);
            Reader.AllocateBlockAt((ulong)_base, allocation);
            Flush();
        }

        public long Plant(int matchRva, int targetRva, bool valid, int? displacement = null)
        {
            Pattern.Data.CopyTo(_module, matchRva);
            int disp = displacement ?? checked(targetRva - (matchRva + Pattern.BytesToSkip + 4));
            BinaryPrimitives.WriteInt32LittleEndian(_module.AsSpan(matchRva + Pattern.BytesToSkip), disp);
            Flush();
            if (valid) MakeValid(targetRva);
            return _base + targetRva;
        }

        public void MakeValid(int targetRva)
        {
            var root = Reader.AllocateBlock(0x200);
            var inGame = Reader.AllocateBlock(0x400);
            var state = Reader.AllocateBlock(0x100);
            var current = Reader.AllocateBlock(0x20);
            var area = Reader.AllocateBlock(0x100);
            Reader.Write(new IntPtr(_base + targetRva), new GameStateStaticOffset { GameState = root });
            Reader.WritePointer(root + 0x90, inGame);
            Reader.WriteStdVector(root + 0x10, current, current + 16, current + 16);
            Reader.WritePointer(current, state);
            Reader.WritePointer(current + 8, inGame);
            Reader.WritePointer(root + 0x50, state);
            Reader.WritePointer(inGame + 0x290, area);
        }

        public RecoveryResult Run(long? current = null, IEnumerable<long>? history = null)
        {
            var session = new RecoverySession(Reader, RecoveryContextSnapshot.Create([]));
            return Od001GameStatesRecovery.Run(session, current, history);
        }

        private void Flush() => Reader.WriteBytes(new IntPtr(_base), _module[..Math.Min(_module.Length, 0x800)]);
        public void Dispose() => Reader.Dispose();
    }
}
