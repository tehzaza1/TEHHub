namespace TEHhub.OffsetDoctor.Tests;

using System.Collections.Immutable;
using System.Text.Json;
using TEHhub.OffsetDoctor.Process;
using TEHhub.OffsetDoctor.RecoveryV1;
using TEHhub.OffsetDoctor.Reporting;
using TEHhub.Offsets.Natives;

public static class Od145RecoveryTests
{
    public static void RunAll(Action<bool, string> check)
    {
        using var fixture = new Fixture();
        var before = fixture.Reader.SnapshotAllBlocks();

        // Scenario A: Both current fields valid -> PASS_CURRENT
        var current = fixture.Run(new AtlasFieldPair(0x310, 0x590));
        check(current.Decision.TerminalResult == RecoveryTerminalResult.PASS_CURRENT &&
            current.Decision.ProposedFieldPair == new AtlasFieldPair(0x310, 0x590) &&
            current.Discovery.Scan is null,
            "Scenario A: Both current fields valid yields PASS_CURRENT before blind discovery.");
        check(fixture.Reader.MemoryMatchesSnapshot(before) && !current.Applied,
            "Current validation is read-only and Applied is false.");

        // Scenario B: Both fields moved -> PROPOSED
        fixture.MoveGridOffset(0x340);
        fixture.MoveConnOffset(0x5C0);
        var moved = fixture.Run(new AtlasFieldPair(0x310, 0x590), [new AtlasFieldPair(0x310, 0x590)]);
        check(moved.Decision.TerminalResult == RecoveryTerminalResult.PROPOSED &&
            moved.Decision.ProposedFieldPair == new AtlasFieldPair(0x340, 0x5C0) &&
            moved.Decision.Proposal is null,
            "Scenario B: Both fields moved yields PROPOSED with atomic field pair (0x340, 0x5C0).");
        check(moved.Discovery.EliminationStages.All(s => s.InputCount == s.SurvivingCount + s.RejectedCount),
            "Elimination stages reconcile input with surviving and rejected counts.");

        // Exact candidate count verification
        var gridStage = moved.Discovery.EliminationStages.Single(s => s.Stage == EliminationStage.Od145GridDiscovery);
        var connStage = moved.Discovery.EliminationStages.Single(s => s.Stage == EliminationStage.Od145ConnectionsDiscovery);
        check(gridStage.InputCount == Od145AtlasLayoutRecovery.GridExpectedCandidates && gridStage.InputCount == 383,
            "Grid candidate search has exactly 383 aligned candidates.");
        check(connStage.InputCount == Od145AtlasLayoutRecovery.ConnExpectedCandidates && connStage.InputCount == 190,
            "Connections candidate search has exactly 190 aligned candidates.");

        // Scenario C: Grid and Connections move independently -> correct pair recovered
        fixture.MoveGridOffset(0x320); // Grid moved to 0x320, Conn stays at 0x5C0
        var independentMove = fixture.Run(new AtlasFieldPair(0x310, 0x590));
        check(independentMove.Decision.TerminalResult == RecoveryTerminalResult.PROPOSED &&
            independentMove.Decision.ProposedFieldPair == new AtlasFieldPair(0x320, 0x5C0),
            "Scenario C: GridPosition and Connections move independently and correct pair is rediscovered.");

        // Scenario D: Many range-plausible scalar fields, only one has true 2D spatial semantics -> Grid unique
        fixture.WriteConstantScalarField(0x280); // constant scalar across nodes
        fixture.WriteCollinearScalarField(0x400); // 1D line across nodes
        var scalarTest = fixture.Run(new AtlasFieldPair(0x310, 0x590));
        check(scalarTest.Decision.TerminalResult == RecoveryTerminalResult.PROPOSED &&
            scalarTest.Discovery.CandidateLedger.Any(c => c.Value == 0x280 &&
                c.RejectionReasons.Any(r => r.Predicate.Contains("variance") || r.Predicate.Contains("coordinates"))) &&
            scalarTest.Discovery.CandidateLedger.Any(c => c.Value == 0x400 &&
                c.RejectionReasons.Any(r => r.Predicate == "non-degenerate-2d-distribution")),
            "Scenario D: Plausible scalar fields without 2D distribution are rejected, GridPosition unique.");
        fixture.ClearField(0x280);
        fixture.ClearField(0x400);

        // Scenario E & G: Two plausible Grid fields, only one matches topology -> correct field survives
        fixture.WriteAlternative2DGridField(0x360); // valid 2D coordinates that do not match connection edges
        var twoGridTest = fixture.Run(new AtlasFieldPair(0x310, 0x590));
        check(twoGridTest.Decision.TerminalResult == RecoveryTerminalResult.PROPOSED &&
            twoGridTest.Decision.ProposedFieldPair == new AtlasFieldPair(0x320, 0x5C0) &&
            twoGridTest.Discovery.CandidateLedger.Any(c => c.DiscoveryOrigin.Contains("+0x360", StringComparison.Ordinal) &&
                c.RejectionReasons.Any(r => r.Predicate == "cross-field-topology-coherence")),
            "Scenario E & G: Two 2D Grid fields exist, but cross-field topology eliminates the non-matching one.");
        fixture.ClearField(0x360);

        // Scenario F & H: Many valid vector-shaped canvas fields, one true Atlas graph -> Connections unique
        fixture.WriteUnrelatedCanvasVector(0x500); // valid 20-byte stride vector with non-matching endpoints
        var twoConnTest = fixture.Run(new AtlasFieldPair(0x310, 0x590));
        check(twoConnTest.Decision.TerminalResult == RecoveryTerminalResult.PROPOSED &&
            twoConnTest.Decision.ProposedFieldPair == new AtlasFieldPair(0x320, 0x5C0) &&
            twoConnTest.Discovery.CandidateLedger.Any(c => c.DiscoveryOrigin.Contains("+0x500", StringComparison.Ordinal) &&
                c.RejectionReasons.Any(r => r.Predicate == "cross-field-topology-coherence")),
            "Scenario F & H: Two valid vectors exist, but cross-field topology eliminates the non-matching one.");
        fixture.ClearCanvasVector(0x500);

        // Scenario I: Two fully valid field pairs -> AMBIGUOUS
        fixture.WriteAlternative2DGridField(0x360);
        fixture.WriteMatchingCanvasVector(0x520, 0x360); // vector at 0x520 matches grid at 0x360
        var ambiguous = fixture.Run(new AtlasFieldPair(0x310, 0x590));
        check(ambiguous.Decision.TerminalResult == RecoveryTerminalResult.AMBIGUOUS &&
            ambiguous.Decision.ProposedFieldPair is null &&
            ambiguous.Decision.SurvivingFieldPairs.Length == 2 &&
            ambiguous.Decision.SurvivingFieldPairs.Contains(new AtlasFieldPair(0x320, 0x5C0)) &&
            ambiguous.Decision.SurvivingFieldPairs.Contains(new AtlasFieldPair(0x360, 0x520)),
            "Scenario I: Two fully valid field pairs yield AMBIGUOUS with both pairs listed and no preference.");
        fixture.ClearField(0x360);
        fixture.ClearCanvasVector(0x520);

        // Scenario J: Complete scan, no valid pair -> NOT_FOUND
        fixture.CorruptAllEdges(0x5C0);
        var notFound = fixture.Run(new AtlasFieldPair(0x310, 0x590));
        check(notFound.Decision.TerminalResult == RecoveryTerminalResult.NOT_FOUND &&
            notFound.Decision.ProposedFieldPair is null,
            "Scenario J: Complete scan with no matching edges yields NOT_FOUND.");
        fixture.RestoreEdges(0x5C0);

        // Scenario K: Atlas context unavailable -> BLOCKED_CONTEXT
        var blockedCtx = fixture.Run(new AtlasFieldPair(0x310, 0x590), includeContext: false);
        check(blockedCtx.Decision.TerminalResult == RecoveryTerminalResult.BLOCKED_CONTEXT,
            "Scenario K: Missing Atlas context is BLOCKED_CONTEXT.");

        // Scenario L: Too few nodes -> BLOCKED_CONTEXT
        fixture.SetTrustedNodeCount(10); // less than MinAtlasNodes (16)
        var tooFewNodes = fixture.Run(new AtlasFieldPair(0x310, 0x590));
        check(tooFewNodes.Decision.TerminalResult == RecoveryTerminalResult.BLOCKED_CONTEXT &&
            tooFewNodes.Detail!.Contains("Insufficient Atlas node population", StringComparison.Ordinal),
            "Scenario L: Insufficient node population (10 < 16) is BLOCKED_CONTEXT.");
        fixture.RestoreTrustedNodeCount();

        // Scenario M: Atlas dependency invalid -> BLOCKED_DEPENDENCY
        check(fixture.Run(new AtlasFieldPair(0x310, 0x590), trustGameUi: false).Decision.TerminalResult == RecoveryTerminalResult.BLOCKED_DEPENDENCY,
            "Scenario M: Untrusted GameUi blocks dependency chain.");
        check(fixture.Run(new AtlasFieldPair(0x310, 0x590), trustCanvas: false).Decision.TerminalResult == RecoveryTerminalResult.BLOCKED_DEPENDENCY,
            "Scenario M: Untrusted Atlas canvas blocks dependency chain.");
        fixture.BreakUiRootLink();
        check(fixture.Run(new AtlasFieldPair(0x310, 0x590)).Decision.TerminalResult == RecoveryTerminalResult.BLOCKED_DEPENDENCY,
            "Scenario M: Broken InGameState-to-UiRoot pointer blocks dependency chain.");
        fixture.RestoreUiRootLink();

        // Scenario N: Incomplete scan/read -> ERROR
        fixture.FreeNodeBlock(5);
        var errorRead = fixture.Run(new AtlasFieldPair(0x310, 0x590));
        check(errorRead.Decision.TerminalResult == RecoveryTerminalResult.ERROR,
            "Scenario N: Incomplete node memory read returns ERROR.");
        fixture.RestoreNodeBlock(5);

        // Scenario O & P: Historical Grid/Conn offsets arbitrarily changed -> identical discovery & FrozenDecision
        var baselineMoved = fixture.Run(new AtlasFieldPair(0x310, 0x590), [new AtlasFieldPair(0x310, 0x590)]);
        var changedHistory = fixture.Run(new AtlasFieldPair(0x888, 0x999), [new AtlasFieldPair(0x111, 0x222), new AtlasFieldPair(0x444, 0x555)]);
        check(baselineMoved.Decision.EvidenceDigest == changedHistory.Decision.EvidenceDigest &&
            baselineMoved.Decision.ProposedFieldPair == changedHistory.Decision.ProposedFieldPair &&
            JsonSerializer.Serialize(baselineMoved.Discovery) == JsonSerializer.Serialize(changedHistory.Discovery) &&
            baselineMoved.FieldPairComparison != changedHistory.FieldPairComparison,
            "Scenario O & P: Arbitrary historical Grid and Connections offsets cannot affect blind discovery or FrozenDecision.");

        // Scenario Q: Historical offsets point at readable false fields -> no preference
        fixture.WriteCollinearScalarField(0x310);
        fixture.WriteUnrelatedCanvasVector(0x590);
        var falseHistorical = fixture.Run(new AtlasFieldPair(0x310, 0x590), [new AtlasFieldPair(0x310, 0x590)]);
        check(falseHistorical.Decision.TerminalResult == RecoveryTerminalResult.PROPOSED &&
            falseHistorical.Decision.ProposedFieldPair == new AtlasFieldPair(0x320, 0x5C0) &&
            falseHistorical.Discovery.CandidateLedger.Any(c => c.DiscoveryOrigin.Contains("+0x310", StringComparison.Ordinal) && c.Disposition == CandidateDisposition.Rejected) &&
            falseHistorical.Discovery.CandidateLedger.Any(c => c.DiscoveryOrigin.Contains("+0x590", StringComparison.Ordinal) && c.Disposition == CandidateDisposition.Rejected),
            "Scenario Q: Readable false fields at historical offsets are not preferred and are rejected.");
        fixture.ClearField(0x310);
        fixture.ClearCanvasVector(0x590);

        // Scenario R: Duplicate observations -> raw ledger retained + explicit equivalence
        var duplicate = fixture.Run(new AtlasFieldPair(0x310, 0x590), passes: 2);
        check(duplicate.Decision.TerminalResult == RecoveryTerminalResult.PROPOSED &&
            duplicate.Discovery.CandidateLedger.Count(c => c.Id.Contains("-pair-", StringComparison.Ordinal)) == 2 &&
            duplicate.Discovery.CandidateLedger.Count(c => c.Disposition == CandidateDisposition.Equivalent) == 1,
            "Scenario R: Duplicate observations retain raw candidates with explicit equivalence in stage.");

        // Scenario S: Valid StdVector header but random/non-Atlas contents -> rejected
        fixture.WriteInvalidEdgeEndpointsVector(0x580);
        var invalidEndpoints = fixture.Run(new AtlasFieldPair(0x310, 0x590));
        check(invalidEndpoints.Discovery.CandidateLedger.Any(c => c.DiscoveryOrigin.Contains("+0x580", StringComparison.Ordinal) &&
            c.RejectionReasons.Any(r => r.Predicate == "plausible-endpoints")),
            "Scenario S: Vector with loop or invalid endpoints is rejected in validation.");
        fixture.ClearCanvasVector(0x580);

        // Scenario T: Plausible Vector2i numeric values but degenerate distribution -> rejected
        fixture.WriteDegenerateDiagonalField(0x380);
        var degenerateGrid = fixture.Run(new AtlasFieldPair(0x310, 0x590));
        check(degenerateGrid.Discovery.CandidateLedger.Any(c => c.DiscoveryOrigin.Contains("+0x380", StringComparison.Ordinal) &&
            c.RejectionReasons.Any(r => r.Predicate == "non-degenerate-2d-distribution")),
            "Scenario T: Plausible coordinates with degenerate 1D diagonal distribution are rejected.");
        fixture.ClearField(0x380);

        // Scenario U: Context changes during run -> BLOCKED_CONTEXT
        var changedContext = fixture.Run(new AtlasFieldPair(0x310, 0x590), passes: 2, beforeObservation: (session, pass) =>
        {
            if (pass == 1) session.ReplaceContext(RecoveryContextSnapshot.Create([]));
        });
        check(changedContext.Decision.TerminalResult == RecoveryTerminalResult.BLOCKED_CONTEXT,
            "Scenario U: Context loss during traversal returns BLOCKED_CONTEXT.");

        // Scenario V: Cross-field topology rejects plausible but mismatched pair -> rejected
        fixture.WriteAlternative2DGridField(0x390);
        var mismatchedTopology = fixture.Run(new AtlasFieldPair(0x310, 0x590));
        check(mismatchedTopology.Discovery.CandidateLedger.Any(c => c.DiscoveryOrigin.Contains("+0x390", StringComparison.Ordinal) &&
            c.RejectionReasons.Any(r => r.Predicate == "cross-field-topology-coherence")),
            "Scenario V: Plausible 2D field with mismatched topology is rejected by cross-field validation.");
        fixture.ClearField(0x390);

        // Scenario W: 24-byte vector HEADER with 20-byte EDGE elements -> PASS
        check(Od145AtlasLayoutRecovery.StdVectorHeaderSize == 24 &&
            Od145AtlasLayoutRecovery.EdgeElementStride == 20,
            "Scenario W: StdVector header size is 24 bytes and edge element stride is 20 bytes.");

        // Scenario X: Treating edge stride as 24 -> FAIL / rejected
        fixture.WriteVectorWith24ByteStride(0x570);
        var stride24Candidate = fixture.Run(new AtlasFieldPair(0x310, 0x590));
        check(stride24Candidate.Discovery.CandidateLedger.Any(c => c.DiscoveryOrigin.Contains("+0x570", StringComparison.Ordinal) &&
            c.RejectionReasons.Any(r => r.Predicate == "edge-element-stride-0x14")),
            "Scenario X: Vector elements of stride 24 fail the required 20-byte stride check.");
        fixture.ClearCanvasVector(0x570);

        // Budget exhaustion regression test
        fixture.SetMalformedCanvasVector();
        var budgetErr = fixture.Run(new AtlasFieldPair(0x310, 0x590));
        check(budgetErr.Decision.TerminalResult == RecoveryTerminalResult.ERROR,
            "Budget exhaustion or malformed canvas child vector returns ERROR.");
        fixture.RestoreCanvasVector();

        // Reporting verification
        using var jsonDoc = JsonDocument.Parse(RecoveryJsonReportExporter.Serialize(new RecoveryReport(
            "recovery-v1.phase6", fixture.RunIdentity, DateTime.UtcNow, [baselineMoved, ambiguous])));
        check(jsonDoc.RootElement.GetProperty("Results")[0].GetProperty("Applied").GetBoolean() == false,
            "JSON report confirms Applied=false.");

        using var writer = new StringWriter();
        RecoveryConsoleReportWriter.Write(writer, new RecoveryReport("recovery-v1.phase6",
            fixture.RunIdentity, DateTime.UtcNow, [baselineMoved, ambiguous]));
        string consoleOutput = writer.ToString();
        check(consoleOutput.Contains("proposed field pair:", StringComparison.Ordinal) &&
            consoleOutput.Contains("surviving field pair:", StringComparison.Ordinal) &&
            consoleOutput.Contains("post-result field pair comparison:", StringComparison.Ordinal),
            "Console report formats OD-145 field pairs and post-result comparison.");

        Console.WriteLine("[OD-145] Synthetic scenarios A-X passed.\n");
    }

