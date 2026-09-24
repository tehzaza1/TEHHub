using System.Net;
using System.Net.Sockets;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using TEHhub.OffsetDoctor.Process;
using TEHhub.OffsetDoctor.RecoveryV1;
using TEHhub.Offsets.Natives;
using TEHhub.Offsets.Objects;
using TEHhub.Offsets.Objects.States;
using TEHhub.Offsets.Objects.UiElement;

internal sealed class AtlasScanApiServer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly TcpListener _listener;
    private readonly int? _targetPid;
    private readonly string _outputDirectory;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly object _snapshotLock = new();
    private readonly List<AtlasScanSnapshot> _snapshots = [];

    private AtlasScanApiServer(int port, int? targetPid)
    {
        _targetPid = targetPid;
        _outputDirectory = Path.Combine(Environment.CurrentDirectory, "artifacts");
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    public static async Task<int> RunAsync(int port, int? targetPid)
    {
        var server = new AtlasScanApiServer(port, targetPid);
        try
        {
            server._listener.Start(16);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Atlas API could not listen on localhost:{port}: {ex.Message}");
            server._listener.Stop();
            return 1;
        }

        Console.WriteLine($"Atlas scan API listening at http://localhost:{port}/");
        Console.WriteLine("POST /scan captures the current Atlas. GET /snapshot returns the latest capture.");
        Console.WriteLine("Memory access is read-only. Press Ctrl+C to stop.");

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
            try { server._listener.Stop(); } catch { }
        };

        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                try
                {
                    var client = await server._listener.AcceptTcpClientAsync(shutdown.Token);
                    _ = Task.Run(() => server.HandleClientAsync(client, shutdown.Token));
                    continue;
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            server._listener.Stop();
            shutdown.Dispose();
        }

        return 0;
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        using (var stream = client.GetStream())
        try
        {
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            string? requestLine = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(requestLine)) return;
            string[] requestParts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (requestParts.Length < 2) return;
            string method = requestParts[0].ToUpperInvariant();
            string path = new Uri(new Uri("http://localhost"), requestParts[1]).AbsolutePath.TrimEnd('/');
            string? header;
            while (!string.IsNullOrEmpty(header = await reader.ReadLineAsync(cancellationToken))) { }

            if (method == "OPTIONS")
            {
                await SendJsonAsync(stream, new { status = "ok" }, 204);
                return;
            }

            if (path == "/health" && method == "GET")
            {
                await SendJsonAsync(stream, new { status = "ok", latestSequence = this.LatestSnapshot()?.Sequence });
                return;
            }

            if (path == "/snapshot" && method == "GET")
            {
                var snapshot = this.LatestSnapshot();
                if (snapshot is null)
                {
                    await SendJsonAsync(stream, new { status = "no_snapshot", diagnostic = "POST /scan to capture the current game state." }, 404);
                    return;
                }
                await SendJsonAsync(stream, snapshot);
                return;
            }

            if (path == "/snapshots" && method == "GET")
            {
                AtlasScanSnapshot[] snapshots;
                lock (this._snapshotLock) snapshots = this._snapshots.ToArray();
                await SendJsonAsync(stream, snapshots);
                return;
            }

            if (path == "/scan" && method == "POST")
            {
                if (!await this._scanGate.WaitAsync(0, cancellationToken))
                {
                    await SendJsonAsync(stream, new { status = "scan_in_progress", diagnostic = "Wait for the active scan, then GET /snapshot." }, 409);
                    return;
                }

                try
                {
                    var snapshot = AtlasMemoryScanner.Capture(this._targetPid);
                    snapshot = this.AddAddressStability(snapshot);
                    Directory.CreateDirectory(this._outputDirectory);
                    string fileName = $"atlas-snapshot-{snapshot.CapturedAtUtc:yyyyMMdd-HHmmss-fffZ}.json";
                    snapshot = snapshot with { OutputFile = Path.Combine(this._outputDirectory, fileName) };
                    await File.WriteAllTextAsync(snapshot.OutputFile, JsonSerializer.Serialize(snapshot, JsonOptions), cancellationToken);
                    this.StoreSnapshot(snapshot);
                    await SendJsonAsync(stream, snapshot);
                }
                finally
                {
                    this._scanGate.Release();
                }
                return;
            }

            await SendJsonAsync(stream, new { status = "not_found", diagnostic = "Use POST /scan, GET /snapshot, GET /snapshots, or GET /health." }, 404);
        }
        catch (Exception ex)
        {
            try { await SendJsonAsync(stream, new { status = "api_error", diagnostic = ex.Message }, 500); }
            catch { client.Dispose(); }
        }
    }

    private AtlasScanSnapshot AddAddressStability(AtlasScanSnapshot snapshot)
    {
        var previous = this.LatestSnapshot();
        if (previous is null || previous.ProcessId != snapshot.ProcessId)
        {
            return snapshot with { Sequence = (previous?.Sequence ?? 0) + 1 };
        }

        var previousByKey = previous.Nodes.ToDictionary(node => node.NodeKey, StringComparer.Ordinal);
        var nodes = snapshot.Nodes.Select(node => previousByKey.TryGetValue(node.NodeKey, out var prior)
            ? node with { PreviousUiAddress = prior.UiAddress, UiAddressChanged = !string.Equals(prior.UiAddress, node.UiAddress, StringComparison.OrdinalIgnoreCase) }
            : node).ToList();
        return snapshot with
        {
            Sequence = previous.Sequence + 1,
            PreviousCaptureTimestampUtc = previous.CapturedAtUtc,
            Nodes = nodes,
        };
    }

    private AtlasScanSnapshot? LatestSnapshot()
    {
        lock (this._snapshotLock) return this._snapshots.Count == 0 ? null : this._snapshots[^1];
    }

    private void StoreSnapshot(AtlasScanSnapshot snapshot)
    {
        lock (this._snapshotLock)
        {
            this._snapshots.Add(snapshot);
            if (this._snapshots.Count > 20) this._snapshots.RemoveAt(0);
        }
    }

    private static async Task SendJsonAsync(NetworkStream stream, object value, int statusCode = 200)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        string reason = statusCode switch { 200 => "OK", 204 => "No Content", 404 => "Not Found", 409 => "Conflict", _ => "Internal Server Error" };
        string headers = $"HTTP/1.1 {statusCode} {reason}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\nAccess-Control-Allow-Origin: *\r\nAccess-Control-Allow-Methods: GET, POST, OPTIONS\r\nAccess-Control-Allow-Headers: Content-Type\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers));
        if (statusCode != 204) await stream.WriteAsync(body);
    }
}

internal static partial class AtlasMemoryScanner
{
    private const int MaxCanvasChildren = 100_000;
    private static readonly long MaxCanvasChildBytes = (long)MaxCanvasChildren * IntPtr.Size;
    private const int MaxAtlasNodes = 100_000;
    private const int MaxAtlasEdges = 100_000;
    private const int MaxAtlasMarkers = 128;
    private const int AtlasEdgeStrideBytes = 20;
    private const long MaxAtlasEdgeBytes = (long)MaxAtlasEdges * AtlasEdgeStrideBytes;
    private static readonly TimeSpan MaxCaptureDuration = TimeSpan.FromSeconds(45);
    private const int MaxContentItems = 64;
    private const uint VisibleMask = 0x800;
    private const uint AtlasCurrentNodeMarkerFingerprint = 0x502EF3;
    private const uint MapNodeFingerprint = 0x542EF3;
    private const uint MistNodeFingerprint = 0x442EF3;
    private const uint ShouldModifyPositionMask = 0x400;
    private const float AtlasMarkerYOffsetPixels = 100f;
    private const float MaxMarkerSnapDistancePixels = 120f;
    private const float MinMarkerSeparationPixels = 30f;

