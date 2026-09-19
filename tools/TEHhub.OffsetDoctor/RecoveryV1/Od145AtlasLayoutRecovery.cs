namespace TEHhub.OffsetDoctor.RecoveryV1;

using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TEHhub.Offsets.Natives;
using TEHhub.Offsets.Objects.States;
using TEHhub.Offsets.Objects.UiElement;

/// <summary>
/// Versioned, observation-only multi-instance structural recovery for Atlas layout constants (OD-145).
/// GridPosition is scanned across all trusted AtlasMapNode instances.
/// Connections vector is scanned on the common Atlas canvas panel.
/// </summary>
public static class Od145AtlasLayoutRecovery
{
    public const string TargetId = "OD-145";
    public const string StrategyId = "od-145-atlas-multi-instance-structural-v1";
    public const string ScopeKey = "ImportantUiElementsAtlasLayout";
    public const string WorldMapPanelId = "OD-134";
    public const string GameUiId = "OD-020";

    // Atlas node UI layout and identity
    public const int UiElementBaseFlagsOffset = 0x168;
    public const uint IsVisibleMask = 0x800;
    public const uint AtlasMapNodeFp = 0x542EF3;
    public const uint AtlasMistNodeFp = 0x442EF3;
    public const uint MaskedAtlasMapNodeFp = AtlasMapNodeFp & ~IsVisibleMask;
    public const uint MaskedAtlasMistNodeFp = AtlasMistNodeFp & ~IsVisibleMask;

    // Search bounds and alignments
    public const int GridStartOffset = 0x200;
    public const int GridEndOffset = 0x7F8;
    public const int GridStep = 4;
    public const int GridExpectedCandidates = (GridEndOffset - GridStartOffset) / GridStep + 1; // 383

    public const int ConnStartOffset = 0x200;
    public const int ConnEndOffset = 0x7E8;
    public const int ConnStep = 8;
    public const int ConnExpectedCandidates = (ConnEndOffset - ConnStartOffset) / ConnStep + 1; // 190

    public const int StdVectorHeaderSize = 24; // 0x18: First, Last, End pointers
    public const int EdgeElementStride = 20;   // 0x14: AtlasNodeConnectionEdgeOffsets with Pack=1

    // Budgets and limits
    public const int MaxNodes = 512;
    public const int MinAtlasNodes = 16;
    public const int MaxEdges = 2048;
    public const int MinEdges = 4;
    public const int MaxReads = 65536;
    public const int MaxMilliseconds = 30000;

    private static readonly string[] Chain = ["OD-001", "OD-007", "OD-010", "OD-014", GameUiId, WorldMapPanelId];

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct AtlasConnectionEdge
    {
        public int Unknown;
        public int SourceX;
        public int SourceY;
        public int TargetX;
        public int TargetY;
    }