    private sealed class Fixture : IDisposable
    {
        public const int NodeCount = 64;
        public SyntheticMemoryReader Reader { get; } = new();
        public IntPtr Canvas { get; }
        private readonly IntPtr _inGame;
        private readonly IntPtr _uiRoot;
        private readonly IntPtr _gameUi;
        private readonly IntPtr _canvasChildrenVector;
        private readonly IntPtr[] _nodes = new IntPtr[NodeCount];
        private readonly (int X, int Y)[] _coords = new (int X, int Y)[NodeCount];
        private IntPtr _edgesBlock;
        private int _currentGridOffset = 0x310;
        private int _currentConnOffset = 0x590;
        public RecoveryIdentity RunIdentity => Session(true).Identity;

        public Fixture()
        {
            Canvas = Reader.AllocateBlock(0x900);
            Reader.WritePointer(Canvas + 8, Canvas); // Self
            Reader.Write(Canvas + 0x168, 0x00462EF1u); // flags

            _inGame = Reader.AllocateBlock(0x400);
            _uiRoot = Reader.AllocateBlock(0xC00);
            _gameUi = Reader.AllocateBlock(0x500);

            Reader.WritePointer(_gameUi + 8, _gameUi);
            Reader.Write(Canvas + 0xB8, _gameUi); // Parent of canvas

            RestoreUiRootLink();
            Reader.WritePointer(_uiRoot + 0xBE0, _gameUi);

            // Allocate child vector for canvas
            _canvasChildrenVector = Reader.AllocateBlock(NodeCount * 8);
            Reader.WriteStdVector(Canvas + 0x10, _canvasChildrenVector,
                _canvasChildrenVector + NodeCount * 8, _canvasChildrenVector + NodeCount * 8);

            // Create 64 nodes
            for (int i = 0; i < NodeCount; i++)
            {
                _nodes[i] = Reader.AllocateBlock(0x900);
                Reader.WritePointer(_nodes[i] + 8, _nodes[i]); // Self
                Reader.WritePointer(_nodes[i] + 0xB8, Canvas); // Parent = Canvas
                Reader.Write(_nodes[i] + 0x168, Od145AtlasLayoutRecovery.AtlasMapNodeFp); // Flags

                // Distinct non-collinear realistic coordinates
                _coords[i] = (1000 + i * 45, 2000 + ((i * 73) % 800) - 400);
                Reader.Write(_nodes[i] + _currentGridOffset, new StdTuple2D<int>(_coords[i].X, _coords[i].Y));
                Reader.WritePointer(_canvasChildrenVector + i * 8, _nodes[i]);
            }

            // Create connection edges connecting node i to node i+1
            WriteEdges(_currentConnOffset);
        }

