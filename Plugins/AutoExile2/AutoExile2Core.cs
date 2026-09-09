// <copyright file="AutoExile2Core.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
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

        // Modes architecture (Matching AutoExile 1)
        private readonly Dictionary<AutoExileMode, IBotMode> modes = new();
        private IBotMode activeMode;
        private AutoExileMode lastModeType = AutoExileMode.MapFarm;

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
        }

        /// <inheritdoc/>
        public override void OnDisable()
        {
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
            int manaPct = 100;

            if (player != null && player.TryGetComponent<Life>(out var life) && life.Health.Total > 0)
            {
                hpPct = (int)((float)life.Health.Current / life.Health.Total * 100f);
                manaPct = life.Mana.Total > 0 ? (int)((float)life.Mana.Current / life.Mana.Total * 100f) : 100;
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
                        detectedSkills.Add(new DetectedSkillInfo
                        {
                            Name = skillName,
                            Category = cat,
                            CooldownMs = details.TotalCooldownTimeInMs,
                            CanBeUsed = usable,
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
            int followerMana = 100;
            bool followerFound = false;
            var p2DetectedSkills = new List<DetectedSkillInfo>();
            string leaderPlayerName = "";
            string followerPlayerName = "";

            if (player != null && player.TryGetComponent<Player>(out var pComp))
            {
                leaderPlayerName = pComp.Name;
            }

            if (currentArea?.AwakeEntities != null && player != null)
            {
                foreach (var kvp in currentArea.AwakeEntities)
                {
                    var ent = kvp.Value;
                    if (!ent.IsValid || ent.Address == player.Address) continue;

                    if (ent.EntityType == EntityTypes.Player || (ent.Path != null && ent.Path.StartsWith("Metadata/Characters/")))
                    {
                        followerFound = true;
                        if (ent.TryGetComponent<Player>(out var fPlayerComp))
                        {
                            followerPlayerName = fPlayerComp.Name;
                        }

                        if (ent.TryGetComponent<Life>(out var fLife) && fLife.Health.Total > 0)
                        {
                            followerHp = (int)((float)fLife.Health.Current / fLife.Health.Total * 100f);
                            followerMana = fLife.Mana.Total > 0 ? (int)((float)fLife.Mana.Current / fLife.Mana.Total * 100f) : 100;
                        }

                        if (ent.TryGetComponent<Actor>(out var fActor) && fActor.ActiveSkills != null)
                        {
                            foreach (var (skillName, details) in fActor.ActiveSkills)
                            {
                                if (string.IsNullOrWhiteSpace(skillName)) continue;
                                string cat = SkillClassifier.Classify(skillName);
                                bool usable = fActor.IsSkillUsable.Contains(skillName);
                                p2DetectedSkills.Add(new DetectedSkillInfo
                                {
                                    Name = skillName,
                                    Category = cat,
                                    CooldownMs = details.TotalCooldownTimeInMs,
                                    CanBeUsed = usable,
                                });
                            }
                        }
                        break;
                    }
                }
            }

            return new AutoExileStatusSnapshot
            {
                IsRunning = this.Settings.IsRunning,
                Mode = this.activeMode.Name,
                State = this.activeMode.CurrentState,
                AreaName = currentWorld?.AreaDetails.Name ?? "Unknown",
                ExplorationCoverage = this.explorationMap.Coverage,
                HostileCount = this.combatSystem.NearbyHostileCount,
                PlayerHpPercent = hpPct,
                PlayerManaPercent = manaPct,
                CurrentAction = this.activeMode.CurrentAction,
                ActiveDuration = this.runtime.FormattedDuration,
                DetectedSkills = detectedSkills,
                P2DetectedSkills = p2DetectedSkills,
                LeaderPlayerName = leaderPlayerName,
                FollowerPlayerName = followerPlayerName,
                DetectedBuffs = detectedBuffs,
                DetectedDebuffs = detectedDebuffs,
                ActiveTotems = activeTotems,
                ActiveMinions = activeMinions,
                ActiveTotemNames = activeTotemNames,
                DeployedObjects = deployedObjects,
                FollowerHpPercent = followerHp,
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
            if (this.Settings.Mode != this.lastModeType)
            {
                if (this.modes.TryGetValue(this.Settings.Mode, out var newMode))
                {
                    if (ctx != null)
                    {
                        this.activeMode.OnExit(ctx);
                        this.activeMode = newMode;
                        this.activeMode.OnEnter(ctx);
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

            // 0.2 Auto-connect gamepads whenever bot is running in Follower mode
            if (this.Settings.IsRunning && this.Settings.Mode == AutoExileMode.Follower)
            {
                if (!this.coopGamepad.IsLeaderConnected || !this.coopGamepad.IsFollowerConnected)
                {
                    this.coopGamepad.EnsureConnected(true, this.Settings.CoopPhysicalPadIndex, true);
                }
            }

            // 1. Hotkey edge trigger polling
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

            var inGameState = Core.States.InGameStateObject;
            var currentArea = inGameState?.CurrentAreaInstance;
            var currentWorld = inGameState?.CurrentWorldInstance;
            var player = currentArea?.Player;

            // 2. Draw overlay badge & active mode visuals
            if (this.Settings.ShowOverlay)
            {
                this.RenderOverlay(currentWorld, player);
            }

            if (!this.Settings.IsRunning)
            {
                return;
            }

            if (Core.States.GameCurrentState is not (GameStateTypes.InGameState or GameStateTypes.EscapeState))
            {
                BotInput.ReleaseAllMovementKeys(this.Settings);
                return;
            }

            if (currentWorld == null || currentArea == null || player == null)
            {
                return;
            }

            if (!player.TryGetComponent<Render>(out var pRender))
            {
                return;
            }

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

            // 3. Build context for the active mode
            var ctx = new BotContext
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

            // Switch active mode if settings changed
            this.CheckModeSwitch(ctx);

            // 4. Record this frame into rolling flight recorder buffer (BotRecorder)
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

            // 5. Delegate execution to the active mode! (MapFarm, Follower, Boss, Idle)
            this.activeMode.Tick(ctx);
        }

        private void RenderOverlay(WorldData? world, Entity? player)
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
            if (world != null)
            {
                var inGameState = Core.States.InGameStateObject;
                var currentArea = inGameState?.CurrentAreaInstance;
                if (currentArea != null && player != null)
                {
                    Vector2 pGrid = Vector2.Zero;
                    if (player.TryGetComponent<Render>(out var pR))
                    {
                        pGrid = new Vector2(pR.GridPosition.X, pR.GridPosition.Y);
                    }

                    var ctx = new BotContext
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
                    this.activeMode.Render(ctx);

                    // Render Real-time Distance & Range Circles (3D Terrain projection)
                    RangeVisualizer.Render(draw, ctx, this.activeMode);
                }
            }
        }
    }
}
