// <copyright file="AtlasObservationPersistence.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Systems
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using TEHhub.RemoteObjects.States.InGameStateObjects;
    using TEHhub.Utils;

    /// <summary>
    /// Loads and saves the compact, process-independent Atlas graph. Disk operations run on a
    /// background worker; live captures only signal that a coalesced write is needed.
    /// </summary>
    internal sealed class AtlasObservationPersistence
    {
        private const int SchemaVersion = 2;
        private const int MaxFileBytes = 32 * 1024 * 1024;
        private const int MaxFileNodes = 20000;
        private const int MaxConnectionsPerNode = 128;
        private const int MaxBadgesPerNode = 32;
        private static readonly TimeSpan SaveCoalesceDelay = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            WriteIndented = true,
            MaxDepth = 24,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };

        private readonly AtlasObservationCache cache;
        private readonly object lifecycleSync = new();
        private readonly object resetSync = new();
        private PersistenceRun? activeRun;
        private Task? lastWorker;

        internal AtlasObservationPersistence(AtlasObservationCache cache)
        {
            this.cache = cache;
        }

        /// <summary>
        /// Starts background loading and persistence for the runtime map file.
        /// </summary>
        /// <param name="filePath">The runtime config path for the map file.</param>
        /// <param name="log">A logger for load and save diagnostics.</param>
        internal void Start(string filePath, Action<string> log)
        {
            lock (this.lifecycleSync)
            {
                lock (this.resetSync)
                {
                    this.cache.BeginPersistenceLoad();
                }

                this.activeRun?.RequestStop();

                var run = new PersistenceRun(Path.GetFullPath(filePath), log);
                var previousWorker = this.lastWorker;
                this.activeRun = run;
                run.Worker = Task.Run(async () =>
                {
                    if (previousWorker != null)
                    {
                        await previousWorker.ConfigureAwait(false);
                    }

                    await this.RunAsync(run).ConfigureAwait(false);
                });
                this.lastWorker = run.Worker;
            }
        }

        /// <summary>
        /// Queues a coalesced save request. Safe to call from the render thread.
        /// </summary>
        internal void ScheduleSave()
        {
            PersistenceRun? run;
            lock (this.lifecycleSync)
            {
                run = this.activeRun;
            }

            run?.ScheduleSave();
        }

        /// <summary>
        /// Clears the live graph immediately and queues deletion of the saved graph on the same
        /// worker that performs loads and writes. The reset generation prevents an older startup
        /// load from merging nodes back after the user clears the cache.
        /// </summary>
        internal void ResetAtlasCache()
        {
            lock (this.lifecycleSync)
            {
                lock (this.resetSync)
                {
                    this.cache.ClearObservedGraph();
                    this.activeRun?.RequestReset();
                }
            }
        }

        /// <summary>
        /// Requests a final background flush and releases the active worker for plugin unload.
        /// </summary>
        internal void Stop()
        {
            PersistenceRun? run;
            lock (this.lifecycleSync)
            {
                run = this.activeRun;
                this.activeRun = null;
            }

            run?.RequestStop();
        }

        private async Task RunAsync(PersistenceRun run)
        {
            try
            {
                await this.LoadExistingFileAsync(run).ConfigureAwait(false);
                await this.WriteLoopAsync(run).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // A failed or stopped worker has no render-thread caller waiting on it.
            }
            catch (Exception ex)
            {
                run.Log($"Atlas persistence worker stopped: {ex.Message}");
            }
        }

        private async Task LoadExistingFileAsync(PersistenceRun run)
        {
            long resetCountAtStart;
            lock (this.resetSync)
            {
                resetCountAtStart = run.RequestedResetCount;
            }

            if (resetCountAtStart > 0)
            {
                run.Log("Skipped loading the Atlas map because a cache reset is pending.");
                this.cache.CompletePersistenceLoad();
                return;
            }

            try
            {
                if (!File.Exists(run.FilePath))
                {
                    return;
                }

                var fileInfo = new FileInfo(run.FilePath);
                if (fileInfo.Length > MaxFileBytes)
                {
                    throw new InvalidDataException($"Atlas file exceeds the {MaxFileBytes} byte limit.");
                }

                var bytes = await File.ReadAllBytesAsync(run.FilePath, run.Token).ConfigureAwait(false);
                if (bytes.Length > MaxFileBytes)
                {
                    throw new InvalidDataException($"Atlas file exceeds the {MaxFileBytes} byte limit.");
                }

                var document = JsonSerializer.Deserialize<AtlasMapFileDto>(bytes, JsonOptions)
                    ?? throw new InvalidDataException("Atlas map JSON root was null.");
                var (observations, filteredConnections) = ValidateAndCreateObservations(document);
                lock (this.resetSync)
                {
                    if (run.RequestedResetCount == resetCountAtStart)
                    {
                        this.cache.MergePersistedNodes(observations);
                        run.Log($"Loaded {observations.Count} Atlas map nodes from {run.FilePath}; ignored {filteredConnections} invalid or dangling edges.");
                    }
                    else
                    {
                        run.Log("Discarded the loaded Atlas map because the cache was reset while it was being read.");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lock (this.resetSync)
                {
                    if (run.RequestedResetCount == resetCountAtStart)
                    {
                        this.BackupRejectedFile(run, ex);
                    }
                    else
                    {
                        run.Log("Skipped preserving the rejected Atlas map because the cache was reset during loading.");
                    }
                }
            }
            finally
            {
                // Live captures may arrive while file IO is in progress. Give them IDs only
                // after persisted IDs have been validated and merged.
                this.cache.CompletePersistenceLoad();
            }
        }

        private static (List<AtlasNodeObservation> Nodes, int FilteredConnections) ValidateAndCreateObservations(AtlasMapFileDto document)
        {
            if (document.SchemaVersion != SchemaVersion)
            {
                throw new InvalidDataException($"Unsupported Atlas map schema version {document.SchemaVersion}.");
            }

            if (document.Nodes == null || document.Nodes.Count > MaxFileNodes)
            {
                throw new InvalidDataException("Atlas map node list is missing or exceeds the supported limit.");
            }

            var candidates = new List<AtlasPersistedNodeDto>(document.Nodes.Count);
            var nodeIds = new HashSet<int>();
            var identities = new HashSet<AtlasNodeIdentity>();
            var nodesById = new Dictionary<int, AtlasPersistedNodeDto>(document.Nodes.Count);
            var nodesByPosition = new Dictionary<AtlasGridPosition, List<AtlasPersistedNodeDto>>();
            foreach (var node in document.Nodes)
            {
                if (node == null ||
                    node.NodeId <= 0 ||
                    node.NodeId > MaxFileNodes ||
                    !nodeIds.Add(node.NodeId) ||
                    !AtlasObservationCache.IsPlausibleMapId(node.MapId) ||
                    string.IsNullOrWhiteSpace(node.Name) || node.Name.Length > 256 ||
                    !IsValidType(node.Type) ||
                    !AtlasObservationCache.IsValidGridPosition(new AtlasGridPosition(node.GridX, node.GridY)) ||
                    node.FirstSeenUtc == default ||
                    node.LastObservedAtUtc == default ||
                    node.FirstSeenUtc > node.LastObservedAtUtc ||
                    !Enum.IsDefined(node.LastObservedState) ||
                    node.ConnectedNodeIds == null || node.ConnectedNodeIds.Count > MaxConnectionsPerNode ||
                    node.ConnectedGridPositions == null || node.ConnectedGridPositions.Count > MaxConnectionsPerNode ||
                    node.BadgeContentIds == null || node.BadgeContentIds.Count > MaxBadgesPerNode ||
                    node.BadgeContentIds.Any(id => !IsValidBadgeId(id)))
                {
                    throw new InvalidDataException("Atlas map contains an invalid node, duplicate node ID/identity, or out-of-range list.");
                }

                var identity = new AtlasNodeIdentity(node.GridX, node.GridY, node.MapId!);
                if (!identities.Add(identity))
                {
                    throw new InvalidDataException("Atlas map contains a duplicate grid and MapId identity.");
                }

                candidates.Add(node);
                nodesById.Add(node.NodeId, node);
                var position = new AtlasGridPosition(node.GridX, node.GridY);
                if (!nodesByPosition.TryGetValue(position, out var atPosition))
                {
                    atPosition = new List<AtlasPersistedNodeDto>(1);
                    nodesByPosition.Add(position, atPosition);
                }

                atPosition.Add(node);
            }

            var adjacency = candidates.ToDictionary(node => node.NodeId, _ => new HashSet<int>());
            var filteredConnections = 0;
            foreach (var node in candidates)
            {
                foreach (var neighborId in node.ConnectedNodeIds!)
                {
                    if (neighborId <= 0 || neighborId == node.NodeId || !nodesById.ContainsKey(neighborId))
                    {
                        filteredConnections++;
                        continue;
                    }

                    adjacency[node.NodeId].Add(neighborId);
                    adjacency[neighborId].Add(node.NodeId);
                }

                var ownPosition = new AtlasGridPosition(node.GridX, node.GridY);
                foreach (var neighborPositionDto in node.ConnectedGridPositions!)
                {
                    var neighborPosition = new AtlasGridPosition(neighborPositionDto.X, neighborPositionDto.Y);
                    if (neighborPosition == ownPosition ||
                        !AtlasObservationCache.IsValidGridPosition(neighborPosition) ||
                        !nodesByPosition.TryGetValue(neighborPosition, out var targets) ||
                        targets.Count != 1)
                    {
                        filteredConnections++;
                        continue;
                    }

                    var neighbor = targets[0];
                    if (neighbor.NodeId != node.NodeId)
                    {
                        adjacency[node.NodeId].Add(neighbor.NodeId);
                        adjacency[neighbor.NodeId].Add(node.NodeId);
                    }
                }
            }

            var result = new List<AtlasNodeObservation>(candidates.Count);
            foreach (var node in candidates)
            {
                var connectedPositions = adjacency[node.NodeId]
                    .Select(id => nodesById[id])
                    .Select(target => new AtlasGridPosition(target.GridX, target.GridY))
                    .Distinct()
                    .OrderBy(position => position.X)
                    .ThenBy(position => position.Y)
                    .ToArray();

                result.Add(new AtlasNodeObservation(
                    new AtlasNodeIdentity(node.GridX, node.GridY, node.MapId!),
                    -1,
                    node.BiomeId,
                    0,
                    node.LastObservedState,
                    node.FirstSeenUtc,
                    node.LastObservedAtUtc,
                    1,
                    connectedPositions,
                    Array.Empty<uint>(),
                    node.BadgeContentIds!.Select(NormalizeBadgeId).Distinct().ToArray(),
                    Array.Empty<string>(),
                    Array.Empty<AtlasNodePointerObservation>(),
                    node.NodeId));
            }

            return (result, filteredConnections);
        }

        private static bool IsValidType(string? type) =>
            string.Equals(type, "normal", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "unique", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "unknown", StringComparison.OrdinalIgnoreCase);

        private static bool IsValidBadgeId(uint id) => id != 0 && (id <= 0xFFFFu || (id >> 16) == 2);

        private static uint NormalizeBadgeId(uint id) => 0x00020000u | (id & 0xFFFFu);

        private void BackupRejectedFile(PersistenceRun run, Exception reason)
        {
            try
            {
                var backupPath = $"{run.FilePath}.invalid-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.bak";
                File.Move(run.FilePath, backupPath);
                run.Log($"Atlas map file was rejected ({reason.Message}) and preserved as {backupPath}.");
            }
            catch (Exception backupException)
            {
                run.DisableWrites();
                run.Log($"Atlas map file could not be loaded ({reason.Message}) or safely preserved ({backupException.Message}); persistence is disabled for this session.");
            }
        }

        private async Task WriteLoopAsync(PersistenceRun run)
        {
            long savedRequest = 0;
            long deletedResetCount = 0;
            while (true)
            {
                await run.WaitForSignalAsync().ConfigureAwait(false);
                if (!run.IsStopping)
                {
                    await Task.Delay(SaveCoalesceDelay, run.Token).ConfigureAwait(false);
                    run.DrainSignals();
                }

                var resetRequest = run.RequestedResetCount;
                if (resetRequest > deletedResetCount)
                {
                    try
                    {
                        File.Delete(run.FilePath);
                        deletedResetCount = resetRequest;
                        run.Log($"Cleared the saved Atlas map at {run.FilePath}.");
                    }
                    catch (Exception ex)
                    {
                        run.Log($"Atlas map reset failed: {ex.Message}. Will retry.");
                        if (run.IsStopping)
                        {
                            return;
                        }

                        await Task.Delay(RetryDelay, run.Token).ConfigureAwait(false);
                        run.SignalRetry();
                        continue;
                    }
                }

                if (!run.CanWrite)
                {
                    if (run.IsStopping)
                    {
                        return;
                    }

                    continue;
                }

                var request = run.RequestedSaveCount;
                if (request > savedRequest)
                {
                    try
                    {
                        var snapshot = this.cache.GetSnapshot();
                        var document = CreateDocument(snapshot);
                        if (document.Nodes?.Count > 0)
                        {
                            await this.WriteAtomicAsync(run, document).ConfigureAwait(false);
                        }

                        savedRequest = request;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        run.Log($"Atlas map save failed: {ex.Message}. Will retry.");
                        if (run.IsStopping)
                        {
                            return;
                        }

                        await Task.Delay(RetryDelay, run.Token).ConfigureAwait(false);
                        run.SignalRetry();
                        continue;
                    }
                }

                if (run.IsStopping && run.RequestedResetCount <= deletedResetCount)
                {
                    return;
                }

                if (run.RequestedResetCount > deletedResetCount)
                {
                    run.SignalRetry();
                    continue;
                }

                if (run.RequestedSaveCount > savedRequest)
                {
                    run.SignalRetry();
                }
            }
        }

        private static AtlasMapFileDto CreateDocument(AtlasObservationSnapshot snapshot)
        {
            var eligibleNodes = snapshot.Nodes
                .Where(node => node.PersistentNodeId > 0 &&
                               AtlasObservationCache.IsPlausibleMapId(node.Identity.MapId) &&
                               AtlasObservationCache.IsValidGridPosition(new AtlasGridPosition(node.Identity.GridX, node.Identity.GridY)))
                .OrderBy(node => node.PersistentNodeId)
                .ToList();
            if (eligibleNodes.Count > MaxFileNodes)
            {
                throw new InvalidDataException($"Atlas map output exceeds the {MaxFileNodes} node limit.");
            }

            var byId = new Dictionary<int, AtlasNodeObservation>(eligibleNodes.Count);
            var byPosition = new Dictionary<AtlasGridPosition, List<AtlasNodeObservation>>();
            foreach (var node in eligibleNodes)
            {
                if (!byId.TryAdd(node.PersistentNodeId, node))
                {
                    throw new InvalidDataException($"Atlas cache contains duplicate persistent node ID {node.PersistentNodeId}.");
                }

                var position = new AtlasGridPosition(node.Identity.GridX, node.Identity.GridY);
                if (!byPosition.TryGetValue(position, out var atPosition))
                {
                    atPosition = new List<AtlasNodeObservation>(1);
                    byPosition.Add(position, atPosition);
                }

                atPosition.Add(node);
            }

            var adjacency = eligibleNodes.ToDictionary(node => node.PersistentNodeId, _ => new HashSet<int>());
            var gridHints = eligibleNodes.ToDictionary(node => node.PersistentNodeId, _ => new HashSet<AtlasGridPosition>());
            foreach (var node in eligibleNodes)
            {
                var ownPosition = new AtlasGridPosition(node.Identity.GridX, node.Identity.GridY);
                foreach (var neighborPosition in node.ConnectedGridPositions)
                {
                    if (neighborPosition == ownPosition ||
                        !AtlasObservationCache.IsValidGridPosition(neighborPosition) ||
                        !byPosition.TryGetValue(neighborPosition, out var targets))
                    {
                        continue;
                    }

                    gridHints[node.PersistentNodeId].Add(neighborPosition);
                    if (targets.Count != 1)
                    {
                        continue;
                    }

                    var targetId = targets[0].PersistentNodeId;
                    if (targetId != node.PersistentNodeId)
                    {
                        adjacency[node.PersistentNodeId].Add(targetId);
                        adjacency[targetId].Add(node.PersistentNodeId);
                        gridHints[targetId].Add(ownPosition);
                    }
                }
            }

            var nodes = eligibleNodes.Select(node =>
            {
                var badgeIds = node.BadgeContentIds
                    .Where(IsValidBadgeId)
                    .Select(NormalizeBadgeId)
                    .Distinct()
                    .Take(MaxBadgesPerNode)
                    .OrderBy(id => id)
                    .ToList();
                var meta = WorldAreaTags.GetMeta(node.Identity.MapId);
                var mapType = meta?.Type;
                if (!IsValidType(mapType))
                {
                    mapType = "unknown";
                }

                var mapName = WorldAreaNames.GetDisplayName(node.Identity.MapId);
                if (string.IsNullOrWhiteSpace(mapName) || mapName.Length > 256)
                {
                    mapName = node.Identity.MapId;
                }

                return new AtlasPersistedNodeDto
                {
                    NodeId = node.PersistentNodeId,
                    GridX = node.Identity.GridX,
                    GridY = node.Identity.GridY,
                    MapId = node.Identity.MapId,
                    Name = mapName,
                    Type = mapType ?? "unknown",
                    BiomeId = node.BiomeId,
                    ConnectedNodeIds = adjacency[node.PersistentNodeId]
                        .OrderBy(id => id)
                        .Take(MaxConnectionsPerNode)
                        .ToList(),
                    ConnectedGridPositions = gridHints[node.PersistentNodeId]
                        .OrderBy(position => position.X)
                        .ThenBy(position => position.Y)
                        .Take(MaxConnectionsPerNode)
                        .Select(position => new AtlasPersistedGridPositionDto { X = position.X, Y = position.Y })
                        .ToList(),
                    BadgeContentIds = badgeIds,
                    FirstSeenUtc = node.FirstSeenUtc,
                    LastObservedAtUtc = node.LastSeenUtc,
                    LastObservedState = node.State,
                };
            }).ToList();

            return new AtlasMapFileDto
            {
                SchemaVersion = SchemaVersion,
                UpdatedAtUtc = DateTime.UtcNow,
                Nodes = nodes,
            };
        }

        private async Task WriteAtomicAsync(PersistenceRun run, AtlasMapFileDto document)
        {
            var directory = Path.GetDirectoryName(run.FilePath)
                ?? throw new InvalidOperationException("Atlas map path has no parent directory.");
            Directory.CreateDirectory(directory);

            var temporaryPath = $"{run.FilePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                var contents = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
                if (contents.Length > MaxFileBytes)
                {
                    throw new InvalidDataException($"Atlas map output exceeds the {MaxFileBytes} byte limit.");
                }

                await using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    65536,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(contents, run.Token).ConfigureAwait(false);
                    await stream.FlushAsync(run.Token).ConfigureAwait(false);
                    stream.Flush(true);
                }

                if (File.Exists(run.FilePath))
                {
                    try
                    {
                        File.Replace(temporaryPath, run.FilePath, null, ignoreMetadataErrors: true);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        File.Move(temporaryPath, run.FilePath, overwrite: true);
                    }
                }
                else
                {
                    File.Move(temporaryPath, run.FilePath);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        private sealed class AtlasMapFileDto
        {
            public int SchemaVersion { get; set; }

            public DateTime UpdatedAtUtc { get; set; }

            public List<AtlasPersistedNodeDto>? Nodes { get; set; }
        }

        private sealed class AtlasPersistedNodeDto
        {
            public int NodeId { get; set; }

            public int GridX { get; set; }

            public int GridY { get; set; }

            public string? MapId { get; set; }

            public string? Name { get; set; }

            public string? Type { get; set; }

            public byte BiomeId { get; set; }

            public List<int>? ConnectedNodeIds { get; set; }

            public List<AtlasPersistedGridPositionDto>? ConnectedGridPositions { get; set; }

            public List<uint>? BadgeContentIds { get; set; }

            public DateTime FirstSeenUtc { get; set; }

            public DateTime LastObservedAtUtc { get; set; }

            public AtlasMapNodeState LastObservedState { get; set; }
        }

        private sealed class AtlasPersistedGridPositionDto
        {
            public int X { get; set; }

            public int Y { get; set; }
        }

        private sealed class PersistenceRun
        {
            private readonly SemaphoreSlim signal = new(0, 1);
            private long requestedSaveCount;
            private long requestedResetCount;
            private int stopping;
            private int canWrite = 1;

            internal PersistenceRun(string filePath, Action<string> log)
            {
                this.FilePath = filePath;
                this.LogAction = log;
            }

            internal string FilePath { get; }

            internal Action<string> LogAction { get; }

            internal CancellationToken Token => CancellationToken.None;

            internal Task? Worker { get; set; }

            internal bool IsStopping => Volatile.Read(ref this.stopping) != 0;

            internal bool CanWrite => Volatile.Read(ref this.canWrite) != 0;

            internal long RequestedSaveCount => Interlocked.Read(ref this.requestedSaveCount);

            internal long RequestedResetCount => Interlocked.Read(ref this.requestedResetCount);

            internal void ScheduleSave()
            {
                Interlocked.Increment(ref this.requestedSaveCount);
                this.ReleaseSignal();
            }

            internal void RequestReset()
            {
                Interlocked.Increment(ref this.requestedResetCount);
                this.ReleaseSignal();
            }

            internal void RequestStop()
            {
                Volatile.Write(ref this.stopping, 1);
                this.ReleaseSignal();
            }

            internal async Task WaitForSignalAsync()
            {
                await this.signal.WaitAsync().ConfigureAwait(false);
            }

            internal void DrainSignals()
            {
                while (this.signal.Wait(0))
                {
                }
            }

            internal void SignalRetry() => this.ReleaseSignal();

            internal void DisableWrites() => Volatile.Write(ref this.canWrite, 0);

            internal void Log(string message)
            {
                try
                {
                    this.LogAction(message);
                }
                catch
                {
                    // A diagnostic callback must not terminate persistence.
                }
            }

            private void ReleaseSignal()
            {
                try
                {
                    this.signal.Release();
                }
                catch (SemaphoreFullException)
                {
                    // One pending signal is sufficient; the worker reads the latest cache state.
                }
            }
        }
    }
}
