// <copyright file="RitualWispAlertCore.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace RitualWispAlert
{
    using System;
    using System.IO;
    using System.Numerics;
    using System.Text.Json;
    using TEHhub;
    using TEHhub.Plugin;
    using TEHhub.RemoteEnums;
    using TEHhub.RemoteObjects.Components;
    using TEHhub.RemoteObjects.States;
    using TEHhub.RemoteObjects.States.InGameStateObjects;
    using TEHhub.Utils;
    using TEHhub.Offsets.Natives;
    using ImGuiNET;

    /// <summary>Draws the effective range around active Ritual wisps.</summary>
    public sealed class RitualWispAlertCore : PCore<RitualWispAlertSettings>
    {
        private const string WispMetadataPath = "Metadata/Monsters/LeagueRitual/RitualWispDaemon";
        private const int CircleSegments = 36;
        private const float GridUnitsPerMeter = 10f;
        private const float WorldUnitsPerGridUnit = 250f / 23f;
        private static readonly JsonSerializerOptions LegacyVectorOptions = new() { IncludeFields = true };

        private string SettingsPath => this.PluginConfigPath("settings.txt");

        /// <inheritdoc/>
        public override void OnEnable(bool isGameOpened)
        {
            if (File.Exists(this.SettingsPath))
            {
                try
                {
                    this.Settings = JsonSerializer.Deserialize(
                        File.ReadAllText(this.SettingsPath),
                        RitualWispAlertJsonContext.Default.RitualWispAlertSettings) ?? new RitualWispAlertSettings();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[RitualWispAlert] Failed to load settings: {ex.Message}");
                    this.Settings = new RitualWispAlertSettings();
                }

                return;
            }

            if (this.TryMigrateRitualHelperSettings())
            {
                this.SaveSettings();
            }
        }

        private bool TryMigrateRitualHelperSettings()
        {
            var pluginsDirectory = Directory.GetParent(this.DllDirectory)?.FullName;
            if (pluginsDirectory == null) return false;

            var legacyPath = Path.Join(pluginsDirectory, "RitualHelper", "config", "settings.txt");
            if (!File.Exists(legacyPath)) return false;

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(legacyPath));
                var legacy = document.RootElement;
                if (legacy.TryGetProperty("DrawWispCircle", out var drawWispCircle) && drawWispCircle.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    this.Settings.EnableOverlay = drawWispCircle.GetBoolean();
                if (legacy.TryGetProperty("HideWispCircleInBackgroundOrPaused", out var hideWispCircle) && hideWispCircle.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    this.Settings.HideWhenGameUnfocusedOrPaused = hideWispCircle.GetBoolean();
                if (legacy.TryGetProperty("WispCircleRadiusMeters", out var radius) && radius.TryGetSingle(out var radiusMeters))
                    this.Settings.RadiusMeters = radiusMeters;
                if (legacy.TryGetProperty("WispCircleThickness", out var thickness) && thickness.TryGetSingle(out var lineThickness))
                    this.Settings.Thickness = lineThickness;
                if (legacy.TryGetProperty("WispCircleColorInside", out var insideColor))
                    this.Settings.InsideColor = JsonSerializer.Deserialize<Vector4>(insideColor, LegacyVectorOptions);
                if (legacy.TryGetProperty("WispCircleColorOutside", out var outsideColor))
                    this.Settings.OutsideColor = JsonSerializer.Deserialize<Vector4>(outsideColor, LegacyVectorOptions);
                if (legacy.TryGetProperty("WispCircleOffsetX", out var offsetX) && offsetX.TryGetSingle(out var x))
                    this.Settings.OffsetX = x;
                if (legacy.TryGetProperty("WispCircleOffsetY", out var offsetY) && offsetY.TryGetSingle(out var y))
                    this.Settings.OffsetY = y;
                if (legacy.TryGetProperty("WispCircleOffsetZ", out var offsetZ) && offsetZ.TryGetSingle(out var z))
                    this.Settings.OffsetZ = z;
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RitualWispAlert] Failed to migrate RitualHelper settings: {ex.Message}");
                return false;
            }
        }

        /// <inheritdoc/>
        public override void OnDisable()
        {
        }

        /// <inheritdoc/>
        public override void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(this.SettingsPath) ?? string.Empty);
                File.WriteAllText(
                    this.SettingsPath,
                    JsonSerializer.Serialize(this.Settings, RitualWispAlertJsonContext.Default.RitualWispAlertSettings));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RitualWispAlert] Failed to save settings: {ex.Message}");
            }
        }

        /// <inheritdoc/>
        public override void DrawSettings()
        {
            ImGui.Checkbox(this.PluginText.Label("settings.enable_overlay", "Draw Ritual wisp range circle", "RitualWispEnableOverlay"), ref this.Settings.EnableOverlay);
            ImGui.DragFloat(this.PluginText.Label("settings.radius", "Circle radius (metres)", "RitualWispRadius"), ref this.Settings.RadiusMeters, 0.1f, 0.5f, 10f, "%.1f m");
            ImGui.DragFloat(this.PluginText.Label("settings.thickness", "Circle thickness", "RitualWispThickness"), ref this.Settings.Thickness, 0.1f, 0.5f, 10f, "%.1f px");
            ImGui.ColorEdit4(this.PluginText.Label("settings.inside_color", "Color while inside range", "RitualWispInsideColor"), ref this.Settings.InsideColor);
            ImGui.ColorEdit4(this.PluginText.Label("settings.outside_color", "Color while outside range", "RitualWispOutsideColor"), ref this.Settings.OutsideColor);
            ImGui.DragFloat(this.PluginText.Label("settings.offset_x", "Center X offset", "RitualWispOffsetX"), ref this.Settings.OffsetX, 0.5f, -500f, 500f, "%.1f");
            ImGui.DragFloat(this.PluginText.Label("settings.offset_y", "Center Y offset", "RitualWispOffsetY"), ref this.Settings.OffsetY, 0.5f, -500f, 500f, "%.1f");
            ImGui.DragFloat(this.PluginText.Label("settings.offset_z", "Height offset", "RitualWispOffsetZ"), ref this.Settings.OffsetZ, 0.5f, -500f, 500f, "%.1f");
        }

        /// <inheritdoc/>
        public override void DrawUI()
        {
            if (!this.Settings.EnableOverlay ||
                Core.States.GameCurrentState is not (GameStateTypes.InGameState or GameStateTypes.EscapeState)) return;

            if (this.Settings.HideWhenGameUnfocusedOrPaused &&
                Core.States.GameCurrentState != GameStateTypes.InGameState) return;

            var inGameState = Core.States.InGameStateObject;
            var areaInstance = inGameState.CurrentAreaInstance;
            var player = areaInstance.Player;
            var worldInstance = inGameState.CurrentWorldInstance;
            if (player == null || worldInstance == null) return;

            var drawList = ImGui.GetBackgroundDrawList();
            foreach (var entity in areaInstance.AwakeEntities.Values)
            {
                if (!entity.IsValid ||
                    entity.Path?.Contains(WispMetadataPath, StringComparison.OrdinalIgnoreCase) != true ||
                    !entity.TryGetComponent<Render>(out var render)) continue;

                var radiusGrid = this.Settings.RadiusMeters * GridUnitsPerMeter;
                var circleColor = player.DistanceFrom(entity) <= radiusGrid
                    ? this.Settings.InsideColor
                    : this.Settings.OutsideColor;
                var worldPosition = render.WorldPosition;
                var center = new Vector3(
                    worldPosition.X + this.Settings.OffsetX,
                    worldPosition.Y + this.Settings.OffsetY,
                    worldPosition.Z + this.Settings.OffsetZ);

                DrawCircle(
                    drawList,
                    inGameState,
                    center,
                    render.TerrainHeight + this.Settings.OffsetZ,
                    radiusGrid * WorldUnitsPerGridUnit,
                    ImGuiHelper.Color(circleColor),
                    this.Settings.Thickness);
            }
        }

        private static void DrawCircle(
            ImDrawListPtr drawList,
            InGameState inGameState,
            Vector3 center,
            float terrainHeight,
            float radius,
            uint color,
            float thickness)
        {
            var worldInstance = inGameState.CurrentWorldInstance;
            if (worldInstance == null) return;

            var points = new Vector2[CircleSegments];
            var validPoints = 0;
            for (var index = 0; index < CircleSegments; index++)
            {
                var angle = index * 2f * MathF.PI / CircleSegments;
                var screenPosition = worldInstance.WorldToScreen(
                    new StdTuple3D<float>
                    {
                        X = center.X + (radius * MathF.Cos(angle)),
                        Y = center.Y + (radius * MathF.Sin(angle)),
                        Z = terrainHeight,
                    },
                    terrainHeight);
                if (screenPosition != Vector2.Zero)
                {
                    points[validPoints++] = screenPosition;
                }
            }

            for (var index = 0; index < validPoints; index++)
            {
                var start = points[index];
                var end = points[(index + 1) % validPoints];
                if (Vector2.Distance(start, end) < 500f)
                {
                    drawList.AddLine(start, end, color, thickness);
                }
            }
        }
    }
}
