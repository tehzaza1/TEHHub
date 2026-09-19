// <copyright file="WaveTick.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Modes.WaveFarm
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;
    using TEHhub.RemoteObjects.Components;
    using TEHhub.Offsets.Natives;
    using AutoExile2.Systems;

    /// <summary>
    /// Core tick logic for wave farming. Every tick answers:
    /// "What is the single best thing to do right now?"
    /// Combat happens continuously while exploring.
    /// Ported directly from AutoExile 1 WaveTick, adapted for PoE 2 WASD and TEHhub.
    /// </summary>
    public class WaveTick
    {
        private readonly DirectionTracker direction = new();
        private readonly LootFilter lootFilter = new();
        private readonly ClearPlan clearPlan = new();
        private readonly BossLearnerSystem bossLearner = new();

        public DirectionTracker Direction => this.direction;
        public LootFilter LootMetrics => this.lootFilter;
        public ClearPlan ClearPlan => this.clearPlan;
        public BossLearnerSystem BossLearner => this.bossLearner;

        private IFarmPlan plan = null!;
        public IFarmPlan CurrentPlan => this.plan;

        // Navigation state
        public List<Vector2> CurrentNavPath { get; } = new();
        public int CurrentWaypointIndex { get; private set; }
        public Vector2? CurrentDestination { get; private set; }

        // Stuck detection & recovery
        private Vector2 lastPlayerGridPos;
        private float stuckTimer;
        private DateTime unstuckUntil = DateTime.MinValue;
        private Vector2 unstuckDir = Vector2.Zero;

        // Combat engagement lock
        private bool engagedInCombat;
        private bool engagementTimedOutForCurrentPack;
        private DateTime engageStartTime = DateTime.MinValue;

        // Repath throttle
        private DateTime lastExplorePath = DateTime.MinValue;
        private readonly List<FailedExploreTarget> failedExploreTargets = new();
        private const float FailedTargetRadius = 30f;
        private const float FailedTargetExpirySeconds = 30f;

        // Status & telemetry
        public string Status { get; private set; } = string.Empty;
        public string Decision { get; private set; } = string.Empty;
        public string LootDebug { get; private set; } = string.Empty;

        public void Initialize(IFarmPlan plan)
        {
            this.plan = plan;
            this.Reset();
        }

        public void Reset()
        {
            this.direction.Reset();
            this.lootFilter.Reset();
            this.clearPlan.Reset();
            this.bossLearner.Reset();
            this.CurrentNavPath.Clear();
            this.CurrentWaypointIndex = 0;
            this.CurrentDestination = null;
            this.engagedInCombat = false;
            this.engagementTimedOutForCurrentPack = false;
            this.engageStartTime = DateTime.MinValue;
            this.failedExploreTargets.Clear();
            this.lastExplorePath = DateTime.MinValue;
            this.stuckTimer = 0f;
            this.unstuckUntil = DateTime.MinValue;
            this.Status = string.Empty;
            this.Decision = string.Empty;
            this.LootDebug = string.Empty;
        }

        /// <summary>
        /// Called every tick while in-map. Returns true if the map is complete (should exit).
        /// </summary>
        public bool Tick(BotContext ctx)
        {
            var area = ctx.Area;
            var world = ctx.World;
            var player = ctx.Player;
            var playerPos = ctx.PlayerGrid;
            float deltaSec = ctx.DeltaTime;
            var config = this.plan.Config;

            // 1. Update forward direction tracker
            this.direction.Update(playerPos);

            // 2. Advance ClearPlan observer queue & BossLearner
            using (ctx.Perf.SectionScope("ClearPlan.Update"))
            {
                this.clearPlan.Update(ctx, playerPos);
                this.bossLearner.Update(ctx, playerPos);
            }

            // 3. Handle active unstuck recovery
            if (DateTime.Now < this.unstuckUntil)
            {
                this.Status = "Wiggling unstuck";
                this.Decision = "Unstuck";
                BotInput.ReleaseSprint(ctx.Settings.SprintKey);
                BotInput.WasdMove(this.unstuckDir, ctx.Settings);
                return false;
            }

            // 4. Combat tick - skills fire continuously
            bool inCombat = false;
            using (ctx.Perf.SectionScope("Combat.Tick"))
            {
                inCombat = ctx.Combat.TickCombat(area, world, player, ctx.Settings);
            }

            // Embedded weighted density engagement: Normal=1, Magic=2, Rare/Unique=3.
            // Farm-plan config is the source of truth; 0 explicitly means never pause for combat.
            int engagementThreshold = Math.Max(0, config.PauseDensity);
            bool denseEnough = engagementThreshold > 0 &&
                               inCombat &&
                               ctx.Combat.WeightedDensity >= engagementThreshold;

            if (!denseEnough)
            {
                // The current pack is cleared or has thinned below the engagement threshold.
                // This also rearms combat locking after a previous timeout.
                this.engagedInCombat = false;
                this.engagementTimedOutForCurrentPack = false;
                this.engageStartTime = DateTime.MinValue;
            }
            else if (!this.engagementTimedOutForCurrentPack)
            {
                if (!this.engagedInCombat)
                {
                    this.engageStartTime = DateTime.Now;
                    this.engagedInCombat = true;
                }

                // Safety disengage after 10s. Suppress re-locking on the same dense pack
                // until it falls below the configured threshold, otherwise this would
                // re-engage again on the very next tick.
                if ((DateTime.Now - this.engageStartTime).TotalSeconds > 10)
                {
                    this.engagedInCombat = false;
                    this.engagementTimedOutForCurrentPack = true;
                    ctx.Log($"[Wave] Pack engagement timed out after 10s (weight {ctx.Combat.WeightedDensity}, threshold {engagementThreshold}); resuming navigation");
                }
            }

            if (this.engagedInCombat && inCombat)
            {
                this.Status = $"Engaging pack ({ctx.Combat.NearbyHostileCount} enemies, weight {ctx.Combat.WeightedDensity}) [{ctx.Settings.CombatStyle}]";
                this.Decision = "PackEngage";

                // Sprint is an exploration/travel state. Never carry it into active combat.
                BotInput.ReleaseSprint(ctx.Settings.SprintKey);

                if (config.SuppressCombatPositioning)
                {
                    BotInput.ReleaseAllMovementKeys(ctx.Settings);
                }
                else
                {
                    ctx.Combat.UpdateCombatMovement(ctx.World, ctx.Player, ctx.Area, ctx.Settings);
                }

                return false;
            }

            // 5. Evaluate best action
            var action = this.EvaluateBestAction(ctx, playerPos);
            this.Decision = action.Type.ToString();

            // 6. Execute action. Sparse combat remains attack-move, but sprint stays disabled.
            return this.Execute(action, ctx, playerPos, deltaSec, inCombat);
        }

        private WaveAction EvaluateBestAction(BotContext ctx, Vector2 playerPos)
        {
            var config = this.plan.Config;
            float coverage = ctx.Exploration.ActiveBlobCoverage;

            this.PruneFailedExploreTargets();

            // P3.5 - Boss Arena Encounter Sequence (Door -> Checkpoint)
            if (this.bossLearner.HasBossTarget && !this.bossLearner.BossReached)
            {
                // If we detected a Fog Door and have not reached it yet, go to the door first.
                if (this.bossLearner.BossDoorPos.HasValue && !this.bossLearner.BossDoorReached)
                {
                    var doorTarget = this.bossLearner.BossDoorPos.Value;
                    if (!this.IsFailedExploreTarget(doorTarget))
                    {
                        return WaveAction.Explore(doorTarget);
                    }
                }

                // Once the door is reached (or when no door was discovered), continue to the checkpoint.
                if (this.bossLearner.BossCheckpointPos.HasValue)
                {
                    var bossTarget = this.bossLearner.BossCheckpointPos.Value;
                    if (!this.IsFailedExploreTarget(bossTarget))
                    {
                        return WaveAction.Explore(bossTarget);
                    }
                }
            }

            // P4 - Chunk-queue exploration via ClearPlan
            if (this.clearPlan.IsInitialized && this.clearPlan.CurrentTarget.HasValue)
            {
                var chunkTarget = this.clearPlan.CurrentTarget.Value;
                if (!this.IsFailedExploreTarget(chunkTarget))
                {
                    return WaveAction.Explore(chunkTarget);
                }

                this.clearPlan.SkipCurrent(ctx);
            }

            // P4b: Post-observation hunt via ThreatMap
            if (this.clearPlan.IsInitialized && this.clearPlan.IsComplete && ctx.ThreatMap.IsInitialized)
            {
                var dense = ctx.ThreatMap.GetDensestAliveChunk(playerPos);
                if (dense.HasValue && !this.IsFailedExploreTarget(dense.Value))
                {
                    return WaveAction.Explore(dense.Value);
                }

                var nearest = ctx.ThreatMap.GetNearestAliveChunk(playerPos);
                if (nearest.HasValue && !this.IsFailedExploreTarget(nearest.Value))
                {
                    return WaveAction.Explore(nearest.Value);
                }
            }

            // Terrain fallback exploration
            if (coverage < config.MinCoverage || !this.clearPlan.IsInitialized)
            {
                var terrainTarget = ctx.Exploration.GetNextTarget(playerPos);
                if (terrainTarget.HasValue && !this.IsFailedExploreTarget(terrainTarget.Value))
                {
                    return WaveAction.Explore(terrainTarget.Value);
                }
            }

            // P5: Post-clear plan actions
            var planAction = this.plan.GetPostClearAction(ctx);
            if (planAction.HasValue)
            {
                return planAction.Value;
            }

            // P6: Boss Safety Check - Never exit map if a Boss Checkpoint is pending and not reached!
            if (this.bossLearner.BossCheckpointPos.HasValue && !this.bossLearner.BossReached)
            {
                var bossTarget = this.bossLearner.BossCheckpointPos.Value;
                if (!this.IsFailedExploreTarget(bossTarget))
                {
                    return WaveAction.Explore(bossTarget);
                }
            }

            // P7: Done
            return WaveAction.ExitMap;
        }

        private bool Execute(WaveAction action, BotContext ctx, Vector2 playerPos, float deltaSec, bool inCombat)
        {
            var area = ctx.Area;
            var walkableData = area.GridWalkableData;
            int bytesPerRow = area.TerrainMetadata.BytesPerRow;

            switch (action.Type)
            {
                case WaveActionType.Explore:
                    var target = action.TargetGridPos;
                    var currentDest = this.CurrentDestination ?? Vector2.Zero;
                    float distToNewTarget = Vector2.Distance(currentDest, target);
                    float distToTarget = Vector2.Distance(playerPos, target);
                    double timeSinceRepath = (DateTime.Now - this.lastExplorePath).TotalMilliseconds;

                    // If player is already at destination, stop movement and wait (dwell / clear plan)
                    if (distToTarget <= 10f || (this.CurrentWaypointIndex >= this.CurrentNavPath.Count && distToTarget <= 14f))
                    {
                        BotInput.ReleaseAllMovementKeys(ctx.Settings);
                        BotInput.ReleaseSprint(ctx.Settings.SprintKey);
                        this.CurrentNavPath.Clear();
                        this.CurrentWaypointIndex = 0;
                        this.Status = $"At explore target ({ctx.Exploration.ActiveBlobCoverage:P0})";
                        return false;
                    }

                    // Repath if destination shifted, finished current path, or no active path
                    bool needRepath = false;
                    if (this.CurrentNavPath.Count == 0)
                    {
                        needRepath = timeSinceRepath > 250;
                    }
                    else if (distToNewTarget > 40f)
                    {
                        needRepath = timeSinceRepath > 300;
                    }
                    else if (this.CurrentWaypointIndex >= this.CurrentNavPath.Count)
                    {
                        needRepath = timeSinceRepath > 250;
                    }

                    if (needRepath)
                    {
                        this.CurrentDestination = target;
                        using (ctx.Perf.SectionScope("Pathfinding"))
                        {
                            var path = Pathfinding.FindPath(walkableData, bytesPerRow, playerPos, target);
                            this.CurrentNavPath.Clear();
                            if (path != null && path.Count > 0)
                            {
                                this.CurrentNavPath.AddRange(path);
                                this.CurrentWaypointIndex = 0;
                            }
                        }

                        this.lastExplorePath = DateTime.Now;

                        if (this.CurrentNavPath.Count == 0)
                        {
                            this.AddFailedExploreTarget(target);
                            ctx.Exploration.MarkTargetFailed(target);
                            ctx.Perf.RecordFailure("Explore", "No path to target");
                            ctx.Log($"[Wave] Explore target unreachable ({target.X:F0},{target.Y:F0})");
                        }
                    }

                    // Navigate along waypoints. Combat can continue firing while moving, but never while sprinting.
                    this.FollowWaypoints(ctx, playerPos, deltaSec, allowSprint: !inCombat);
                    this.Status = $"Exploring ({ctx.Exploration.ActiveBlobCoverage:P0})";
                    return false;

                case WaveActionType.ExitMap:
                    BotInput.ReleaseAllMovementKeys(ctx.Settings);
                    BotInput.ReleaseSprint(ctx.Settings.SprintKey);
                    this.Status = "Map complete";
                    return true;

                case WaveActionType.None:
                default:
                    if (this.CurrentWaypointIndex < this.CurrentNavPath.Count)
                    {
                        this.FollowWaypoints(ctx, playerPos, deltaSec, allowSprint: !inCombat);
                    }
                    else
                    {
                        BotInput.ReleaseAllMovementKeys(ctx.Settings);
                        BotInput.ReleaseSprint(ctx.Settings.SprintKey);
                    }

                    return false;
            }
        }

        private void FollowWaypoints(BotContext ctx, Vector2 playerPos, float deltaSec, bool allowSprint)
        {
            if (this.CurrentWaypointIndex >= this.CurrentNavPath.Count)
            {
                BotInput.ReleaseAllMovementKeys(ctx.Settings);
                BotInput.ReleaseSprint(ctx.Settings.SprintKey);
                return;
            }

            // Check stuck detection
            float moveDelta = Vector2.Distance(playerPos, this.lastPlayerGridPos);
            this.lastPlayerGridPos = playerPos;

            if (moveDelta < 1.0f)
            {
                this.stuckTimer += deltaSec;
                if (this.stuckTimer > 1.5f)
                {
                    this.stuckTimer = 0f;
                    var rand = new Random();
                    float angle = (float)(rand.NextDouble() * Math.PI * 2.0);
                    this.unstuckDir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
                    this.unstuckUntil = DateTime.Now.AddMilliseconds(500);
                    this.CurrentNavPath.Clear();
                    BotInput.ReleaseSprint(ctx.Settings.SprintKey);
                    ctx.Log("[Wave] Stuck detected, wiggling unstuck and clearing path");
                    return;
                }
            }
            else
            {
                this.stuckTimer = 0f;
            }

            // Advance waypoints if player is already within 12 units
            while (this.CurrentWaypointIndex < this.CurrentNavPath.Count &&
                   Vector2.Distance(playerPos, this.CurrentNavPath[this.CurrentWaypointIndex]) <= 12f)
            {
                this.CurrentWaypointIndex++;
            }

            if (this.CurrentWaypointIndex >= this.CurrentNavPath.Count)
            {
                BotInput.ReleaseAllMovementKeys(ctx.Settings);
                BotInput.ReleaseSprint(ctx.Settings.SprintKey);
                return;
            }

            var targetWp = this.CurrentNavPath[this.CurrentWaypointIndex];

            // Convert target waypoint from Grid space to Screen space for accurate WASD movement
            var screenDir = BotInput.GridToScreenDirection(
                ctx.World,
                ctx.Player,
                targetWp,
                playerPos,
                ctx.Area.WorldToGridConvertor);

            // Sprint / Dodge Roll management
            float distToDest = this.CurrentDestination.HasValue
                ? Vector2.Distance(playerPos, this.CurrentDestination.Value)
                : Vector2.Distance(playerPos, targetWp);
            bool shouldSprint = allowSprint &&
                                ctx.Settings.UseSprint &&
                                distToDest >= ctx.Settings.SprintMinDistance;
            BotInput.SetSprint(ctx.Settings.SprintKey, shouldSprint);

            BotInput.WasdMove(screenDir, ctx.Settings);
        }

        private void PruneFailedExploreTargets()
        {
            var now = DateTime.Now;
            this.failedExploreTargets.RemoveAll(f => (now - f.FailedAt).TotalSeconds > FailedTargetExpirySeconds);
        }

        private bool IsFailedExploreTarget(Vector2 pos)
        {
            foreach (var f in this.failedExploreTargets)
            {
                if (Vector2.Distance(pos, f.GridPos) < FailedTargetRadius)
                {
                    return true;
                }
            }

            return false;
        }

        private void AddFailedExploreTarget(Vector2 pos)
        {
            if (!this.IsFailedExploreTarget(pos))
            {
                this.failedExploreTargets.Add(new FailedExploreTarget
                {
                    GridPos = pos,
                    FailedAt = DateTime.Now,
                });
            }
        }
    }

    internal struct FailedExploreTarget
    {
        public Vector2 GridPos;
        public DateTime FailedAt;
    }
}
