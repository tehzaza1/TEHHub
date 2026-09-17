namespace ExpeditionPathOptimizer
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Numerics;
    using System.Text.Json;
    using ClickableTransparentOverlay.Win32;
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

        private readonly record struct DetonatorDiagnosticInfo(
            uint EntityId,
            IntPtr Address,
            Vector2 GridPos,
            Vector3 WorldPos,
            float DistanceFromPlayer);

        private sealed class SelectedEncounterState
        {
            public DetonatorDiagnosticInfo Detonator { get; set; }
            public List<List<ExpeditionRemnant>> AllBatches { get; set; } = new();
            public List<ExpeditionRemnant> SelectedBatch { get; set; } = new();
            public float SelectedBatchNearestDistance { get; set; }
            public List<ExpeditionRemnant> ComponentRemnants { get; set; } = new();
            public List<ExpeditionRemnant> ExcludedValidRemnants { get; set; } = new();
            public ExpeditionRemnant? FinalTarget { get; set; }
        }

        private Vector3 detonatorWorldPos = Vector3.Zero;
        private Vector2 detonatorGridPos = Vector2.Zero;
        private readonly List<DetonatorDiagnosticInfo> detectedDetonators = new();
        private readonly Dictionary<IntPtr, ExpeditionRemnant> discoveredRemnants = new();
        private readonly Dictionary<IntPtr, ExpeditionChest> discoveredChests = new();
        private readonly Dictionary<IntPtr, Vector3> placedExplosives = new();
        private readonly HashSet<IntPtr> expedition2EncounterAddresses = new();
        private SelectedEncounterState? selectedNormalMapEncounter = null;
        private ExpeditionRemnant? selectedFinalTarget = null;
        private bool detectedDetonatorDistancesHaveValidPlayerPosition = false;

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
        private bool isAutoStartWaitingForPrice = false;
        private string manualSearchWarningMessage = string.Empty;
        private DateTime manualSearchWarningTimeUtc = DateTime.MinValue;
        private bool scanAndStartHotkeyWasDown = false;
        private VK lastConfiguredHotkey = (VK)0;
        private bool isCapturingHotkey = false;
        private int lastDrawSettingsFrame = -1;
        private readonly HashSet<VK> keysDownAtCaptureStart = new();

        private static readonly VK[] CaptureCandidateKeys = Enum.GetValues<VK>()
            .Where(k => (int)k >= 0x08)
            .Distinct()
            .ToArray();

        private static string GetFriendlyKeyName(VK key)
        {
            if (key == (VK)0)
            {
                return "Unbound";
            }

            return key switch
            {
                VK.RETURN => "Enter",
                VK.ESCAPE => "Esc",
                VK.BACK => "Backspace",
                VK.TAB => "Tab",
                VK.SPACE => "Space",
                VK.PRIOR => "PageUp",
                VK.NEXT => "PageDown",
                VK.CAPITAL => "CapsLock",
                VK.SNAPSHOT => "PrintScreen",
                VK.NUMLOCK => "NumLock",
                VK.SCROLL => "ScrollLock",
                _ => key.ToString()
            };
        }

        private void StartHotkeyCapture()
        {
            this.isCapturingHotkey = true;
            this.lastDrawSettingsFrame = ImGui.GetFrameCount();
            this.keysDownAtCaptureStart.Clear();
            foreach (var key in CaptureCandidateKeys)
            {
                if (Utils.IsKeyPressed(key))
                {
                    this.keysDownAtCaptureStart.Add(key);
                }
            }
        }

        public static string[] RuneNames => RuneshapeRecipePredictor.RuneNames;

        private static string GetCurrentAreaId()
        {
            return Core.States.InGameStateObject?.CurrentWorldInstance?.AreaDetails?.Id ?? string.Empty;
        }

        private static bool IsExpeditionSubArea(string areaId)
        {
            return areaId.StartsWith("ExpeditionSubArea_", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsGrandExpeditionOrLogbook(AreaInstance? area, string areaId)
        {
            bool configSaysGrand = area?.ExpeditionConfig.IsGrandExpedition == true;
            bool idSaysInternalLogbook = areaId.StartsWith("ExpeditionLogBook_", StringComparison.OrdinalIgnoreCase);
            return configSaysGrand || idSaysInternalLogbook;
        }

        private static bool IsNormalMap(string areaId)
        {
            return areaId.StartsWith("Map", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsEligibleForNormalMapDiagnostics(AreaInstance? area, string areaId)
        {
            if (area == null) return false;
            if (IsExpeditionSubArea(areaId)) return false;
            if (IsGrandExpeditionOrLogbook(area, areaId)) return false;
            if (!IsNormalMap(areaId)) return false;

            return true;
        }

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
            this.detectedDetonators.Clear();
            this.discoveredRemnants.Clear();
            this.discoveredChests.Clear();
            this.placedExplosives.Clear();
            this.expedition2EncounterAddresses.Clear();
            this.selectedNormalMapEncounter = null;
            this.selectedFinalTarget = null;
            this.detectedDetonatorDistancesHaveValidPlayerPosition = false;
            this.hasAutoSearchedThisArea = false;
            this.isAutoStartWaitingForPrice = false;
            this.manualSearchWarningMessage = string.Empty;
        }

        public static bool IsPriceReady(double recipePriceScoreMultiplier, bool isPriceServiceLoaded)
        {
            if (recipePriceScoreMultiplier <= 0.0)
            {
                return true;
            }

            return isPriceServiceLoaded;
        }

        public static bool EvaluateAutoStartReadiness(bool hasDetonator, int remnantCount, bool priceReadyAtScanStart, out bool waitingForPrice)
        {
            if (hasDetonator && remnantCount > 0)
            {
                if (!priceReadyAtScanStart)
                {
                    waitingForPrice = true;
                    return false;
                }

                waitingForPrice = false;
                return true;
            }

            waitingForPrice = false;
            return false;
        }

        public bool IsPriceServiceReadyForSearch()
        {
            return IsPriceReady(this.Settings.RecipePriceScoreMultiplier, this.priceService != null && this.priceService.IsLoaded);
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

                var area = Core.States.InGameStateObject?.CurrentAreaInstance;
                if (area == null || area.Address == IntPtr.Zero) continue;

                var areaId = GetCurrentAreaId();
                if (IsExpeditionSubArea(areaId))
                {
                    if (this.runner.IsRunning || this.runner.CurrentBestPath != null || this.discoveredRemnants.Count > 0)
                    {
                        this.runner.Clear();
                        this.ClearState();
                    }
                    continue;
                }

                // Check background price auto-refresh
                if (this.priceService != null && this.Settings.AutoRefreshMinutes > 0)
                {
                    if (DateTime.UtcNow - this.lastPriceRefreshUtc > TimeSpan.FromMinutes(this.Settings.AutoRefreshMinutes))
                    {
                        this.lastPriceRefreshUtc = DateTime.UtcNow;
                        this.priceService.TriggerRefresh(this.Settings.League, this.Settings.PriceSource);
                    }
                }

                var areaHash = area.AreaHash ?? string.Empty;
                if (areaHash != this.lastAreaHash)
                {
                    this.lastAreaHash = areaHash;
                    this.runner.Clear();
                    this.ClearState();
                }

                bool priceReadyAtScanStart = this.IsPriceServiceReadyForSearch();

                this.ScanEntities(area);

                // Auto-start only ONCE per area when Expedition Detonator and Remnants are detected (Grand Expedition / Logbook only; normal maps require manual search)
                bool isNormalMap = IsNormalMap(areaId) && !IsGrandExpeditionOrLogbook(area, areaId);
                if (!isNormalMap &&
                    !this.hasAutoSearchedThisArea &&
                    this.Settings.AutoStartOnAreaChange &&
                    !this.runner.IsRunning &&
                    this.runner.CurrentBestPath == null)
                {
                    if (EvaluateAutoStartReadiness(this.detonatorWorldPos != Vector3.Zero, this.discoveredRemnants.Count, priceReadyAtScanStart, out this.isAutoStartWaitingForPrice))
                    {
                        if (this.StartSearch(area))
                        {
                            this.hasAutoSearchedThisArea = true;
                        }
                    }
                }
                else
                {
                    this.isAutoStartWaitingForPrice = false;
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
            var currentAreaId = GetCurrentAreaId();
            bool isSubArea = IsExpeditionSubArea(currentAreaId);

            ImGui.TextColored(new Vector4(0.2f, 0.8f, 1.0f, 1.0f), "Expedition Path Optimizer (Runeshape Recipe & Path Strategy)");
            if (isSubArea)
            {
                ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), $"[SAFETY GATE] Expedition Path Optimizer is DISABLED in Expedition Sub-Area ({currentAreaId}).");
            }
            ImGui.Separator();
            ImGui.Spacing();

            // ==========================================
            // 1. MAIN / NORMAL UI CONTROLS & STATUS
            // ==========================================
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

            int modeIdx = (int)this.Settings.OptimizationMode;
            if (ImGui.Combo("Optimization Mode", ref modeIdx, ExpeditionUiConstants.OptimizationModeComboString))
            {
                this.Settings.OptimizationMode = (OptimizationMode)modeIdx;
                this.SaveSettings();
            }

            if (this.Settings.OptimizationMode == OptimizationMode.PillarLootOnly)
            {
                ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), "  ★ Pillar Loot Only: optimizing by recipe value — ignoring generic farming bonuses & chests.");
            }

            ImGui.Text("Scan & Start Hotkey:");
            ImGui.SameLine();

            if (this.isCapturingHotkey)
            {
                this.lastDrawSettingsFrame = ImGui.GetFrameCount();

                ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.2f, 1.0f), "Press a key... (Esc to cancel)");
                ImGui.SameLine();
                if (ImGui.SmallButton("Cancel##ScanHotkey"))
                {
                    this.isCapturingHotkey = false;
                    this.keysDownAtCaptureStart.Clear();
                }

                if (Utils.IsKeyPressed(VK.ESCAPE))
                {
                    this.isCapturingHotkey = false;
                    this.keysDownAtCaptureStart.Clear();
                }
                else
                {
                    foreach (var key in CaptureCandidateKeys)
                    {
                        bool isKeyDown = Utils.IsKeyPressed(key);
                        if (isKeyDown)
                        {
                            if (!this.keysDownAtCaptureStart.Contains(key))
                            {
                                this.Settings.ScanAndStartHotkey = key;
                                this.isCapturingHotkey = false;
                                this.keysDownAtCaptureStart.Clear();
                                this.SaveSettings();
                                break;
                            }
                        }
                        else
                        {
                            this.keysDownAtCaptureStart.Remove(key);
                        }
                    }
                }
            }
            else
            {
                string keyDisplay = this.Settings.ScanAndStartHotkey == (VK)0
                    ? "Unbound"
                    : GetFriendlyKeyName(this.Settings.ScanAndStartHotkey);

                ImGui.TextColored(
                    this.Settings.ScanAndStartHotkey == (VK)0 ? new Vector4(0.7f, 0.7f, 0.7f, 1.0f) : new Vector4(0.2f, 0.9f, 1.0f, 1.0f),
                    $"[ {keyDisplay} ]");
                ImGui.SameLine();

                if (this.Settings.ScanAndStartHotkey == (VK)0)
                {
                    if (ImGui.SmallButton("Bind##ScanHotkey"))
                    {
                        this.StartHotkeyCapture();
                    }
                }
                else
                {
                    if (ImGui.SmallButton("Rebind##ScanHotkey"))
                    {
                        this.StartHotkeyCapture();
                    }
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Clear##ScanHotkey"))
                    {
                        this.Settings.ScanAndStartHotkey = (VK)0;
                        this.SaveSettings();
                    }
                }
            }

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
                    this.TryManualScanAndStart();
                }
                ImGui.SameLine();
                if (ImGui.Button("Clear Path", new Vector2(110, 30)))
                {
                    this.runner.Clear();
                    this.selectedNormalMapEncounter = null;
                    this.selectedFinalTarget = null;
                }
                ImGui.SameLine();
                if (this.runner.CurrentBestPath != null && this.runner.CurrentBestPath.PerPointScore.Count > 0)
                {
                    var bestPlan = this.runner.CurrentBestPath;
                    bool wasPruned = bestPlan.WasPruned;
                    if (!wasPruned)
                    {
                        ImGui.TextColored(new Vector4(0.4f, 0.9f, 1.0f, 1.0f), $"Path Locked: Score {this.runner.CurrentBestScore:F1} ({bestPlan.PerPointScore.Count} bombs)");
                    }
                    else
                    {
                        ImGui.TextColored(new Vector4(1.0f, 0.6f, 0.2f, 1.0f), $"Path Locked: Score {this.runner.CurrentBestScore:F1} ({bestPlan.PerPointScore.Count} bombs)");
                    }
                }
                else if (this.isAutoStartWaitingForPrice)
                {
                    ImGui.TextColored(new Vector4(1.0f, 0.75f, 0.2f, 1.0f), "Auto-Start waiting for initial price data...");
                }
                else
                {
                    bool isNorm = IsNormalMap(currentAreaId) && !IsGrandExpeditionOrLogbook(Core.States.InGameStateObject?.CurrentAreaInstance, currentAreaId);
                    if (isNorm)
                    {
                        ImGui.TextDisabled("Idle (Normal-map multi-Expedition selection is manual)");
                    }
                    else
                    {
                        ImGui.TextDisabled("Idle (No path calculated)");
                    }
                }

                if (!string.IsNullOrEmpty(this.manualSearchWarningMessage) && (DateTime.UtcNow - this.manualSearchWarningTimeUtc).TotalSeconds < 6.0)
                {
                    ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.2f, 1.0f), this.manualSearchWarningMessage);
                }
            }

            if (this.runner.CurrentBestPath != null && this.runner.CurrentBestPath.PerPointScore.Count > 0)
            {
                var bestPlan = this.runner.CurrentBestPath;
                if (bestPlan.WasPruned)
                {
                    ImGui.TextColored(new Vector4(1.0f, 0.35f, 0.2f, 1.0f), "Recipe DP: PRUNED / APPROXIMATE (state cap reached)");
                }
                else
                {
                    ImGui.TextColored(new Vector4(0.2f, 1.0f, 0.4f, 1.0f), "Recipe DP: Exact");
                }
            }

            ImGui.Text($"Placed Explosives Detected: {this.placedExplosives.Count}");

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

            // ==========================================
            // 2. ADVANCED TUNING SECTION
            // ==========================================
            if (ImGui.CollapsingHeader("Advanced Tuning"))
            {
                ImGui.Indent();

                ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.3f, 1.0f), "Algorithm Parameters");
                int threads = this.Settings.SearchThreads;
                if (ImGui.SliderInt("Search Threads", ref threads, ExpeditionUiConstants.SearchThreadsMin, ExpeditionUiConstants.SearchThreadsMax))
                {
                    this.Settings.SearchThreads = threads;
                    this.SaveSettings();
                }

                float maxTime = this.Settings.MaximumGenerationTimeSeconds;
                if (ImGui.SliderFloat("Search Duration (sec)", ref maxTime, ExpeditionUiConstants.SearchDurationMinSec, ExpeditionUiConstants.SearchDurationMaxSec, "%.1f"))
                {
                    this.Settings.MaximumGenerationTimeSeconds = maxTime;
                    this.SaveSettings();
                }

                int popSize = this.Settings.PathGenerationSize;
                if (ImGui.SliderInt("Population Size", ref popSize, ExpeditionUiConstants.PopulationSizeMin, ExpeditionUiConstants.PopulationSizeMax))
                {
                    this.Settings.PathGenerationSize = popSize;
                    this.SaveSettings();
                }

                float mutateChance = this.Settings.PathMutateChance;
                if (ImGui.SliderFloat("Path Mutate Chance", ref mutateChance, ExpeditionUiConstants.MutateChanceMin, ExpeditionUiConstants.MutateChanceMax, "%.2f"))
                {
                    this.Settings.PathMutateChance = mutateChance;
                    this.SaveSettings();
                }

                float injectRate = this.Settings.NewRandomPathInjectionRate;
                if (ImGui.SliderFloat("New Path Injection Rate", ref injectRate, ExpeditionUiConstants.RandomPathInjectionMin, ExpeditionUiConstants.RandomPathInjectionMax, "%.2f"))
                {
                    this.Settings.NewRandomPathInjectionRate = injectRate;
                    this.SaveSettings();
                }

                ImGui.Spacing();
                ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.3f, 1.0f), "Scoring & Penalty Parameters");

                float priceMult = (float)this.Settings.RecipePriceScoreMultiplier;
                if (ImGui.SliderFloat("Recipe Price Multiplier", ref priceMult, ExpeditionUiConstants.RecipePriceMultiplierMin, ExpeditionUiConstants.RecipePriceMultiplierMax, "%.2f"))
                {
                    this.Settings.RecipePriceScoreMultiplier = priceMult;
                    this.SaveSettings();
                }

                float oathPen = (float)this.Settings.OathPerSlotExposurePenalty;
                if (ImGui.SliderFloat("Oath Exposure Penalty / Slot", ref oathPen, ExpeditionUiConstants.OathPerSlotPenaltyMin, ExpeditionUiConstants.OathPerSlotPenaltyMax, "%.0f"))
                {
                    this.Settings.OathPerSlotExposurePenalty = oathPen;
                    this.SaveSettings();
                }

                float backtrackPen = (float)this.Settings.BacktrackPenaltyPerGrid;
                if (ImGui.SliderFloat("Backtrack Penalty / Grid", ref backtrackPen, ExpeditionUiConstants.BacktrackPenaltyMin, ExpeditionUiConstants.BacktrackPenaltyMax, "%.1f"))
                {
                    this.Settings.BacktrackPenaltyPerGrid = backtrackPen;
                    this.SaveSettings();
                }

                float chestScore = (float)this.Settings.ChestHitBaseScore;
                if (ImGui.SliderFloat("Chest Hit Score (+)", ref chestScore, ExpeditionUiConstants.ChestScoreMin, ExpeditionUiConstants.ChestScoreMax, "%.0f"))
                {
                    this.Settings.ChestHitBaseScore = chestScore;
                    this.SaveSettings();
                }

                float usefulBridge = (float)this.Settings.UsefulBridgePenalty;
                if (ImGui.SliderFloat("Useful Bridge Penalty (-)", ref usefulBridge, ExpeditionUiConstants.UsefulBridgePenaltyMin, ExpeditionUiConstants.UsefulBridgePenaltyMax, "%.0f"))
                {
                    this.Settings.UsefulBridgePenalty = usefulBridge;
                    this.SaveSettings();
                }

                float uselessBridge = (float)this.Settings.EmptyBombPenalty;
                if (ImGui.SliderFloat("Useless Empty Bomb Penalty (-)", ref uselessBridge, ExpeditionUiConstants.EmptyBombPenaltyMin, ExpeditionUiConstants.EmptyBombPenaltyMax, "%.0f"))
                {
                    this.Settings.EmptyBombPenalty = uselessBridge;
                    this.SaveSettings();
                }

                float shortBridgePct = (float)(this.Settings.ShortBridgePenaltyThreshold * 100.0);
                if (ImGui.SliderFloat("Short Bridge Threshold (% Reach)", ref shortBridgePct, ExpeditionUiConstants.ShortBridgeReachPctMin, ExpeditionUiConstants.ShortBridgeReachPctMax, "%.0f%%"))
                {
                    this.Settings.ShortBridgePenaltyThreshold = shortBridgePct / 100.0;
                    this.SaveSettings();
                }

                float shortBridgePen = (float)this.Settings.ShortBridgePenaltyMultiplier;
                if (ImGui.SliderFloat("Short Bridge Penalty (-)", ref shortBridgePen, ExpeditionUiConstants.ShortBridgePenaltyMin, ExpeditionUiConstants.ShortBridgePenaltyMax, "%.0f"))
                {
                    this.Settings.ShortBridgePenaltyMultiplier = shortBridgePen;
                    this.SaveSettings();
                }

                float travelPen = (float)this.Settings.TravelPenaltyMultiplier;
                if (ImGui.SliderFloat("Travel Penalty Multiplier", ref travelPen, ExpeditionUiConstants.TravelPenaltyMin, ExpeditionUiConstants.TravelPenaltyMax, "%.0f"))
                {
                    this.Settings.TravelPenaltyMultiplier = travelPen;
                    this.SaveSettings();
                }

                ImGui.Spacing();
                ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.3f, 1.0f), "Rune Weights");
                if (ImGui.TreeNode("Rune Weights Configuration"))
                {
                    ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), "Tier S (High Value / Key Runes)");
                    this.DrawRuneWeightSliders(ExpeditionUiConstants.TierSRunes);

                    ImGui.Spacing();
                    ImGui.TextColored(new Vector4(0.8f, 0.4f, 1.0f, 1.0f), "Tier A (Strong Runes)");
                    this.DrawRuneWeightSliders(ExpeditionUiConstants.TierARunes);

                    ImGui.Spacing();
                    ImGui.TextColored(new Vector4(0.2f, 0.8f, 1.0f, 1.0f), "Tier B (Utility / Support Runes)");
                    this.DrawRuneWeightSliders(ExpeditionUiConstants.TierBRunes);

                    ImGui.Spacing();
                    ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), "Tier C (Common Runes)");
                    this.DrawRuneWeightSliders(ExpeditionUiConstants.TierCRunes);

                    ImGui.TreePop();
                }

                ImGui.Spacing();
                ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.3f, 1.0f), "Price Service Configuration");

                string league = this.Settings.League ?? string.Empty;
                if (ImGui.InputText("League", ref league, 64))
                {
                    this.Settings.League = league;
                    this.SaveSettings();
                }

                int priceSource = this.Settings.PriceSource;
                if (ImGui.Combo("Price Source", ref priceSource, ExpeditionUiConstants.PriceSourceComboString))
                {
                    this.Settings.PriceSource = priceSource;
                    this.SaveSettings();
                }

                int refreshMins = this.Settings.AutoRefreshMinutes;
                if (ImGui.SliderInt("Auto Refresh (mins)", ref refreshMins, ExpeditionUiConstants.AutoRefreshMinutesMin, ExpeditionUiConstants.AutoRefreshMinutesMax))
                {
                    this.Settings.AutoRefreshMinutes = refreshMins;
                    this.SaveSettings();
                }

                if (this.priceService != null)
                {
                    if (ImGui.Button("Refresh Prices Now", new Vector2(160, 24)))
                    {
                        this.priceService.TriggerRefresh(this.Settings.League ?? "Standard", this.Settings.PriceSource);
                        this.lastPriceRefreshUtc = DateTime.UtcNow;
                    }
                }

                ImGui.Unindent();
                ImGui.Spacing();
            }

            // ==========================================
            // 3. DEBUG DIAGNOSTICS SECTION
            // ==========================================
            if (ImGui.CollapsingHeader("Debug Diagnostics"))
            {
                ImGui.Indent();

                var area = Core.States.InGameStateObject?.CurrentAreaInstance;
                if (isSubArea)
                {
                    ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), $"Expedition Path Optimizer is disabled in Expedition Sub-Area ({currentAreaId}).");
                }
                else
                {
                    ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.3f, 1.0f), "Live Encounter Detection Diagnostics");
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

                    if (this.IsEligibleForNormalMapDiagnostics(area, currentAreaId))
                    {
                        ImGui.Spacing();
                        ImGui.Separator();
                        ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.3f, 1.0f), "Regular Map Multi-Expedition Diagnostics");

                        int detCount = this.detectedDetonators.Count;
                        ImGui.Text($"Detonators Detected This Scan: {detCount}");
                        ImGui.TextDisabled($"  Player Position For Detonator Distance: {(this.detectedDetonatorDistancesHaveValidPlayerPosition ? "VALID" : "INVALID")}");

                        if (detCount > 0)
                        {
                            var primaryDet = this.detectedDetonators[0];
                            ImGui.TextColored(new Vector4(0.4f, 0.9f, 1.0f, 1.0f), "Primary Diagnostic Detonator (Nearest to Player):");
                            ImGui.TextDisabled($"  EntityId={primaryDet.EntityId} Address=0x{primaryDet.Address.ToInt64():X} Grid=({primaryDet.GridPos.X:F1}, {primaryDet.GridPos.Y:F1}) DistFromPlayer={primaryDet.DistanceFromPlayer:F1}");

                            if (detCount > 1)
                            {
                                ImGui.TextDisabled("All Detected Detonators This Scan:");
                                for (int d = 0; d < this.detectedDetonators.Count; d++)
                                {
                                    var det = this.detectedDetonators[d];
                                    string tag = d == 0 ? " (PRIMARY DIAGNOSTIC)" : "";
                                    ImGui.TextDisabled($"  [{d}] EntityId={det.EntityId} Address=0x{det.Address.ToInt64():X} Grid=({det.GridPos.X:F1}, {det.GridPos.Y:F1}) DistFromPlayer={det.DistanceFromPlayer:F1}{tag}");
                                }
                            }

                            var validRemnantsList = this.discoveredRemnants.Values.Where(r => r.RuneSlots > 0).ToList();
                            ImGui.Text($"Total discovered valid Remnants in area: {validRemnantsList.Count}");

                            if (validRemnantsList.Count > 0)
                            {
                                var sortedByDetDist = validRemnantsList
                                    .Select(r => new
                                    {
                                        Remnant = r,
                                        Distance = Vector2.Distance(primaryDet.GridPos, r.GridPos)
                                    })
                                    .OrderBy(x => x.Distance)
                                    .ThenBy(x => x.Remnant.EntityId)
                                    .ToList();

                                for (int i = 0; i < sortedByDetDist.Count; i++)
                                {
                                    var item = sortedByDetDist[i];
                                    var r = item.Remnant;
                                    string anchorInfo = r.IsUnique ? "Unique" : (r.AnchorRune ?? "None");

                                    float nearestOther = float.MaxValue;
                                    for (int j = 0; j < sortedByDetDist.Count; j++)
                                    {
                                        if (i == j) continue;
                                        float dOther = Vector2.Distance(r.GridPos, sortedByDetDist[j].Remnant.GridPos);
                                        if (dOther < nearestOther) nearestOther = dOther;
                                    }
                                    string nearestStr = sortedByDetDist.Count > 1 ? $" NearestOther={nearestOther:F1}" : "";

                                    ImGui.Text($"  [{i}] Entity={r.EntityId} Grid=({r.GridPos.X:F1}, {r.GridPos.Y:F1}) Slots={r.RuneSlots} Anchor={anchorInfo} Dist={item.Distance:F1}{nearestStr}");
                                }

                                if (sortedByDetDist.Count >= 2)
                                {
                                    int splitIndex = 1;
                                    float beforeDist = sortedByDetDist[0].Distance;
                                    float afterDist = sortedByDetDist[1].Distance;
                                    float maxGap = afterDist - beforeDist;

                                    for (int i = 2; i < sortedByDetDist.Count; i++)
                                    {
                                        float dPrev = sortedByDetDist[i - 1].Distance;
                                        float dCurr = sortedByDetDist[i].Distance;
                                        float gap = dCurr - dPrev;
                                        if (gap > maxGap)
                                        {
                                            maxGap = gap;
                                            splitIndex = i;
                                            beforeDist = dPrev;
                                            afterDist = dCurr;
                                        }
                                    }

                                    ImGui.Spacing();
                                    int nearCount = splitIndex;
                                    int farCount = sortedByDetDist.Count - splitIndex;
                                    ImGui.TextColored(new Vector4(0.3f, 1.0f, 0.8f, 1.0f), "Largest Distance Gap:");
                                    ImGui.Text($"  before={beforeDist:F1}");
                                    ImGui.Text($"  after={afterDist:F1}");
                                    ImGui.Text($"  gap={maxGap:F1}");
                                    ImGui.Text($"  splitIndex={splitIndex}");
                                    ImGui.Text($"  candidateNearCount={nearCount}");
                                    ImGui.Text($"  candidateFarCount={farCount}");
                                }
                                else
                                {
                                    ImGui.TextDisabled("Largest Distance Gap: N/A (< 2 remnants)");
                                }
                            }
                        }
                        else
                        {
                            var validRemnantsList = this.discoveredRemnants.Values.Where(r => r.RuneSlots > 0).ToList();
                            ImGui.TextDisabled($"Total discovered valid Remnants in area: {validRemnantsList.Count}");
                            ImGui.TextDisabled("Distance sorting and gap diagnostics require at least one detected Detonator.");
                        }

                        ImGui.Spacing();
                        ImGui.Separator();
                        ImGui.TextColored(new Vector4(0.3f, 1.0f, 0.6f, 1.0f), "Selected Normal-Map Encounter (Locked Route)");
                        if (this.selectedNormalMapEncounter != null)
                        {
                            var enc = this.selectedNormalMapEncounter;
                            ImGui.TextDisabled("  Ownership Mode: Expedition2Encounter EntityId +2 batch");
                            ImGui.Text($"  Selected Detonator: EntityId={enc.Detonator.EntityId} Address=0x{enc.Detonator.Address.ToInt64():X} Grid=({enc.Detonator.GridPos.X:F1}, {enc.Detonator.GridPos.Y:F1})");

                            if (enc.AllBatches.Count > 0)
                            {
                                ImGui.Spacing();
                                ImGui.TextColored(new Vector4(0.4f, 0.9f, 1.0f, 1.0f), $"  Spawn Batches ({enc.AllBatches.Count}):");
                                for (int b = 0; b < enc.AllBatches.Count; b++)
                                {
                                    var batch = enc.AllBatches[b];
                                    string idsStr = string.Join(",", batch.Select(r => r.EntityId));
                                    float dNear = batch.Min(m => Vector2.Distance(enc.Detonator.GridPos, m.GridPos));
                                    bool isSel = batch.Count == enc.SelectedBatch.Count && batch.Count > 0 && batch[0].EntityId == enc.SelectedBatch[0].EntityId;
                                    string selTag = isSel ? " [SELECTED]" : "";
                                    ImGui.TextDisabled($"    Batch [{b}]: [{idsStr}] | NearestDist={dNear:F1}{selTag}");
                                }
                            }

                            string selIdsStr = string.Join(",", enc.SelectedBatch.Select(r => r.EntityId));
                            ImGui.Text($"  Selected Batch: IDs=[{selIdsStr}] (NearestDist={enc.SelectedBatchNearestDistance:F1})");

                            ImGui.Spacing();
                            ImGui.TextColored(new Vector4(0.2f, 1.0f, 0.4f, 1.0f), $"  Selected Component Remnants ({enc.ComponentRemnants.Count}):");
                            foreach (var c in enc.ComponentRemnants)
                            {
                                string aStr = c.IsUnique ? "Unique" : (c.AnchorRune ?? "None");
                                ImGui.TextDisabled($"    #{c.EntityId} Grid=({c.GridPos.X:F1}, {c.GridPos.Y:F1}) Slots={c.RuneSlots} Anchor={aStr}");
                            }

                            if (enc.ExcludedValidRemnants.Count > 0)
                            {
                                ImGui.Spacing();
                                ImGui.TextColored(new Vector4(1.0f, 0.5f, 0.5f, 1.0f), $"  Excluded Valid Remnants ({enc.ExcludedValidRemnants.Count}):");
                                foreach (var ex in enc.ExcludedValidRemnants)
                                {
                                    string aStr = ex.IsUnique ? "Unique" : (ex.AnchorRune ?? "None");
                                    ImGui.TextDisabled($"    #{ex.EntityId} Grid=({ex.GridPos.X:F1}, {ex.GridPos.Y:F1}) Slots={ex.RuneSlots} Anchor={aStr}");
                                }
                            }

                            if (enc.FinalTarget != null)
                            {
                                var ft = enc.FinalTarget;
                                string aStr = ft.IsUnique ? "Unique" : (ft.AnchorRune ?? "None");
                                ImGui.TextColored(new Vector4(1.0f, 0.84f, 0.0f, 1.0f), $"  Selected FinalTarget: #{ft.EntityId} ({ft.RuneSlots} Slots, Anchor: {aStr})");
                            }

                            ImGui.TextDisabled($"  Chest Ownership: Phase 2 unchanged / area-global ({this.discoveredChests.Count} chests)");
                        }
                        else
                        {
                            ImGui.TextDisabled("  Ownership Mode: Expedition2Encounter EntityId +2 batch");
                            ImGui.TextDisabled("  Selected Encounter: None (Walk near desired Detonator and click 'Scan & Start Search')");
                            ImGui.TextDisabled($"  Chest Ownership: Phase 2 unchanged / area-global ({this.discoveredChests.Count} chests)");
                        }
                    }
                }

                ImGui.Spacing();
                ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.3f, 1.0f), "Runeshape Database & Price Diagnostics");
                ImGui.Text($"Recipes Loaded: {this.recipePredictor.RecipeCount} | Partial Weights: {this.recipePredictor.RuneWeightsCount}");
                if (this.priceService != null)
                {
                    var pStat = this.priceService.GetStatus();
                    ImGui.TextColored(pStat.Loaded ? new Vector4(0.2f, 1.0f, 0.4f, 1.0f) : new Vector4(0.9f, 0.9f, 0.2f, 1.0f), $"Price Status: {pStat.Message}");
                    ImGui.Text($"Cached Rates: 1 Divine = {pStat.DivineInChaos:F1}c | 1 Exalt = {pStat.ExaltedInChaos:F2}c ({pStat.TotalItems} items)");
                }
                if (this.isAutoStartWaitingForPrice)
                {
                    ImGui.TextColored(new Vector4(1.0f, 0.75f, 0.2f, 1.0f), "  [Status] Auto-Start waiting for initial price data...");
                }

                if (this.runner.CurrentBestPath != null && this.runner.CurrentBestPath.PerPointScore.Count > 0)
                {
                    var bestPlan = this.runner.CurrentBestPath;
                    ImGui.Spacing();
                    ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.3f, 1.0f), "Locked Route Technical Diagnostics");

                    ImGui.TextDisabled($"  Final DP State Cap Used: {bestPlan.DpStateCapUsed}");
                    ImGui.TextDisabled($"  GA Search DP Cap: {PathPlanner.SearchRecipeDpStateCap}");
                    ImGui.TextDisabled($"  Finalists Re-ranked: {bestPlan.FinalistsReRanked}");

                    double econTotal = bestPlan.PerPointScore.Sum(p => p.RecipeEconomicScore);
                    double recipeRuneTotal = bestPlan.PerPointScore.Sum(p => p.RecipeRuneScore);
                    double inheritedPropTotal = bestPlan.PerPointScore.Sum(p => p.InheritedPropagationScore);
                    double oathAcqTotal = bestPlan.PerPointScore.Sum(p => p.OathAcquisitionScore);
                    double oathExpTotal = bestPlan.PerPointScore.Sum(p => p.OathExposurePenalty);
                    double backtrackTotal = bestPlan.PerPointScore.Sum(p => p.BacktrackPenalty);
                    double totalScore = bestPlan.TotalScore;

                    double projectedRawPillarRewardsChaos = bestPlan.PerPointScore
                        .Where(p => p.PlannedRecipes != null)
                        .SelectMany(p => p.PlannedRecipes)
                        .Sum(plan => (double)plan.Recipe.PriceChaos);

                    ImGui.Text($"  Projected Pillar Rewards: {projectedRawPillarRewardsChaos:F1}c");
                    ImGui.Text($"  Recipe Economic Score: +{econTotal:F0}");
                    ImGui.Text($"  Recipe Rune Score: +{recipeRuneTotal:F0}");
                    ImGui.Text($"  Inherited Propagation Score: +{inheritedPropTotal:F0}");
                    if (oathAcqTotal < 0.0)
                    {
                        ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), $"  Oath Acquisition Score: {oathAcqTotal:F0}");
                    }
                    if (oathExpTotal > 0.0)
                    {
                        ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), $"  Oath Exposure: -{oathExpTotal:F0}");
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
                        int bombNum = bIdx + 1;
                        string newlyActivatedStr = pt.NewlyActivatedRunes != null && pt.NewlyActivatedRunes.Count > 0
                            ? string.Join(", ", pt.NewlyActivatedRunes)
                            : "None";

                        string bombOathAcqStr = pt.OathAcquisitionScore < 0.0 ? $" | Oath Acquisition: {pt.OathAcquisitionScore:F0}" : "";
                        string bombOathExpStr = pt.OathExposurePenalty > 0.0 ? $" | Oath Exposure: -{pt.OathExposurePenalty:F0}" : "";

                        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), $"Bomb {bombNum} (Step ScoreDiff: {pt.ScoreDiff:F1}) | Newly Activated: [{newlyActivatedStr}]{bombOathAcqStr}{bombOathExpStr}");

                        if (pt.PlannedRecipes != null && pt.PlannedRecipes.Count > 0)
                        {
                            for (int pIdx = 0; pIdx < pt.PlannedRecipes.Count; pIdx++)
                            {
                                var plan = pt.PlannedRecipes[pIdx];
                                int slots = plan.Remnant.RuneSlots;
                                string recId = plan.Recipe.RecipeId ?? "Unknown";
                                string rewStr = $"{plan.Recipe.Reward} x{plan.Recipe.RewardCount} | {plan.Recipe.PriceChaos:F1}c";
                                string runesStr = plan.Recipe.Runes != null ? $"[{string.Join(", ", plan.Recipe.Runes)}]" : "[]";

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
                                string goldenMapStr = goldenList.Count > 0 ? $"[{string.Join(", ", goldenList)}]" : "None";
                                string goldenOutStr = plan.GoldenOutputRunes != null && plan.GoldenOutputRunes.Count > 0 ? string.Join(", ", plan.GoldenOutputRunes) : "None";

                                ImGui.TextColored(new Vector4(0.8f, 0.9f, 1.0f, 1.0f), $"  Remnant #{plan.Remnant.EntityId} ({slots} slots) -> {recId}");
                                ImGui.TextDisabled($"     Runes: {runesStr} | Reward: {rewStr}");
                                ImGui.TextDisabled($"     Golden Mapping: {goldenMapStr} | Golden Output: [{goldenOutStr}]");
                                ImGui.Text($"     Recipe Rune: +{plan.RecipeRuneScore:F0} | Inherited Prop: +{plan.InheritedPropagationScore:F0} | Econ: +{plan.EconomicScore:F0}");
                            }
                        }
                        else
                        {
                            ImGui.TextDisabled("  No selected recipe on this bomb");
                        }
                    }

                    ImGui.Spacing();
                    if (ImGui.TreeNode("Recipe Candidate Audit"))
                    {
                        ImGui.TextDisabled("Raw candidate offers captured in the locked route snapshot (sorted by PriceChaos desc, RecipeId asc).");
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
                                var remnant = plan.Remnant;
                                var offers = remnant.RecipeOffers ?? Array.Empty<RuneshapeRecipeOffer>();
                                int candidateCount = offers.Count;

                                string anchorInfo = remnant.IsUnique ? "Unique" : (remnant.AnchorRune != null ? $"Anchor: {remnant.AnchorRune}" : "No Anchor");
                                string goldenInfo = remnant.GoldenSlots != null && remnant.GoldenSlots.Count > 0
                                    ? $"GoldenSlots: [{string.Join(", ", remnant.GoldenSlots)}]"
                                    : "GoldenSlots: None";

                                string headerLabel = $"B{bombNum}: Remnant #{remnant.EntityId} ({remnant.RuneSlots} slots, {anchorInfo}) - Candidates: {candidateCount}###Audit_B{bombNum}_R{pIdx}";

                                if (ImGui.TreeNode(headerLabel))
                                {
                                    ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), $"EntityId: {remnant.EntityId} | RuneSlots: {remnant.RuneSlots} | {anchorInfo} | {goldenInfo}");
                                    ImGui.TextColored(new Vector4(0.2f, 1.0f, 0.4f, 1.0f), $"Selected RecipeId: {plan.Recipe.RecipeId} | Locked DP Econ Score: +{plan.EconomicScore:F1}");

                                    // Find highest price among captured offers on this remnant for BEST PRICE badge
                                    float maxCapturedPrice = candidateCount > 0 ? offers.Max(o => o.PriceChaos) : 0.0f;

                                    // Sort candidates deterministically for UI display: PriceChaos descending, then RecipeId ascending
                                    var sortedCandidates = offers
                                        .OrderByDescending(o => o.PriceChaos)
                                        .ThenBy(o => o.RecipeId, StringComparer.OrdinalIgnoreCase)
                                        .ToList();

                                    for (int cIdx = 0; cIdx < sortedCandidates.Count; cIdx++)
                                    {
                                        var cand = sortedCandidates[cIdx];
                                        bool isSelected = string.Equals(cand.RecipeId, plan.Recipe.RecipeId, StringComparison.OrdinalIgnoreCase);
                                        bool isBestPrice = maxCapturedPrice > 0.0f && cand.PriceChaos == maxCapturedPrice;

                                        string badges = "";
                                        if (isSelected) badges += "[SELECTED] ";
                                        if (isBestPrice) badges += "[BEST PRICE] ";

                                        Vector4 rowColor = isSelected
                                            ? new Vector4(0.2f, 1.0f, 0.4f, 1.0f)
                                            : (isBestPrice ? new Vector4(1.0f, 0.84f, 0.0f, 1.0f) : new Vector4(0.9f, 0.9f, 0.9f, 1.0f));

                                        string goldenMap = RuneshapeRecipePredictor.FormatGoldenSlotMapping(remnant.GoldenSlots, cand.Runes);
                                        string runesStr = cand.Runes != null && cand.Runes.Count > 0 ? string.Join(", ", cand.Runes) : "None";
                                        string propStr = cand.PropagatedRunes != null && cand.PropagatedRunes.Count > 0 ? string.Join(", ", cand.PropagatedRunes) : "None";
                                        string pricedStatus = cand.IsPriced ? "Priced" : "Unpriced";

                                        // Diagnostic current-settings economic math
                                        double liveMultiplier = this.Settings.RecipePriceScoreMultiplier;
                                        double diagEconScore = cand.PriceChaos * liveMultiplier;

                                        ImGui.Separator();
                                        ImGui.TextColored(rowColor, $"  #{cIdx + 1}: {badges}{cand.RecipeId} - {cand.Reward} x{cand.RewardCount}");
                                        ImGui.TextDisabled($"      Price: {cand.PriceChaos:F1}c ({pricedStatus}) | Current Mult: {liveMultiplier:F2} | Est Econ: {diagEconScore:F1} (diagnostic)");
                                        ImGui.TextDisabled($"      Runes: [{runesStr}]");
                                        ImGui.TextDisabled($"      PropagatedRunes: [{propStr}]");
                                        ImGui.TextDisabled($"      Golden Mapping: {goldenMap}");

                                        // Check strict dominance warning against SELECTED offer
                                        if (!isSelected && RuneshapeRecipePredictor.CheckStrictDominance(plan.Recipe, cand, liveMultiplier, out float deltaPrice))
                                        {
                                            ImGui.TextColored(new Vector4(1.0f, 0.3f, 0.3f, 1.0f), $"      [STRICT DOMINANCE WARNING] Same propagation mask & runes as selected, but higher PriceChaos!");
                                            ImGui.TextColored(new Vector4(1.0f, 0.6f, 0.3f, 1.0f), $"      Selected: {plan.Recipe.PriceChaos:F1}c | Alternative: {cand.PriceChaos:F1}c | Delta: +{deltaPrice:F1}c");
                                        }
                                    }

                                    ImGui.TreePop();
                                }
                            }
                        }

                        ImGui.TreePop();
                    }
                }

                ImGui.Unindent();
                ImGui.Spacing();
            }
        }

        public void ScanEntities(AreaInstance area)
        {
            var areaId = GetCurrentAreaId();
            if (IsExpeditionSubArea(areaId))
            {
                this.ClearState();
                return;
            }

            var reader = Core.Process.Handle;
            var currentLiveExplosives = new Dictionary<IntPtr, Vector3>();
            var detectedDetonatorsThisScan = new List<DetonatorDiagnosticInfo>();

            var player = area.Player;
            bool hasPlayerGridPos = false;
            Vector2 playerGridPos = Vector2.Zero;
            if (player != null && player.TryGetComponent<Render>(out var pRender, false) && pRender != null)
            {
                playerGridPos = new Vector2(pRender.GridPosition.X, pRender.GridPosition.Y);
                hasPlayerGridPos = true;
            }

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
                    // Existing production behavior — preserve exactly.
                    this.detonatorWorldPos = wPos;
                    this.detonatorGridPos = gPos;

                    // Phase 1 diagnostic snapshot only.
                    if (!detectedDetonatorsThisScan.Any(d => d.Address == entity.Address))
                    {
                        float distToPlayer = hasPlayerGridPos ? Vector2.Distance(playerGridPos, gPos) : float.PositiveInfinity;
                        detectedDetonatorsThisScan.Add(new DetonatorDiagnosticInfo(entity.Id, entity.Address, gPos, wPos, distToPlayer));
                    }
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
                    if (path.Contains("Expedition2Encounter", StringComparison.OrdinalIgnoreCase))
                    {
                        this.expedition2EncounterAddresses.Add(entity.Address);
                    }

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

            // Synchronize diagnostic detonators (Diagnostic snapshot only - does NOT mutate production detonator fields)
            this.detectedDetonators.Clear();
            this.detectedDetonatorDistancesHaveValidPlayerPosition = hasPlayerGridPos;
            if (detectedDetonatorsThisScan.Count > 0)
            {
                var sortedDetonators = detectedDetonatorsThisScan
                    .OrderBy(d => d.DistanceFromPlayer)
                    .ThenBy(d => d.EntityId)
                    .ToList();

                this.detectedDetonators.AddRange(sortedDetonators);
            }

            // 1. Determine Final Target BEFORE starting search (Grand Expedition / Logbook uses global area remnants; normal map uses manual encounter selection)
            bool isNormalMap = IsNormalMap(areaId) && !IsGrandExpeditionOrLogbook(area, areaId);
            if (isNormalMap)
            {
                if (this.selectedNormalMapEncounter != null)
                {
                    this.selectedFinalTarget = this.selectedNormalMapEncounter.FinalTarget;
                }
                else
                {
                    this.selectedFinalTarget = null;
                }
            }
            else
            {
                var validRemnants = this.discoveredRemnants.Values.Where(r => r.RuneSlots > 0).ToList();
                if (validRemnants.Count > 0)
                {
                    if (this.Settings.OptimizationMode == OptimizationMode.PillarLootOnly)
                    {
                        // Pillar mode: RuneSlots is tie-break only — NOT a filter gate
                        // Candidate universe: all RuneSlots > 0 remnants with at least one recipe offer
                        var pillarCandidates = validRemnants
                            .Where(r => r.RecipeOffers != null && r.RecipeOffers.Count > 0)
                            .OrderByDescending(r => PathPlanner.StaticFinalPillarValue(r, this.Settings))
                            .ThenByDescending(r => r.RecipeOffers.Max(o => o.PriceChaos))
                            .ThenByDescending(r => r.RuneSlots)
                            .ThenBy(r => Vector2.Distance(this.detonatorGridPos, r.GridPos))
                            .ThenBy(r => r.EntityId)
                            .FirstOrDefault();

                        this.selectedFinalTarget = pillarCandidates;

                        if (this.selectedFinalTarget == null)
                        {
                            PluginLog.Warning("ExpeditionPathOptimizer", "[PillarLootOnly] No offer-bearing remnants found — cannot select FinalTarget for Grand/Logbook.");
                        }
                    }
                    else
                    {
                        // Balanced: max RuneSlots -> CalculateBaseRuneWeight -> nearest Detonator
                        int maxSlots = validRemnants.Max(r => r.RuneSlots);
                        var candidates = validRemnants.Where(r => r.RuneSlots == maxSlots).ToList();
                        this.selectedFinalTarget = candidates
                            .OrderByDescending(r => r.CalculateBaseRuneWeight(this.Settings))
                            .ThenBy(r => Vector2.Distance(this.detonatorGridPos, r.GridPos))
                            .First();
                    }
                }
                else
                {
                    this.selectedFinalTarget = null;
                }
            }

            // 2. Synchronize placed explosives positions for grey marker detection
            this.placedExplosives.Clear();
            foreach (var (k, v) in currentLiveExplosives)
            {
                this.placedExplosives[k] = v;
            }
        }

        private static List<List<ExpeditionRemnant>> BuildExpedition2Batches(IEnumerable<ExpeditionRemnant> remnants)
        {
            var sorted = remnants.OrderBy(r => r.EntityId).ToList();
            var batches = new List<List<ExpeditionRemnant>>();
            List<ExpeditionRemnant>? currentBatch = null;

            for (int i = 0; i < sorted.Count; i++)
            {
                var rem = sorted[i];
                if (currentBatch == null)
                {
                    currentBatch = new List<ExpeditionRemnant> { rem };
                    batches.Add(currentBatch);
                }
                else
                {
                    var prev = currentBatch[currentBatch.Count - 1];
                    if (rem.EntityId - prev.EntityId == 2)
                    {
                        currentBatch.Add(rem);
                    }
                    else
                    {
                        currentBatch = new List<ExpeditionRemnant> { rem };
                        batches.Add(currentBatch);
                    }
                }
            }

            return batches;
        }

        private bool TryBuildNormalMapEncounterSelection(
            AreaInstance area,
            DetonatorDiagnosticInfo selectedDetonator,
            out SelectedEncounterState? encounter,
            out string failureReason)
        {
            encounter = null;
            failureReason = string.Empty;

            if (selectedDetonator.Address == IntPtr.Zero)
            {
                failureReason = "No valid Detonator selected.";
                return false;
            }

            var validExpedition2Remnants = this.discoveredRemnants.Values
                .Where(r => r.RuneSlots > 0 && this.expedition2EncounterAddresses.Contains(r.EntityAddress))
                .OrderBy(r => r.EntityId)
                .ToList();

            if (validExpedition2Remnants.Count == 0)
            {
                failureReason = "No valid Expedition2Encounter Remnants with known slot count detected in area.";
                return false;
            }

            var allBatches = BuildExpedition2Batches(validExpedition2Remnants);
            if (allBatches.Count == 0)
            {
                failureReason = "No valid Expedition2Encounter spawn batches could be formed.";
                return false;
            }

            var rankedBatches = allBatches
                .Select(b => new
                {
                    Batch = b,
                    NearestDistance = b.Min(m => Vector2.Distance(selectedDetonator.GridPos, m.GridPos)),
                    MinEntityId = b.Min(m => m.EntityId)
                })
                .OrderBy(x => x.NearestDistance)
                .ThenBy(x => x.MinEntityId)
                .ToList();

            var selectedBatchInfo = rankedBatches[0];
            var componentRemnants = selectedBatchInfo.Batch;

            if (componentRemnants.Count == 0)
            {
                failureReason = "Selected Remnant batch is empty.";
                return false;
            }

            var selectedSet = new HashSet<IntPtr>(componentRemnants.Select(r => r.EntityAddress));
            var allValidRemnants = this.discoveredRemnants.Values
                .Where(r => r.RuneSlots > 0)
                .OrderBy(r => r.EntityId)
                .ToList();

            var excludedRemnants = allValidRemnants
                .Where(r => !selectedSet.Contains(r.EntityAddress))
                .OrderBy(r => r.EntityId)
                .ToList();

            ExpeditionRemnant? finalTarget;
            if (this.Settings.OptimizationMode == OptimizationMode.PillarLootOnly)
            {
                // Pillar mode: RuneSlots is tie-break only — NOT a filter gate
                // Candidate universe: all batch remnants with RuneSlots > 0 AND at least one recipe offer
                finalTarget = componentRemnants
                    .Where(r => r.RuneSlots > 0 && r.RecipeOffers != null && r.RecipeOffers.Count > 0)
                    .OrderByDescending(r => PathPlanner.StaticFinalPillarValue(r, this.Settings))
                    .ThenByDescending(r => r.RecipeOffers.Max(o => o.PriceChaos))
                    .ThenByDescending(r => r.RuneSlots)
                    .ThenBy(r => Vector2.Distance(selectedDetonator.GridPos, r.GridPos))
                    .ThenBy(r => r.EntityId)
                    .FirstOrDefault();
            }
            else
            {
                // Balanced: max RuneSlots -> CalculateBaseRuneWeight -> nearest Detonator
                int maxSlots = componentRemnants.Max(r => r.RuneSlots);
                var ftCandidates = componentRemnants.Where(r => r.RuneSlots == maxSlots).ToList();
                finalTarget = ftCandidates
                    .OrderByDescending(r => r.CalculateBaseRuneWeight(this.Settings))
                    .ThenBy(r => Vector2.Distance(selectedDetonator.GridPos, r.GridPos))
                    .FirstOrDefault();
            }

            if (finalTarget == null)
            {
                failureReason = "Failed to determine Final Target for selected Remnant batch.";
                return false;
            }

            encounter = new SelectedEncounterState
            {
                Detonator = selectedDetonator,
                AllBatches = allBatches,
                SelectedBatch = componentRemnants,
                SelectedBatchNearestDistance = selectedBatchInfo.NearestDistance,
                ComponentRemnants = componentRemnants,
                ExcludedValidRemnants = excludedRemnants,
                FinalTarget = finalTarget
            };

            return true;
        }

        public bool TryManualScanAndStart()
        {
            if (this.runner.IsRunning)
            {
                return false;
            }

            var currentAreaId = GetCurrentAreaId();
            if (IsExpeditionSubArea(currentAreaId))
            {
                this.manualSearchWarningMessage = $"Expedition Path Optimizer is disabled in Expedition sub-areas ({currentAreaId}).";
                this.manualSearchWarningTimeUtc = DateTime.UtcNow;
                PluginLog.Warning("ExpeditionPathOptimizer", this.manualSearchWarningMessage);
                return false;
            }

            if (!this.IsPriceServiceReadyForSearch())
            {
                this.manualSearchWarningMessage = "Price data is still loading; optimization was not started. Try again when price data is ready.";
                this.manualSearchWarningTimeUtc = DateTime.UtcNow;
                PluginLog.Warning("ExpeditionPathOptimizer", this.manualSearchWarningMessage);
                return false;
            }

            this.manualSearchWarningMessage = string.Empty;
            var area = Core.States.InGameStateObject?.CurrentAreaInstance;
            if (area != null && area.Address != IntPtr.Zero)
            {
                this.ScanEntities(area);
                if (this.StartSearch(area))
                {
                    this.hasAutoSearchedThisArea = true;
                    return true;
                }
            }

            return false;
        }

        public bool StartSearch(AreaInstance area)
        {
            var areaId = GetCurrentAreaId();
            if (IsExpeditionSubArea(areaId))
            {
                PluginLog.Warning("ExpeditionPathOptimizer", $"Expedition Path Optimizer is disabled in Expedition sub-areas ({areaId}).");
                return false;
            }

            if (!this.IsPriceServiceReadyForSearch())
            {
                PluginLog.Warning("ExpeditionPathOptimizer", "Price data is still loading; optimization was not started. Try again when price data is ready.");
                return false;
            }

            var config = area.ExpeditionConfig;
            float radiusWorld = config.ExplosionRadiusWorld > 0 ? config.ExplosionRadiusWorld : 300f;
            float rangeWorld = config.PlacementReachWorld > 0 ? config.PlacementReachWorld : 1000f;
            int maxExplosions = config.ExplosiveCount > 0 ? config.ExplosiveCount : 5;

            float radiusGrid = radiusWorld / GridToWorldMultiplier;
            float rangeGrid = rangeWorld / GridToWorldMultiplier;

            bool isNormalMap = IsNormalMap(areaId) && !IsGrandExpeditionOrLogbook(area, areaId);

            if (isNormalMap)
            {
                if (this.detectedDetonators.Count == 0)
                {
                    this.manualSearchWarningMessage = "Cannot start optimization: No Detonator detected in scan. Please walk towards the desired Detonator.";
                    this.manualSearchWarningTimeUtc = DateTime.UtcNow;
                    PluginLog.Warning("ExpeditionPathOptimizer", this.manualSearchWarningMessage);
                    return false;
                }

                if (!this.detectedDetonatorDistancesHaveValidPlayerPosition)
                {
                    this.manualSearchWarningMessage = "Cannot start optimization: Player grid position could not be read during this scan. Try again.";
                    this.manualSearchWarningTimeUtc = DateTime.UtcNow;
                    PluginLog.Warning("ExpeditionPathOptimizer", this.manualSearchWarningMessage);
                    return false;
                }

                var selectedDet = this.detectedDetonators[0]; // Nearest detected Detonator to player

                if (!this.TryBuildNormalMapEncounterSelection(area, selectedDet, out var encounter, out string failureReason))
                {
                    this.manualSearchWarningMessage = $"Cannot start optimization: {failureReason}";
                    this.manualSearchWarningTimeUtc = DateTime.UtcNow;
                    PluginLog.Warning("ExpeditionPathOptimizer", this.manualSearchWarningMessage);
                    return false;
                }

                this.selectedNormalMapEncounter = encounter;
                this.selectedFinalTarget = encounter!.FinalTarget;
                this.manualSearchWarningMessage = string.Empty;

                Vector2 startGrid = encounter.Detonator.GridPos;

                bool isPillarMode = this.Settings.OptimizationMode == OptimizationMode.PillarLootOnly;
                var chestsForSearch = isPillarMode
                    ? new List<ExpeditionChest>()
                    : this.discoveredChests.Values.ToList();

                var env = new ExpeditionEnvironment(
                    encounter.ComponentRemnants,
                    chestsForSearch,
                    encounter.FinalTarget!,
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
            else
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

                Vector2 startGrid = this.detonatorGridPos;

                bool isPillarModeGrand = this.Settings.OptimizationMode == OptimizationMode.PillarLootOnly;
                var chestsForSearchGrand = isPillarModeGrand
                    ? new List<ExpeditionChest>()
                    : this.discoveredChests.Values.ToList();

                var env = new ExpeditionEnvironment(
                    this.discoveredRemnants.Values.ToList(),
                    chestsForSearchGrand,
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
        }

        public override void DrawUI()
        {
            if (this.isCapturingHotkey && ImGui.GetFrameCount() - this.lastDrawSettingsFrame > 2)
            {
                this.isCapturingHotkey = false;
                this.keysDownAtCaptureStart.Clear();
            }

            var configuredHotkey = this.Settings.ScanAndStartHotkey;
            bool isBound = configuredHotkey != (VK)0 && (int)configuredHotkey > 0;
            bool isDown = isBound && Utils.IsKeyPressed(configuredHotkey);

            bool bindingChanged = configuredHotkey != this.lastConfiguredHotkey;
            if (bindingChanged)
            {
                this.lastConfiguredHotkey = configuredHotkey;
                this.scanAndStartHotkeyWasDown = isDown;
            }

            bool pressedThisFrame = !bindingChanged && isDown && !this.scanAndStartHotkeyWasDown;
            this.scanAndStartHotkeyWasDown = isDown;

            if (!this.Settings.Enable)
            {
                return;
            }

            if (pressedThisFrame && !this.runner.IsRunning && !this.isCapturingHotkey)
            {
                this.TryManualScanAndStart();
            }

            var game = Core.States.InGameStateObject;
            var area = game?.CurrentAreaInstance;
            var world = game?.CurrentWorldInstance;
            var gameUi = game?.GameUi;
            if (area == null || world == null) return;

            var areaId = world.AreaDetails?.Id ?? string.Empty;
            if (IsExpeditionSubArea(areaId)) return;

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
            Vector3 detonatorDrawWorldPos = this.selectedNormalMapEncounter != null && this.selectedNormalMapEncounter.Detonator.WorldPos != Vector3.Zero
                ? this.selectedNormalMapEncounter.Detonator.WorldPos
                : this.detonatorWorldPos;

            if (detonatorDrawWorldPos != Vector3.Zero && this.Settings.ShowDetonatorMarker)
            {
                if (canMapProject)
                {
                    var dMapPos = ToMap(detonatorDrawWorldPos);
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
                    var dScreenPos = world.WorldToScreen(new Vector2(detonatorDrawWorldPos.X, detonatorDrawWorldPos.Y), detonatorDrawWorldPos.Z);
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
            float zHeight = detonatorDrawWorldPos != Vector3.Zero ? detonatorDrawWorldPos.Z : (playerRender?.TerrainHeight ?? 0f);

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
                if (detonatorDrawWorldPos != Vector3.Zero)
                {
                    wirePoints.Add(detonatorDrawWorldPos);
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

        private void DrawRuneWeightSliders(string[] runeNames)
        {
            if (this.Settings.RuneWeights == null) return;

            foreach (var rName in runeNames)
            {
                float val = (float)this.Settings.RuneWeights.GetValueOrDefault(rName, 20.0);
                if (ImGui.SliderFloat($"{rName}##rw_{rName}", ref val, ExpeditionUiConstants.RuneWeightMin, ExpeditionUiConstants.RuneWeightMax, "%.0f"))
                {
                    this.Settings.RuneWeights[rName] = val;
                    this.SaveSettings();
                }
            }
        }
    }
}

