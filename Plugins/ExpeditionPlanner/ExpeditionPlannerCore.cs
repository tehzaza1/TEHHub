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
    using TEHhub.Offsets.Objects.States.InGameState;
    using TEHhub.Offsets.Objects.UiElement;
    using TEHhub.Plugin;
    using TEHhub.RemoteObjects.Components;
    using TEHhub.RemoteObjects.States;
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

        private static bool TryGetEntityPositions(Entity entity, out Vector3 gridPos, out Vector3 worldPos)
        {
            gridPos = Vector3.Zero;
            worldPos = Vector3.Zero;
            if (entity == null || entity.Address == IntPtr.Zero) return false;

            if (entity.TryGetComponent<Render>(out var render, false))
            {
                gridPos = new Vector3(render.GridPosition.X, render.GridPosition.Y, render.GridPosition.Z);
                worldPos = new Vector3(render.WorldPosition.X, render.WorldPosition.Y, render.WorldPosition.Z);
                return true;
            }

            return false;
        }

        private static bool ProjectToClipSpace(Matrix4x4 m, Vector3 p, out Vector4 clip)
        {
            clip = new Vector4(
                (m.M11 * p.X) + (m.M21 * p.Y) + (m.M31 * p.Z) + m.M41,
                (m.M12 * p.X) + (m.M22 * p.Y) + (m.M32 * p.Z) + m.M42,
                (m.M13 * p.X) + (m.M23 * p.Y) + (m.M33 * p.Z) + m.M43,
                (m.M14 * p.X) + (m.M24 * p.Y) + (m.M34 * p.Z) + m.M44);
            return clip.W > 0.001f;
        }

        private static Vector2 ClipToScreen(Vector4 clip, float winW, float winH)
        {
            float ndcX = clip.X / clip.W;
            float ndcY = clip.Y / clip.W;
            return new Vector2(
                (ndcX + 1.0f) * (winW * 0.5f),
                (1.0f - ndcY) * (winH * 0.5f));
        }

        /// <summary>
        /// Draws a circle that lies flat on the ground by projecting world-space perimeter
        /// points through the view matrix. This produces the correct perspective ellipse
        /// instead of a flat screen-space disc that appears to float in the air.
        /// </summary>
        private static void DrawGroundCircle(
            ImDrawListPtr drawList,
            Matrix4x4 matrix,
            float winW,
            float winH,
            Vector3 centerWorld,
            float worldRadius,
            uint color,
            int segments = 48,
            float thickness = 1.5f)
        {
            Vector2 prev = default;
            bool hasPrev = false;
            float step = 2f * MathF.PI / segments;

            for (int i = 0; i <= segments; i++)
            {
                float a = i * step;
                var pt = new Vector3(
                    centerWorld.X + MathF.Cos(a) * worldRadius,
                    centerWorld.Y + MathF.Sin(a) * worldRadius,
                    centerWorld.Z);  // same Z = stays on ground

                if (!ProjectToClipSpace(matrix, pt, out var clip) || clip.W < 0.05f)
                {
                    hasPrev = false;
                    continue;
                }

                var cur = ClipToScreen(clip, winW, winH);
                if (hasPrev)
                {
                    drawList.AddLine(prev, cur, color, thickness);
                }
                prev = cur;
                hasPrev = true;
            }
        }

        private static bool ClipLine2D(ref Vector2 p0, ref Vector2 p1, float xMin, float yMin, float xMax, float yMax)
        {
            float dx = p1.X - p0.X;
            float dy = p1.Y - p0.Y;
            float t0 = 0.0f;
            float t1 = 1.0f;

            if (!ClipTest(-dx, -(xMin - p0.X), ref t0, ref t1)) return false;
            if (!ClipTest(dx, xMax - p0.X, ref t0, ref t1)) return false;
            if (!ClipTest(-dy, -(yMin - p0.Y), ref t0, ref t1)) return false;
            if (!ClipTest(dy, yMax - p0.Y, ref t0, ref t1)) return false;

            if (t1 < 1.0f)
            {
                p1 = new Vector2(p0.X + (t1 * dx), p0.Y + (t1 * dy));
            }
            if (t0 > 0.0f)
            {
                p0 = new Vector2(p0.X + (t0 * dx), p0.Y + (t0 * dy));
            }
            return true;
        }

        private static bool ClipTest(float p, float q, ref float t0, ref float t1)
        {
            if (p < 0.0f)
            {
                float r = q / p;
                if (r > t1) return false;
                if (r > t0) t0 = r;
            }
            else if (p > 0.0f)
            {
                float r = q / p;
                if (r < t0) return false;
                if (r < t1) t1 = r;
            }
            else if (q < 0.0f)
            {
                return false;
            }
            return true;
        }

        private static void DrawSafe3DLine(
            ImDrawListPtr drawList,
            Matrix4x4 matrix,
            float winW,
            float winH,
            Vector3 worldA,
            Vector3 worldB,
            uint color,
            float thickness)
        {
            ProjectToClipSpace(matrix, worldA, out var clipA);
            ProjectToClipSpace(matrix, worldB, out var clipB);

            const float nearW = 0.05f;
            bool aFront = clipA.W >= nearW;
            bool bFront = clipB.W >= nearW;

            if (!aFront && !bFront) return;

            if (!aFront)
            {
                float t = (nearW - clipA.W) / (clipB.W - clipA.W);
                clipA = Vector4.Lerp(clipA, clipB, Math.Clamp(t, 0f, 1f));
            }
            else if (!bFront)
            {
                float t = (nearW - clipB.W) / (clipA.W - clipB.W);
                clipB = Vector4.Lerp(clipB, clipA, Math.Clamp(t, 0f, 1f));
            }

            var screenA = ClipToScreen(clipA, winW, winH);
            var screenB = ClipToScreen(clipB, winW, winH);

            const float margin = 60f;
            if (ClipLine2D(ref screenA, ref screenB, -margin, -margin, winW + margin, winH + margin))
            {
                drawList.AddLine(screenA, screenB, color, thickness);
            }
        }

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
            if (this.currentRoute.IsPillarOrder && this.Settings.ShowPillarOrderOnLargeMap)
            {
                this.DrawPillarOrderOnLargeMap(game, area, drawList);
            }

            var reader = Core.Process.Handle;
            var worldData = reader.ReadMemory<WorldDataOffset>(world.Address);
            var matrix = worldData.CameraStructurePtr.WorldToScreenMatrix;
            float winW = Core.Process.WindowArea.Width;
            float winH = Core.Process.WindowArea.Height;

            // 1. Draw 3D wire, blast circles, and bomb dots ONLY when NOT in Pillar Order Advisor mode (Requirement 6)
            if (!this.currentRoute.IsPillarOrder)
            {
                // Connector lines between placed bombs
                if (this.hasDetonator && this.detonatorWorld.LengthSquared() > 1f && this.placedBombs.Count > 0)
                {
                    DrawSafe3DLine(drawList, matrix, winW, winH, this.detonatorWorld, this.placedBombs[0].WorldPosition, 0xDD00CCCC, 2.5f);
                }
                for (int i = 0; i < this.placedBombs.Count - 1; i++)
                {
                    DrawSafe3DLine(drawList, matrix, winW, winH, this.placedBombs[i].WorldPosition, this.placedBombs[i + 1].WorldPosition, 0xDD00CCCC, 2.5f);
                }

                // Connector lines from anchor to recommended route
                if (this.Settings.ShowBadges && this.currentRoute.Placements.Count > 0)
                {
                    Vector3? anchorPos = this.placedBombs.Count > 0
                        ? this.placedBombs[^1].WorldPosition
                        : (this.hasDetonator && this.detonatorWorld.LengthSquared() > 1f ? this.detonatorWorld : null);

                    if (anchorPos.HasValue)
                    {
                        var firstP = this.currentRoute.Placements[0];
                        uint lineColor = firstP.IsObstructed ? 0xDD3333FF : 0xDD00E5FF;
                        DrawSafe3DLine(drawList, matrix, winW, winH, anchorPos.Value, firstP.WorldPosition, lineColor, firstP.IsObstructed ? 3.0f : 2.5f);
                    }

                    for (int i = 0; i < this.currentRoute.Placements.Count - 1; i++)
                    {
                        var p = this.currentRoute.Placements[i];
                        var nextP = this.currentRoute.Placements[i + 1];
                        uint lineColor = nextP.IsObstructed ? 0xDD3333FF : 0xAAFFCC00;
                        DrawSafe3DLine(drawList, matrix, winW, winH, p.WorldPosition, nextP.WorldPosition, lineColor, nextP.IsObstructed ? 3.0f : 2.5f);
                    }
                }

                // 2. Draw Placed Bombs badges (solid cyan) + blast radius ring
                float gridToWorldScale = 10.87f;
                var refTarget = this.activeTargets.FirstOrDefault(t => t.GridPosition.X > 1f && t.WorldPosition.X > 1f);
                if (refTarget != null && refTarget.GridPosition.X > 0.5f)
                {
                    gridToWorldScale = refTarget.WorldPosition.X / refTarget.GridPosition.X;
                }
                for (int i = 0; i < this.placedBombs.Count; i++)
                {
                    var bomb = this.placedBombs[i];
                    if (!ProjectToClipSpace(matrix, bomb.WorldPosition, out var clip) || clip.W < 0.05f) continue;
                    var sPos = ClipToScreen(clip, winW, winH);
                    if (sPos.X < -this.Settings.BadgeRadius || sPos.X > winW + this.Settings.BadgeRadius ||
                        sPos.Y < -this.Settings.BadgeRadius || sPos.Y > winH + this.Settings.BadgeRadius) continue;

                    if (this.Settings.ShowBlastRadius)
                    {
                        float worldRadius = this.Settings.BlastRadiusGrid * gridToWorldScale;
                        DrawGroundCircle(drawList, matrix, winW, winH, bomb.WorldPosition, worldRadius, 0x5500CCCC);
                    }

                    drawList.AddCircleFilled(sPos, this.Settings.BadgeRadius * 0.8f, 0xDD00CCCC);
                    drawList.AddCircle(sPos, this.Settings.BadgeRadius * 0.8f, 0xFFFFFFFF, 0, 2f);
                    var text = $"P{i + 1}";
                    var textSize = ImGui.CalcTextSize(text);
                    drawList.AddText(sPos - (textSize * 0.5f), 0xFFFFFFFF, text);
                }

                // 3. Draw Recommended Placement Badges (1 -> 2 -> 3)
                if (this.Settings.ShowBadges && this.currentRoute.Placements.Count > 0)
                {
                    for (int i = 0; i < this.currentRoute.Placements.Count; i++)
                    {
                        var p = this.currentRoute.Placements[i];
                        if (!ProjectToClipSpace(matrix, p.WorldPosition, out var clip) || clip.W < 0.05f) continue;
                        var sPos = ClipToScreen(clip, winW, winH);
                        if (sPos.X < -this.Settings.BadgeRadius || sPos.X > winW + this.Settings.BadgeRadius ||
                            sPos.Y < -this.Settings.BadgeRadius || sPos.Y > winH + this.Settings.BadgeRadius) continue;

                        if (this.Settings.ShowBlastRadius)
                        {
                            float worldRadius = this.Settings.BlastRadiusGrid * gridToWorldScale;
                            DrawGroundCircle(drawList, matrix, winW, winH, p.WorldPosition, worldRadius, 0x77FFCC00);
                        }

                        uint badgeColor = p.IsObstructed ? 0xFF3333DD : (this.currentRoute.IsUnsafe ? 0xFF3388DD : 0xFF00AAFF);
                        drawList.AddCircleFilled(sPos, this.Settings.BadgeRadius, 0xCC111111);
                        drawList.AddCircleFilled(sPos, this.Settings.BadgeRadius * 0.85f, badgeColor);
                        drawList.AddCircle(sPos, this.Settings.BadgeRadius, 0xFFFFFFFF, 0, 2.0f);

                        var badgeText = $"{p.Step}";
                        var bSize = ImGui.CalcTextSize(badgeText);
                        drawList.AddText(sPos - (bSize * 0.5f), 0xFF000000, badgeText);

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

                            var goldenDisplay = remnantTarget.GoldenSlotIndices.Count > 1
                                ? $"Golden Slots {string.Join(", ", remnantTarget.GoldenSlotIndices.Select(i => $"#{i + 1}"))}/{remnantTarget.HoleCount}"
                                : (remnantTarget.GoldenSlotIndex >= 0
                                    ? $"Golden Slot #{remnantTarget.GoldenSlotIndex + 1}/{remnantTarget.HoleCount}"
                                    : "Golden Slot");

                            string choiceText;
                            string subText;

                            if (remnantTarget.IsAnchorInGoldenSlot && remnantTarget.GoldenSlotIndices.Count <= 1)
                            {
                                choiceText = $"{tierTag} {remnantTarget.RecommendedRuneChoice}";
                                subText = remnantTarget.ProliferationRemaining > 0
                                    ? $"{goldenDisplay}: [{remnantTarget.AnchorRuneName}] (Anchor) -> Proliferates: x{remnantTarget.ProliferationRemaining} bombs"
                                    : $"{goldenDisplay}: [{remnantTarget.AnchorRuneName}] (Local only)";
                            }
                            else
                            {
                                choiceText = $"{tierTag} {remnantTarget.RecommendedRuneChoice} (Anchor: {remnantTarget.AnchorRuneName})";
                                var gCandidate = !string.IsNullOrEmpty(remnantTarget.GoldenRuneCandidate) ? remnantTarget.GoldenRuneCandidate : "Blue Rune";
                                if (remnantTarget.CanProliferate)
                                {
                                    subText = remnantTarget.ProliferationRemaining > 0
                                        ? $"{goldenDisplay}: [{gCandidate}] -> Proliferates: x{remnantTarget.ProliferationRemaining} bombs"
                                        : $"{goldenDisplay}: [{gCandidate}] (Local only)";
                                }
                                else
                                {
                                    subText = $"{goldenDisplay}: [{gCandidate}] (No Proliferation)";
                                }
                            }

                            if (remnantTarget.NeedsReroll)
                            {
                                subText = $"[!] REROLL: {remnantTarget.RerollReason}";
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
            }

            // 4. Draw preview badges for discovered Remnants before route is calculated
            if (this.Settings.ShowBadges && this.currentRoute.Placements.Count == 0 && this.activeTargets.Count > 0)
            {
                foreach (var target in this.activeTargets)
                {
                    if (target.Kind != TargetKind.RemnantPillar && target.Kind != TargetKind.VerisiumSentinel) continue;
                    if (!ProjectToClipSpace(matrix, target.WorldPosition, out var clip) || clip.W < 0.05f) continue;
                    var sPos = ClipToScreen(clip, winW, winH);
                    if (sPos.X < -this.Settings.BadgeRadius || sPos.X > winW + this.Settings.BadgeRadius ||
                        sPos.Y < -this.Settings.BadgeRadius || sPos.Y > winH + this.Settings.BadgeRadius) continue;

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

                    var goldenSlotText = target.GoldenSlotIndices.Count > 1
                        ? $"#{string.Join(",#", target.GoldenSlotIndices.Select(i => (i + 1).ToString()))}"
                        : (target.GoldenSlotIndex >= 0 ? $"#{target.GoldenSlotIndex + 1}" : "");
                    var gText = !string.IsNullOrEmpty(target.GoldenRuneCandidate) ? target.GoldenRuneCandidate : target.AnchorRuneName;
                    var lbl = $"{goldenSlotText} {gText}";
                    var lSize = ImGui.CalcTextSize(lbl);
                    var bMin = sPos + new Vector2(-lSize.X * 0.5f - 4, this.Settings.BadgeRadius * 0.7f + 2);
                    var bMax = sPos + new Vector2(lSize.X * 0.5f + 4, this.Settings.BadgeRadius * 0.7f + 4 + lSize.Y);
                    drawList.AddRectFilled(bMin, bMax, 0xDD111111, 3f);
                    drawList.AddText(sPos + new Vector2(-lSize.X * 0.5f, this.Settings.BadgeRadius * 0.7f + 3), badgeColor, lbl);
                }
            }

            // 3. Draw Pillar Order Advisor Window
            if (this.Settings.ShowReasonCard)
            {
                ImGui.SetNextWindowPos(new Vector2(20, 220), ImGuiCond.FirstUseEver);
                ImGui.SetNextWindowSize(new Vector2(520, 680), ImGuiCond.FirstUseEver);
                var flags = ImGuiWindowFlags.NoCollapse;
                var advisorOpen = this.Settings.ShowReasonCard;
                if (ImGui.Begin("Expedition Pillar Order Advisor###ExpeditionPlannerCard", ref advisorOpen, flags))
                {
                    int remnantCount = this.activeTargets.Count(t => t.Kind == TargetKind.RemnantPillar);
                    int maxHoles = remnantCount > 0 ? this.activeTargets.Where(t => t.Kind == TargetKind.RemnantPillar).Max(t => t.HoleCount) : 0;

                    if (ImGui.Button($"★ Order Pillars ({this.Settings.CalculateHotkey})###CalcRouteBtn", new Vector2(230, 26)))
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

                    ImGui.TextDisabled($"Discovered: {remnantCount} Remnant Pillars (Max holes: {maxHoles})");

                    if (!string.IsNullOrEmpty(this.calculationStatusMessage))
                    {
                        ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), this.calculationStatusMessage);
                    }
                    ImGui.Separator();

                    if (this.currentRoute.Placements.Count > 0)
                    {
                        ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), $"Pillar Sequence: {string.Join(" -> ", this.currentRoute.Placements.Select(p => $"[{p.Step}]"))}");
                        ImGui.TextDisabled($"Pillars Sequenced: {this.currentRoute.Placements.Count} | Max Holes on Map: {maxHoles}");
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
                            ImGui.TextDisabled("Golden slot has no high-tier purple/gold runes. Reroll recommended before detonating!");
                        }

                        ImGui.Separator();
                        ImGui.TextColored(new Vector4(0.2f, 1f, 0.8f, 1f), "Pillar Sequence Directives:");
                        for (int i = 0; i < this.currentRoute.Placements.Count; i++)
                        {
                            var p = this.currentRoute.Placements[i];
                            var remnant = p.CoveredTargets.FirstOrDefault(t => t.Kind == TargetKind.RemnantPillar || t.Kind == TargetKind.VerisiumSentinel);
                            if (remnant == null) continue;

                            bool isFinal = (p.Step == this.currentRoute.Placements.Count);
                            var tierColor = remnant.ProliferatedRuneTier switch
                            {
                                RuneTier.Golden => new Vector4(1f, 0.84f, 0f, 1f),
                                RuneTier.Purple_S => new Vector4(1f, 0.35f, 1f, 1f),
                                RuneTier.Purple_A => new Vector4(0.85f, 0.45f, 0.9f, 1f),
                                RuneTier.Purple_B => new Vector4(0.7f, 0.4f, 0.85f, 1f),
                                _ => new Vector4(0.3f, 0.7f, 1f, 1f)
                            };

                            var goldenSlotDisplay = remnant.GoldenSlotIndices.Count > 1
                                ? $"Golden Slots #{string.Join(", #", remnant.GoldenSlotIndices.Select(x => x + 1))}"
                                : (remnant.GoldenSlotIndex >= 0 ? $"Golden Slot #{remnant.GoldenSlotIndex + 1}" : "ไม่มี Golden Slot");

                            var goldenRuneDisplay = !string.IsNullOrEmpty(remnant.GoldenRuneCandidate)
                                ? remnant.GoldenRuneCandidate
                                : (remnant.IsAnchorInGoldenSlot ? remnant.AnchorRuneName : "-");

                            if (isFinal)
                            {
                                ImGui.Separator();
                                ImGui.TextColored(new Vector4(1f, 0.55f, 0f, 1f), $"★ Step [{p.Step}] - FINAL STACK PILLAR ★");
                                ImGui.TextColored(new Vector4(1f, 0.85f, 0.2f, 1f), $"   “Final stack pillar” เสาสุดท้าย ({remnant.HoleCount} รู) - รับผลคูณจากเสาก่อนหน้าทั้งหมด!");
                            }
                            else
                            {
                                ImGui.TextColored(tierColor, $"Step [{p.Step}] Pillar ({remnant.HoleCount} รู):");
                            }

                            // 1. ชื่อ rune / Golden rune
                            ImGui.BulletText($"รูนหลัก: {remnant.AnchorRuneName} | Golden Rune: {goldenRuneDisplay}");

                            // 2. จำนวนรูและ Golden slot
                            ImGui.TextDisabled($"   จำนวนรู: {remnant.HoleCount} รู | {goldenSlotDisplay}");

                            // 3. เลือก rune อะไร
                            var runeChoice = !string.IsNullOrEmpty(remnant.RecommendedRuneChoice)
                                ? remnant.RecommendedRuneChoice
                                : (isFinal ? $"ใช้เสาสุดท้าย ({remnant.AnchorRuneName})" : $"เลือก {remnant.AnchorRuneName}");
                            ImGui.TextColored(new Vector4(0.3f, 0.9f, 1f, 1f), $"   เลือกรูน: {runeChoice}");

                            // 4. ต้อง reroll หรือไม่ พร้อมเหตุผล
                            if (remnant.NeedsReroll)
                            {
                                ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), $"   [!] ต้อง REROLL: {remnant.RerollReason}");
                            }
                            else
                            {
                                ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), "   [✓] ไม่ต้อง Reroll (Golden Slot ได้รูนเหมาะสมแล้ว)");
                            }

                            if (remnant.CanProliferate && !isFinal)
                            {
                                ImGui.TextColored(new Vector4(0.44f, 1f, 0.44f, 1f), $"   -> ส่งผลคูณ [{remnant.ProliferatedRuneName}] ไปยังเสาลำดับถัดไป");
                            }
                        }

                        if (this.currentRoute.Warnings.Count > 0)
                        {
                            ImGui.Separator();
                            ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), "[!] Advisor Notes:");
                            foreach (var w in this.currentRoute.Warnings)
                            {
                                ImGui.BulletText(w);
                            }
                        }
                    }
                    else
                    {
                        ImGui.TextColored(new Vector4(1f, 0.84f, 0f, 1f), "Expedition Pillar Advisor");
                        ImGui.Separator();
                        ImGui.TextWrapped(remnantCount > 0
                            ? $"Discovered {remnantCount} Remnant Pillars across the map (Max Holes = {maxHoles}).\nPress [{this.Settings.CalculateHotkey}] or click the Order Pillars button above to compute the optimal detonation sequence."
                            : "Explore the map to locate Expedition Remnant Pillars...");
                    }

                }
                // ImGui requires End for every Begin, including the collapsed/closed case.
                // Leaving it inside the true branch triggers "Missing EndChild" and aborts TEHhub.
                ImGui.End();
            }
        }

        private void DrawPillarOrderOnLargeMap(InGameState game, AreaInstance area, ImDrawListPtr drawList)
        {
            var map = game.GameUi.LargeMap;
            if (map.Address == IntPtr.Zero || !map.IsVisible || this.currentRoute.Placements.Count == 0) return;
            var worldMap = game.GameUi.WorldMapPanel;
            if (worldMap.Address != IntPtr.Zero && worldMap.IsVisible) return;

            var player = area.Player;
            if (player == null || !player.TryGetComponent<Render>(out var playerRender, false)) return;

            var center = map.Center + map.Shift + map.DefaultShift;
            const float LargeMapXBias = 0.6f;
            const float LargeMapYBias = 0.3f;
            center.X += LargeMapXBias;
            center.Y += LargeMapYBias;

            var baseRes = UiElementBaseFuncs.BaseResolution;
            var baseDiag = Math.Sqrt((baseRes.X * baseRes.X) + (baseRes.Y * baseRes.Y));
            var mapHeight = map.Address != IntPtr.Zero && map.Size.Y > 0 ? map.Size.Y : Core.Process.WindowArea.Size.Height;
            var largeMapDiagonalLength = baseDiag * mapHeight / baseRes.Y;

            const float LargeMapScaleBaseline = 0.187812f;
            var largeMapModifiedZoom = Math.Max(0.001f, (float)(map.Zoom * LargeMapScaleBaseline));

            const double CameraAngle = 38.7 * Math.PI / 180;
            float mapScale = 240f / largeMapModifiedZoom;
            float cos = (float)(largeMapDiagonalLength * Math.Cos(CameraAngle) / mapScale);
            float sin = (float)(largeMapDiagonalLength * Math.Sin(CameraAngle) / mapScale);

            Vector2 ToMap(Vector3 grid, float terrainHeight)
            {
                var delta = new Vector2(grid.X - playerRender.GridPosition.X, grid.Y - playerRender.GridPosition.Y);
                float deltaZ = (terrainHeight - playerRender.TerrainHeight) / 10.86957f;
                return center + new Vector2((delta.X - delta.Y) * cos, (deltaZ - (delta.X + delta.Y)) * sin);
            }

            var points = new List<Vector2>(this.currentRoute.Placements.Count);
            for (int i = 0; i < this.currentRoute.Placements.Count; i++)
            {
                var step = this.currentRoute.Placements[i];
                points.Add(ToMap(step.GridPosition, step.TerrainHeight));
            }

            // Draw thin line connecting pillar order only (Requirement 4)
            for (int i = 0; i < points.Count - 1; i++)
            {
                drawList.AddLine(points[i], points[i + 1], 0xCCFFD700, 1.8f);
            }

            // Draw badges 1, 2, 3... on pillar positions (Requirement 4)
            for (int i = 0; i < this.currentRoute.Placements.Count; i++)
            {
                var step = this.currentRoute.Placements[i];
                var point = points[i];
                bool isFinal = (i == this.currentRoute.Placements.Count - 1);
                var pillar = step.CoveredTargets.FirstOrDefault();

                bool isOpulent = pillar != null && (
                    pillar.AnchorRuneName.Equals("Opulent", StringComparison.OrdinalIgnoreCase) ||
                    pillar.GoldenRuneCandidate.Equals("Opulent", StringComparison.OrdinalIgnoreCase) ||
                    pillar.ProliferatedRuneName.Equals("Opulent", StringComparison.OrdinalIgnoreCase));

                uint ringColor = isFinal ? 0xFFFF8800 : (isOpulent ? 0xFFFFD700 : (pillar?.ProliferatedRuneTier switch
                {
                    RuneTier.Purple_S => 0xFFFF55FF,
                    RuneTier.Purple_A => 0xFFDA70D6,
                    RuneTier.Purple_B => 0xFFBA55D3,
                    _ => 0xFF00E5FF
                }));

                float radius = isFinal ? 16f : 14f;
                drawList.AddCircleFilled(point, radius, 0xEE141414);
                drawList.AddCircle(point, radius, ringColor, 0, isFinal ? 2.5f : 1.8f);

                var label = step.Step.ToString();
                var size = ImGui.CalcTextSize(label);
                drawList.AddText(point - (size * 0.5f), 0xFFFFFFFF, label);

                if (isFinal)
                {
                    var tag = "FINAL";
                    var tagSize = ImGui.CalcTextSize(tag);
                    var tagPos = new Vector2(point.X - (tagSize.X * 0.5f), point.Y + radius + 1f);
                    drawList.AddRectFilled(tagPos - new Vector2(2, 0), tagPos + tagSize + new Vector2(2, 0), 0xCC000000, 2f);
                    drawList.AddText(tagPos, 0xFFFF8800, tag);
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
                    if (TryGetEntityPositions(entity, out var grid, out var world))
                    {
                        this.hasDetonator = true;
                        this.detonatorGrid = grid;
                        this.detonatorWorld = world;
                    }
                }
                else if (path == ExplosivePath)
                {
                    if (TryGetEntityPositions(entity, out var grid, out var world))
                    {
                        currentBombs.Add(new PlacedBombInfo
                        {
                            EntityId = entity.Id,
                            GridPosition = grid,
                            WorldPosition = world,
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

            // If detonator not yet discovered from awake entities, search sleeping entities across the area immediately!
            if (!this.hasDetonator)
            {
                area.ScanSleepingEntities(
                    path => path == DetonatorPath,
                    (key, entity) =>
                    {
                        if (TryGetEntityPositions(entity, out var grid, out var worldPos))
                        {
                            this.hasDetonator = true;
                            this.detonatorGrid = grid;
                            this.detonatorWorld = worldPos;
                        }
                    });
            }

            // In Logbooks or areas without a standalone ExpeditionDetonator entity,
            // fall back to the entrance transition portal or campsite as the detonator anchor.
            if (!this.hasDetonator)
            {
                foreach (var entity in area.AwakeEntities.Values)
                {
                    if (entity == null || entity.Address == IntPtr.Zero || string.IsNullOrEmpty(entity.Path)) continue;
                    if (entity.Path is "Metadata/MiscellaneousObjects/AreaTransitionToggleableReverse" or
                        "Metadata/MiscellaneousObjects/Expedition/ExpeditionCampsite" or
                        "Metadata/Terrain/Gallows/Leagues/Expedition/Logbook_Prairie/Objects/Monolith")
                    {
                        if (TryGetEntityPositions(entity, out var grid, out var world))
                        {
                            this.hasDetonator = true;
                            this.detonatorGrid = grid;
                            this.detonatorWorld = world;
                            break;
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

        private void CalculateRoute(AreaInstance? area, string triggerSource = "Manual")
        {
            if (area == null) return;
            if (this.pendingRouteCalculation is { IsCompleted: false })
            {
                this.calculationStatusMessage = "Sequencing pillar order...";
                return;
            }

            var targetSnapshot = this.activeTargets.Select(CloneTarget).ToList();
            var remnantPillars = targetSnapshot.Where(t => t.Kind == TargetKind.RemnantPillar).ToList();
            if (remnantPillars.Count == 0)
            {
                this.calculationStatusMessage = "No Remnant pillars found to sequence";
                return;
            }

            var settings = CloneSettings(this.Settings);
            this.pendingRouteAreaHash = area.AreaHash ?? string.Empty;
            this.pendingRouteCalculation = Task.Run(() => ExpeditionPillarOrderPlanner.Build(targetSnapshot, settings));
            this.calculationStatusMessage = $"Sequencing pillars ({triggerSource})...";
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
                this.calculationStatusMessage = this.currentRoute.Placements.Count > 0
                    ? $"Sequenced @ {DateTime.Now:HH:mm:ss} | {this.currentRoute.Placements.Count} pillar(s)"
                    : "No Remnant pillars available to sequence.";
            }
            catch (Exception ex)
            {
                this.calculationStatusMessage = $"Pillar sequencing failed: {ex.GetType().Name}";
            }
        }

        private static ExpeditionTarget CloneTarget(ExpeditionTarget source) => new()
        {
            EntityId = source.EntityId, Path = source.Path, Kind = source.Kind, DisplayName = source.DisplayName,
            GridPosition = source.GridPosition, WorldPosition = source.WorldPosition, TerrainHeight = source.TerrainHeight,
            ModNames = new List<string>(source.ModNames), BaseWeight = source.BaseWeight, IsDangerous = source.IsDangerous,
            HoleCount = source.HoleCount, GoldenSlotIndex = source.GoldenSlotIndex,
            GoldenSlotIndices = new List<int>(source.GoldenSlotIndices),
            AnchorSlotIndex = source.AnchorSlotIndex,
            AnchorRuneName = source.AnchorRuneName, IsAnchorInGoldenSlot = source.IsAnchorInGoldenSlot,
            RecommendedRuneChoice = source.RecommendedRuneChoice, RecipeDescription = source.RecipeDescription,
            GoldenRuneCandidate = source.GoldenRuneCandidate, CandidateRuneSequence = new List<string>(source.CandidateRuneSequence),
            ProliferatedRuneName = source.ProliferatedRuneName, ProliferatedRuneTier = source.ProliferatedRuneTier,
            NeedsReroll = source.NeedsReroll, RerollReason = source.RerollReason,
            WasCoveredByPlacedBomb = source.WasCoveredByPlacedBomb,
            IsDuplicateProliferation = source.IsDuplicateProliferation,
            ProliferationRemaining = source.ProliferationRemaining
        };

        private static ExpeditionPlannerSettings CloneSettings(ExpeditionPlannerSettings source) => new()
        {
            Profile = source.Profile,
            CalculateHotkey = source.CalculateHotkey,
            MaxPlacementRangeGrid = source.MaxPlacementRangeGrid,
            BlastRadiusGrid = source.BlastRadiusGrid,
            BombClearanceRadiusGrid = source.BombClearanceRadiusGrid,
            CampExclusionRadiusGrid = source.CampExclusionRadiusGrid,
            MaxExplosiveBudget = source.MaxExplosiveBudget,
            WeightChest = source.WeightChest, WeightElite = source.WeightElite, WeightMonster = source.WeightMonster,
            WeightRemnant = source.WeightRemnant, WeightSentinel = source.WeightSentinel, WeightBoss = source.WeightBoss,
            RuneWeights = new Dictionary<string, float>(source.RuneWeights),
            NeverTakeRunes = new HashSet<string>(source.NeverTakeRunes),
            ShowBadges = source.ShowBadges, ShowReasonCard = source.ShowReasonCard,
            ShowBlastRadius = source.ShowBlastRadius, ShowTargetScores = source.ShowTargetScores,
            BadgeRadius = source.BadgeRadius
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
                var goldenSlots = new List<int>();
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

                if (RemnantRuneAdvisor.TryReadMonolith(entity, area.CurrentAreaLevel, out holes, out goldenSlot, out goldenSlots, out anchorSlot, out anchor, out goldenRune, out candidateRuneSeq, out choice, out desc, out score, out tier, out isAnchorInGoldenSlot, out needsReroll, out rerollReason))
                {
                    var goldenText = goldenSlots.Count > 1
                        ? $"Golden Slots {string.Join(",", goldenSlots.Select(i => $"#{i + 1}"))}/{holes}"
                        : (goldenSlot >= 0 ? $"Golden Slot #{goldenSlot + 1}/{holes}" : $"{holes}x");

                    displayName = isAnchorInGoldenSlot && goldenSlots.Count <= 1
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
                    GoldenSlotIndices = goldenSlots,
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

            var showLargeMap = this.Settings.ShowPillarOrderOnLargeMap;
            if (ImGui.Checkbox("Show Pillar Order on Large Map (1, 2, 3...)", ref showLargeMap))
            {
                this.Settings.ShowPillarOrderOnLargeMap = showLargeMap;
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

