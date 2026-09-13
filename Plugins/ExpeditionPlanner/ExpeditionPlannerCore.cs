namespace ExpeditionPlanner
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Numerics;
    using System.Text.Json;
    using Coroutine;
    using ImGuiNET;
    using TEHhub;
    using TEHhub.CoroutineEvents;
    using TEHhub.Offsets.Natives;
    using TEHhub.Plugin;
    using TEHhub.RemoteObjects.Components;
    using TEHhub.RemoteObjects.States.InGameStateObjects;

    public sealed class ExpeditionPlannerCore : PCore<ExpeditionPlannerSettings>
    {
        private const string DetonatorPath = "Metadata/MiscellaneousObjects/Expedition/ExpeditionDetonator";
        private const string ExplosivePath = "Metadata/MiscellaneousObjects/Expedition/ExpeditionExplosive";
        private const string ConnectorPolePath = "Metadata/MiscellaneousObjects/Expedition/ExpeditionConnectorPole";
        private const string FusePath = "Metadata/MiscellaneousObjects/Expedition/ExpeditionExplosiveFuse";
        private const string MarkerPath = "Metadata/MiscellaneousObjects/Expedition/ExpeditionMarker";
        private const string RemnantPath = "Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter";
        private const string SentinelPath = "Metadata/Monsters/LeagueExpeditionNew/Sentinels/KalguurDrone";
        private const string ControllerPath = "Metadata/Monsters/LeagueExpeditionNew/RuneEncounterController";
        private const string BossPath = "Metadata/Monsters/Quadrilla/QuadrillaBossSTANDALONEExpedition";

        private string SettingPathname => this.PluginConfigPath("settings.json");

        private ActiveCoroutine? onAreaChange;
        private string currentAreaHash = string.Empty;
        private DateTime nextScanUtc = DateTime.MinValue;

        // Cached snapshot state
        private Vector3 detonatorGrid = Vector3.Zero;
        private Vector3 detonatorWorld = Vector3.Zero;
        private bool hasDetonator = false;
        private readonly List<PlacedBombInfo> placedBombs = new();
        private readonly List<ExpeditionTarget> activeTargets = new();
        private RouteEvaluation currentRoute = new();

        public override void OnEnable(bool isGameOpened)
        {
            if (File.Exists(this.SettingPathname))
            {
                try
                {
                    var content = File.ReadAllText(this.SettingPathname);
                    this.Settings = JsonSerializer.Deserialize<ExpeditionPlannerSettings>(content) ?? new ExpeditionPlannerSettings();
                }
                catch
                {
                    this.Settings = new ExpeditionPlannerSettings();
                }
            }

            this.onAreaChange = CoroutineHandler.Start(this.OnAreaChangeCoroutine());
        }

        public override void OnDisable()
        {
            this.onAreaChange?.Cancel();
            this.onAreaChange = null;
            this.ResetState();
        }

        public override void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(this.SettingPathname) ?? string.Empty);
                var json = JsonSerializer.Serialize(this.Settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(this.SettingPathname, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ExpeditionPlanner] Failed to save settings: {ex.Message}");
            }
        }

        private IEnumerator<Wait> OnAreaChangeCoroutine()
        {
            while (true)
            {
                yield return new Wait(RemoteEvents.AreaChanged);
                this.ResetState();
            }
        }

        private static StdTuple3D<float> ToStdTuple(Vector3 v) => new() { X = v.X, Y = v.Y, Z = v.Z };

        private void ResetState()
        {
            this.currentAreaHash = string.Empty;
            this.hasDetonator = false;
            this.detonatorGrid = Vector3.Zero;
            this.detonatorWorld = Vector3.Zero;
            this.placedBombs.Clear();
            this.activeTargets.Clear();
            this.currentRoute = new RouteEvaluation();
        }

        public override void DrawUI()
        {
            var game = Core.States.InGameStateObject;
            if (game == null || game.Address == IntPtr.Zero) return;
            var area = game.CurrentAreaInstance;
            if (area == null || area.Address == IntPtr.Zero) return;

            // Area transition detection
            if (this.currentAreaHash != area.AreaHash)
            {
                this.ResetState();
                this.currentAreaHash = area.AreaHash ?? string.Empty;
            }

            // Periodic entity snapshot refresh (every 150 ms)
            if (DateTime.UtcNow >= this.nextScanUtc)
            {
                this.nextScanUtc = DateTime.UtcNow.AddMilliseconds(150);
                this.RefreshSnapshot(area);
            }

            // Draw overlay only when an active Expedition encounter exists
            if (!this.hasDetonator && this.activeTargets.Count == 0 && this.placedBombs.Count == 0)
            {
                return;
            }

            var world = game.CurrentWorldInstance;
            if (world == null || world.Address == IntPtr.Zero) return;

            var drawList = ImGui.GetBackgroundDrawList();

            // 1. Draw Placed Bombs badges (solid cyan)
            for (int i = 0; i < this.placedBombs.Count; i++)
            {
                var bomb = this.placedBombs[i];
                var sPos = world.WorldToScreen(ToStdTuple(bomb.WorldPosition), bomb.WorldPosition.Z);
                if (sPos.X > 0 && sPos.Y > 0)
                {
                    drawList.AddCircleFilled(sPos, this.Settings.BadgeRadius * 0.8f, 0xDD00CCCC);
                    drawList.AddCircle(sPos, this.Settings.BadgeRadius * 0.8f, 0xFFFFFFFF, 0, 2f);
                    var text = $"P{i + 1}";
                    var textSize = ImGui.CalcTextSize(text);
                    drawList.AddText(sPos - (textSize * 0.5f), 0xFFFFFFFF, text);
                }
            }

            // 2. Draw Recommended Placement Badges (1 -> 2 -> 3)
            if (this.Settings.ShowBadges && this.currentRoute.Placements.Count > 0)
            {
                // Draw connector line from active anchor (detonator or last placed bomb) to first recommended placement
                var firstP = this.currentRoute.Placements[0];
                var firstScreen = world.WorldToScreen(ToStdTuple(firstP.WorldPosition), firstP.WorldPosition.Z);
                if (firstScreen.X > 0 && firstScreen.Y > 0)
                {
                    Vector3? anchorPos = null;
                    if (this.placedBombs.Count > 0)
                    {
                        anchorPos = this.placedBombs[^1].WorldPosition;
                    }
                    else if (this.detonatorWorld.LengthSquared() > 1f)
                    {
                        anchorPos = this.detonatorWorld;
                    }

                    if (anchorPos.HasValue)
                    {
                        var aScreen = world.WorldToScreen(ToStdTuple(anchorPos.Value), anchorPos.Value.Z);
                        if (aScreen.X > 0 && aScreen.Y > 0)
                        {
                            uint lineColor = firstP.IsObstructed ? 0xDD3333FF : 0xDD00E5FF;
                            drawList.AddLine(aScreen, firstScreen, lineColor, firstP.IsObstructed ? 3.0f : 2.5f);
                        }
                    }
                }

                for (int i = 0; i < this.currentRoute.Placements.Count; i++)
                {
                    var p = this.currentRoute.Placements[i];
                    var sPos = world.WorldToScreen(ToStdTuple(p.WorldPosition), p.WorldPosition.Z);
                    if (sPos.X <= 0 || sPos.Y <= 0) continue;

                    // Blast radius circle on ground (subtle gold ring)
                    if (this.Settings.ShowBlastRadius)
                    {
                        drawList.AddCircle(sPos, this.Settings.BlastRadiusGrid * 2.2f, 0x77FFCC00, 32, 1.5f);
                    }

                    // Badge circle (vibrant gold with dark core, or red if obstructed/unsafe)
                    uint badgeColor = p.IsObstructed ? 0xFF3333DD : (this.currentRoute.IsUnsafe ? 0xFF3388DD : 0xFF00AAFF);
                    drawList.AddCircleFilled(sPos, this.Settings.BadgeRadius, 0xCC111111);
                    drawList.AddCircleFilled(sPos, this.Settings.BadgeRadius * 0.85f, badgeColor);
                    drawList.AddCircle(sPos, this.Settings.BadgeRadius, 0xFFFFFFFF, 0, 2.0f);

                    var badgeText = $"{p.Step}";
                    var bSize = ImGui.CalcTextSize(badgeText);
                    drawList.AddText(sPos - (bSize * 0.5f), 0xFF000000, badgeText);

                    // Connector line to next recommended bomb
                    if (i < this.currentRoute.Placements.Count - 1)
                    {
                        var nextP = this.currentRoute.Placements[i + 1];
                        var nextScreen = world.WorldToScreen(ToStdTuple(nextP.WorldPosition), nextP.WorldPosition.Z);
                        if (nextScreen.X > 0 && nextScreen.Y > 0)
                        {
                            uint nextLineColor = nextP.IsObstructed ? 0xDD3333FF : 0xAAFFCC00;
                            drawList.AddLine(sPos, nextScreen, nextLineColor, nextP.IsObstructed ? 3.0f : 2.5f);
                        }
                    }

                    // Floating recommendation card for Remnant pillars
                    var remnantTarget = p.CoveredTargets.Find(t => t.Kind == TargetKind.RemnantPillar || t.Kind == TargetKind.VerisiumSentinel);
                    if (remnantTarget != null && !string.IsNullOrEmpty(remnantTarget.RecommendedRuneChoice))
                    {
                        var tierTag = remnantTarget.ProliferatedRuneTier switch
                        {
                            RuneTier.Golden => "[GOLDEN OPULENT]",
                            RuneTier.Purple_S => "[S-TIER PURPLE]",
                            RuneTier.Purple_A => "[A-TIER PURPLE]",
                            RuneTier.Purple_B => "[B-TIER PURPLE]",
                            _ => "[BLUE RUNE]"
                        };
                        uint tagColor = remnantTarget.ProliferatedRuneTier switch
                        {
                            RuneTier.Golden => 0xFFFFD700,
                            RuneTier.Purple_S => 0xFFFF55FF,
                            RuneTier.Purple_A => 0xFFDA70D6,
                            RuneTier.Purple_B => 0xFFBA55D3,
                            _ => 0xFF00D2FF
                        };

                        var choiceText = $"{tierTag} {remnantTarget.RecommendedRuneChoice}";
                        var subText = remnantTarget.ProliferationRemaining > 0
                            ? $"Golden Slot [{remnantTarget.AnchorRuneName}] Proliferates: x{remnantTarget.ProliferationRemaining} bombs"
                            : "Final blast in chain (Local only)";

                        if (remnantTarget.NeedsReroll)
                        {
                            subText = "[!] REROLL RECOMMENDED (No Purple/Gold)";
                        }

                        var cSize = ImGui.CalcTextSize(choiceText);
                        var sSize = ImGui.CalcTextSize(subText);
                        var boxW = MathF.Max(cSize.X, sSize.X) + 16f;
                        var boxH = 34f;
                        var boxPos = sPos + new Vector2(-boxW * 0.5f, this.Settings.BadgeRadius + 6f);

                        uint boxBorder = remnantTarget.NeedsReroll ? 0xFF0033FF : (remnantTarget.ProliferatedRuneTier == RuneTier.Golden ? 0xFFFFD700 : 0xFF00E5FF);
                        drawList.AddRectFilled(boxPos, boxPos + new Vector2(boxW, boxH), 0xEE111111, 4f);
                        drawList.AddRect(boxPos, boxPos + new Vector2(boxW, boxH), boxBorder, 4f, 0, 1.5f);
                        drawList.AddText(boxPos + new Vector2(8, 2), tagColor, choiceText);
                        drawList.AddText(boxPos + new Vector2(8, 17), remnantTarget.NeedsReroll ? 0xFF3333FF : 0xFFFFAA00, subText);
                    }
                }
            }

            // 3. Draw Recommendation Summary Card in top viewport
            if (this.Settings.ShowReasonCard)
            {
                ImGui.SetNextWindowPos(new Vector2(20, 220), ImGuiCond.FirstUseEver);
                ImGui.SetNextWindowSize(new Vector2(420, 0), ImGuiCond.Always);
                var flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize;
                if (ImGui.Begin("Expedition Route Advisor###ExpeditionPlannerCard", flags))
                {
                    if (this.currentRoute.Placements.Count > 0)
                    {
                        ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), $"Recommended Route: {string.Join(" -> ", this.currentRoute.Placements.Select(p => $"[{p.Step}]"))}");
                        ImGui.TextDisabled($"Profile: {this.currentRoute.Profile} | Net Score: {this.currentRoute.NetScore:F0} | Targets: {this.activeTargets.Count}");
                        ImGui.Separator();

                        ImGui.TextWrapped(this.currentRoute.Reason);

                        if (this.currentRoute.ProliferatedStack.Count > 0)
                        {
                            ImGui.Separator();
                            ImGui.TextColored(new Vector4(1f, 0.85f, 0.2f, 1f), $"Proliferated Rune Stack ({this.currentRoute.ProliferatedStack.Count}):");
                            ImGui.TextWrapped(string.Join("  ->  ", this.currentRoute.ProliferatedStack.Select(r => $"[{r}]")));
                        }

                        if (this.currentRoute.RerollRemnantCount > 0)
                        {
                            ImGui.Separator();
                            ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f), $"[!] Remnants Needing Reroll: {this.currentRoute.RerollRemnantCount}");
                            ImGui.TextDisabled("Golden slot has no purple/gold runes. Aggressive reroll recommended on 5-slot maps!");
                        }

                        ImGui.Separator();
                        ImGui.TextColored(new Vector4(0.2f, 1f, 0.8f, 1f), "Pillar Rune & Monster Directives:");
                        foreach (var p in this.currentRoute.Placements)
                        {
                            var remnant = p.CoveredTargets.Find(t => t.Kind == TargetKind.RemnantPillar || t.Kind == TargetKind.VerisiumSentinel);
                            if (remnant != null && !string.IsNullOrEmpty(remnant.RecommendedRuneChoice))
                            {
                                var tierColor = remnant.ProliferatedRuneTier switch
                                {
                                    RuneTier.Golden => new Vector4(1f, 0.84f, 0f, 1f),
                                    RuneTier.Purple_S => new Vector4(1f, 0.35f, 1f, 1f),
                                    RuneTier.Purple_A => new Vector4(0.85f, 0.45f, 0.9f, 1f),
                                    RuneTier.Purple_B => new Vector4(0.7f, 0.4f, 0.85f, 1f),
                                    _ => new Vector4(0.3f, 0.7f, 1f, 1f)
                                };

                                ImGui.TextColored(tierColor, $"Step [{p.Step}] Pillar ({remnant.HoleCount}x {remnant.AnchorRuneName}):");
                                ImGui.BulletText($"Choose: {remnant.RecommendedRuneChoice}");
                                if (remnant.NeedsReroll)
                                {
                                    ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), $"   -> [REROLL]: {remnant.RerollReason}");
                                }
                                else if (remnant.ProliferationRemaining > 0)
                                {
                                    ImGui.TextDisabled($"   -> Golden Slot [{remnant.AnchorRuneName}] proliferates to {remnant.ProliferationRemaining} subsequent bombs! (Other slots apply only here)");
                                }
                            }
                            else if (p.CoveredTargets.Count > 0)
                            {
                                ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.8f, 1f), $"Step [{p.Step}] Excavation:");
                                ImGui.BulletText($"Excavates {p.CoveredTargets.Count} targets (inherits all active Remnant buffs)");
                            }
                            else
                            {
                                ImGui.TextColored(new Vector4(0.4f, 0.85f, 1f, 1f), $"Step [{p.Step}] Pathway Bomb:");
                                ImGui.BulletText($"Bridges wire distance towards Remnants ({p.WireDistance:F0} grid)");
                            }
                        }

                        if (this.currentRoute.Warnings.Count > 0)
                        {
                            ImGui.Separator();
                            ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), "[!] Route Warnings:");
                            foreach (var w in this.currentRoute.Warnings)
                            {
                                ImGui.BulletText(w);
                            }
                        }
                    }
                    else
                    {
                        ImGui.TextColored(new Vector4(1f, 0.6f, 0.2f, 1f), "Expedition Encounter Detected");
                        ImGui.TextDisabled($"Active Targets: {this.activeTargets.Count} | Detonator: {(this.hasDetonator ? "Found" : "Player Anchor")}");
                        ImGui.Separator();
                        ImGui.TextWrapped(string.IsNullOrEmpty(this.currentRoute.Reason)
                            ? "Analyzing placement route..."
                            : this.currentRoute.Reason);
                    }

                    ImGui.End();
                }
            }
        }

        private void RefreshSnapshot(AreaInstance area)
        {
            this.placedBombs.Clear();
            this.activeTargets.Clear();

            int bombOrder = 1;
            foreach (var entity in area.AwakeEntities.Values)
            {
                if (entity == null || entity.Address == IntPtr.Zero || string.IsNullOrEmpty(entity.Path)) continue;

                var path = entity.Path;
                if (path == DetonatorPath)
                {
                    if (entity.TryGetComponent<Render>(out var render, false))
                    {
                        this.hasDetonator = true;
                        this.detonatorGrid = new Vector3(render.GridPosition.X, render.GridPosition.Y, render.GridPosition.Z);
                        this.detonatorWorld = new Vector3(render.WorldPosition.X, render.WorldPosition.Y, render.WorldPosition.Z);
                    }
                }
                else if (path == ExplosivePath)
                {
                    if (entity.TryGetComponent<Render>(out var render, false))
                    {
                        this.placedBombs.Add(new PlacedBombInfo
                        {
                            EntityId = entity.Id,
                            GridPosition = new Vector3(render.GridPosition.X, render.GridPosition.Y, render.GridPosition.Z),
                            WorldPosition = new Vector3(render.WorldPosition.X, render.WorldPosition.Y, render.WorldPosition.Z),
                            Order = bombOrder++
                        });
                    }
                }
                else
                {
                    var classified = ClassifyTarget(entity, area);
                    if (classified != null)
                    {
                        this.activeTargets.Add(classified);
                    }
                }
            }

            // If detonator has not been found yet, fallback to player position or first target as anchor
            if (!this.hasDetonator && this.placedBombs.Count == 0)
            {
                var player = area.Player;
                if (player != null && player.TryGetComponent<Render>(out var pRender, false))
                {
                    this.detonatorGrid = new Vector3(pRender.GridPosition.X, pRender.GridPosition.Y, pRender.GridPosition.Z);
                    this.detonatorWorld = new Vector3(pRender.WorldPosition.X, pRender.WorldPosition.Y, pRender.WorldPosition.Z);
                }
                else if (this.activeTargets.Count > 0)
                {
                    this.detonatorGrid = this.activeTargets[0].GridPosition;
                    this.detonatorWorld = this.activeTargets[0].WorldPosition;
                }
            }

            // Solve optimal route for available targets
            this.currentRoute = ExpeditionSolver.Solve(
                this.detonatorGrid,
                this.detonatorWorld,
                this.placedBombs,
                this.activeTargets,
                area,
                this.Settings);
        }

        private static ExpeditionTarget? ClassifyTarget(Entity entity, AreaInstance area)
        {
            if (!entity.TryGetComponent<Render>(out var render, false)) return null;

            var path = entity.Path ?? string.Empty;
            TargetKind kind = TargetKind.Unknown;
            string displayName = string.Empty;

            if (path == MarkerPath)
            {
                var model = entity.TryGetComponent<Animated>(out var anim, false) ? anim.ModelPath ?? string.Empty : string.Empty;
                if (model.Contains("chest", StringComparison.OrdinalIgnoreCase))
                {
                    kind = TargetKind.ChestReward;
                    displayName = "Chest";
                }
                else if (model.Contains("elite", StringComparison.OrdinalIgnoreCase))
                {
                    kind = TargetKind.EliteMonster;
                    displayName = "Elite";
                }
                else
                {
                    kind = TargetKind.NormalMonster;
                    displayName = "Monster";
                }
            }
            else if (path == RemnantPath)
            {
                kind = TargetKind.RemnantPillar;
                displayName = "Remnant";

                int holes = 0;
                string anchor = string.Empty;
                string choice = string.Empty;
                string desc = string.Empty;
                float score = 40f;
                RuneTier tier = RuneTier.Blue_C;
                bool needsReroll = false;
                string rerollReason = string.Empty;

                if (RemnantRuneAdvisor.TryReadMonolith(entity, area.CurrentAreaLevel, out holes, out anchor, out choice, out desc, out score, out tier, out needsReroll, out rerollReason))
                {
                    displayName = $"Remnant [{holes}x {anchor}]";
                }

                var remnantMods = new List<string>();
                if (entity.TryGetComponent<ObjectMagicProperties>(out var remOmp, false))
                {
                    remnantMods.AddRange(remOmp.ModNames);
                }
                if (!string.IsNullOrEmpty(anchor))
                {
                    remnantMods.Add(anchor);
                }

                return new ExpeditionTarget
                {
                    EntityId = entity.Id,
                    Path = path,
                    Kind = kind,
                    DisplayName = displayName,
                    GridPosition = new Vector3(render.GridPosition.X, render.GridPosition.Y, render.GridPosition.Z),
                    WorldPosition = new Vector3(render.WorldPosition.X, render.WorldPosition.Y, render.WorldPosition.Z),
                    TerrainHeight = render.TerrainHeight,
                    ModNames = remnantMods,
                    HoleCount = holes,
                    AnchorRuneName = anchor,
                    RecommendedRuneChoice = choice,
                    RecipeDescription = desc,
                    BaseWeight = score,
                    ProliferatedRuneName = anchor,
                    ProliferatedRuneTier = tier,
                    NeedsReroll = needsReroll,
                    RerollReason = rerollReason
                };
            }
            else if (path.Contains(SentinelPath, StringComparison.OrdinalIgnoreCase))
            {
                kind = TargetKind.VerisiumSentinel;
                displayName = "Verisium Sentinel";
            }
            else if (path.Contains(BossPath, StringComparison.OrdinalIgnoreCase))
            {
                kind = TargetKind.ExpeditionBoss;
                displayName = "Expedition Boss";
            }

            if (kind == TargetKind.Unknown) return null;

            var mods = new List<string>();
            if (entity.TryGetComponent<ObjectMagicProperties>(out var omp, false))
            {
                mods.AddRange(omp.ModNames);
            }

            float baseWeight = kind switch
            {
                TargetKind.ChestReward => 50f,
                TargetKind.EliteMonster => 35f,
                TargetKind.NormalMonster => 10f,
                TargetKind.VerisiumSentinel => 80f,
                TargetKind.ExpeditionBoss => 150f,
                _ => 20f
            };

            return new ExpeditionTarget
            {
                EntityId = entity.Id,
                Path = path,
                Kind = kind,
                DisplayName = displayName,
                GridPosition = new Vector3(render.GridPosition.X, render.GridPosition.Y, render.GridPosition.Z),
                WorldPosition = new Vector3(render.WorldPosition.X, render.WorldPosition.Y, render.WorldPosition.Z),
                TerrainHeight = render.TerrainHeight,
                ModNames = mods,
                BaseWeight = baseWeight
            };
        }

        public override void DrawSettings()
        {
            ImGui.Text("Expedition Planner - Grand Expedition Route Advisor");
            ImGui.Separator();

            int profile = (int)this.Settings.Profile;
            if (ImGui.Combo("Planner Profile", ref profile, "Safe\0Balanced\0Greedy\0\0"))
            {
                this.Settings.Profile = (PlannerProfile)profile;
            }

            var range = this.Settings.MaxPlacementRangeGrid;
            if (ImGui.SliderFloat("Max Placement Range", ref range, 50f, 150f, "%.1f grid"))
            {
                this.Settings.MaxPlacementRangeGrid = range;
            }

            var radius = this.Settings.BlastRadiusGrid;
            if (ImGui.SliderFloat("Blast Radius", ref radius, 25f, 75f, "%.1f grid"))
            {
                this.Settings.BlastRadiusGrid = radius;
            }

            var budget = this.Settings.MaxExplosiveBudget;
            if (ImGui.SliderInt("Max Explosives", ref budget, 2, 10))
            {
                this.Settings.MaxExplosiveBudget = budget;
            }

            ImGui.Separator();
            ImGui.Text("Overlay Elements:");
            var showBadges = this.Settings.ShowBadges;
            if (ImGui.Checkbox("Show Recommended Badges (1 -> 2 -> 3)", ref showBadges))
            {
                this.Settings.ShowBadges = showBadges;
            }

            var showCard = this.Settings.ShowReasonCard;
            if (ImGui.Checkbox("Show Route Summary Card", ref showCard))
            {
                this.Settings.ShowReasonCard = showCard;
            }

            var showBlast = this.Settings.ShowBlastRadius;
            if (ImGui.Checkbox("Show Blast Radius Circles", ref showBlast))
            {
                this.Settings.ShowBlastRadius = showBlast;
            }

            ImGui.Separator();
            ImGui.Text("Reward Weights:");
            var wChest = this.Settings.WeightChest;
            if (ImGui.DragFloat("Chest Weight", ref wChest, 1f, 0f, 200f)) this.Settings.WeightChest = wChest;
            var wElite = this.Settings.WeightElite;
            if (ImGui.DragFloat("Elite Weight", ref wElite, 1f, 0f, 200f)) this.Settings.WeightElite = wElite;
            var wSentinel = this.Settings.WeightSentinel;
            if (ImGui.DragFloat("Sentinel Weight", ref wSentinel, 1f, 0f, 300f)) this.Settings.WeightSentinel = wSentinel;
            var wBoss = this.Settings.WeightBoss;
            if (ImGui.DragFloat("Boss Weight", ref wBoss, 1f, 0f, 500f)) this.Settings.WeightBoss = wBoss;
        }
    }
}
