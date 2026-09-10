// <copyright file="ClearPlan.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Modes.WaveFarm
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;
    using AutoExile2.Systems;

    /// <summary>
    /// Region-based map clearing with observer set-cover.
    /// Every ExplorationMap Region (the 80x80 blocks) is a unit to observe.
    /// The network bubble (~180g) covers what is within observation radius from an observer point.
    /// Ported directly from AutoExile 1 ClearPlan.
    /// </summary>
    public class ClearPlan
    {
        private const float DwellSeconds = 0.8f;      // brief "was in bubble" confirmation
        private const float ArriveRadius = 12f;       // "reached" observer point (was 25f)
        private const double PlanStallSeconds = 45.0; // global safety bail
        private const int MinRegionCellCount = 50;    // ignore tiny pockets
        private const float ObservationFloor = 60f;   // clamp: never drop observer radius below this
        private const float AlreadySeenRatio = 0.90f; // pre-seen regions start visited

        private class RegionEntry
        {
            public int Index;
            public Vector2 Center;           // snapped-to-walkable centroid
            public float BoundingRadius;    // farthest cell from raw centroid
            public float ObservationRadius; // max distance for bubble to fully cover the region
            public DateTime DwellStart;     // MinValue = not dwelling
            public bool Visited;
        }

        private readonly Dictionary<int, RegionEntry> regions = new();
        private readonly HashSet<int> blockedObservers = new();
        private Vector2? currentObserver;
        private int currentObserverRegionIndex = -1;
        private DateTime observerArrivedAt = DateTime.MinValue;
        private DateTime planLastProgress = DateTime.MinValue;
        private string zoneHash = string.Empty;
        private bool initialized;

        public bool IsInitialized => this.initialized;
        public bool IsComplete => this.initialized && this.PendingCount == 0;
        public Vector2? CurrentTarget => this.currentObserver;
        public int RemainingCount => this.PendingCount;
        public int VisitedCount => this.CountVisited();
        public int TotalCount => this.regions.Count;
        public string Status { get; private set; } = string.Empty;

        private int PendingCount
        {
            get
            {
                int n = 0;
                foreach (var r in this.regions.Values)
                {
                    if (!r.Visited)
                    {
                        n++;
                    }
                }

                return n;
            }
        }

        private int CountVisited()
        {
            int n = 0;
            foreach (var r in this.regions.Values)
            {
                if (r.Visited)
                {
                    n++;
                }
            }

            return n;
        }

        public void Initialize(BotContext ctx)
        {
            var area = ctx.Area;
            var currentHash = area?.AreaHash ?? string.Empty;
            if (this.initialized && currentHash == this.zoneHash && this.PendingCount > 0)
            {
                return;
            }

            this.Reset();
            this.zoneHash = currentHash;

            if (!ctx.Exploration.IsInitialized)
            {
                return;
            }

            var blob = ctx.Exploration.ActiveBlob;
            if (blob == null || blob.Regions.Count == 0)
            {
                return;
            }

            foreach (var region in blob.Regions)
            {
                if (region.CellCount < MinRegionCellCount)
                {
                    continue;
                }

                if (ctx.Exploration.FailedRegions.Contains(region.Index))
                {
                    continue;
                }

                // Region bounding radius: farthest cell from centroid.
                float maxDistSq = 0f;
                foreach (var cell in region.Cells)
                {
                    float dx = cell.X - region.Center.X;
                    float dy = cell.Y - region.Center.Y;
                    float d = (dx * dx) + (dy * dy);
                    if (d > maxDistSq)
                    {
                        maxDistSq = d;
                    }
                }

                float boundRadius = MathF.Sqrt(maxDistSq);

                // Snap the observation point to actual walkable terrain
                var snappedCenter = ctx.Exploration.SnapToActiveBlob(region.Center, 120f) ?? region.Center;
                var obsRadius = MathF.Max(ObservationFloor, Pathfinding.NetworkBubbleRadius - boundRadius);

                this.regions[region.Index] = new RegionEntry
                {
                    Index = region.Index,
                    Center = snappedCenter,
                    BoundingRadius = boundRadius,
                    ObservationRadius = obsRadius,
                    DwellStart = DateTime.MinValue,
                    Visited = region.ExploredRatio > AlreadySeenRatio,
                };
            }

            this.initialized = true;
            this.planLastProgress = DateTime.Now;
            ctx.Log($"[ClearPlan] Initialized: {this.PendingCount} pending of {this.regions.Count} regions (bubble={Pathfinding.NetworkBubbleRadius:F0}g)");
        }

        public void Update(BotContext ctx, Vector2 playerPos)
        {
            if (!this.initialized)
            {
                return;
            }

            if ((DateTime.Now - this.planLastProgress).TotalSeconds > PlanStallSeconds)
            {
                int abandoned = this.PendingCount;
                foreach (var r in this.regions.Values)
                {
                    if (!r.Visited)
                    {
                        r.Visited = true;
                    }
                }

                this.currentObserver = null;
                this.currentObserverRegionIndex = -1;
                this.Status = "plan stall — giving up";
                ctx.Log($"[ClearPlan] Global stall after {PlanStallSeconds}s — abandoning {abandoned} regions");
                return;
            }

            // 1. Observer arrival & dwell check
            if (this.currentObserver.HasValue)
            {
                float distToObs = Vector2.Distance(playerPos, this.currentObserver.Value);
                if (distToObs <= ArriveRadius)
                {
                    if (this.observerArrivedAt == DateTime.MinValue)
                    {
                        this.observerArrivedAt = DateTime.Now;
                    }
                    else if ((DateTime.Now - this.observerArrivedAt).TotalSeconds >= DwellSeconds)
                    {
                        // Observer dwell completed! Mark observer's own region visited
                        if (this.currentObserverRegionIndex >= 0 && this.regions.TryGetValue(this.currentObserverRegionIndex, out var ownRegion))
                        {
                            ownRegion.Visited = true;
                        }

                        // Mark any unvisited regions covered by this observer as visited only if very close (prevent skipping boss rooms/dead ends)
                        foreach (var r in this.regions.Values)
                        {
                            float effectiveCoverageRadius = MathF.Min(r.ObservationRadius, 55f);
                            if (!r.Visited && Vector2.Distance(this.currentObserver.Value, r.Center) <= effectiveCoverageRadius + ArriveRadius)
                            {
                                r.Visited = true;
                            }
                        }

                        this.planLastProgress = DateTime.Now;
                        this.currentObserver = null;
                        this.currentObserverRegionIndex = -1;
                        this.observerArrivedAt = DateTime.MinValue;
                    }
                }
                else
                {
                    this.observerArrivedAt = DateTime.MinValue;
                }
            }

            // 2. Passive Dwell check for any regions the player moves through
            // Must actually be close to the region (within bounding radius or 25g max) to avoid marking boss arenas prematurely from far away!
            foreach (var r in this.regions.Values)
            {
                if (r.Visited)
                {
                    continue;
                }

                float maxPassiveDist = MathF.Min(MathF.Max(25f, r.BoundingRadius), 40f);
                float dist = Vector2.Distance(playerPos, r.Center);
                if (dist > maxPassiveDist)
                {
                    r.DwellStart = DateTime.MinValue;
                    continue;
                }

                if (r.DwellStart == DateTime.MinValue)
                {
                    r.DwellStart = DateTime.Now;
                }

                if ((DateTime.Now - r.DwellStart).TotalSeconds < DwellSeconds)
                {
                    continue;
                }

                r.Visited = true;
                this.planLastProgress = DateTime.Now;
            }

            // 3. Reselect observer if the current one no longer covers any pending region.
            if (this.currentObserver.HasValue && !this.CurrentObserverStillUseful(playerPos, this.currentObserver.Value))
            {
                this.currentObserver = null;
                this.currentObserverRegionIndex = -1;
                this.observerArrivedAt = DateTime.MinValue;
            }

            if (!this.currentObserver.HasValue && this.PendingCount > 0)
            {
                this.PickNextObserver(playerPos);
            }

            if (this.currentObserver.HasValue)
            {
                float d = Vector2.Distance(playerPos, this.currentObserver.Value);
                int covered = this.CountCoverageAt(this.currentObserver.Value);
                this.Status = d <= ArriveRadius
                    ? $"observing {covered} regions ({this.CountVisited()}/{this.regions.Count})"
                    : $"en route -> ({this.currentObserver.Value.X:F0},{this.currentObserver.Value.Y:F0}) [{d:F0}g, covers {covered}]";
            }
            else
            {
                this.Status = this.IsComplete ? "complete" : "no reachable observer";
            }
        }

        public void SkipCurrent(BotContext ctx)
        {
            if (this.currentObserverRegionIndex >= 0)
            {
                this.blockedObservers.Add(this.currentObserverRegionIndex);
            }

            this.currentObserver = null;
            this.currentObserverRegionIndex = -1;
            this.observerArrivedAt = DateTime.MinValue;
            this.planLastProgress = DateTime.Now;
            this.Status = "unreachable observer — reselecting";
        }

        public void Reset()
        {
            this.regions.Clear();
            this.blockedObservers.Clear();
            this.currentObserver = null;
            this.currentObserverRegionIndex = -1;
            this.observerArrivedAt = DateTime.MinValue;
            this.initialized = false;
            this.zoneHash = string.Empty;
            this.planLastProgress = DateTime.MinValue;
            this.Status = string.Empty;
        }

        private bool CurrentObserverStillUseful(Vector2 playerPos, Vector2 observer)
        {
            // If the observer no longer covers any unvisited regions, it's no longer useful
            return this.CountCoverageAt(observer) > 0;
        }

        private int CountCoverageAt(Vector2 point)
        {
            int n = 0;
            foreach (var r in this.regions.Values)
            {
                if (r.Visited)
                {
                    continue;
                }

                if (Vector2.Distance(point, r.Center) <= r.ObservationRadius)
                {
                    n++;
                }
            }

            return n;
        }

        private void PickNextObserver(Vector2 playerPos)
        {
            int bestCover = 0;
            float bestDist = float.MaxValue;
            Vector2? best = null;
            int bestIdx = -1;

            foreach (var candidate in this.regions.Values)
            {
                if (candidate.Visited)
                {
                    continue;
                }

                if (this.blockedObservers.Contains(candidate.Index))
                {
                    continue;
                }

                int cover = this.CountCoverageAt(candidate.Center);
                if (cover == 0)
                {
                    continue;
                }

                float d = Vector2.Distance(playerPos, candidate.Center);
                bool better = cover > bestCover || (cover == bestCover && d < bestDist);
                if (better)
                {
                    bestCover = cover;
                    bestDist = d;
                    best = candidate.Center;
                    bestIdx = candidate.Index;
                }
            }

            this.currentObserver = best;
            this.currentObserverRegionIndex = bestIdx;
        }
    }
}
