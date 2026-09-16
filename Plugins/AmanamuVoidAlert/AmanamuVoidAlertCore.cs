// <copyright file="AmanamuVoidAlertCore.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AmanamuVoidAlert
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Numerics;
    using System.Text.Json;
    using TEHhub;
    using TEHhub.Plugin;
    using TEHhub.RemoteEnums;
    using TEHhub.RemoteEnums.Entity;
    using TEHhub.RemoteObjects.Components;
    using TEHhub.RemoteObjects.States;
    using TEHhub.RemoteObjects.States.InGameStateObjects;
    using TEHhub.Utils;
    using TEHhub.Offsets.Natives;
    using ImGuiNET;

    /// <summary>
    /// Core class for Amanamu Void Alert plugin.
    /// Tracks Amanamu/Lightless monsters and alerts whether they are inside or outside the void cloud.
    /// </summary>
    public sealed class AmanamuVoidAlertCore : PCore<AmanamuVoidAlertSettings>
    {
        private const string ExpectedMonsterModId = "MonsterAbyssLightlessFaction1";
        private const string ExpectedMonsterModMetadata = "Metadata/Monsters/MonsterMods/LeagueAbyss/LightlessWells";
        private const string BuffPrefixAbyssLightlessWell = "abyss_lightless_well";
        private const string BuffInsideCloud = "abyss_lightless_well_immune";

        private readonly Dictionary<uint, TrackedMonster> tracked = new();
        private readonly Stopwatch stopwatch = Stopwatch.StartNew();

        private string SettingsPath => this.PluginConfigPath("settings.txt");

        private sealed class TrackedMonster
        {
            public uint Id;
            public IntPtr Address;
            public string Path = string.Empty;

            public bool SeenThisFrame;
            public bool InsideCloud;
            public bool HasAnyLightlessBuff;
            public bool HasAmanamuMonsterMod;

            public float Distance;
            public float LastSeenSeconds;
            public float LastLiveEntitySeconds;
            public bool WasRecentlyLive;

            public Vector3 WorldPosition;
            public float ModelBoundsZ = 80f;

            public bool HasLastScreenDirection;
            public Vector2 LastScreenDirection = -Vector2.UnitY;

            public List<string> LastBuffs = new();
            public List<string> LastMonsterMods = new();
        }

        private float SecondsSinceEnable => (float)this.stopwatch.Elapsed.TotalSeconds;

        /// <inheritdoc/>
        public override void OnEnable(bool isGameOpened)
        {
            if (File.Exists(this.SettingsPath))
            {
                try
                {
                    this.Settings = JsonSerializer.Deserialize(
                        File.ReadAllText(this.SettingsPath),
                        AmanamuVoidAlertJsonContext.Default.AmanamuVoidAlertSettings) ?? new AmanamuVoidAlertSettings();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AmanamuVoidAlert] Failed to load settings: {ex.Message}");
                    this.Settings = new AmanamuVoidAlertSettings();
                }
            }
            else
            {
                this.Settings = new AmanamuVoidAlertSettings();
            }
        }

        /// <inheritdoc/>
        public override void OnDisable()
        {
            this.tracked.Clear();
        }

        /// <inheritdoc/>
        public override void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(this.SettingsPath) ?? string.Empty);
                File.WriteAllText(
                    this.SettingsPath,
                    JsonSerializer.Serialize(this.Settings, AmanamuVoidAlertJsonContext.Default.AmanamuVoidAlertSettings));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AmanamuVoidAlert] Failed to save settings: {ex.Message}");
            }
        }

        /// <inheritdoc/>
        public override void DrawSettings()
        {
            ImGui.Checkbox(this.PluginText.Label("settings.enable_overlay", "Enable overlay", "AmanamuEnableOverlay"), ref this.Settings.EnableOverlay);
            ImGui.Checkbox(this.PluginText.Label("settings.hide_unfocused", "Hide when game unfocused/paused", "AmanamuHideUnfocused"), ref this.Settings.HideWhenGameUnfocusedOrPaused);
            ImGui.Checkbox(this.PluginText.Label("settings.show_debug_window", "Show debug window", "AmanamuShowDebug"), ref this.Settings.ShowDebugWindow);
            ImGui.Checkbox(this.PluginText.Label("settings.draw_labels", "Draw on-screen labels", "AmanamuDrawLabels"), ref this.Settings.DrawOnScreenLabels);
            ImGui.Checkbox(this.PluginText.Label("settings.draw_arrows", "Draw off-screen edge arrows", "AmanamuDrawArrows"), ref this.Settings.DrawOffscreenArrows);
            ImGui.Checkbox(this.PluginText.Label("settings.draw_edge_always", "Draw edge arrow even when monster is on screen", "AmanamuDrawEdgeAlways"), ref this.Settings.DrawEdgeArrowForOnScreenMonsters);
            ImGui.Checkbox(this.PluginText.Label("settings.draw_circle", "Draw circle around monster", "AmanamuDrawCircle"), ref this.Settings.DrawCircle);
            ImGui.Checkbox(this.PluginText.Label("settings.only_rare_unique", "Only Rare or Unique monsters", "AmanamuOnlyRareUnique"), ref this.Settings.OnlyRareOrUnique);
            ImGui.Checkbox(this.PluginText.Label("settings.log_new", "Log newly detected monsters", "AmanamuLogNew"), ref this.Settings.LogNewDetections);

            ImGui.Separator();

            ImGui.DragFloat("Max tracking distance", ref this.Settings.MaxDistance, 50f, 500f, 10000f, "%.0f");
            ImGui.DragFloat("Forget after seconds", ref this.Settings.ForgetAfterSeconds, 0.5f, 1f, 60f, "%.1fs");
            ImGui.DragFloat("Forget missing entity seconds", ref this.Settings.MissingEntityForgetSeconds, 0.1f, 0.2f, 10f, "%.2fs");
            ImGui.DragFloat("Circle radius", ref this.Settings.CircleRadius, 1f, 10f, 100f, "%.0f px");
            ImGui.DragFloat("Circle thickness", ref this.Settings.CircleThickness, 0.5f, 1f, 10f, "%.1f px");
            ImGui.DragFloat("Label Y offset", ref this.Settings.LabelYOffset, 1f, 10f, 200f, "%.0f px");
            ImGui.DragFloat("Arrow edge margin", ref this.Settings.ArrowEdgeMargin, 1f, 10f, 250f, "%.0f px");

            ImGui.Separator();

            ImGui.ColorEdit4("Inside Cloud Color", ref this.Settings.InsideCloudColor);
            ImGui.ColorEdit4("Outside Cloud Color", ref this.Settings.OutsideCloudColor);

            ImGui.Separator();

            ImGui.TextWrapped("Primary detection:");
            ImGui.BulletText($"MonsterMod: {ExpectedMonsterModId}");
            ImGui.BulletText($"Mod Metadata: {ExpectedMonsterModMetadata}");
            ImGui.BulletText($"Inside cloud buff: {BuffInsideCloud}");
            ImGui.BulletText($"Lightless buff prefix: {BuffPrefixAbyssLightlessWell}");

            if (ImGui.Button("Clear tracked monsters"))
            {
                this.tracked.Clear();
            }
        }

        /// <inheritdoc/>
        public override void DrawUI()
        {
            if (Core.States.GameCurrentState is not (GameStateTypes.InGameState or GameStateTypes.EscapeState))
            {
                return;
            }

            if (this.Settings.HideWhenGameUnfocusedOrPaused &&
                Core.States.GameCurrentState != GameStateTypes.InGameState)
            {
                return;
            }

            if (!this.Settings.EnableOverlay && !this.Settings.ShowDebugWindow)
            {
                return;
            }

            var inGameState = Core.States.InGameStateObject;
            var currentArea = inGameState.CurrentAreaInstance;
            var currentWorld = inGameState.CurrentWorldInstance;
            var player = currentArea.Player;
            if (player == null || currentWorld == null)
            {
                return;
            }

            if (currentWorld.AreaDetails.IsTown || currentWorld.AreaDetails.IsHideout)
            {
                return;
            }

            var now = this.SecondsSinceEnable;

            this.MarkAllUnseen();
            this.ScanEntities(currentArea, player, now);
            this.PruneOld(now);

            if (this.Settings.EnableOverlay)
            {
                this.DrawOverlay(currentWorld, player, now);
            }

            if (this.Settings.ShowDebugWindow)
            {
                this.DrawDebugWindow(currentWorld, now);
            }
        }

        private void MarkAllUnseen()
        {
            foreach (var kv in this.tracked)
            {
                kv.Value.SeenThisFrame = false;
            }
        }

        private void ScanEntities(AreaInstance area, Entity player, float now)
        {
            Vector3 playerPos = Vector3.Zero;
            if (player.TryGetComponent<Render>(out var pRender))
            {
                playerPos = new Vector3(pRender.WorldPosition.X, pRender.WorldPosition.Y, pRender.WorldPosition.Z);
            }

            foreach (var entity in area.AwakeEntities.Values)
            {
                if (!entity.IsValid || entity.EntityType != EntityTypes.Monster)
                {
                    continue;
                }

                if (entity.TryGetComponent<Life>(out var life) && life.Health.Current <= 0 && life.Health.Total > 0)
                {
                    this.tracked.Remove(entity.Id);
                    continue;
                }

                if (!entity.TryGetComponent<Render>(out var render))
                {
                    continue;
                }

                var entityPos = new Vector3(render.WorldPosition.X, render.WorldPosition.Y, render.WorldPosition.Z);
                var distance = Vector2.Distance(new Vector2(playerPos.X, playerPos.Y), new Vector2(entityPos.X, entityPos.Y));
                if (distance > this.Settings.MaxDistance)
                {
                    continue;
                }

                // Check Buffs
                bool hasAnyLightlessBuff = false;
                bool insideCloud = false;
                var buffNames = new List<string>();

                if (entity.TryGetComponent<Buffs>(out var buffs))
                {
                    foreach (var kv in buffs.StatusEffects)
                    {
                        buffNames.Add(kv.Key);
                        if (kv.Key.Contains(BuffPrefixAbyssLightlessWell, StringComparison.OrdinalIgnoreCase))
                        {
                            hasAnyLightlessBuff = true;
                        }

                        if (kv.Key.Contains(BuffInsideCloud, StringComparison.OrdinalIgnoreCase))
                        {
                            insideCloud = true;
                        }
                    }
                }

                // Check ObjectMagicProperties
                bool foundByMonsterMod = false;
                var monsterModDebug = new List<string>();

                if (entity.TryGetComponent<ObjectMagicProperties>(out var omp))
                {
                    if (this.Settings.OnlyRareOrUnique && omp.Rarity < Rarity.Rare)
                    {
                        continue;
                    }

                    foreach (var modName in omp.ModNames)
                    {
                        monsterModDebug.Add(modName);
                        if (string.Equals(modName, ExpectedMonsterModId, StringComparison.OrdinalIgnoreCase) ||
                            modName.Contains("LightlessWells", StringComparison.OrdinalIgnoreCase) ||
                            modName.Contains("MonsterAbyssLightless", StringComparison.OrdinalIgnoreCase))
                        {
                            foundByMonsterMod = true;
                        }
                    }

                    if (!foundByMonsterMod)
                    {
                        foreach (var (modName, _) in omp.Mods)
                        {
                            if (string.Equals(modName, ExpectedMonsterModId, StringComparison.OrdinalIgnoreCase) ||
                                modName.Contains("LightlessWells", StringComparison.OrdinalIgnoreCase) ||
                                modName.Contains("MonsterAbyssLightless", StringComparison.OrdinalIgnoreCase))
                            {
                                foundByMonsterMod = true;
                            }
                        }
                    }
                }

                // Fallback: entity metadata path contains Amanamu
                bool foundByPath = !string.IsNullOrEmpty(entity.Path) &&
                    (entity.Path.Contains("Amanamu", StringComparison.OrdinalIgnoreCase) ||
                     entity.Path.Contains("LightlessLeader", StringComparison.OrdinalIgnoreCase));

                bool alreadyKnown = this.tracked.ContainsKey(entity.Id);

                if (!foundByMonsterMod && !hasAnyLightlessBuff && !foundByPath && !alreadyKnown)
                {
                    continue;
                }

                bool isNew = !alreadyKnown;
                if (!this.tracked.TryGetValue(entity.Id, out var trackedMonster))
                {
                    trackedMonster = new TrackedMonster();
                    this.tracked[entity.Id] = trackedMonster;
                }

                trackedMonster.Id = entity.Id;
                trackedMonster.Address = entity.Address;
                trackedMonster.Path = entity.Path ?? string.Empty;
                trackedMonster.SeenThisFrame = true;
                trackedMonster.Distance = distance;
                trackedMonster.LastSeenSeconds = now;
                trackedMonster.LastLiveEntitySeconds = now;
                trackedMonster.WasRecentlyLive = true;

                trackedMonster.WorldPosition = entityPos;
                trackedMonster.ModelBoundsZ = Math.Max(render.ModelBounds.Z, 80f);

                trackedMonster.HasAmanamuMonsterMod = foundByMonsterMod || trackedMonster.HasAmanamuMonsterMod;
                trackedMonster.HasAnyLightlessBuff = hasAnyLightlessBuff || trackedMonster.HasAnyLightlessBuff;
                trackedMonster.InsideCloud = insideCloud;
                trackedMonster.LastBuffs = buffNames;

                if (monsterModDebug.Count > 0)
                {
                    trackedMonster.LastMonsterMods = monsterModDebug;
                }

                if (isNew && this.Settings.LogNewDetections)
                {
                    Console.WriteLine($"[AmanamuVoidAlert] Detected monster: ID={trackedMonster.Id}, byMod={foundByMonsterMod}, byBuff={hasAnyLightlessBuff}, path={trackedMonster.Path}");
                }
            }
        }

        private void PruneOld(float now)
        {
            var eraseIds = new List<uint>();
            foreach (var (id, item) in this.tracked)
            {
                float ageSinceSeen = now - item.LastSeenSeconds;
                float ageSinceLive = now - item.LastLiveEntitySeconds;

                if (ageSinceSeen > this.Settings.ForgetAfterSeconds)
                {
                    eraseIds.Add(id);
                    continue;
                }

                if (item.WasRecentlyLive && ageSinceLive > this.Settings.MissingEntityForgetSeconds)
                {
                    eraseIds.Add(id);
                    continue;
                }
            }

            foreach (var id in eraseIds)
            {
                this.tracked.Remove(id);
            }
        }

        private void DrawOverlay(WorldData worldInstance, Entity player, float now)
        {
            if (this.tracked.Count == 0)
            {
                return;
            }

            var draw = ImGui.GetForegroundDrawList();

            float screenW = Core.Process.WindowArea.Width;
            float screenH = Core.Process.WindowArea.Height;
            if (screenW <= 0f || screenH <= 0f)
            {
                return;
            }

            var screenCenter = new Vector2(screenW * 0.5f, screenH * 0.5f);

            Vector3 playerWorldPos = Vector3.Zero;
            if (player.TryGetComponent<Render>(out var pRender))
            {
                playerWorldPos = new Vector3(pRender.WorldPosition.X, pRender.WorldPosition.Y, pRender.WorldPosition.Z);
            }

            var pScreen = worldInstance.WorldToScreen(new StdTuple3D<float>
            {
                X = playerWorldPos.X,
                Y = playerWorldPos.Y,
                Z = playerWorldPos.Z + 40f,
            }, playerWorldPos.Z + 40f);

            var originScreen = (pScreen != Vector2.Zero &&
                                pScreen.X >= 0f && pScreen.X <= screenW &&
                                pScreen.Y >= 0f && pScreen.Y <= screenH)
                                ? pScreen
                                : screenCenter;

            foreach (var trackedMonster in this.tracked.Values)
            {
                var worldPos = trackedMonster.WorldPosition;
                var markerZ = worldPos.Z + Math.Max(trackedMonster.ModelBoundsZ, 80f);

                // 1. Calculate true screen-space direction vector pointing from player towards the monster.
                Vector2 dir = this.GetArrowDirection(worldInstance, playerWorldPos, trackedMonster);

                // 2. Project monster position to screen
                var screenPos = worldInstance.WorldToScreen(new StdTuple3D<float>
                {
                    X = worldPos.X,
                    Y = worldPos.Y,
                    Z = markerZ,
                }, markerZ);

                bool projectionReturned = screenPos != Vector2.Zero &&
                                         !float.IsNaN(screenPos.X) && !float.IsNaN(screenPos.Y);

                // Verify that screenPos is genuinely in front of the camera and not a mirrored point behind the camera:
                bool inFrontOfCamera = false;
                if (projectionReturned)
                {
                    var screenVec = screenPos - originScreen;
                    if (screenVec.LengthSquared() > 4f)
                    {
                        inFrontOfCamera = Vector2.Dot(Vector2.Normalize(screenVec), dir) > 0.2f;
                    }
                    else
                    {
                        inFrontOfCamera = true;
                    }
                }

                bool visibleOnScreen = inFrontOfCamera &&
                                      screenPos.X >= 0f && screenPos.X <= screenW &&
                                      screenPos.Y >= 0f && screenPos.Y <= screenH;

                uint color = ImGuiHelper.Color(trackedMonster.InsideCloud
                    ? this.Settings.InsideCloudColor
                    : this.Settings.OutsideCloudColor);
                uint textShadow = ImGuiHelper.Color(0, 0, 0, 230);

                // Draw on-screen circle and label
                if (visibleOnScreen)
                {
                    if (this.Settings.DrawCircle)
                    {
                        draw.AddCircle(screenPos, this.Settings.CircleRadius, color, 48, this.Settings.CircleThickness);
                    }

                    if (this.Settings.DrawOnScreenLabels)
                    {
                        string state = trackedMonster.InsideCloud ? "INSIDE CLOUD" : "OUTSIDE CLOUD";
                        string label = $"AMANAMU VOID\n{state}\n{trackedMonster.Distance:F0}";

                        var textSize = ImGui.CalcTextSize(label);
                        var textPos = new Vector2(screenPos.X - (textSize.X * 0.5f), screenPos.Y - this.Settings.LabelYOffset - textSize.Y);

                        draw.AddText(new Vector2(textPos.X + 1f, textPos.Y + 1f), textShadow, label);
                        draw.AddText(textPos, color, label);
                    }
                }

                // Draw off-screen edge arrow
                bool shouldDrawEdgeArrow = this.Settings.DrawOffscreenArrows &&
                                           (!visibleOnScreen || this.Settings.DrawEdgeArrowForOnScreenMonsters);

                if (shouldDrawEdgeArrow)
                {
                    var arrowPos = ClampToScreenEdge(originScreen, dir, screenW, screenH, this.Settings.ArrowEdgeMargin);
                    DrawArrow(draw, arrowPos, dir, color);

                    string text = $"VOID {trackedMonster.Distance:F0} {(trackedMonster.InsideCloud ? "IN" : "OUT")}";
                    var textSize = ImGui.CalcTextSize(text);
                    var textPos = new Vector2(arrowPos.X - (textSize.X * 0.5f), arrowPos.Y + 16f);

                    // Clamp text label strictly inside screen boundaries
                    textPos.X = Math.Clamp(textPos.X, 10f, screenW - textSize.X - 10f);
                    textPos.Y = Math.Clamp(textPos.Y, 10f, screenH - textSize.Y - 10f);

                    draw.AddText(new Vector2(textPos.X + 1f, textPos.Y + 1f), textShadow, text);
                    draw.AddText(textPos, color, text);
                }
            }
        }

        private Vector2 GetArrowDirection(
            WorldData worldInstance,
            Vector3 playerPos,
            TrackedMonster trackedMonster)
        {
            var delta = new Vector2(trackedMonster.WorldPosition.X - playerPos.X, trackedMonster.WorldPosition.Y - playerPos.Y);
            if (delta.LengthSquared() < 0.1f)
            {
                if (trackedMonster.HasLastScreenDirection)
                {
                    return trackedMonster.LastScreenDirection;
                }

                return -Vector2.UnitY;
            }

            var normDelta = Vector2.Normalize(delta);

            // Sample a lookahead point just 80 world units (~7.4 grid units) from the player
            // in the exact direction of the monster.
            // Because the player is on-screen, a point 80 units away is guaranteed to be in front of the camera (W > 0)
            // and never inverted by perspective projection.
            var pScreen = worldInstance.WorldToScreen(new StdTuple3D<float>
            {
                X = playerPos.X,
                Y = playerPos.Y,
                Z = playerPos.Z + 40f,
            }, playerPos.Z + 40f);

            var sampleScreen = worldInstance.WorldToScreen(new StdTuple3D<float>
            {
                X = playerPos.X + (normDelta.X * 80f),
                Y = playerPos.Y + (normDelta.Y * 80f),
                Z = playerPos.Z + 40f,
            }, playerPos.Z + 40f);

            if (pScreen != Vector2.Zero && sampleScreen != Vector2.Zero)
            {
                var pDir = sampleScreen - pScreen;
                if (pDir.LengthSquared() > 0.001f)
                {
                    var norm = Vector2.Normalize(pDir);
                    trackedMonster.LastScreenDirection = norm;
                    trackedMonster.HasLastScreenDirection = true;
                    return norm;
                }
            }

            // PoE camera isometric projection fallback:
            // Camera tilted at PoE's ~38.7 deg angle.
            // Screen X = (World X - World Y) * 0.78
            // Screen Y = (World X + World Y) * 0.62
            float sx = (normDelta.X - normDelta.Y) * 0.78f;
            float sy = (normDelta.X + normDelta.Y) * 0.62f;
            var isoDir = new Vector2(sx, sy);
            if (isoDir.LengthSquared() > 0.001f)
            {
                var norm = Vector2.Normalize(isoDir);
                trackedMonster.LastScreenDirection = norm;
                trackedMonster.HasLastScreenDirection = true;
                return norm;
            }

            if (trackedMonster.HasLastScreenDirection)
            {
                return trackedMonster.LastScreenDirection;
            }

            return -Vector2.UnitY;
        }

        private static Vector2 ClampToScreenEdge(Vector2 center, Vector2 dir, float width, float height, float margin)
        {
            if (dir.LengthSquared() <= 0.001f)
            {
                dir = -Vector2.UnitY;
            }
            else
            {
                dir = Vector2.Normalize(dir);
            }

            float tx = float.MaxValue;
            float ty = float.MaxValue;

            if (Math.Abs(dir.X) > 0.001f)
            {
                float edgeX = dir.X > 0f ? (width - margin) : margin;
                tx = (edgeX - center.X) / dir.X;
            }

            if (Math.Abs(dir.Y) > 0.001f)
            {
                float edgeY = dir.Y > 0f ? (height - margin) : margin;
                ty = (edgeY - center.Y) / dir.Y;
            }

            float t = Math.Min(tx, ty);
            if (t < 0f || float.IsNaN(t) || float.IsInfinity(t))
            {
                t = 0f;
            }

            return new Vector2(center.X + (dir.X * t), center.Y + (dir.Y * t));
        }

        private static void DrawArrow(ImDrawListPtr draw, Vector2 pos, Vector2 dir, uint color)
        {
            if (dir.LengthSquared() <= 0.001f)
            {
                dir = -Vector2.UnitY;
            }
            else
            {
                dir = Vector2.Normalize(dir);
            }

            var perp = new Vector2(-dir.Y, dir.X);
            const float size = 18f;

            var tip = pos + (dir * size);
            var back = pos - (dir * size * 0.65f);

            var p1 = tip;
            var p2 = back + (perp * size * 0.55f);
            var p3 = back - (perp * size * 0.55f);

            draw.AddTriangleFilled(p1, p2, p3, color);
            draw.AddTriangle(p1, p2, p3, ImGuiHelper.Color(0, 0, 0, 220), 2f);
        }

        private void DrawDebugWindow(WorldData world, float now)
        {
            ImGui.SetNextWindowSize(new Vector2(750, 420), ImGuiCond.FirstUseEver);

            if (!ImGui.Begin("Amanamu Void Alert Debug", ref this.Settings.ShowDebugWindow))
            {
                ImGui.End();
                return;
            }

            ImGui.Text($"Area: {world.AreaDetails.Id}");
            ImGui.Text($"Tracked monsters: {this.tracked.Count}");
            ImGui.Text($"Expected MonsterMod: {ExpectedMonsterModId}");
            ImGui.Text($"Inside Cloud Buff: {BuffInsideCloud}");

            ImGui.Separator();

            if (ImGui.Button("Clear tracked"))
            {
                this.tracked.Clear();
            }

            ImGui.SameLine();

            if (ImGui.Button("Save settings"))
            {
                this.SaveSettings();
            }

            ImGui.Separator();

            if (ImGui.BeginTable("##tracked_amanamu", 7,
                ImGuiTableFlags.Borders |
                ImGuiTableFlags.RowBg |
                ImGuiTableFlags.Resizable |
                ImGuiTableFlags.ScrollY))
            {
                ImGui.TableSetupColumn("Id");
                ImGui.TableSetupColumn("State");
                ImGui.TableSetupColumn("ByMod");
                ImGui.TableSetupColumn("Dist");
                ImGui.TableSetupColumn("Seen");
                ImGui.TableSetupColumn("Path");
                ImGui.TableSetupColumn("Mods / Buffs");
                ImGui.TableHeadersRow();

                foreach (var (id, item) in this.tracked)
                {
                    ImGui.TableNextRow();

                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text($"{id}");

                    ImGui.TableSetColumnIndex(1);
                    if (item.InsideCloud)
                    {
                        ImGui.TextColored(this.Settings.InsideCloudColor, "INSIDE");
                    }
                    else
                    {
                        ImGui.TextColored(this.Settings.OutsideCloudColor, "OUTSIDE");
                    }

                    ImGui.TableSetColumnIndex(2);
                    if (item.HasAmanamuMonsterMod)
                    {
                        ImGui.TextColored(new Vector4(0.3f, 1f, 0.45f, 1f), "YES");
                    }
                    else
                    {
                        ImGui.TextDisabled("NO");
                    }

                    ImGui.TableSetColumnIndex(3);
                    ImGui.Text($"{item.Distance:F0}");

                    ImGui.TableSetColumnIndex(4);
                    ImGui.Text($"{now - item.LastSeenSeconds:F1}s");

                    ImGui.TableSetColumnIndex(5);
                    ImGui.TextWrapped(item.Path);

                    ImGui.TableSetColumnIndex(6);
                    if (item.LastMonsterMods.Count > 0)
                    {
                        ImGui.TextColored(new Vector4(0.6f, 0.9f, 1f, 1f), "Mods:");
                        foreach (var m in item.LastMonsterMods)
                        {
                            ImGui.TextWrapped(m);
                        }
                    }

                    if (item.LastBuffs.Count > 0)
                    {
                        ImGui.TextColored(new Vector4(0.9f, 0.8f, 1f, 1f), "Buffs:");
                        foreach (var b in item.LastBuffs)
                        {
                            if (b.Contains("abyss", StringComparison.OrdinalIgnoreCase) ||
                                b.Contains("lightless", StringComparison.OrdinalIgnoreCase) ||
                                b.Contains("well", StringComparison.OrdinalIgnoreCase))
                            {
                                ImGui.TextWrapped(b);
                            }
                        }
                    }

                    if (item.LastMonsterMods.Count == 0 && item.LastBuffs.Count == 0)
                    {
                        ImGui.TextDisabled("-");
                    }
                }

                ImGui.EndTable();
            }

            ImGui.End();
        }
    }
}
