namespace ExpeditionPathOptimizer
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Numerics;
    using System.Text.Json;
    using Coroutine;
    using ExpeditionPathOptimizer.PathPlannerData;
    using ImGuiNET;
    using TEHhub;
    using TEHhub.CoroutineEvents;
    using TEHhub.Offsets.Natives;
    using TEHhub.Offsets.Objects.UiElement;
    using TEHhub.Plugin;
    using TEHhub.RemoteObjects.Components;
    using TEHhub.RemoteObjects.States.InGameStateObjects;
    using TEHhub.RemoteObjects.UiElement;
    using TEHhub.Utils;

    public sealed class ExpeditionPathOptimizerCore : PCore<ExpeditionPathOptimizerSettings>
    {
        private const float GridToWorldMultiplier = 250f / 23f; // 10.869565f
        private string SettingPathname => this.PluginConfigPath("settings.json");
        private readonly PathPlannerRunner runner = new();
        private readonly RuneshapeRecipePredictor recipePredictor = new();
        private ExpeditionPriceService? priceService;
        private DateTime lastPriceRefreshUtc = DateTime.MinValue;
        private ActiveCoroutine? onAreaChangeCoroutine;
        private ActiveCoroutine? periodicScanCoroutine;

        private Vector3 detonatorWorldPos = Vector3.Zero;
        private Vector2 detonatorGridPos = Vector2.Zero;
        private readonly Dictionary<IntPtr, ExpeditionRemnant> discoveredRemnants = new();
        private readonly Dictionary<IntPtr, ExpeditionChest> discoveredChests = new();
        private readonly Dictionary<IntPtr, Vector3> placedExplosives = new();
        private ExpeditionRemnant? selectedFinalTarget = null;

        private static readonly HashSet<string> ExpeditionRewardChestIcons =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "RewardChestCurrencyRare",
                "RewardChestCurrency",
                "RewardChestUnique",
                "RewardChestMaps",
                "RewardChestGeneric",
            };

        private string lastAreaHash = string.Empty;
        private bool hasAutoSearchedThisArea = false;

        public static string[] RuneNames => RuneshapeRecipePredictor.RuneNames;

        public override void OnEnable(bool isAutoEnabling)
        {
            this.LoadSettings();
            this.recipePredictor.LoadRecipes(this.DllDirectory);
            this.priceService = new ExpeditionPriceService(this.PluginConfigDirectory);
            this.priceService.TriggerRefresh(this.Settings.League, this.Settings.PriceSource);
            this.lastPriceRefreshUtc = DateTime.UtcNow;

            this.onAreaChangeCoroutine = CoroutineHandler.Start(this.OnAreaChange(), "[ExpeditionPathOptimizer] Area Change");
            this.periodicScanCoroutine = CoroutineHandler.Start(this.PeriodicScan(), "[ExpeditionPathOptimizer] Periodic Scan");
        }

        public override void OnDisable()
        {
            this.onAreaChangeCoroutine?.Cancel();
            this.onAreaChangeCoroutine = null;
            this.periodicScanCoroutine?.Cancel();
            this.periodicScanCoroutine = null;
            this.priceService?.Dispose();
            this.priceService = null;
            this.runner.Clear();
            this.ClearState();
        }

        private void ClearState()
        {
            this.detonatorWorldPos = Vector3.Zero;
            this.detonatorGridPos = Vector2.Zero;
            this.discoveredRemnants.Clear();
            this.discoveredChests.Clear();
            this.placedExplosives.Clear();
            this.selectedFinalTarget = null;
            this.hasAutoSearchedThisArea = false;
        }

        private IEnumerator<Wait> OnAreaChange()
        {
            while (true)
            {
                yield return new Wait(RemoteEvents.AreaChanged);
                this.runner.Clear();
                this.ClearState();
                var area = Core.States.InGameStateObject?.CurrentAreaInstance;
                this.lastAreaHash = area?.AreaHash ?? string.Empty;
            }
        }

        private IEnumerator<Wait> PeriodicScan()
        {
            while (true)
            {
                yield return new Wait(0.5f);
                if (!this.Settings.Enable) continue;

                // Check background price auto-refresh
                if (this.priceService != null && this.Settings.AutoRefreshMinutes > 0)
                {
                    if (DateTime.UtcNow - this.lastPriceRefreshUtc > TimeSpan.FromMinutes(this.Settings.AutoRefreshMinutes))
                    {
                        this.lastPriceRefreshUtc = DateTime.UtcNow;
                        this.priceService.TriggerRefresh(this.Settings.League, this.Settings.PriceSource);
                    }
                }

                var area = Core.States.InGameStateObject?.CurrentAreaInstance;
                if (area == null || area.Address == IntPtr.Zero) continue;

                var areaHash = area.AreaHash ?? string.Empty;
                if (areaHash != this.lastAreaHash)
                {
                    this.lastAreaHash = areaHash;
                    this.runner.Clear();
                    this.ClearState();
                }

                this.ScanEntities(area);

                // Auto-start only ONCE per area when Expedition Detonator and Remnants are detected
                if (!this.hasAutoSearchedThisArea &&
                    this.Settings.AutoStartOnAreaChange &&
                    !this.runner.IsRunning &&
                    this.runner.CurrentBestPath == null)
                {
                    if (this.detonatorWorldPos != Vector3.Zero && this.discoveredRemnants.Count > 0)
                    {
                        if (this.StartSearch(area))
                        {
                            this.hasAutoSearchedThisArea = true;
                        }
                    }
                }
            }
        }

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(this.SettingPathname))
                {
                    var json = File.ReadAllText(this.SettingPathname);
                    var s = JsonSerializer.Deserialize<ExpeditionPathOptimizerSettings>(json);
                    if (s != null) this.Settings = s;
                }
            }
            catch
            {
                // Fallback to default
            }
        }

        public override void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(this.SettingPathname) ?? string.Empty);
                var json = JsonSerializer.Serialize(this.Settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(this.SettingPathname, json);
            }
            catch
            {
                // Ignore
            }
        }

        public override void DrawSettings()
        {
            ImGui.TextColored(new Vector4(0.2f, 0.8f, 1.0f, 1.0f), "Expedition Path Optimizer (Runeshape Recipe & Path Strategy)");
            ImGui.Separator();
            ImGui.Spacing();

            bool enable = this.Settings.Enable;
            if (ImGui.Checkbox("Enable Optimizer", ref enable))
            {
                this.Settings.Enable = enable;
                this.SaveSettings();
            }

            bool autoStart = this.Settings.AutoStartOnAreaChange;
            if (ImGui.Checkbox("Auto-Start Optimization Once When In Range", ref autoStart))
            {
                this.Settings.AutoStartOnAreaChange = autoStart;
                this.SaveSettings();
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.3f, 1.0f), "Runeshape & Live Price Status");

            ImGui.Text($"Recipes Loaded: {this.recipePredictor.RecipeCount} | Partial Weights: {this.recipePredictor.RuneWeightsCount}");
            if (this.priceService != null)
            {
                var pStat = this.priceService.GetStatus();
                ImGui.TextColored(pStat.Loaded ? new Vector4(0.2f, 1.0f, 0.4f, 1.0f) : new Vector4(0.9f, 0.9f, 0.2f, 1.0f), $"Price Status: {pStat.Message}");
                ImGui.Text($"Cached Rates: 1 Divine = {pStat.DivineInChaos:F1}c | 1 Exalt = {pStat.ExaltedInChaos:F2}c ({pStat.TotalItems} items)");

                if (ImGui.Button("Refresh Prices Now", new Vector2(160, 24)))
                {
                    this.priceService.TriggerRefresh(this.Settings.League, this.Settings.PriceSource);
                    this.lastPriceRefreshUtc = DateTime.UtcNow;
                }
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.3f, 1.0f), "Live Encounter Detection Status");

            bool hasDetonator = this.detonatorWorldPos != Vector3.Zero;
            if (hasDetonator)
            {
                ImGui.TextColored(new Vector4(0.2f, 1.0f, 0.4f, 1.0f), $"Detonator Plunger: FOUND at ({this.detonatorWorldPos.X:F0}, {this.detonatorWorldPos.Y:F0})");
            }
            else
            {
                ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), "Detonator Plunger: NOT FOUND (Please walk towards Detonator)");
            }

            ImGui.Text($"Discovered Remnants / Monoliths: {this.discoveredRemnants.Count}");
            ImGui.Text($"Discovered Reward Chests: {this.discoveredChests.Count}");
            if (this.discoveredChests.Count > 0)
            {
                var chestGroups = this.discoveredChests.Values.GroupBy(c => c.IconName).Select(g => $"{g.Key}: {g.Count()}");
                ImGui.TextDisabled($"  ({string.Join(", ", chestGroups)})");
            }
            if (this.selectedFinalTarget != null)
            {
                var target = this.selectedFinalTarget;
                string anchorStr = target.IsUnique ? "Unique" : (target.AnchorRune ?? "None");
                string propStr = target.BestRuneRecipe?.PropagatedRunes != null && target.BestRuneRecipe.PropagatedRunes.Count > 0
                    ? string.Join(", ", target.BestRuneRecipe.PropagatedRunes)
                    : "None";
                string priceBestStr = target.BestPriceRecipe != null
                    ? $" | Price Best: {target.BestPriceRecipe.Reward} x{target.BestPriceRecipe.RewardCount} ({(target.BestPriceRecipe.IsPriced ? $"{target.BestPriceRecipe.PriceChaos:F0}c / {target.BestPriceRecipe.PriceDivine:F2}d" : "No price")})"
                    : "";

                ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), $"★ Final Target: {target.RuneSlots} Slots (Anchor: {anchorStr}), Rune Best Propagated: [{propStr}]{priceBestStr}");
            }
            ImGui.Text($"Placed Explosives Detected: {this.placedExplosives.Count}");

            ImGui.Spacing();
            if (this.runner.IsRunning)
            {
                if (ImGui.Button("Stop Search", new Vector2(160, 30)))
                {
                    this.runner.Stop();
                }
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.2f, 1.0f, 0.2f, 1.0f), "Optimizing in background...");
                ImGui.TextDisabled("Search settings are locked for this run; changes apply to the next search.");
            }
            else
            {
                if (ImGui.Button("Scan & Start Search", new Vector2(160, 30)))
                {
                    var area = Core.States.InGameStateObject?.CurrentAreaInstance;
                    if (area != null)
                    {
                        this.ScanEntities(area);
                        if (this.StartSearch(area))
                        {
                            this.hasAutoSearchedThisArea = true;
                        }
                    }
                }
                ImGui.SameLine();
                if (ImGui.Button("Clear Path", new Vector2(110, 30)))
                {
                    this.runner.Clear();
                }
                ImGui.SameLine();
                if (this.runner.CurrentBestPath != null && this.runner.CurrentBestPath.PerPointScore.Count > 0)
                {
                    var bestPlan = this.runner.CurrentBestPath;
                    bool wasPruned = bestPlan.WasPruned;
                    if (!wasPruned)
                    {
                        ImGui.TextColored(new Vector4(0.4f, 0.9f, 1.0f, 1.0f), $"Path Locked (Exact Recipe DP) - Score: {this.runner.CurrentBestScore:F1}, {bestPlan.PerPointScore.Count} bombs");
                    }
                    else
                    {
                        ImGui.TextColored(new Vector4(1.0f, 0.6f, 0.2f, 1.0f), $"Path Locked (Recipe DP Approximate) - Score: {this.runner.CurrentBestScore:F1}, {bestPlan.PerPointScore.Count} bombs");
                    }
                }
                else
                {
                    ImGui.TextDisabled("Idle (No path calculated)");
                }
            }

            if (this.runner.CurrentBestPath != null && this.runner.CurrentBestPath.PerPointScore.Count > 0)
            {
                var bestPlan = this.runner.CurrentBestPath;
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.3f, 1.0f), "Locked Route Score Breakdown");

                if (bestPlan.WasPruned)
                {
                    ImGui.TextColored(new Vector4(1.0f, 0.35f, 0.2f, 1.0f), "Recipe DP: PRUNED / APPROXIMATE (state cap reached)");
                }
                else
                {
                    ImGui.TextColored(new Vector4(0.2f, 1.0f, 0.4f, 1.0f), "Recipe DP: Exact");
                }
                ImGui.TextDisabled($"  Final DP State Cap Used: {bestPlan.DpStateCapUsed}");
                ImGui.TextDisabled($"  GA Search DP Cap: {PathPlanner.SearchRecipeDpStateCap}");
                ImGui.TextDisabled($"  Finalists Re-ranked: {bestPlan.FinalistsReRanked}");

                double econTotal = bestPlan.PerPointScore.Sum(p => p.RecipeEconomicScore);
                double runeTotal = bestPlan.PerPointScore.Sum(p => p.RuneScore);
                double oathTotal = bestPlan.PerPointScore.Sum(p => p.OathExposurePenalty);
                double backtrackTotal = bestPlan.PerPointScore.Sum(p => p.BacktrackPenalty);
                double totalScore = bestPlan.TotalScore;

                ImGui.Text($"  Recipe Economics: +{econTotal:F0}c");
                ImGui.Text($"  New Propagated Rune Value: +{runeTotal:F0}");
                if (oathTotal > 0.0)
                {
                    ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), $"  Oath Exposure: -{oathTotal:F0}");
                }
                else
                {
                    ImGui.Text($"  Oath Exposure: -0");
                }
                ImGui.Text($"  Backtrack: -{backtrackTotal:F1}");
                ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), $"  Total Route Score: {totalScore:F1}");

                ImGui.Spacing();
                ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.3f, 1.0f), "Planned Recipes (Debug)");
                for (int bIdx = 0; bIdx < bestPlan.PerPointScore.Count; bIdx++)
                {
                    var pt = bestPlan.PerPointScore[bIdx];
                    if (pt.PlannedRecipes == null || pt.PlannedRecipes.Count == 0)
                    {
                        continue;
                    }

                    int bombNum = bIdx + 1;
                    for (int pIdx = 0; pIdx < pt.PlannedRecipes.Count; pIdx++)
                    {
                        var plan = pt.PlannedRecipes[pIdx];
                        int slots = plan.Remnant.RuneSlots;
                        string recId = plan.Recipe.RecipeId ?? "Unknown";
                        string rewStr = $"{plan.Recipe.Reward} x{plan.Recipe.RewardCount} | {plan.Recipe.PriceChaos:F1}c";

                        var goldenList = new List<string>();
                        if (plan.Remnant.GoldenSlots != null && plan.Recipe.Runes != null)
                        {
                            for (int g = 0; g < plan.Remnant.GoldenSlots.Count; g++)
                            {
                                int gSlot = plan.Remnant.GoldenSlots[g];
                                if (gSlot >= 0 && gSlot < plan.Recipe.Runes.Count)
                                {
                                    goldenList.Add($"{gSlot}={plan.Recipe.Runes[gSlot]}");
                                }
                            }
                        }
                        string goldenStr = goldenList.Count > 0 ? $"[{string.Join(", ", goldenList)}]" : "None";
                        string newRunesStr = plan.NewlyAcquiredRunes.Count > 0 ? string.Join(", ", plan.NewlyAcquiredRunes) : "None";
                        string oathMarker = plan.NewlyAcquiredRunes.Contains("Oath", StringComparer.OrdinalIgnoreCase) ? " [OATH ACTIVATED]" : "";
                        string oathExpStr = pt.OathExposurePenalty > 0.0 ? $" | Oath Exposure: -{pt.OathExposurePenalty:F0}" : "";

                        ImGui.TextColored(new Vector4(0.8f, 0.9f, 1.0f, 1.0f), $"B{bombNum} | {slots} slots | {recId}{oathMarker}");
                        ImGui.TextDisabled($"   Reward: {rewStr}");
                        ImGui.TextDisabled($"   Golden: {goldenStr}");
                        ImGui.TextDisabled($"   New: {newRunesStr}");
                        ImGui.Text($"   Rune: +{plan.PropagatedRuneScore:F0} | Econ: +{plan.EconomicScore:F0}{oathExpStr}");
                    }
                }
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.3f, 1.0f), "Visual Overlays");

            bool showDet = this.Settings.ShowDetonatorMarker;
            if (ImGui.Checkbox("Show Detonator '0' (START) Badge", ref showDet))
            {
                this.Settings.ShowDetonatorMarker = showDet;
                this.SaveSettings();
            }

            bool showWorld = this.Settings.ShowWorldMarkers;
            if (ImGui.Checkbox("Show 3D World Markers", ref showWorld))
            {
                this.Settings.ShowWorldMarkers = showWorld;
                this.SaveSettings();
            }

            bool showMap = this.Settings.ShowLargeMapMarkers;
            if (ImGui.Checkbox("Show Large Map Markers", ref showMap))
            {
                this.Settings.ShowLargeMapMarkers = showMap;
                this.SaveSettings();
            }

            bool showRad = this.Settings.ShowBlastRadius;
            if (ImGui.Checkbox("Show Blast Radius Circles", ref showRad))
            {
                this.Settings.ShowBlastRadius = showRad;
                this.SaveSettings();
            }

            bool showLines = this.Settings.ShowPathLines;
            if (ImGui.Checkbox("Show Explosive Connecting Lines", ref showLines))
            {
                this.Settings.ShowPathLines = showLines;
                this.SaveSettings();
            }

            float radOp = this.Settings.BlastRadiusOpacity;
            if (ImGui.SliderFloat("Blast Radius Fill Opacity", ref radOp, 0.0f, 1.0f, "%.2f"))
            {
                this.Settings.BlastRadiusOpacity = radOp;
                this.SaveSettings();
            }

            float lineW = this.Settings.PathLineWidth;
            if (ImGui.SliderFloat("Connecting Line Width", ref lineW, 1.0f, 6.0f, "%.1f"))
            {
                this.Settings.PathLineWidth = lineW;
                this.SaveSettings();
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.3f, 1.0f), "Algorithm Parameters");

            int threads = this.Settings.SearchThreads;
            if (ImGui.SliderInt("Search Threads", ref threads, 1, 12))
            {
                this.Settings.SearchThreads = threads;
                this.SaveSettings();
            }

            float maxTime = this.Settings.MaximumGenerationTimeSeconds;
            if (ImGui.SliderFloat("Search Duration (sec)", ref maxTime, 1.0f, 10.0f, "%.1f"))
            {
                this.Settings.MaximumGenerationTimeSeconds = maxTime;
                this.SaveSettings();
            }

            int popSize = this.Settings.PathGenerationSize;
            if (ImGui.SliderInt("Population Size", ref popSize, 20, 500))
            {
                this.Settings.PathGenerationSize = popSize;
                this.SaveSettings();
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.3f, 1.0f), "Scoring & Penalty Parameters");

            float priceMult = (float)this.Settings.RecipePriceScoreMultiplier;
            if (ImGui.SliderFloat("Recipe Price Multiplier", ref priceMult, 0.0f, 5.0f, "%.2f"))
            {
                this.Settings.RecipePriceScoreMultiplier = priceMult;
                this.SaveSettings();
            }

            float oathPen = (float)this.Settings.OathPerSlotExposurePenalty;
            if (ImGui.SliderFloat("Oath Exposure Penalty / Slot", ref oathPen, 0.0f, 100.0f, "%.0f"))
            {
                this.Settings.OathPerSlotExposurePenalty = oathPen;
                this.SaveSettings();
            }

            float backtrackPen = (float)this.Settings.BacktrackPenaltyPerGrid;
            if (ImGui.SliderFloat("Backtrack Penalty / Grid", ref backtrackPen, 0.0f, 20.0f, "%.1f"))
            {
                this.Settings.BacktrackPenaltyPerGrid = backtrackPen;
                this.SaveSettings();
            }

            float chestScore = (float)this.Settings.ChestHitBaseScore;
            if (ImGui.SliderFloat("Chest Hit Score (+)", ref chestScore, 0.0f, 150.0f, "%.0f"))
            {
                this.Settings.ChestHitBaseScore = chestScore;
                this.SaveSettings();
            }

            float usefulBridge = (float)this.Settings.UsefulBridgePenalty;
            if (ImGui.SliderFloat("Useful Bridge Penalty (-)", ref usefulBridge, 0.0f, 100.0f, "%.0f"))
            {
                this.Settings.UsefulBridgePenalty = usefulBridge;
                this.SaveSettings();
            }

            float uselessBridge = (float)this.Settings.EmptyBombPenalty;
            if (ImGui.SliderFloat("Useless Empty Bomb Penalty (-)", ref uselessBridge, 50.0f, 300.0f, "%.0f"))
            {
                this.Settings.EmptyBombPenalty = uselessBridge;
                this.SaveSettings();
            }

            float shortBridgePct = (float)(this.Settings.ShortBridgePenaltyThreshold * 100.0);
            if (ImGui.SliderFloat("Short Bridge Min Reach (%)", ref shortBridgePct, 20.0f, 90.0f, "%.0f%%"))
            {
                this.Settings.ShortBridgePenaltyThreshold = shortBridgePct / 100.0;
                this.SaveSettings();
            }

            float travelPen = (float)this.Settings.TravelPenaltyMultiplier;
            if (ImGui.SliderFloat("Travel Penalty Multiplier", ref travelPen, 0.0f, 100.0f, "%.0f"))
            {
                this.Settings.TravelPenaltyMultiplier = travelPen;
                this.SaveSettings();
            }
        }

        public void ScanEntities(AreaInstance area)
        {
            var reader = Core.Process.Handle;
            var currentLiveExplosives = new Dictionary<IntPtr, Vector3>();

            void ProcessEntity(Entity entity)
            {
                if (entity == null || entity.Address == IntPtr.Zero || !entity.IsValid) return;
                var path = entity.Path;
                if (string.IsNullOrEmpty(path)) return;

                if (!entity.TryGetComponent<Render>(out var render, false) || render == null) return;
                var wPos = new Vector3(render.WorldPosition.X, render.WorldPosition.Y, render.TerrainHeight);
                var gPos = new Vector2(render.GridPosition.X, render.GridPosition.Y);

                bool isDetonator = false;
                if (path.Contains("Detonator", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains("Plunger", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains("ExpeditionDetonator", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains("Expedition2Detonator", StringComparison.OrdinalIgnoreCase))
                {
                    isDetonator = true;
                }
                else if (entity.TryGetComponent<Animated>(out var anim, false) && anim != null && !string.IsNullOrEmpty(anim.ModelPath))
                {
                    if (anim.ModelPath.Contains("plunger", StringComparison.OrdinalIgnoreCase) ||
                        anim.ModelPath.Contains("detonator", StringComparison.OrdinalIgnoreCase))
                    {
                        isDetonator = true;
                    }
                }

                if (isDetonator)
                {
                    this.detonatorWorldPos = wPos;
                    this.detonatorGridPos = gPos;
                }
                else if (path.Contains("ExpeditionExplosive", StringComparison.OrdinalIgnoreCase) &&
                         !path.Contains("Fuse", StringComparison.OrdinalIgnoreCase) &&
                         !path.Contains("Connector", StringComparison.OrdinalIgnoreCase) &&
                         !path.Contains("Indicator", StringComparison.OrdinalIgnoreCase) &&
                         !path.Contains("Marker", StringComparison.OrdinalIgnoreCase))
                {
                    currentLiveExplosives[entity.Address] = wPos;
                }
                else if (path.Contains("ExpeditionRelic", StringComparison.OrdinalIgnoreCase) ||
                         path.Contains("Expedition2Encounter", StringComparison.OrdinalIgnoreCase) ||
                         path.Contains("ExpeditionEncounter", StringComparison.OrdinalIgnoreCase) ||
                         path.Contains("Expedition2Remnant", StringComparison.OrdinalIgnoreCase) ||
                         path.Contains("ExpeditionRemnant", StringComparison.OrdinalIgnoreCase))
                {
                    int slots = 0;
                    int anchorPos = 0;
                    int anchorIdx = -1;
                    string? anchorRune = null;
                    bool isUnique = false;
                    var goldenSlots = new List<int>();

                    // Read Remnant details from StateMachine & Station pointer
                    if (entity.TryGetComponent<StateMachine>(out var sm) && sm.Address != IntPtr.Zero)
                    {
                        var listeners = reader.ReadMemory<StdVector>(sm.Address + 0x20);
                        var nodes = reader.ReadStdVector<long>(listeners);
                        if (nodes != null && nodes.Length > 0 && nodes.Length <= 256)
                        {
                            IntPtr station = IntPtr.Zero;
                            foreach (var nodeValue in nodes)
                            {
                                if (nodeValue == 0) continue;
                                var sub = reader.ReadMemory<IntPtr>(new IntPtr(nodeValue));
                                if (sub == IntPtr.Zero) continue;

                                var cand1 = sub - 0xA0;
                                if (reader.ReadMemory<IntPtr>(cand1 + 0x10) == entity.Address)
                                {
                                    station = cand1;
                                    break;
                                }

                                var cand2 = sub - 0x98;
                                if (reader.ReadMemory<IntPtr>(cand2 + 0x10) == entity.Address)
                                {
                                    station = cand2;
                                    break;
                                }

                                for (int off = 0x60; off <= 0x120; off += 8)
                                {
                                    var cand = sub - off;
                                    if (reader.ReadMemory<IntPtr>(cand + 0x10) == entity.Address)
                                    {
                                        station = cand;
                                        break;
                                    }
                                }

                                if (station != IntPtr.Zero) break;
                            }

                            if (station != IntPtr.Zero)
                            {
                                var hCount = reader.ReadMemory<int>(station + 0x38);
                                if (hCount is > 0 and <= 16) slots = hCount;

                                anchorPos = reader.ReadMemory<int>(station + 0x3C);
                                var rowPtr = reader.ReadMemory<IntPtr>(station + 0x28);
                                if (rowPtr == IntPtr.Zero)
                                {
                                    isUnique = true;
                                }
                                else
                                {
                                    var holder = reader.ReadMemory<IntPtr>(station + 0x30);
                                    if (holder != IntPtr.Zero)
                                    {
                                        var p1 = reader.ReadMemory<IntPtr>(holder + 0x28);
                                        if (p1 != IntPtr.Zero)
                                        {
                                            var tableBase = reader.ReadMemory<long>(p1);
                                            if (tableBase != 0)
                                            {
                                                var delta = rowPtr.ToInt64() - tableBase;
                                                if (delta >= 0)
                                                {
                                                    if (delta % 0x68 == 0) anchorIdx = (int)(delta / 0x68);
                                                    else if (delta % 0x6C == 0) anchorIdx = (int)(delta / 0x6C);
                                                }

                                                if (anchorIdx >= 0 && anchorIdx < RuneNames.Length)
                                                {
                                                    anchorRune = RuneNames[anchorIdx];
                                                }
                                                else
                                                {
                                                    anchorIdx = -1;
                                                }
                                            }
                                        }
                                    }
                                }

                                var goldenVec = reader.ReadMemory<StdVector>(station + 0x40);
                                var goldenCount = goldenVec.TotalElements(sizeof(int));
                                if (goldenCount > 0 && goldenCount <= 2)
                                {
                                    var gSlots = reader.ReadMemoryArray<int>(goldenVec.First, (int)goldenCount);
                                    if (gSlots != null)
                                    {
                                        foreach (var s in gSlots)
                                        {
                                            if (s >= 0 && s < slots && !goldenSlots.Contains(s))
                                            {
                                                goldenSlots.Add(s);
                                            }
                                        }
                                    }
                                    goldenSlots.Sort();
                                }
                                else if (goldenCount > 2)
                                {
                                    // A monolith has at most TWO GoldenSlots; >2 indicates invalid/suspicious data
                                    goldenSlots.Clear();
                                }
                            }
                        }
                    }

                    int areaLevel = area.CurrentAreaLevel;
                    List<RuneshapeRecipeOffer> offers;
                    RuneshapeRecipeOffer? bestPriceRecipe = null;
                    RuneshapeRecipeOffer? bestRuneRecipe = null;

                    if (slots > 0 && (isUnique || (anchorIdx >= 0 && anchorIdx < RuneNames.Length && anchorPos >= 0 && anchorPos < slots)))
                    {
                        offers = this.recipePredictor.PredictOffers(
                            slots,
                            anchorIdx,
                            anchorPos,
                            isUnique,
                            goldenSlots,
                            areaLevel,
                            this.priceService,
                            this.Settings.RuneWeights);

                        bestPriceRecipe = this.recipePredictor.SelectBestPriceRecipe(offers);
                        bestRuneRecipe = this.recipePredictor.SelectBestRuneRecipe(offers, this.Settings);
                    }
                    else
                    {
                        offers = new List<RuneshapeRecipeOffer>();
                    }

                    var remnant = new ExpeditionRemnant(
                        entity.Id,
                        entity.Address,
                        wPos,
                        gPos,
                        slots,
                        anchorIdx,
                        anchorPos,
                        anchorRune,
                        isUnique,
                        goldenSlots,
                        offers,
                        bestPriceRecipe,
                        bestRuneRecipe);

                    this.discoveredRemnants[entity.Address] = remnant;
                }
                else if (entity.TryGetComponent<MinimapIcon>(out var miniIcon, false) && miniIcon != null)
                {
                    var iconName = miniIcon.IconName;
                    if (!string.IsNullOrEmpty(iconName) && ExpeditionRewardChestIcons.Contains(iconName))
                    {
                        var chest = new ExpeditionChest(entity.Id, entity.Address, wPos, gPos, iconName);
                        this.discoveredChests[entity.Address] = chest;
                    }
                }
            }

            if (area.AwakeEntities != null)
            {
                foreach (var e in area.AwakeEntities.Values) ProcessEntity(e);
            }

            if (area.SleepingEntities != null)
            {
                foreach (var e in area.SleepingEntities.Values) ProcessEntity(e);
            }

            // 1. Determine Final Target BEFORE starting search: Max Slots -> Highest Rune Weight -> Proximity (only consider remnants with RuneSlots > 0)
            var validRemnants = this.discoveredRemnants.Values.Where(r => r.RuneSlots > 0).ToList();
            if (validRemnants.Count > 0)
            {
                int maxSlots = validRemnants.Max(r => r.RuneSlots);
                var candidates = validRemnants.Where(r => r.RuneSlots == maxSlots).ToList();
                this.selectedFinalTarget = candidates
                    .OrderByDescending(r => r.CalculateBaseRuneWeight(this.Settings))
                    .ThenBy(r => Vector2.Distance(this.detonatorGridPos, r.GridPos))
                    .First();
            }
            else
            {
                this.selectedFinalTarget = null;
            }

            // 2. Synchronize placed explosives positions for grey marker detection
            this.placedExplosives.Clear();
            foreach (var (k, v) in currentLiveExplosives)
            {
                this.placedExplosives[k] = v;
            }
        }

        public bool StartSearch(AreaInstance area)
        {
            if (this.detonatorWorldPos == Vector3.Zero)
            {
                PluginLog.Warning("ExpeditionPathOptimizer", "Cannot start optimization: Detonator Plunger not detected yet. Please walk towards the Detonator.");
                return false;
            }

            if (this.discoveredRemnants.Count == 0 || this.selectedFinalTarget == null)
            {
                PluginLog.Warning("ExpeditionPathOptimizer", "No valid expedition remnants/monoliths with known slot count detected in area.");
                return false;
            }

            var config = area.ExpeditionConfig;
            float radiusWorld = config.ExplosionRadiusWorld > 0 ? config.ExplosionRadiusWorld : 300f;
            float rangeWorld = config.PlacementReachWorld > 0 ? config.PlacementReachWorld : 1000f;
            int maxExplosions = config.ExplosiveCount > 0 ? config.ExplosiveCount : 5;

            float radiusGrid = radiusWorld / GridToWorldMultiplier;
            float rangeGrid = rangeWorld / GridToWorldMultiplier;

            Vector2 startGrid = this.detonatorGridPos;

            var env = new ExpeditionEnvironment(
                this.discoveredRemnants.Values.ToList(),
                this.discoveredChests.Values.ToList(),
                this.selectedFinalTarget,
                rangeGrid,
                radiusGrid,
                maxExplosions,
                startGrid,
                area.GridWalkableData,
                area.TerrainMetadata.BytesPerRow,
                config.IsGrandExpedition);

            this.runner.Start(this.Settings, env);
            return true;
        }

        public override void DrawUI()
        {
            if (!this.Settings.Enable) return;

            var game = Core.States.InGameStateObject;
            var area = game?.CurrentAreaInstance;
            var world = game?.CurrentWorldInstance;
            var gameUi = game?.GameUi;
            if (area == null || world == null) return;

            if (gameUi != null && (gameUi.IsAnyLargePanelOpen || (gameUi.RuneshapeCombinationsPanel.Address != IntPtr.Zero && gameUi.RuneshapeCombinationsPanel.IsVisible)))
            {
                return;
            }

            var largeMap = gameUi?.LargeMap;
            bool isLargeMap = largeMap != null && largeMap.Address != IntPtr.Zero && largeMap.IsVisible;
            var player = area.Player;
            Render? playerRender = null;
            bool canMapProject = isLargeMap && player != null && player.TryGetComponent<Render>(out playerRender, false) && playerRender != null;

            if (isLargeMap && !this.Settings.ShowLargeMapMarkers) return;
            if (!isLargeMap && !this.Settings.ShowWorldMarkers) return;

            Vector2 mapCenter = Vector2.Zero;
            float cos = 0, sin = 0;
            if (canMapProject && playerRender != null)
            {
                mapCenter = largeMap!.Center + largeMap.Shift + largeMap.DefaultShift;
                const float LargeMapXBias = 0.6f;
                const float LargeMapYBias = 0.3f;
                mapCenter.X += LargeMapXBias;
                mapCenter.Y += LargeMapYBias;

                var baseRes = UiElementBaseFuncs.BaseResolution;
                var baseDiag = Math.Sqrt((baseRes.X * baseRes.X) + (baseRes.Y * baseRes.Y));
                var mapHeight = largeMap.Size.Y > 0 ? largeMap.Size.Y : Core.Process.WindowArea.Size.Height;
                var largeMapDiagonalLength = baseDiag * mapHeight / baseRes.Y;

                const float LargeMapScaleBaseline = 0.187812f;
                var largeMapModifiedZoom = Math.Max(0.001f, (float)(largeMap.Zoom * LargeMapScaleBaseline));

                const double CameraAngle = 38.7 * Math.PI / 180;
                float mapScale = 240f / largeMapModifiedZoom;
                cos = (float)(largeMapDiagonalLength * Math.Cos(CameraAngle) / mapScale);
                sin = (float)(largeMapDiagonalLength * Math.Sin(CameraAngle) / mapScale);
            }

            Vector2 ToMap(Vector3 worldPos)
            {
                if (!canMapProject || playerRender == null) return Vector2.Zero;
                var gridX = worldPos.X / GridToWorldMultiplier;
                var gridY = worldPos.Y / GridToWorldMultiplier;
                var delta = new Vector2(gridX - playerRender.GridPosition.X, gridY - playerRender.GridPosition.Y);
                float deltaZ = (worldPos.Z - playerRender.TerrainHeight) / GridToWorldMultiplier;
                return mapCenter + new Vector2((delta.X - delta.Y) * cos, (deltaZ - (delta.X + delta.Y)) * sin);
            }

            var dl = ImGui.GetBackgroundDrawList();

            // 1. Draw Detonator "0" / START Anchor Badge
            if (this.detonatorWorldPos != Vector3.Zero && this.Settings.ShowDetonatorMarker)
            {
                if (canMapProject)
                {
                    var dMapPos = ToMap(this.detonatorWorldPos);
                    if (dMapPos != Vector2.Zero)
                    {
                        dl.AddCircleFilled(dMapPos, 12f, this.Settings.DetonatorBadgeBgColor);
                        dl.AddCircle(dMapPos, 12f, 0xFFFFFFFFu, 0, 2.0f);
                        string dStr = "0";
                        var tSz = ImGui.CalcTextSize(dStr);
                        dl.AddText(new Vector2(dMapPos.X - tSz.X * 0.5f, dMapPos.Y - tSz.Y * 0.5f), 0xFFFFFFFFu, dStr);

                        var lblSz = ImGui.CalcTextSize("START");
                        dl.AddText(new Vector2(dMapPos.X - lblSz.X * 0.5f, dMapPos.Y - 24f), 0xFF2ECC71u, "START");
                    }
                }
                else
                {
                    var dScreenPos = world.WorldToScreen(new Vector2(this.detonatorWorldPos.X, this.detonatorWorldPos.Y), this.detonatorWorldPos.Z);
                    if (dScreenPos != Vector2.Zero)
                    {
                        dl.AddCircleFilled(dScreenPos, 16f, this.Settings.DetonatorBadgeBgColor);
                        dl.AddCircle(dScreenPos, 16f, 0xFFFFFFFFu, 0, 2.5f);
                        string dStr = "0";
                        var tSz = ImGui.CalcTextSize(dStr);
                        dl.AddText(new Vector2(dScreenPos.X - tSz.X * 0.5f, dScreenPos.Y - tSz.Y * 0.5f), 0xFFFFFFFFu, dStr);

                        var lblSz = ImGui.CalcTextSize("START");
                        dl.AddText(new Vector2(dScreenPos.X - lblSz.X * 0.5f, dScreenPos.Y - 30f), 0xFF2ECC71u, "START");
                    }
                }
            }

            var bestDetailed = this.runner.CurrentBestPath;
            if (bestDetailed == null || bestDetailed.PerPointScore == null || bestDetailed.PerPointScore.Count == 0) return;

            var points = bestDetailed.PerPointScore;
            float radiusWorld = bestDetailed.Environment.ExplosionRadius * GridToWorldMultiplier;
            float zHeight = this.detonatorWorldPos != Vector3.Zero ? this.detonatorWorldPos.Z : (playerRender?.TerrainHeight ?? 0f);

            // 2. Match placed explosives with planned bomb locations
            const float matchTolerance = 4.0f;
            var placedPlanIndices = new HashSet<int>();

            foreach (var placedWorld in this.placedExplosives.Values)
            {
                var placedGrid = new Vector2(
                    placedWorld.X / GridToWorldMultiplier,
                    placedWorld.Y / GridToWorldMultiplier);

                int bestIndex = -1;
                float bestDist = matchTolerance;

                for (int i = 0; i < points.Count; i++)
                {
                    if (placedPlanIndices.Contains(i))
                        continue;

                    float d = Vector2.Distance(placedGrid, points[i].Point);

                    if (d <= bestDist)
                    {
                        bestDist = d;
                        bestIndex = i;
                    }
                }

                if (bestIndex >= 0)
                {
                    placedPlanIndices.Add(bestIndex);
                }
            }

            // 3. Draw Connecting Lines (Detonator -> Planned Bombs)
            if (this.Settings.ShowPathLines)
            {
                var wirePoints = new List<Vector3>();
                if (this.detonatorWorldPos != Vector3.Zero)
                {
                    wirePoints.Add(this.detonatorWorldPos);
                }

                foreach (var pt in points)
                {
                    wirePoints.Add(new Vector3(pt.Point.X * GridToWorldMultiplier, pt.Point.Y * GridToWorldMultiplier, zHeight));
                }

                for (int k = 0; k < wirePoints.Count - 1; k++)
                {
                    var p1 = wirePoints[k];
                    var p2 = wirePoints[k + 1];

                    var s1 = canMapProject ? ToMap(p1) : world.WorldToScreen(new Vector2(p1.X, p1.Y), p1.Z);
                    var s2 = canMapProject ? ToMap(p2) : world.WorldToScreen(new Vector2(p2.X, p2.Y), p2.Z);

                    if (s1 != Vector2.Zero && s2 != Vector2.Zero)
                    {
                        dl.AddLine(s1, s2, this.Settings.PathLineColor, this.Settings.PathLineWidth);
                    }
                }
            }

            // 4. Draw Planned Blast Circles & Badges (Greyed out if placed)
            for (int i = 0; i < points.Count; i++)
            {
                var ptInfo = points[i];
                var gridPt = ptInfo.Point;
                var bombWorld = new Vector3(gridPt.X * GridToWorldMultiplier, gridPt.Y * GridToWorldMultiplier, zHeight);
                int bombIndex = i + 1;
                bool isPlaced = placedPlanIndices.Contains(i);
                bool isFinalBomb = (i == points.Count - 1);

                uint badgeBg = isPlaced
                    ? 0xFF666666u
                    : isFinalBomb
                        ? this.Settings.FinalTargetBadgeBgColor
                        : this.Settings.BadgeBgColor;

                uint borderColor = isPlaced
                    ? 0xFF999999u
                    : isFinalBomb
                        ? 0xFFFFD700u
                        : 0xFFFFFFFFu;

                uint explosiveColor = isPlaced
                    ? 0xFF666666u
                    : this.Settings.ExplosiveColor;

                if (canMapProject)
                {
                    var mCenter = ToMap(bombWorld);
                    var mEdge = ToMap(new Vector3(bombWorld.X + radiusWorld, bombWorld.Y, zHeight));
                    float mapR = Vector2.Distance(mCenter, mEdge);

                    if (mCenter != Vector2.Zero && mapR > 1f)
                    {
                        if (this.Settings.ShowBlastRadius)
                        {
                            uint fillAlpha = (uint)(Math.Clamp(this.Settings.BlastRadiusOpacity, 0f, 1f) * 255f);
                            uint fillColor = (fillAlpha << 24) | (explosiveColor & 0x00FFFFFFu);
                            dl.AddCircleFilled(mCenter, mapR, fillColor, 32);
                            dl.AddCircle(mCenter, mapR, explosiveColor, 32, isFinalBomb ? 2.5f : 2.0f);
                        }

                        // Draw Number Badge
                        dl.AddCircleFilled(mCenter, isFinalBomb ? 12f : 10f, badgeBg);
                        dl.AddCircle(mCenter, isFinalBomb ? 12f : 10f, borderColor, 0, isFinalBomb ? 2.0f : 1.5f);
                        string numStr = bombIndex.ToString();
                        var tSz = ImGui.CalcTextSize(numStr);
                        dl.AddText(new Vector2(mCenter.X - tSz.X * 0.5f, mCenter.Y - tSz.Y * 0.5f), 0xFFFFFFFFu, numStr);

                        if (isFinalBomb)
                        {
                            var fSz = ImGui.CalcTextSize("★ FINAL");
                            dl.AddText(new Vector2(mCenter.X - fSz.X * 0.5f, mCenter.Y - 24f), isPlaced ? 0xFF999999u : 0xFFFFD700u, "★ FINAL");
                        }
                    }
                }
                else
                {
                    var sPos = world.WorldToScreen(new Vector2(bombWorld.X, bombWorld.Y), zHeight);
                    if (sPos == Vector2.Zero) continue;

                    if (this.Settings.ShowBlastRadius)
                    {
                        const int segments = 32;
                        Vector2 prevRing = Vector2.Zero;
                        Vector2 firstRing = Vector2.Zero;

                        for (int s = 0; s <= segments; s++)
                        {
                            float angle = (s % segments) * (MathF.PI * 2f / segments);
                            float px = bombWorld.X + MathF.Cos(angle) * radiusWorld;
                            float py = bombWorld.Y + MathF.Sin(angle) * radiusWorld;
                            var ringScreen = world.WorldToScreen(new Vector2(px, py), zHeight);

                            if (ringScreen == Vector2.Zero)
                            {
                                prevRing = Vector2.Zero;
                                continue;
                            }

                            if (s == 0) firstRing = ringScreen;
                            if (prevRing != Vector2.Zero)
                            {
                                dl.AddLine(prevRing, ringScreen, explosiveColor, isFinalBomb ? 2.5f : 2.0f);
                            }
                            prevRing = ringScreen;
                        }

                        if (prevRing != Vector2.Zero && firstRing != Vector2.Zero)
                        {
                            dl.AddLine(prevRing, firstRing, explosiveColor, isFinalBomb ? 2.5f : 2.0f);
                        }
                    }

                    // Draw Number Badge in World
                    dl.AddCircleFilled(sPos, isFinalBomb ? 16f : 14f, badgeBg);
                    dl.AddCircle(sPos, isFinalBomb ? 16f : 14f, borderColor, 0, isFinalBomb ? 2.5f : 2.0f);
                    string numStr = bombIndex.ToString();
                    var tSz = ImGui.CalcTextSize(numStr);
                    dl.AddText(new Vector2(sPos.X - tSz.X * 0.5f, sPos.Y - tSz.Y * 0.5f), 0xFFFFFFFFu, numStr);

                    if (isFinalBomb)
                    {
                        var fSz = ImGui.CalcTextSize("★ FINAL");
                        dl.AddText(new Vector2(sPos.X - fSz.X * 0.5f, sPos.Y - 30f), isPlaced ? 0xFF999999u : 0xFFFFD700u, "★ FINAL");
                    }
                }
            }
        }
    }
}