    private static readonly int[][] KeyboardAtlasPaths = [[22, 0, 6]];
    private static readonly int[][] ControllerAtlasPaths = [[24, 2, 3, 0, 0, 6]];

    public static AtlasScanSnapshot Capture(int? targetPid)
    {
        var capturedAt = DateTime.UtcNow;
        var captureTimer = Stopwatch.StartNew();
        var discovery = ProcessDiscovery.DiscoverTargetProcess(targetPid);
        if (discovery.Status != ProcessDiscoveryStatus.Success || discovery.SelectedProcess is null)
        {
            return EmptySnapshot(capturedAt, discovery.Status == ProcessDiscoveryStatus.MultipleMatches ? "multiple_processes" : "no_game",
                discovery.ErrorMessage ?? "Path of Exile 2 is not running.");
        }

        var process = discovery.SelectedProcess;
        try
        {
            using var reader = new WindowsProcessMemoryReader(process.Id);
            var session = new RecoverySession(reader, RecoveryContextSnapshot.Create([]));
            var staticResult = Od001GameStatesRecovery.Run(session);
            long staticAddress = staticResult.Decision.Proposal
                ?? staticResult.CurrentValidation?.Value
                ?? 0;
            if (staticAddress < 0x10000 || !reader.TryRead(new IntPtr(staticAddress), out GameStateStaticOffset staticOffset) ||
                staticOffset.GameState == IntPtr.Zero || !reader.TryRead(staticOffset.GameState, out GameStateOffset stateOffset))
            {
                return EmptySnapshot(capturedAt, "game_state_unavailable",
                    $"Could not resolve GameStates. OD-001 status={staticResult.Decision.TerminalResult}; {staticResult.Detail ?? "no readable GameState root"}", process.Id, process.ProcessName);
            }

            long currentVectorLength = stateOffset.CurrentStatePtr.Last.ToInt64() - stateOffset.CurrentStatePtr.First.ToInt64();
            long currentStateAddress = currentVectorLength >= 2 * IntPtr.Size && currentVectorLength <= 13 * IntPtr.Size
                ? ReadPointer(reader, stateOffset.CurrentStatePtr.Last.ToInt64() - 2 * IntPtr.Size)
                : 0;
            long inGameStateAddress = stateOffset.States[4].X.ToInt64();
            string stateName = GetStateName(stateOffset, currentStateAddress);
            if (inGameStateAddress < 0x10000 || currentStateAddress != inGameStateAddress)
            {
                return EmptySnapshot(capturedAt, "not_in_game",
                    $"Current state is {stateName}; an in-game Atlas scan requires the active InGameState.", process.Id, process.ProcessName) with
                {
                    CurrentState = stateName,
                    StaticGameStatesAddress = Hex(staticAddress),
                    GameStatesAddress = Hex(staticOffset.GameState),
                };
            }

            if (!reader.TryRead(new IntPtr(inGameStateAddress), out InGameStateOffset inGameState))
            {
                return EmptySnapshot(capturedAt, "game_state_unavailable", "InGameState memory could not be read.", process.Id, process.ProcessName);
            }

            string? areaHash = null;
            long areaAddress = inGameState.AreaInstanceData.ToInt64();
            if (areaAddress >= 0x10000 && reader.TryRead(new IntPtr(areaAddress + 0x114), out uint rawAreaHash))
            {
                areaHash = rawAreaHash.ToString("X8");
            }

            var uiRootManager = inGameState.UiRootStructPtr != IntPtr.Zero
                ? inGameState.UiRootStructPtr
                : inGameState.GamepadUiRootStructPtr;
            if (uiRootManager == IntPtr.Zero || !reader.TryRead(uiRootManager, out UiRootStruct uiRoot))
            {
                return EmptySnapshot(capturedAt, "atlas_unavailable", "UiRoot is not ready yet.", process.Id, process.ProcessName) with
                {
                    CurrentState = stateName,
                    Area = new AreaIdentity(Hex(inGameState.AreaInstanceData), areaHash),
                    InGameStateAddress = Hex(inGameStateAddress),
                };
            }

            var panel = FindAtlasPanel(reader, inGameState, uiRootManager, uiRoot);
            if (panel is null)
            {
                return EmptySnapshot(capturedAt, "atlas_unavailable",
                    "Could not resolve the Atlas panel through the current UiRoot/GameUi path. Open the Atlas panel and scan again.", process.Id, process.ProcessName) with
                {
                    CurrentState = stateName,
                    Area = new AreaIdentity(Hex(inGameState.AreaInstanceData), areaHash),
                    InGameStateAddress = Hex(inGameStateAddress),
                    UiRootAddress = Hex(uiRootManager),
                };
            }

            var panelOffset = panel.Value.Offset;
            bool panelVisible = IsEffectivelyVisible(reader, panel.Value.Address);
            if (!TryGetChildVector(reader, panel.Value.Address, out var children, out int childCount, out string vectorDiagnostic))
            {
                return EmptySnapshot(capturedAt, "atlas_unavailable", vectorDiagnostic, process.Id, process.ProcessName) with
                {
                    CurrentState = stateName,
                    Area = new AreaIdentity(Hex(inGameState.AreaInstanceData), areaHash),
                    InGameStateAddress = Hex(inGameStateAddress),
                    UiRootAddress = Hex(uiRootManager),
                    Atlas = ToUiInfo(reader, panel.Value.Address, panelVisible, panelOffset),
                    AtlasChildCount = childCount,
                    NodesTruncated = vectorDiagnostic.Contains("byte limits", StringComparison.Ordinal),
                    AtlasChildrenTruncated = vectorDiagnostic.Contains("byte limits", StringComparison.Ordinal),
                    CaptureDurationMilliseconds = captureTimer.ElapsedMilliseconds,
                    MarkerDiagnostic = "Not scanned because the Atlas child vector could not be validated.",
                };
            }

            var nodeCandidates = new List<(int Index, long Address)>();
            var markerCandidates = new List<AtlasUiGeometry>();
            int detectedMarkerCount = 0;
            int detectedNodeCount = 0;
            bool childEnumerationComplete = true;
            for (int i = 0; i < childCount; i++)
            {
                if (captureTimer.Elapsed >= MaxCaptureDuration)
                {
                    childEnumerationComplete = false;
                    break;
                }
                if (!reader.TryRead(new IntPtr(children + (i * IntPtr.Size)), out IntPtr childPtr) || childPtr == IntPtr.Zero)
                    continue;
                long childAddress = childPtr.ToInt64();
                if (!reader.TryRead(childPtr, out UiElementBaseOffset child)) continue;
                uint fingerprint = child.Flags & ~VisibleMask;
                if (child.Self.ToInt64() != childAddress || child.ParentPtr.ToInt64() != panel.Value.Address)
                    continue;
                if (fingerprint == (AtlasCurrentNodeMarkerFingerprint & ~VisibleMask) && (child.Flags & VisibleMask) != 0)
                {
                    detectedMarkerCount++;
                    if (markerCandidates.Count < MaxAtlasMarkers)
                        markerCandidates.Add(ToUiGeometry(i, childAddress, child));
                    continue;
                }
                if (fingerprint != (MapNodeFingerprint & ~VisibleMask) && fingerprint != (MistNodeFingerprint & ~VisibleMask))
                    continue;
                detectedNodeCount++;
                if (nodeCandidates.Count < MaxAtlasNodes) nodeCandidates.Add((i, childAddress));
            }

            var connectionScan = ReadConnections(reader, panel.Value.Address);
            var connectionMap = connectionScan.Neighbors;
            var nodes = new List<AtlasScanNode>(nodeCandidates.Count);
            var nodeUiByIndex = new Dictionary<int, AtlasUiGeometry>();
            bool nodeEnumerationComplete = true;
            foreach (var candidate in nodeCandidates)
            {
                if (captureTimer.Elapsed >= MaxCaptureDuration)
                {
                    nodeEnumerationComplete = false;
                    break;
                }
                if (TryReadNode(reader, candidate.Index, candidate.Address, connectionMap, out var node, out var nodeUi))
                {
                    nodes.Add(node);
                    if (!string.IsNullOrWhiteSpace(node.MapId)) nodeUiByIndex[node.Index] = nodeUi;
                }
            }

            var playerMarkers = ResolvePlayerMarkers(process, panelOffset, markerCandidates, nodeUiByIndex, nodes);
            bool markersTruncated = detectedMarkerCount > MaxAtlasMarkers;
            string markerDiagnostic = BuildMarkerDiagnostic(detectedMarkerCount, playerMarkers,
                childEnumerationComplete, markersTruncated, nodeEnumerationComplete);

            var nodeCoordinates = nodes.Select(node => (node.Grid.X, node.Grid.Y)).ToHashSet();
            int connectedNodeCount = nodes.Count(node => connectionMap.ContainsKey((node.Grid.X, node.Grid.Y)));
            int edgesTouchingCapturedNodes = connectionScan.Edges.Count(edge =>
                nodeCoordinates.Contains((edge.SourceX, edge.SourceY)) || nodeCoordinates.Contains((edge.TargetX, edge.TargetY)));
            var connectionDiagnostics = connectionScan.Diagnostics with
            {
                CapturedNodeCoordinatesWithConnections = connectedNodeCount,
                EdgesTouchingCapturedNodes = edgesTouchingCapturedNodes,
            };

            string status = !panelVisible ? "atlas_hidden" : nodes.Count == 0 ? "atlas_visible_not_ready" : "ready";
            string diagnostic = status switch
            {
                "atlas_hidden" => "Atlas panel was resolved but is not effectively visible. UI addresses and rectangles are ephemeral.",
                "atlas_visible_not_ready" => "Atlas panel is visible but no map nodes were readable yet. Retry after the Atlas finishes loading.",
                _ => "Atlas snapshot captured. UI addresses and rectangles are ephemeral and may change after pan, zoom, panel changes, or game updates.",
            };
            if (detectedNodeCount > MaxAtlasNodes)
                diagnostic += $" The node safety limit captured {MaxAtlasNodes} of {detectedNodeCount} detected node elements.";
            bool captureTimedOut = captureTimer.Elapsed >= MaxCaptureDuration || !childEnumerationComplete || !nodeEnumerationComplete;
            if (captureTimedOut)
                diagnostic += $" The scan hit its {MaxCaptureDuration.TotalSeconds:0}-second time limit and returned a partial snapshot.";
            if (connectionDiagnostics.Status != "decoded" || connectedNodeCount == 0 || edgesTouchingCapturedNodes == 0)
                diagnostic += $" Atlas connection topology is not verified: {connectionDiagnostics.Diagnostic}";
            diagnostic += $" {markerDiagnostic}";

            return new AtlasScanSnapshot
            {
                CapturedAtUtc = capturedAt,
                Status = status,
                Diagnostic = diagnostic,
                ProcessId = process.Id,
                ProcessName = process.ProcessName,
                CurrentState = stateName,
                GameStatesAddress = Hex(staticOffset.GameState),
                StaticGameStatesAddress = Hex(staticAddress),
                InGameStateAddress = Hex(inGameStateAddress),
                UiRootAddress = Hex(uiRootManager),
                Area = new AreaIdentity(Hex(inGameState.AreaInstanceData), areaHash),
                Atlas = ToUiInfo(reader, panel.Value.Address, panelVisible, panelOffset),
                AtlasChildCount = childCount,
                NodeCount = nodes.Count,
                DetectedNodeCount = detectedNodeCount,
                NodesTruncated = detectedNodeCount > MaxAtlasNodes || !childEnumerationComplete || !nodeEnumerationComplete,
                AtlasChildrenTruncated = false,
                DetectedNodeCountComplete = childEnumerationComplete,
                CaptureTimedOut = captureTimedOut,
                CaptureDurationMilliseconds = captureTimer.ElapsedMilliseconds,
                Connections = connectionDiagnostics,
                PlayerMarkers = playerMarkers,
                DetectedMarkerCount = detectedMarkerCount,
                MarkersTruncated = markersTruncated,
                MarkerDetectionComplete = childEnumerationComplete,
                MarkerDiagnostic = markerDiagnostic,
                TopologyStatus = connectionDiagnostics.Status == "decoded" && connectedNodeCount > 0 && edgesTouchingCapturedNodes > 0
                    ? "decoded"
                    : "unverified",
                Nodes = nodes,
            };
        }
        catch (Exception ex)
        {
            return EmptySnapshot(capturedAt, "scan_error", ex.Message, process.Id, process.ProcessName);
        }
    }

