namespace ExpeditionPlanner
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Numerics;
    using System.Text.Json;
    using System.Threading.Tasks;
    using ClickableTransparentOverlay.Win32;
    using Coroutine;
    using ImGuiNET;
    using TEHhub;
    using TEHhub.CoroutineEvents;
    using TEHhub.Offsets.Natives;
    using TEHhub.Plugin;
    using TEHhub.RemoteObjects.Components;
    using TEHhub.RemoteObjects.States.InGameStateObjects;
    using TEHhub.Utils;

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
        private readonly HashSet<uint> placedTargetIds = new();
        private readonly Dictionary<uint, ExpeditionTarget> rememberedTargets = new();
        private readonly List<ExpeditionTarget> activeTargets = new();
        private RouteEvaluation currentRoute = new();
        private Task<RouteEvaluation>? pendingRouteCalculation;
        private string pendingRouteAreaHash = string.Empty;

        private string calculationStatusMessage = string.Empty;

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
            this.placedTargetIds.Clear();
            this.rememberedTargets.Clear();
            this.activeTargets.Clear();
            this.currentRoute = new RouteEvaluation();
            this.pendingRouteCalculation = null;
            this.pendingRouteAreaHash = string.Empty;
            this.calculationStatusMessage = string.Empty;
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

            this.ApplyCompletedRouteCalculation(area);

            // Hotkey trigger to calculate route on demand
            if (Utils.IsKeyPressedAndNotTimeout(this.Settings.CalculateHotkey, 250))
            {
                this.CalculateRoute(area, $"Hotkey [{this.Settings.CalculateHotkey}]");
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
                            _ => (remnantTarget.IsAnchorInGoldenSlot ? "[BLUE RUNE]" : "[NON-PROLIFERATING]")
                        };
                        uint tagColor = remnantTarget.ProliferatedRuneTier switch
                        {
                            RuneTier.Golden => 0xFFFFD700,
                            RuneTier.Purple_S => 0xFFFF55FF,
                            RuneTier.Purple_A => 0xFFDA70D6,
                            RuneTier.Purple_B => 0xFFBA55D3,
                            _ => (remnantTarget.IsAnchorInGoldenSlot ? 0xFF00D2FF : 0xFF9E9E9E)
                        };

                        var goldenDisplay = remnantTarget.GoldenSlotIndex >= 0
                            ? $"Golden Slot #{remnantTarget.GoldenSlotIndex + 1}/{remnantTarget.HoleCount}"
                            : "Golden Slot";

                        string choiceText;
                        string subText;

                        if (remnantTarget.IsAnchorInGoldenSlot)
                        {
                            choiceText = $"{tierTag} {remnantTarget.RecommendedRuneChoice}";
                            subText = remnantTarget.ProliferationRemaining > 0
                                ? $"{goldenDisplay}: [{remnantTarget.AnchorRuneName}] (Anchor) -> Proliferates: x{remnantTarget.ProliferationRemaining} bombs"
                                : $"{goldenDisplay}: [{remnantTarget.AnchorRuneName}] (Local only)";
                        }
                        else
                        {
                            var localSlot = remnantTarget.AnchorSlotIndex >= 0 ? $"Slot #{remnantTarget.AnchorSlotIndex + 1}" : "Local";
                            choiceText = $"{tierTag} {remnantTarget.RecommendedRuneChoice} (Anchor: {remnantTarget.AnchorRuneName} @ {localSlot})";

                            var gCandidate = !string.IsNullOrEmpty(remnantTarget.GoldenRuneCandidate) ? remnantTarget.GoldenRuneCandidate : "Blue Rune";
                            if (remnantTarget.CanProliferate)
                            {
                                var gTierName = remnantTarget.ProliferatedRuneTier switch
                                {
                                    RuneTier.Golden => "Opulent",
                                    RuneTier.Purple_S => "S-Tier",
                                    RuneTier.Purple_A => "A-Tier",
                                    _ => "Purple"
                                };
                                subText = remnantTarget.ProliferationRemaining > 0
                                    ? $"{goldenDisplay}: Likely [{gCandidate}] [{gTierName}] -> Proliferates: x{remnantTarget.ProliferationRemaining} bombs"
                                    : $"{goldenDisplay}: Likely [{gCandidate}] [{gTierName}] (Local only)";
                            }
                            else
                            {
                                subText = $"{goldenDisplay}: Likely [{gCandidate}] [Blue Rune - No Proliferation]";
                            }
                        }

                        if (remnantTarget.NeedsReroll)
                        {
                            subText = $"[!] REROLL: {goldenDisplay} is Blue ({remnantTarget.RerollReason})";
                        }

                        if (remnantTarget.WasCoveredByPlacedBomb)
                        {
                            subText = $"[PLACED] {subText}";
                        }

                        var cSize = ImGui.CalcTextSize(choiceText);
                        var sSize = ImGui.CalcTextSize(subText);
                        var padX = 14f;
                        var padY = 8f;
                        var lineGap = 4f;
                        var boxW = MathF.Max(cSize.X, sSize.X) + (padX * 2f);
                        var boxH = (padY * 2f) + cSize.Y + sSize.Y + lineGap;
                        var boxPos = sPos + new Vector2(-boxW * 0.5f, this.Settings.BadgeRadius + 8f);

                        uint boxBorder = remnantTarget.NeedsReroll ? 0xFF0033FF : (remnantTarget.ProliferatedRuneTier == RuneTier.Golden ? 0xFFFFD700 : (remnantTarget.ProliferatedRuneTier == RuneTier.Purple_S ? 0xFFFF55FF : 0xFF00E5FF));
                        uint subTextColor = remnantTarget.NeedsReroll ? 0xFF3333FF : (remnantTarget.CanProliferate ? 0xFF70FF70 : 0xFFFFAA00);
                        drawList.AddRectFilled(boxPos, boxPos + new Vector2(boxW, boxH), 0xF0141414, 6f);
                        drawList.AddRect(boxPos, boxPos + new Vector2(boxW, boxH), boxBorder, 6f, 0, 2.0f);
                        drawList.AddText(boxPos + new Vector2(padX, padY), tagColor, choiceText);
                        drawList.AddText(boxPos + new Vector2(padX, padY + cSize.Y + lineGap), subTextColor, subText);
                    }
                }
            }

            // 2.5 Draw preview badges for discovered Remnants before route is calculated
            if (this.Settings.ShowBadges && this.currentRoute.Placements.Count == 0 && this.activeTargets.Count > 0)
            {
                foreach (var target in this.activeTargets)
                {
                    if (target.Kind != TargetKind.RemnantPillar && target.Kind != TargetKind.VerisiumSentinel) continue;
                    var sPos = world.WorldToScreen(ToStdTuple(target.WorldPosition), target.WorldPosition.Z);
                    if (sPos.X <= 0 || sPos.Y <= 0) continue;

                    uint badgeColor = target.ProliferatedRuneTier switch
                    {
                        RuneTier.Golden => 0xFFFFD700,
                        RuneTier.Purple_S => 0xFFFF55FF,
                        RuneTier.Purple_A => 0xFFDA70D6,
                        RuneTier.Purple_B => 0xFFBA55D3,
                        _ => (target.IsAnchorInGoldenSlot ? 0xFF00D2FF : 0xFF9E9E9E)
                    };

                    drawList.AddCircleFilled(sPos, this.Settings.BadgeRadius * 0.7f, 0xCC111111);
                    drawList.AddCircleFilled(sPos, this.Settings.BadgeRadius * 0.55f, badgeColor);
                    drawList.AddCircle(sPos, this.Settings.BadgeRadius * 0.7f, 0xFFFFFFFF, 0, 1.5f);

                    var goldenSlotText = target.GoldenSlotIndex >= 0 ? $"#{target.GoldenSlotIndex + 1}" : "";
                    var gText = !string.IsNullOrEmpty(target.GoldenRuneCandidate) ? target.GoldenRuneCandidate : target.AnchorRuneName;
                    var lbl = $"{goldenSlotText} {gText}";
                    var lSize = ImGui.CalcTextSize(lbl);
                    var bMin = sPos + new Vector2(-lSize.X * 0.5f - 4, this.Settings.BadgeRadius * 0.7f + 2);
                    var bMax = sPos + new Vector2(lSize.X * 0.5f + 4, this.Settings.BadgeRadius * 0.7f + 4 + lSize.Y);
                    drawList.AddRectFilled(bMin, bMax, 0xDD111111, 3f);
                    drawList.AddText(sPos + new Vector2(-lSize.X * 0.5f, this.Settings.BadgeRadius * 0.7f + 3), badgeColor, lbl);
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
                    int remnantCount = 0, chestCount = 0, monsterCount = 0;
                    foreach (var t in this.activeTargets)
                    {
                        if (t.Kind == TargetKind.RemnantPillar || t.Kind == TargetKind.VerisiumSentinel) remnantCount++;
                        else if (t.Kind == TargetKind.ChestReward) chestCount++;
                        else monsterCount++;
                    }

                    if (ImGui.Button($"★ Calculate Route ({this.Settings.CalculateHotkey})###CalcRouteBtn", new Vector2(230, 26)))
                    {
                        this.CalculateRoute(area, "UI Button");
                    }
                    ImGui.SameLine();
                    if (ImGui.Button("Reset Cache###ResetCacheBtn", new Vector2(100, 26)))
                    {
                        this.ResetState();
                    }
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("Clear remembered map targets and restart scanning.");
                    }

                    ImGui.TextDisabled($"Discovered: {this.activeTargets.Count} targets ({remnantCount} Remnants, {chestCount} Chests, {monsterCount} Monsters)");
                    ImGui.TextDisabled($"Placed: {this.placedBombs.Count} / Budget: {this.Settings.MaxExplosiveBudget} (Remaining: {Math.Max(0, this.Settings.MaxExplosiveBudget - this.placedBombs.Count)})");
                    ImGui.TextDisabled($"Covered by placed explosives: {this.placedTargetIds.Count} targets");
                    if (!string.IsNullOrEmpty(this.calculationStatusMessage))
                    {
                        ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), this.calculationStatusMessage);
                    }
                    ImGui.Separator();

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

                                var goldenSlotNum = remnant.GoldenSlotIndex >= 0 ? $"Golden Slot #{remnant.GoldenSlotIndex + 1}/{remnant.HoleCount}" : $"{remnant.HoleCount} Slots";
                                var goldenRuneDisplay = !string.IsNullOrEmpty(remnant.GoldenRuneCandidate) ? remnant.GoldenRuneCandidate : (remnant.IsAnchorInGoldenSlot ? remnant.AnchorRuneName : "Blue Rune");
                                ImGui.TextColored(tierColor, $"Step [{p.Step}] Pillar ({goldenSlotNum} - Golden: {goldenRuneDisplay}):");
                                ImGui.BulletText($"Recipe: {remnant.RecommendedRuneChoice}");
                                if (remnant.CandidateRuneSequence.Count > 0)
                                {
                                    var seqParts = remnant.CandidateRuneSequence.Select((r, idx) =>
                                        idx == remnant.GoldenSlotIndex ? $"[#{idx + 1}: {r} ★]" :
                                        (idx == remnant.AnchorSlotIndex ? $"[#{idx + 1}: {r} (Anchor)]" : $"[#{idx + 1}: {r}]"));
                                    ImGui.TextDisabled($"   Sockets: {string.Join(" -> ", seqParts)}");
                                }
                                if (remnant.GoldenSlotIndex >= 0)
                                {
                                    ImGui.TextColored(new Vector4(1f, 0.84f, 0f, 1f), $"   [★] Golden Crown: ช่องที่ {remnant.GoldenSlotIndex + 1} ({goldenRuneDisplay})");
                                }
                                if (!remnant.IsAnchorInGoldenSlot && remnant.AnchorSlotIndex >= 0)
                                {
                                    ImGui.TextColored(new Vector4(0.7f, 0.85f, 1f, 1f), $"   [•] รูนหลัก [{remnant.AnchorRuneName}] อยู่ช่องที่ {remnant.AnchorSlotIndex + 1} (บัฟเกิดเฉพาะที่เสานี้)");
                                }
                                if (remnant.NeedsReroll)
                                {
                                    ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), $"   -> [REROLL]: {remnant.RerollReason}");
                                }
                                else if (remnant.ProliferationRemaining > 0 && remnant.CanProliferate)
                                {
                                    ImGui.TextColored(new Vector4(0.44f, 1f, 0.44f, 1f), $"   -> Golden Slot [{remnant.ProliferatedRuneName}] ส่งผลคูณไปยังระเบิดลูกถัดไป {remnant.ProliferationRemaining} ลูก!");
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
                        ImGui.TextColored(new Vector4(1f, 0.84f, 0f, 1f), "Expedition Encounter Discovered");
                        ImGui.TextDisabled($"Detonator: {(this.hasDetonator ? "Found" : "Player Anchor (Run near detonator to lock)")}");
                        ImGui.Separator();
                        ImGui.TextWrapped(this.activeTargets.Count > 0
                            ? $"Discovered {this.activeTargets.Count} targets across the map.\nRun around to harvest all pillars and chests, then press [{this.Settings.CalculateHotkey}] or click the Calculate button above to solve the optimal bomb route."
                            : "Explore the map to locate Expedition Remnants and Chests...");
                    }

                    ImGui.End();
                }
            }
        }

        private void RefreshSnapshot(AreaInstance area)
        {
            var currentBombs = new List<PlacedBombInfo>();
            int bombOrder = 1;
            bool newTargetsFound = false;

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
                        currentBombs.Add(new PlacedBombInfo
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
                    if (!this.rememberedTargets.ContainsKey(entity.Id))
                    {
                        var classified = ClassifyTarget(entity, area);
                        if (classified != null)
                        {
                            this.rememberedTargets[entity.Id] = classified;
                            newTargetsFound = true;
                        }
                    }
                }
            }

            // Update placed bombs. A snapshot never solves a route: calculation is manual
            // so the route stays fixed while the player is placing explosives.
            this.placedBombs.Clear();
            this.placedBombs.AddRange(currentBombs);

            // Synchronize activeTargets list with rememberedTargets values
            if (newTargetsFound || this.activeTargets.Count != this.rememberedTargets.Count)
            {
                this.activeTargets.Clear();
                this.activeTargets.AddRange(this.rememberedTargets.Values);
            }

            this.MarkTargetsCoveredByPlacedBombs();
        }

        private void MarkTargetsCoveredByPlacedBombs()
        {
            this.placedTargetIds.Clear();
            var radiusSquared = this.Settings.BlastRadiusGrid * this.Settings.BlastRadiusGrid;

            foreach (var bomb in this.placedBombs)
            {
                foreach (var target in this.activeTargets)
                {
                    var dx = target.GridPosition.X - bomb.GridPosition.X;
                    var dy = target.GridPosition.Y - bomb.GridPosition.Y;
                    if ((dx * dx) + (dy * dy) <= radiusSquared)
                    {
                        this.placedTargetIds.Add(target.EntityId);
                    }
                }
            }

            foreach (var target in this.activeTargets)
            {
                target.WasCoveredByPlacedBomb = this.placedTargetIds.Contains(target.EntityId);
            }
        }

        private void CalculateRoute(AreaInstance area, string triggerSource = "Manual")
        {
            if (this.pendingRouteCalculation is { IsCompleted: false })
            {
                this.calculationStatusMessage = "Calculating route...";
                return;
            }

            if (this.activeTargets.Count == 0 && this.rememberedTargets.Count == 0)
            {
                this.calculationStatusMessage = "No targets found to calculate";
                return;
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

            var targetSnapshot = this.activeTargets.Select(CloneTarget).ToList();
            var bombSnapshot = this.placedBombs.Select(b => new PlacedBombInfo
            {
                EntityId = b.EntityId, GridPosition = b.GridPosition, WorldPosition = b.WorldPosition, Order = b.Order
            }).ToList();
            var anchorGrid = this.detonatorGrid;
            var anchorWorld = this.detonatorWorld;
            var settings = this.Settings;
            this.pendingRouteAreaHash = area.AreaHash ?? string.Empty;
            this.pendingRouteCalculation = Task.Run(() => ExpeditionSolver.Solve(
                anchorGrid, anchorWorld, bombSnapshot, targetSnapshot, area, settings));
            this.calculationStatusMessage = $"Calculating ({triggerSource})...";
        }

        private void ApplyCompletedRouteCalculation(AreaInstance area)
        {
            if (this.pendingRouteCalculation is not { IsCompleted: true } calculation)
            {
                return;
            }

            this.pendingRouteCalculation = null;
            if (!string.Equals(this.pendingRouteAreaHash, area.AreaHash ?? string.Empty, StringComparison.Ordinal))
            {
                return;
            }

            try
            {
                this.currentRoute = calculation.GetAwaiter().GetResult();
                this.calculationStatusMessage = $"Calculated @ {DateTime.Now:HH:mm:ss} | {this.activeTargets.Count} targets";
            }
            catch (Exception ex)
            {
                this.calculationStatusMessage = $"Route calculation failed: {ex.GetType().Name}";
            }
        }

        private static ExpeditionTarget CloneTarget(ExpeditionTarget source) => new()
        {
            EntityId = source.EntityId, Path = source.Path, Kind = source.Kind, DisplayName = source.DisplayName,
            GridPosition = source.GridPosition, WorldPosition = source.WorldPosition, TerrainHeight = source.TerrainHeight,
            ModNames = new List<string>(source.ModNames), BaseWeight = source.BaseWeight, IsDangerous = source.IsDangerous,
            HoleCount = source.HoleCount, GoldenSlotIndex = source.GoldenSlotIndex, AnchorSlotIndex = source.AnchorSlotIndex,
            AnchorRuneName = source.AnchorRuneName, IsAnchorInGoldenSlot = source.IsAnchorInGoldenSlot,
            RecommendedRuneChoice = source.RecommendedRuneChoice, RecipeDescription = source.RecipeDescription,
            GoldenRuneCandidate = source.GoldenRuneCandidate, CandidateRuneSequence = new List<string>(source.CandidateRuneSequence),
            ProliferatedRuneName = source.ProliferatedRuneName, ProliferatedRuneTier = source.ProliferatedRuneTier,
            NeedsReroll = source.NeedsReroll, RerollReason = source.RerollReason
        };

        private ExpeditionTarget? ClassifyTarget(Entity entity, AreaInstance area)
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
                int goldenSlot = -1;
                int anchorSlot = -1;
                string anchor = string.Empty;
                string goldenRune = string.Empty;
                var candidateRuneSeq = new List<string>();
                string choice = string.Empty;
                string desc = string.Empty;
                float score = 40f;
                RuneTier tier = RuneTier.Blue_C;
                bool isAnchorInGoldenSlot = true;
                bool needsReroll = false;
                string rerollReason = string.Empty;

                if (RemnantRuneAdvisor.TryReadMonolith(entity, area.CurrentAreaLevel, out holes, out goldenSlot, out anchorSlot, out anchor, out goldenRune, out candidateRuneSeq, out choice, out desc, out score, out tier, out isAnchorInGoldenSlot, out needsReroll, out rerollReason))
                {
                    var goldenText = goldenSlot >= 0 ? $"Golden Slot #{goldenSlot + 1}/{holes}" : $"{holes}x";
                    displayName = isAnchorInGoldenSlot
                        ? $"Remnant [{goldenText} {anchor}]"
                        : $"Remnant [{goldenText} {goldenRune} | {anchor} @ #{anchorSlot + 1}]";
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
                    GoldenSlotIndex = goldenSlot,
                    AnchorSlotIndex = anchorSlot,
                    AnchorRuneName = anchor,
                    IsAnchorInGoldenSlot = isAnchorInGoldenSlot,
                    RecommendedRuneChoice = choice,
                    RecipeDescription = desc,
                    BaseWeight = score,
                    GoldenRuneCandidate = goldenRune,
                    CandidateRuneSequence = candidateRuneSeq,
                    ProliferatedRuneName = isAnchorInGoldenSlot ? anchor : goldenRune,
                    ProliferatedRuneTier = tier,
                    NeedsReroll = needsReroll,
                    RerollReason = rerollReason,
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
            if (ImGui.Combo("Planner Profile###PlannerProfileCombo", ref profile, "\u2605 PillarFirst (\u0e40\u0e19\u0e49\u0e19\u0e40\u0e2a\u0e32)\0\u2696 Optimal (\u0e04\u0e27\u0e32\u0e21\u0e04\u0e38\u0e49\u0e21)\0\0"))
            {
                this.Settings.Profile = (PlannerProfile)profile;
                this.currentRoute = new RouteEvaluation();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("PillarFirst = \u0e40\u0e19\u0e49\u0e19\u0e40\u0e2a\u0e32 Remnant/SSS \u0e17\u0e38\u0e01\u0e25\u0e39\u0e01, \u0e44\u0e21\u0e48\u0e19\u0e31\u0e1a\u0e2a\u0e01\u0e2d\u0e23\u0e4c\u0e21\u0e2d\u0e19\u0e2a\u0e40\u0e15\u0e2d\u0e23\u0e4c/\u0e2b\u0e35\u0e1a\n" +
                                 "Optimal = \u0e04\u0e27\u0e32\u0e21\u0e04\u0e38\u0e49\u0e21\u0e40\u0e15\u0e47\u0e21 (\u0e40\u0e2a\u0e32+\u0e2b\u0e35\u0e1a+\u0e21\u0e2d\u0e19\u0e2a\u0e40\u0e15\u0e2d\u0e23\u0e4c) \u0e41\u0e15\u0e48 SSS \u0e44\u0e14\u0e49 bonus \u0e01\u0e32\u0e23\u0e31\u0e19\u0e15\u0e35\u0e0a\u0e19\u0e30\u0e40\u0e2a\u0e21\u0e2d");
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

            var clearance = this.Settings.BombClearanceRadiusGrid;
            if (ImGui.SliderFloat("Bomb Clearance (from Walls/Tent)", ref clearance, 2.0f, 10.0f, "%.1f grid"))
            {
                this.Settings.BombClearanceRadiusGrid = clearance;
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Uses GridWalkableData to guarantee bombs are placed with clearance from the tent, wagon, cliff walls, and rocks.");
            }

            var campRadius = this.Settings.CampExclusionRadiusGrid;
            if (ImGui.SliderFloat("Camp Exclusion Radius", ref campRadius, 5f, 30f, "%.1f grid"))
            {
                this.Settings.CampExclusionRadiusGrid = campRadius;
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Minimum distance from the Detonator / Campsite (tent & wagon) where explosives cannot be placed.");
            }

            var budget = this.Settings.MaxExplosiveBudget;
            if (ImGui.SliderInt("Max Explosives", ref budget, 1, 35))
            {
                this.Settings.MaxExplosiveBudget = budget;
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Map (4)"))
            {
                this.Settings.MaxExplosiveBudget = 4;
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Logbook (20)"))
            {
                this.Settings.MaxExplosiveBudget = 20;
            }

            ImGui.Separator();
            ImGui.Text("Overlay Elements:");
            var showBadges = this.Settings.ShowBadges;
            if (ImGui.Checkbox("Show Recommended Badges (1 -> 2 -> 3 -> 4...)", ref showBadges))
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

            var bRadius = this.Settings.BadgeRadius;
            if (ImGui.SliderFloat("Badge Circle Radius", ref bRadius, 15f, 45f, "%.0f px"))
            {
                this.Settings.BadgeRadius = bRadius;
            }

            ImGui.Separator();
            ImGui.Text("Calculation & Discovery Options:");

            ImGui.Text("Calculation Hotkey:");
            ImGui.SameLine();
            var hotkey = this.Settings.CalculateHotkey;
            if (ImGuiHelper.NonContinuousEnumComboBox("##CalcHotkey", ref hotkey))
            {
                this.Settings.CalculateHotkey = hotkey;
            }

            ImGui.TextDisabled("Route calculation runs only when you press the hotkey or Calculate button.");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("While you place explosives, the current route remains locked.\nPlaced explosives are used only to mark covered targets.");
            }
        }
    }
}
