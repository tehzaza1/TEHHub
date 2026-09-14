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
        private bool needsPillarResequence = false;

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
            this.needsPillarResequence = false;
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

            // Auto-calculate pillar sequence on discovery or when targets change
            if (this.Settings.AutoOrderPillars && this.pendingRouteCalculation == null)
            {
                int remnantCount = this.activeTargets.Count(t => t.Kind == TargetKind.RemnantPillar);
                if (remnantCount > 0 && (this.needsPillarResequence || this.currentRoute.Placements.Count == 0))
                {
                    this.needsPillarResequence = false;
                    this.CalculateRoute(area, "Auto");
                }
            }

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

            // In Pillar Order Advisor mode: render floating badges and directive cards directly on Remnant pillars in 3D world
            if (this.currentRoute.IsPillarOrder)
            {
                this.DrawPillarOrderInWorld(drawList, matrix, winW, winH);
            }
            else
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
                        ? $"Golden #{string.Join(",#", target.GoldenSlotIndices.Select(i => (i + 1).ToString()))}"
                        : (target.GoldenSlotIndex >= 0 ? $"Golden #{target.GoldenSlotIndex + 1}" : "");
                    var gText = !string.IsNullOrEmpty(target.GoldenRuneCandidate) && target.GoldenRuneCandidate != "Unknown"
                        ? target.GoldenRuneCandidate
                        : target.AnchorRuneName;
                    var lbl = string.IsNullOrEmpty(goldenSlotText) ? $"{target.HoleCount}h | {gText}" : $"{goldenSlotText} ({target.HoleCount}h) | {gText}";
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

                    if (ImGui.Button($"Order Pillars ({this.Settings.CalculateHotkey})###CalcRouteBtn", new Vector2(230, 26)))
                    {
                        this.CalculateRoute(area, "UI Button");
                    }
                    if (this.Settings.AutoOrderPillars)
                    {
                        ImGui.SameLine();
                        ImGui.TextColored(new Vector4(0.2f, 1f, 0.5f, 1f), "[AUTO]");
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
                                : (remnant.GoldenSlotIndex >= 0 ? $"Golden Slot #{remnant.GoldenSlotIndex + 1}" : "No Golden Slot");

                            var goldenRuneDisplay = !string.IsNullOrEmpty(remnant.GoldenRuneCandidate)
                                ? remnant.GoldenRuneCandidate
                                : (remnant.IsAnchorInGoldenSlot ? remnant.AnchorRuneName : "-");

                            if (isFinal)
                            {
                                ImGui.Separator();
                                ImGui.TextColored(new Vector4(1f, 0.55f, 0f, 1f), $"* Step [{p.Step}] - FINAL STACK PILLAR *");
                                ImGui.TextColored(new Vector4(1f, 0.85f, 0.2f, 1f), $"   Final Stack Pillar ({remnant.HoleCount} Holes) - Receives all multiplier stacks!");
                            }
                            else
                            {
                                ImGui.TextColored(tierColor, $"Step [{p.Step}] Pillar ({remnant.HoleCount} Holes):");
                            }

                            // 1. Rune info
                            ImGui.BulletText($"Anchor: {remnant.AnchorRuneName} | Golden Rune: {goldenRuneDisplay}");

                            // 2. Sockets and Golden slot
                            ImGui.TextDisabled($"   Holes: {remnant.HoleCount} | {goldenSlotDisplay}");

                            // 3. Recommended choice
                            var runeChoice = !string.IsNullOrEmpty(remnant.RecommendedRuneChoice)
                                ? remnant.RecommendedRuneChoice
                                : (isFinal ? $"Final Stack ({remnant.AnchorRuneName})" : $"Pick {remnant.AnchorRuneName}");
                            ImGui.TextColored(new Vector4(0.3f, 0.9f, 1f, 1f), $"   Choice: {runeChoice}");

                            // 4. Reroll decision
                            if (remnant.NeedsReroll)
                            {
                                ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), $"   [!] MUST REROLL: {remnant.RerollReason}");
                            }
                            else
                            {
                                ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), "   [OK] Keep (Optimal rune in Golden Slot)");
                            }

                            if (remnant.CanProliferate && !isFinal)
                            {
                                ImGui.TextColored(new Vector4(0.44f, 1f, 0.44f, 1f), $"   -> Proliferates [{remnant.ProliferatedRuneName}] to subsequent pillars");
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

        private static bool IsOpulent(ExpeditionTarget? target)
        {
            if (target == null) return false;
            return target.AnchorRuneName.Equals("Opulent", StringComparison.OrdinalIgnoreCase) ||
                   target.GoldenRuneCandidate.Contains("Opulent", StringComparison.OrdinalIgnoreCase) ||
                   target.ProliferatedRuneName.Equals("Opulent", StringComparison.OrdinalIgnoreCase);
        }

        private void DrawPillarOrderInWorld(ImDrawListPtr drawList, Matrix4x4 matrix, float winW, float winH)
        {
            if (!this.Settings.ShowBadges || this.currentRoute.Placements.Count == 0) return;

            for (int i = 0; i < this.currentRoute.Placements.Count; i++)
            {
                var step = this.currentRoute.Placements[i];
                var pillar = step.CoveredTargets.FirstOrDefault(t => t.Kind == TargetKind.RemnantPillar || t.Kind == TargetKind.VerisiumSentinel);
                if (pillar == null) continue;

                if (!ProjectToClipSpace(matrix, pillar.WorldPosition, out var clip) || clip.W < 0.05f) continue;
                var sPos = ClipToScreen(clip, winW, winH);
                if (sPos.X < -250f || sPos.X > winW + 250f || sPos.Y < -250f || sPos.Y > winH + 250f) continue;

                bool isFinal = (step.Step == this.currentRoute.Placements.Count);
                bool isOp = IsOpulent(pillar);
                var tier = pillar.ProliferatedRuneTier;

                uint ringColor = isFinal ? 0xFFFF8800 : (isOp || tier == RuneTier.Golden ? 0xFFFFD700 : (tier switch
                {
                    RuneTier.Purple_S => 0xFFFF55FF,
                    RuneTier.Purple_A => 0xFFDA70D6,
                    RuneTier.Purple_B => 0xFFBA55D3,
                    _ => 0xFF00E5FF
                }));

                // 1. Circular badge on the pillar
                float badgeRadius = isFinal ? 20f : 16f;
                drawList.AddCircleFilled(sPos, badgeRadius, 0xEE141414);
                drawList.AddCircle(sPos, badgeRadius, ringColor, 0, isFinal ? 2.8f : 1.8f);

                string badgeText = step.Step.ToString();
                var bSize = ImGui.CalcTextSize(badgeText);
                drawList.AddText(sPos - (bSize * 0.5f), 0xFFFFFFFF, badgeText);

                // 2. Compact 2-line instruction card directly below the badge (if enabled)
                if (!this.Settings.ShowReasonCard) continue;

                var prolifRune = ExpeditionPillarOrderPlanner.GetProliferatingRuneName(pillar);
                bool isBlue = ExpeditionPillarOrderPlanner.IsBlueRune(prolifRune) || pillar.ProliferatedRuneTier == RuneTier.Blue_C;

                string line1;
                uint headerColor;
                string rerollSuffix = pillar.IsRerolled ? " [R]" : "";
                if (isFinal)
                {
                    line1 = $"[#{step.Step}] FINAL STACK ({pillar.HoleCount}h){rerollSuffix}";
                    headerColor = 0xFFFF8800;
                }
                else if (isOp || tier == RuneTier.Golden)
                {
                    line1 = $"[#{step.Step}] SSS Opulent ({pillar.HoleCount}h){rerollSuffix}";
                    headerColor = 0xFFFFD700;
                }
                else if (tier == RuneTier.Purple_S)
                {
                    line1 = $"[#{step.Step}] S-Tier {prolifRune} ({pillar.HoleCount}h){rerollSuffix}";
                    headerColor = 0xFFFF55FF;
                }
                else if (tier == RuneTier.Purple_A)
                {
                    line1 = $"[#{step.Step}] A-Tier {prolifRune} ({pillar.HoleCount}h){rerollSuffix}";
                    headerColor = 0xFFDA70D6;
                }
                else if (isBlue)
                {
                    var rName = !string.IsNullOrEmpty(prolifRune) ? prolifRune : pillar.AnchorRuneName;
                    line1 = $"[#{step.Step}] Blue {rName} ({pillar.HoleCount}h){rerollSuffix}";
                    headerColor = 0xFF00E5FF;
                }
                else
                {
                    var rName = !string.IsNullOrEmpty(prolifRune) ? prolifRune : pillar.AnchorRuneName;
                    line1 = $"[#{step.Step}] {rName} ({pillar.HoleCount}h){rerollSuffix}";
                    headerColor = ringColor;
                }

                string line2;
                uint statusColor;
                if (pillar.NeedsReroll)
                {
                    line2 = $"[!] REROLL: {pillar.RerollReason}";
                    statusColor = 0xFF3333FF; // Red
                }
                else if (isFinal)
                {
                    line2 = pillar.IsRerolled ? "[OK] Final Stack (Rerolled)" : "[OK] Final Stack (Max Multipliers)";
                    statusColor = 0xFFFF8800; // Orange
                }
                else if (pillar.IsDuplicateProliferation)
                {
                    line2 = $"[OK] Duplicate [{prolifRune}] (No Prolif)";
                    statusColor = 0xFFFFAA00; // Amber
                }
                else if (isBlue)
                {
                    line2 = pillar.IsRerolled ? "[OK] Blue (Rerolled - No Prolif)" : "[OK] Blue (No Prolif)";
                    statusColor = 0xFF88CC88; // Soft Green
                }
                else if (pillar.CanProliferate)
                {
                    line2 = pillar.IsRerolled ? $"[OK] Proliferate: [{prolifRune}] (Rerolled)" : $"[OK] Proliferate: [{prolifRune}]";
                    statusColor = 0xFF55FF55; // Bright Green
                }
                else
                {
                    line2 = pillar.IsRerolled ? "[OK] Already Rerolled" : "[OK] Keep";
                    statusColor = 0xFF55FF55;
                }

                var size1 = ImGui.CalcTextSize(line1);
                var size2 = ImGui.CalcTextSize(line2);

                float padX = 8f;
                float padY = 4f;
                float lineGap = 2f;
                float boxW = MathF.Max(size1.X, size2.X) + (padX * 2f);
                float boxH = (padY * 2f) + size1.Y + size2.Y + lineGap;
                var boxPos = sPos + new Vector2(-boxW * 0.5f, badgeRadius + 4f);

                uint boxBorder = isFinal ? 0xFFFF8800 : (pillar.NeedsReroll ? 0xFF0033FF : (pillar.IsDuplicateProliferation ? 0xFFFFAA00 : ringColor));

                drawList.AddRectFilled(boxPos, boxPos + new Vector2(boxW, boxH), 0xE0101010, 4f);
                drawList.AddRect(boxPos, boxPos + new Vector2(boxW, boxH), boxBorder, 4f, 0, 1.5f);

                float curY = boxPos.Y + padY;
                drawList.AddText(new Vector2(boxPos.X + padX, curY), headerColor, line1);
                curY += size1.Y + lineGap;
                drawList.AddText(new Vector2(boxPos.X + padX, curY), statusColor, line2);
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

            // Draw wire line connecting player's placed bombs on Large Map (Detonator -> Bomb 1 -> Bomb 2 -> ...)
            // User requirement: Placed bomb wire connecting detonator -> bomb 1 -> bomb 2...
            if (this.placedBombs.Count > 0)
            {
                Vector2? prevMapPos = null;

                if (this.hasDetonator && this.detonatorWorld.LengthSquared() > 1f)
                {
                    prevMapPos = ToMap(this.detonatorGrid, this.detonatorWorld.Z);
                    drawList.AddCircleFilled(prevMapPos.Value, 5f, 0xFF00AAFF);
                    drawList.AddCircle(prevMapPos.Value, 5f, 0xFFFFFFFF, 0, 1.2f);
                }

                for (int b = 0; b < this.placedBombs.Count; b++)
                {
                    var bomb = this.placedBombs[b];
                    var curMapPos = ToMap(bomb.GridPosition, bomb.WorldPosition.Z);

                    if (prevMapPos.HasValue)
                    {
                        drawList.AddLine(prevMapPos.Value, curMapPos, 0xEE00E5FF, 2.2f);
                    }

                    // Draw marker dot for each placed bomb with its order
                    float bombRadius = 5f;
                    drawList.AddCircleFilled(curMapPos, bombRadius, 0xFF00FFFF);
                    drawList.AddCircle(curMapPos, bombRadius, 0xFF141414, 0, 1.5f);

                    prevMapPos = curMapPos;
                }
            }

            // Draw badges 1, 2, 3... on pillar positions (Requirement 4)
            for (int i = 0; i < this.currentRoute.Placements.Count; i++)
            {
                var step = this.currentRoute.Placements[i];
                var point = points[i];
                bool isFinal = (i == this.currentRoute.Placements.Count - 1);
                var pillar = step.CoveredTargets.FirstOrDefault();

                bool isOp = IsOpulent(pillar);
                var pTier = pillar?.ProliferatedRuneTier ?? RuneTier.Blue_C;

                uint ringColor = isFinal ? 0xFFFF8800 : (isOp || pTier == RuneTier.Golden ? 0xFFFFD700 : (pTier switch
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

                // Tag label below badge on Large Map
                var prolifRune = pillar != null ? ExpeditionPillarOrderPlanner.GetProliferatingRuneName(pillar) : "";
                bool isBlue = pillar != null && (ExpeditionPillarOrderPlanner.IsBlueRune(prolifRune) || pillar.ProliferatedRuneTier == RuneTier.Blue_C);

                // Tag label below badge on Large Map
                string tag;
                uint tagTextColor;
                if (isFinal)
                {
                    tag = $"FINAL ({pillar?.HoleCount ?? 0}h)";
                    tagTextColor = 0xFFFF8800;
                }
                else if (pillar?.NeedsReroll == true)
                {
                    tag = "REROLL";
                    tagTextColor = 0xFF4444FF;
                }
                else if (pillar?.IsDuplicateProliferation == true)
                {
                    tag = $"[DUP] {prolifRune}";
                    tagTextColor = 0xFFFFAA00;
                }
                else if (isOp || pTier == RuneTier.Golden)
                {
                    tag = "SSS Opulent *";
                    tagTextColor = 0xFFFFD700;
                }
                else if (pTier == RuneTier.Purple_S)
                {
                    tag = $"[S] {prolifRune}";
                    tagTextColor = 0xFFFF55FF;
                }
                else if (pTier == RuneTier.Purple_A)
                {
                    tag = $"[A] {prolifRune}";
                    tagTextColor = 0xFFDA70D6;
                }
                else
                {
                    var gRune = !string.IsNullOrEmpty(prolifRune) ? prolifRune : (pillar?.AnchorRuneName ?? "");
                    tag = $"{pillar?.HoleCount}h {gRune}";
                    tagTextColor = ringColor;
                }

                if (!string.IsNullOrEmpty(tag))
                {
                    var tagSize = ImGui.CalcTextSize(tag);
                    var tagPos = new Vector2(point.X - (tagSize.X * 0.5f), point.Y + radius + 1f);
                    drawList.AddRectFilled(tagPos - new Vector2(2, 0), tagPos + tagSize + new Vector2(2, 0), 0xCC000000, 2f);
                    drawList.AddText(tagPos, tagTextColor, tag);
                }

                // Interactive Hover Tooltip on Large Map
                var mousePos = ImGui.GetMousePos();
                if (Vector2.Distance(mousePos, point) <= radius + 8f && pillar != null)
                {
                    ImGui.BeginTooltip();
                    var rerollDisp = pillar.IsRerolled ? " | [Rerolled: 1]" : " | [Reroll: 0]";
                    ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), $"Step [{step.Step}] Remnant Pillar ({pillar.HoleCount} Holes){rerollDisp}");
                    ImGui.Separator();
                    var gSlotDisp = pillar.GoldenSlotIndices.Count > 1
                        ? $"Golden Slots #{string.Join(", #", pillar.GoldenSlotIndices.Select(x => x + 1))}"
                        : (pillar.GoldenSlotIndex >= 0 ? $"Golden Slot #{pillar.GoldenSlotIndex + 1}" : "No Golden Slot");
                    ImGui.Text($"Slot: {gSlotDisp}");
                    ImGui.Text($"Anchor: {pillar.AnchorRuneName} | Golden: {pillar.GoldenRuneCandidate}");
                    var pickPrompt = !string.IsNullOrEmpty(pillar.GoldenRuneCandidate) && pillar.GoldenRuneCandidate != "Unknown"
                        ? pillar.GoldenRuneCandidate
                        : pillar.AnchorRuneName;
                    ImGui.TextColored(new Vector4(0.3f, 0.9f, 1f, 1f), $">> Choice: {pickPrompt}");
                    if (pillar.NeedsReroll)
                        ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), $"[!] MUST REROLL: {pillar.RerollReason}");
                    else if (isFinal)
                        ImGui.TextColored(new Vector4(1f, 0.55f, 0f, 1f), "* FINAL STACK PILLAR - Max Multipliers *");
                    else if (pillar.IsDuplicateProliferation)
                        ImGui.TextColored(new Vector4(1f, 0.7f, 0.2f, 1f), $"[!] Duplicate [{prolifRune}] (No Proliferation)");
                    else if (isBlue)
                        ImGui.TextColored(new Vector4(0.6f, 0.8f, 0.6f, 1f), "[i] Blue Rune: No Proliferation");
                    else
                        ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), $"[OK] Proliferates [{prolifRune}] to next pillars");
                    ImGui.EndTooltip();
                }
            }
        }

        private void RefreshSnapshot(AreaInstance area)
        {
            var currentBombs = new List<PlacedBombInfo>();
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
                            Order = 0 // Will be set after sorting
                        });
                    }
                }
                else
                {
                    if (!this.rememberedTargets.TryGetValue(entity.Id, out var existing))
                    {
                        var classified = ClassifyTarget(entity, area);
                        if (classified != null)
                        {
                            // If a remnant was replaced at this exact position, clean up the stale entity
                            if (classified.Kind == TargetKind.RemnantPillar)
                            {
                                var stale = this.rememberedTargets
                                    .FirstOrDefault(kv => kv.Value.Kind == TargetKind.RemnantPillar &&
                                                          kv.Key != entity.Id &&
                                                          Vector2.Distance(new Vector2(kv.Value.GridPosition.X, kv.Value.GridPosition.Y),
                                                                           new Vector2(classified.GridPosition.X, classified.GridPosition.Y)) < 8f)
                                    .Key;
                                if (stale != 0)
                                {
                                    this.rememberedTargets.Remove(stale);
                                }
                            }

                            this.rememberedTargets[entity.Id] = classified;
                            newTargetsFound = true;
                        }
                    }
                    else if (existing.Kind == TargetKind.RemnantPillar)
                    {
                        // Always re-check awake Remnant pillars on each scan to detect in-game rerolls!
                        var updated = ClassifyTarget(entity, area);
                        if (updated != null)
                        {
                            bool changed = existing.HoleCount != updated.HoleCount ||
                                           existing.IsRerolled != updated.IsRerolled ||
                                           existing.AnchorRuneName != updated.AnchorRuneName ||
                                           existing.AnchorSlotIndex != updated.AnchorSlotIndex ||
                                           existing.GoldenSlotIndex != updated.GoldenSlotIndex ||
                                           existing.GoldenRuneCandidate != updated.GoldenRuneCandidate ||
                                           existing.ProliferatedRuneTier != updated.ProliferatedRuneTier ||
                                           existing.NeedsReroll != updated.NeedsReroll ||
                                           existing.RerollReason != updated.RerollReason ||
                                           !existing.GoldenSlotIndices.SequenceEqual(updated.GoldenSlotIndices) ||
                                           !existing.ModNames.SequenceEqual(updated.ModNames);

                            if (changed)
                            {
                                this.rememberedTargets[entity.Id] = updated;
                                newTargetsFound = true;
                                this.needsPillarResequence = true;
                            }
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

            // Sort placed bombs by EntityId to maintain true placement order (Bomb 1, 2, 3...)
            currentBombs.Sort((a, b) => a.EntityId.CompareTo(b.EntityId));
            for (int i = 0; i < currentBombs.Count; i++)
            {
                currentBombs[i].Order = i + 1;
            }

            this.placedBombs.Clear();
            this.placedBombs.AddRange(currentBombs);

            // Synchronize activeTargets list with rememberedTargets values
            if (newTargetsFound || this.activeTargets.Count != this.rememberedTargets.Count)
            {
                this.activeTargets.Clear();
                this.activeTargets.AddRange(this.rememberedTargets.Values);
                this.needsPillarResequence = true;

                // If AutoOrderPillars is enabled, re-calculate sequence immediately on reroll/target change
                if (this.Settings.AutoOrderPillars)
                {
                    this.needsPillarResequence = false;
                    this.CalculateRoute(area, "Auto (Remnant Updated)");
                }
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

            var targetSnapshot = this.activeTargets.Select(CloneTarget).ToList();
            var remnantPillars = targetSnapshot.Where(t => t.Kind == TargetKind.RemnantPillar).ToList();
            if (remnantPillars.Count == 0)
            {
                this.calculationStatusMessage = "No Remnant pillars found to sequence";
                return;
            }

            var settings = CloneSettings(this.Settings);
            this.currentRoute = ExpeditionPillarOrderPlanner.Build(targetSnapshot, settings);
            this.calculationStatusMessage = this.currentRoute.Placements.Count > 0
                ? $"Sequenced @ {DateTime.Now:HH:mm:ss} | {this.currentRoute.Placements.Count} pillar(s)"
                : "No Remnant pillars available to sequence.";
        }

        private void ApplyCompletedRouteCalculation(AreaInstance area)
        {
            // Calculation is synchronous and instantaneous
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
            IsRerolled = source.IsRerolled,
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
            ShowPillarOrderOnLargeMap = source.ShowPillarOrderOnLargeMap,
            AutoOrderPillars = source.AutoOrderPillars,
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
                bool isRerolled = false;

                if (RemnantRuneAdvisor.TryReadMonolith(entity, area.CurrentAreaLevel, out holes, out goldenSlot, out goldenSlots, out anchorSlot, out anchor, out goldenRune, out candidateRuneSeq, out choice, out desc, out score, out tier, out isAnchorInGoldenSlot, out needsReroll, out rerollReason, out isRerolled))
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
                    IsRerolled = isRerolled,
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
            if (ImGui.Combo("Planner Profile###PlannerProfileCombo", ref profile, "PillarFirst (Remnants)\0Optimal (All Targets)\0\0"))
            {
                this.Settings.Profile = (PlannerProfile)profile;
                this.currentRoute = new RouteEvaluation();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("PillarFirst = Prioritize Remnant/SSS pillars every bomb.\nOptimal = Balance all targets (remnants, chests, monsters) while guaranteeing SSS.");
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

            var autoOrder = this.Settings.AutoOrderPillars;
            if (ImGui.Checkbox("Auto-order Pillars on Discovery (No Hotkey Needed)", ref autoOrder))
            {
                this.Settings.AutoOrderPillars = autoOrder;
            }

            var showCard = this.Settings.ShowReasonCard;
            if (ImGui.Checkbox("Show In-World Floating Pillar Cards", ref showCard))
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