    private static (long Address, UiElementBaseOffset Offset)? FindAtlasPanel(
        IProcessMemoryReader reader,
        InGameStateOffset inGame,
        IntPtr managerAddress,
        UiRootStruct uiRoot)
    {
        var candidates = new List<(long Address, UiElementBaseOffset Offset, int Fingerprints, int ChildCount)>();
        var roots = new List<(IntPtr Address, bool Controller, string Source)>();
        if (inGame.UiRootStructPtr != IntPtr.Zero) roots.Add((inGame.UiRootStructPtr, false, "ui-root"));
        if (inGame.GamepadUiRootStructPtr != IntPtr.Zero) roots.Add((inGame.GamepadUiRootStructPtr, true, "gamepad-ui-root"));
        if (uiRoot.GameUiPtr != IntPtr.Zero) roots.Add((uiRoot.GameUiPtr, false, "game-ui"));
        if (uiRoot.GameUiControllerPtr != IntPtr.Zero) roots.Add((uiRoot.GameUiControllerPtr, true, "game-ui-controller"));
        roots.Add((managerAddress, inGame.UiRootStructPtr == IntPtr.Zero, "ui-manager"));

        var visitedBases = new HashSet<(long, bool)>();
        foreach (var root in roots)
        {
            if (!visitedBases.Add((root.Address.ToInt64(), root.Controller))) continue;
            var paths = root.Controller ? ControllerAtlasPaths : KeyboardAtlasPaths;
            foreach (var path in paths)
            {
                long address = FollowChildPath(reader, root.Address.ToInt64(), path);
                AddCandidate(reader, address, candidates);
            }
        }

        if (candidates.Count == 0)
        {
            foreach (var root in roots)
                SearchAtlasPanel(reader, root.Address.ToInt64(), 0, new HashSet<long>(), candidates);
        }

        if (candidates.Count == 0) return null;
        var best = candidates.OrderByDescending(candidate => candidate.Fingerprints).ThenByDescending(candidate => candidate.ChildCount).First();
        return (best.Address, best.Offset);
    }

    private static void AddCandidate(IProcessMemoryReader reader, long address,
        List<(long Address, UiElementBaseOffset Offset, int Fingerprints, int ChildCount)> candidates)
    {
        if (address < 0x10000 || !reader.TryRead(new IntPtr(address), out UiElementBaseOffset panel) || panel.Self.ToInt64() != address)
            return;
        if (!TryGetChildVector(reader, address, out var first, out int count, out _)) return;
        int fingerprints = CountNodeFingerprints(reader, first, count);
        candidates.Add((address, panel, fingerprints, count));
    }

