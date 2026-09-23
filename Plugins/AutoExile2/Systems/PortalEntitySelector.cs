// <copyright file="PortalEntitySelector.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Systems
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Numerics;
    using TEHhub.RemoteObjects.Components;
    using TEHhub.RemoteObjects.States.InGameStateObjects;

    /// <summary>
    /// Conservative selector shared by portal entry and exit. It recognizes the base
    /// MultiplexPortal and the Town_Portals metadata family without depending on MTX names
    /// or a Transitionable component.
    /// </summary>
    internal static class PortalEntitySelector
    {
        public static bool IsTownPortalPath(string? path) =>
            !string.IsNullOrWhiteSpace(path) &&
            (path.Contains("MultiplexPortal", StringComparison.OrdinalIgnoreCase) ||
             path.Contains("TownPortal", StringComparison.OrdinalIgnoreCase) ||
             path.Contains("Town_Portals", StringComparison.OrdinalIgnoreCase));

        public static HashSet<uint> Snapshot(AreaInstance area)
        {
            var result = new HashSet<uint>();
            foreach (var entity in area.AwakeEntities.Values)
            {
                if (entity.IsValid && entity.Id != 0 && IsTownPortalPath(entity.Path))
                {
                    result.Add(entity.Id);
                }
            }

            return result;
        }

        public static List<PortalCandidate> FindFreshTargetable(
            AreaInstance area,
            Vector2 playerGrid,
            HashSet<uint> existing,
            float maxDistance)
        {
            var result = new List<PortalCandidate>();
            foreach (var entity in area.AwakeEntities.Values)
            {
                if (!entity.IsValid || entity.Id == 0 || !IsTownPortalPath(entity.Path) ||
                    existing.Contains(entity.Id) ||
                    !entity.TryGetComponent<Targetable>(out var targetable) || !targetable.IsTargetable ||
                    !entity.TryGetComponent<Render>(out var render))
                {
                    continue;
                }

                var grid = new Vector2(render.GridPosition.X, render.GridPosition.Y);
                var distance = Vector2.Distance(playerGrid, grid);
                if (distance <= maxDistance)
                {
                    result.Add(new PortalCandidate(entity, distance));
                }
            }

            return result;
        }

        public static bool TrySelectNearest(
            IReadOnlyList<PortalCandidate> candidates,
            out PortalCandidate selected)
        {
            selected = default;
            if (candidates.Count == 0)
            {
                return false;
            }

            selected = candidates
                .OrderBy(candidate => candidate.Distance)
                .ThenBy(candidate => candidate.Entity.Id)
                .First();
            return true;
        }

        internal readonly record struct PortalCandidate(Entity Entity, float Distance);
    }
}
