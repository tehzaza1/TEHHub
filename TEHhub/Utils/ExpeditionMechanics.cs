// <copyright file="ExpeditionMechanics.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace TEHhub.Utils
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text.RegularExpressions;
    using TEHhub.RemoteObjects.Components;
    using TEHhub.RemoteObjects.States.InGameStateObjects;

    /// <summary>
    ///     Authoritative PoE / PoE 2 Expedition constants, formulas, and dynamic Map Mod calculations.
    ///     Strictly enforces the PoE engine rule: All integer stats and charges are ALWAYS floored (Math.Floor / ปัดเศษลงเสมอ).
    /// </summary>
    public static class ExpeditionMechanics
    {
        private static readonly Regex PercentageRegex = new(@"([+-]?\d+(?:\.\d+)?)\s*%", RegexOptions.Compiled);
        private static readonly Regex NumberRegex = new(@"([+-]?\d+(?:\.\d+)?)", RegexOptions.Compiled);

        private static float ExtractModValue(AreaMod mod)
        {
            if (!float.IsNaN(mod.Values.Value0))
            {
                return mod.Values.Value0;
            }

            var text = $"{mod.DisplayName} {mod.RawName}";
            var pctMatch = PercentageRegex.Match(text);
            if (pctMatch.Success && float.TryParse(pctMatch.Groups[1].Value, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var val))
            {
                return val;
            }

            var numMatch = NumberRegex.Match(text);
            if (numMatch.Success && float.TryParse(numMatch.Groups[1].Value, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var num))
            {
                return num;
            }

            return 0f;
        }
        // -------------------------------------------------------------
        // Base Constants (World Units: 100 W = 1.0 metre)
        // -------------------------------------------------------------

        /// <summary>
        ///     Tolerance factor applied to the blast radius for coverage detection (+5%).
        ///     Compensates for entity model center offsets and the game's visual blast
        ///     circle being slightly larger than the raw constant.
        /// </summary>
        public const float CoverageToleranceFactor = 1.05f;

        /// <summary>
        ///     Regular Expedition explosive base count.
        /// </summary>
        public const int RegularBaseExplosives = 5;

        /// <summary>
        ///     Regular Expedition explosive explosion radius in World Units (300 W = 3.0 metres).
        /// </summary>
        public const float RegularRadiusWorld = 300f;

        /// <summary>
        ///     Regular Expedition explosive placement wire reach limit in World Units (1,000 W = 10.0 metres).
        /// </summary>
        public const float RegularPlacementReachWorld = 1000f;

        /// <summary>
        ///     Grand Expedition explosive base count.
        /// </summary>
        public const int GrandBaseExplosives = 15;

        /// <summary>
        ///     Grand Expedition explosive explosion radius in World Units (380 W = 3.8 metres).
        /// </summary>
        public const float GrandRadiusWorld = 380f;

        /// <summary>
        ///     Grand Expedition explosive placement wire reach limit in World Units (1,200 W = 12.0 metres).
        /// </summary>
        public const float GrandPlacementReachWorld = 1200f;

        /// <summary>
        ///     Represents dynamic runtime Expedition parameters calculated from current Area and Map Mods.
        /// </summary>
        public readonly struct ExpeditionConfig
        {
            public bool IsGrandExpedition { get; }

            public int ExplosiveCount { get; }

            public float ExplosionRadiusWorld { get; }

            public float ExplosionRadiusGrid { get; }

            public float PlacementReachWorld { get; }

            public float PlacementReachGrid { get; }

            public float IncreasedExplosivesPercent { get; }

            public float IncreasedRadiusPercent { get; }

            public ExpeditionConfig(
                bool isGrand,
                int explosiveCount,
                float radiusWorld,
                float radiusGrid,
                float reachWorld,
                float reachGrid,
                float incCountPct,
                float incRadiusPct)
            {
                this.IsGrandExpedition = isGrand;
                this.ExplosiveCount = explosiveCount;
                this.ExplosionRadiusWorld = radiusWorld;
                this.ExplosionRadiusGrid = radiusGrid;
                this.PlacementReachWorld = reachWorld;
                this.PlacementReachGrid = reachGrid;
                this.IncreasedExplosivesPercent = incCountPct;
                this.IncreasedRadiusPercent = incRadiusPct;
            }

            public override string ToString()
            {
                var type = this.IsGrandExpedition ? "Grand Expedition" : "Regular Expedition";
                var incStr = this.IncreasedExplosivesPercent > 0f ? $" (+{this.IncreasedExplosivesPercent:F0}%)" : string.Empty;
                return $"[{type}] Explosives: {this.ExplosiveCount}{incStr} | Radius: {this.ExplosionRadiusWorld:F0} W ({this.ExplosionRadiusGrid:F1} G) | Reach: {this.PlacementReachWorld:F0} W ({this.PlacementReachGrid:F1} G)";
            }
        }

        /// <summary>
        ///     Represents explosive placement coverage status for an Expedition target entity (Remnant / Monolith / Chest).
        /// </summary>
        public readonly struct ExpeditionExplosiveCoverage
        {
            public bool IsCovered { get; }

            public uint CoveringExplosiveId { get; }

            public Entity? CoveringExplosive { get; }

            public float DistanceWorld { get; }

            public float DistanceGrid { get; }

            public float ExplosionRadiusWorld { get; }

            public float ExplosionRadiusGrid { get; }

            public int TotalCoveringExplosives { get; }

            public int TotalExplosivesInArea { get; }

            public float DistanceToClosestWorld { get; }

            public uint ClosestExplosiveId { get; }

            public Entity? ClosestExplosive { get; }

            public ExpeditionExplosiveCoverage(
                bool isCovered,
                uint coveringExplosiveId,
                Entity? coveringExplosive,
                float distanceWorld,
                float distanceGrid,
                float explosionRadiusWorld,
                float explosionRadiusGrid,
                int totalCoveringExplosives,
                int totalExplosivesInArea,
                float distanceToClosestWorld,
                uint closestExplosiveId,
                Entity? closestExplosive)
            {
                this.IsCovered = isCovered;
                this.CoveringExplosiveId = coveringExplosiveId;
                this.CoveringExplosive = coveringExplosive;
                this.DistanceWorld = distanceWorld;
                this.DistanceGrid = distanceGrid;
                this.ExplosionRadiusWorld = explosionRadiusWorld;
                this.ExplosionRadiusGrid = explosionRadiusGrid;
                this.TotalCoveringExplosives = totalCoveringExplosives;
                this.TotalExplosivesInArea = totalExplosivesInArea;
                this.DistanceToClosestWorld = distanceToClosestWorld;
                this.ClosestExplosiveId = closestExplosiveId;
                this.ClosestExplosive = closestExplosive;
            }

            public static ExpeditionExplosiveCoverage None(float radiusWorld = RegularRadiusWorld, float radiusGrid = 27.6f) =>
                new(false, 0, null, float.MaxValue, float.MaxValue, radiusWorld, radiusGrid, 0, 0, float.MaxValue, 0, null);

            public override string ToString()
            {
                if (this.IsCovered)
                {
                    return $"[IN RANGE] Bomb #{this.CoveringExplosiveId} at {this.DistanceWorld:F1} W <= Radius {this.ExplosionRadiusWorld:F0} W ({this.TotalCoveringExplosives} bombs cover)";
                }

                if (this.TotalExplosivesInArea > 0)
                {
                    return $"[OUT OF RANGE] Closest Bomb #{this.ClosestExplosiveId} at {this.DistanceToClosestWorld:F1} W > Radius {this.ExplosionRadiusWorld:F0} W";
                }

                return $"[NO BOMBS] Effective Radius: {this.ExplosionRadiusWorld:F0} W ({this.ExplosionRadiusGrid:F1} G)";
            }
        }

        /// <summary>
        ///     Calculates the dynamic Expedition configuration for the given area by inspecting active map/area modifiers.
        /// </summary>
        /// <param name="area">AreaInstance to inspect, or null to inspect Core.States.InGameStateObject.CurrentAreaInstance.</param>
        /// <returns>Computed authoritative ExpeditionConfig.</returns>
        public static ExpeditionConfig GetConfig(AreaInstance? area = null)
        {
            if (area == null)
            {
                area = Core.States.InGameStateObject?.CurrentAreaInstance;
            }

            var gridToWorld = area?.WorldToGridConvertor ?? 10.869565f;
            if (gridToWorld <= 0f)
            {
                gridToWorld = 10.869565f;
            }

            bool isGrand = false;
            float increasedExplosivesPct = 0f;
            float increasedRadiusPct = 0f;

            // 1. Check World Area Id & Name (e.g. ExpeditionLogBook_Reef, ExpeditionLogBook_Atoll, etc.)
            var worldAreaId = Core.States.InGameStateObject?.CurrentWorldInstance?.AreaDetails?.Id ?? string.Empty;
            var worldAreaName = Core.States.InGameStateObject?.CurrentWorldInstance?.AreaDetails?.Name ?? string.Empty;

            if (worldAreaId.StartsWith("ExpeditionLogBook", StringComparison.OrdinalIgnoreCase) ||
                worldAreaId.StartsWith("ExpeditionSubArea", StringComparison.OrdinalIgnoreCase) ||
                worldAreaId.Contains("ExpeditionLogBook", StringComparison.OrdinalIgnoreCase) ||
                worldAreaName.Contains("Logbook", StringComparison.OrdinalIgnoreCase) ||
                worldAreaName.Contains("Grand Expedition", StringComparison.OrdinalIgnoreCase))
            {
                isGrand = true;
            }

            if (area?.AreaMods != null)
            {
                foreach (var mod in area.AreaMods)
                {
                    var text = $"{mod.DisplayName} {mod.RawName}";

                    // Check for Grand Expedition
                    if (text.Contains("Grand Expedition", StringComparison.OrdinalIgnoreCase) ||
                        text.Contains("Große Expedition", StringComparison.OrdinalIgnoreCase) ||
                        text.Contains("Logbook", StringComparison.OrdinalIgnoreCase))
                    {
                        isGrand = true;
                    }

                    // Check for increased number of Expedition Explosives
                    // e.g. "42% increased number of Expedition Explosives"
                    if (text.Contains("Expedition Explosive", StringComparison.OrdinalIgnoreCase) &&
                        (text.Contains("number", StringComparison.OrdinalIgnoreCase) ||
                         text.Contains("increased", StringComparison.OrdinalIgnoreCase) ||
                         text.Contains("additional", StringComparison.OrdinalIgnoreCase)) &&
                        !text.Contains("Radius", StringComparison.OrdinalIgnoreCase) &&
                        !text.Contains("Area of Effect", StringComparison.OrdinalIgnoreCase) &&
                        !text.Contains("Placement", StringComparison.OrdinalIgnoreCase) &&
                        !text.Contains("Range", StringComparison.OrdinalIgnoreCase))
                    {
                        var val = ExtractModValue(mod);
                        if (val != 0f)
                        {
                            increasedExplosivesPct += val;
                        }
                    }

                    // Check for increased Expedition Explosive Radius / Area of Effect
                    if (text.Contains("Expedition Explosive Radius", StringComparison.OrdinalIgnoreCase) ||
                        text.Contains("radius of Expedition Explosives", StringComparison.OrdinalIgnoreCase) ||
                        text.Contains("area of effect of Expedition Explosives", StringComparison.OrdinalIgnoreCase) ||
                        (text.Contains("Expedition", StringComparison.OrdinalIgnoreCase) && text.Contains("Explosive", StringComparison.OrdinalIgnoreCase) && (text.Contains("Radius", StringComparison.OrdinalIgnoreCase) || text.Contains("Area of Effect", StringComparison.OrdinalIgnoreCase))))
                    {
                        var val = ExtractModValue(mod);
                        if (val != 0f)
                        {
                            increasedRadiusPct += val;
                        }
                    }
                }
            }

            int baseCount = isGrand ? GrandBaseExplosives : RegularBaseExplosives;
            float baseRadius = isGrand ? GrandRadiusWorld : RegularRadiusWorld;
            float baseReach = isGrand ? GrandPlacementReachWorld : RegularPlacementReachWorld;

            // PoE Rule: Integer counts and stats are ALWAYS floored (Math.Floor / ปัดเศษลงเสมอ)
            int finalCount = (int)Math.Floor(baseCount * (1.0 + (increasedExplosivesPct / 100.0)));
            float finalRadius = baseRadius * (1f + (increasedRadiusPct / 100f));
            float finalReach = baseReach;

            return new ExpeditionConfig(
                isGrand,
                finalCount,
                finalRadius,
                finalRadius / gridToWorld,
                finalReach,
                finalReach / gridToWorld,
                increasedExplosivesPct,
                increasedRadiusPct);
        }

        /// <summary>
        ///     Path of placed Expedition explosive entities.
        /// </summary>
        public const string ExplosivePath = "Metadata/MiscellaneousObjects/Expedition/ExpeditionExplosive";

        /// <summary>
        ///     Path of Expedition 2 encounter (Remnant pillar / Monolith) entities.
        /// </summary>
        public const string EncounterPath = "Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter";

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, (System.Numerics.Vector3 Pos, Entity? EntityRef)> TrackedExplosives = new();
        private static string lastTrackedAreaHash = string.Empty;

        /// <summary>
        ///     Clears cached explosive positions. Called on area transitions.
        /// </summary>
        public static void ClearTrackedExplosives()
        {
            TrackedExplosives.Clear();
        }

        /// <summary>
        ///     Finds all placed Expedition explosive entities currently in the area.
        ///     Persists explosive locations so distant explosives remain tracked when out of proximity bubble.
        /// </summary>
        /// <param name="area">AreaInstance to inspect, or null to query the current area.</param>
        /// <returns>A list of placed explosive entities.</returns>
        public static List<Entity> GetPlacedExplosives(AreaInstance? area = null)
        {
            if (area == null)
            {
                area = Core.States.InGameStateObject?.CurrentAreaInstance;
            }

            var currentHash = area?.AreaHash ?? string.Empty;
            if (!string.Equals(currentHash, lastTrackedAreaHash, StringComparison.Ordinal))
            {
                lastTrackedAreaHash = currentHash;
                TrackedExplosives.Clear();
            }

            var results = new List<Entity>();
            if (area?.AwakeEntities != null)
            {
                foreach (var entity in area.AwakeEntities.Values)
                {
                    if (entity != null && entity.IsValid && IsExplosiveEntity(entity.Path))
                    {
                        results.Add(entity);
                        if (entity.TryGetComponent<Render>(out var r, false) && r != null)
                        {
                            TrackedExplosives[entity.Id] = (new System.Numerics.Vector3(r.WorldPosition.X, r.WorldPosition.Y, r.TerrainHeight), entity);
                        }
                    }
                }
            }

            // Also scan sleeping entities — placed explosives may appear there during placement phase
            if (area?.SleepingEntities != null)
            {
                foreach (var entity in area.SleepingEntities.Values)
                {
                    if (entity != null && entity.IsValid && IsExplosiveEntity(entity.Path))
                    {
                        results.Add(entity);
                        if (entity.TryGetComponent<Render>(out var r, false) && r != null)
                        {
                            TrackedExplosives[entity.Id] = (new System.Numerics.Vector3(r.WorldPosition.X, r.WorldPosition.Y, r.TerrainHeight), entity);
                        }
                    }
                }
            }

            // Synchronize TrackedExplosives: only prune explosives that are within the player's wake bubble (~1200 W)
            // and genuinely missing (undone/removed by player). Keep distant explosives tracked across the area.
            if (!TrackedExplosives.IsEmpty)
            {
                var liveIds = new HashSet<uint>(results.Select(e => e.Id));
                var playerRender = area?.Player?.TryGetComponent<Render>(out var pr, false) == true ? pr : null;
                var playerPos = playerRender != null ? new System.Numerics.Vector3(playerRender.WorldPosition.X, playerRender.WorldPosition.Y, playerRender.TerrainHeight) : (System.Numerics.Vector3?)null;

                foreach (var (id, (ePos, _)) in TrackedExplosives)
                {
                    if (!liveIds.Contains(id))
                    {
                        if (playerPos.HasValue)
                        {
                            var dx = playerPos.Value.X - ePos.X;
                            var dy = playerPos.Value.Y - ePos.Y;
                            var distToPlayer = MathF.Sqrt(dx * dx + dy * dy);
                            if (distToPlayer < 1200f)
                            {
                                TrackedExplosives.TryRemove(id, out _);
                            }
                        }
                    }
                }
            }

            return results;
        }

        /// <summary>
        ///     Checks whether there are any placed explosives in the current area (live or persistently tracked).
        /// </summary>
        public static bool HasPlacedExplosives(AreaInstance? area = null)
        {
            var explosives = GetPlacedExplosives(area);
            return explosives.Count > 0 || !TrackedExplosives.IsEmpty;
        }

        /// <summary>
        ///     Checks whether the entity path matches any known Expedition explosive path variants.
        ///     Explicitly excludes the detonator, fuse wire, connector poles, markers, and encounters.
        /// </summary>
        private static bool IsExplosiveEntity(string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            // Must NOT be a fuse wire, detonator plunger, connector pole, indicator, marker, relic, or encounter
            if (path.Contains("Fuse", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("Detonator", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("ConnectorPole", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("PlacementIndicator", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("Marker", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("Relic", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("Encounter", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Match real placed explosive bomb: ExpeditionExplosive, ExpeditionDynamite, Expedition2Explosive, etc.
            return path.Contains("ExpeditionExplosive", StringComparison.OrdinalIgnoreCase) ||
                   path.Contains("ExpeditionDynamite", StringComparison.OrdinalIgnoreCase) ||
                   (path.Contains("Expedition", StringComparison.OrdinalIgnoreCase) &&
                    path.Contains("Explosive", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        ///     Calculates the explosive coverage for a target world position.
        ///     Uses the dynamic blast radius calculated from map modifiers.
        ///     Evaluates against both active explosive entities and persistently tracked explosive locations.
        /// </summary>
        /// <param name="targetPos">The target world position to test.</param>
        /// <param name="area">The active AreaInstance, or null to use the current area.</param>
        /// <param name="customRadiusWorld">Optional custom radius in World Units. If null, calculates from map modifiers.</param>
        /// <returns>Authoritative explosive placement coverage details.</returns>
        public static ExpeditionExplosiveCoverage CalculateCoverage(System.Numerics.Vector3 targetPos, AreaInstance? area = null, float? customRadiusWorld = null)
        {
            if (targetPos == System.Numerics.Vector3.Zero)
            {
                return ExpeditionExplosiveCoverage.None();
            }

            if (area == null)
            {
                area = Core.States.InGameStateObject?.CurrentAreaInstance;
            }

            var config = GetConfig(area);
            var radiusWorld = customRadiusWorld ?? config.ExplosionRadiusWorld;
            var gridConvertor = area?.WorldToGridConvertor ?? 10.869565f;
            if (gridConvertor <= 0f)
            {
                gridConvertor = 10.869565f;
            }

            var radiusGrid = radiusWorld / gridConvertor;
            var effectiveRadius = radiusWorld * CoverageToleranceFactor;

            var explosives = GetPlacedExplosives(area);
            if (explosives.Count == 0 && TrackedExplosives.IsEmpty)
            {
                return ExpeditionExplosiveCoverage.None(radiusWorld, radiusGrid);
            }

            float closestDist = float.MaxValue;
            Entity? closestEntity = null;
            uint closestId = 0;

            int coveringCount = 0;
            float bestCoveringDist = float.MaxValue;
            Entity? bestCoveringEntity = null;
            uint bestCoveringId = 0;

            var checkedIds = new HashSet<uint>();

            // 1. Check live explosive entities
            for (int i = 0; i < explosives.Count; i++)
            {
                var exp = explosives[i];
                if (exp == null) continue;
                checkedIds.Add(exp.Id);

                if (!exp.TryGetComponent<Render>(out var expRender, false) || expRender == null) continue;
                var dx = targetPos.X - expRender.WorldPosition.X;
                var dy = targetPos.Y - expRender.WorldPosition.Y;
                var dist = MathF.Sqrt(dx * dx + dy * dy);

                if (dist < closestDist)
                {
                    closestDist = dist;
                    closestEntity = exp;
                    closestId = exp.Id;
                }

                if (dist <= effectiveRadius)
                {
                    coveringCount++;
                    if (dist < bestCoveringDist)
                    {
                        bestCoveringDist = dist;
                        bestCoveringEntity = exp;
                        bestCoveringId = exp.Id;
                    }
                }
            }

            // 2. Check persistently tracked explosive locations (handles explosives that fell out of proximity bubble)
            foreach (var (expId, (ePos, eRef)) in TrackedExplosives)
            {
                if (checkedIds.Contains(expId)) continue;

                var dx = targetPos.X - ePos.X;
                var dy = targetPos.Y - ePos.Y;
                var dist = MathF.Sqrt(dx * dx + dy * dy);

                if (dist < closestDist)
                {
                    closestDist = dist;
                    closestEntity = eRef;
                    closestId = expId;
                }

                if (dist <= effectiveRadius)
                {
                    coveringCount++;
                    if (dist < bestCoveringDist)
                    {
                        bestCoveringDist = dist;
                        bestCoveringEntity = eRef;
                        bestCoveringId = expId;
                    }
                }
            }

            bool isCovered = coveringCount > 0;
            var finalDist = isCovered ? bestCoveringDist : closestDist;
            int totalExplosiveCount = Math.Max(explosives.Count, TrackedExplosives.Count);

            return new ExpeditionExplosiveCoverage(
                isCovered,
                bestCoveringId,
                bestCoveringEntity,
                finalDist,
                finalDist < float.MaxValue ? finalDist / gridConvertor : float.MaxValue,
                radiusWorld,
                radiusGrid,
                coveringCount,
                totalExplosiveCount,
                closestDist,
                closestId,
                closestEntity);
        }

        /// <summary>
        ///     Calculates the explosive coverage for a specific target entity (e.g. Expedition2Encounter).
        ///     Uses the dynamic blast radius calculated from map modifiers.
        ///     Evaluates against both active explosive entities and persistently tracked explosive locations.
        /// </summary>
        /// <param name="target">The target entity to test.</param>
        /// <param name="area">The active AreaInstance, or null to use the current area.</param>
        /// <param name="customRadiusWorld">Optional custom radius in World Units. If null, calculates from map modifiers.</param>
        /// <returns>Authoritative explosive placement coverage details.</returns>
        public static ExpeditionExplosiveCoverage CalculateCoverage(Entity target, AreaInstance? area = null, float? customRadiusWorld = null)
        {
            if (target == null || !target.IsValid)
            {
                return ExpeditionExplosiveCoverage.None();
            }

            if (target.TryGetComponent<Render>(out var r, false) && r != null)
            {
                return CalculateCoverage(new System.Numerics.Vector3(r.WorldPosition.X, r.WorldPosition.Y, r.TerrainHeight), area, customRadiusWorld);
            }

            return ExpeditionExplosiveCoverage.None();
        }
    }
}