    private static void SearchAtlasPanel(IProcessMemoryReader reader, long address, int depth,
        HashSet<long> visited, List<(long Address, UiElementBaseOffset Offset, int Fingerprints, int ChildCount)> candidates)
    {
        if (address < 0x10000 || depth > 8 || visited.Count > 4096 || !visited.Add(address) ||
            !reader.TryRead(new IntPtr(address), out UiElementBaseOffset element) || element.Self.ToInt64() != address ||
            !TryGetChildVector(reader, address, out long first, out int count, out _)) return;

        int fingerprints = CountNodeFingerprints(reader, first, count);
        if (fingerprints >= 2)
        {
            candidates.Add((address, element, fingerprints, count));
            return;
        }

        for (int i = 0; i < Math.Min(count, 128); i++)
        {
            if (reader.TryRead(new IntPtr(first + i * IntPtr.Size), out IntPtr child))
                SearchAtlasPanel(reader, child.ToInt64(), depth + 1, visited, candidates);
            if (visited.Count > 4096) return;
        }
    }

    private static int CountNodeFingerprints(IProcessMemoryReader reader, long first, int count)
    {
        int found = 0;
        for (int i = 0; i < Math.Min(count, 32); i++)
        {
            if (reader.TryRead(new IntPtr(first + i * IntPtr.Size), out IntPtr child) && child != IntPtr.Zero &&
                reader.TryRead(child + 0x168, out uint flags))
            {
                uint fingerprint = flags & ~VisibleMask;
                if (fingerprint is (MapNodeFingerprint & ~VisibleMask) or (MistNodeFingerprint & ~VisibleMask)) found++;
            }
        }
        return found;
    }

    private static long FollowChildPath(IProcessMemoryReader reader, long root, int[] path)
    {
        long current = root;
        foreach (int index in path)
        {
            if (!TryGetChildVector(reader, current, out long first, out int count, out _) || index >= count ||
                !reader.TryRead(new IntPtr(first + index * IntPtr.Size), out IntPtr child))
                return 0;
            current = child.ToInt64();
        }
        return current;
    }

    private static bool TryGetChildVector(IProcessMemoryReader reader, long address, out long first, out int count, out string diagnostic)
    {
        first = 0;
        count = 0;
        diagnostic = "UI element child vector is unreadable.";
        if (address < 0x10000 || !reader.TryRead(new IntPtr(address), out UiElementBaseOffset element) || element.Self.ToInt64() != address)
            return false;
        long begin = element.ChildrensPtr.First.ToInt64();
        long end = element.ChildrensPtr.Last.ToInt64();
        long capacity = element.ChildrensPtr.End.ToInt64();
        if (begin < 0x10000 || end < begin || capacity < end ||
            begin % IntPtr.Size != 0 || end % IntPtr.Size != 0 || capacity % IntPtr.Size != 0 ||
            (end - begin) % IntPtr.Size != 0 || (capacity - begin) % IntPtr.Size != 0)
        {
            diagnostic = $"UI child vector at 0x{address:X} has invalid First/Last/End pointers.";
            return false;
        }
        long total = (end - begin) / IntPtr.Size;
        long allocated = (capacity - begin) / IntPtr.Size;
        long dataBytes = end - begin;
        long capacityBytes = capacity - begin;
        count = total > int.MaxValue ? int.MaxValue : (int)total;
        if (dataBytes > MaxCanvasChildBytes || capacityBytes > MaxCanvasChildBytes * 4L)
        {
            diagnostic = $"UI child vector at 0x{address:X} contains {total} children with {allocated} allocated slots; byte limits are {MaxCanvasChildBytes} data and {MaxCanvasChildBytes * 4L} capacity.";
            return false;
        }
        first = begin;
        diagnostic = string.Empty;
        return true;
    }

    private static string GetStateName(GameStateOffset state, long currentAddress)
    {
        for (int i = 0; i < GameStateHelper.TOTAL_STATES; i++)
            if (state.States[i].X.ToInt64() == currentAddress) return i switch
            {
                0 => "AreaLoadingState",
                4 => "InGameState",
                _ => $"GameState[{i}]",
            };
        return "GameNotLoaded";
    }

    private static ConnectionScanResult ReadConnections(IProcessMemoryReader reader, long atlasAddress)
    {
        var result = new Dictionary<(int X, int Y), HashSet<(int X, int Y)>>();
        var edges = new List<AtlasConnectionEdge>();
        if (!reader.TryRead(new IntPtr(atlasAddress + 0x590), out StdVector vector))
            return new(result, edges, new AtlasConnectionDiagnostics
            {
                Status = "header_unreadable",
                Diagnostic = $"Could not read the StdVector header at atlas+0x590 (0x{atlasAddress + 0x590:X}).",
                VectorAddress = Hex(atlasAddress + 0x590),
            });
        long first = vector.First.ToInt64(), last = vector.Last.ToInt64(), end = vector.End.ToInt64();
        long byteLength = last - first;
        long capacityLength = end - first;
        var diagnostics = new AtlasConnectionDiagnostics
        {
            Status = "invalid_vector",
            Diagnostic = string.Empty,
            VectorAddress = Hex(atlasAddress + 0x590),
            First = Hex(first),
            Last = Hex(last),
            End = Hex(end),
        };
        if (first == 0 && last == 0 && end == 0)
            return new(result, edges, diagnostics with { Status = "empty_vector", Diagnostic = "The Atlas connection vector is empty." });
        if (first < 0x10000 || last < first || end < last ||
            first % sizeof(int) != 0 || last % sizeof(int) != 0 || end % sizeof(int) != 0 ||
            byteLength % AtlasEdgeStrideBytes != 0 || capacityLength % AtlasEdgeStrideBytes != 0)
            return new(result, edges, diagnostics with { Diagnostic = $"Malformed vector: first=0x{first:X}, last=0x{last:X}, end=0x{end:X}." });
        long edgeCountLong = byteLength / AtlasEdgeStrideBytes;
        long capacityCount = capacityLength / AtlasEdgeStrideBytes;
        if (byteLength > MaxAtlasEdgeBytes || capacityLength > MaxAtlasEdgeBytes * 4L)
            return new(result, edges, diagnostics with
            {
                Status = "edge_limit_exceeded",
                EdgeCount = edgeCountLong,
                EdgesTruncated = true,
                Diagnostic = $"Connection vector has {edgeCountLong} edges and capacity {capacityCount}; byte limits are {MaxAtlasEdgeBytes} data and {MaxAtlasEdgeBytes * 4L} capacity.",
            });
        int count = (int)edgeCountLong;
        if (count == 0)
            return new(result, edges, diagnostics with { Status = "empty_vector", EdgeCount = 0, Diagnostic = "The Atlas connection vector contains no edges." });

        int edgeByteCount = checked(count * AtlasEdgeStrideBytes);
        if (edgeByteCount > MaxAtlasEdgeBytes)
            return new(result, edges, diagnostics with
            {
                Status = "edge_limit_exceeded",
                EdgeCount = count,
                EdgesTruncated = true,
                Diagnostic = $"Connection byte request {edgeByteCount} exceeds the {MaxAtlasEdgeBytes}-byte limit.",
            });
        byte[] edgeBytes = new byte[edgeByteCount];
        if (!reader.TryReadBytes(new IntPtr(first), edgeBytes))
            return new(result, edges, diagnostics with
            {
                Status = "edge_data_unreadable",
                EdgeCount = count,
                Diagnostic = $"The connection vector header is valid, but {edgeBytes.Length} edge bytes at 0x{first:X} could not be read as one bounded range.",
            });

        int unusableEdgeCount = 0;
        for (int i = 0; i < count; i++)
        {
            int offset = i * AtlasEdgeStrideBytes;
            var edge = new AtlasConnectionEdge
            {
                Unknown = BinaryPrimitives.ReadInt32LittleEndian(edgeBytes.AsSpan(offset, 4)),
                SourceX = BinaryPrimitives.ReadInt32LittleEndian(edgeBytes.AsSpan(offset + 4, 4)),
                SourceY = BinaryPrimitives.ReadInt32LittleEndian(edgeBytes.AsSpan(offset + 8, 4)),
                TargetX = BinaryPrimitives.ReadInt32LittleEndian(edgeBytes.AsSpan(offset + 12, 4)),
                TargetY = BinaryPrimitives.ReadInt32LittleEndian(edgeBytes.AsSpan(offset + 16, 4)),
            };
            edges.Add(edge);
            var source = (X: edge.SourceX, Y: edge.SourceY);
            var target = (X: edge.TargetX, Y: edge.TargetY);
            if (source == target || target == default || source == default ||
                source.X is < -0x80000 or > 0x80000 || source.Y is < -0x80000 or > 0x80000 ||
                target.X is < -0x80000 or > 0x80000 || target.Y is < -0x80000 or > 0x80000)
            {
                unusableEdgeCount++;
                continue;
            }
            AddConnection(result, source, target);
            AddConnection(result, target, source);
        }
        return new(result, edges, diagnostics with
        {
            Status = "decoded",
            EdgeCount = count,
            EdgesRead = count,
            UsableEdges = count - unusableEdgeCount,
            UniqueUndirectedConnections = result.Sum(entry => entry.Value.Count) / 2,
            Diagnostic = $"Decoded {count} edges from the Atlas canvas vector; {count - unusableEdgeCount} passed endpoint checks.",
        });
    }