        private void WriteEdges(int offset)
        {
            int edgeCount = NodeCount - 1; // 63 edges
            _edgesBlock = Reader.AllocateBlock(edgeCount * 20);
            for (int i = 0; i < edgeCount; i++)
            {
                var edge = new Od145AtlasLayoutRecovery.AtlasConnectionEdge
                {
                    Unknown = 0,
                    SourceX = _coords[i].X,
                    SourceY = _coords[i].Y,
                    TargetX = _coords[i + 1].X,
                    TargetY = _coords[i + 1].Y
                };
                Reader.Write(_edgesBlock + i * 20, edge);
            }
            Reader.WriteStdVector(Canvas + offset, _edgesBlock, _edgesBlock + edgeCount * 20, _edgesBlock + edgeCount * 20);
        }

        public void MoveGridOffset(int newOffset)
        {
            int oldOffset = _currentGridOffset;
            _currentGridOffset = newOffset;
            for (int i = 0; i < NodeCount; i++)
            {
                if (oldOffset != newOffset)
                {
                    Reader.Write(_nodes[i] + oldOffset, new StdTuple2D<int>(0, 0));
                }
                Reader.Write(_nodes[i] + newOffset, new StdTuple2D<int>(_coords[i].X, _coords[i].Y));
            }
        }

