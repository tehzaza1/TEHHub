// <copyright file="RangeVisualizer.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Systems
{
    using System;
    using System.Linq;
    using System.Numerics;
    using AutoExile2.Modes;
    using GameHelper.RemoteObjects.States.InGameStateObjects;
    using GameHelper.Utils;
    using ImGuiNET;

    /// <summary>
    /// Renders real-time 3D terrain distance circles and aim indicators in-game
    /// when adjusting sliders in the Web Dashboard or navigating in Co-op / Solo modes.
    /// </summary>
    public static class RangeVisualizer
    {
        private static readonly object LockObj = new();
        private static RangePreviewRequest? activePreview;

        /// <summary>
        /// Represents a temporary active range preview triggered by slider dragging in the UI.
        /// </summary>
        public sealed class RangePreviewRequest
        {
            public string Type { get; set; } = string.Empty;
            public float Radius { get; set; }
            public string Unit { get; set; } = "world"; // "world" or "grid"
            public string Label { get; set; } = string.Empty;
            public string Color { get; set; } = string.Empty;
            public string TargetEntity { get; set; } = "Leader"; // "Leader", "Follower", "Player"
            public DateTime ExpiresAt { get; set; }
        }

        /// <summary>
        /// Sets or refreshes an active preview request (called by POST /api/preview_range).
        /// </summary>
        public static void SetPreview(string type, float radius, string unit, string label, string colorHex, string targetEntity = "Leader", float durationSeconds = 3.5f)
        {
            lock (LockObj)
            {
                activePreview = new RangePreviewRequest
                {
                    Type = type,
                    Radius = radius,
                    Unit = unit,
                    Label = label,
                    Color = colorHex,
                    TargetEntity = targetEntity,
                    ExpiresAt = DateTime.UtcNow.AddSeconds(durationSeconds),
                };
            }
        }

        /// <summary>
        /// Gets the current active preview if not expired.
        /// </summary>
        public static RangePreviewRequest? GetActivePreview()
        {
            lock (LockObj)
            {
                if (activePreview == null) return null;
                if (DateTime.UtcNow > activePreview.ExpiresAt)
                {
                    activePreview = null;
                    return null;
                }
                return activePreview;
            }
        }

        /// <summary>
        /// Main render hook called on every overlay frame.
        /// Renders distance circle/aim indicator ONLY while adjusting sliders in the UI.
        /// </summary>
        public static void Render(ImDrawListPtr draw, BotContext ctx, IBotMode activeMode)
        {
            if (ctx.World == null || ctx.Area == null) return;

            var s = ctx.Settings;
            if (!s.ShowDistanceCircles || !s.ShowOverlay) return;

            // "ให้วงมันขึ้นแค่ตอนปรับเท่านั้นนะ" -> Only draw when actively previewing/adjusting a slider!
            var preview = GetActivePreview();
            if (preview == null) return;

            float convertor = ctx.Area.WorldToGridConvertor > 0 ? ctx.Area.WorldToGridConvertor : 10.87f;
            var heightGrid = ctx.Area.GridHeightData;

            Vector2 centerGrid = ctx.PlayerGrid;
            Vector2 heading = new Vector2(0, -1);

            if (activeMode is CoopFollowerMode coop)
            {
                centerGrid = preview.TargetEntity == "Follower"
                    ? (coop.LastKnownFollowerGrid ?? ctx.PlayerGrid)
                    : (coop.LastKnownLeaderGrid ?? ctx.PlayerGrid);
                heading = coop.LeaderHeading;
            }
            else if (activeMode is BossMode boss && preview.TargetEntity == "Boss")
            {
                centerGrid = boss.CurrentBossGrid ?? ctx.PlayerGrid;
            }

            float radiusWorld = preview.Unit == "grid" ? (preview.Radius * convertor) : preview.Radius;
            uint color = ParseColor(preview.Color, 255);

            // Smooth pulsing thickness effect while adjusting
            float remainingSec = (float)(preview.ExpiresAt - DateTime.UtcNow).TotalSeconds;
            float pulse = 0.5f + (0.5f * MathF.Sin((3.0f - remainingSec) * 8f));
            float thickness = 3.0f + (pulse * 1.5f);

            if (preview.Type == "CullerAim")
            {
                DrawWorldAimIndicator(draw, ctx.World, heightGrid, convertor, centerGrid, heading, radiusWorld, color, thickness, preview.Label);
            }
            else
            {
                // Double-ring: subtle outer glow ring + bright crisp primary contour
                DrawWorldCircle(draw, ctx.World, heightGrid, convertor, centerGrid, radiusWorld + (3f * convertor / 10.87f), ParseColor(preview.Color, 80), 1.4f, 64, null, false);
                DrawWorldCircle(draw, ctx.World, heightGrid, convertor, centerGrid, radiusWorld, color, thickness, 64, preview.Label, true);
            }
        }

        /// <summary>
        /// Draws a 3D circle projected on the terrain using GameHelper's WorldToScreen and GridHeightData.
        /// </summary>
        public static void DrawWorldCircle(
            ImDrawListPtr draw,
            WorldData world,
            float[][]? heightGrid,
            float convertor,
            Vector2 centerGrid,
            float radiusWorld,
            uint color,
            float thickness = 2f,
            int segments = 48,
            string? label = null,
            bool drawBadge = true)
        {
            if (world == null || radiusWorld <= 1f) return;
            if (convertor <= 0f) convertor = 10.87f;

            float gridRadius = radiusWorld / convertor;
            float centerZ = Pathfinding.GetTerrainHeight(heightGrid, (int)centerGrid.X, (int)centerGrid.Y, 0f);

            Span<Vector2> screenPoints = stackalloc Vector2[segments];
            int validCount = 0;
            float step = (float)(2.0 * Math.PI / segments);

            Vector2 topScreenPos = Vector2.Zero;
            float minScreenY = float.MaxValue;

            for (int i = 0; i < segments; i++)
            {
                float angle = i * step;
                float gx = centerGrid.X + (gridRadius * MathF.Cos(angle));
                float gy = centerGrid.Y + (gridRadius * MathF.Sin(angle));
                float z = Pathfinding.GetTerrainHeight(heightGrid, (int)gx, (int)gy, centerZ);

                Vector2 sPos = world.WorldToScreen(new Vector2(gx * convertor, gy * convertor), z);
                if (sPos != Vector2.Zero)
                {
                    screenPoints[validCount++] = sPos;
                    if (sPos.Y < minScreenY)
                    {
                        minScreenY = sPos.Y;
                        topScreenPos = sPos;
                    }
                }
            }

            if (validCount >= 3)
            {
                for (int i = 0; i < validCount; i++)
                {
                    var p1 = screenPoints[i];
                    var p2 = screenPoints[(i + 1) % validCount];
                    // Skip line segments that span across the whole screen (camera wrap prevention)
                    if (Vector2.DistanceSquared(p1, p2) < 250000f)
                    {
                        draw.AddLine(p1, p2, color, thickness);
                    }
                }

                if (drawBadge && !string.IsNullOrEmpty(label) && topScreenPos != Vector2.Zero)
                {
                    var labelSize = ImGui.CalcTextSize(label);
                    var badgePos = topScreenPos - new Vector2(labelSize.X / 2f, labelSize.Y + 8f);
                    var pad = new Vector2(5, 3);

                    draw.AddRectFilled(badgePos - pad, badgePos + labelSize + pad, ImGuiHelper.Color(10, 14, 22, 220), 4f);
                    draw.AddRect(badgePos - pad, badgePos + labelSize + pad, color, 4f);
                    draw.AddText(badgePos, ImGuiHelper.Color(255, 255, 255, 255), label);
                }
            }
        }

        /// <summary>
        /// Draws a directional aim ray and target reticle on the ground in 3D world space.
        /// </summary>
        public static void DrawWorldAimIndicator(
            ImDrawListPtr draw,
            WorldData world,
            float[][]? heightGrid,
            float convertor,
            Vector2 leaderGrid,
            Vector2 heading,
            float aimDistanceWorld,
            uint color,
            float thickness = 2.5f,
            string? label = null)
        {
            if (world == null || aimDistanceWorld <= 10f) return;
            if (convertor <= 0f) convertor = 10.87f;
            if (heading == Vector2.Zero) heading = new Vector2(0, -1);
            heading = Vector2.Normalize(heading);

            float aimDistGrid = aimDistanceWorld / convertor;
            Vector2 aimGridPos = leaderGrid + (heading * aimDistGrid);

            float lZ = Pathfinding.GetTerrainHeight(heightGrid, (int)leaderGrid.X, (int)leaderGrid.Y, 0f);
            float aZ = Pathfinding.GetTerrainHeight(heightGrid, (int)aimGridPos.X, (int)aimGridPos.Y, lZ);

            Vector2 sLeader = world.WorldToScreen(new Vector2(leaderGrid.X * convertor, leaderGrid.Y * convertor), lZ);
            Vector2 sAim = world.WorldToScreen(new Vector2(aimGridPos.X * convertor, aimGridPos.Y * convertor), aZ);

            if (sAim != Vector2.Zero)
            {
                // Connecting line from Leader to Aim Target
                if (sLeader != Vector2.Zero)
                {
                    draw.AddLine(sLeader, sAim, color, thickness);
                }

                // Ground reticle circle around aim target
                DrawWorldCircle(draw, world, heightGrid, convertor, aimGridPos, 45f, color, thickness, 24, null, false);

                // Crosshair on screen
                draw.AddLine(sAim - new Vector2(10, 0), sAim + new Vector2(10, 0), color, 2f);
                draw.AddLine(sAim - new Vector2(0, 10), sAim + new Vector2(0, 10), color, 2f);

                // Text Badge
                string txt = !string.IsNullOrEmpty(label) ? label : $"🎯 Aim: {aimDistanceWorld:F0}w";
                var txtSize = ImGui.CalcTextSize(txt);
                var badgePos = sAim - new Vector2(txtSize.X / 2f, txtSize.Y + 12f);
                var pad = new Vector2(5, 3);

                draw.AddRectFilled(badgePos - pad, badgePos + txtSize + pad, ImGuiHelper.Color(10, 14, 22, 220), 4f);
                draw.AddRect(badgePos - pad, badgePos + txtSize + pad, color, 4f);
                draw.AddText(badgePos, ImGuiHelper.Color(255, 255, 255, 255), txt);
            }
        }

        /// <summary>
        /// Parses a hex color string like #f43f5e to an ImGui uint color with specified alpha.
        /// </summary>
        public static uint ParseColor(string hex, byte alpha = 230)
        {
            if (!string.IsNullOrEmpty(hex) && hex.StartsWith("#") && hex.Length >= 7)
            {
                try
                {
                    byte r = Convert.ToByte(hex.Substring(1, 2), 16);
                    byte g = Convert.ToByte(hex.Substring(3, 2), 16);
                    byte b = Convert.ToByte(hex.Substring(5, 2), 16);
                    return ImGuiHelper.Color(r, g, b, alpha);
                }
                catch { }
            }
            return ImGuiHelper.Color(244, 63, 94, alpha);
        }
    }
}