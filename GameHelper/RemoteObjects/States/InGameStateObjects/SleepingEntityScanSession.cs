// <copyright file="SleepingEntityScanSession.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace GameHelper.RemoteObjects.States.InGameStateObjects
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using GameHelper.Utils;
    using GameOffsets.Natives;
    using GameOffsets.Objects.States.InGameState;

    /// <summary>
    /// Current state of a bounded sleeping-entity scan.
    /// </summary>
    public enum SleepingEntityScanState
    {
        Running,
        Completed,
        Aborted,
    }

    /// <summary>
    /// A sleeping entity whose metadata path matched the scan filter.
    /// </summary>
    public sealed class SleepingEntityScanMatch
    {
        private readonly IntPtr entityAddress;

        internal SleepingEntityScanMatch(EntityNodeKey key, IntPtr entityAddress, string path)
        {
            this.Key = key;
            this.entityAddress = entityAddress;
            this.Path = path;
        }

        public EntityNodeKey Key { get; }

        public string Path { get; }

        /// <summary>
        /// Materializes the full entity only when the consumer needs components such as Render.
        /// The live map may remove the entity between discovery and this call, so failure is normal.
        /// </summary>
        public bool TryCreateEntity(out Entity? entity)
        {
            var candidate = new Entity(this.entityAddress);
            if (string.Equals(candidate.Path, this.Path, StringComparison.Ordinal))
            {
                entity = candidate;
                return true;
            }

            entity = null;
            return false;
        }
    }

    /// <summary>
    /// Incrementally walks one snapshot of the game's SleepingEntities std::map. Each call to
    /// <see cref="Step"/> is bounded by both a node count and a wall-clock budget. The cheap pass
    /// reads only EntityOffsets, EntityDetails and Path; a full Entity is constructed only after
    /// the path filter accepts it.
    /// </summary>
    public sealed class SleepingEntityScanSession
    {
        private const int AbsoluteNodeLimit = 500000;
        private readonly IntPtr areaAddress;
        private readonly string areaHash;
        private readonly IntPtr mapHead;
        private readonly Func<string, bool> pathFilter;
        private readonly Queue<IntPtr> pending = new();
        private readonly Queue<SleepingEntityScanMatch> matches = new();
        private readonly HashSet<IntPtr> visited = new();
        private long elapsedStopwatchTicks;

        internal SleepingEntityScanSession(
            IntPtr areaAddress,
            string areaHash,
            StdMap sleepingEntities,
            Func<string, bool> pathFilter)
        {
            this.areaAddress = areaAddress;
            this.areaHash = areaHash;
            this.mapHead = sleepingEntities.Head;
            this.pathFilter = pathFilter;
            this.DeclaredNodes = sleepingEntities.Size;
            this.State = SleepingEntityScanState.Running;

            if (!SafeMemoryHandle.IsValidAddress(this.mapHead) ||
                this.DeclaredNodes <= 0 ||
                this.DeclaredNodes > AbsoluteNodeLimit)
            {
                this.Abort("Invalid SleepingEntities map header.");
                return;
            }

            var reader = Core.Process.Handle;
            if (!reader.TryReadMemory<StdMapNode<EntityNodeKey, EntityNodeValue>>(
                    this.mapHead, out var head) ||
                !SafeMemoryHandle.IsValidAddress(head.Parent) ||
                head.Parent == this.mapHead)
            {
                this.Abort("Unable to read the SleepingEntities root node.");
                return;
            }

            this.visited.Add(this.mapHead);
            this.visited.Add(head.Parent);
            this.pending.Enqueue(head.Parent);
        }

        public SleepingEntityScanState State { get; private set; }

        public int DeclaredNodes { get; }

        public int ProcessedNodes { get; private set; }

        public int ValidPaths { get; private set; }

        public int MatchedPaths { get; private set; }

        public int ReadFailures { get; private set; }

        public int RejectedNodes { get; private set; }

        public int PendingNodes => this.pending.Count;

        public double ElapsedMilliseconds =>
            this.elapsedStopwatchTicks * 1000d / Stopwatch.Frequency;

        public string StatusMessage { get; private set; } = string.Empty;

        /// <summary>
        /// Advances the scan without monopolizing the render thread.
        /// </summary>
        public void Step(int maxNodes, double maxMilliseconds)
        {
            if (this.State != SleepingEntityScanState.Running || maxNodes <= 0 || maxMilliseconds <= 0)
            {
                return;
            }

            var currentArea = Core.States.InGameStateObject.CurrentAreaInstance;
            if (currentArea.Address != this.areaAddress ||
                !string.Equals(currentArea.AreaHash, this.areaHash, StringComparison.Ordinal))
            {
                this.Abort("Area changed while the SleepingEntities scan was running.");
                return;
            }

            var reader = Core.Process.Handle;
            if (!reader.TryReadMemory<AreaInstanceOffsets>(this.areaAddress, out var areaData) ||
                areaData.Entities.SleepingEntities.Head != this.mapHead)
            {
                this.Abort("SleepingEntities map identity changed during the scan.");
                return;
            }

            var started = Stopwatch.GetTimestamp();
            var processedThisStep = 0;
            try
            {
                while (processedThisStep < maxNodes && this.pending.TryDequeue(out var nodeAddress))
                {
                    if (Stopwatch.GetElapsedTime(started).TotalMilliseconds >= maxMilliseconds)
                    {
                        this.pending.Enqueue(nodeAddress);
                        break;
                    }

                    processedThisStep++;
                    if (!reader.TryReadMemory<StdMapNode<EntityNodeKey, EntityNodeValue>>(
                            nodeAddress, out var node))
                    {
                        this.ReadFailures++;
                        continue;
                    }

                    if (node.IsNil || node.Color > 1)
                    {
                        this.RejectedNodes++;
                        continue;
                    }

                    this.ProcessedNodes++;
                    this.TryReadCandidate(node.Data.Key, node.Data.Value, reader);
                    this.TryQueueChild(node.Left);
                    this.TryQueueChild(node.Right);

                    // A live tree can rotate while it is being inspected. The declared size plus
                    // a small margin is enough to finish a stable traversal while still stopping
                    // corrupted/cyclic graphs even if their addresses are all different.
                    if (this.ProcessedNodes > Math.Min(AbsoluteNodeLimit, this.DeclaredNodes + 64))
                    {
                        this.Abort("SleepingEntities tree changed too much during traversal.");
                        break;
                    }
                }

                if (this.State == SleepingEntityScanState.Running && this.pending.Count == 0)
                {
                    this.State = SleepingEntityScanState.Completed;
                    this.StatusMessage = "Scan completed.";
                }
            }
            finally
            {
                this.elapsedStopwatchTicks += Stopwatch.GetTimestamp() - started;
            }
        }

        public bool TryDequeueMatch(out SleepingEntityScanMatch? match)
        {
            if (this.matches.TryDequeue(out var value))
            {
                match = value;
                return true;
            }

            match = null;
            return false;
        }

        private void TryReadCandidate(
            EntityNodeKey key,
            EntityNodeValue value,
            SafeMemoryHandle reader)
        {
            if (!SafeMemoryHandle.IsValidAddress(value.EntityPtr) ||
                !reader.TryReadMemory<EntityOffsets>(value.EntityPtr, out var entityData) ||
                !EntityHelper.IsValidEntity(entityData.IsValid) ||
                !reader.TryReadMemory<EntityDetails>(entityData.ItemBase.EntityDetailsPtr, out var details))
            {
                this.ReadFailures++;
                return;
            }

            var path = reader.ReadStdWString(details.name);
            if (string.IsNullOrEmpty(path) ||
                !path.StartsWith("Metadata/", StringComparison.Ordinal))
            {
                return;
            }

            this.ValidPaths++;
            if (!this.pathFilter(path))
            {
                return;
            }

            this.MatchedPaths++;
            this.matches.Enqueue(new SleepingEntityScanMatch(key, value.EntityPtr, path));
        }

        private void TryQueueChild(IntPtr child)
        {
            if (child != this.mapHead &&
                SafeMemoryHandle.IsValidAddress(child) &&
                this.visited.Add(child))
            {
                this.pending.Enqueue(child);
            }
        }

        private void Abort(string reason)
        {
            this.State = SleepingEntityScanState.Aborted;
            this.StatusMessage = reason;
            this.pending.Clear();
        }
    }
}
