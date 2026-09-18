namespace myFarming
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;
    using TEHhub;
    using TEHhub.Plugin;
    using TEHhub.RemoteObjects.Components;
    using TEHhub.RemoteObjects.States.InGameStateObjects;

    public sealed class GoldTracker
    {
        private readonly struct GroundGoldPile
        {
            public readonly uint Id;
            public readonly Vector2 GridPos;
            public readonly long Amount;
            public readonly bool AmountWasExact;
            public readonly string Path;

            public GroundGoldPile(uint id, Vector2 gridPos, long amount, bool amountWasExact, string path)
            {
                this.Id = id;
                this.GridPos = gridPos;
                this.Amount = amount;
                this.AmountWasExact = amountWasExact;
                this.Path = path;
            }
        }

        private readonly Dictionary<uint, GroundGoldPile> knownGoldPiles = new();
        private long groundLootedGold = 0;
        private long memoryGoldGain = 0;

        private long lastDiagGround = -1;
        private long lastDiagMemory = -1;
        private long lastDiagPublished = -1;

        public long BaselineGold { get; private set; } = 0;
        public long CurrentGold { get; private set; } = 0;
        public long MapGoldGain { get; private set; } = 0;

        public void StartMap()
        {
            this.knownGoldPiles.Clear();
            this.groundLootedGold = 0;
            this.memoryGoldGain = 0;
            this.MapGoldGain = 0;
            this.lastDiagGround = -1;
            this.lastDiagMemory = -1;
            this.lastDiagPublished = -1;

            if (this.TryReadPlayerGold(out var gold))
            {
                this.CurrentGold = gold;
                this.BaselineGold = gold;
            }
        }

        public void ResumeMap(long previousGoldGain)
        {
            this.knownGoldPiles.Clear();
            this.groundLootedGold = previousGoldGain;
            this.memoryGoldGain = previousGoldGain;
            this.MapGoldGain = previousGoldGain;
            this.lastDiagGround = -1;
            this.lastDiagMemory = -1;
            this.lastDiagPublished = -1;

            if (this.TryReadPlayerGold(out var gold))
            {
                this.CurrentGold = gold;
                this.BaselineGold = this.CurrentGold > previousGoldGain ? this.CurrentGold - previousGoldGain : this.CurrentGold;
            }
        }

        public void Reset()
        {
            this.knownGoldPiles.Clear();
            this.BaselineGold = 0;
            this.CurrentGold = 0;
            this.MapGoldGain = 0;
            this.groundLootedGold = 0;
            this.memoryGoldGain = 0;
            this.lastDiagGround = -1;
            this.lastDiagMemory = -1;
            this.lastDiagPublished = -1;
        }

        public void Update()
        {
            var area = Core.States.InGameStateObject?.CurrentAreaInstance;
            if (area == null) return;

            // 1. Ground Gold Auto-Pickup Tracking
            this.TrackGroundGold(area);

            // 2. Native SDK Gold Reading & Gain Tracking
            this.TrackNativeGold(area);

            // 3. Aggregate authoritative MapGoldGain
            long bestGain = Math.Max(this.groundLootedGold, this.memoryGoldGain);
            this.MapGoldGain = Math.Max(this.MapGoldGain, bestGain);

            if (this.groundLootedGold != this.lastDiagGround ||
                this.memoryGoldGain != this.lastDiagMemory ||
                this.MapGoldGain != this.lastDiagPublished)
            {
                this.lastDiagGround = this.groundLootedGold;
                this.lastDiagMemory = this.memoryGoldGain;
                this.lastDiagPublished = this.MapGoldGain;
                PluginLog.Info("myFarming", $"[GoldDiag] source totals changed: ground={this.groundLootedGold}, nativeMemory={this.memoryGoldGain}, published={this.MapGoldGain}, currentGold={this.CurrentGold}");
            }
        }

        private void TrackGroundGold(AreaInstance area)
        {
            var awake = area.AwakeEntities;
            if (awake == null) return;

            var player = area.Player;
            Vector2 playerGrid = Vector2.Zero;
            if (player != null && player.TryGetComponent<Render>(out var pRender) && pRender != null)
            {
                playerGrid = new Vector2(pRender.GridPosition.X, pRender.GridPosition.Y);
            }

            var currentSeenIds = new HashSet<uint>();

            foreach (var entity in awake.Values)
            {
                if (entity == null || !entity.IsValid || string.IsNullOrEmpty(entity.Path)) continue;

                if (IsGoldEntity(entity.Path))
                {
                    currentSeenIds.Add(entity.Id);
                    if (!this.knownGoldPiles.ContainsKey(entity.Id))
                    {
                        long amount = ReadGoldAmountFromEntity(entity, out bool wasExact);
                        Vector2 gridPos = playerGrid;
                        if (entity.TryGetComponent<Render>(out var r) && r != null)
                        {
                            gridPos = new Vector2(r.GridPosition.X, r.GridPosition.Y);
                        }

                        this.knownGoldPiles[entity.Id] = new GroundGoldPile(entity.Id, gridPos, amount, wasExact, entity.Path);
                        PluginLog.Info("myFarming", $"[GoldDiag] ground pile discovered: Id={entity.Id}, Path={entity.Path}, Amount={amount}, WasExact={wasExact}, Pos=({gridPos.X:F1}, {gridPos.Y:F1})");
                    }
                }
            }

            // Check disappearing gold entities (picked up by player)
            if (this.knownGoldPiles.Count > 0)
            {
                var removedIds = new List<uint>();
                foreach (var kvp in this.knownGoldPiles)
                {
                    if (!currentSeenIds.Contains(kvp.Key))
                    {
                        removedIds.Add(kvp.Key);
                        var pile = kvp.Value;

                        // Verify distance to player at pickup time (auto-pickup reach is ~40-60 grid units)
                        float dist = playerGrid != Vector2.Zero ? Vector2.Distance(playerGrid, pile.GridPos) : 0f;
                        bool accepted = dist <= 75f;
                        if (accepted)
                        {
                            this.groundLootedGold += pile.Amount;
                        }

                        PluginLog.Info("myFarming", $"[GoldDiag] ground pile disappeared: Id={pile.Id}, Dist={dist:F1}, Accepted={accepted}, Amount={pile.Amount} (Exact={pile.AmountWasExact}), groundLootedGold={this.groundLootedGold}");
                    }
                }

                foreach (var id in removedIds)
                {
                    this.knownGoldPiles.Remove(id);
                }
            }
        }

        private static bool IsGoldEntity(string path)
        {
            return path.Contains("Gold", StringComparison.OrdinalIgnoreCase) ||
                   path.Contains("CoinPile", StringComparison.OrdinalIgnoreCase) ||
                   path.Contains("CurrencyCoin", StringComparison.OrdinalIgnoreCase);
        }

        private static long ReadGoldAmountFromEntity(Entity entity, out bool wasExact)
        {
            if (entity.TryGetComponent<WorldItem>(out var wi) && wi != null && wi.Item != null)
            {
                if (wi.Item.TryGetComponent<Stack>(out var stack) && stack != null && stack.Count > 0)
                {
                    wasExact = true;
                    return stack.Count;
                }
            }

            wasExact = false;
            // Heuristic defaults based on drop tier path
            var p = entity.Path;
            if (p.Contains("Tier3", StringComparison.OrdinalIgnoreCase) || p.Contains("Large", StringComparison.OrdinalIgnoreCase)) return 1000;
            if (p.Contains("Tier2", StringComparison.OrdinalIgnoreCase) || p.Contains("Medium", StringComparison.OrdinalIgnoreCase)) return 350;
            return 120;
        }

        private void TrackNativeGold(AreaInstance area)
        {
            var serverData = area.ServerDataObject;
            if (serverData == null) return;

            if (serverData.TryGetGold(out int nativeGold))
            {
                this.CurrentGold = nativeGold;
                if (this.BaselineGold == 0 && nativeGold > 0)
                {
                    this.BaselineGold = nativeGold;
                }

                if (nativeGold >= this.BaselineGold)
                {
                    this.memoryGoldGain = nativeGold - this.BaselineGold;
                }
            }
        }

        public bool TryReadPlayerGold(out long gold)
        {
            gold = 0;
            var serverData = Core.States.InGameStateObject?.CurrentAreaInstance?.ServerDataObject;
            if (serverData != null && serverData.TryGetGold(out int nativeGold))
            {
                gold = nativeGold;
                return true;
            }

            return false;
        }

        public long ReadPlayerGoldFromMemory()
        {
            if (this.TryReadPlayerGold(out var gold))
            {
                return gold;
            }

            return 0;
        }

        public static string FormatGold(long gold)
        {
            if (gold <= 0) return "0";
            if (gold < 1_000)
            {
                return gold.ToString();
            }
            if (gold < 1_000_000)
            {
                double k = gold / 1000.0;
                return k < 10.0 ? $"{k:0.#}k" : $"{k:0}k";
            }
            double m = gold / 1_000_000.0;
            return $"{m:0.0}m";
        }
    }
}
