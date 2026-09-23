// <copyright file="ThreatMap.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Systems
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;
    using TEHhub.RemoteEnums;
    using TEHhub.RemoteEnums.Entity;
    using TEHhub.RemoteObjects.Components;
    using TEHhub.RemoteObjects.States.InGameStateObjects;
    using TEHhub.Offsets.Objects.States.InGameState;

    /// <summary>
    /// Persistent, map-wide spatial grid tracking every monster observed during a run.
    /// Chunk-based: map divided into 40x40 grid unit cells.
    /// Ported directly from AutoExile 1 ThreatMap architecture, adapted to TEHhub PoE 2.
    /// </summary>
    public class ThreatMap
    {
        public const int ChunkSize = 40;

        private ThreatChunk[]? chunks;
        private int cols;
        private int rows;
        private int originX;
        private int originY;

        private readonly Dictionary<uint, TrackedMonster> tracked = new(512);

        private DateTime lastReconcile = DateTime.MinValue;
        private const double ReconcileIntervalMs = 250;
        private const float ReconcileRadius = 200f;

        public int TotalAlive { get; private set; }
        public int TotalTracked { get; private set; }
        public int TotalDead { get; private set; }
        public int ChunkCount => this.chunks?.Length ?? 0;
        public bool IsInitialized => this.chunks != null;

        /// <summary>
        /// Initialize the chunk grid from walkable data dimensions.
        /// </summary>
        public void Initialize(byte[] walkableData, int bytesPerRow)
        {
            this.Clear();

            if (walkableData == null || bytesPerRow <= 0)
            {
                return;
            }

            int gridRows = walkableData.Length / bytesPerRow;
            int gridCols = bytesPerRow * 2;

            this.originX = 0;
            this.originY = 0;
            this.cols = (gridCols + ChunkSize - 1) / ChunkSize;
            this.rows = (gridRows + ChunkSize - 1) / ChunkSize;

            this.chunks = new ThreatChunk[this.rows * this.cols];
            for (int i = 0; i < this.chunks.Length; i++)
            {
                this.chunks[i] = new ThreatChunk();
            }
        }

        public void Initialize(int gridRows, int gridCols)
        {
            this.Clear();
            if (gridRows <= 0 || gridCols <= 0)
            {
                return;
            }

            this.originX = 0;
            this.originY = 0;
            this.cols = (gridCols + ChunkSize - 1) / ChunkSize;
            this.rows = (gridRows + ChunkSize - 1) / ChunkSize;

            this.chunks = new ThreatChunk[this.rows * this.cols];
            for (int i = 0; i < this.chunks.Length; i++)
            {
                this.chunks[i] = new ThreatChunk();
            }
        }

        public void Clear()
        {
            this.chunks = null;
            this.tracked.Clear();
            this.TotalAlive = 0;
            this.TotalTracked = 0;
            this.TotalDead = 0;
            this.lastReconcile = DateTime.MinValue;
        }

        /// <summary>
        /// Registers or updates a monster entity.
        /// </summary>
        public void OnEntityAdded(Entity entity)
        {
            if (!this.IsInitialized || entity == null || !entity.IsValid)
            {
                return;
            }

            if (!CombatSystem.IsHostileMonster(entity, IntPtr.Zero))
            {
                return;
            }

            if (!entity.TryGetComponent<Render>(out var render))
            {
                return;
            }

            uint id = entity.Id;
            var pos = new Vector2(render.GridPosition.X, render.GridPosition.Y);
            int ci = this.ChunkIndex(pos);
            if (ci < 0 || this.chunks == null)
            {
                return;
            }

            var rarity = Rarity.Normal;
            if (entity.TryGetComponent<ObjectMagicProperties>(out var omp))
            {
                rarity = omp.Rarity;
            }

            float weight = RarityWeight(rarity);

            if (this.tracked.TryGetValue(id, out var existing))
            {
                if (existing.Status == MonsterStatus.Dead)
                {
                    return;
                }

                if (existing.Status == MonsterStatus.LeftRange)
                {
                    existing.Status = MonsterStatus.Alive;
                    existing.GridPos = pos;
                    this.tracked[id] = existing;
                }
                return;
            }

            this.tracked[id] = new TrackedMonster
            {
                EntityId = id,
                GridPos = pos,
                Rarity = rarity,
                Weight = weight,
                ChunkIndex = ci,
                Status = MonsterStatus.Alive,
            };

            var chunk = this.chunks[ci];
            chunk.AliveCount++;
            chunk.AliveWeight += weight;
            chunk.TotalSeen++;
            chunk.TotalWeight += weight;
            chunk.EntityIds.Add(id);

            this.TotalAlive++;
            this.TotalTracked++;
        }

        public void OnEntityRemoved(Entity entity)
        {
            if (entity == null || entity.EntityType != EntityTypes.Monster)
            {
                return;
            }

            if (!this.tracked.TryGetValue(entity.Id, out var item))
            {
                return;
            }

            if (item.Status != MonsterStatus.Alive)
            {
                return;
            }

            bool isDead = false;
            if (entity.TryGetComponent<Life>(out var life) && life.Health.Current <= 0)
            {
                isDead = true;
            }

            if (isDead)
            {
                this.MarkDead(entity.Id, item);
            }
            else
            {
                item.Status = MonsterStatus.LeftRange;
                this.tracked[entity.Id] = item;
            }
        }

        public void Reconcile(Vector2 playerPos, AreaInstance area)
        {
            if (!this.IsInitialized || this.chunks == null || area == null)
            {
                return;
            }

            if ((DateTime.Now - this.lastReconcile).TotalMilliseconds < ReconcileIntervalMs)
            {
                return;
            }

            this.lastReconcile = DateTime.Now;

            int pcx = ((int)playerPos.X - this.originX) / ChunkSize;
            int pcy = ((int)playerPos.Y - this.originY) / ChunkSize;
            int chunkRadius = (int)(ReconcileRadius / ChunkSize) + 1;

            for (int dy = -chunkRadius; dy <= chunkRadius; dy++)
            {
                int cy = pcy + dy;
                if (cy < 0 || cy >= this.rows)
                {
                    continue;
                }

                for (int dx = -chunkRadius; dx <= chunkRadius; dx++)
                {
                    int cx = pcx + dx;
                    if (cx < 0 || cx >= this.cols)
                    {
                        continue;
                    }

                    var chunk = this.chunks[(cy * this.cols) + cx];
                    if (chunk.AliveCount == 0)
                    {
                        continue;
                    }

                    for (int i = chunk.EntityIds.Count - 1; i >= 0; i--)
                    {
                        uint entityId = chunk.EntityIds[i];
                        if (!this.tracked.TryGetValue(entityId, out var item))
                        {
                            continue;
                        }

                        if (item.Status == MonsterStatus.Dead)
                        {
                            continue;
                        }

                        if (area.AwakeEntities.TryGetValue(new EntityNodeKey { id = entityId }, out var e))
                        {
                            if (!e.IsValid ||
                                (e.TryGetComponent<Life>(out var l) && l.Health.Current <= 0) ||
                                (e.TryGetComponent<Targetable>(out var t) && !t.IsTargetable))
                            {
                                this.MarkDead(entityId, item);
                            }
                        }
                    }
                }
            }
        }

        public void ReconcileAll(AreaInstance area)
        {
            if (!this.IsInitialized || this.chunks == null || area == null)
            {
                return;
            }

            foreach (var chunk in this.chunks)
            {
                if (chunk.AliveCount == 0)
                {
                    continue;
                }

                for (int i = chunk.EntityIds.Count - 1; i >= 0; i--)
                {
                    uint entityId = chunk.EntityIds[i];
                    if (!this.tracked.TryGetValue(entityId, out var item))
                    {
                        continue;
                    }

                    if (item.Status == MonsterStatus.Dead)
                    {
                        continue;
                    }

                    if (area.AwakeEntities.TryGetValue(new EntityNodeKey { id = entityId }, out var e))
                    {
                        if (!e.IsValid || (e.TryGetComponent<Life>(out var l) && l.Health.Current <= 0))
                        {
                            this.MarkDead(entityId, item);
                        }
                    }
                }
            }
        }

        public void RebuildFromEntities(IEnumerable<Entity> monsters)
        {
            foreach (var entity in monsters)
            {
                this.OnEntityAdded(entity);
            }
        }

        public Vector2? GetDensestAliveChunk(Vector2 playerPos, float minDistance = 15f)
        {
            if (this.chunks == null)
            {
                return null;
            }

            float bestWeight = 0;
            Vector2? bestPos = null;
            float minDistSq = minDistance * minDistance;

            for (int cy = 0; cy < this.rows; cy++)
            {
                for (int cx = 0; cx < this.cols; cx++)
                {
                    var chunk = this.chunks[(cy * this.cols) + cx];
                    if (chunk.AliveWeight <= 0)
                    {
                        continue;
                    }

                    var center = this.ChunkCenter(cx, cy);
                    if (Vector2.DistanceSquared(playerPos, center) < minDistSq)
                    {
                        continue;
                    }

                    if (chunk.AliveWeight > bestWeight)
                    {
                        bestWeight = chunk.AliveWeight;
                        bestPos = center;
                    }
                }
            }

            return bestPos;
        }

        public Vector2? GetNearestAliveChunk(Vector2 playerPos, float minDistance = 15f)
        {
            if (this.chunks == null)
            {
                return null;
            }

            float bestDistSq = float.MaxValue;
            Vector2? bestPos = null;
            float minDistSq = minDistance * minDistance;

            for (int cy = 0; cy < this.rows; cy++)
            {
                for (int cx = 0; cx < this.cols; cx++)
                {
                    var chunk = this.chunks[(cy * this.cols) + cx];
                    if (chunk.AliveCount <= 0)
                    {
                        continue;
                    }

                    var center = this.ChunkCenter(cx, cy);
                    var distSq = Vector2.DistanceSquared(playerPos, center);
                    if (distSq < minDistSq)
                    {
                        continue;
                    }

                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        bestPos = center;
                    }
                }
            }

            return bestPos;
        }

        public int GetChunkAliveCount(Vector2 pos)
        {
            int ci = this.ChunkIndex(pos);
            if (ci < 0 || this.chunks == null)
            {
                return 0;
            }

            return this.chunks[ci].AliveCount;
        }

        public float GetChunkAliveWeight(Vector2 pos)
        {
            int ci = this.ChunkIndex(pos);
            if (ci < 0 || this.chunks == null)
            {
                return 0;
            }

            return this.chunks[ci].AliveWeight;
        }

        public float GetThreatInRadius(Vector2 center, float radius)
        {
            if (this.chunks == null)
            {
                return 0;
            }

            float total = 0;
            float radiusSq = radius * radius;

            int minCx = Math.Max(0, ((int)(center.X - radius) - this.originX) / ChunkSize);
            int maxCx = Math.Min(this.cols - 1, ((int)(center.X + radius) - this.originX) / ChunkSize);
            int minCy = Math.Max(0, ((int)(center.Y - radius) - this.originY) / ChunkSize);
            int maxCy = Math.Min(this.rows - 1, ((int)(center.Y + radius) - this.originY) / ChunkSize);

            for (int cy = minCy; cy <= maxCy; cy++)
            {
                for (int cx = minCx; cx <= maxCx; cx++)
                {
                    var chunk = this.chunks[(cy * this.cols) + cx];
                    if (chunk.AliveWeight <= 0)
                    {
                        continue;
                    }

                    var cc = this.ChunkCenter(cx, cy);
                    if (Vector2.DistanceSquared(center, cc) <= radiusSq)
                    {
                        total += chunk.AliveWeight;
                    }
                }
            }

            return total;
        }

        internal ThreatMapSnapshot CreateSnapshot()
        {
            var snap = new ThreatMapSnapshot
            {
                Cols = this.cols,
                Rows = this.rows,
                OriginX = this.originX,
                OriginY = this.originY,
                TotalAlive = this.TotalAlive,
                TotalTracked = this.TotalTracked,
                TotalDead = this.TotalDead,
            };

            if (this.chunks != null)
            {
                snap.Chunks = new ThreatChunkSnapshot[this.chunks.Length];
                for (int i = 0; i < this.chunks.Length; i++)
                {
                    var c = this.chunks[i];
                    snap.Chunks[i] = new ThreatChunkSnapshot
                    {
                        AliveCount = c.AliveCount,
                        DeadCount = c.DeadCount,
                        TotalSeen = c.TotalSeen,
                        AliveWeight = c.AliveWeight,
                        TotalWeight = c.TotalWeight,
                        EntityIds = new List<uint>(c.EntityIds),
                    };
                }
            }

            snap.Tracked = new Dictionary<uint, TrackedMonster>(this.tracked);
            return snap;
        }

        internal void RestoreSnapshot(ThreatMapSnapshot snap)
        {
            this.Clear();

            this.cols = snap.Cols;
            this.rows = snap.Rows;
            this.originX = snap.OriginX;
            this.originY = snap.OriginY;
            this.TotalAlive = snap.TotalAlive;
            this.TotalTracked = snap.TotalTracked;
            this.TotalDead = snap.TotalDead;

            if (snap.Chunks != null)
            {
                this.chunks = new ThreatChunk[snap.Chunks.Length];
                for (int i = 0; i < snap.Chunks.Length; i++)
                {
                    var s = snap.Chunks[i];
                    this.chunks[i] = new ThreatChunk
                    {
                        AliveCount = s.AliveCount,
                        DeadCount = s.DeadCount,
                        TotalSeen = s.TotalSeen,
                        AliveWeight = s.AliveWeight,
                        TotalWeight = s.TotalWeight,
                        EntityIds = new List<uint>(s.EntityIds),
                    };
                }
            }

            foreach (var kvp in snap.Tracked)
            {
                this.tracked[kvp.Key] = kvp.Value;
            }
        }

        private void MarkDead(uint entityId, TrackedMonster item)
        {
            item.Status = MonsterStatus.Dead;
            this.tracked[entityId] = item;

            if (item.ChunkIndex >= 0 && this.chunks != null)
            {
                var chunk = this.chunks[item.ChunkIndex];
                chunk.AliveCount = Math.Max(0, chunk.AliveCount - 1);
                chunk.AliveWeight = MathF.Max(0, chunk.AliveWeight - item.Weight);
                chunk.DeadCount++;
            }

            this.TotalAlive = Math.Max(0, this.TotalAlive - 1);
            this.TotalDead++;
        }

        private int ChunkIndex(Vector2 pos)
        {
            if (this.chunks == null)
            {
                return -1;
            }

            int cx = ((int)pos.X - this.originX) / ChunkSize;
            int cy = ((int)pos.Y - this.originY) / ChunkSize;
            if (cx < 0 || cx >= this.cols || cy < 0 || cy >= this.rows)
            {
                return -1;
            }

            return (cy * this.cols) + cx;
        }

        private Vector2 ChunkCenter(int cx, int cy) =>
            new(this.originX + (cx * ChunkSize) + (ChunkSize / 2f),
                this.originY + (cy * ChunkSize) + (ChunkSize / 2f));

        private static float RarityWeight(Rarity rarity) => rarity switch
        {
            Rarity.Magic => 2f,
            Rarity.Rare => 5f,
            Rarity.Unique => 8f,
            _ => 1f,
        };
    }

    public struct TrackedMonster
    {
        public uint EntityId;
        public Vector2 GridPos;
        public Rarity Rarity;
        public float Weight;
        public int ChunkIndex;
        public MonsterStatus Status;
    }

    public enum MonsterStatus : byte
    {
        Alive,
        Dead,
        LeftRange,
    }

    public class ThreatChunk
    {
        public int AliveCount;
        public int DeadCount;
        public int TotalSeen;
        public float AliveWeight;
        public float TotalWeight;
        public List<uint> EntityIds = new(8);
    }

    public class ThreatMapSnapshot
    {
        public int Cols, Rows, OriginX, OriginY;
        public int TotalAlive, TotalTracked, TotalDead;
        public ThreatChunkSnapshot[]? Chunks;
        public Dictionary<uint, TrackedMonster> Tracked = new();
    }

    public class ThreatChunkSnapshot
    {
        public int AliveCount, DeadCount, TotalSeen;
        public float AliveWeight, TotalWeight;
        public List<uint> EntityIds = new();
    }
}
