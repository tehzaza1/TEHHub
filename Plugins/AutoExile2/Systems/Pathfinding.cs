// <copyright file="Pathfinding.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Systems
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Numerics;
    using System.Runtime.CompilerServices;

    /// <summary>
    /// A* pathfinding, line-of-sight path smoothing, and terrain clearance navigation.
    /// Ported directly from AutoExile and optimized specifically for GameHelper PoE 2.
    ///
    /// Operates natively on GameHelper's nibble-packed GridWalkableData (byte[]) with zero heap allocations.
    /// Grid values: 0 = impassable/wall, 1-2 = wall fringe, 3-5 = walkable with cost = 6 - value.
    /// All positions are in grid coordinates. Convert to world only at camera/input call sites.
    /// </summary>
    public static class Pathfinding
    {
        public const float GridToWorld = 10.88f;
        public const float WorldToGrid = 1f / GridToWorld;

        /// <summary>
        /// Conservative network bubble radius in grid units.
        /// Entities enter the client entity list at ~200-215 grid; 180 is a safe "seen" threshold.
        /// </summary>
        public const float NetworkBubbleRadius = 180f;

        // 8-directional movement: cardinal + diagonal (Matching AutoExile)
        private static readonly (int dx, int dy, float baseCost)[] Neighbors =
        {
            ( 1,  0, 1.0f),
            (-1,  0, 1.0f),
            ( 0,  1, 1.0f),
            ( 0, -1, 1.0f),
            ( 1,  1, 1.414f),
            ( 1, -1, 1.414f),
            (-1,  1, 1.414f),
            (-1, -1, 1.414f),
        };

        // =========================================================================
        // Coordinate & Elevation Helpers
        // =========================================================================

        public static Vector2 GridToWorldPos(int gx, int gy) =>
            new(gx * GridToWorld, gy * GridToWorld);

        public static Vector2 GridToWorldPos(Vector2 gridPos) =>
            gridPos * GridToWorld;

        public static (int x, int y) WorldToGridPos(Vector2 world) =>
            ((int)(world.X * WorldToGrid), (int)(world.Y * WorldToGrid));

        public static Vector2 WorldToGridPosVec(Vector2 world) =>
            world * WorldToGrid;

        /// <summary>
        /// Convert grid position to world Vector3 using GameHelper's terrain elevation lookup.
        /// </summary>
        public static Vector3 GridToWorld3D(Vector2 gridPos, float[][]? heightGrid = null, float fallbackZ = 0f)
        {
            var gx = (int)gridPos.X;
            var gy = (int)gridPos.Y;
            var z = GetTerrainHeight(heightGrid, gx, gy, fallbackZ);
            return new Vector3(gridPos.X * GridToWorld, gridPos.Y * GridToWorld, z);
        }

        /// <summary>
        /// Get terrain height at a grid position from GameHelper's GridHeightData.
        /// Returns fallbackZ if height data is unavailable or position is out of bounds.
        /// </summary>
        public static float GetTerrainHeight(float[][]? heightGrid, int gx, int gy, float fallbackZ)
        {
            if (heightGrid == null || gy < 0 || gy >= heightGrid.Length)
            {
                return fallbackZ;
            }

            var row = heightGrid[gy];
            if (row == null || gx < 0 || gx >= row.Length)
            {
                return fallbackZ;
            }

            return row[gx];
        }

        // =========================================================================
        // GameHelper Nibble-Encoded Cell Accessors
        // =========================================================================

        /// <summary>
        /// Reads the terrain cell value from GameHelper's nibble-encoded GridWalkableData.
        /// (0 = wall, 1-2 = edge fringe, 3-5 = walkable).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetCellValue(byte[]? walkableData, int bytesPerRow, int x, int y)
        {
            if (walkableData == null || bytesPerRow <= 0 || x < 0 || y < 0)
            {
                return 0;
            }

            int byteIndex = (y * bytesPerRow) + (x / 2);
            if ((uint)byteIndex >= (uint)walkableData.Length)
            {
                return 0;
            }

            int shift = ((x & 1) == 0) ? 0 : 4;
            return (walkableData[byteIndex] >> shift) & 0xF;
        }

        /// <summary>
        /// Checks if a cell is walkable with minimum value threshold.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsWalkable(byte[]? walkableData, int bytesPerRow, int x, int y, int minWalkable = 2)
        {
            return GetCellValue(walkableData, bytesPerRow, x, y) >= minWalkable;
        }

        /// <summary>
        /// Check if a grid cell is solidly walkable (pathfinding value >= 3).
        /// Values 1-2 are wall fringe cells that cause player clipping.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsWalkableCell(byte[]? walkableData, int bytesPerRow, int gx, int gy)
        {
            return GetCellValue(walkableData, bytesPerRow, gx, gy) >= 3;
        }

        /// <summary>
        /// Find the nearest walkable cell (pf >= 3) within searchRadius of the given grid position.
        /// Returns null if nothing walkable found. Searches in expanding rings.
        /// </summary>
        public static (int x, int y)? FindNearestWalkableCell(byte[]? walkableData, int bytesPerRow, int gx, int gy, int searchRadius = 10)
        {
            if (IsWalkableCell(walkableData, bytesPerRow, gx, gy))
            {
                return (gx, gy);
            }

            for (int r = 1; r <= searchRadius; r++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    for (int dy = -r; dy <= r; dy++)
                    {
                        if (Math.Abs(dx) != r && Math.Abs(dy) != r) continue; // ring only
                        if (IsWalkableCell(walkableData, bytesPerRow, gx + dx, gy + dy))
                        {
                            return (gx + dx, gy + dy);
                        }
                    }
                }
            }

            return null;
        }

        // =========================================================================
        // Primary Pathfinding (FindPath)
        // =========================================================================

        /// <summary>
        /// Run A* from start to goal on GameHelper's walkable grid.
        /// Incorporates AutoExile's weighted terrain costing, spiral fallback, and LOS smoothing.
        /// Returns smoothed, merged path of grid positions, or empty if no path.
        /// </summary>
        public static List<Vector2> FindPath(
            byte[]? walkableData,
            int bytesPerRow,
            Vector2 gridStart,
            Vector2 gridEnd,
            int maxNodes = 200000,
            bool flatCost = false,
            HashSet<Vector2>? dynamicObstacles = null)
        {
            if (walkableData == null || bytesPerRow <= 0)
            {
                return new List<Vector2>();
            }

            int rows = walkableData.Length / bytesPerRow;
            int cols = bytesPerRow * 2;

            int sx = Math.Clamp((int)gridStart.X, 0, cols - 1);
            int sy = Math.Clamp((int)gridStart.Y, 0, rows - 1);
            int gx = Math.Clamp((int)gridEnd.X, 0, cols - 1);
            int gy = Math.Clamp((int)gridEnd.Y, 0, rows - 1);

            // Spiral search fallback if start or goal is on wall boundary/fringe
            if (GetCellValue(walkableData, bytesPerRow, sx, sy) <= 1)
            {
                (sx, sy) = FindNearestWalkable(walkableData, bytesPerRow, sx, sy, rows, cols);
            }

            if (GetCellValue(walkableData, bytesPerRow, gx, gy) <= 1)
            {
                (gx, gy) = FindNearestWalkable(walkableData, bytesPerRow, gx, gy, rows, cols);
            }

            if (GetCellValue(walkableData, bytesPerRow, sx, sy) <= 1 || GetCellValue(walkableData, bytesPerRow, gx, gy) <= 1)
            {
                return new List<Vector2>();
            }

            var open = new PriorityQueue<(int x, int y), float>();
            var gScore = new Dictionary<(int, int), float>();
            var cameFrom = new Dictionary<(int, int), (int x, int y)>();

            gScore[(sx, sy)] = 0;
            open.Enqueue((sx, sy), Heuristic(sx, sy, gx, gy));

            int explored = 0;

            while (open.Count > 0 && explored < maxNodes)
            {
                var (cx, cy) = open.Dequeue();
                explored++;

                if (cx == gx && cy == gy)
                {
                    var rawPositions = ReconstructPath(cameFrom, sx, sy, gx, gy);
                    var smoothed = SmoothPath(walkableData, bytesPerRow, rawPositions, 4, dynamicObstacles);
                    return MergeCloseWaypoints(walkableData, bytesPerRow, smoothed, 8f, 3, dynamicObstacles);
                }

                float currentG = gScore.GetValueOrDefault((cx, cy), float.MaxValue);
                if (currentG == float.MaxValue)
                {
                    continue;
                }

                foreach (var (dx, dy, baseCost) in Neighbors)
                {
                    int nx = cx + dx;
                    int ny = cy + dy;

                    if (nx < 0 || nx >= cols || ny < 0 || ny >= rows)
                    {
                        continue;
                    }

                    int cellValue = GetCellValue(walkableData, bytesPerRow, nx, ny);
                    if (cellValue <= 1 || (dynamicObstacles != null && dynamicObstacles.Contains(new Vector2(nx, ny))))
                    {
                        continue;
                    }

                    // AutoExile weighted cost formula: prefers open terrain over narrow/fringe edges
                    float moveCost = flatCost ? baseCost : baseCost * (6 - Math.Min(cellValue, 5));
                    float tentativeG = currentG + moveCost;
                    var key = (nx, ny);

                    if (!gScore.TryGetValue(key, out float existingG) || tentativeG < existingG)
                    {
                        gScore[key] = tentativeG;
                        cameFrom[key] = (cx, cy);
                        open.Enqueue(key, tentativeG + Heuristic(nx, ny, gx, gy));
                    }
                }
            }

            return new List<Vector2>();
        }

        // =========================================================================
        // Path Smoothing & Waypoint Merging
        // =========================================================================

        /// <summary>
        /// Simplify a grid path by removing intermediate points that are on a straight line.
        /// Uses line-of-sight checks on the grid to skip unnecessary waypoints.
        /// Limits max segment length to 100f to avoid wall clipping.
        /// </summary>
        public static List<Vector2> SmoothPath(
            byte[]? walkableData,
            int bytesPerRow,
            List<Vector2> path,
            int minWalkable = 4,
            HashSet<Vector2>? dynamicObstacles = null)
        {
            if (walkableData == null || bytesPerRow <= 0 || path.Count <= 2)
            {
                return path;
            }

            const float maxSegmentLength = 100f;
            int rows = walkableData.Length / bytesPerRow;
            int cols = bytesPerRow * 2;

            var result = new List<Vector2> { path[0] };
            int current = 0;

            while (current < path.Count - 1)
            {
                int farthest = current + 1;
                for (int i = path.Count - 1; i > current; i--)
                {
                    if (Vector2.Distance(path[current], path[i]) > maxSegmentLength)
                    {
                        continue;
                    }

                    if (HasLineOfSight(walkableData, bytesPerRow, path[current], path[i], rows, cols, minWalkable, dynamicObstacles))
                    {
                        farthest = i;
                        break;
                    }
                }

                result.Add(path[farthest]);
                current = farthest;
            }

            return result;
        }

        /// <summary>
        /// Collapse consecutive walk waypoints that are close together.
        /// Eliminates micro-stutter on staircases and narrow ramps.
        /// </summary>
        public static List<Vector2> MergeCloseWaypoints(
            byte[]? walkableData,
            int bytesPerRow,
            List<Vector2> path,
            float mergeThreshold = 8f,
            int minWalkable = 3,
            HashSet<Vector2>? dynamicObstacles = null)
        {
            if (walkableData == null || bytesPerRow <= 0 || path.Count <= 2)
            {
                return path;
            }

            int rows = walkableData.Length / bytesPerRow;
            int cols = bytesPerRow * 2;
            var result = new List<Vector2> { path[0] };

            for (int i = 1; i < path.Count; i++)
            {
                var wp = path[i];
                var prev = result[result.Count - 1];
                bool isLast = i == path.Count - 1;

                if (isLast)
                {
                    result.Add(wp);
                    continue;
                }

                float dist = Vector2.Distance(prev, wp);
                if (dist < mergeThreshold)
                {
                    int lookAhead = Math.Min(i + 1, path.Count - 1);
                    if (HasLineOfSight(walkableData, bytesPerRow, prev, path[lookAhead], rows, cols, minWalkable, dynamicObstacles))
                    {
                        continue;
                    }
                }

                result.Add(wp);
            }

            return result;
        }

        // =========================================================================
        // Line-of-Sight Check (Bresenham Raycast)
        // =========================================================================

        /// <summary>
        /// Check walkable line of sight between two positions using Bresenham raycast.
        /// </summary>
        public static bool HasLineOfSight(
            byte[]? walkableData,
            int bytesPerRow,
            Vector2 a,
            Vector2 b,
            int rows,
            int cols,
            int minWalkable = 4,
            HashSet<Vector2>? dynamicObstacles = null)
        {
            if (walkableData == null || bytesPerRow <= 0) return false;

            int ax = (int)a.X;
            int ay = (int)a.Y;
            int bx = (int)b.X;
            int by = (int)b.Y;

            int dx = Math.Abs(bx - ax);
            int dy = Math.Abs(by - ay);
            int sx = ax < bx ? 1 : -1;
            int sy = ay < by ? 1 : -1;
            int err = dx - dy;

            int cx = ax;
            int cy = ay;

            while (cx != bx || cy != by)
            {
                if (cx < 0 || cx >= cols || cy < 0 || cy >= rows)
                {
                    return false;
                }

                if (GetCellValue(walkableData, bytesPerRow, cx, cy) < minWalkable)
                {
                    return false;
                }

                if (dynamicObstacles != null && dynamicObstacles.Contains(new Vector2(cx, cy)))
                {
                    return false;
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

            return true;
        }

        // =========================================================================
        // Nearest Walkable Spiral Fallback & Path Reconstruction
        // =========================================================================

        /// <summary>
        /// Spiral search for nearest walkable cell within radius 60.
        /// </summary>
        public static (int x, int y) FindNearestWalkable(
            byte[] walkableData,
            int bytesPerRow,
            int x,
            int y,
            int rows,
            int cols)
        {
            for (int radius = 1; radius <= 60; radius++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    for (int dy = -radius; dy <= radius; dy++)
                    {
                        if (Math.Abs(dx) != radius && Math.Abs(dy) != radius)
                        {
                            continue;
                        }

                        int nx = x + dx;
                        int ny = y + dy;
                        if (nx >= 0 && nx < cols && ny >= 0 && ny < rows && GetCellValue(walkableData, bytesPerRow, nx, ny) >= 2)
                        {
                            return (nx, ny);
                        }
                    }
                }
            }

            return (x, y);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float Heuristic(int ax, int ay, int bx, int by)
        {
            int dx = Math.Abs(ax - bx);
            int dy = Math.Abs(ay - by);
            return Math.Max(dx, dy) + (0.414f * Math.Min(dx, dy));
        }

        public static List<Vector2> ReconstructPath(
            Dictionary<(int, int), (int x, int y)> cameFrom,
            int sx,
            int sy,
            int gx,
            int gy)
        {
            var path = new List<(int x, int y)>();
            var current = (gx, gy);

            while (current != (sx, sy))
            {
                path.Add(current);
                current = cameFrom[current];
            }

            path.Add((sx, sy));
            path.Reverse();

            return path.Select(p => new Vector2(p.x, p.y)).ToList();
        }
    }
}