    public static RecoveryResult Run(
        RecoverySession session,
        AtlasFieldPair? currentConfiguredPair = null,
        IEnumerable<AtlasFieldPair>? historicalPairs = null,
        int observationPasses = 1,
        Action<int>? beforeObservation = null)
    {
        var target = RecoveryTargetSpec.Create(TargetId,
            new RecoveryTargetScope(ScopeKey, session.Identity.ProcessName), StrategyId,
            ["atlas-ui-context"], WorldMapPanelId,
            additionalDependencyIds: Chain.Take(5));
        var initial = session.Context;
        var gridCandidates = new List<MutableCandidate>();
        var connCandidates = new List<MutableCandidate>();
        var pairCandidates = new List<MutableCandidate>();
        var stages = new List<CandidateEliminationStage>();
        var dependencies = ImmutableArray<RecoveryDependencyState>.Empty;
        RecoveryCurrentValidation? currentValidation = null;
        RecoveryTerminalResult? forced = null;
        string? detail = null;
        long canvasAddress = 0;
        int nodesFound = 0;
        int reads = 0;
        long bytesScanned = 0;
        var watch = Stopwatch.StartNew();

        string? Guard() => !session.IdentityIsCurrent ? "Process/build identity became stale." :
            ContextRequirementEvaluator.CheckRequired(target, initial, session.Context);

        static RecoveryTerminalResult GuardResult(string reason) =>
            reason.StartsWith("Required context", StringComparison.Ordinal)
                ? RecoveryTerminalResult.BLOCKED_CONTEXT : RecoveryTerminalResult.ERROR;

        var view = new ReadContext(session.Memory,
            () => ++reads <= MaxReads && watch.ElapsedMilliseconds <= MaxMilliseconds,
            size => bytesScanned += size);

        try
        {
            if (observationPasses is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(observationPasses));
            detail = Guard();
            if (detail is not null) forced = GuardResult(detail);

            if (forced is null && !DependencyEvaluator.TryResolve(session, target,
                    out canvasAddress, out dependencies, out detail))
                forced = RecoveryTerminalResult.BLOCKED_DEPENDENCY;

            if (forced is null && (canvasAddress < 0x10000 || !session.Memory.IsValidAddress(canvasAddress)))
            {
                forced = RecoveryTerminalResult.BLOCKED_DEPENDENCY;
                detail = "Trusted Atlas canvas anchor is not addressable.";
            }

            if (forced is null)
            {
                // Verify UI chain integrity
                long inGame = dependencies.Single(d => d.TargetId == "OD-010").Anchor!.Value;
                long uiRoot = dependencies.Single(d => d.TargetId == "OD-014").Anchor!.Value;
                long gameUi = dependencies.Single(d => d.TargetId == GameUiId).Anchor!.Value;
                long uiRootField = Marshal.OffsetOf<InGameStateOffset>(nameof(InGameStateOffset.UiRootStructPtr)).ToInt64();
                long gameUiField = Marshal.OffsetOf<UiRootStruct>(nameof(UiRootStruct.GameUiPtr)).ToInt64();

                if (inGame < 0x10000 || uiRoot < 0x10000 || gameUi < 0x10000 ||
                    !session.Memory.TryRead(inGame + uiRootField, out long observedUiRoot) ||
                    !session.Memory.TryRead(uiRoot + gameUiField, out long observedGameUi) ||
                    observedUiRoot != uiRoot || observedGameUi != gameUi ||
                    !view.TryRead(canvasAddress, out UiElementBaseOffset canvasShape) ||
                    canvasShape.Self.ToInt64() != canvasAddress)
                {
                    forced = RecoveryTerminalResult.BLOCKED_DEPENDENCY;
                    detail = "InGameState, UiRoot, GameUi, and Atlas canvas trusted relationship is invalid.";
                }
            }

            var trustedNodes = new List<long>();
            if (forced is null)
            {
                CollectTrustedNodes(view, canvasAddress, trustedNodes);
                nodesFound = trustedNodes.Count;
                if (trustedNodes.Count < MinAtlasNodes)
                {
                    forced = RecoveryTerminalResult.BLOCKED_CONTEXT;
                    detail = $"Insufficient Atlas node population: {trustedNodes.Count} < {MinAtlasNodes}.";
                }
            }

            // Step 1: Validate Current Configured Field Pair First
            if (forced is null && currentConfiguredPair is not null)
            {
                var curEvidence = ImmutableArray.CreateBuilder<RecoveryEvidenceRecord>();
                bool gridOk = ValidateGridField(view, trustedNodes, currentConfiguredPair.GridPositionOffset, curEvidence);
                bool connOk = ValidateConnField(view, canvasAddress, currentConfiguredPair.ConnectionsOffset, curEvidence);
                bool topologyOk = false;
                if (gridOk && connOk)
                {
                    topologyOk = ValidateTopologyCoherence(view, trustedNodes, currentConfiguredPair.GridPositionOffset,
                        canvasAddress, currentConfiguredPair.ConnectionsOffset, curEvidence);
                }

                currentValidation = new RecoveryCurrentValidation(0, curEvidence.ToImmutable(), true)
                {
                    FieldPair = currentConfiguredPair
                };

                if (gridOk && connOk && topologyOk)
                {
                    forced = RecoveryTerminalResult.PASS_CURRENT;
                }

                if (Guard() is string guard) { forced = GuardResult(guard); detail = guard; }
            }

            // Step 2: Blind Rediscovery if not passed
            if (forced is null)
            {
                reads = 0;
                bytesScanned = 0;
                watch.Restart();

                for (int pass = 0; pass < observationPasses; pass++)
                {
                    beforeObservation?.Invoke(pass);
                    if (Guard() is string guard) { forced = GuardResult(guard); detail = guard; break; }

                    trustedNodes.Clear();
                    CollectTrustedNodes(view, canvasAddress, trustedNodes);
                    nodesFound = trustedNodes.Count;
                    if (trustedNodes.Count < MinAtlasNodes)
                    {
                        forced = RecoveryTerminalResult.BLOCKED_CONTEXT;
                        detail = $"Insufficient Atlas node population: {trustedNodes.Count} < {MinAtlasNodes}.";
                        break;
                    }

                    // Scan GridPosition across node instances
                    var passGridCandidates = new List<MutableCandidate>();
                    for (int offset = GridStartOffset; offset <= GridEndOffset; offset += GridStep)
                    {
                        var c = new MutableCandidate($"observation-{pass:D2}-grid-0x{offset:X3}", offset,
                            $"AtlasMapNode field +0x{offset:X3} (per-node scan across {trustedNodes.Count} nodes)",
                            pass, CandidateKind.Grid);
                        passGridCandidates.Add(c);
                        gridCandidates.Add(c);

                        // Discovery filters
                        EvaluateGridDiscovery(view, trustedNodes, offset, c);
                        if (!c.Rejected)
                        {
                            EvaluateGridValidation(view, trustedNodes, offset, c);
                        }
                    }

                    // Scan Connections vector on the canvas panel
                    var passConnCandidates = new List<MutableCandidate>();
                    for (int offset = ConnStartOffset; offset <= ConnEndOffset; offset += ConnStep)
                    {
                        var c = new MutableCandidate($"observation-{pass:D2}-conn-0x{offset:X3}", offset,
                            $"Atlas canvas field +0x{offset:X3} (StdVector scan on canvas 0x{canvasAddress:X})",
                            pass, CandidateKind.Connection);
                        passConnCandidates.Add(c);
                        connCandidates.Add(c);

                        // Discovery filters
                        EvaluateConnDiscovery(view, canvasAddress, offset, c);
                        if (!c.Rejected)
                        {
                            EvaluateConnValidation(view, canvasAddress, offset, c);
                        }
                    }

                    // Cross-field topology validation
                    var survivingGrid = passGridCandidates.Where(g => !g.Rejected).ToList();
                    var survivingConn = passConnCandidates.Where(c => !c.Rejected).ToList();

                    foreach (var g in survivingGrid)
                    {
                        foreach (var c in survivingConn)
                        {
                            var pair = new AtlasFieldPair(g.Value, c.Value);
                            long pairKey = (g.Value << 32) | (c.Value & 0xFFFFFFFFL);
                            var p = new MutableCandidate($"observation-{pass:D2}-pair-grid-0x{g.Value:X3}-conn-0x{c.Value:X3}",
                                pairKey, $"Atlas field pair {pair} cross-field topology", pass, CandidateKind.FieldPair)
                            {
                                FieldPair = pair
                            };
                            pairCandidates.Add(p);

                            EvaluateCrossFieldTopology(view, trustedNodes, g.Value, canvasAddress, c.Value, p);
                        }
                    }

                    if (Guard() is string changed) { forced = GuardResult(changed); detail = changed; break; }
                }

                if (forced is null)
                {
                    // Elimination stage 1: Grid Discovery
                    ApplyEliminationStage(stages, EliminationStage.Od145GridDiscovery, gridCandidates,
                        c => c.Kind == CandidateKind.Grid, "Complete read, coordinate domain [-0x80000, 0x80000], distinct pairs, X/Y variance.");

                    // Elimination stage 2: Grid Independent Validation
                    var gridSurvivors = gridCandidates.Where(c => c.Kind == CandidateKind.Grid && !c.Rejected).ToArray();
                    ApplyEliminationStage(stages, EliminationStage.Od145GridValidation, gridSurvivors,
                        _ => true, "Coordinate diversity >= 75%, non-degenerate 2D distribution.");

                    // Elimination stage 3: Connections Discovery
                    ApplyEliminationStage(stages, EliminationStage.Od145ConnectionsDiscovery, connCandidates,
                        c => c.Kind == CandidateKind.Connection, "Valid StdVector header, First <= Last <= End, 20-byte stride, 4..2048 elements.");

                    // Elimination stage 4: Connections Independent Validation
                    var connSurvivors = connCandidates.Where(c => c.Kind == CandidateKind.Connection && !c.Rejected).ToArray();
                    ApplyEliminationStage(stages, EliminationStage.Od145ConnectionsValidation, connSurvivors,
                        _ => true, "Structural edge read, valid non-loop endpoints, non-random topology.");

                    // Elimination stage 5: Cross-Field Topology Validation
                    ApplyEliminationStage(stages, EliminationStage.Od145CrossFieldValidation, pairCandidates,
                        c => c.Kind == CandidateKind.FieldPair, "Edge Source/Target endpoints match discovered Atlas node coordinates with >= 60% participation.");

                    // Elimination stage 6: Field-pair Equivalence
                    var pairSurvivors = pairCandidates.Where(c => !c.Rejected).ToArray();
                    var seenPairs = new Dictionary<AtlasFieldPair, string>();
                    foreach (var p in pairSurvivors)
                    {
                        if (seenPairs.TryGetValue(p.FieldPair!, out var firstId))
                        {
                            p.MarkEquivalent(firstId);
                        }
                        else
                        {
                            seenPairs.Add(p.FieldPair!, p.Id);
                        }
                    }
                    ApplyEliminationStage(stages, EliminationStage.Od145FieldPairEquivalence, pairSurvivors,
                        _ => true, "Exact field pair (GridOffset, ConnOffset); first observation represents equivalent instances.");

                    if (Guard() is string guard) { forced = GuardResult(guard); detail = guard; }
                }
            }
        }
        catch (IncompleteUiException ex)
        {
            forced = RecoveryTerminalResult.ERROR;
            detail = ex.Message;
        }
        catch (Exception ex)
        {
            forced = RecoveryTerminalResult.ERROR;
            detail = ex.Message;
        }

        // Decision logic
        var survivingPairs = forced is null
            ? pairCandidates.Where(c => !c.Rejected && !c.IsEquivalent).ToArray()
            : [];

        var terminal = forced ?? (survivingPairs.Length switch
        {
            0 => RecoveryTerminalResult.NOT_FOUND,
            1 => RecoveryTerminalResult.PROPOSED,
            _ => RecoveryTerminalResult.AMBIGUOUS
        });

        foreach (var p in survivingPairs) p.Disposition = CandidateDisposition.Survivor;

        var allCandidates = gridCandidates.Concat(connCandidates).Concat(pairCandidates).ToList();
        var survivorIds = survivingPairs.Select(c => c.Id).ToImmutableArray();
        var survivingFieldPairs = terminal == RecoveryTerminalResult.PASS_CURRENT && currentConfiguredPair is not null
            ? [currentConfiguredPair]
            : survivingPairs.Select(c => c.FieldPair!).ToImmutableArray();
        AtlasFieldPair? proposedPair = terminal switch
        {
            RecoveryTerminalResult.PROPOSED => survivingFieldPairs.Length > 0 ? survivingFieldPairs[0] : null,
            RecoveryTerminalResult.PASS_CURRENT => currentConfiguredPair,
            _ => null
        };

        var frozenLedger = allCandidates.Select(c => c.Freeze()).ToImmutableArray();
        var scan = allCandidates.Count == 0 ? null : new DiscoveryScanEvidence(bytesScanned, allCandidates.Count,
            [$"trusted Atlas canvas=0x{canvasAddress:X}; nodes={nodesFound}; grid-candidates={GridExpectedCandidates}; conn-candidates={ConnExpectedCandidates}; max-reads={MaxReads}; max-ms={MaxMilliseconds}"]);
        var discovery = new FrozenDiscoveryResult(frozenLedger, stages.ToImmutableArray(), survivorIds, scan);

        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new { target.Id, terminal, survivorIds, proposedPair, stages }))));

        var decision = new FrozenDecision(terminal, survivorIds, null, digest)
        {
            ProposedFieldPair = proposedPair,
            SurvivingFieldPairs = survivingFieldPairs
        };

        // Post-decision historical comparison
        var history = (historicalPairs ?? []).ToImmutableArray();
        var comparison = currentConfiguredPair is null && history.IsEmpty ? null :
            new AtlasFieldPairComparison(
                currentConfiguredPair,
                history,
                proposedPair is null || currentConfiguredPair is null ? null : proposedPair == currentConfiguredPair,
                proposedPair is null || history.IsEmpty ? null : history.Contains(proposedPair));

        var facts = initial.Facts
            .SetItem("ui-root-provenance", string.Join(" -> ", Chain))
            .SetItem("trusted-atlas-canvas", canvasAddress == 0 ? "unavailable" : $"0x{canvasAddress:X}")
            .SetItem("nodes-inspected", nodesFound.ToString())
            .SetItem("read-count", reads.ToString())
            .SetItem("grid-scan-range", $"0x{GridStartOffset:X}..0x{GridEndOffset:X}; step={GridStep}; candidates={GridExpectedCandidates}")
            .SetItem("conn-scan-range", $"0x{ConnStartOffset:X}..0x{ConnEndOffset:X}; step={ConnStep}; candidates={ConnExpectedCandidates}")
            .SetItem("edge-element-stride", $"{EdgeElementStride} bytes (0x{EdgeElementStride:X}); Pack=1; header={StdVectorHeaderSize} bytes")
            .SetItem("traversal-budgets", $"nodes={MaxNodes},min-nodes={MinAtlasNodes},min-edges={MinEdges},max-edges={MaxEdges},reads={MaxReads},ms={MaxMilliseconds}");

        var result = new RecoveryResult(target, decision, discovery, currentValidation,
            dependencies, facts, null, detail)
        {
            FieldPairComparison = comparison
        };

        session.Record(result);
        return result;
    }

    private static void CollectTrustedNodes(ReadContext view, long canvasAddress, List<long> trustedNodes)
    {
        if (!view.TryRead(canvasAddress, out UiElementBaseOffset canvas))
            throw new IncompleteUiException("Cannot read Atlas canvas base.");
        if (canvas.Self.ToInt64() != canvasAddress)
            throw new IncompleteUiException("Atlas canvas Self invariant failed.");

        long first = canvas.ChildrensPtr.First.ToInt64();
        long last = canvas.ChildrensPtr.Last.ToInt64();
        long end = canvas.ChildrensPtr.End.ToInt64();

        if (first < 0x10000 || last < first || end < last || (last - first) % 8 != 0)
            throw new IncompleteUiException("Atlas canvas child vector is malformed.");

        int childCount = (int)((last - first) / 8);
        if (childCount > MaxNodes)
            throw new IncompleteUiException($"Atlas canvas child count {childCount} exceeds budget {MaxNodes}.");

        for (int i = 0; i < childCount; i++)
        {
            if (!view.TryRead(first + i * 8, out long childAddr))
                throw new IncompleteUiException("Cannot read child pointer in Atlas canvas child vector.");
            if (childAddr == 0) continue;
            if (!view.TryRead(childAddr, out UiElementBaseOffset child))
                throw new IncompleteUiException($"Cannot read child UI element at 0x{childAddr:X}.");

            if (child.Self.ToInt64() != childAddr) continue;
            if (child.ParentPtr.ToInt64() != canvasAddress) continue;

            uint maskedFlags = child.Flags & ~IsVisibleMask;
            if (maskedFlags == MaskedAtlasMapNodeFp || maskedFlags == MaskedAtlasMistNodeFp)
            {
                trustedNodes.Add(childAddr);
            }
        }
    }

    private static void EvaluateGridDiscovery(ReadContext view, List<long> nodes, int offset, MutableCandidate c)
    {
        bool complete = true;
        bool domain = true;
        var distinctCoords = new HashSet<(int X, int Y)>();
        var distinctX = new HashSet<int>();
        var distinctY = new HashSet<int>();

        foreach (long nodeAddr in nodes)
        {
            if (!view.TryRead(nodeAddr + offset, out StdTuple2D<int> coord))
            {
                complete = false;
                break;
            }
            if (coord.X is < -0x80000 or > 0x80000 || coord.Y is < -0x80000 or > 0x80000)
            {
                domain = false;
            }
            distinctCoords.Add((coord.X, coord.Y));
            distinctX.Add(coord.X);
            distinctY.Add(coord.Y);
            c.DiscoveredCoordinates[nodeAddr] = (coord.X, coord.Y);
        }

        c.AddEvidence("complete-node-reads", complete,
            $"Read {c.DiscoveredCoordinates.Count}/{nodes.Count} nodes at +0x{offset:X}.", required: true);
        if (!complete)
        {
            c.Reject(EliminationStage.Od145GridDiscovery, "complete-node-reads", "Failed to read coordinate field on all nodes.");
            return;
        }

        c.AddEvidence("plausible-coordinate-domain", domain,
            $"Coordinates within [-0x80000, 0x80000].", required: true);
        if (!domain)
        {
            c.Reject(EliminationStage.Od145GridDiscovery, "plausible-coordinate-domain", "Coordinates outside plausible Atlas domain.");
            return;
        }

        bool xVar = distinctX.Count >= 5 && (distinctX.Max() - distinctX.Min()) > 0;
        c.AddEvidence("non-trivial-x-variance", xVar,
            $"Distinct X count={distinctX.Count}; span={distinctX.Max() - distinctX.Min()}.", required: true);
        if (!xVar)
        {
            c.Reject(EliminationStage.Od145GridDiscovery, "non-trivial-x-variance", "Insufficient X coordinate variance.");
            return;
        }

        bool yVar = distinctY.Count >= 5 && (distinctY.Max() - distinctY.Min()) > 0;
        c.AddEvidence("non-trivial-y-variance", yVar,
            $"Distinct Y count={distinctY.Count}; span={distinctY.Max() - distinctY.Min()}.", required: true);
        if (!yVar)
        {
            c.Reject(EliminationStage.Od145GridDiscovery, "non-trivial-y-variance", "Insufficient Y coordinate variance.");
            return;
        }

        bool diversity = distinctCoords.Count >= (nodes.Count * 3 / 4);
        c.AddEvidence("sufficient-distinct-coordinates", diversity,
            $"Distinct coordinates {distinctCoords.Count}/{nodes.Count}.", required: true);
        if (!diversity)
        {
            c.Reject(EliminationStage.Od145GridDiscovery, "sufficient-distinct-coordinates", "Distinct coordinate ratio too low.");
        }
    }

    private static void EvaluateGridValidation(ReadContext view, List<long> nodes, int offset, MutableCandidate c)
    {
        var coords = c.DiscoveredCoordinates.Values.ToList();
        var distinctCoords = coords.ToHashSet();
        double ratio = (double)distinctCoords.Count / coords.Count;

        bool diversity = ratio >= 0.75;
        c.AddValidation("coordinate-diversity", diversity,
            $"Distinct ratio={ratio:F2} ({distinctCoords.Count}/{coords.Count}).", required: true);
        if (!diversity)
        {
            c.Reject(EliminationStage.Od145GridValidation, "coordinate-diversity", "Coordinate diversity below independent threshold.");
            return;
        }

        var xs = coords.Select(p => p.X).ToList();
        var ys = coords.Select(p => p.Y).ToList();
        int spanX = xs.Max() - xs.Min();
        int spanY = ys.Max() - ys.Min();
        bool nonDegenerate = spanX >= 10 && spanY >= 10 && !coords.All(p => p.X == p.Y) && !coords.All(p => p.X == 0) && !coords.All(p => p.Y == 0);

        c.AddValidation("non-degenerate-2d-distribution", nonDegenerate,
            $"spanX={spanX}, spanY={spanY}, non-collinear={nonDegenerate}.", required: true);
        if (!nonDegenerate)
        {
            c.Reject(EliminationStage.Od145GridValidation, "non-degenerate-2d-distribution", "Coordinates form a degenerate or 1D distribution.");
        }
    }

    private static void EvaluateConnDiscovery(ReadContext view, long canvasAddress, int offset, MutableCandidate c)
    {
        if (!view.TryRead(canvasAddress + offset, out StdVector vector))
        {
            c.AddEvidence("readable-vector-header", false, $"Failed to read StdVector header at canvas+0x{offset:X}.", required: true);
            c.Reject(EliminationStage.Od145ConnectionsDiscovery, "readable-vector-header", "StdVector header unreadable.");
            return;
        }
        c.AddEvidence("readable-vector-header", true, $"StdVector header read at canvas+0x{offset:X}.", required: true);

        long first = vector.First.ToInt64();
        long last = vector.Last.ToInt64();
        long end = vector.End.ToInt64();

        bool order = first >= 0x10000 && first <= last && last <= end;
        c.AddEvidence("vector-pointer-order", order,
            $"First=0x{first:X}, Last=0x{last:X}, End=0x{end:X}.", required: true);
        if (!order)
        {
            c.Reject(EliminationStage.Od145ConnectionsDiscovery, "vector-pointer-order", "StdVector pointers disordered or below minimum address.");
            return;
        }

        long byteLength = last - first;
        bool strideOk = byteLength % EdgeElementStride == 0;
        c.AddEvidence("edge-element-stride-0x14", strideOk,
            $"Length {byteLength} bytes, divisible by {EdgeElementStride} (0x{EdgeElementStride:X}) = {strideOk}.", required: true);
        if (!strideOk)
        {
            c.Reject(EliminationStage.Od145ConnectionsDiscovery, "edge-element-stride-0x14", $"Length {byteLength} is not a multiple of edge stride {EdgeElementStride}.");
            return;
        }

        int count = (int)(byteLength / EdgeElementStride);
        bool countOk = count >= MinEdges && count <= MaxEdges;
        c.AddEvidence("plausible-edge-count", countOk,
            $"Edge count={count} in [{MinEdges}, {MaxEdges}].", required: true);
        if (!countOk)
        {
            c.Reject(EliminationStage.Od145ConnectionsDiscovery, "plausible-edge-count", $"Edge count {count} outside [{MinEdges}, {MaxEdges}].");
            return;
        }

        c.EdgeCount = count;
        c.VectorFirst = first;
    }

    private static void EvaluateConnValidation(ReadContext view, long canvasAddress, int offset, MutableCandidate c)
    {
        int count = c.EdgeCount;
        long first = c.VectorFirst;

        bool allRead = true;
        bool validEndpoints = true;
        var endpoints = new HashSet<(int X, int Y)>();

        for (int i = 0; i < count; i++)
        {
            if (!view.TryRead(first + i * EdgeElementStride, out AtlasConnectionEdge edge))
            {
                allRead = false;
                break;
            }
            c.DecodedEdges.Add(edge);

            if (edge.SourceX is < -0x80000 or > 0x80000 || edge.SourceY is < -0x80000 or > 0x80000 ||
                edge.TargetX is < -0x80000 or > 0x80000 || edge.TargetY is < -0x80000 or > 0x80000 ||
                (edge.SourceX == 0 && edge.SourceY == 0 && edge.TargetX == 0 && edge.TargetY == 0) ||
                (edge.SourceX == edge.TargetX && edge.SourceY == edge.TargetY))
            {
                validEndpoints = false;
            }

            endpoints.Add((edge.SourceX, edge.SourceY));
            endpoints.Add((edge.TargetX, edge.TargetY));
        }

        c.AddValidation("all-edges-decode-structurally", allRead,
            $"Read {c.DecodedEdges.Count}/{count} edge elements (stride={EdgeElementStride}).", required: true);
        if (!allRead)
        {
            c.Reject(EliminationStage.Od145ConnectionsValidation, "all-edges-decode-structurally", "Edge struct read failure.");
            return;
        }

        c.AddValidation("plausible-endpoints", validEndpoints,
            $"Non-loop, non-default endpoints in Atlas domain.", required: true);
        if (!validEndpoints)
        {
            c.Reject(EliminationStage.Od145ConnectionsValidation, "plausible-endpoints", "Invalid or loop edge endpoints.");
            return;
        }

        bool nonRandom = endpoints.Count >= Math.Min(count, MinEdges);
        c.AddValidation("non-random-topology", nonRandom,
            $"Unique endpoints count={endpoints.Count}; edges={count}.", required: true);
        if (!nonRandom)
        {
            c.Reject(EliminationStage.Od145ConnectionsValidation, "non-random-topology", "Degenerate edge endpoint count.");
        }
    }

    private static void EvaluateCrossFieldTopology(ReadContext view, List<long> nodes, long gridOffset,
        long canvasAddress, long connOffset, MutableCandidate pairCandidate)
    {
        var nodeCoords = new HashSet<(int X, int Y)>();
        foreach (long nodeAddr in nodes)
        {
            if (view.TryRead(nodeAddr + gridOffset, out StdTuple2D<int> coord))
                nodeCoords.Add((coord.X, coord.Y));
        }

        if (!view.TryRead(canvasAddress + connOffset, out StdVector vector))
        {
            pairCandidate.Reject(EliminationStage.Od145CrossFieldValidation, "vector-readable", "Cannot read vector.");
            return;
        }

        long first = vector.First.ToInt64();
        long last = vector.Last.ToInt64();
        int count = (int)((last - first) / EdgeElementStride);
        int matchedEdges = 0;

        for (int i = 0; i < count; i++)
        {
            if (view.TryRead(first + i * EdgeElementStride, out AtlasConnectionEdge edge))
            {
                bool sourceMatch = nodeCoords.Contains((edge.SourceX, edge.SourceY));
                bool targetMatch = nodeCoords.Contains((edge.TargetX, edge.TargetY));
                if (sourceMatch && targetMatch)
                {
                    matchedEdges++;
                }
                else if (sourceMatch || targetMatch)
                {
                    matchedEdges++;
                }
            }
        }

        double matchRate = count > 0 ? (double)matchedEdges / count : 0;
        bool coherent = matchRate >= 0.60 && matchedEdges >= MinEdges;

        pairCandidate.AddValidation("cross-field-topology-coherence", coherent,
            $"Edges matching node population: {matchedEdges}/{count} ({matchRate:P0}); min-edges={MinEdges}.", required: true);

        if (!coherent)
        {
            pairCandidate.Reject(EliminationStage.Od145CrossFieldValidation, "cross-field-topology-coherence",
                $"Topology coherence failure: match rate {matchRate:P0} ({matchedEdges}/{count}) < 60%.");
        }
    }

    private static bool ValidateGridField(ReadContext view, List<long> nodes, long offset,
        ImmutableArray<RecoveryEvidenceRecord>.Builder evidence)
    {
        var distinctCoords = new HashSet<(int X, int Y)>();
        var distinctX = new HashSet<int>();
        var distinctY = new HashSet<int>();
        bool allRead = true;
        bool inDomain = true;

        foreach (long nodeAddr in nodes)
        {
            if (!view.TryRead(nodeAddr + offset, out StdTuple2D<int> coord))
            {
                allRead = false;
                break;
            }
            if (coord.X is < -0x80000 or > 0x80000 || coord.Y is < -0x80000 or > 0x80000)
                inDomain = false;
            distinctCoords.Add((coord.X, coord.Y));
            distinctX.Add(coord.X);
            distinctY.Add(coord.Y);
        }

        evidence.Add(new("current-grid-complete-read", allRead ? RecoveryEvidenceResult.PASS : RecoveryEvidenceResult.FAIL,
            $"Read {nodes.Count} nodes at +0x{offset:X}.", true, true));
        if (!allRead) return false;

        evidence.Add(new("current-grid-domain", inDomain ? RecoveryEvidenceResult.PASS : RecoveryEvidenceResult.FAIL,
            "Coordinates in domain [-0x80000, 0x80000].", true, true));
        if (!inDomain) return false;

        bool diverse = distinctCoords.Count >= (nodes.Count * 3 / 4);
        evidence.Add(new("current-grid-diversity", diverse ? RecoveryEvidenceResult.PASS : RecoveryEvidenceResult.FAIL,
            $"Distinct coords {distinctCoords.Count}/{nodes.Count}.", true, true));
        if (!diverse) return false;

        bool nonDegenerate = distinctX.Count >= 5 && distinctY.Count >= 5 &&
            (distinctX.Max() - distinctX.Min()) >= 10 && (distinctY.Max() - distinctY.Min()) >= 10;
        evidence.Add(new("current-grid-2d-distribution", nonDegenerate ? RecoveryEvidenceResult.PASS : RecoveryEvidenceResult.FAIL,
            $"2D spread: spanX={distinctX.Max() - distinctX.Min()}, spanY={distinctY.Max() - distinctY.Min()}.", true, true));
        return nonDegenerate;
    }

    private static bool ValidateConnField(ReadContext view, long canvasAddress, long offset,
        ImmutableArray<RecoveryEvidenceRecord>.Builder evidence)
    {
        if (!view.TryRead(canvasAddress + offset, out StdVector vector))
        {
            evidence.Add(new("current-conn-header-read", RecoveryEvidenceResult.FAIL,
                $"Cannot read StdVector at canvas+0x{offset:X}.", true, true));
            return false;
        }

        long first = vector.First.ToInt64();
        long last = vector.Last.ToInt64();
        long end = vector.End.ToInt64();

        bool order = first >= 0x10000 && first <= last && last <= end;
        evidence.Add(new("current-conn-pointer-order", order ? RecoveryEvidenceResult.PASS : RecoveryEvidenceResult.FAIL,
            $"First=0x{first:X}, Last=0x{last:X}, End=0x{end:X}.", true, true));
        if (!order) return false;

        long byteLength = last - first;
        bool strideOk = byteLength % EdgeElementStride == 0;
        evidence.Add(new("current-conn-stride-0x14", strideOk ? RecoveryEvidenceResult.PASS : RecoveryEvidenceResult.FAIL,
            $"Length {byteLength} divisible by {EdgeElementStride}.", true, true));
        if (!strideOk) return false;

        int count = (int)(byteLength / EdgeElementStride);
        bool countOk = count >= MinEdges && count <= MaxEdges;
        evidence.Add(new("current-conn-edge-count", countOk ? RecoveryEvidenceResult.PASS : RecoveryEvidenceResult.FAIL,
            $"Count {count} in [{MinEdges}, {MaxEdges}].", true, true));
        if (!countOk) return false;

        bool allEdges = true;
        for (int i = 0; i < count; i++)
        {
            if (!view.TryRead(first + i * EdgeElementStride, out AtlasConnectionEdge edge))
            {
                allEdges = false;
                break;
            }
        }
        evidence.Add(new("current-conn-decode-edges", allEdges ? RecoveryEvidenceResult.PASS : RecoveryEvidenceResult.FAIL,
            $"Decoded {count} edges.", true, true));
        return allEdges;
    }

    private static bool ValidateTopologyCoherence(ReadContext view, List<long> nodes, long gridOffset,
        long canvasAddress, long connOffset, ImmutableArray<RecoveryEvidenceRecord>.Builder evidence)
    {
        var nodeCoords = new HashSet<(int X, int Y)>();
        foreach (long nodeAddr in nodes)
        {
            if (view.TryRead(nodeAddr + gridOffset, out StdTuple2D<int> coord))
                nodeCoords.Add((coord.X, coord.Y));
        }

        if (!view.TryRead(canvasAddress + connOffset, out StdVector vector)) return false;

        long first = vector.First.ToInt64();
        long last = vector.Last.ToInt64();
        int count = (int)((last - first) / EdgeElementStride);
        int matched = 0;

        for (int i = 0; i < count; i++)
        {
            if (view.TryRead(first + i * EdgeElementStride, out AtlasConnectionEdge edge))
            {
                if (nodeCoords.Contains((edge.SourceX, edge.SourceY)) ||
                    nodeCoords.Contains((edge.TargetX, edge.TargetY)))
                {
                    matched++;
                }
            }
        }

        double rate = count > 0 ? (double)matched / count : 0;
        bool pass = rate >= 0.60 && matched >= MinEdges;
        evidence.Add(new("current-cross-field-topology", pass ? RecoveryEvidenceResult.PASS : RecoveryEvidenceResult.FAIL,
            $"Matched {matched}/{count} edges ({rate:P0}).", true, true));
        return pass;
    }

    private static void ApplyEliminationStage(
        List<CandidateEliminationStage> stages,
        EliminationStage stage,
        IEnumerable<MutableCandidate> candidates,
        Func<MutableCandidate, bool> filter,
        string rule)
    {
        var input = candidates.Where(filter).ToArray();
        var surviving = input.Where(c => !c.Rejected && !c.IsEquivalent).ToArray();
        var rejected = input.Where(c => c.Rejected || c.IsEquivalent).ToArray();

        var rejections = rejected.ToImmutableDictionary(
            c => c.Id,
            c => c.Rejections.Where(r => r.Stage == stage).ToImmutableArray(),
            StringComparer.Ordinal);

        stages.Add(new CandidateEliminationStage(
            stage,
            input.Select(c => c.Id).ToImmutableArray(),
            surviving.Select(c => c.Id).ToImmutableArray(),
            rejected.Select(c => c.Id).ToImmutableArray(),
            rejections,
            rule));
    }

    private enum CandidateKind { Grid, Connection, FieldPair }

    private sealed class MutableCandidate(string id, long value, string origin, int pass, CandidateKind kind)
    {
        public string Id { get; } = id;
        public long Value { get; } = value;
        public string Origin { get; } = origin;
        public int Pass { get; } = pass;
        public CandidateKind Kind { get; } = kind;
        public AtlasFieldPair? FieldPair { get; set; }
        public int EdgeCount { get; set; }
        public long VectorFirst { get; set; }
        public List<RecoveryEvidenceRecord> Evidence { get; } = [];
        public List<RecoveryEvidenceRecord> ValidationEvidence { get; } = [];
        public List<CandidateRejection> Rejections { get; } = [];
        public CandidateDisposition Disposition { get; set; } = CandidateDisposition.Unresolved;
        public bool Rejected => Disposition == CandidateDisposition.Rejected;
        public bool IsEquivalent => Disposition == CandidateDisposition.Equivalent;
        public string? EquivalentTo { get; private set; }
        public Dictionary<long, (int X, int Y)> DiscoveredCoordinates { get; } = new();
        public List<AtlasConnectionEdge> DecodedEdges { get; } = [];

        public void AddEvidence(string pred, bool pass, string detail, bool required)
        {
            Evidence.Add(new(pred, pass ? RecoveryEvidenceResult.PASS : RecoveryEvidenceResult.FAIL, detail, required, false));
        }

        public void AddValidation(string pred, bool pass, string detail, bool required)
        {
            ValidationEvidence.Add(new(pred, pass ? RecoveryEvidenceResult.PASS : RecoveryEvidenceResult.FAIL, detail, required, true));
        }

        public void Reject(EliminationStage stage, string pred, string reason)
        {
            Rejections.Add(new(stage, pred, reason));
            Disposition = CandidateDisposition.Rejected;
        }

        public void MarkEquivalent(string representativeId)
        {
            EquivalentTo = representativeId;
            Rejections.Add(new(EliminationStage.Od145FieldPairEquivalence, "field-pair-equivalence",
                $"Equivalent to {representativeId}."));
            Disposition = CandidateDisposition.Equivalent;
        }

        public RecoveryCandidate Freeze() => new(
            Id, Value, Origin,
            Evidence.ToImmutableArray(),
            ValidationEvidence.ToImmutableArray(),
            Rejections.ToImmutableArray(),
            Disposition);
    }

    private sealed class ReadContext(
        IRecoveryReadOnlyMemory memory,
        Func<bool> checkBudget,
        Action<int> recordBytes)
    {
        public bool TryRead<T>(long address, out T value) where T : unmanaged
        {
            if (!checkBudget()) throw new IncompleteUiException("Read count or time budget exceeded.");
            recordBytes(System.Runtime.CompilerServices.Unsafe.SizeOf<T>());
            return memory.TryRead(address, out value);
        }
    }

    private sealed class IncompleteUiException(string message) : Exception(message);
}