    private static void AddConnection(Dictionary<(int X, int Y), HashSet<(int X, int Y)>> connections,
        (int X, int Y) source, (int X, int Y) target)
    {
        if (!connections.TryGetValue(source, out var neighbors)) connections[source] = neighbors = [];
        neighbors.Add(target);
    }

    private static bool TryReadNode(IProcessMemoryReader reader, int index, long nodeAddress,
        Dictionary<(int X, int Y), HashSet<(int X, int Y)>> connections,
        out AtlasScanNode node, out AtlasUiGeometry uiGeometry)
    {
        node = null!;
        uiGeometry = default;
        if (!reader.TryRead(new IntPtr(nodeAddress), out UiElementBaseOffset ui) || ui.Self.ToInt64() != nodeAddress ||
            !reader.TryRead(new IntPtr(nodeAddress + 0x10), out IntPtr dataStorage) || dataStorage.ToInt64() < 0x10000 ||
            !reader.TryRead(dataStorage + 0x20, out IntPtr nodeData) || nodeData.ToInt64() < 0x10000 ||
            !reader.TryRead(new IntPtr(nodeAddress + 0x310), out StdTuple2D<int> grid) ||
            !reader.TryRead(nodeData + 0x2BE, out byte biome) ||
            !reader.TryRead(nodeData + 0x2BF, out byte rawStatus)) return false;

        uiGeometry = ToUiGeometry(index, nodeAddress, ui);
        string mapId = ReadMapId(reader, nodeData.ToInt64());
        var tokens = ReadContentTokens(reader, nodeAddress);
        var badges = ReadBadgeIds(reader, nodeAddress);
        var key = string.IsNullOrWhiteSpace(mapId) ? $"unknown:{grid.X},{grid.Y}:{index}" : $"{mapId}:{grid.X},{grid.Y}";
        var neighborList = connections.TryGetValue((grid.X, grid.Y), out var adjacent)
            ? adjacent.OrderBy(point => point.X).ThenBy(point => point.Y).Select(point => new GridPoint(point.X, point.Y)).ToList()
            : [];
        bool visible = IsEffectivelyVisible(reader, nodeAddress);
        node = new AtlasScanNode
        {
            NodeKey = key,
            MapId = mapId,
            Index = index,
            Grid = new GridPoint(grid.X, grid.Y),
            BiomeId = biome,
            State = (rawStatus & 0x02) != 0 ? "CompletedBase" : (rawStatus & 0x01) != 0 ? "AccessibleNow" : "None",
            RawStatus = rawStatus,
            RawStatusHex = $"0x{rawStatus:X2}",
            Connections = neighborList,
            ContentTokens = tokens,
            BadgeContentIds = badges,
            UiAddress = Hex(nodeAddress),
            Visible = visible,
            Rect = ToRect(ui),
        };
        return true;
    }

    private static List<AtlasPlayerMarkerObservation> ResolvePlayerMarkers(
        Process process,
        UiElementBaseOffset atlasPanel,
        List<AtlasUiGeometry> markers,
        Dictionary<int, AtlasUiGeometry> uiByIndex,
        List<AtlasScanNode> nodes)
    {
        var observations = new List<AtlasPlayerMarkerObservation>(markers.Count);
        if (markers.Count == 0) return observations;

        var nodesByIndex = nodes
            .Where(node => !string.IsNullOrWhiteSpace(node.MapId))
            .ToDictionary(node => node.Index);
        bool clientDimensionsKnown = TryGetClientDimensions(process, out int clientWidth, out int clientHeight);
        var scaleScenarios = clientDimensionsKnown
            ? [(clientWidth, clientHeight)]
            : GetFallbackScaleScenarios();

        foreach (var marker in markers)
        {
            var resolutions = scaleScenarios
                .Select(scenario => FindNearestMapNode(marker, atlasPanel, uiByIndex, nodesByIndex,
                    scenario.Width, scenario.Height))
                .ToList();
            var representative = resolutions[0];
            bool stableResolution = representative.Status == "resolved" && resolutions.All(result =>
                result.Status == "resolved" && result.NearestNode?.NodeKey == representative.NearestNode?.NodeKey);
            if (!stableResolution)
            {
                string reason = !clientDimensionsKnown
                    ? "Client dimensions were unavailable, and the nearest-node result did not remain uniquely inside the snap threshold across the bounded scale scenarios."
                    : representative.Diagnostic;
                observations.Add(CreateUnresolvedMarker(marker, reason) with
                {
                    ResolutionStatus = resolutions.Any(result => result.Status == "ambiguous") ? "ambiguous" :
                        resolutions.Any(result => result.Status == "outside_threshold") ? "outside_threshold" : "unresolved",
                    NearestCandidateNodeKey = clientDimensionsKnown ? representative.NearestNode?.NodeKey : null,
                    NearestCandidateMapId = clientDimensionsKnown ? representative.NearestNode?.MapId : null,
                    NearestCandidateGrid = clientDimensionsKnown ? representative.NearestNode?.Grid : null,
                    DistanceSquared = clientDimensionsKnown ? representative.DistanceSquared : null,
                    DistanceToNearestPixels = clientDimensionsKnown ? representative.NearestDistancePixels : null,
                    DistanceToNextNearestPixels = clientDimensionsKnown ? representative.NextDistancePixels : null,
                });
                continue;
            }

            var closestNode = representative.NearestNode!;
            string resolutionDiagnostic = clientDimensionsKnown
                ? "Resolved among Atlas siblings using the Core nearest-center rule and +100px marker offset. Scale uses current game client dimensions; widescreen horizontal cull is estimated, so treat the result as approximate."
                : $"Resolved to the same unique node across {scaleScenarios.Count} bounded client-size scenarios after the Core +100px Y offset. The actual game window size was unavailable; result is stable under the tested scale envelope but approximate.";

            observations.Add(new AtlasPlayerMarkerObservation
            {
                Index = marker.Index,
                UiAddress = Hex(marker.Address),
                AddressLifetime = "ephemeral",
                Visible = true,
                Rect = ToRect(marker),
                ResolvedNodeKey = closestNode.NodeKey,
                ResolvedMapId = closestNode.MapId,
                ResolvedGrid = closestNode.Grid,
                NearestCandidateNodeKey = closestNode.NodeKey,
                NearestCandidateMapId = closestNode.MapId,
                NearestCandidateGrid = closestNode.Grid,
                ResolutionStatus = "resolved_approximate",
                ResolutionMethod = clientDimensionsKnown
                    ? "nearest_map_node_center_after_100px_y_offset"
                    : "nearest_map_node_center_scale_envelope_after_100px_y_offset",
                DistanceSquared = clientDimensionsKnown ? representative.DistanceSquared : null,
                DistanceToNearestPixels = clientDimensionsKnown ? representative.NearestDistancePixels : null,
                DistanceToNextNearestPixels = clientDimensionsKnown ? representative.NextDistancePixels : null,
                Diagnostic = resolutionDiagnostic,
            });
        }

        return observations;
    }

