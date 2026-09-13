namespace ExpeditionPlanner
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;
    using TEHhub.RemoteObjects.States.InGameStateObjects;

    public static class LegalPlacement
    {
        /// <summary>
        /// Physical footprint radius around a Remnant pillar or Sentinel where a bomb cannot be placed.
        /// In PoE 2 grid coordinates, the base collision is ~4.5 units (~50 world units).
        /// </summary>
        public const float PillarPhysicalRadius = 4.5f;

        /// <summary>
        /// Effective collision radius around a pillar for fuse wire routing and peg placement.
        /// </summary>
        public const float PillarEffectiveRadius = 7.0f;

        /// <summary>
        /// Minimum grid distance between any two bomb placements (including already-planned bombs).
        /// Bombs closer than this will overlap blast radii wastefully and confuse the wire path.
        /// </summary>
        public const float MinBombSeparationGrid = 22.0f;

        /// <summary>
        /// Checks whether a single grid cell is walkable using AreaInstance.GridWalkableData.
        /// (0 = blocked, 1-5 = walkable).
        /// </summary>
        public static bool IsCellWalkable(AreaInstance? area, int gx, int gy)
        {
            if (area == null) return true;
            var data = area.GridWalkableData;
            var bpr = area.TerrainMetadata.BytesPerRow;
            if (data == null || data.Length == 0 || bpr <= 0 || gx < 0 || gy < 0) return true;

            var byteIndex = (gy * bpr) + (gx / 2);
            if ((uint)byteIndex >= (uint)data.Length) return false;

            var shift = ((gx & 1) == 0) ? 0 : 4;
            var val = (data[byteIndex] >> shift) & 0xF;
            return val != 0;
        }

        /// <summary>
        /// Checks whether a candidate bomb position has physical clearance from unwalkable terrain
        /// (such as the campsite tent, wagon, cliff walls, or rocks) using AreaInstance.GridWalkableData.
        /// An explosive has a physical collision footprint of ~4.5 - 6.0 grid units.
        /// </summary>
        public static bool HasWalkableClearance(AreaInstance? area, float gx, float gy, float clearanceRadius = 5.0f)
        {
            if (area == null) return true;
            int r = (int)MathF.Ceiling(clearanceRadius);
            int cx = (int)MathF.Round(gx);
            int cy = (int)MathF.Round(gy);
            float rSq = clearanceRadius * clearanceRadius;

            for (int dy = -r; dy <= r; dy++)
            {
                int y = cy + dy;
                for (int dx = -r; dx <= r; dx++)
                {
                    if ((dx * dx) + (dy * dy) <= rSq)
                    {
                        int x = cx + dx;
                        if (!IsCellWalkable(area, x, y))
                        {
                            return false; // Physical footprint clips into unwalkable terrain (tent, wagon, rock)!
                        }
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// Bresenham raycast to check if straight line between two grid positions is clear of terrain walls.
        /// </summary>
        public static bool IsLineClearOfTerrain(AreaInstance? area, Vector2 start, Vector2 end, out int blockedCells)
        {
            blockedCells = 0;
            if (area == null) return true;
            var data = area.GridWalkableData;
            var bpr = area.TerrainMetadata.BytesPerRow;
            if (data == null || data.Length == 0 || bpr <= 0) return true;

            int x0 = (int)start.X;
            int y0 = (int)start.Y;
            int x1 = (int)end.X;
            int y1 = (int)end.Y;

            int dx = Math.Abs(x1 - x0);
            int dy = Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1;
            int sy = y0 < y1 ? 1 : -1;
            int err = dx - dy;

            int cx = x0;
            int cy = y0;

            while (cx != x1 || cy != y1)
            {
                if (!IsCellWalkable(area, cx, cy))
                {
                    blockedCells++;
                }

                int e2 = 2 * err;
                if (e2 > -dy)
                {
                    err -= dy;
                    cx += sx;
                }
                if (e2 < dx)
                {
                    err += dx;
                    cy += sy;
                }
            }

            // Line is strictly clear if no blocked cells encountered
            return blockedCells == 0;
        }

        /// <summary>
        /// Determines if the line segment from <paramref name="a"/> to <paramref name="b"/>
        /// penetrates a circular obstacle with center <paramref name="center"/> and radius <paramref name="radius"/>.
        /// Obstacles lying behind <paramref name="a"/> or beyond <paramref name="b"/> do NOT obstruct the segment.
        /// </summary>
        public static bool IntersectsObstacle(Vector2 a, Vector2 b, Vector2 center, float radius, out float penetrationDepth)
        {
            penetrationDepth = 0f;
            var ab = b - a;
            var abLenSq = ab.LengthSquared();
            if (abLenSq < 1e-4f)
            {
                return false;
            }

            // Obstacle center must project strictly between a and b along the line segment
            var t = Vector2.Dot(center - a, ab) / abLenSq;
            if (t <= 0.05f || t >= 0.95f)
            {
                // Obstacle is behind start point or beyond end point
                return false;
            }

            var closest = a + (ab * t);
            var dist = Vector2.Distance(closest, center);

            if (dist < radius)
            {
                penetrationDepth = radius - dist;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Computes the convex hull detour distance for a wire wrapping around a circular obstacle.
        /// Consists of tangent from A to circle, circular arc along obstacle boundary, tangent from circle to B,
        /// plus discrete peg spacing overhead.
        /// </summary>
        public static float ComputeCircleDetourDistance(Vector2 a, Vector2 b, Vector2 c, float radius)
        {
            var directDist = Vector2.Distance(a, b);
            var da = MathF.Max(radius + 0.1f, Vector2.Distance(a, c));
            var db = MathF.Max(radius + 0.1f, Vector2.Distance(b, c));

            var la = MathF.Sqrt(MathF.Max(0.01f, (da * da) - (radius * radius)));
            var lb = MathF.Sqrt(MathF.Max(0.01f, (db * db) - (radius * radius)));

            var vA = a - c;
            var vB = b - c;

            // Angular difference between A and B around obstacle center C
            var dot = Math.Clamp(Vector2.Dot(vA, vB) / (da * db), -1.0f, 1.0f);
            var deltaTheta = MathF.Acos(dot);

            // Tangent angles
            var alphaA = MathF.Acos(Math.Clamp(radius / da, -1.0f, 1.0f));
            var alphaB = MathF.Acos(Math.Clamp(radius / db, -1.0f, 1.0f));

            var arcAngle = deltaTheta - (alphaA + alphaB);
            if (arcAngle <= 0f)
            {
                // Line of sight tangents do not cross the circle core
                return directDist;
            }

            var arcLength = radius * arcAngle;
            var continuousDetour = la + lb + arcLength;

            // The game drops connector poles (pegs) around obstacles,
            // adding cornering slack and peg spacing overhead (~10% + 2 units)
            return MathF.Max(directDist, (continuousDetour * 1.10f) + 2.0f);
        }

        /// <summary>
        /// Calculates the effective fuse length taking into account pillar detour distance and terrain.
        /// If a pillar is between start and candidate, the fuse must bend around it, adding distance.
        /// </summary>
        public static float CalculateEffectiveDistance(
            Vector2 start,
            Vector2 candidate,
            IEnumerable<ExpeditionTarget>? obstacles,
            AreaInstance? area,
            out bool isLineObstructed)
        {
            var directDist = Vector2.Distance(start, candidate);
            isLineObstructed = false;
            float maxPillarDetour = directDist;

            if (obstacles != null)
            {
                foreach (var obs in obstacles)
                {
                    if (obs.Kind != TargetKind.RemnantPillar && obs.Kind != TargetKind.VerisiumSentinel)
                    {
                        continue;
                    }

                    var obsCenter = new Vector2(obs.GridPosition.X, obs.GridPosition.Y);
                    if (IntersectsObstacle(start, candidate, obsCenter, PillarEffectiveRadius, out _))
                    {
                        isLineObstructed = true;
                        float detour = ComputeCircleDetourDistance(start, candidate, obsCenter, PillarEffectiveRadius);
                        if (detour > maxPillarDetour)
                        {
                            maxPillarDetour = detour;
                        }
                    }
                }
            }

            float totalDistance = maxPillarDetour;

            // Check terrain walkability line of sight
            if (area != null && !IsLineClearOfTerrain(area, start, candidate, out var blocked))
            {
                isLineObstructed = true;
                if (blocked >= 35)
                {
                    // Impassable large mountain, cliff, or outer wall
                    return 9999.0f;
                }

                // Add minor wire slack for small doodads or tile fringes
                totalDistance += MathF.Min(20.0f, blocked * 1.5f);
            }

            return totalDistance;
        }

        /// <summary>
        /// Evaluates whether placing an explosive at <paramref name="candidateGrid"/> is legal,
        /// ensuring effective fuse reach (including detour around pillars) does not exceed MaxPlacementRangeGrid.
        /// </summary>
        public static bool IsPlaceable(
            Vector3 candidateGrid,
            Vector3? anchorGrid,
            AreaInstance? area,
            ExpeditionPlannerSettings settings,
            IEnumerable<ExpeditionTarget>? obstacles = null,
            Vector3? detonatorGrid = null,
            IReadOnlyList<Vector3>? plannedPositions = null)
        {
            // 1. Grid boundary and physical clearance check (bomb footprint clearance from tent, wagon, wall)
            if (!HasWalkableClearance(area, candidateGrid.X, candidateGrid.Y, settings.BombClearanceRadiusGrid))
            {
                return false;
            }

            var cand2D = new Vector2(candidateGrid.X, candidateGrid.Y);

            // 2. Check campsite exclusion zone: do not place bomb inside the campsite tent/wagon area near Detonator
            if (detonatorGrid.HasValue && detonatorGrid.Value.LengthSquared() > 1f)
            {
                var det2D = new Vector2(detonatorGrid.Value.X, detonatorGrid.Value.Y);
                if (Vector2.Distance(cand2D, det2D) < settings.CampExclusionRadiusGrid)
                {
                    return false; // Inside camp exclusion zone!
                }
            }

            // 3. Check distance from obstacles (do not place bomb inside a pillar base)
            if (obstacles != null)
            {
                foreach (var obs in obstacles)
                {
                    if (obs.Kind == TargetKind.RemnantPillar || obs.Kind == TargetKind.VerisiumSentinel)
                    {
                        var obs2D = new Vector2(obs.GridPosition.X, obs.GridPosition.Y);
                        if (Vector2.Distance(cand2D, obs2D) < PillarPhysicalRadius)
                        {
                            return false; // Inside solid pillar footprint
                        }
                    }
                }
            }

            // 3b. Minimum separation from all already-planned bomb positions in this route
            if (plannedPositions != null)
            {
                float minSepSq = MinBombSeparationGrid * MinBombSeparationGrid;
                foreach (var planned in plannedPositions)
                {
                    var plan2D = new Vector2(planned.X, planned.Y);
                    if (Vector2.DistanceSquared(cand2D, plan2D) < minSepSq)
                    {
                        return false; // Too close to an already-planned bomb in this route
                    }
                }
            }

            // 4. Fuse reach check from anchor
            if (anchorGrid.HasValue)
            {
                var start2D = new Vector2(anchorGrid.Value.X, anchorGrid.Value.Y);
                var directDist = Vector2.Distance(start2D, cand2D);

                if (directDist < 3.5f) return false; // Too close to existing bomb

                // Direct Euclidean distance must not exceed max placement range
                if (directDist > settings.MaxPlacementRangeGrid) return false;

                var effectiveDist = CalculateEffectiveDistance(start2D, cand2D, obstacles, area, out var obstructed);

                // Allow placements up to MaxPlacementRangeGrid (effectiveDist already accounts for detours and wire slack)
                if (effectiveDist > settings.MaxPlacementRangeGrid)
                {
                    return false; // Fuse will NOT reach after bending around obstacles!
                }
            }

            return true;
        }
    }
}
