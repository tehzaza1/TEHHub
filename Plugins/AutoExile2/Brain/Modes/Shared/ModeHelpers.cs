// <copyright file="ModeHelpers.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Modes.Shared
{
    using System;
    using System.Numerics;
    using TEHhub;
    using TEHhub.RemoteObjects.Components;
    using TEHhub.RemoteObjects.States.InGameStateObjects;
    using TEHhub.Offsets.Natives;
    using AutoExile2.Systems;

    /// <summary>
    /// Static utilities shared across farming modes, ported from AutoExile 1 ModeHelpers.
    /// </summary>
    public static class ModeHelpers
    {
        /// <summary>
        /// Finds the nearest targetable personal stash entity inside the PoE 2 network bubble.
        /// Guild stash objects are deliberately excluded from automatic interaction.
        /// </summary>
        public static Entity? FindNearestStash(
            AreaInstance area,
            Vector2 playerGrid,
            float maxDist = Pathfinding.NetworkBubbleRadius)
        {
            if (area == null)
            {
                return null;
            }

            Entity? best = null;
            float bestDist = maxDist;
            foreach (var entity in area.AwakeEntities.Values)
            {
                if (!entity.IsValid || !IsPersonalStashPath(entity.Path) ||
                    !entity.TryGetComponent<Targetable>(out var targetable) || !targetable.IsTargetable ||
                    !entity.TryGetComponent<Render>(out var render))
                {
                    continue;
                }

                var gridPos = new Vector2(render.GridPosition.X, render.GridPosition.Y);
                var distance = Vector2.Distance(playerGrid, gridPos);
                if (distance < bestDist)
                {
                    best = entity;
                    bestDist = distance;
                }
            }

            return best;
        }

        /// <summary>
        /// Conservative metadata-path classifier for a personal stash target.
        /// </summary>
        public static bool IsPersonalStashPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) ||
                path.Contains("Guild", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return path.Contains("Stash", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Find the best targetable TownPortal entity near player.
        /// Checks entity path containing "Portal" or "Town_Portals" and Targetable.IsTargetable.
        /// </summary>
        public static Entity? FindNearestPortal(AreaInstance area, Vector2 playerGrid, float maxDist = 80f)
        {
            if (area == null)
            {
                return null;
            }

            Entity? best = null;
            float bestDist = maxDist;

            foreach (var entity in area.AwakeEntities.Values)
            {
                if (!entity.IsValid)
                {
                    continue;
                }

                string path = entity.Path ?? string.Empty;
                bool isPortalMatch = path.Contains("Portal", StringComparison.OrdinalIgnoreCase) ||
                                     path.Contains("Town_Portals", StringComparison.OrdinalIgnoreCase) ||
                                     path.Contains("Gateway", StringComparison.OrdinalIgnoreCase);

                if (!isPortalMatch && !entity.TryGetComponent<Transitionable>(out _))
                {
                    continue;
                }

                if (!entity.TryGetComponent<Targetable>(out var targetable) || !targetable.IsTargetable)
                {
                    continue;
                }

                if (!entity.TryGetComponent<Render>(out var r))
                {
                    continue;
                }

                var gridPos = new Vector2(r.GridPosition.X, r.GridPosition.Y);
                float dist = Vector2.Distance(playerGrid, gridPos);

                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = entity;
                }
            }

            return best;
        }

        /// <summary>
        /// Converts entity world position to screen coordinates and performs a humanized click.
        /// </summary>
        public static bool ClickEntity(
            WorldData world,
            Entity entity,
            Func<bool>? canClick = null,
            Func<bool>? preMouseDownValidation = null)
        {
            if (world == null || entity == null || !entity.IsValid)
            {
                return false;
            }

            if (!entity.TryGetComponent<Render>(out var r))
            {
                return false;
            }

            var targetPos = r.WorldPosition;
            float targetZ = targetPos.Z + (r.ModelBounds.Z * 0.5f);

            var screenPos = world.WorldToScreen(new StdTuple3D<float>
            {
                X = targetPos.X,
                Y = targetPos.Y,
                Z = targetZ
            });

            if (screenPos == Vector2.Zero || float.IsNaN(screenPos.X))
            {
                return false;
            }

            var window = Core.Process.WindowArea;
            if (!float.IsFinite(screenPos.X) || !float.IsFinite(screenPos.Y) ||
                screenPos.X < 0f || screenPos.Y < 0f ||
                screenPos.X >= window.Width || screenPos.Y >= window.Height)
            {
                return false;
            }

            BotInput.HumanClick(
                screenPos + new Vector2(window.Left, window.Top),
                canClick: canClick,
                preMouseDownValidation: preMouseDownValidation);
            return true;
        }

    }
}