    private static AtlasMarkerNearestResult FindNearestMapNode(AtlasUiGeometry marker,
        UiElementBaseOffset atlasPanel, Dictionary<int, AtlasUiGeometry> uiByIndex,
        Dictionary<int, AtlasScanNode> nodesByIndex, int clientWidth, int clientHeight)
    {
        if (!TryGetUiScale(atlasPanel, clientWidth, clientHeight, out var parentScale) ||
            !TryGetUiScale(marker, clientWidth, clientHeight, out var markerScale))
            return new(null, null, float.MaxValue, null, null, "invalid_scale",
                "Atlas parent or marker scale metadata is invalid.");

        var markerCenter = GetSiblingScreenCenter(marker, markerScale, atlasPanel, parentScale);
        markerCenter.Y += AtlasMarkerYOffsetPixels;
        AtlasScanNode? closestNode = null;
        AtlasScanNode? secondClosestNode = null;
        float closestDistanceSquared = float.MaxValue;
        float secondClosestDistanceSquared = float.MaxValue;
        foreach (var (nodeIndex, candidateUi) in uiByIndex)
        {
            if (!nodesByIndex.TryGetValue(nodeIndex, out var node) ||
                !TryGetUiScale(candidateUi, clientWidth, clientHeight, out var nodeScale))
                continue;

            var nodeCenter = GetSiblingScreenCenter(candidateUi, nodeScale, atlasPanel, parentScale);
            float dx = markerCenter.X - nodeCenter.X;
            float dy = markerCenter.Y - nodeCenter.Y;
            float distanceSquared = (dx * dx) + (dy * dy);
            if (!float.IsFinite(distanceSquared)) continue;
            if (distanceSquared < closestDistanceSquared)
            {
                secondClosestNode = closestNode;
                secondClosestDistanceSquared = closestDistanceSquared;
                closestDistanceSquared = distanceSquared;
                closestNode = node;
            }
            else if (distanceSquared < secondClosestDistanceSquared)
            {
                secondClosestDistanceSquared = distanceSquared;
                secondClosestNode = node;
            }
        }

        if (closestNode is null)
            return new(null, null, float.MaxValue, null, null, "no_candidate",
                "No captured map node with a readable MapId was available for nearest-center resolution.");

        float closestDistance = MathF.Sqrt(closestDistanceSquared);
        float secondClosestDistance = MathF.Sqrt(secondClosestDistanceSquared);
        if (closestDistance > MaxMarkerSnapDistancePixels)
            return new(closestNode, secondClosestNode, closestDistanceSquared, closestDistance,
                secondClosestNode is null ? null : secondClosestDistance,
                "outside_threshold",
                $"Nearest candidate {closestNode.NodeKey} is {closestDistance:0.#}px away, beyond the {MaxMarkerSnapDistancePixels:0}px resolution threshold.");
        if (secondClosestNode is not null && secondClosestDistance - closestDistance < MinMarkerSeparationPixels)
            return new(closestNode, secondClosestNode, closestDistanceSquared, closestDistance, secondClosestDistance,
                "ambiguous",
                $"Nearest candidates are ambiguous: {closestNode.NodeKey} at {closestDistance:0.#}px and {secondClosestNode.NodeKey} at {secondClosestDistance:0.#}px differ by less than {MinMarkerSeparationPixels:0}px.");
        return new(closestNode, secondClosestNode, closestDistanceSquared, closestDistance,
            secondClosestNode is null ? null : secondClosestDistance, "resolved", string.Empty);
    }

    private static List<(int Width, int Height)> GetFallbackScaleScenarios()
    {
        float[] aspectRatios = [1.0f, 1.2f, 4f / 3f, 1.5f, 1.6f, 16f / 9f, 2.0f, 21f / 9f, 32f / 9f];
        int[] heights = [480, 600, 720, 768, 900, 1080, 1200, 1440, 1600, 2160, 2880, 4320];
        var scenarios = new List<(int Width, int Height)>(aspectRatios.Length * heights.Length);
        foreach (int height in heights)
        foreach (float aspect in aspectRatios)
            scenarios.Add(((int)MathF.Round(height * aspect), height));
        return scenarios;
    }

    private static AtlasPlayerMarkerObservation CreateUnresolvedMarker(AtlasUiGeometry ui, string diagnostic) => new()
    {
        Index = ui.Index,
        UiAddress = Hex(ui.Address),
        AddressLifetime = "ephemeral",
        Visible = (ui.Flags & VisibleMask) != 0,
        Rect = ToRect(ui),
        ResolutionStatus = "unresolved",
        ResolutionMethod = "nearest_map_node_center_after_100px_y_offset",
        Diagnostic = diagnostic,
    };

    private static string BuildMarkerDiagnostic(int foundCount, List<AtlasPlayerMarkerObservation> markers,
        bool detectionComplete, bool markersTruncated, bool nodeEnumerationComplete)
    {
        if (foundCount == 0)
            return detectionComplete
                ? "No visible current-location marker with fingerprint 0x502EF3 was found among the Atlas children."
                : "No visible current-location marker was encountered before child enumeration stopped; absence is not conclusive because the scan was partial.";
        int resolvedCount = markers.Count(marker => marker.ResolutionStatus == "resolved_approximate");
        string truncation = markersTruncated ? $" Only the first {markers.Count} marker observations were retained." : string.Empty;
        string nodePartial = nodeEnumerationComplete ? string.Empty : " Map-node enumeration was partial, so an unresolved marker may match an uncaptured node.";
        if (resolvedCount == markers.Count && !markersTruncated)
            return $"Found and approximately resolved {foundCount} visible current-location marker(s). Marker addresses and rectangles are ephemeral.{nodePartial}";
        return $"Found {foundCount} visible current-location marker(s); {resolvedCount} captured markers were approximately resolved and {markers.Count - resolvedCount} remain unresolved.{truncation}{nodePartial} Marker addresses and rectangles are ephemeral.";
    }

