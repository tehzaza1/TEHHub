// <copyright file="AtlasObservationCache.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Systems
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Linq;
    using System.Text.RegularExpressions;
    using TEHhub.RemoteObjects.States.InGameStateObjects;

    /// <summary>
    /// Keeps a process-local history of Atlas nodes observed from TEHHub's current Atlas snapshot.
    /// This is observation data only; consumers must resolve a node against the current UI before
    /// taking any action.
    /// </summary>
    internal sealed class AtlasObservationCache
    {
        private const int MaxNodesPerCapture = 256;
        private const int MaxRetainedNodes = 20000;
        private const int MaxPointerHistoryPerNode = 16;
        private static readonly TimeSpan PersistenceCheckpointInterval = TimeSpan.FromHours(1);
        private static readonly Regex PlausibleMapIdPattern = new("^[A-Za-z][A-Za-z0-9_]{0,95}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly object sync = new();
        private readonly Dictionary<AtlasNodeIdentity, MutableAtlasNodeObservation> observations = new();
        private readonly Dictionary<int, MutableAtlasNodeObservation> observationsByPersistentId = new();
        private readonly HashSet<AtlasGridPosition> validGridPositions = new();
        private uint gameProcessId;
        private long revision;
        private int captureCursor;
        private int lastSourceNodeCount = -1;
        private DateTime lastCaptureAtUtc = DateTime.MinValue;
        private long lastAtlasMapsRevision = -1;
        private bool hasCompletedCapturePass;
        private bool hitRetentionLimit;
        private bool persistentIdsReady;
        private int nextPersistentNodeId = 1;
        private readonly HashSet<int> dirtyPersistentNodeIds = new();
        private bool fullPersistenceSnapshotRequested;
        private bool persistenceSignalPending;
        private DateTime lastPersistenceSaveScheduledAtUtc = DateTime.MinValue;

        /// <summary>
        /// Gets the number of currently retained Atlas observations without copying the graph.
        /// </summary>
        public int ObservedNodeCount
        {
            get
            {
                lock (this.sync)
                {
                    return this.observations.Count;
                }
            }
        }

        /// <summary>
        /// Captures one bounded slice of the live Atlas snapshot. Call this from AutoExile2's
        /// render callback, after TEHHub's per-frame update has refreshed <see cref="ImportantUiElements.AtlasMaps"/>.
        /// </summary>
        /// <param name="processId">The currently attached game process id.</param>
        /// <param name="gameUi">The live game UI object, or <see langword="null"/> when unavailable.</param>
        /// <param name="observedAtUtc">The UTC time assigned to this observation slice.</param>
        /// <returns><see langword="true"/> when a slice was captured; otherwise <see langword="false"/>.</returns>
        public bool CaptureSlice(uint processId, ImportantUiElements? gameUi, DateTime observedAtUtc)
        {
            lock (this.sync)
            {
                if (processId == 0)
                {
                    if (this.gameProcessId != 0)
                    {
                        this.ResetGameProcessLocked();
                    }

                    return false;
                }

                if (this.gameProcessId != processId)
                {
                    this.ResetGameProcessLocked();
                    this.gameProcessId = processId;
                }

                if (gameUi == null)
                {
                    this.ClearCurrentProcessObservationMarkersLocked();
                    return false;
                }

                var atlasMaps = gameUi.AtlasMaps;
                // In controller mode Core can establish Atlas visibility from the raw panel flags
                // even when the materialized Atlas UiElement's parent visibility chain is stale.
                // Core clears AtlasMaps whenever its own visibility check says the panel is closed.
                if (!gameUi.Atlas.IsVisible && atlasMaps.Count == 0)
                {
                    this.ClearCurrentProcessObservationMarkersLocked();
                    this.captureCursor = 0;
                    this.lastSourceNodeCount = -1;
                    this.lastCaptureAtUtc = DateTime.MinValue;
                    this.lastAtlasMapsRevision = -1;
                    this.validGridPositions.Clear();
                    this.hasCompletedCapturePass = false;
                    return false;
                }

                var sourceNodeCount = atlasMaps.Count;
                if (sourceNodeCount != this.lastSourceNodeCount)
                {
                    // A changed source size starts a new bounded pass. Historical nodes remain
                    // available, but consumers can tell that a full pass for this size is pending.
                    this.captureCursor = 0;
                    this.hasCompletedCapturePass = false;
                    this.lastSourceNodeCount = sourceNodeCount;
                }

                // Core refreshes AtlasMaps topology less often than the render loop. Rebuild this
                // lookup only when that snapshot changes so 60 Hz capture stays proportional to
                // the bounded slice rather than rescanning every map node every frame.
                if (this.lastAtlasMapsRevision != gameUi.AtlasMapsRevision)
                {
                    this.validGridPositions.Clear();
                    foreach (var candidate in atlasMaps)
                    {
                        if (candidate != null && IsPlausibleMapId(candidate.MapId))
                        {
                            var position = new AtlasGridPosition(candidate.GridPosition.X, candidate.GridPosition.Y);
                            if (IsValidGridPosition(position))
                            {
                                this.validGridPositions.Add(position);
                            }
                        }
                    }

                    this.lastAtlasMapsRevision = gameUi.AtlasMapsRevision;
                }

                this.lastCaptureAtUtc = observedAtUtc;
                if (sourceNodeCount == 0)
                {
                    this.hasCompletedCapturePass = true;
                    this.revision++;
                    return true;
                }

                if (this.captureCursor >= sourceNodeCount)
                {
                    this.captureCursor = 0;
                }

                var end = Math.Min(sourceNodeCount, this.captureCursor + MaxNodesPerCapture);
                for (var index = this.captureCursor; index < end; index++)
                {
                    var node = atlasMaps[index];
                    if (node == null ||
                        !IsPlausibleMapId(node.MapId) ||
                        !IsValidGridPosition(new AtlasGridPosition(node.GridPosition.X, node.GridPosition.Y)))
                    {
                        continue;
                    }

                    var identity = new AtlasNodeIdentity(node.GridPosition.X, node.GridPosition.Y, node.MapId ?? string.Empty);
                    if (!this.observations.TryGetValue(identity, out var observation))
                    {
                        if (this.observations.Count >= MaxRetainedNodes)
                        {
                            this.hitRetentionLimit = true;
                            continue;
                        }

                        observation = new MutableAtlasNodeObservation(identity, observedAtUtc);
                        if (this.persistentIdsReady)
                        {
                            observation.AssignPersistentNodeId(this.AllocatePersistentNodeIdLocked());
                            this.observationsByPersistentId.Add(observation.PersistentNodeId, observation);
                        }

                        this.observations.Add(identity, observation);
                        if (observation.PersistentNodeId > 0)
                        {
                            this.dirtyPersistentNodeIds.Add(observation.PersistentNodeId);
                        }
                    }

                    if (observation.Update(node, observedAtUtc, MaxPointerHistoryPerNode, validGridPositions) &&
                        observation.PersistentNodeId > 0)
                    {
                        this.dirtyPersistentNodeIds.Add(observation.PersistentNodeId);
                    }
                }

                this.captureCursor = end >= sourceNodeCount ? 0 : end;
                if (this.captureCursor == 0)
                {
                    this.hasCompletedCapturePass = true;
                }

                this.revision++;
                return true;
            }
        }

        /// <summary>
        /// Returns a detached, read-only snapshot of the observations collected so far.
        /// </summary>
        public AtlasObservationSnapshot GetSnapshot()
        {
            lock (this.sync)
            {
                var nodes = this.observations.Values
                    .Select(observation => observation.ToSnapshot())
                    .OrderBy(observation => observation.Identity.GridX)
                    .ThenBy(observation => observation.Identity.GridY)
                    .ThenBy(observation => observation.Identity.MapId, StringComparer.Ordinal)
                    .ToList();

                return new AtlasObservationSnapshot(
                    this.gameProcessId,
                    this.revision,
                    this.lastCaptureAtUtc,
                    this.lastSourceNodeCount,
                    this.captureCursor,
                    this.hasCompletedCapturePass,
                    this.hitRetentionLimit,
                    nodes);
            }
        }

        /// <summary>
        /// Resolves a cached identity against the current Core Atlas snapshot. The returned node's
        /// state is live for this snapshot; callers must still recheck it and its current UI
        /// visibility/rectangle immediately before any input.
        /// </summary>
        public bool TryResolveCurrentAtlasNode(
            ImportantUiElements? gameUi,
            AtlasNodeIdentity identity,
            out AtlasMapNode node)
        {
            node = null!;
            if (gameUi == null)
            {
                return false;
            }

            var atlasMaps = gameUi.AtlasMaps;
            if (!gameUi.Atlas.IsVisible && atlasMaps.Count == 0)
            {
                return false;
            }

            AtlasMapNode? match = null;
            foreach (var candidate in atlasMaps)
            {
                if (candidate.GridPosition.X != identity.GridX ||
                    candidate.GridPosition.Y != identity.GridY ||
                    !string.Equals(candidate.MapId, identity.MapId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (match != null)
                {
                    return false;
                }

                match = candidate;
            }

            if (match == null)
            {
                return false;
            }

            node = match;
            return true;
        }

        /// <summary>
        /// Merges persisted observations into the in-memory map without replacing newer live data.
        /// </summary>
        public void MergePersistedNodes(IReadOnlyList<AtlasNodeObservation> persistedNodes)
        {
            lock (this.sync)
            {
                foreach (var persisted in persistedNodes)
                {
                    if (this.observations.TryGetValue(persisted.Identity, out var current))
                    {
                        var liveObservationIsNewer = current.LastSeenUtc > persisted.LastSeenUtc;
                        current.MergePersisted(persisted);
                        if (current.PersistentNodeId > 0)
                        {
                            this.observationsByPersistentId[current.PersistentNodeId] = current;
                        }

                        if (liveObservationIsNewer && current.PersistentNodeId > 0)
                        {
                            this.dirtyPersistentNodeIds.Add(current.PersistentNodeId);
                        }
                        continue;
                    }

                    if (this.observations.Count >= MaxRetainedNodes)
                    {
                        this.hitRetentionLimit = true;
                        break;
                    }

                    var restored = new MutableAtlasNodeObservation(persisted);
                    this.observations.Add(persisted.Identity, restored);
                    if (restored.PersistentNodeId > 0)
                    {
                        this.observationsByPersistentId.Add(restored.PersistentNodeId, restored);
                    }
                }

                this.revision++;
            }
        }

        /// <summary>
        /// Pauses assignment of new durable IDs while the startup file is merged, preventing a
        /// live capture from claiming an ID that already belongs to an unread persisted node.
        /// </summary>
        public void BeginPersistenceLoad()
        {
            lock (this.sync)
            {
                this.persistentIdsReady = false;
            }
        }

        /// <summary>
        /// Marks the initial durable-map merge complete and assigns file-local IDs to observations
        /// captured while the background loader was still reading the map.
        /// </summary>
        public void CompletePersistenceLoad()
        {
            lock (this.sync)
            {
                this.nextPersistentNodeId = Math.Max(
                    1,
                    this.observations.Values
                        .Select(observation => observation.PersistentNodeId)
                        .DefaultIfEmpty(0)
                        .Max() + 1);

                foreach (var observation in this.observations.Values
                    .Where(observation => observation.PersistentNodeId == 0)
                    .Where(observation => IsPlausibleMapId(observation.Identity.MapId) &&
                                          IsValidGridPosition(new AtlasGridPosition(observation.Identity.GridX, observation.Identity.GridY)))
                    .OrderBy(observation => observation.Identity.GridX)
                    .ThenBy(observation => observation.Identity.GridY)
                    .ThenBy(observation => observation.Identity.MapId, StringComparer.Ordinal))
                {
                    observation.AssignPersistentNodeId(this.AllocatePersistentNodeIdLocked());
                    this.observationsByPersistentId.Add(observation.PersistentNodeId, observation);
                    this.dirtyPersistentNodeIds.Add(observation.PersistentNodeId);
                }

                this.persistentIdsReady = true;
                if (this.lastPersistenceSaveScheduledAtUtc == DateTime.MinValue)
                {
                    this.lastPersistenceSaveScheduledAtUtc = DateTime.UtcNow;
                }

                this.revision++;
            }
        }

        /// <summary>
        /// Consumes a pending durable change or requests a low-frequency checkpoint for the
        /// last-seen timestamps. Called only after a live Atlas slice was captured.
        /// </summary>
        public bool TryConsumePersistenceChangeOrCheckpoint(DateTime nowUtc)
        {
            lock (this.sync)
            {
                var hasPersistentNodes = this.persistentIdsReady && this.observations.Count > 0;
                var checkpointDue = hasPersistentNodes &&
                    (this.lastPersistenceSaveScheduledAtUtc == DateTime.MinValue ||
                     nowUtc - this.lastPersistenceSaveScheduledAtUtc >= PersistenceCheckpointInterval);
                var hasChanges = this.dirtyPersistentNodeIds.Count > 0 || this.fullPersistenceSnapshotRequested;
                if ((!hasChanges && !checkpointDue) || (this.persistenceSignalPending && !checkpointDue))
                {
                    return false;
                }

                this.fullPersistenceSnapshotRequested |= checkpointDue;
                this.persistenceSignalPending = true;
                this.lastPersistenceSaveScheduledAtUtc = nowUtc;
                return true;
            }
        }

        /// <summary>
        /// Takes a detached batch of durable node changes. Periodic checkpoints return the full
        /// graph; ordinary updates return only nodes whose durable fields changed.
        /// </summary>
        public IReadOnlyList<AtlasNodeObservation> TakePersistenceBatch(out bool isFullSnapshot, bool forceFullSnapshot = false)
        {
            lock (this.sync)
            {
                isFullSnapshot = forceFullSnapshot || this.fullPersistenceSnapshotRequested;
                this.persistenceSignalPending = false;
                List<AtlasNodeObservation> nodes;
                if (isFullSnapshot)
                {
                    nodes = this.observations.Values
                        .Where(observation => observation.PersistentNodeId > 0)
                        .Select(observation => observation.ToSnapshot())
                        .ToList();
                    this.fullPersistenceSnapshotRequested = false;
                    this.dirtyPersistentNodeIds.Clear();
                }
                else
                {
                    nodes = new List<AtlasNodeObservation>(this.dirtyPersistentNodeIds.Count);
                    foreach (var persistentNodeId in this.dirtyPersistentNodeIds.OrderBy(id => id))
                    {
                        if (this.observationsByPersistentId.TryGetValue(persistentNodeId, out var observation))
                        {
                            nodes.Add(observation.ToSnapshot());
                        }
                    }

                    this.dirtyPersistentNodeIds.Clear();
                }

                return nodes;
            }
        }

        /// <summary>
        /// Requeues a failed persistence batch without losing changes captured while disk IO ran.
        /// </summary>
        public void RequeuePersistenceBatch(IReadOnlyList<AtlasNodeObservation> nodes, bool isFullSnapshot)
        {
            lock (this.sync)
            {
                if (isFullSnapshot)
                {
                    this.fullPersistenceSnapshotRequested = true;
                }
                else
                {
                    foreach (var node in nodes)
                    {
                        if (node.PersistentNodeId > 0)
                        {
                            this.dirtyPersistentNodeIds.Add(node.PersistentNodeId);
                        }
                    }
                }

                this.persistenceSignalPending = true;
            }
        }

        /// <summary>
        /// Clears process-specific sampling and UI pointer diagnostics while retaining the
        /// process-independent observed map for merging with persisted Atlas knowledge.
        /// </summary>
        public void ResetGameProcess()
        {
            lock (this.sync)
            {
                this.ResetGameProcessLocked();
            }
        }

        /// <summary>
        /// Clears the observed graph while keeping the current process and persistence-load state
        /// so subsequent live Atlas captures can immediately rebuild it.
        /// </summary>
        public void ClearObservedGraph()
        {
            lock (this.sync)
            {
                this.observations.Clear();
                this.observationsByPersistentId.Clear();
                this.captureCursor = 0;
                this.lastSourceNodeCount = -1;
                this.lastCaptureAtUtc = DateTime.MinValue;
                this.lastAtlasMapsRevision = -1;
                this.validGridPositions.Clear();
                this.hasCompletedCapturePass = false;
                this.hitRetentionLimit = false;
                this.nextPersistentNodeId = 1;
                this.dirtyPersistentNodeIds.Clear();
                this.fullPersistenceSnapshotRequested = false;
                this.persistenceSignalPending = false;
                this.lastPersistenceSaveScheduledAtUtc = DateTime.MinValue;
                this.revision++;
            }
        }

        internal static bool IsPlausibleMapId(string? mapId) =>
            !string.IsNullOrWhiteSpace(mapId) && PlausibleMapIdPattern.IsMatch(mapId);

        internal static bool IsValidGridPosition(AtlasGridPosition position) =>
            Math.Abs((long)position.X) <= 100000 && Math.Abs((long)position.Y) <= 100000;

        /// <summary>
        /// Clears observations at a plugin or game-process lifecycle boundary.
        /// </summary>
        public void Reset()
        {
            lock (this.sync)
            {
                this.ResetLocked();
            }
        }

        private void ResetLocked()
        {
            this.observations.Clear();
            this.observationsByPersistentId.Clear();
            this.ResetGameProcessLocked();
            this.hitRetentionLimit = false;
            this.persistentIdsReady = false;
            this.nextPersistentNodeId = 1;
            this.dirtyPersistentNodeIds.Clear();
            this.fullPersistenceSnapshotRequested = false;
            this.persistenceSignalPending = false;
            this.lastPersistenceSaveScheduledAtUtc = DateTime.MinValue;
            this.revision++;
        }

        private int AllocatePersistentNodeIdLocked()
        {
            if (this.nextPersistentNodeId <= 0 || this.nextPersistentNodeId == int.MaxValue)
            {
                throw new InvalidOperationException("Atlas persistent node ID space is exhausted.");
            }

            return this.nextPersistentNodeId++;
        }

        private void ResetGameProcessLocked()
        {
            foreach (var observation in this.observations.Values)
            {
                observation.ClearPointerHistory();
                observation.MarkNotObservedInCurrentProcess();
            }

            this.gameProcessId = 0;
            this.captureCursor = 0;
            this.lastSourceNodeCount = -1;
            this.lastCaptureAtUtc = DateTime.MinValue;
            this.lastAtlasMapsRevision = -1;
            this.validGridPositions.Clear();
            this.hasCompletedCapturePass = false;
            this.revision++;
        }

        private void ClearCurrentProcessObservationMarkersLocked()
        {
            foreach (var observation in this.observations.Values)
            {
                observation.MarkNotObservedInCurrentProcess();
            }
        }
    }

    /// <summary>
    /// Stable cache identity for a map node within the observed Atlas.
    /// </summary>
    internal readonly record struct AtlasNodeIdentity(int GridX, int GridY, string MapId);

    /// <summary>
    /// Detached Atlas cache metadata and node observations.
    /// </summary>
    internal sealed class AtlasObservationSnapshot
    {
        internal AtlasObservationSnapshot(
            uint gameProcessId,
            long revision,
            DateTime lastCaptureAtUtc,
            int sourceNodeCount,
            int nextCaptureIndex,
            bool hasCompletedCapturePass,
            bool hitRetentionLimit,
            IReadOnlyList<AtlasNodeObservation> nodes)
        {
            this.GameProcessId = gameProcessId;
            this.Revision = revision;
            this.LastCaptureAtUtc = lastCaptureAtUtc;
            this.SourceNodeCount = sourceNodeCount;
            this.NextCaptureIndex = nextCaptureIndex;
            this.HasCompletedCapturePass = hasCompletedCapturePass;
            this.HitRetentionLimit = hitRetentionLimit;
            this.Nodes = new ReadOnlyCollection<AtlasNodeObservation>(nodes.ToList());
        }

        internal uint GameProcessId { get; }

        internal long Revision { get; }

        internal DateTime LastCaptureAtUtc { get; }

        internal int SourceNodeCount { get; }

        internal int NextCaptureIndex { get; }

        internal bool HasCompletedCapturePass { get; }

        internal bool HitRetentionLimit { get; }

        internal IReadOnlyList<AtlasNodeObservation> Nodes { get; }
    }

    /// <summary>
    /// Immutable observed Atlas node data. The pointer history is diagnostic metadata only and
    /// must never be used to select or click a node.
    /// </summary>
    internal sealed class AtlasNodeObservation
    {
        internal AtlasNodeObservation(
            AtlasNodeIdentity identity,
            int lastObservedIndex,
            byte biomeId,
            byte rawStatus,
            AtlasMapNodeState state,
            DateTime firstSeenUtc,
            DateTime lastSeenUtc,
            int observationCount,
            IReadOnlyList<AtlasGridPosition> connectedGridPositions,
            IReadOnlyList<uint> contentTokens,
            IReadOnlyList<uint> badgeContentIds,
            IReadOnlyList<string> contentNames,
            IReadOnlyList<AtlasNodePointerObservation> pointerHistory,
            int persistentNodeId = 0,
            bool wasObservedInCurrentProcess = false)
        {
            this.Identity = identity;
            this.PersistentNodeId = persistentNodeId;
            this.WasObservedInCurrentProcess = wasObservedInCurrentProcess;
            this.LastObservedIndex = lastObservedIndex;
            this.BiomeId = biomeId;
            this.RawStatus = rawStatus;
            this.State = state;
            this.FirstSeenUtc = firstSeenUtc;
            this.LastSeenUtc = lastSeenUtc;
            this.ObservationCount = observationCount;
            this.ConnectedGridPositions = new ReadOnlyCollection<AtlasGridPosition>(connectedGridPositions.ToList());
            this.ContentTokens = new ReadOnlyCollection<uint>(contentTokens.ToList());
            this.BadgeContentIds = new ReadOnlyCollection<uint>(badgeContentIds.ToList());
            this.ContentNames = new ReadOnlyCollection<string>(contentNames.ToList());
            this.PointerHistory = new ReadOnlyCollection<AtlasNodePointerObservation>(pointerHistory.ToList());
        }

        internal AtlasNodeIdentity Identity { get; }

        internal int PersistentNodeId { get; }

        /// <summary>
        /// Gets whether a live Atlas capture has observed this node in the current game process.
        /// Persisted status remains a historical hint until it is resolved from the current UI.
        /// </summary>
        internal bool WasObservedInCurrentProcess { get; }

        internal int LastObservedIndex { get; }

        internal byte BiomeId { get; }

        internal byte RawStatus { get; }

        /// <summary>
        /// Gets the last observed state. It may be loaded from the durable map and is only a
        /// planning hint; use <see cref="AtlasObservationCache.TryResolveCurrentAtlasNode"/>
        /// before relying on current accessibility.
        /// </summary>
        internal AtlasMapNodeState State { get; }

        internal DateTime FirstSeenUtc { get; }

        internal DateTime LastSeenUtc { get; }

        internal int ObservationCount { get; }

        internal IReadOnlyList<AtlasGridPosition> ConnectedGridPositions { get; }

        internal IReadOnlyList<uint> ContentTokens { get; }

        internal IReadOnlyList<uint> BadgeContentIds { get; }

        internal IReadOnlyList<string> ContentNames { get; }

        /// <summary>
        /// Gets historical UI addresses for diagnostics only. Resolve a fresh current UI node
        /// before any future interaction; these addresses are not stable click targets.
        /// </summary>
        internal IReadOnlyList<AtlasNodePointerObservation> PointerHistory { get; }
    }

    /// <summary>
    /// Atlas grid position independent of transient UI node pointers and indices.
    /// </summary>
    internal readonly record struct AtlasGridPosition(int X, int Y);

    /// <summary>
    /// One distinct UI address observed for a stable Atlas node identity.
    /// </summary>
    internal sealed class AtlasNodePointerObservation
    {
        internal AtlasNodePointerObservation(IntPtr uiAddress, DateTime firstSeenUtc, DateTime lastSeenUtc, int observationCount)
        {
            this.UiAddress = uiAddress;
            this.FirstSeenUtc = firstSeenUtc;
            this.LastSeenUtc = lastSeenUtc;
            this.ObservationCount = observationCount;
        }

        internal IntPtr UiAddress { get; }

        internal DateTime FirstSeenUtc { get; }

        internal DateTime LastSeenUtc { get; }

        internal int ObservationCount { get; }
    }

    internal sealed class MutableAtlasNodeObservation
    {
        private readonly Dictionary<IntPtr, MutableAtlasNodePointerObservation> pointerHistory = new();
        private readonly List<IntPtr> pointerOrder = new();

        internal MutableAtlasNodeObservation(AtlasNodeIdentity identity, DateTime firstSeenUtc)
        {
            this.Identity = identity;
            this.FirstSeenUtc = firstSeenUtc;
            this.LastSeenUtc = firstSeenUtc;
            this.ConnectedGridPositions = Array.Empty<AtlasGridPosition>();
            this.ContentTokens = Array.Empty<uint>();
            this.BadgeContentIds = Array.Empty<uint>();
            this.ContentNames = Array.Empty<string>();
        }

        internal MutableAtlasNodeObservation(AtlasNodeObservation snapshot)
        {
            this.Identity = snapshot.Identity;
            this.PersistentNodeId = snapshot.PersistentNodeId;
            this.WasObservedInCurrentProcess = snapshot.WasObservedInCurrentProcess;
            this.LastObservedIndex = snapshot.LastObservedIndex;
            this.BiomeId = snapshot.BiomeId;
            this.RawStatus = snapshot.RawStatus;
            this.State = snapshot.State;
            this.FirstSeenUtc = snapshot.FirstSeenUtc;
            this.LastSeenUtc = snapshot.LastSeenUtc;
            this.ObservationCount = snapshot.ObservationCount;
            this.ConnectedGridPositions = snapshot.ConnectedGridPositions.ToArray();
            this.ContentTokens = snapshot.ContentTokens.ToArray();
            this.BadgeContentIds = snapshot.BadgeContentIds.ToArray();
            this.ContentNames = snapshot.ContentNames.ToArray();
        }

        internal AtlasNodeIdentity Identity { get; }

        internal int PersistentNodeId { get; private set; }

        internal bool WasObservedInCurrentProcess { get; private set; }

        internal int LastObservedIndex { get; private set; }

        internal byte BiomeId { get; private set; }

        internal byte RawStatus { get; private set; }

        internal AtlasMapNodeState State { get; private set; }

        internal DateTime FirstSeenUtc { get; private set; }

        internal DateTime LastSeenUtc { get; private set; }

        internal int ObservationCount { get; private set; }

        internal AtlasGridPosition[] ConnectedGridPositions { get; private set; }

        internal uint[] ContentTokens { get; private set; }

        internal uint[] BadgeContentIds { get; private set; }

        internal string[] ContentNames { get; private set; }

        internal void AssignPersistentNodeId(int persistentNodeId)
        {
            if (persistentNodeId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(persistentNodeId));
            }

            if (this.PersistentNodeId != 0 && this.PersistentNodeId != persistentNodeId)
            {
                throw new InvalidOperationException("An Atlas node cannot be assigned a different persistent ID.");
            }

            this.PersistentNodeId = persistentNodeId;
        }

        internal bool Update(
            AtlasMapNode node,
            DateTime observedAtUtc,
            int maxPointerHistory,
            IReadOnlySet<AtlasGridPosition> validGridPositions)
        {
            var hasUsableBiome = node.BiomeId != 0 || this.BiomeId == 0;
            var durableFieldsChanged =
                (hasUsableBiome && this.BiomeId != node.BiomeId) ||
                this.State != node.State;
            this.LastObservedIndex = node.Index;
            if (hasUsableBiome)
            {
                this.BiomeId = node.BiomeId;
            }

            this.RawStatus = node.RawStatus;
            this.State = node.State;
            this.WasObservedInCurrentProcess = true;
            this.LastSeenUtc = observedAtUtc;
            this.ObservationCount++;
            var connections = node.ConnectedGridPositions
                .Select(position => new AtlasGridPosition(position.X, position.Y))
                .Where(position => position != new AtlasGridPosition(node.GridPosition.X, node.GridPosition.Y) &&
                                   AtlasObservationCache.IsValidGridPosition(position) &&
                                   validGridPositions.Contains(position))
                .Distinct()
                .OrderBy(position => position.X)
                .ThenBy(position => position.Y)
                .ToArray();
            if (connections.Length > 0 || this.ConnectedGridPositions.Length == 0)
            {
                if (!this.ConnectedGridPositions.SequenceEqual(connections))
                {
                    this.ConnectedGridPositions = connections;
                    durableFieldsChanged = true;
                }
            }

            if (node.ContentTokens.Count > 0 || this.ContentTokens.Length == 0)
            {
                this.ContentTokens = node.ContentTokens.ToArray();
            }

            if (node.BadgeContentIds.Count > 0 || this.BadgeContentIds.Length == 0)
            {
                var badgeIds = node.BadgeContentIds
                    .Where(id => id != 0 && (id <= 0xFFFFu || (id >> 16) == 2))
                    .Select(id => 0x00020000u | (id & 0xFFFFu))
                    .Distinct()
                    .OrderBy(id => id)
                    .ToArray();
                var currentBadgeIds = this.BadgeContentIds
                    .Where(id => id != 0 && (id <= 0xFFFFu || (id >> 16) == 2))
                    .Select(id => 0x00020000u | (id & 0xFFFFu))
                    .Distinct()
                    .OrderBy(id => id)
                    .ToArray();
                if (!currentBadgeIds.SequenceEqual(badgeIds))
                {
                    this.BadgeContentIds = node.BadgeContentIds.ToArray();
                    durableFieldsChanged = true;
                }
            }

            if (node.ContentNames.Count > 0 || this.ContentNames.Length == 0)
            {
                this.ContentNames = node.ContentNames.ToArray();
            }

            var uiAddress = node.Address;
            if (uiAddress == IntPtr.Zero)
            {
                return durableFieldsChanged;
            }

            if (this.pointerHistory.TryGetValue(uiAddress, out var pointerObservation))
            {
                pointerObservation.Update(observedAtUtc);
                return durableFieldsChanged;
            }

            this.pointerHistory.Add(uiAddress, new MutableAtlasNodePointerObservation(uiAddress, observedAtUtc));
            this.pointerOrder.Add(uiAddress);
            while (this.pointerOrder.Count > maxPointerHistory)
            {
                var oldestAddress = this.pointerOrder
                    .Select(address => this.pointerHistory[address])
                    .OrderBy(pointer => pointer.LastSeenUtc)
                    .First()
                    .UiAddress;
                this.pointerOrder.Remove(oldestAddress);
                this.pointerHistory.Remove(oldestAddress);
            }

            return durableFieldsChanged;
        }

        internal void MergePersisted(AtlasNodeObservation persisted)
        {
            if (persisted.PersistentNodeId > 0)
            {
                this.AssignPersistentNodeId(persisted.PersistentNodeId);
            }

            if (persisted.FirstSeenUtc < this.FirstSeenUtc)
            {
                this.FirstSeenUtc = persisted.FirstSeenUtc;
            }

            var persistedIsNewer = persisted.LastSeenUtc > this.LastSeenUtc;
            if (persistedIsNewer)
            {
                this.LastSeenUtc = persisted.LastSeenUtc;
                this.LastObservedIndex = persisted.LastObservedIndex;
                this.BiomeId = persisted.BiomeId;
                this.RawStatus = persisted.RawStatus;
                this.State = persisted.State;
            }

            if ((persistedIsNewer && persisted.ConnectedGridPositions.Count > 0) ||
                this.ConnectedGridPositions.Length == 0)
            {
                this.ConnectedGridPositions = persisted.ConnectedGridPositions.ToArray();
            }

            if ((persistedIsNewer && persisted.ContentTokens.Count > 0) || this.ContentTokens.Length == 0)
            {
                this.ContentTokens = persisted.ContentTokens.ToArray();
            }

            if ((persistedIsNewer && persisted.BadgeContentIds.Count > 0) || this.BadgeContentIds.Length == 0)
            {
                this.BadgeContentIds = persisted.BadgeContentIds.ToArray();
            }

            if ((persistedIsNewer && persisted.ContentNames.Count > 0) || this.ContentNames.Length == 0)
            {
                this.ContentNames = persisted.ContentNames.ToArray();
            }

            this.ObservationCount = Math.Max(this.ObservationCount, persisted.ObservationCount);
        }

        internal void ClearPointerHistory()
        {
            this.pointerHistory.Clear();
            this.pointerOrder.Clear();
        }

        internal void MarkNotObservedInCurrentProcess() => this.WasObservedInCurrentProcess = false;

        internal AtlasNodeObservation ToSnapshot()
        {
            var pointers = this.pointerHistory.Values
                .OrderBy(pointer => pointer.FirstSeenUtc)
                .Select(pointer => pointer.ToSnapshot())
                .ToList();

            return new AtlasNodeObservation(
                this.Identity,
                this.LastObservedIndex,
                this.BiomeId,
                this.RawStatus,
                this.State,
                this.FirstSeenUtc,
                this.LastSeenUtc,
                this.ObservationCount,
                this.ConnectedGridPositions,
                this.ContentTokens,
                this.BadgeContentIds,
                this.ContentNames,
                pointers,
                this.PersistentNodeId,
                this.WasObservedInCurrentProcess);
        }
    }

    internal sealed class MutableAtlasNodePointerObservation
    {
        internal MutableAtlasNodePointerObservation(IntPtr uiAddress, DateTime firstSeenUtc)
        {
            this.UiAddress = uiAddress;
            this.FirstSeenUtc = firstSeenUtc;
            this.LastSeenUtc = firstSeenUtc;
            this.ObservationCount = 1;
        }

        internal IntPtr UiAddress { get; }

        internal DateTime FirstSeenUtc { get; }

        internal DateTime LastSeenUtc { get; private set; }

        internal int ObservationCount { get; private set; }

        internal void Update(DateTime observedAtUtc)
        {
            this.LastSeenUtc = observedAtUtc;
            this.ObservationCount++;
        }

        internal AtlasNodePointerObservation ToSnapshot() =>
            new(this.UiAddress, this.FirstSeenUtc, this.LastSeenUtc, this.ObservationCount);
    }
}
