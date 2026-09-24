// <copyright file="AtlasObservationPersistence.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Systems
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.Sqlite;
    using TEHhub.RemoteObjects.States.InGameStateObjects;
    using TEHhub.Utils;

    /// <summary>
    /// Loads and saves the compact, process-independent Atlas graph. Disk operations run on a
    /// background worker; live captures only signal that a coalesced write is needed.
    /// </summary>
    internal sealed class AtlasObservationPersistence
    {
        private const int LegacySchemaVersion = 2;
        private const int DatabaseSchemaVersion = 1;
        private const int MaxLegacyFileBytes = 32 * 1024 * 1024;
        private const long MaxDatabaseBytes = 128L * 1024 * 1024;
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
        /// <param name="filePath">The runtime path for the SQLite map database.</param>
        /// <param name="log">A logger for load and save diagnostics.</param>
        /// <param name="legacyFilePath">The previous JSON map path, if a migration source exists.</param>
        internal void Start(string filePath, Action<string> log, string? legacyFilePath = null)
        {
            lock (this.lifecycleSync)
            {
                lock (this.resetSync)
                {
                    this.cache.BeginPersistenceLoad();
                }

                this.activeRun?.RequestStop();

                var run = new PersistenceRun(
                    Path.GetFullPath(filePath),
                    legacyFilePath == null ? null : Path.GetFullPath(legacyFilePath),
                    log);
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
            using var persistenceGate = CreatePersistenceGate(run.FilePath);
            var ownsPersistenceGate = false;
            try
            {
                // AutoExile2 can be reloaded into a new collectible context before this worker
                // finishes. Serialize the complete load/write lifetime across those contexts so
                // the next instance assigns IDs only after the final flush is visible.
                persistenceGate.WaitOne();
                ownsPersistenceGate = true;
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
            finally
            {
                if (ownsPersistenceGate)
                {
                    persistenceGate.Release();
                }
            }
        }

        private static Semaphore CreatePersistenceGate(string databasePath)
        {
            var pathHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(databasePath.ToUpperInvariant())));
            return new Semaphore(1, 1, $"Local\\TEHhub.AutoExile2.AtlasPersistence.{pathHash}");
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

            var persistedNodes = new List<AtlasNodeObservation>();
            var databaseLoaded = false;
            var legacyLoaded = false;
            try
            {
                if (File.Exists(run.FilePath))
                {
                    try
                    {
                        var (databaseNodes, filteredConnections) = LoadDatabase(run.FilePath);
                        persistedNodes.AddRange(databaseNodes);
                        databaseLoaded = databaseNodes.Count > 0;
                        run.Log($"Loaded {databaseNodes.Count} Atlas map nodes from {run.FilePath}; ignored {filteredConnections} invalid or dangling edges.");
                    }
                    catch (Exception ex)
                    {
                        lock (this.resetSync)
                        {
                            if (run.RequestedResetCount == resetCountAtStart)
                            {
                                this.BackupRejectedFile(run, run.FilePath, ex, isSqlite: true);
                            }
                            else
                            {
                                run.Log("Skipped preserving the rejected Atlas database because the cache was reset during loading.");
                            }
                        }
                    }
                }

                if (run.LegacyFilePath != null && File.Exists(run.LegacyFilePath))
                {
                    try
                    {
                        var legacyNodes = await LoadLegacyFileAsync(run.LegacyFilePath, run.Token).ConfigureAwait(false);
                        if (!databaseLoaded)
                        {
                            persistedNodes.AddRange(legacyNodes);
                        }

                        legacyLoaded = true;
                        run.Log(databaseLoaded
                            ? $"Validated the legacy Atlas map at {run.LegacyFilePath}; the SQLite database is authoritative and will replace it after a durable checkpoint."
                            : $"Loaded {legacyNodes.Count} legacy Atlas nodes from {run.LegacyFilePath}; SQLite will absorb the map.");
                    }
                    catch (Exception ex)
                    {
                        lock (this.resetSync)
                        {
                            if (run.RequestedResetCount == resetCountAtStart)
                            {
                                this.BackupRejectedFile(run, run.LegacyFilePath, ex, isSqlite: false);
                            }
                            else
                            {
                                run.Log("Skipped preserving the rejected legacy Atlas map because the cache was reset during loading.");
                            }
                        }
                    }
                }

                lock (this.resetSync)
                {
                    if (run.RequestedResetCount == resetCountAtStart)
                    {
                        if (persistedNodes.Count > 0)
                        {
                            this.cache.MergePersistedNodes(persistedNodes);
                        }

                        if (legacyLoaded)
                        {
                            run.RequestMigration();
                        }
                    }
                    else
                    {
                        run.Log("Discarded the loaded Atlas map because the cache was reset while it was being read.");
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

        private static async Task<List<AtlasNodeObservation>> LoadLegacyFileAsync(string filePath, CancellationToken cancellationToken)
        {
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length > MaxLegacyFileBytes)
            {
                throw new InvalidDataException($"Atlas JSON file exceeds the {MaxLegacyFileBytes} byte limit.");
            }

            var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
            if (bytes.Length > MaxLegacyFileBytes)
            {
                throw new InvalidDataException($"Atlas JSON file exceeds the {MaxLegacyFileBytes} byte limit.");
            }

            var document = JsonSerializer.Deserialize<AtlasMapFileDto>(bytes, JsonOptions)
                ?? throw new InvalidDataException("Atlas map JSON root was null.");
            return ValidateAndCreateObservations(document).Nodes;
        }

        private static (List<AtlasNodeObservation> Nodes, int FilteredConnections) ValidateAndCreateObservations(AtlasMapFileDto document)
        {
            if (document.SchemaVersion != LegacySchemaVersion)
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

        private static (List<AtlasNodeObservation> Nodes, int FilteredConnections) LoadDatabase(string filePath)
        {
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length > MaxDatabaseBytes)
            {
                throw new InvalidDataException($"Atlas database exceeds the {MaxDatabaseBytes} byte limit.");
            }

            using var connection = OpenDatabase(filePath);
            EnsureDatabaseSchema(connection);
            using (var integrityCommand = connection.CreateCommand())
            {
                integrityCommand.CommandText = "PRAGMA quick_check(1);";
                var integrityResult = Convert.ToString(integrityCommand.ExecuteScalar());
                if (!string.Equals(integrityResult, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Atlas database integrity check failed: {integrityResult}");
                }
            }

            var nodesById = new Dictionary<int, AtlasPersistedNodeDto>();
            using (var nodeCommand = connection.CreateCommand())
            {
                nodeCommand.CommandText = "SELECT NodeId, GridX, GridY, MapId, Name, Type, BiomeId, FirstSeenUtcTicks, LastObservedUtcTicks, LastObservedState FROM Nodes ORDER BY NodeId;";
                using var reader = nodeCommand.ExecuteReader();
                while (reader.Read())
                {
                    if (nodesById.Count >= MaxFileNodes)
                    {
                        throw new InvalidDataException($"Atlas database exceeds the {MaxFileNodes} node limit.");
                    }

                    var node = new AtlasPersistedNodeDto
                    {
                        NodeId = reader.GetInt32(0),
                        GridX = reader.GetInt32(1),
                        GridY = reader.GetInt32(2),
                        MapId = reader.GetString(3),
                        Name = reader.GetString(4),
                        Type = reader.GetString(5),
                        BiomeId = checked((byte)reader.GetInt32(6)),
                        FirstSeenUtc = ReadUtcDateTime(reader.GetInt64(7)),
                        LastObservedAtUtc = ReadUtcDateTime(reader.GetInt64(8)),
                        LastObservedState = (AtlasMapNodeState)reader.GetInt32(9),
                        ConnectedNodeIds = new List<int>(),
                        ConnectedGridPositions = new List<AtlasPersistedGridPositionDto>(),
                        BadgeContentIds = new List<uint>(),
                    };
                    if (!nodesById.TryAdd(node.NodeId, node))
                    {
                        throw new InvalidDataException($"Atlas database contains duplicate persistent node ID {node.NodeId}.");
                    }
                }
            }

            using (var connectionCommand = connection.CreateCommand())
            {
                connectionCommand.CommandText = "SELECT NodeId, TargetX, TargetY FROM Connections ORDER BY NodeId, TargetX, TargetY;";
                using var reader = connectionCommand.ExecuteReader();
                while (reader.Read())
                {
                    if (!nodesById.TryGetValue(reader.GetInt32(0), out var node) || node.ConnectedGridPositions!.Count >= MaxConnectionsPerNode)
                    {
                        throw new InvalidDataException("Atlas database contains a dangling or excessive connection.");
                    }

                    node.ConnectedGridPositions.Add(new AtlasPersistedGridPositionDto
                    {
                        X = reader.GetInt32(1),
                        Y = reader.GetInt32(2),
                    });
                }
            }

            using (var badgeCommand = connection.CreateCommand())
            {
                badgeCommand.CommandText = "SELECT NodeId, ContentId FROM Badges ORDER BY NodeId, ContentId;";
                using var reader = badgeCommand.ExecuteReader();
                while (reader.Read())
                {
                    if (!nodesById.TryGetValue(reader.GetInt32(0), out var node) || node.BadgeContentIds!.Count >= MaxBadgesPerNode)
                    {
                        throw new InvalidDataException("Atlas database contains a dangling or excessive badge.");
                    }

                    node.BadgeContentIds.Add(checked((uint)reader.GetInt64(1)));
                }
            }

            var document = new AtlasMapFileDto
            {
                SchemaVersion = LegacySchemaVersion,
                UpdatedAtUtc = DateTime.UtcNow,
                Nodes = nodesById.Values.OrderBy(node => node.NodeId).ToList(),
            };
            return ValidateAndCreateObservations(document);
        }

        private static DateTime ReadUtcDateTime(long ticks)
        {
            if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
            {
                throw new InvalidDataException("Atlas database contains an invalid UTC timestamp.");
            }

            return new DateTime(ticks, DateTimeKind.Utc);
        }

        private static SqliteConnection OpenDatabase(string filePath)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = filePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                DefaultTimeout = 5,
                ForeignKeys = true,
            }.ToString());
            connection.Open();
            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA wal_autocheckpoint=1000;";
                command.ExecuteNonQuery();
                return connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        private static void EnsureDatabaseSchema(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            var schemaVersion = Convert.ToInt32(command.ExecuteScalar());
            if (schemaVersion == DatabaseSchemaVersion)
            {
                return;
            }

            if (schemaVersion != 0)
            {
                throw new InvalidDataException($"Unsupported Atlas database schema version {schemaVersion}.");
            }

            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
            if (Convert.ToInt32(command.ExecuteScalar()) != 0)
            {
                throw new InvalidDataException("Atlas database has tables but no recognized schema version.");
            }

            using var transaction = connection.BeginTransaction();
            command.Transaction = transaction;
            command.CommandText = "CREATE TABLE Nodes (NodeId INTEGER NOT NULL PRIMARY KEY CHECK(NodeId > 0 AND NodeId <= 20000), GridX INTEGER NOT NULL, GridY INTEGER NOT NULL, MapId TEXT NOT NULL, Name TEXT NOT NULL, Type TEXT NOT NULL, BiomeId INTEGER NOT NULL CHECK(BiomeId BETWEEN 0 AND 255), FirstSeenUtcTicks INTEGER NOT NULL, LastObservedUtcTicks INTEGER NOT NULL, LastObservedState INTEGER NOT NULL, UNIQUE(GridX, GridY, MapId), CHECK(FirstSeenUtcTicks <= LastObservedUtcTicks)); CREATE TABLE Connections (NodeId INTEGER NOT NULL REFERENCES Nodes(NodeId) ON DELETE CASCADE, TargetX INTEGER NOT NULL CHECK(TargetX BETWEEN -100000 AND 100000), TargetY INTEGER NOT NULL CHECK(TargetY BETWEEN -100000 AND 100000), PRIMARY KEY(NodeId, TargetX, TargetY)); CREATE TABLE Badges (NodeId INTEGER NOT NULL REFERENCES Nodes(NodeId) ON DELETE CASCADE, ContentId INTEGER NOT NULL CHECK(ContentId BETWEEN 131072 AND 196607), PRIMARY KEY(NodeId, ContentId)); PRAGMA user_version=1;";
            command.ExecuteNonQuery();
            transaction.Commit();
        }

        private static void SaveDatabase(string filePath, IReadOnlyList<AtlasNodeObservation> nodes)
        {
            var directory = Path.GetDirectoryName(filePath)
                ?? throw new InvalidOperationException("Atlas database path has no parent directory.");
            Directory.CreateDirectory(directory);
            using var connection = OpenDatabase(filePath);
            EnsureDatabaseSchema(connection);
            using var transaction = connection.BeginTransaction();

            using var nodeCommand = connection.CreateCommand();
            nodeCommand.Transaction = transaction;
            nodeCommand.CommandText = "INSERT INTO Nodes (NodeId, GridX, GridY, MapId, Name, Type, BiomeId, FirstSeenUtcTicks, LastObservedUtcTicks, LastObservedState) VALUES ($id, $x, $y, $mapId, $name, $type, $biomeId, $firstSeen, $lastSeen, $state) ON CONFLICT(NodeId) DO UPDATE SET GridX=excluded.GridX, GridY=excluded.GridY, MapId=excluded.MapId, Name=excluded.Name, Type=excluded.Type, BiomeId=excluded.BiomeId, FirstSeenUtcTicks=excluded.FirstSeenUtcTicks, LastObservedUtcTicks=excluded.LastObservedUtcTicks, LastObservedState=excluded.LastObservedState;";
            nodeCommand.Parameters.Add("$id", SqliteType.Integer);
            nodeCommand.Parameters.Add("$x", SqliteType.Integer);
            nodeCommand.Parameters.Add("$y", SqliteType.Integer);
            nodeCommand.Parameters.Add("$mapId", SqliteType.Text);
            nodeCommand.Parameters.Add("$name", SqliteType.Text);
            nodeCommand.Parameters.Add("$type", SqliteType.Text);
            nodeCommand.Parameters.Add("$biomeId", SqliteType.Integer);
            nodeCommand.Parameters.Add("$firstSeen", SqliteType.Integer);
            nodeCommand.Parameters.Add("$lastSeen", SqliteType.Integer);
            nodeCommand.Parameters.Add("$state", SqliteType.Integer);
            nodeCommand.Prepare();

            using var clearConnectionsCommand = connection.CreateCommand();
            clearConnectionsCommand.Transaction = transaction;
            clearConnectionsCommand.CommandText = "DELETE FROM Connections WHERE NodeId=$id;";
            clearConnectionsCommand.Parameters.Add("$id", SqliteType.Integer);
            clearConnectionsCommand.Prepare();

            using var insertConnectionCommand = connection.CreateCommand();
            insertConnectionCommand.Transaction = transaction;
            insertConnectionCommand.CommandText = "INSERT INTO Connections (NodeId, TargetX, TargetY) VALUES ($id, $x, $y);";
            insertConnectionCommand.Parameters.Add("$id", SqliteType.Integer);
            insertConnectionCommand.Parameters.Add("$x", SqliteType.Integer);
            insertConnectionCommand.Parameters.Add("$y", SqliteType.Integer);
            insertConnectionCommand.Prepare();

            using var clearBadgesCommand = connection.CreateCommand();
            clearBadgesCommand.Transaction = transaction;
            clearBadgesCommand.CommandText = "DELETE FROM Badges WHERE NodeId=$id;";
            clearBadgesCommand.Parameters.Add("$id", SqliteType.Integer);
            clearBadgesCommand.Prepare();

            using var insertBadgeCommand = connection.CreateCommand();
            insertBadgeCommand.Transaction = transaction;
            insertBadgeCommand.CommandText = "INSERT INTO Badges (NodeId, ContentId) VALUES ($id, $contentId);";
            insertBadgeCommand.Parameters.Add("$id", SqliteType.Integer);
            insertBadgeCommand.Parameters.Add("$contentId", SqliteType.Integer);
            insertBadgeCommand.Prepare();

            foreach (var observation in nodes)
            {
                var node = CreatePersistedNode(observation);
                nodeCommand.Parameters["$id"].Value = node.NodeId;
                nodeCommand.Parameters["$x"].Value = node.GridX;
                nodeCommand.Parameters["$y"].Value = node.GridY;
                nodeCommand.Parameters["$mapId"].Value = node.MapId!;
                nodeCommand.Parameters["$name"].Value = node.Name!;
                nodeCommand.Parameters["$type"].Value = node.Type!;
                nodeCommand.Parameters["$biomeId"].Value = node.BiomeId;
                nodeCommand.Parameters["$firstSeen"].Value = node.FirstSeenUtc.Ticks;
                nodeCommand.Parameters["$lastSeen"].Value = node.LastObservedAtUtc.Ticks;
                nodeCommand.Parameters["$state"].Value = (int)node.LastObservedState;
                nodeCommand.ExecuteNonQuery();

                clearConnectionsCommand.Parameters["$id"].Value = node.NodeId;
                clearConnectionsCommand.ExecuteNonQuery();
                foreach (var position in node.ConnectedGridPositions!)
                {
                    insertConnectionCommand.Parameters["$id"].Value = node.NodeId;
                    insertConnectionCommand.Parameters["$x"].Value = position.X;
                    insertConnectionCommand.Parameters["$y"].Value = position.Y;
                    insertConnectionCommand.ExecuteNonQuery();
                }

                clearBadgesCommand.Parameters["$id"].Value = node.NodeId;
                clearBadgesCommand.ExecuteNonQuery();
                foreach (var badgeId in node.BadgeContentIds!)
                {
                    insertBadgeCommand.Parameters["$id"].Value = node.NodeId;
                    insertBadgeCommand.Parameters["$contentId"].Value = badgeId;
                    insertBadgeCommand.ExecuteNonQuery();
                }
            }

            transaction.Commit();
        }

        private static AtlasPersistedNodeDto CreatePersistedNode(AtlasNodeObservation node)
        {
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
                ConnectedNodeIds = new List<int>(),
                ConnectedGridPositions = node.ConnectedGridPositions
                    .Where(position => position != new AtlasGridPosition(node.Identity.GridX, node.Identity.GridY) &&
                                       AtlasObservationCache.IsValidGridPosition(position))
                    .Distinct()
                    .OrderBy(position => position.X)
                    .ThenBy(position => position.Y)
                    .Take(MaxConnectionsPerNode)
                    .Select(position => new AtlasPersistedGridPositionDto { X = position.X, Y = position.Y })
                    .ToList(),
                BadgeContentIds = node.BadgeContentIds
                    .Where(IsValidBadgeId)
                    .Select(NormalizeBadgeId)
                    .Distinct()
                    .Take(MaxBadgesPerNode)
                    .OrderBy(id => id)
                    .ToList(),
                FirstSeenUtc = node.FirstSeenUtc,
                LastObservedAtUtc = node.LastSeenUtc,
                LastObservedState = node.State,
            };
        }

        private static bool IsValidType(string? type) =>
            string.Equals(type, "normal", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "unique", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "unknown", StringComparison.OrdinalIgnoreCase);

        private static bool IsValidBadgeId(uint id) => id != 0 && (id <= 0xFFFFu || (id >> 16) == 2);

        private static uint NormalizeBadgeId(uint id) => 0x00020000u | (id & 0xFFFFu);

        private void BackupRejectedFile(PersistenceRun run, string filePath, Exception reason, bool isSqlite)
        {
            try
            {
                var backupPath = $"{filePath}.invalid-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.bak";
                var suffixes = isSqlite ? new[] { string.Empty, "-wal", "-shm" } : new[] { string.Empty };
                var movedAny = false;
                foreach (var suffix in suffixes)
                {
                    var source = filePath + suffix;
                    if (!File.Exists(source))
                    {
                        continue;
                    }

                    File.Move(source, backupPath + suffix);
                    movedAny = true;
                }

                if (!movedAny)
                {
                    throw new FileNotFoundException("The rejected Atlas file disappeared before it could be preserved.", filePath);
                }

                run.Log($"Atlas map data was rejected ({reason.Message}) and preserved as {backupPath}.");
            }
            catch (Exception backupException)
            {
                run.DisableWrites();
                run.Log($"Atlas map data could not be loaded ({reason.Message}) or safely preserved ({backupException.Message}); persistence is disabled for this session.");
            }
        }

        private static void DeleteSavedAtlasData(PersistenceRun run)
        {
            File.Delete(run.FilePath);
            File.Delete(run.FilePath + "-wal");
            File.Delete(run.FilePath + "-shm");
            if (run.LegacyFilePath != null)
            {
                File.Delete(run.LegacyFilePath);
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
                        DeleteSavedAtlasData(run);
                        deletedResetCount = resetRequest;
                        run.ClearMigration();
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
                    var migration = run.NeedsLegacyMigration;
                    var batch = this.cache.TakePersistenceBatch(out var isFullSnapshot, forceFullSnapshot: migration);
                    try
                    {
                        if (batch.Count > 0 || migration)
                        {
                            SaveDatabase(run.FilePath, batch);
                        }

                        if (migration && run.RequestedResetCount == resetRequest)
                        {
                            if (run.LegacyFilePath != null)
                            {
                                File.Delete(run.LegacyFilePath);
                            }

                            run.CompleteMigration();
                            run.Log($"Migrated {batch.Count} Atlas map nodes into {run.FilePath}.");
                        }

                        savedRequest = request;
                        if (batch.Count > 0 && !migration)
                        {
                            run.Log($"Saved {batch.Count} changed Atlas map nodes to {run.FilePath}.");
                        }
                    }
                    catch (Exception ex)
                    {
                        this.cache.RequeuePersistenceBatch(batch, isFullSnapshot);
                        run.Log($"Atlas database save failed: {ex.Message}. Will retry.");
                        if (run.IsStopping)
                        {
                            return;
                        }

                        await Task.Delay(RetryDelay, run.Token).ConfigureAwait(false);
                        run.SignalRetry();
                        continue;
                    }
                }

                if (run.RequestedResetCount > deletedResetCount)
                {
                    run.SignalRetry();
                    continue;
                }

                if (run.RequestedSaveCount > savedRequest)
                {
                    run.SignalRetry();
                    continue;
                }

                if (run.IsStopping)
                {
                    return;
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
            private int needsLegacyMigration;

            internal PersistenceRun(string filePath, string? legacyFilePath, Action<string> log)
            {
                this.FilePath = filePath;
                this.LegacyFilePath = legacyFilePath;
                this.LogAction = log;
            }

            internal string FilePath { get; }

            internal string? LegacyFilePath { get; }

            internal Action<string> LogAction { get; }

            internal CancellationToken Token => CancellationToken.None;

            internal Task? Worker { get; set; }

            internal bool IsStopping => Volatile.Read(ref this.stopping) != 0;

            internal bool CanWrite => Volatile.Read(ref this.canWrite) != 0;

            internal bool NeedsLegacyMigration => Volatile.Read(ref this.needsLegacyMigration) != 0;

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

            internal void RequestMigration()
            {
                Volatile.Write(ref this.needsLegacyMigration, 1);
                this.ScheduleSave();
            }

            internal void CompleteMigration() => Volatile.Write(ref this.needsLegacyMigration, 0);

            internal void ClearMigration() => Volatile.Write(ref this.needsLegacyMigration, 0);

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