    private static bool TryGetClientDimensions(Process process, out int width, out int height)
    {
        width = 0;
        height = 0;
        process.Refresh();
        IntPtr window = process.MainWindowHandle;
        if (window != IntPtr.Zero && TryGetClientDimensions(window, out width, out height)) return true;

        bool foundVisible = false;
        long largestArea = 0;
        int selectedWidth = 0;
        int selectedHeight = 0;
        uint targetPid = (uint)process.Id;
        EnumWindows((candidate, _) =>
        {
            GetWindowThreadProcessId(candidate, out uint candidatePid);
            if (candidatePid != targetPid || !TryGetClientDimensions(candidate, out int candidateWidth, out int candidateHeight))
                return true;

            bool visible = IsWindowVisible(candidate);
            long area = (long)candidateWidth * candidateHeight;
            if ((visible && !foundVisible) || (visible == foundVisible && area > largestArea))
            {
                foundVisible = visible;
                largestArea = area;
                selectedWidth = candidateWidth;
                selectedHeight = candidateHeight;
            }
            return true;
        }, IntPtr.Zero);

        if (selectedWidth <= 0 || selectedHeight <= 0) return false;
        width = selectedWidth;
        height = selectedHeight;
        return true;
    }

    private static bool TryGetClientDimensions(IntPtr window, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (window == IntPtr.Zero || !GetClientRect(window, out var rect)) return false;
        width = rect.Right - rect.Left;
        height = rect.Bottom - rect.Top;
        return width is >= 200 and <= 20_000 && height is >= 200 and <= 20_000;
    }

    private static bool TryGetUiScale(UiElementBaseOffset ui, int clientWidth, int clientHeight,
        out (float Width, float Height) scale) =>
        TryGetUiScale(ui.ScaleIndex, ui.LocalScaleMultiplier, clientWidth, clientHeight, out scale);

    private static bool TryGetUiScale(AtlasUiGeometry ui, int clientWidth, int clientHeight,
        out (float Width, float Height) scale) =>
        TryGetUiScale(ui.ScaleIndex, ui.LocalScaleMultiplier, clientWidth, clientHeight, out scale);

    private static bool TryGetUiScale(int scaleIndex, float localScaleMultiplier, int clientWidth, int clientHeight,
        out (float Width, float Height) scale)
    {
        scale = default;
        if (clientWidth <= 0 || clientHeight <= 0 || !float.IsFinite(localScaleMultiplier) || localScaleMultiplier <= 0f)
            return false;

        float horizontalBaseScale = clientWidth / 2560f;
        float verticalBaseScale = clientHeight / 1600f;
        if (clientWidth / (float)clientHeight > 2560f / 1600f)
            horizontalBaseScale = verticalBaseScale;
        float widthScale = localScaleMultiplier;
        float heightScale = localScaleMultiplier;
        switch (scaleIndex)
        {
            case 1:
                widthScale *= horizontalBaseScale;
                heightScale *= horizontalBaseScale;
                break;
            case 2:
                widthScale *= verticalBaseScale;
                heightScale *= verticalBaseScale;
                break;
            case 3:
                widthScale *= horizontalBaseScale;
                heightScale *= verticalBaseScale;
                break;
        }
        if (!float.IsFinite(widthScale) || !float.IsFinite(heightScale) || widthScale <= 0f || heightScale <= 0f)
            return false;
        scale = (widthScale, heightScale);
        return true;
    }

    private static (float X, float Y) GetSiblingScreenCenter(UiElementBaseOffset ui,
        (float Width, float Height) uiScale, UiElementBaseOffset parent,
        (float Width, float Height) parentScale)
    {
        float x = (ui.RelativePosition.X + (ui.UnscaledSize.X * 0.5f)) * uiScale.Width;
        float y = (ui.RelativePosition.Y + (ui.UnscaledSize.Y * 0.5f)) * uiScale.Height;
        if ((ui.Flags & ShouldModifyPositionMask) != 0)
        {
            x += parent.PositionModifier.X * parentScale.Width;
            y += parent.PositionModifier.Y * parentScale.Height;
        }
        return (x, y);
    }

    private static (float X, float Y) GetSiblingScreenCenter(AtlasUiGeometry ui,
        (float Width, float Height) uiScale, UiElementBaseOffset parent,
        (float Width, float Height) parentScale)
    {
        float x = (ui.RelativeX + (ui.Width * 0.5f)) * uiScale.Width;
        float y = (ui.RelativeY + (ui.Height * 0.5f)) * uiScale.Height;
        if ((ui.Flags & ShouldModifyPositionMask) != 0)
        {
            x += parent.PositionModifier.X * parentScale.Width;
            y += parent.PositionModifier.Y * parentScale.Height;
        }
        return (x, y);
    }

    private static AtlasUiGeometry ToUiGeometry(int index, long address, UiElementBaseOffset ui) => new(
        index,
        address,
        ui.RelativePosition.X,
        ui.RelativePosition.Y,
        ui.UnscaledSize.X,
        ui.UnscaledSize.Y,
        ui.ScaleIndex,
        ui.LocalScaleMultiplier,
        ui.Flags);

    private static UiRect ToRect(AtlasUiGeometry ui) => new()
    {
        X = ui.RelativeX,
        Y = ui.RelativeY,
        Width = ui.Width,
        Height = ui.Height,
        CoordinateSpace = "parent-relative game UI units",
    };

    private readonly record struct AtlasUiGeometry(
        int Index,
        long Address,
        float RelativeX,
        float RelativeY,
        float Width,
        float Height,
        int ScaleIndex,
        float LocalScaleMultiplier,
        uint Flags);

    private sealed record AtlasMarkerNearestResult(
        AtlasScanNode? NearestNode,
        AtlasScanNode? SecondNearestNode,
        float DistanceSquared,
        float? NearestDistancePixels,
        float? NextDistancePixels,
        string Status,
        string Diagnostic);

    private static string ReadMapId(IProcessMemoryReader reader, long nodeData)
    {
        if (!reader.TryRead(new IntPtr(nodeData + 0x290), out IntPtr mapData) || mapData.ToInt64() < 0x10000 ||
            !reader.TryRead(mapData, out IntPtr stringHeader) || stringHeader.ToInt64() < 0x10000 ||
            !reader.TryRead(stringHeader, out IntPtr stringBuffer) || stringBuffer.ToInt64() < 0x10000) return string.Empty;

        const int maxChars = 128;
        var chars = new char[maxChars];
        int length = 0;
        for (; length < maxChars; length++)
        {
            if (!reader.TryRead(stringBuffer + (length * sizeof(char)), out char ch)) return string.Empty;
            if (ch == '\0') break;
            chars[length] = ch;
        }
        return new string(chars, 0, length);
    }

    private static List<uint> ReadContentTokens(IProcessMemoryReader reader, long nodeAddress)
    {
        var values = new List<uint>();
        if (!reader.TryRead(new IntPtr(nodeAddress + 0x350), out StdVector vector)) return values;
        long first = vector.First.ToInt64(), last = vector.Last.ToInt64(), end = vector.End.ToInt64();
        if (first < 0x10000 || last < first || end < last || (last - first) % 4 != 0 || (end - first) % 4 != 0) return values;
        int count = (int)((last - first) / 4);
        if (count is <= 0 or > MaxContentItems || (count & 1) != 0) return values;
        for (int i = 0; i < count; i += 2)
        {
            if (!reader.TryRead(new IntPtr(first + i * 4L), out uint statId) ||
                !reader.TryRead(new IntPtr(first + (i + 1) * 4L), out uint scaledValue)) break;
            uint scaled = (uint)Math.Min((ulong)scaledValue * 64u, 0xFFFFu);
            values.Add((scaled << 16) | (statId & 0xFFFFu));
        }
        return values;
    }

