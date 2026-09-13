// <copyright file="ExplorationMap.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Systems
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Numerics;

    /// <summary>
    /// Tracks map exploration coverage using flood-fill connected component blobs and 80x80 region graphs.
    /// Ported directly from AutoExile ExplorationMap.
    /// </summary>
    public class ExplorationMap
    {
        private static int DefaultRange => (int)Pathfinding.NetworkBubbleRadius;

        public int SeenRadiusOverride { get; set; }

        private int RenderRange => this.SeenRadiusOverride > 0 ? this.SeenRadiusOverride : DefaultRange;

        // Region chunk size — blob is divided into NxN grid chunks for navigation targeting
        private const int RegionChunkSize = 80;

        // Minimum walkable cells for a region to be worth exploring
        private const int MinRegionSize = 50;

        // Small pocket threshold — dead-end areas smaller than this are deprioritized
        private const int SmallPocketThreshold = 200;

        // Minimum pathfinding grid value to count as "real" walkable space (3-5)
        private const int MinWalkableValue = 3;

        // ── Public state ──
        public List<Blob> Blobs { get; } = new();
        public int ActiveBlobIndex { get; private set; } = -1;
        public Blob? ActiveBlob => this.ActiveBlobIndex >= 0 && this.ActiveBlobIndex < this.Blobs.Count ? this.Blobs[this.ActiveBlobIndex] : null;
        public float ActiveBlobCoverage => this.ActiveBlob?.Coverage ?? 0f;
        public float Coverage => this.ActiveBlobCoverage * 100f; // 0-100% format for UI
        public int TotalBlobCount => this.Blobs.Count;
        public bool IsInitialized => this.Blobs.Count > 0;

        // Known transition portals (grid positions where we've seen AreaTransition entities)
        public List<TransitionPortal> KnownTransitions { get; } = new();

        // Regions that pathfinding couldn't reach — skip on future targeting
        public HashSet<int> FailedRegions { get; } = new();

        public string LastAction { get; private set; } = string.Empty;
        public int TotalWalkableCells { get; private set; }

        private string currentAreaHash = string.Empty;
        private Vector2 lastUpdatePos = Vector2.Zero;
        private const float MinUpdateDistance = 5f;

        /// <summary>
        /// Mark a region or target as unreachable so GetNextExplorationTarget skips it.
        /// </summary>
        public void MarkTargetFailed(Vector2 targetGridPos)
        {
            var blob = this.ActiveBlob;
            if (blob == null)
            {
                return;
            }

            var cell = new Vector2i((int)targetGridPos.X, (int)targetGridPos.Y);
            if (blob.CellToRegion.TryGetValue(cell, out int regionIdx))
            {
                this.FailedRegions.Add(regionIdx);
                this.LastAction = $"Marked region {regionIdx} as unreachable";
            }
            else
            {
                float bestDist = float.MaxValue;
                int bestIdx = -1;
                for (int i = 0; i < blob.Regions.Count; i++)
                {
                    float d = Vector2.Distance(targetGridPos, blob.Regions[i].Center);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        bestIdx = i;
                    }
                }

                if (bestIdx >= 0)
                {
                    this.FailedRegions.Add(bestIdx);
                    this.LastAction = $"Marked region {bestIdx} as unreachable (nearest)";
                }
            }
        }

        /// <summary>
        /// Initialize from walkable terrain data. Flood fills from player position to discover
        /// the primary active blob (connected reachable space).
        /// Ported directly from AutoExile Initialize.
        /// </summary>
        public void Initialize(byte[] walkableData, int bytesPerRow, Vector2 playerGridPos, string areaHash)
        {
            if (this.currentAreaHash == areaHash && this.Blobs.Count > 0)
            {
                return;
            }

            this.Clear();

            if (walkableData == null || bytesPerRow <= 0)
            {
                return;
            }

            this.currentAreaHash = areaHash;
            int rows = walkableData.Length / bytesPerRow;
            int cols = bytesPerRow * 2;

            int px = Math.Clamp((int)playerGridPos.X, 0, cols - 1);
            int py = Math.Clamp((int)playerGridPos.Y, 0, rows - 1);

            if (Pathfinding.GetCellValue(walkableData, bytesPerRow, px, py) < MinWalkableValue)
            {
                var nearest = Pathfinding.FindNearestWalkable(walkableData, bytesPerRow, px, py, rows, cols);
                px = nearest.x;
                py = nearest.y;
            }

            var blobCells = this.FloodFill(walkableData, bytesPerRow, px, py, rows, cols);
            if (blobCells.Count == 0)
            {
                this.LastAction = "Flood fill returned 0 cells";
                return;
            }

            var blob = this.CreateBlob(blobCells, 0);
            this.Blobs.Add(blob);
            this.ActiveBlobIndex = 0;
            this.TotalWalkableCells = blobCells.Count;
            this.LastAction = $"Initialized blob 0: {blobCells.Count} cells, {blob.Regions.Count} regions";

            this.Update(playerGridPos);
        }

        /// <summary>
        /// Backward-compatible overload for existing calls.
        /// </summary>
        public void Initialize(byte[] walkableData, int bytesPerRow, string areaHash)
        {
            this.Initialize(walkableData, bytesPerRow, Vector2.Zero, areaHash);
        }

        public void Clear()
        {
            this.Blobs.Clear();
            this.ActiveBlobIndex = -1;
            this.KnownTransitions.Clear();
            this.FailedRegions.Clear();
            this.TotalWalkableCells = 0;
            this.LastAction = string.Empty;
        }

        /// <summary>
        /// Clear all seen state while preserving blob/region structure.
        /// Useful when the same area must be re-swept to find newly spawned monsters.
        /// </summary>
        public void ResetSeen()
        {
            foreach (var blob in this.Blobs)
            {
                blob.SeenCells.Clear();
                blob.Coverage = 0f;
                foreach (var region in blob.Regions)
                {
                    region.SeenCount = 0;
                }
            }

            this.FailedRegions.Clear();
        }

        /// <summary>
        /// Get the weighted centroid of the active blob (center of all walkable space).
        /// Uses region centers weighted by cell count for efficiency.
        /// </summary>
        public Vector2? GetMapCenter()
        {
            var blob = this.ActiveBlob;
            if (blob == null || blob.Regions.Count == 0)
            {
                return null;
            }

            var weightedSum = Vector2.Zero;
            int totalCells = 0;
            foreach (var region in blob.Regions)
            {
                weightedSum += region.Center * region.CellCount;
                totalCells += region.CellCount;
            }

            return totalCells > 0 ? weightedSum / totalCells : null;
        }

        /// <summary>
        /// Record a discovered transition portal position.
        /// </summary>
        public void RecordTransition(Vector2 gridPos, string name = "")
        {
            foreach (var t in this.KnownTransitions)
            {
                if (Vector2.Distance(t.GridPos, gridPos) < 5f)
                {
                    return;
                }
            }

            this.KnownTransitions.Add(new TransitionPortal
            {
                GridPos = gridPos,
                Name = name,
                SourceBlobIndex = this.ActiveBlobIndex,
                DestBlobIndex = -1,
            });
        }

        /// <summary>
        /// Mark cells within render range of the player as seen.
        /// Ported directly from AutoExile Update.
        /// </summary>
        public void Update(Vector2 playerGridPos)
        {
            var blob = this.ActiveBlob;
            if (blob == null)
            {
                return;
            }

            if (Vector2.DistanceSquared(playerGridPos, this.lastUpdatePos) < (MinUpdateDistance * MinUpdateDistance))
            {
                return;
            }

            this.lastUpdatePos = playerGridPos;

            int px = (int)playerGridPos.X;
            int py = (int)playerGridPos.Y;
            int rangeSq = this.RenderRange * this.RenderRange;

            int minX = px - this.RenderRange;
            int maxX = px + this.RenderRange;
            int minY = py - this.RenderRange;
            int maxY = py + this.RenderRange;

            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    var cell = new Vector2i(x, y);
                    if (!blob.WalkableCells.Contains(cell)) continue;
                    if (blob.SeenCells.Contains(cell)) continue;

                    int dx = x - px;
                    int dy = y - py;
                    if ((dx * dx) + (dy * dy) <= rangeSq)
                    {
                        blob.SeenCells.Add(cell);

                        if (blob.CellToRegion.TryGetValue(cell, out int regionIdx) && regionIdx < blob.Regions.Count)
                        {
                            blob.Regions[regionIdx].SeenCount++;
                        }
                    }
                }
            }

            blob.Coverage = blob.WalkableCells.Count > 0
                ? (float)blob.SeenCells.Count / blob.WalkableCells.Count
                : 0f;
        }

        public void UpdatePlayerPosition(Vector2 playerGridPos) => this.Update(playerGridPos);

        /// <summary>
        /// Gets the best unexplored region center or snapped unseen centroid to navigate to.
        /// Ported directly from AutoExile GetNextExplorationTarget.
        /// </summary>
        public Vector2? GetNextExplorationTarget(Vector2 playerGridPos)
        {
            var blob = this.ActiveBlob;
            if (blob == null)
            {
                return null;
            }

            Region? bestRegion = null;
            float bestScore = float.MinValue;

            foreach (var region in blob.Regions)
            {
                float exploredThreshold = this.SeenRadiusOverride > 0 ? 0.98f : 0.8f;
                if (region.ExploredRatio > exploredThreshold) continue;

                // Skip tiny dead-end pockets (<50 cells)
                if (region.CellCount < MinRegionSize) continue;

                // Skip regions we failed to path to
                if (this.FailedRegions.Contains(region.Index)) continue;

                int unseenCount = region.CellCount - region.SeenCount;
                float dist = Vector2.Distance(playerGridPos, region.Center);

                // Base score: unseen cells per unit distance with sqrt scaling
                float sizeScore = MathF.Sqrt(unseenCount);
                if (region.CellCount < SmallPocketThreshold)
                {
                    sizeScore *= 0.5f; // mild deprioritize for tiny alcoves
                }

                float score = sizeScore / (dist + 30f);

                // Branch score bonus: prefer regions with escape routes (loops) over dead-ends
                float branchBonus = this.BranchScore(blob, region);
                score += branchBonus * 0.001f;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestRegion = region;
                }
            }

            if (bestRegion == null)
            {
                return null;
            }

            // Return the centroid of unseen cells snapped to an actual walkable unseen cell
            return this.GetUnseenCentroid(blob, bestRegion);
        }

        public Vector2? GetNextTarget(Vector2 playerGridPos) => this.GetNextExplorationTarget(playerGridPos);

        /// <summary>
        /// Score a region's branch quality using DFS. Detects loops and escape routes.
        /// Ported directly from AutoExile BranchScore.
        /// </summary>
        private float BranchScore(Blob blob, Region startRegion)
        {
            var visited = new HashSet<int> { startRegion.Index };
            var stack = new Stack<int>();
            stack.Push(startRegion.Index);

            float unseenCells = 0f;
            bool hasEscapeRoute = false;

            while (stack.Count > 0)
            {
                int idx = stack.Pop();
                var region = blob.Regions[idx];
                unseenCells += region.CellCount - region.SeenCount;

                foreach (int neighborIdx in region.Neighbors)
                {
                    if (this.FailedRegions.Contains(neighborIdx)) continue;
                    var neighbor = blob.Regions[neighborIdx];

                    if (visited.Contains(neighborIdx))
                    {
                        if (neighbor.ExploredRatio > 0.5f)
                        {
                            hasEscapeRoute = true;
                        }

                        continue;
                    }

                    visited.Add(neighborIdx);

                    float exploredThreshold = this.SeenRadiusOverride > 0 ? 0.98f : 0.8f;
                    if (neighbor.ExploredRatio < exploredThreshold)
                    {
                        stack.Push(neighborIdx);
                    }
                    else
                    {
                        hasEscapeRoute = true;
                    }
                }
            }

            return hasEscapeRoute ? unseenCells + 100f : unseenCells;
        }

        /// <summary>
        /// Snaps centroid to nearest actual unseen walkable cell.
        /// Ported directly from AutoExile GetUnseenCentroid.
        /// </summary>
        private Vector2? GetUnseenCentroid(Blob blob, Region region)
        {
            float sumX = 0, sumY = 0;
            int count = 0;

            foreach (var cell in region.Cells)
            {
                if (!blob.SeenCells.Contains(cell))
                {
                    sumX += cell.X;
                    sumY += cell.Y;
                    count++;
                }
            }

            if (count == 0)
            {
                return null;
            }

            var centroid = new Vector2(sumX / count, sumY / count);
            float bestDist = float.MaxValue;
            Vector2i bestCell = default;

            foreach (var cell in region.Cells)
            {
                if (blob.SeenCells.Contains(cell)) continue;
                float dx = cell.X - centroid.X;
                float dy = cell.Y - centroid.Y;
                float dist = (dx * dx) + (dy * dy);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestCell = cell;
                }
            }

            return new Vector2(bestCell.X, bestCell.Y);
        }

        private HashSet<Vector2i> FloodFill(byte[] walkableData, int bytesPerRow, int startX, int startY, int rows, int cols)
        {
            var visited = new HashSet<Vector2i>();
            var queue = new Queue<Vector2i>();

            var start = new Vector2i(startX, startY);
            visited.Add(start);
            queue.Enqueue(start);

            ReadOnlySpan<(int dx, int dy)> dirs = stackalloc (int, int)[]
            {
                (1, 0), (-1, 0), (0, 1), (0, -1),
                (1, 1), (1, -1), (-1, 1), (-1, -1),
            };

            while (queue.Count > 0)
            {
                var cell = queue.Dequeue();

                foreach (var (dx, dy) in dirs)
                {
                    int nx = cell.X + dx;
                    int ny = cell.Y + dy;

                    if (nx < 0 || nx >= cols || ny < 0 || ny >= rows) continue;

                    if (Pathfinding.GetCellValue(walkableData, bytesPerRow, nx, ny) < MinWalkableValue) continue;

                    var neighbor = new Vector2i(nx, ny);
                    if (visited.Add(neighbor))
                    {
                        queue.Enqueue(neighbor);
                    }
                }
            }

            return visited;
        }

        private Blob CreateBlob(HashSet<Vector2i> cells, int index)
        {
            var blob = new Blob
            {
                Index = index,
                WalkableCells = cells,
            };

            int minX = int.MaxValue, minY = int.MaxValue;
            int maxX = int.MinValue, maxY = int.MinValue;
            foreach (var c in cells)
            {
                if (c.X < minX) minX = c.X;
                if (c.Y < minY) minY = c.Y;
                if (c.X > maxX) maxX = c.X;
                if (c.Y > maxY) maxY = c.Y;
            }

            int chunksX = ((maxX - minX) / RegionChunkSize) + 1;
            int chunksY = ((maxY - minY) / RegionChunkSize) + 1;

            var regionGrid = new List<Vector2i>[chunksX * chunksY];
            for (int i = 0; i < regionGrid.Length; i++)
            {
                regionGrid[i] = new List<Vector2i>();
            }

            foreach (var cell in cells)
            {
                int rx = (cell.X - minX) / RegionChunkSize;
                int ry = (cell.Y - minY) / RegionChunkSize;
                int ri = (ry * chunksX) + rx;

                if (ri >= 0 && ri < regionGrid.Length)
                {
                    regionGrid[ri].Add(cell);
                }
            }

            for (int i = 0; i < regionGrid.Length; i++)
            {
                var chunk = regionGrid[i];
                if (chunk.Count < MinRegionSize) continue;

                float sumX = 0, sumY = 0;
                foreach (var c in chunk)
                {
                    sumX += c.X;
                    sumY += c.Y;
                }

                var region = new Region
                {
                    Index = blob.Regions.Count,
                    Center = new Vector2(sumX / chunk.Count, sumY / chunk.Count),
                    CellCount = chunk.Count,
                    SeenCount = 0,
                    Cells = chunk,
                };

                foreach (var c in chunk)
                {
                    blob.CellToRegion[c] = region.Index;
                }

                blob.Regions.Add(region);
            }

            // Merge small chunks into nearest region
            for (int i = 0; i < regionGrid.Length; i++)
            {
                var chunk = regionGrid[i];
                if (chunk.Count >= MinRegionSize || chunk.Count == 0) continue;

                float sumX = 0, sumY = 0;
                foreach (var c in chunk) { sumX += c.X; sumY += c.Y; }
                var chunkCenter = new Vector2(sumX / chunk.Count, sumY / chunk.Count);

                int nearestRegion = -1;
                float nearestDist = float.MaxValue;
                for (int r = 0; r < blob.Regions.Count; r++)
                {
                    float d = Vector2.Distance(chunkCenter, blob.Regions[r].Center);
                    if (d < nearestDist)
                    {
                        nearestDist = d;
                        nearestRegion = r;
                    }
                }

                if (nearestRegion >= 0)
                {
                    var target = blob.Regions[nearestRegion];
                    target.CellCount += chunk.Count;
                    target.Cells.AddRange(chunk);
                    foreach (var c in chunk)
                    {
                        blob.CellToRegion[c] = nearestRegion;
                    }
                }
            }

            // Build region adjacency graph
            BuildRegionAdjacency(blob, minX, minY, chunksX, chunksY);

            return blob;
        }

        private static void BuildRegionAdjacency(Blob blob, int minX, int minY, int chunksX, int chunksY)
        {
            var chunkToRegion = new Dictionary<int, int>();
            foreach (var cell in blob.WalkableCells)
            {
                int rx = (cell.X - minX) / RegionChunkSize;
                int ry = (cell.Y - minY) / RegionChunkSize;
                int ci = (ry * chunksX) + rx;
                if (!chunkToRegion.ContainsKey(ci) && blob.CellToRegion.TryGetValue(cell, out int ri))
                {
                    chunkToRegion[ci] = ri;
                }
            }

            for (int cy = 0; cy < chunksY; cy++)
            {
                for (int cx = 0; cx < chunksX; cx++)
                {
                    int ci = (cy * chunksX) + cx;
                    if (!chunkToRegion.TryGetValue(ci, out int regionA)) continue;

                    // Right neighbor
                    if (cx + 1 < chunksX)
                    {
                        int ni = (cy * chunksX) + (cx + 1);
                        if (chunkToRegion.TryGetValue(ni, out int regionB) && regionA != regionB)
                        {
                            blob.Regions[regionA].Neighbors.Add(regionB);
                            blob.Regions[regionB].Neighbors.Add(regionA);
                        }
                    }

                    // Bottom neighbor
                    if (cy + 1 < chunksY)
                    {
                        int ni = ((cy + 1) * chunksX) + cx;
                        if (chunkToRegion.TryGetValue(ni, out int regionB) && regionA != regionB)
                        {
                            blob.Regions[regionA].Neighbors.Add(regionB);
                            blob.Regions[regionB].Neighbors.Add(regionA);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Finds the nearest walkable cell within the active blob to the given target position.
        /// Uses region centers as a spatial index for efficiency.
        /// </summary>
        public Vector2? SnapToActiveBlob(Vector2 targetGridPos, float maxDistance = 80f)
        {
            var blob = this.ActiveBlob;
            if (blob == null)
            {
                return null;
            }

            var targetCell = new Vector2i((int)targetGridPos.X, (int)targetGridPos.Y);

            // Fast path: target is already in active blob
            if (blob.WalkableCells.Contains(targetCell))
            {
                return targetGridPos;
            }

            // Use region centers to narrow the search area
            float bestDistSq = float.MaxValue;
            Vector2i bestCell = default;
            bool found = false;

            float searchRadius = maxDistance + RegionChunkSize;

            foreach (var region in blob.Regions)
            {
                if (Vector2.Distance(region.Center, targetGridPos) > searchRadius)
                {
                    continue;
                }

                foreach (var cell in region.Cells)
                {
                    var dx = cell.X - targetGridPos.X;
                    var dy = cell.Y - targetGridPos.Y;
                    var distSq = (dx * dx) + (dy * dy);
                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        bestCell = cell;
                        found = true;
                    }
                }
            }

            if (!found)
            {
                return null;
            }

            var actualDist = MathF.Sqrt(bestDistSq);
            if (actualDist > maxDistance)
            {
                return null;
            }

            return new Vector2(bestCell.X, bestCell.Y);
        }

        /// <summary>
        /// Capture a lightweight snapshot of exploration state that can be restored later.
        /// </summary>
        public ExplorationSnapshot CreateSnapshot()
        {
            var snapshot = new ExplorationSnapshot
            {
                ActiveBlobIndex = this.ActiveBlobIndex,
                TotalWalkableCells = this.TotalWalkableCells,
                LastAction = this.LastAction,
            };

            foreach (var blob in this.Blobs)
            {
                snapshot.BlobSnapshots.Add(new BlobSnapshot
                {
                    Index = blob.Index,
                    WalkableCells = new HashSet<Vector2i>(blob.WalkableCells),
                    SeenCells = new HashSet<Vector2i>(blob.SeenCells),
                    Regions = blob.Regions.Select(r => new RegionSnapshot
                    {
                        Index = r.Index,
                        Center = r.Center,
                        CellCount = r.CellCount,
                        SeenCount = r.SeenCount,
                        Cells = new List<Vector2i>(r.Cells),
                        Neighbors = new HashSet<int>(r.Neighbors),
                    }).ToList(),
                    CellToRegion = new Dictionary<Vector2i, int>(blob.CellToRegion),
                    Coverage = blob.Coverage,
                });
            }

            foreach (var t in this.KnownTransitions)
            {
                snapshot.Transitions.Add(new TransitionPortal
                {
                    GridPos = t.GridPos,
                    Name = t.Name,
                    SourceBlobIndex = t.SourceBlobIndex,
                    DestBlobIndex = t.DestBlobIndex,
                });
            }

            snapshot.FailedRegionsCopy = new HashSet<int>(this.FailedRegions);
            return snapshot;
        }

        /// <summary>
        /// Restore exploration state from a previously captured snapshot.
        /// </summary>
        public void RestoreSnapshot(ExplorationSnapshot snapshot)
        {
            this.Clear();

            this.ActiveBlobIndex = snapshot.ActiveBlobIndex;
            this.TotalWalkableCells = snapshot.TotalWalkableCells;
            this.LastAction = snapshot.LastAction + " (restored)";

            foreach (var bs in snapshot.BlobSnapshots)
            {
                var blob = new Blob
                {
                    Index = bs.Index,
                    WalkableCells = bs.WalkableCells,
                    SeenCells = bs.SeenCells,
                    CellToRegion = bs.CellToRegion,
                    Coverage = bs.Coverage,
                };

                foreach (var rs in bs.Regions)
                {
                    blob.Regions.Add(new Region
                    {
                        Index = rs.Index,
                        Center = rs.Center,
                        CellCount = rs.CellCount,
                        SeenCount = rs.SeenCount,
                        Cells = rs.Cells,
                        Neighbors = rs.Neighbors,
                    });
                }

                this.Blobs.Add(blob);
            }

            foreach (var t in snapshot.Transitions)
            {
                this.KnownTransitions.Add(t);
            }

            foreach (var r in snapshot.FailedRegionsCopy)
            {
                this.FailedRegions.Add(r);
            }
        }
    }

    public class TransitionPortal
    {
        public Vector2 GridPos;
        public string Name = string.Empty;
        public int SourceBlobIndex = -1;
        public int DestBlobIndex = -1;
    }

    public class ExplorationSnapshot
    {
        public int ActiveBlobIndex;
        public int TotalWalkableCells;
        public string LastAction = string.Empty;
        public List<BlobSnapshot> BlobSnapshots = new();
        public List<TransitionPortal> Transitions = new();
        public HashSet<int> FailedRegionsCopy = new();
    }

    public class BlobSnapshot
    {
        public int Index;
        public HashSet<Vector2i> WalkableCells = new();
        public HashSet<Vector2i> SeenCells = new();
        public List<RegionSnapshot> Regions = new();
        public Dictionary<Vector2i, int> CellToRegion = new();
        public float Coverage;
    }

    public class RegionSnapshot
    {
        public int Index;
        public Vector2 Center;
        public int CellCount;
        public int SeenCount;
        public List<Vector2i> Cells = new();
        public HashSet<int> Neighbors = new();
    }

    public struct Vector2i : IEquatable<Vector2i>
    {
        public int X;
        public int Y;

        public Vector2i(int x, int y)
        {
            this.X = x;
            this.Y = y;
        }

        public bool Equals(Vector2i other) => this.X == other.X && this.Y == other.Y;

        public override bool Equals(object? obj) => obj is Vector2i other && this.Equals(other);

        public override int GetHashCode() => HashCode.Combine(this.X, this.Y);

        public override string ToString() => $"({this.X}, {this.Y})";
    }

    public class Blob
    {
        public int Index;
        public HashSet<Vector2i> WalkableCells = new();
        public HashSet<Vector2i> SeenCells = new();
        public List<Region> Regions = new();
        public Dictionary<Vector2i, int> CellToRegion = new();
        public float Coverage;
    }

    public class Region
    {
        public int Index;
        public Vector2 Center;
        public int CellCount;
        public int SeenCount;
        public List<Vector2i> Cells = new();
        public HashSet<int> Neighbors = new();
        public float ExploredRatio => this.CellCount > 0 ? (float)this.SeenCount / this.CellCount : 1f;
    }
}
