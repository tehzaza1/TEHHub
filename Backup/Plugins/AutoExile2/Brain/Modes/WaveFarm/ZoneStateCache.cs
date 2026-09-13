// <copyright file="ZoneStateCache.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Modes.WaveFarm
{
    using System;
    using System.Collections.Generic;
    using AutoExile2.Systems;

    /// <summary>
    /// Caches per-zone exploration, threat map, and wave tick state across sub-zone transitions.
    /// Ported directly from AutoExile 1 ZoneStateCache.
    /// </summary>
    internal class ZoneStateCache
    {
        private const int MaxEntries = 3;

        private readonly Dictionary<string, ZoneSnapshot> cache = new();
        private readonly LinkedList<string> accessOrder = new();

        public void Save(string hash, ExplorationMap exploration, ThreatMap threatMap, WaveTick wave)
        {
            if (string.IsNullOrEmpty(hash))
            {
                return;
            }

            var snapshot = new ZoneSnapshot
            {
                Hash = hash,
                SavedAt = DateTime.Now,
                Exploration = exploration.CreateSnapshot(),
                ThreatMap = threatMap.CreateSnapshot(),
                LootAttempts = wave.LootMetrics.PickupAttempts,
                LootSuccesses = wave.LootMetrics.PickupSuccesses,
                LootFailures = wave.LootMetrics.PickupsFailed,
            };

            this.cache[hash] = snapshot;
            this.TouchAccessOrder(hash);
            this.Evict();
        }

        public bool TryRestore(string hash, ExplorationMap exploration, ThreatMap threatMap, WaveTick wave)
        {
            if (string.IsNullOrEmpty(hash) || !this.cache.TryGetValue(hash, out var snapshot))
            {
                return false;
            }

            if (snapshot.Exploration != null)
            {
                exploration.RestoreSnapshot(snapshot.Exploration);
            }

            threatMap.RestoreSnapshot(snapshot.ThreatMap);

            wave.LootMetrics.RestoreCounters(
                snapshot.LootAttempts,
                snapshot.LootSuccesses,
                snapshot.LootFailures);

            this.TouchAccessOrder(hash);
            return true;
        }

        public bool Has(string hash) => !string.IsNullOrEmpty(hash) && this.cache.ContainsKey(hash);

        public void Clear()
        {
            this.cache.Clear();
            this.accessOrder.Clear();
        }

        private void TouchAccessOrder(string hash)
        {
            this.accessOrder.Remove(hash);
            this.accessOrder.AddFirst(hash);
        }

        private void Evict()
        {
            while (this.cache.Count > MaxEntries)
            {
                var oldest = this.accessOrder.Last!.Value;
                this.accessOrder.RemoveLast();
                this.cache.Remove(oldest);
            }
        }
    }

    internal class ZoneSnapshot
    {
        public string Hash = string.Empty;
        public DateTime SavedAt;
        public ExplorationSnapshot Exploration = null!;
        public ThreatMapSnapshot ThreatMap = null!;
        public int LootAttempts;
        public int LootSuccesses;
        public int LootFailures;
    }
}
