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
        /// </summary>
        public const float PillarPhysicalRadius = 14.0f;

        /// <summary>
        /// Effective collision radius around a pillar for fuse wire routing and peg placement.
        /// In-game connector pegs are dropped on open ground outside the pillar base (~18 units from center).
        /// </summary>
        public const float PillarEffectiveRadius = 18.0f;

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
        /// </summary>
        public static bool IntersectsObstacle(Vector2 a, Vector2 b, Vector2 center, float radius, out float penetrationDepth)
        {
            penetrationDepth = 0f;
            var ab = b - a;
            var abLenSq = ab.LengthSquared();
            if (abLenSq < 1e-4f)
            {
                var d = Vector2.Distance(a, center);
                if (d < radius)
                {
                    penetrationDepth = radius - d;
                    return true;
                }
                return false;
            }

            var t = Math.Clamp(Vector2.Dot(center - a, ab) / abLenSq, 0f, 1f);
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
        /// Computes the exact convex hull detour distance for a wire wrapping around a circular obstacle.
        /// Consists of tangent from A to circle, circular arc along obstacle boundary, tangent from circle to B,
        /// plus discrete peg spacing overhead.
        /// </summary>
        public static float ComputeCircleDetourDistance(Vector2 a, Vector2 b, Vector2 c, float radius)
        {
            var da = Vector2.Distance(a, c);
            var db = Vector2.Distance(b, c);

            // If either point is inside or very close to the obstacle radius, wire cannot enter
            if (da <= radius || db <= radius)
            {
                return 9999.0f;
            }

            var la = MathF.Sqrt((da * da) - (radius * radius));
            var lb = MathF.Sqrt((db * db) - (radius * radius));

            var vA = a - c;
            var vB = b - c;

            // Angular difference between A and B around obstacle center C
            var dot = Math.Clamp(Vector2.Dot(vA, vB) / (da * db), -1.0f, 1.0f);
            var deltaTheta = MathF.Acos(dot);

            // Tangent angles
            var alphaA = MathF.Acos(radius / da);
            var alphaB = MathF.Acos(radius / db);

            var arcAngle = deltaTheta - (alphaA + alphaB);
            if (arcAngle <= 0f)
            {
                // Line of sight tangents do not cross the circle core
                return Vector2.Distance(a, b);
            }

            var arcLength = radius * arcAngle;
            var continuousDetour = la + lb + arcLength;

            // The game drops connector poles (pegs) around obstacles,
            // adding cornering slack and peg spacing overhead (~12% + 4 units)
            return (continuousDetour * 1.12f) + 4.0f;
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
            IEnumerable<ExpeditionTarget>? obstacles = null)
        {
            // 1. Grid boundary check
            if (area != null && area.GridHeightData.Length > 0)
            {
                var gy = (int)candidateGrid.Y;
                var gx = (int)candidateGrid.X;
                if (gy >= 0 && gy < area.GridHeightData.Length)
                {
                    var row = area.GridHeightData[gy];
                    if (row != null && gx >= 0 && gx < row.Length)
                    {
                        // Candidate point must not be placed inside void
                        if (!IsCellWalkable(area, gx, gy))
                        {
                            return false;
                        }
                    }
                }
            }

            // 2. Check distance from obstacles (do not place bomb inside a pillar base)
            var cand2D = new Vector2(candidateGrid.X, candidateGrid.Y);
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

            // 3. Fuse reach check from anchor
            if (anchorGrid.HasValue)
            {
                var start2D = new Vector2(anchorGrid.Value.X, anchorGrid.Value.Y);
                var directDist = Vector2.Distance(start2D, cand2D);

                if (directDist < 4.0f) return false; // Too close to existing bomb

                // Direct Euclidean distance must have reasonable headroom
                if (directDist > settings.MaxPlacementRangeGrid - 2.0f) return false;

                var effectiveDist = CalculateEffectiveDistance(start2D, cand2D, obstacles, area, out var obstructed);

                // Allow placements up to MaxPlacementRangeGrid
                var maxAllowed = obstructed
                    ? MathF.Max(70.0f, settings.MaxPlacementRangeGrid - 10.0f)
                    : settings.MaxPlacementRangeGrid;

                if (effectiveDist > maxAllowed)
                {
                    return false; // Fuse will NOT reach after bending around obstacles!
                }
            }

            return true;
        }
    }
}
