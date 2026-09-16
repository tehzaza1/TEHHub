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
    using TEHhub.Offsets.Objects.UiElement;
    using TEHhub.Plugin;
    using TEHhub.RemoteObjects.Components;
    using TEHhub.RemoteObjects.States.InGameStateObjects;
    using TEHhub.RemoteObjects.UiElement;
    using TEHhub.Utils;

    public sealed class ExpeditionPathOptimizerCore : PCore<ExpeditionPathOptimizerSettings>
    {
        private string SettingPathname => this.PluginConfigPath("settings.json");
        private readonly PathPlannerRunner runner = new();
        private ActiveCoroutine? onAreaChangeCoroutine;

        private Vector3 detonatorPos = Vector3.Zero;
        private readonly List<(Vector3 WorldPos, IExpeditionRelic Relic)> discoveredRelics = new();
        private readonly List<(Vector3 WorldPos, IExpeditionLoot Loot)> discoveredLoot = new();

        public override void OnEnable(bool isAutoEnabling)
        {
            this.LoadSettings();
            this.onAreaChangeCoroutine = CoroutineHandler.Start(this.OnAreaChange(), "[ExpeditionPathOptimizer] Area Change");
        }

        public override void OnDisable()
        {
            this.onAreaChangeCoroutine?.Cancel();
            this.onAreaChangeCoroutine = null;
            this.runner.Stop();
            this.discoveredRelics.Clear();
            this.discoveredLoot.Clear();
            this.detonatorPos = Vector3.Zero;
        }

        private IEnumerator<Wait> OnAreaChange()
        {
            while (true)
            {
                yield return new Wait(RemoteEvents.AreaChanged);
                this.runner.Stop();
                this.discoveredRelics.Clear();
                this.discoveredLoot.Clear();
                this.detonatorPos = Vector3.Zero;

                if (this.Settings.Enable && this.Settings.AutoStartOnAreaChange)
                {
                    yield return new Wait(1.5f);
                    this.ScanAndStartSearch();
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
            ImGui.TextColored(new Vector4(0.2f, 0.8f, 1.0f, 1.0f), "Expedition Path Optimizer Settings (ExileApi Port)");
            ImGui.Separator();
            ImGui.Spacing();

            bool enable = this.Settings.Enable;
            if (ImGui.Checkbox("Enable Optimizer", ref enable))
            {
                this.Settings.Enable = enable;
                this.SaveSettings();
            }

            bool autoStart = this.Settings.AutoStartOnAreaChange;
            if (ImGui.Checkbox("Auto-Start Optimization On Area Load", ref autoStart))
            {
                this.Settings.AutoStartOnAreaChange = autoStart;
                this.SaveSettings();
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.3f, 1.0f), "Optimization Controls");

            if (this.runner.IsRunning)
            {
                if (ImGui.Button("Stop Search", new Vector2(150, 30)))
                {
                    this.runner.Stop();
                }
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.2f, 1.0f, 0.2f, 1.0f), "Optimizing in background...");
            }
            else
            {
                if (ImGui.Button("Start Search", new Vector2(150, 30)))
                {
                    this.ScanAndStartSearch();
                }
                ImGui.SameLine();
                if (this.runner.CurrentBestPath != null)
                {
                    ImGui.TextColored(new Vector4(0.4f, 0.9f, 1.0f, 1.0f), $"Best Score: {this.runner.CurrentBestScore:F1} ({this.runner.CurrentBestPath.PerPointScore.Count} bombs)");
                }
                else
                {
                    ImGui.TextDisabled("Idle");
                }
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.3f, 1.0f), "Visual Overlays");

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
            if (ImGui.Checkbox("Show Suggested Blast Radius Circles", ref showRad))
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
            ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.3f, 1.0f), "Genetic Algorithm Parameters");

            int threads = this.Settings.SearchThreads;
            if (ImGui.SliderInt("Search Threads", ref threads, 1, 12))
            {
                this.Settings.SearchThreads = threads;
                this.SaveSettings();
            }

            float maxTime = this.Settings.MaximumGenerationTimeSeconds;
            if (ImGui.SliderFloat("Max Search Time (sec)", ref maxTime, 1.0f, 20.0f, "%.1f"))
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
        }

        public void ScanAndStartSearch()
        {
            var area = Core.States.InGameStateObject?.CurrentAreaInstance;
            if (area == null) return;

            this.discoveredRelics.Clear();
            this.discoveredLoot.Clear();
            this.detonatorPos = Vector3.Zero;

            foreach (var kvp in area.AwakeEntities)
            {
                var entity = kvp.Value;
                if (entity == null || string.IsNullOrEmpty(entity.Path)) continue;

                if (!entity.TryGetComponent<Render>(out var render) || render == null) continue;
                var pos = new Vector3(render.WorldPosition.X, render.WorldPosition.Y, render.TerrainHeight);
                var path = entity.Path;

                if (path.Contains("ExpeditionDetonator", StringComparison.OrdinalIgnoreCase))
                {
                    this.detonatorPos = pos;
                }
                else if (path.Contains("ExpeditionRelic", StringComparison.OrdinalIgnoreCase))
                {
                    this.discoveredRelics.Add((pos, new ConfigurableRelic(1.5, 0.4, true)));
                }
                else if (path.Contains("ExpeditionMarker", StringComparison.OrdinalIgnoreCase) || path.Contains("ExpeditionChest", StringComparison.OrdinalIgnoreCase))
                {
                    if (path.Contains("Currency", StringComparison.OrdinalIgnoreCase))
                        this.discoveredLoot.Add((pos, new PathPlannerData.Chest(ExpeditionChestType.Currency)));
                    else if (path.Contains("Artifact", StringComparison.OrdinalIgnoreCase))
                        this.discoveredLoot.Add((pos, new PathPlannerData.Chest(ExpeditionChestType.Artifact)));
                    else if (path.Contains("Map", StringComparison.OrdinalIgnoreCase))
                        this.discoveredLoot.Add((pos, new PathPlannerData.Chest(ExpeditionChestType.Map)));
                    else if (path.Contains("Fragment", StringComparison.OrdinalIgnoreCase))
                        this.discoveredLoot.Add((pos, new PathPlannerData.Chest(ExpeditionChestType.Fragment)));
                    else
                        this.discoveredLoot.Add((pos, new PathPlannerData.Chest(ExpeditionChestType.Generic)));
                }
                else if (path.Contains("ExpeditionMonster", StringComparison.OrdinalIgnoreCase) || path.Contains("Runic", StringComparison.OrdinalIgnoreCase))
                {
                    this.discoveredLoot.Add((pos, new RunicMonster()));
                }
            }

            if (this.detonatorPos == Vector3.Zero && this.discoveredRelics.Count > 0)
            {
                this.detonatorPos = this.discoveredRelics[0].WorldPos;
            }

            if (this.detonatorPos == Vector3.Zero) return;

            var config = area.ExpeditionConfig;
            float radiusWorld = config.ExplosionRadiusWorld > 0 ? config.ExplosionRadiusWorld : 300f;
            float rangeWorld = config.PlacementReachWorld > 0 ? config.PlacementReachWorld : 1000f;
            int maxExplosions = config.ExplosiveCount > 0 ? config.ExplosiveCount : 5;

            var envRelics = this.discoveredRelics.Select(r => (new Vector2(r.WorldPos.X, r.WorldPos.Y), r.Relic)).ToList();
            var envLoot = this.discoveredLoot.Select(l => (new Vector2(l.WorldPos.X, l.WorldPos.Y), l.Loot)).ToList();

            var env = new ExpeditionEnvironment(
                envRelics,
                envLoot,
                rangeWorld,
                radiusWorld,
                maxExplosions,
                new Vector2(this.detonatorPos.X, this.detonatorPos.Y),
                p => true, // Walkable
                (new Vector2(-100000, -100000), new Vector2(100000, 100000)),
                config.IsGrandExpedition);

            this.runner.Start(this.Settings, env);
        }

        public override void DrawUI()
        {
            if (!this.Settings.Enable) return;

            var bestDetailed = this.runner.CurrentBestPath;
            if (bestDetailed == null || bestDetailed.PerPointScore == null || bestDetailed.PerPointScore.Count == 0) return;

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
                var gridX = worldPos.X / 10.86957f;
                var gridY = worldPos.Y / 10.86957f;
                var delta = new Vector2(gridX - playerRender.GridPosition.X, gridY - playerRender.GridPosition.Y);
                float deltaZ = (worldPos.Z - playerRender.TerrainHeight) / 10.86957f;
                return mapCenter + new Vector2((delta.X - delta.Y) * cos, (deltaZ - (delta.X + delta.Y)) * sin);
            }

            var dl = ImGui.GetBackgroundDrawList();
            var points = bestDetailed.PerPointScore;
            float radiusWorld = bestDetailed.Environment.ExplosionRadius;
            float zHeight = this.detonatorPos.Z;

            // 1. Draw Connecting Lines
            if (this.Settings.ShowPathLines)
            {
                Vector2 prevScreen = canMapProject
                    ? ToMap(this.detonatorPos)
                    : world.WorldToScreen(new Vector2(this.detonatorPos.X, this.detonatorPos.Y), zHeight);

                for (int i = 0; i < points.Count; i++)
                {
                    var pt = points[i].Point;
                    var curScreen = canMapProject
                        ? ToMap(new Vector3(pt.X, pt.Y, zHeight))
                        : world.WorldToScreen(pt, zHeight);

                    if (curScreen != Vector2.Zero && prevScreen != Vector2.Zero)
                    {
                        dl.AddLine(prevScreen, curScreen, this.Settings.PathLineColor, this.Settings.PathLineWidth);
                    }
                    prevScreen = curScreen;
                }
            }

            // 2. Draw Blast Circles & Badges
            for (int i = 0; i < points.Count; i++)
            {
                var pt = points[i].Point;
                int bombIndex = i + 1;

                if (canMapProject)
                {
                    var mCenter = ToMap(new Vector3(pt.X, pt.Y, zHeight));
                    var mEdge = ToMap(new Vector3(pt.X + radiusWorld, pt.Y, zHeight));
                    float mapR = Vector2.Distance(mCenter, mEdge);

                    if (mCenter != Vector2.Zero && mapR > 1f)
                    {
                        if (this.Settings.ShowBlastRadius)
                        {
                            uint fillAlpha = (uint)(Math.Clamp(this.Settings.BlastRadiusOpacity, 0f, 1f) * 255f);
                            uint fillColor = (fillAlpha << 24) | (this.Settings.ExplosiveColor & 0x00FFFFFFu);
                            dl.AddCircleFilled(mCenter, mapR, fillColor, 32);
                            dl.AddCircle(mCenter, mapR, this.Settings.ExplosiveColor, 32, 2.0f);
                        }

                        // Draw Number Badge
                        dl.AddCircleFilled(mCenter, 10f, this.Settings.BadgeBgColor);
                        dl.AddCircle(mCenter, 10f, 0xFFFFFFFFu, 0, 1.5f);
                        string numStr = bombIndex.ToString();
                        var tSz = ImGui.CalcTextSize(numStr);
                        dl.AddText(new Vector2(mCenter.X - tSz.X * 0.5f, mCenter.Y - tSz.Y * 0.5f), 0xFFFFFFFFu, numStr);
                    }
                }
                else
                {
                    var sPos = world.WorldToScreen(pt, zHeight);
                    if (sPos == Vector2.Zero) continue;

                    if (this.Settings.ShowBlastRadius)
                    {
                        const int segments = 32;
                        Vector2 prevRing = Vector2.Zero;
                        Vector2 firstRing = Vector2.Zero;

                        for (int s = 0; s <= segments; s++)
                        {
                            float angle = (s % segments) * (MathF.PI * 2f / segments);
                            float px = pt.X + MathF.Cos(angle) * radiusWorld;
                            float py = pt.Y + MathF.Sin(angle) * radiusWorld;
                            var ringScreen = world.WorldToScreen(new Vector2(px, py), zHeight);

                            if (ringScreen == Vector2.Zero)
                            {
                                prevRing = Vector2.Zero;
                                continue;
                            }

                            if (s == 0) firstRing = ringScreen;
                            if (prevRing != Vector2.Zero)
                            {
                                dl.AddLine(prevRing, ringScreen, this.Settings.ExplosiveColor, 2.0f);
                            }
                            prevRing = ringScreen;
                        }

                        if (prevRing != Vector2.Zero && firstRing != Vector2.Zero)
                        {
                            dl.AddLine(prevRing, firstRing, this.Settings.ExplosiveColor, 2.0f);
                        }
                    }

                    // Draw Number Badge in World
                    dl.AddCircleFilled(sPos, 14f, this.Settings.BadgeBgColor);
                    dl.AddCircle(sPos, 14f, 0xFFFFFFFFu, 0, 2.0f);
                    string numStr = bombIndex.ToString();
                    var tSz = ImGui.CalcTextSize(numStr);
                    dl.AddText(new Vector2(sPos.X - tSz.X * 0.5f, sPos.Y - tSz.Y * 0.5f), 0xFFFFFFFFu, numStr);
                }
            }
        }
    }
}