    private static List<uint> ReadBadgeIds(IProcessMemoryReader reader, long nodeAddress)
    {
        var values = new List<uint>();
        if (!reader.TryRead(new IntPtr(nodeAddress + 0x368), out IntPtr firstPtr) ||
            !reader.TryRead(new IntPtr(nodeAddress + 0x370), out IntPtr lastPtr)) return values;
        long first = firstPtr.ToInt64(), last = lastPtr.ToInt64();
        if (first < 0x10000 || last < first || last - first > MaxContentItems) return values;
        for (long i = 0; i < last - first; i++)
        {
            if (reader.TryRead(new IntPtr(first + i), out byte row)) values.Add((uint)row + 100u);
        }
        return values;
    }

    private static bool IsEffectivelyVisible(IProcessMemoryReader reader, long address)
    {
        var visited = new HashSet<long>();
        int depth = 0;
        while (address >= 0x10000 && depth++ < 32 && visited.Add(address))
        {
            if (!reader.TryRead(new IntPtr(address), out UiElementBaseOffset element) || element.Self.ToInt64() != address ||
                (element.Flags & VisibleMask) == 0) return false;
            address = element.ParentPtr.ToInt64();
        }
        return address == 0;
    }

    private static AtlasUiInfo ToUiInfo(IProcessMemoryReader reader, long address, bool visible, UiElementBaseOffset ui) => new()
    {
        Address = Hex(address),
        AddressLifetime = "ephemeral",
        Visible = visible,
        Rect = ToRect(ui),
    };

    private static UiRect ToRect(UiElementBaseOffset ui) => new()
    {
        X = ui.RelativePosition.X,
        Y = ui.RelativePosition.Y,
        Width = ui.UnscaledSize.X,
        Height = ui.UnscaledSize.Y,
        CoordinateSpace = "parent-relative game UI units",
    };

    private static long ReadPointer(IProcessMemoryReader reader, long address) =>
        reader.TryRead(new IntPtr(address), out IntPtr value) ? value.ToInt64() : 0;

    private static string Hex(IntPtr value) => Hex(value.ToInt64());
    private static string Hex(long value) => value == 0 ? "0x0" : $"0x{value:X}";

    [LibraryImport("user32.dll", EntryPoint = "GetClientRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(IntPtr window, out Win32Rect rect);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate bool EnumWindowsProc(IntPtr window, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private static AtlasScanSnapshot EmptySnapshot(DateTime capturedAt, string status, string diagnostic,
        int? processId = null, string? processName = null) => new()
    {
        CapturedAtUtc = capturedAt,
        Status = status,
        Diagnostic = diagnostic,
        ProcessId = processId,
        ProcessName = processName,
        Nodes = [],
    };

}

internal sealed record ConnectionScanResult(
    Dictionary<(int X, int Y), HashSet<(int X, int Y)>> Neighbors,
    List<AtlasConnectionEdge> Edges,
    AtlasConnectionDiagnostics Diagnostics);

internal sealed record AtlasConnectionDiagnostics
{
    public string Status { get; init; } = "unknown";
    public string Diagnostic { get; init; } = string.Empty;
    public string? VectorAddress { get; init; }
    public string? First { get; init; }
    public string? Last { get; init; }
    public string? End { get; init; }
    public long EdgeCount { get; init; }
    public bool EdgesTruncated { get; init; }
    public int EdgesRead { get; init; }
    public int UsableEdges { get; init; }
    public int UniqueUndirectedConnections { get; init; }
    public int CapturedNodeCoordinatesWithConnections { get; init; }
    public int EdgesTouchingCapturedNodes { get; init; }
}

internal sealed record AtlasConnectionEdge
{
    public int Unknown { get; init; }
    public int SourceX { get; init; }
    public int SourceY { get; init; }
    public int TargetX { get; init; }
    public int TargetY { get; init; }
}

internal sealed record AtlasScanSnapshot
{
    public int Sequence { get; init; }
    public DateTime CapturedAtUtc { get; init; }
    public DateTime? PreviousCaptureTimestampUtc { get; init; }
    public string Status { get; init; } = "unknown";
    public string Diagnostic { get; init; } = string.Empty;
    public int? ProcessId { get; init; }
    public string? ProcessName { get; init; }
    public string? CurrentState { get; init; }
    public string? StaticGameStatesAddress { get; init; }
    public string? GameStatesAddress { get; init; }
    public string? InGameStateAddress { get; init; }
    public string? UiRootAddress { get; init; }
    public AreaIdentity? Area { get; init; }
    public AtlasUiInfo? Atlas { get; init; }
    public int AtlasChildCount { get; init; }
    public int NodeCount { get; init; }
    public int DetectedNodeCount { get; init; }
    public bool NodesTruncated { get; init; }
    public bool AtlasChildrenTruncated { get; init; }
    public bool DetectedNodeCountComplete { get; init; }
    public bool CaptureTimedOut { get; init; }
    public long CaptureDurationMilliseconds { get; init; }
    public AtlasConnectionDiagnostics? Connections { get; init; }
    public List<AtlasPlayerMarkerObservation> PlayerMarkers { get; init; } = [];
    public int DetectedMarkerCount { get; init; }
    public bool MarkersTruncated { get; init; }
    public bool MarkerDetectionComplete { get; init; }
    public string MarkerDiagnostic { get; init; } = "Not scanned because the Atlas panel was not resolved.";
    public string TopologyStatus { get; init; } = "unverified";
    public string? OutputFile { get; init; }
    public List<AtlasScanNode> Nodes { get; init; } = [];
}

internal sealed record AreaIdentity(string? InstanceAddress, string? AreaHash);
internal sealed record GridPoint(int X, int Y);

internal sealed record AtlasUiInfo
{
    public string Address { get; init; } = "0x0";
    public string AddressLifetime { get; init; } = "ephemeral";
    public bool Visible { get; init; }
    public UiRect Rect { get; init; } = new();
}

internal sealed record UiRect
{
    public float X { get; init; }
    public float Y { get; init; }
    public float Width { get; init; }
    public float Height { get; init; }
    public string CoordinateSpace { get; init; } = string.Empty;
}

internal sealed record AtlasScanNode
{
    public string NodeKey { get; init; } = string.Empty;
    public string MapId { get; init; } = string.Empty;
    public int Index { get; init; }
    public GridPoint Grid { get; init; } = new(0, 0);
    public byte BiomeId { get; init; }
    public string State { get; init; } = "None";
    public byte RawStatus { get; init; }
    public string RawStatusHex { get; init; } = string.Empty;
    public List<GridPoint> Connections { get; init; } = [];
    public List<uint> ContentTokens { get; init; } = [];
    public List<uint> BadgeContentIds { get; init; } = [];
    public string UiAddress { get; init; } = "0x0";
    public string? PreviousUiAddress { get; init; }
    public bool? UiAddressChanged { get; init; }
    public bool Visible { get; init; }
    public UiRect Rect { get; init; } = new();
}

internal sealed record AtlasPlayerMarkerObservation
{
    public int Index { get; init; }
    public string UiAddress { get; init; } = "0x0";
    public string AddressLifetime { get; init; } = "ephemeral";
    public bool Visible { get; init; }
    public UiRect Rect { get; init; } = new();
    public string? ResolvedNodeKey { get; init; }
    public string? ResolvedMapId { get; init; }
    public GridPoint? ResolvedGrid { get; init; }
    public string? NearestCandidateNodeKey { get; init; }
    public string? NearestCandidateMapId { get; init; }
    public GridPoint? NearestCandidateGrid { get; init; }
    public string ResolutionStatus { get; init; } = "unresolved";
    public string ResolutionMethod { get; init; } = string.Empty;
    public float? DistanceSquared { get; init; }
    public float? DistanceToNearestPixels { get; init; }
    public float? DistanceToNextNearestPixels { get; init; }
    public string Diagnostic { get; init; } = string.Empty;
}