        public void MoveConnOffset(int newOffset)
        {
            int oldOffset = _currentConnOffset;
            _currentConnOffset = newOffset;
            if (oldOffset != newOffset)
            {
                Reader.WriteStdVector(Canvas + oldOffset, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            }
            WriteEdges(newOffset);
        }

        public void WriteConstantScalarField(int offset)
        {
            for (int i = 0; i < NodeCount; i++)
            {
                Reader.Write(_nodes[i] + offset, new StdTuple2D<int>(42, 42));
            }
        }

        public void WriteCollinearScalarField(int offset)
        {
            for (int i = 0; i < NodeCount; i++)
            {
                // Degenerate 1D line: X == Y
                Reader.Write(_nodes[i] + offset, new StdTuple2D<int>(100 + i, 100 + i));
            }
        }

        public void WriteDegenerateDiagonalField(int offset)
        {
            for (int i = 0; i < NodeCount; i++)
            {
                Reader.Write(_nodes[i] + offset, new StdTuple2D<int>(500 + i * 10, 500 + i * 10));
            }
        }

        public void WriteAlternative2DGridField(int offset)
        {
            for (int i = 0; i < NodeCount; i++)
            {
                // High-diversity 2D distribution that does not match the edges
                Reader.Write(_nodes[i] + offset, new StdTuple2D<int>(-5000 - i * 50, -6000 - ((i * 31) % 400)));
            }
        }

        public void WriteUnrelatedCanvasVector(int offset)
        {
            int count = 10;
            var block = Reader.AllocateBlock(count * 20);
            for (int i = 0; i < count; i++)
            {
                var edge = new Od145AtlasLayoutRecovery.AtlasConnectionEdge
                {
                    Unknown = 0,
                    SourceX = 99990 + i,
                    SourceY = 88880 + i,
                    TargetX = 77770 + i,
                    TargetY = 66660 + i
                };
                Reader.Write(block + i * 20, edge);
            }
            Reader.WriteStdVector(Canvas + offset, block, block + count * 20, block + count * 20);
        }

        public void WriteMatchingCanvasVector(int connOffset, int gridOffset)
        {
            int count = 20;
            var block = Reader.AllocateBlock(count * 20);
            for (int i = 0; i < count; i++)
            {
                Reader.TryRead(_nodes[i] + gridOffset, out StdTuple2D<int> s);
                Reader.TryRead(_nodes[i + 1] + gridOffset, out StdTuple2D<int> t);
                var edge = new Od145AtlasLayoutRecovery.AtlasConnectionEdge
                {
                    Unknown = 0,
                    SourceX = s.X,
                    SourceY = s.Y,
                    TargetX = t.X,
                    TargetY = t.Y
                };
                Reader.Write(block + i * 20, edge);
            }
            Reader.WriteStdVector(Canvas + connOffset, block, block + count * 20, block + count * 20);
        }

        public void WriteInvalidEdgeEndpointsVector(int offset)
        {
            int count = 10;
            var block = Reader.AllocateBlock(count * 20);
            for (int i = 0; i < count; i++)
            {
                var edge = new Od145AtlasLayoutRecovery.AtlasConnectionEdge
                {
                    Unknown = 0,
                    SourceX = 500,
                    SourceY = 500,
                    TargetX = 500,
                    TargetY = 500 // Loop
                };
                Reader.Write(block + i * 20, edge);
            }
            Reader.WriteStdVector(Canvas + offset, block, block + count * 20, block + count * 20);
        }

        public void WriteVectorWith24ByteStride(int offset)
        {
            // Test a length not divisible by 20: 24 * 3 = 72 bytes
            var block = Reader.AllocateBlock(72);
            Reader.WriteStdVector(Canvas + offset, block, block + 72, block + 72);
        }

        public void CorruptAllEdges(int offset)
        {
            int edgeCount = NodeCount - 1;
            for (int i = 0; i < edgeCount; i++)
            {
                var edge = new Od145AtlasLayoutRecovery.AtlasConnectionEdge
                {
                    Unknown = 0,
                    SourceX = 9999999,
                    SourceY = 9999999,
                    TargetX = 9999999,
                    TargetY = 9999999
                };
                Reader.Write(_edgesBlock + i * 20, edge);
            }
        }

        public void RestoreEdges(int offset)
        {
            WriteEdges(offset);
        }

        public void ClearField(int offset)
        {
            for (int i = 0; i < NodeCount; i++)
            {
                Reader.Write(_nodes[i] + offset, 0L);
            }
        }

        public void ClearCanvasVector(int offset)
        {
            Reader.WriteStdVector(Canvas + offset, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        }

        public void SetTrustedNodeCount(int count)
        {
            Reader.WriteStdVector(Canvas + 0x10, _canvasChildrenVector,
                _canvasChildrenVector + count * 8, _canvasChildrenVector + NodeCount * 8);
        }

        public void RestoreTrustedNodeCount()
        {
            Reader.WriteStdVector(Canvas + 0x10, _canvasChildrenVector,
                _canvasChildrenVector + NodeCount * 8, _canvasChildrenVector + NodeCount * 8);
        }

        public void FreeNodeBlock(int index) => Reader.FreeBlock(_nodes[index]);

        public void RestoreNodeBlock(int index)
        {
            _nodes[index] = Reader.AllocateBlock(0x900);
            Reader.WritePointer(_nodes[index] + 8, _nodes[index]);
            Reader.WritePointer(_nodes[index] + 0xB8, Canvas);
            Reader.Write(_nodes[index] + 0x168, Od145AtlasLayoutRecovery.AtlasMapNodeFp);
            Reader.Write(_nodes[index] + _currentGridOffset, new StdTuple2D<int>(_coords[index].X, _coords[index].Y));
            Reader.WritePointer(_canvasChildrenVector + index * 8, _nodes[index]);
        }

        public void BreakUiRootLink() => Reader.WritePointer(_inGame + 0x2F0, IntPtr.Zero);
        public void RestoreUiRootLink() => Reader.WritePointer(_inGame + 0x2F0, _uiRoot);

        public void SetMalformedCanvasVector() =>
            Reader.WritePointer(Canvas + 0x18, new IntPtr(0x10000));

        public void RestoreCanvasVector() =>
            Reader.WriteStdVector(Canvas + 0x10, _canvasChildrenVector,
                _canvasChildrenVector + NodeCount * 8, _canvasChildrenVector + NodeCount * 8);

        private RecoverySession Session(bool includeContext) => new(Reader,
            RecoveryContextSnapshot.Create(includeContext
                ? [new("atlas-ui-context", "true")]
                : []));

        public RecoveryResult Run(AtlasFieldPair? current = null,
            IEnumerable<AtlasFieldPair>? history = null, bool includeContext = true,
            bool trustCanvas = true, bool trustGameUi = true, int passes = 1,
            Action<RecoverySession, int>? beforeObservation = null)
        {
            var session = Session(includeContext);
            foreach (string id in new[] { "OD-001", "OD-007", "OD-010", "OD-014", "OD-020", "OD-134" })
            {
                if (id == "OD-020" && !trustGameUi) continue;
                if (id == "OD-134" && !trustCanvas) continue;
                Trust(session, id, id switch
                {
                    "OD-010" => _inGame,
                    "OD-014" => _uiRoot,
                    "OD-020" => _gameUi,
                    "OD-134" => Canvas,
                    _ => Canvas
                });
            }
            return Od145AtlasLayoutRecovery.Run(session, current, history, passes,
                pass => beforeObservation?.Invoke(session, pass));
        }

        private static void Trust(RecoverySession session, string id, IntPtr address)
        {
            var target = RecoveryTargetSpec.Create(id, new RecoveryTargetScope("synthetic", id),
                "synthetic-trusted-ui", rootAddress: address.ToInt64());
            new RecoveryCoordinator().Run(session, target, new TrustedDiscovery(address.ToInt64()),
                new TrustedValidator(address.ToInt64()));
        }

        public void Dispose() => Reader.Dispose();
    }

    private sealed class TrustedDiscovery(long address) : IBlindDiscoveryStrategy
    {
        public string StrategyId => "synthetic-trusted-ui";
        public DiscoveryOutcome Discover(IRecoveryReadOnlyMemory memory, BlindDiscoveryRequest request) =>
            new([new("trusted", address, "synthetic independently validated UI anchor",
                [new("readable", RecoveryEvidenceResult.PASS, "Addressable", true, false)])], true);
    }

    private sealed class TrustedValidator(long address) : IIndependentCandidateValidator
    {
        public IndependentValidationOutcome Validate(IRecoveryReadOnlyMemory memory,
            IndependentValidationRequest request) => new([
                new("independent-ui-anchor", memory.IsValidAddress(address) && request.CandidateValue == address
                    ? RecoveryEvidenceResult.PASS : RecoveryEvidenceResult.FAIL,
                    "Synthetic trusted address", true, true)], true);
    }
}
