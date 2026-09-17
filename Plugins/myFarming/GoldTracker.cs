namespace myFarming
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;
    using System.Text.RegularExpressions;
    using TEHhub;
    using TEHhub.Offsets.Natives;
    using TEHhub.Offsets.Objects.States.InGameState;
    using TEHhub.Offsets.Objects.UiElement;
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
        private readonly Dictionary<int, int> baselineMemorySnap = new();
        private int discoveredOffset = -1;
        private bool isLongType = false;
        private long groundLootedGold = 0;
        private long uiGoldGain = 0;
        private long memoryGoldGain = 0;

        private long lastDiagGround = -1;
        private long lastDiagMemory = -1;
        private long lastDiagUi = -1;
        private long lastDiagPublished = -1;
        private long lastDiagUiCandidateVal = -1;

        public long BaselineGold { get; private set; } = 0;
        public long CurrentGold { get; private set; } = 0;
        public long MapGoldGain { get; private set; } = 0;

        public void StartMap()
        {
            this.knownGoldPiles.Clear();
            this.groundLootedGold = 0;
            this.uiGoldGain = 0;
            this.memoryGoldGain = 0;
            this.MapGoldGain = 0;
            this.discoveredOffset = -1;
            this.lastDiagGround = -1;
            this.lastDiagMemory = -1;
            this.lastDiagUi = -1;
            this.lastDiagPublished = -1;
            this.lastDiagUiCandidateVal = -1;
            this.SnapshotMemoryBaseline();

            this.CurrentGold = this.ReadPlayerGoldFromMemory();
            this.BaselineGold = this.CurrentGold;
        }

        public void ResumeMap(long previousGoldGain)
        {
            this.knownGoldPiles.Clear();
            this.groundLootedGold = previousGoldGain;
            this.uiGoldGain = previousGoldGain;
            this.memoryGoldGain = previousGoldGain;
            this.MapGoldGain = previousGoldGain;
            this.lastDiagGround = -1;
            this.lastDiagMemory = -1;
            this.lastDiagUi = -1;
            this.lastDiagPublished = -1;
            this.lastDiagUiCandidateVal = -1;
            this.SnapshotMemoryBaseline();

            this.CurrentGold = this.ReadPlayerGoldFromMemory();
            this.BaselineGold = this.CurrentGold > previousGoldGain ? this.CurrentGold - previousGoldGain : this.CurrentGold;
        }

        public void Reset()
        {
            this.knownGoldPiles.Clear();
            this.baselineMemorySnap.Clear();
            this.BaselineGold = 0;
            this.CurrentGold = 0;
            this.MapGoldGain = 0;
            this.groundLootedGold = 0;
            this.uiGoldGain = 0;
            this.memoryGoldGain = 0;
            this.discoveredOffset = -1;
            this.lastDiagGround = -1;
            this.lastDiagMemory = -1;
            this.lastDiagUi = -1;
            this.lastDiagPublished = -1;
            this.lastDiagUiCandidateVal = -1;
        }

        public void Update()
        {
            var area = Core.States.InGameStateObject?.CurrentAreaInstance;
            if (area == null) return;

            // 1. Ground Gold Auto-Pickup Tracking
            this.TrackGroundGold(area);

            // 2. Memory Offset Tracking & Auto-Calibration
            this.TrackMemoryGold(area);

            // 3. UI Element Gold Reading (when inventory is available)
            this.TrackUiGold();

            // 4. Aggregate authoritative MapGoldGain
            long bestGain = Math.Max(this.groundLootedGold, Math.Max(this.uiGoldGain, this.memoryGoldGain));
            this.MapGoldGain = Math.Max(this.MapGoldGain, bestGain);

            if (this.groundLootedGold != this.lastDiagGround ||
                this.memoryGoldGain != this.lastDiagMemory ||
                this.uiGoldGain != this.lastDiagUi ||
                this.MapGoldGain != this.lastDiagPublished)
            {
                this.lastDiagGround = this.groundLootedGold;
                this.lastDiagMemory = this.memoryGoldGain;
                this.lastDiagUi = this.uiGoldGain;
                this.lastDiagPublished = this.MapGoldGain;
                PluginLog.Info("myFarming", $"[GoldDiag] source totals changed: ground={this.groundLootedGold}, memory={this.memoryGoldGain}, ui={this.uiGoldGain}, published={this.MapGoldGain}");
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

        private void SnapshotMemoryBaseline()
        {
            this.baselineMemorySnap.Clear();
            var pServer = GetPlayerServerDataAddress();
            if (pServer == IntPtr.Zero) return;

            var reader = Core.Process.Handle;
            if (reader == null) return;

            try
            {
                for (int offset = 0x200; offset <= 0x1500; offset += 4)
                {
                    int val = reader.ReadMemory<int>(pServer + offset);
                    if (val > 0 && val <= 2_000_000_000)
                    {
                        this.baselineMemorySnap[offset] = val;
                    }
                }
            }
            catch
            {
            }
        }

        private void TrackMemoryGold(AreaInstance area)
        {
            var pServer = GetPlayerServerDataAddress();
            if (pServer == IntPtr.Zero) return;

            var reader = Core.Process.Handle;
            if (reader == null) return;

            try
            {
                // If offset is already discovered and valid
                if (this.discoveredOffset >= 0)
                {
                    long currentVal = this.isLongType
                        ? reader.ReadMemory<long>(pServer + this.discoveredOffset)
                        : (long)reader.ReadMemory<int>(pServer + this.discoveredOffset);

                    if (currentVal >= 0 && currentVal <= 2_000_000_000L)
                    {
                        this.CurrentGold = currentVal;
                        if (this.BaselineGold == 0 && currentVal > 0)
                        {
                            this.BaselineGold = currentVal;
                        }

                        if (currentVal >= this.BaselineGold)
                        {
                            this.memoryGoldGain = currentVal - this.BaselineGold;
                        }
                        return;
                    }

                    PluginLog.Info("myFarming", $"[GoldDiag] memory offset invalidated: 0x{this.discoveredOffset:X}, ObservedValue={currentVal}");
                    this.discoveredOffset = -1;
                }

                // Auto-calibrate offset by matching memory delta with ground looted gold gain
                if (this.groundLootedGold > 0 && this.baselineMemorySnap.Count > 0)
                {
                    var candidates = new List<(int Offset, int BaseVal, int CurrentVal, int Delta)>();
                    for (int offset = 0x200; offset <= 0x1500; offset += 4)
                    {
                        if (this.baselineMemorySnap.TryGetValue(offset, out int baseVal))
                        {
                            int currentVal = reader.ReadMemory<int>(pServer + offset);
                            int delta = currentVal - baseVal;
                            if (delta > 0 && Math.Abs(delta - this.groundLootedGold) < 200)
                            {
                                candidates.Add((offset, baseVal, currentVal, delta));
                            }
                        }
                    }

                    if (candidates.Count > 0)
                    {
                        for (int i = 0; i < candidates.Count; i++)
                        {
                            var c = candidates[i];
                            PluginLog.Info("myFarming", $"[GoldDiag] memory offset candidate: 0x{c.Offset:X}, Baseline={c.BaseVal}, Current={c.CurrentVal}, Delta={c.Delta}, TargetGround={this.groundLootedGold}");
                        }

                        var selected = candidates[0];
                        this.discoveredOffset = selected.Offset;
                        this.isLongType = false;
                        this.CurrentGold = selected.CurrentVal;
                        this.BaselineGold = selected.BaseVal;
                        this.memoryGoldGain = selected.Delta;
                        PluginLog.Info("myFarming", $"[GoldDiag] memory offset locked: 0x{selected.Offset:X}, Type=int, Baseline={selected.BaseVal}, Current={selected.CurrentVal}, CandidateCount={candidates.Count}, Ambiguity={(candidates.Count > 1 ? "HIGH" : "None")}");
                        return;
                    }
                }
            }
            catch
            {
            }
        }

        private void TrackUiGold()
        {
            var gameUi = Core.States.InGameStateObject?.GameUi;
            if (gameUi == null || gameUi.RightPanel.Address == IntPtr.Zero) return;

            var rightAddr = gameUi.RightPanel.Address;
            var reader = Core.Process.Handle;
            if (reader == null) return;

            try
            {
                // Scan RightPanel children recursively for Gold counter text
                long parsedGold = ScanUiGoldText(reader, rightAddr, 0, 5, out var matchedRawText, out var matchedElemAddr, out var matchedDepth);
                if (parsedGold > 0)
                {
                    if (parsedGold != this.lastDiagUiCandidateVal)
                    {
                        this.lastDiagUiCandidateVal = parsedGold;
                        PluginLog.Info("myFarming", $"[GoldDiag] UI gold candidate (UNVERIFIED generic numeric): Parsed={parsedGold}, Raw='{matchedRawText}', Depth={matchedDepth}, Element=0x{matchedElemAddr:X}");
                    }

                    this.CurrentGold = parsedGold;
                    if (this.BaselineGold == 0)
                    {
                        this.BaselineGold = parsedGold;
                    }

                    if (parsedGold >= this.BaselineGold)
                    {
                        this.uiGoldGain = parsedGold - this.BaselineGold;
                    }
                }
            }
            catch
            {
            }
        }

        // NOTE: Scans RightPanel children recursively for ANY generic numeric text. This is an UNVERIFIED generic numeric search with NO gold icon or gold label verification.
        private static long ScanUiGoldText(
            TEHhub.Utils.SafeMemoryHandle reader,
            IntPtr elem,
            int depth,
            int maxDepth,
            out string matchedRawText,
            out IntPtr matchedElemAddr,
            out int matchedDepth)
        {
            if (elem == IntPtr.Zero || depth > maxDepth)
            {
                matchedRawText = string.Empty;
                matchedElemAddr = IntPtr.Zero;
                matchedDepth = -1;
                return 0;
            }

            const int UiElementTextOffset = 0x360;
            var ws = reader.ReadMemory<StdWString>(elem + UiElementTextOffset);
            if (ws.Buffer != IntPtr.Zero && ws.Length is > 0 and < 32)
            {
                var text = ws.Buffer != IntPtr.Zero ? reader.ReadUnicodeString(ws.Buffer) : string.Empty;
                if (!string.IsNullOrEmpty(text))
                {
                    var clean = text.Replace(",", "").Replace(" ", "").Replace(".", "").Trim();
                    if (long.TryParse(clean, out var val) && val > 0 && val < 2_000_000_000L)
                    {
                        // Check if parent or nearby sibling has gold icon or gold indicator
                        if (text.Length >= 2 && clean.Length >= 2)
                        {
                            matchedRawText = text;
                            matchedElemAddr = elem;
                            matchedDepth = depth;
                            return val;
                        }
                    }
                }
            }

            var ui = reader.ReadMemory<UiElementBaseOffset>(elem);
            var children = reader.ReadStdVector<IntPtr>(ui.ChildrensPtr);
            if (children != null && children.Length > 0 && children.Length <= 100)
            {
                for (int i = 0; i < children.Length; i++)
                {
                    long res = ScanUiGoldText(reader, children[i], depth + 1, maxDepth, out matchedRawText, out matchedElemAddr, out matchedDepth);
                    if (res > 0) return res;
                }
            }

            matchedRawText = string.Empty;
            matchedElemAddr = IntPtr.Zero;
            matchedDepth = -1;
            return 0;
        }

        private static IntPtr GetPlayerServerDataAddress()
        {
            var area = Core.States.InGameStateObject?.CurrentAreaInstance;
            if (area == null) return IntPtr.Zero;

            var serverData = area.ServerDataObject;
            if (serverData == null || serverData.Address == IntPtr.Zero) return IntPtr.Zero;

            var reader = Core.Process.Handle;
            if (reader == null) return IntPtr.Zero;

            try
            {
                var sData = reader.ReadMemory<ServerDataOffsets>(serverData.Address);
                var playerDataArray = reader.ReadStdVector<IntPtr>(sData.PlayerServerDataPtr);
                if (playerDataArray.Length > 0)
                {
                    return playerDataArray[0];
                }
            }
            catch
            {
            }

            return IntPtr.Zero;
        }

        public long ReadPlayerGoldFromMemory()
        {
            var pServer = GetPlayerServerDataAddress();
            if (pServer == IntPtr.Zero) return 0;

            var reader = Core.Process.Handle;
            if (reader == null) return 0;

            try
            {
                if (this.discoveredOffset >= 0)
                {
                    return this.isLongType
                        ? reader.ReadMemory<long>(pServer + this.discoveredOffset)
                        : (long)reader.ReadMemory<int>(pServer + this.discoveredOffset);
                }
            }
            catch
            {
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
