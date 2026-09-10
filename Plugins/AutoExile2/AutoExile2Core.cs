// <copyright file="AutoExile2Core.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.IO;
    using System.Numerics;
    using ClickableTransparentOverlay.Win32;
    using GameHelper;
    using GameHelper.Plugin;
    using GameHelper.RemoteEnums;
    using GameHelper.RemoteEnums.Entity;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.RemoteObjects.States;
    using GameHelper.RemoteObjects.States.InGameStateObjects;
    using GameHelper.Utils;
    using ImGuiNET;
    using AutoExile2.Modes;
    using AutoExile2.Systems;
    using AutoExile2.WebServer;
    using Coroutine;
    using Newtonsoft.Json;

    /// <summary>
    /// Core plugin class for AutoExile 2.
    /// Manages bot lifecycle, modular mode dispatching (MapFarm, Follower, Boss, Idle),
    /// humanized input, flight recorder, telemetry, and web dashboard.
    /// </summary>
    public sealed class AutoExile2Core : PCore<AutoExile2Settings>
    {
        // Core shared systems
        private readonly ExplorationMap explorationMap = new();
        private readonly CombatSystem combatSystem = new();
        private readonly ThreatMap threatMap = new();
        private readonly BotRecorder recorder = new();
        private readonly PerformanceTracker perf = new();
        private readonly RuntimeTracker runtime = new();
        private readonly ProfileManager profileManager = new();
        private readonly CoopVirtualGamepad coopGamepad = new();
        private AutoExileWebServer? webServer;

        // Coroutine & cached execution contexts to eliminate GC allocation
        private ActiveCoroutine? botCoroutine;
        private BotContext? renderCtx;
        private BotContext? botCtx;

        // Modes architecture (Matching AutoExile 1)
        private readonly Dictionary<AutoExileMode, IBotMode> modes = new();
        private IBotMode activeMode;
        private AutoExileMode lastModeType = AutoExileMode.MapFarm;
        private bool isModeInitialized = false;

        private string SettingsPath => Path.Join(this.DllDirectory, "config", "settings.txt");

        // Hotkey edge trigger
        private bool lastToggleKeyDown = false;
        private bool lastDumpKeyDown = false;
        private string lastDumpStatus = string.Empty;
        private DateTime lastTickTime = DateTime.Now;
        private bool lastIsRunning = false;

        public AutoExile2Core()
        {
            var waveFarm = new AutoExile2.Modes.WaveFarm.WaveFarmMode();
            var follower = new CoopFollowerMode();
            var boss = new BossMode();
            var idle = new IdleMode();

            this.modes[AutoExileMode.MapFarm] = waveFarm;
            this.modes[AutoExileMode.Follower] = follower;
            this.modes[AutoExileMode.Boss] = boss;
            this.modes[AutoExileMode.Idle] = idle;

            this.activeMode = waveFarm;
        }

        /// <inheritdoc/>
        public override void OnEnable(bool isGameOpened)
        {
            this.profileManager.Initialize(this.DllDirectory);
            this.profileManager.OnProfileSwitched += (newName) =>
            {
                if (this.modes.TryGetValue(this.Settings.Mode, out var configuredMode))
                {
                    this.activeMode = configuredMode;
                    this.lastModeType = this.Settings.Mode;
                }
            };

            this.Settings = this.profileManager.LoadActive(new AutoExile2Settings());

            if (this.modes.TryGetValue(this.Settings.Mode, out var activeModeObj))
            {
                this.activeMode = activeModeObj;
                this.lastModeType = this.Settings.Mode;
            }

            if (this.Settings.EnableWebServer)
            {
                this.StartWebServer();
            }

            this.botCoroutine = CoroutineHandler.Start(this.BotLogicCoroutine());
        }

        /// <inheritdoc/>
        public override void OnDisable()
        {
            this.botCoroutine?.Cancel();
            this.botCoroutine = null;

            this.Settings.IsRunning = false;
            this.combatSystem.StopAllChannels();
            BotInput.ReleaseAllMovementKeys(this.Settings);
            this.coopGamepad.Disconnect();
            this.webServer?.Stop();
            this.webServer = null;
        }

        /// <inheritdoc/>
        public override void SaveSettings()
        {
            this.profileManager.SaveActive(this.Settings);
        }

        private void StartWebServer()
        {
            this.webServer?.Stop();
            this.webServer = new AutoExileWebServer(
                this.Settings.WebServerPort,
                this.Settings.WebServerNetworkAccess,
                this.Settings,
                this.profileManager,
                this.GetStatusSnapshot,
                this.SaveSettings,
                this.TriggerDump,
                () => this.modes.Values,
                this.coopGamepad);
            this.webServer.Start();
        }

        public string TriggerDump()
        {
            var inGameState = Core.States.InGameStateObject;
            var currentArea = inGameState?.CurrentAreaInstance;
            var currentWorld = inGameState?.CurrentWorldInstance;
            string areaName = currentWorld?.AreaDetails.Name ?? currentArea?.AreaHash ?? "UnknownArea";

            var (_, _, message) = DebugDumpSystem.DumpCurrentState(
                currentArea,
                areaName,
                this.explorationMap,
                this.combatSystem,
                this.Settings,
                this.activeMode.CurrentState,
                this.activeMode.CurrentAction,
                this.activeMode.CurrentNavPath,
                this.activeMode.CurrentWaypointIndex,
                this.activeMode.CurrentDestination,
                this.perf,
                this.runtime);

            string recorderStatus = this.recorder.ForceDump("MANUAL", areaName);
            string fullStatus = $"{message} | {recorderStatus}";
            this.lastDumpStatus = fullStatus;
            return fullStatus;
        }

        private AutoExileStatusSnapshot GetStatusSnapshot()
        {
            var inGameState = Core.States.InGameStateObject;
            var currentArea = inGameState?.CurrentAreaInstance;
            var currentWorld = inGameState?.CurrentWorldInstance;
            var player = currentArea?.Player;

            int hpPct = 100;
            int esPct = 0;
            int hpCur = 0, hpTot = 0;
            int esCur = 0, esTot = 0;
            int manaPct = 100;

            if (player != null && player.TryGetComponent<Life>(out var life))
            {
                var vitals = new PlayerVitals(life);
                hpCur = vitals.CurrentHp;
                hpTot = vitals.MaxHp;
                hpPct = (int)vitals.HpPercent;
                esCur = vitals.CurrentEs;
                esTot = vitals.MaxEs;
                esPct = vitals.HasEs ? (int)vitals.EsPercent : 0;
                manaPct = (int)vitals.ManaPercent;
            }

            var detectedSkills = new List<DetectedSkillInfo>();
            int activeTotems = 0;
            int activeMinions = 0;

            var deployedObjects = new List<DeployedObjectInfo>();

            if (player != null && player.TryGetComponent<Actor>(out var actor))
            {
                if (actor.ActiveSkills != null)
                {
                    foreach (var (skillName, details) in actor.ActiveSkills)
                    {
                        if (string.IsNullOrWhiteSpace(skillName)) continue;
                        string cat = SkillClassifier.Classify(skillName);
                        bool usable = actor.IsSkillUsable.Contains(skillName);
                        int maxUses = 1;
                        int activeCds = 0;
                        if (actor.ActiveSkillCooldowns != null && actor.ActiveSkillCooldowns.TryGetValue(details.UnknownIdAndEquipmentInfo, out var cdInfo))
                        {
                            maxUses = cdInfo.MaxUses;
                            activeCds = cdInfo.TotalActiveCooldowns();
                        }

                        detectedSkills.Add(new DetectedSkillInfo
                        {
                            Name = skillName,
                            Category = cat,
                            CooldownMs = details.TotalCooldownTimeInMs,
                            CanBeUsed = usable,
                            MaxUses = maxUses,
                            ActiveCooldowns = activeCds,
                        });
                    }
                }

                if (actor.DeployedEntities != null)
                {
                    foreach (var (typeId, count) in actor.DeployedEntities)
                    {
                        string catName = DeployedObjectCounter.CategoryName(typeId);
                        deployedObjects.Add(new DeployedObjectInfo
                        {
                            TypeId = typeId,
                            Count = count,
                            Category = catName,
                        });

                        if (catName == "Totems" || typeId == 48349 || typeId == 52139 || catName.Contains("Totem", StringComparison.OrdinalIgnoreCase))
                        {
                            activeTotems += count;
                        }
                        else
                        {
                            activeMinions += count;
                        }
                    }
                }
            }

            // Also robustly scan active friendly totems in the area (within 40g of player)
            var activeTotemNames = new List<string>();
            Vector2 pPosGrid = Vector2.Zero;
            bool hasPPos = false;
            if (player != null && player.TryGetComponent<Render>(out var pRnd))
            {
                hasPPos = true;
                pPosGrid = new Vector2(pRnd.GridPosition.X, pRnd.GridPosition.Y);
            }

            if (currentArea != null && currentArea.AwakeEntities != null)
            {
                foreach (var entity in currentArea.AwakeEntities.Values)
                {
                    if (!entity.IsValid) continue;
                    if (entity.Path.Contains("Totem", StringComparison.OrdinalIgnoreCase) ||
                        entity.Path.Contains("Ballista", StringComparison.OrdinalIgnoreCase) ||
                        entity.Path.Contains("Ancestor", StringComparison.OrdinalIgnoreCase))
                    {
                        if (CombatSystem.IsFriendlyTotem(entity) && CombatSystem.IsTotemAlive(entity))
                        {
                            if (hasPPos && entity.TryGetComponent<Render>(out var tRnd) && tRnd != null)
                            {
                                var tGrid = new Vector2(tRnd.GridPosition.X, tRnd.GridPosition.Y);
                                if (Vector2.Distance(pPosGrid, tGrid) > 40f)
                                {
                                    continue; // Farther than 40g, count as 0/none
                                }
                            }

                            string cleanName = entity.Path;
                            int lastSlash = cleanName.LastIndexOf('/');
                            if (lastSlash >= 0) cleanName = cleanName.Substring(lastSlash + 1);
                            int atSign = cleanName.IndexOf('@');
                            if (atSign >= 0) cleanName = cleanName.Substring(0, atSign);
                            activeTotemNames.Add(cleanName);
                        }
                    }
                }
            }
            activeTotems = activeTotemNames.Count;

            var detectedBuffs = new List<ActiveBuffInfo>();
            if (player != null && player.TryGetComponent<Buffs>(out var pBuffs) && pBuffs.StatusEffects != null)
            {
                foreach (var (buffName, eff) in pBuffs.StatusEffects)
                {
                    if (string.IsNullOrWhiteSpace(buffName)) continue;
                    detectedBuffs.Add(new ActiveBuffInfo
                    {
                        Name = buffName,
                        TimeLeft = float.IsInfinity(eff.TimeLeft) ? 0f : (float)Math.Round(eff.TimeLeft, 1),
                        Charges = eff.Charges,
                    });
                }
            }

            var detectedDebuffs = new List<ActiveBuffInfo>(this.combatSystem.ObservedTargetDebuffs);

            int followerHp = 100;
            int followerEs = 0;
            int fHpCur = 0, fHpTot = 0;
            int fEsCur = 0, fEsTot = 0;
            int followerMana = 100;
            bool followerFound = false;
            var p2DetectedSkills = new List<DetectedSkillInfo>();
            string leaderPlayerName = "";
            string followerPlayerName = "";

            if (player != null && player.TryGetComponent<Player>(out var pComp))
            {
                leaderPlayerName = pComp.Name;
            }

            var nearbyPlayerNames = new List<string>();
            var nearbyPlayers = new List<NearbyPlayerDetail>();
            Entity? targetFollowerEntity = null;

            Vector2 leaderPos = Vector2.Zero;
            if (player != null && player.TryGetComponent<Render>(out var lRnd))
            {
                leaderPos = new Vector2(lRnd.GridPosition.X, lRnd.GridPosition.Y);
            }

            if (currentArea?.Player2 != null && currentArea.Player2.Address != IntPtr.Zero && currentArea.Player2.IsValid)
            {
                targetFollowerEntity = currentArea.Player2;
                if (targetFollowerEntity.TryGetComponent<Player>(out var fPlayerComp) && !string.IsNullOrEmpty(fPlayerComp.Name))
                {
                    followerPlayerName = fPlayerComp.Name;
                    if (!nearbyPlayerNames.Contains(followerPlayerName))
                    {
                        nearbyPlayerNames.Add(followerPlayerName);
                    }
                }
            }

            if (currentArea?.AwakeEntities != null && player != null)
            {
                var candidates = new List<(Entity ent, string name)>();

                foreach (var kvp in currentArea.AwakeEntities)
                {
                    var ent = kvp.Value;
                    if (!ent.IsValid || ent.Address == player.Address) continue;

                    if (ent.EntityType == EntityTypes.Player || (ent.Path != null && ent.Path.StartsWith("Metadata/Characters/")))
                    {
                        string pName = ent.TryGetComponent<Player>(out var fPlayerComp) ? fPlayerComp.Name : string.Empty;
                        candidates.Add((ent, pName));
                        if (!string.IsNullOrWhiteSpace(pName) && !nearbyPlayerNames.Contains(pName))
                        {
                            nearbyPlayerNames.Add(pName);

                            float dist = 0f;
                            if (ent.TryGetComponent<Render>(out var eRnd))
                            {
                                dist = Vector2.Distance(leaderPos, new Vector2(eRnd.GridPosition.X, eRnd.GridPosition.Y));
                            }

                            int php = 100;
                            if (ent.TryGetComponent<Life>(out var eLife) && eLife.Health.Total > 0)
                            {
                                php = (int)((float)eLife.Health.Current / eLife.Health.Total * 100f);
                            }

                            string cls = ParseCharacterClass(ent.Path);

                            nearbyPlayers.Add(new NearbyPlayerDetail
                            {
                                Name = pName,
                                ClassName = cls,
                                Distance = (float)Math.Round(dist, 1),
                                HpPercent = php,
                            });
                        }
                    }
                }

                string targetFilter = this.Settings.FollowerCharacterName?.Trim() ?? string.Empty;

                if (targetFollowerEntity == null)
                {
                    if (!string.IsNullOrEmpty(targetFilter))
                    {
                        var exact = candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c.name) && c.name.Equals(targetFilter, StringComparison.OrdinalIgnoreCase));
                        if (exact.ent != null)
                        {
                            targetFollowerEntity = exact.ent;
                            followerPlayerName = exact.name;
                        }
                        else
                        {
                            var partial = candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c.name) && c.name.Contains(targetFilter, StringComparison.OrdinalIgnoreCase));
                            if (partial.ent != null)
                            {
                                targetFollowerEntity = partial.ent;
                                followerPlayerName = partial.name;
                            }
                        }
                    }
                    else if (candidates.Count == 1)
                    {
                        targetFollowerEntity = candidates[0].ent;
                        followerPlayerName = candidates[0].name;

                        // Auto-lock onto buddy when in 2-player private instance
                        if (!string.IsNullOrEmpty(candidates[0].name) && string.IsNullOrEmpty(this.Settings.FollowerCharacterName))
                        {
                            this.Settings.FollowerCharacterName = candidates[0].name;
                        }
                    }
                }

                if (targetFollowerEntity != null)
                {
                    followerFound = true;
                    if (string.IsNullOrEmpty(followerPlayerName) && targetFollowerEntity.TryGetComponent<Player>(out var fPlayerComp))
                    {
                        followerPlayerName = fPlayerComp.Name;
                    }

                    if (targetFollowerEntity.TryGetComponent<Life>(out var fLife))
                    {
                        var fVitals = new PlayerVitals(fLife);
                        fHpCur = fVitals.CurrentHp;
                        fHpTot = fVitals.MaxHp;
                        fEsCur = fVitals.CurrentEs;
                        fEsTot = fVitals.MaxEs;
                        followerHp = (int)fVitals.HpPercent;
                        followerEs = fVitals.HasEs ? (int)fVitals.EsPercent : 0;
                        followerMana = (int)fVitals.ManaPercent;
                    }

                    if (targetFollowerEntity.TryGetComponent<Actor>(out var fActor) && fActor.ActiveSkills != null)
                    {
                        foreach (var (skillName, details) in fActor.ActiveSkills)
                        {
                            if (string.IsNullOrWhiteSpace(skillName)) continue;
                            string cat = SkillClassifier.Classify(skillName);
                            bool usable = fActor.IsSkillUsable.Contains(skillName);
                            int maxUses = 1;
                            int activeCds = 0;
                            if (fActor.ActiveSkillCooldowns != null && fActor.ActiveSkillCooldowns.TryGetValue(details.UnknownIdAndEquipmentInfo, out var cdInfo))
                            {
                                maxUses = cdInfo.MaxUses;
                                activeCds = cdInfo.TotalActiveCooldowns();
                            }

                            p2DetectedSkills.Add(new DetectedSkillInfo
                            {
                                Name = skillName,
                                Category = cat,
                                CooldownMs = details.TotalCooldownTimeInMs,
                                CanBeUsed = usable,
                                MaxUses = maxUses,
                                ActiveCooldowns = activeCds,
                            });
                        }
                    }
                }
            }

            return new AutoExileStatusSnapshot
            {
                IsRunning = this.Settings.IsRunning,
                Mode = this.activeMode.Name,
                State = this.activeMode.CurrentState,
                AreaName = currentWorld?.AreaDetails.Name ?? "Unknown",
                FollowerPlayerName = followerPlayerName,
                LeaderPlayerName = leaderPlayerName,
                NearbyPlayerNames = nearbyPlayerNames,
                NearbyPlayers = nearbyPlayers,
                ExplorationCoverage = this.explorationMap.Coverage,
                HostileCount = this.combatSystem.NearbyHostileCount,
                PlayerHpPercent = hpPct,
                PlayerEsPercent = esPct,
                PlayerHpCurrent = hpCur,
                PlayerHpTotal = hpTot,
                PlayerEsCurrent = esCur,
                PlayerEsTotal = esTot,
                PlayerManaPercent = manaPct,
                CurrentAction = this.activeMode.CurrentAction,
                ActiveDuration = this.runtime.FormattedDuration,
                DetectedSkills = detectedSkills,
                P2DetectedSkills = p2DetectedSkills,
                DetectedBuffs = detectedBuffs,
                DetectedDebuffs = detectedDebuffs,
                ActiveTotems = activeTotems,
                ActiveMinions = activeMinions,
                ActiveTotemNames = activeTotemNames,
                DeployedObjects = deployedObjects,
                FollowerHpPercent = followerHp,
                FollowerEsPercent = followerEs,
                FollowerHpCurrent = fHpCur,
                FollowerHpTotal = fHpTot,
                FollowerEsCurrent = fEsCur,
                FollowerEsTotal = fEsTot,
                FollowerManaPercent = followerMana,
                IsFollowerActive = followerFound,
                FollowerSlotIndex = this.coopGamepad.FollowerSlotIndex,
                LeaderSlotIndex = this.coopGamepad.LeaderSlotIndex,
            };
        }

        /// <inheritdoc/>
        public override void DrawSettings()
        {
            bool running = this.Settings.IsRunning;
            if (running)
            {
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.8f, 0.2f, 0.2f, 1f));
                if (ImGui.Button("STOP BOT (PAUSE)", new Vector2(200, 36)))
                {
                    this.Settings.IsRunning = false;
                    this.combatSystem.StopAllChannels();
                    BotInput.ReleaseAllMovementKeys(this.Settings);
                    this.coopGamepad.Disconnect();
                }
                ImGui.PopStyleColor();
            }
            else
            {
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.7f, 0.3f, 1f));
                if (ImGui.Button("START BOT", new Vector2(200, 36)))
                {
                    this.Settings.IsRunning = true;
                }
                ImGui.PopStyleColor();
            }

            ImGui.SameLine();
            ImGui.Text($"Mode: {this.activeMode.Name} | State: {this.activeMode.CurrentState} ({this.activeMode.CurrentAction})");

            ImGui.TextColored(new Vector4(0.35f, 0.85f, 1f, 1f), "[Brain Tick Rate: 60 Hz (16.6ms) - High Performance]");

            var inGameState = Core.States.InGameStateObject;
            var curArea = inGameState?.CurrentAreaInstance;
            if (curArea?.Player2 != null && curArea.Player2.Address != IntPtr.Zero && curArea.Player2.IsValid)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.2f, 1.0f, 0.4f, 1f), "[Co-op: Connected]");
            }

            ImGui.Separator();

            ImGui.Checkbox("Show Circles On Adjust (All Modes)", ref this.Settings.ShowDistanceCircles);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Renders 3D ground circles only while adjusting distance sliders (Follow, Culler, Combat, Fight, Skills), auto-hiding when done.");
            }
            ImGui.SameLine();
            ImGui.Checkbox("Show Overlay", ref this.Settings.ShowOverlay);

            if (ImGui.Checkbox("Allow Network / Phone Access", ref this.Settings.WebServerNetworkAccess))
            {
                if (this.Settings.EnableWebServer)
                {
                    this.StartWebServer();
                }
            }

            if (this.webServer != null && this.webServer.IsRunning)
            {
                ImGui.TextColored(new Vector4(0.3f, 1f, 0.45f, 1f), $"Dashboard online at {this.webServer.Url}");
                ImGui.SameLine();
                if (ImGui.SmallButton("Open in Browser"))
                {
                    Process.Start(new ProcessStartInfo(this.webServer.Url) { UseShellExecute = true });
                }
            }
            else
            {
                ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "Dashboard is offline.");
                ImGui.SameLine();
                if (ImGui.SmallButton("Start Dashboard"))
                {
                    this.Settings.EnableWebServer = true;
                    this.StartWebServer();
                }
            }
        }

        private static ref int UnsafeEnumRef<T>(ref T value) where T : unmanaged, Enum
        {
            return ref System.Runtime.CompilerServices.Unsafe.As<T, int>(ref value);
        }

        private void CheckModeSwitch(BotContext? ctx)
        {
            if (this.Settings.Mode != this.lastModeType || !this.isModeInitialized)
            {
                if (this.modes.TryGetValue(this.Settings.Mode, out var newMode))
                {
                    if (ctx != null)
                    {
                        if (this.isModeInitialized)
                        {
                            this.activeMode.OnExit(ctx);
                        }
                        this.activeMode = newMode;
                        this.activeMode.OnEnter(ctx);
                        this.isModeInitialized = true;
                    }
                    else
                    {
                        this.activeMode = newMode;
                    }
                    this.lastModeType = this.Settings.Mode;
                }
            }
        }

        /// <inheritdoc/>
        public override void DrawUI()
        {
            // 1. Hotkey edge trigger polling (instant sub-microsecond response)
            bool isToggleDown = BotInput.IsKeyDown(this.Settings.ToggleKey);
            if (isToggleDown && !this.lastToggleKeyDown)
            {
                this.Settings.IsRunning = !this.Settings.IsRunning;
                if (!this.Settings.IsRunning)
                {
                    this.combatSystem.StopAllChannels();
                    BotInput.ReleaseAllMovementKeys(this.Settings);
                    this.coopGamepad.ResetAllInputs();
                }
            }
            this.lastToggleKeyDown = isToggleDown;

            bool isDumpDown = BotInput.IsKeyDown(this.Settings.DumpKey);
            if (isDumpDown && !this.lastDumpKeyDown)
            {
                this.TriggerDump();
            }
            this.lastDumpKeyDown = isDumpDown;

            // 2. High-speed Emergency Vitals & Flasks (Zero-delay frame reaction, matching AutoHotKeyTrigger)
            this.TickEmergencyRecovery();

            // 3. Draw overlay badge & active mode visuals ONLY (no heavy bot AI or entity loops!)
            if (this.Settings.ShowOverlay)
            {
                var inGameState = Core.States.InGameStateObject;
                var currentArea = inGameState?.CurrentAreaInstance;
                var currentWorld = inGameState?.CurrentWorldInstance;
                var player = currentArea?.Player;

                this.RenderOverlay(currentWorld, player, currentArea);
            }
        }

        /// <summary>
        /// Evaluated on EVERY render frame (~60-144 FPS) inside DrawUI with ZERO artificial delay or sleep.
        /// Directly mirrors AutoHotKeyTrigger's ultra-responsive emergency rule triggers for Life/Mana flasks
        /// and emergency defense guard skills as soon as player health drops.
        /// </summary>
        private void TickEmergencyRecovery()
        {
            if (Core.States.GameCurrentState != GameStateTypes.InGameState)
            {
                return;
            }

            if (!Core.Process.Foreground)
            {
                return;
            }

            var inGameState = Core.States.InGameStateObject;
            if (inGameState == null)
            {
                return;
            }

            // Do not drink flasks or trigger skills while chatting
            if (inGameState.GameUi?.ChatParent?.IsChatActive == true)
            {
                return;
            }

            var currentArea = inGameState.CurrentAreaInstance;
            var currentWorld = inGameState.CurrentWorldInstance;
            if (currentArea == null || currentWorld == null)
            {
                return;
            }

            // Do not drink flasks in town
            if (currentWorld.AreaDetails.IsTown)
            {
                return;
            }

            var player = currentArea.Player;
            if (player == null || !player.IsValid)
            {
                return;
            }

            if (!player.TryGetComponent<Life>(out var pLife) || pLife.Health.Current <= 0)
            {
                return; // Player dead or no life component
            }

            // Check grace period (invulnerability after entering area)
            if (player.TryGetComponent<Buffs>(out var pBuffs) && pBuffs.StatusEffects != null)
            {
                if (pBuffs.StatusEffects.ContainsKey("grace_period"))
                {
                    return;
                }
            }

            var vitals = new PlayerVitals(pLife);

            // 1. Instant Auto Flasks
            if (this.Settings.Mode == AutoExileMode.Follower)
            {
                var now = DateTime.Now;
                if (this.Settings.P1AutoLifeFlask && vitals.HpPercent <= this.Settings.P1LifeFlaskThresholdPercent)
                {
                    int lifeSlot = CombatSystem.GetFlaskSlotFromKey(this.Settings.LifeFlaskKey, 0);
                    bool active = this.Settings.CheckFlaskActiveEffect && CombatSystem.IsFlaskActive(player, lifeSlot, isLife: true);
                    bool hasCharges = !this.Settings.CheckFlaskCharges || CombatSystem.HasFlaskCharges(currentArea.ServerDataObject, lifeSlot);

                    if (!active && hasCharges)
                    {
                        const int debounceMs = CombatSystem.FlaskDebounceMs;

                        if ((now - this.combatSystem.LastLifeFlaskAt).TotalMilliseconds >= debounceMs)
                        {
                            this.combatSystem.LastLifeFlaskAt = now;
                            if (this.coopGamepad.IsLeaderConnected)
                            {
                                this.coopGamepad.PressLeaderFlask(true, debounceMs);
                            }
                            else
                            {
                                BotInput.FastPressKey(this.Settings.LifeFlaskKey);
                            }
                        }
                    }
                }

                if (this.Settings.P1AutoManaFlask && vitals.ManaPercent <= this.Settings.P1ManaFlaskThresholdPercent)
                {
                    int manaSlot = CombatSystem.GetFlaskSlotFromKey(this.Settings.ManaFlaskKey, 1);
                    bool active = this.Settings.CheckFlaskActiveEffect && CombatSystem.IsFlaskActive(player, manaSlot, isLife: false);
                    bool hasCharges = !this.Settings.CheckFlaskCharges || CombatSystem.HasFlaskCharges(currentArea.ServerDataObject, manaSlot);

                    if (!active && hasCharges)
                    {
                        const int debounceMs = CombatSystem.FlaskDebounceMs;

                        if ((now - this.combatSystem.LastManaFlaskAt).TotalMilliseconds >= debounceMs)
                        {
                            this.combatSystem.LastManaFlaskAt = now;
                            if (this.coopGamepad.IsLeaderConnected)
                            {
                                this.coopGamepad.PressLeaderFlask(false, debounceMs);
                            }
                            else
                            {
                                BotInput.FastPressKey(this.Settings.ManaFlaskKey);
                            }
                        }
                    }
                }
            }
            else
            {
                this.combatSystem.TickAutoFlasks(player, currentArea.ServerDataObject, this.Settings, vitals.HpPercent, vitals.ManaPercent, this.coopGamepad);
            }

            // 2. Instant Emergency Low-HP SelfBuffGuard Skills
            this.combatSystem.TickEmergencyLowHpSkills(player, this.Settings, vitals, this.coopGamepad, currentArea);
        }

        private void RenderOverlay(WorldData? world, Entity? player, AreaInstance? currentArea)
        {
            var draw = ImGui.GetForegroundDrawList();

            // Status Badge top-left
            string badgeText = this.Settings.IsRunning
                ? $"[AutoExile 2: {this.activeMode.Name} (RUNNING)] {this.activeMode.CurrentState} · {this.activeMode.CurrentAction}"
                : $"[AutoExile 2: {this.activeMode.Name} (PAUSED)] Press {this.Settings.ToggleKey} to Start";

            uint badgeBg = this.Settings.IsRunning ? ImGuiHelper.Color(20, 80, 40, 220) : ImGuiHelper.Color(40, 40, 40, 220);
            uint badgeTextCol = this.Settings.IsRunning ? ImGuiHelper.Color(100, 255, 140, 255) : ImGuiHelper.Color(200, 200, 200, 255);

            var pos = new Vector2(25, 45);
            var size = ImGui.CalcTextSize(badgeText) + new Vector2(16, 10);

            draw.AddRectFilled(pos, pos + size, badgeBg, 6f);
            draw.AddRect(pos, pos + size, ImGuiHelper.Color(80, 80, 80, 255), 6f);
            draw.AddText(pos + new Vector2(8, 5), badgeTextCol, badgeText);

            // Delegate in-game visual overlay to active mode (rendered running or paused)
            if (world != null && currentArea != null && player != null)
            {
                Vector2 pGrid = Vector2.Zero;
                if (player.TryGetComponent<Render>(out var pR))
                {
                    pGrid = new Vector2(pR.GridPosition.X, pR.GridPosition.Y);
                }

                if (this.renderCtx == null)
                {
                    this.renderCtx = new BotContext
                    {
                        Area = currentArea,
                        World = world,
                        Player = player,
                        PlayerGrid = pGrid,
                        DeltaTime = 0f,
                        Settings = this.Settings,
                        Combat = this.combatSystem,
                        Exploration = this.explorationMap,
                        ThreatMap = this.threatMap,
                        Perf = this.perf,
                        Runtime = this.runtime,
                        Recorder = this.recorder,
                        CoopGamepad = this.coopGamepad,
                    };
                }
                else
                {
                    this.renderCtx.Area = currentArea;
                    this.renderCtx.World = world;
                    this.renderCtx.Player = player;
                    this.renderCtx.PlayerGrid = pGrid;
                    this.renderCtx.Settings = this.Settings;
                }

                this.activeMode.Render(this.renderCtx);

                // Render Real-time Distance & Range Circles (3D Terrain projection)
                RangeVisualizer.Render(draw, this.renderCtx, this.activeMode);
            }
        }

        private IEnumerator<Wait> BotLogicCoroutine()
        {
            while (true)
            {
                // 0. Update active runtime accounting
                this.runtime.Tick(this.Settings.IsRunning);

                // 0.1 Edge detection: if bot was stopped, reset virtual gamepad inputs to neutral (keep gamepads connected!)
                if (!this.Settings.IsRunning && this.lastIsRunning)
                {
                    this.combatSystem.StopAllChannels();
                    BotInput.ReleaseAllMovementKeys(this.Settings);
                    this.coopGamepad.ResetAllInputs();
                }
                this.lastIsRunning = this.Settings.IsRunning;

                if (!this.Settings.IsRunning)
                {
                    if (this.activeMode is CoopFollowerMode coopPaused && coopPaused.CurrentState != "Standby")
                    {
                        coopPaused.SetStatus("Standby", $"Press {this.Settings.ToggleKey} to Start");
                    }
                    yield return new Wait(0.05d); // 20 Hz low-power check when paused
                    continue;
                }

                Wait waitResult;
                try
                {
                    var inGameState = Core.States.InGameStateObject;
                    var currentArea = inGameState?.CurrentAreaInstance;
                    var currentWorld = inGameState?.CurrentWorldInstance;
                    var player = currentArea?.Player;

                    bool isPlayerValid = player != null && player.IsValid;
                    bool isInGameState = Core.States.GameCurrentState is GameStateTypes.InGameState or GameStateTypes.EscapeState;

                    if ((!isInGameState && !isPlayerValid) || currentWorld == null || currentArea == null || player == null)
                    {
                        BotInput.ReleaseAllMovementKeys(this.Settings);
                        if (this.activeMode is CoopFollowerMode coopWait)
                        {
                            coopWait.SetStatus("[Waiting for Game]", "Waiting for Area / Player to load...");
                        }
                        waitResult = new Wait(0.05d);
                    }
                    else
                    {
                        // 0.2 Auto-connect gamepads whenever bot is running in Follower mode
                        if (this.Settings.Mode == AutoExileMode.Follower)
                        {
                            if (!this.coopGamepad.IsLeaderConnected || !this.coopGamepad.IsFollowerConnected)
                            {
                                if (this.activeMode is CoopFollowerMode coopConnecting)
                                {
                                    coopConnecting.SetStatus("[Connecting Controls]", "Connecting Dual Virtual Controllers (ViGEm)...");
                                }

                                bool ok = this.coopGamepad.EnsureConnected(true, this.Settings.CoopPhysicalPadIndex, true);
                                if (!ok && this.coopGamepad.LastError != null && this.activeMode is CoopFollowerMode coopErr)
                                {
                                    coopErr.SetStatus("[ViGEm Error]", $"ViGEm: {this.coopGamepad.LastError}");
                                }
                            }
                        }

                        if (!player.TryGetComponent<Render>(out var pRender))
                        {
                            if (this.activeMode is CoopFollowerMode coopRender)
                            {
                                coopRender.SetStatus("[Waiting for Render]", "Waiting for Player Model...");
                            }
                            waitResult = new Wait(0.0166d);
                        }
                        else
                        {
                            var playerGrid = new Vector2(pRender.GridPosition.X, pRender.GridPosition.Y);
                            float deltaSec = (float)(DateTime.Now - this.lastTickTime).TotalSeconds;
                            this.lastTickTime = DateTime.Now;

                            int playerHp = 0, playerMaxHp = 0, playerMana = 0, playerMaxMana = 0;
                            bool isAlive = true;
                            if (player.TryGetComponent<Life>(out var pLife))
                            {
                                playerHp = pLife.Health.Current;
                                playerMaxHp = pLife.Health.Total;
                                playerMana = pLife.Mana.Current;
                                playerMaxMana = pLife.Mana.Total;
                                isAlive = playerHp > 0;
                            }

                            // Reuse or allocate cached BotContext
                            if (this.botCtx == null)
                            {
                                this.botCtx = new BotContext
                                {
                                    Area = currentArea,
                                    World = currentWorld,
                                    Player = player,
                                    PlayerGrid = playerGrid,
                                    DeltaTime = deltaSec,
                                    Settings = this.Settings,
                                    Combat = this.combatSystem,
                                    Exploration = this.explorationMap,
                                    ThreatMap = this.threatMap,
                                    Perf = this.perf,
                                    Runtime = this.runtime,
                                    Recorder = this.recorder,
                                    CoopGamepad = this.coopGamepad,
                                    Log = msg => Console.WriteLine($"[AutoExile2] {msg}"),
                                };
                            }
                            else
                            {
                                this.botCtx.Area = currentArea;
                                this.botCtx.World = currentWorld;
                                this.botCtx.Player = player;
                                this.botCtx.PlayerGrid = playerGrid;
                                this.botCtx.DeltaTime = deltaSec;
                                this.botCtx.Settings = this.Settings;
                            }

                            // Switch active mode if settings changed
                            this.CheckModeSwitch(this.botCtx);

                            // Record this frame into rolling flight recorder buffer (BotRecorder)
                            this.recorder.RecordTick(
                                currentArea,
                                currentWorld.AreaDetails.Name ?? currentArea.AreaHash ?? "UnknownArea",
                                playerGrid,
                                playerHp,
                                playerMaxHp,
                                playerMana,
                                playerMaxMana,
                                isAlive,
                                this.activeMode.Name,
                                this.activeMode.CurrentState,
                                this.activeMode.CurrentAction,
                                this.combatSystem,
                                this.explorationMap,
                                this.activeMode.CurrentNavPath,
                                this.activeMode.CurrentWaypointIndex,
                                this.activeMode.CurrentDestination,
                                0f,
                                deltaSec);

                            // Delegate execution to the active mode! (MapFarm, Follower, Boss, Idle)
                            this.activeMode.Tick(this.botCtx);
                            waitResult = new Wait(0.0166d);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AutoExile2] BotLogicCoroutine exception: {ex.Message}");
                    if (this.activeMode is CoopFollowerMode coopErr)
                    {
                        coopErr.SetStatus("[Bot Error]", $"Error: {ex.Message}");
                    }
                    waitResult = new Wait(0.05d);
                }

                // Yield ~60 Hz tick rate (16.6ms)
                yield return waitResult;
            }
        }

        private static string ParseCharacterClass(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "Player";
            string clean = path;
            int idx = clean.LastIndexOf('/');
            if (idx >= 0 && idx < clean.Length - 1) clean = clean.Substring(idx + 1);
            return clean switch
            {
                "Str" => "Warrior / Marauder",
                "Dex" => "Ranger / Huntress",
                "Int" => "Sorceress / Witch",
                "StrDex" => "Mercenary / Duelist",
                "StrInt" => "Monk / Templar",
                "DexInt" => "Shadow",
                _ => clean
            };
        }
    }
}
