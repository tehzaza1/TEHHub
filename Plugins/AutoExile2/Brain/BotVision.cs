// <copyright file="BotVision.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Brain
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;
    using GameHelper.RemoteEnums;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.RemoteObjects.States.InGameStateObjects;
    using AutoExile2.Modes;
    using AutoExile2.Modes.Shared;
    using AutoExile2.Systems;

    /// <summary>
    /// Represents the "Eyes &amp; Senses" (ระบบดวงตาและประสาทสัมผัส) of the bot.
    /// Aggregates spatial hazard maps, exploration fog-of-war, entity recognition, and line-of-sight.
    /// </summary>
    public class BotVision
    {
        public HazardVision Hazard { get; } = new();

        public ExplorationVision Exploration { get; } = new();

        public EntityVision Entities { get; } = new();

        public SpatialVision Spatial { get; } = new();

        /// <summary>
        /// Scans and populates all vision subsystems from the current BotContext and game state.
        /// </summary>
        public void Scan(
            BotContext ctx,
            Entity? leader,
            Vector2 leaderGrid,
            Entity? follower,
            Vector2 followerGrid,
            Vector2 leaderHeading,
            int followerAnimId,
            Vector2 formationTarget,
            int nearbyEnemies,
            float closestDist,
            Entity? bestTarget)
        {
            // 1. Scan Spatial LOS and distances
            this.Spatial.Scan(ctx, followerGrid, leaderGrid, formationTarget);

            // 2. Scan Entities (Leader, Follower, Hostiles, Environment)
            this.Entities.Scan(ctx, leader, leaderGrid, follower, followerGrid, leaderHeading, followerAnimId, nearbyEnemies, closestDist, bestTarget);

            // 3. Scan Hazards via ThreatMap
            this.Hazard.Scan(ctx, followerGrid);

            // 4. Scan Exploration via ExplorationMap
            this.Exploration.Scan(ctx, followerGrid);
        }
    }

    /// <summary>
    /// Hazard &amp; Threat Vision: Detects danger zones, monster density, and incoming lethal threats.
    /// </summary>
    public class HazardVision
    {
        public float ThreatInProximity { get; private set; }

        public bool IsHighDangerArea { get; private set; }

        public Vector2? DensestMonsterPack { get; private set; }

        public Vector2? NearestMonsterCluster { get; private set; }

        public int TotalAliveTrackedMonsters { get; private set; }

        public const float HighDangerThreshold = 60f;

        public void Scan(BotContext ctx, Vector2 playerGrid)
        {
            var tm = ctx.ThreatMap;
            if (tm == null || !tm.IsInitialized)
            {
                this.ThreatInProximity = 0f;
                this.IsHighDangerArea = false;
                this.DensestMonsterPack = null;
                this.NearestMonsterCluster = null;
                this.TotalAliveTrackedMonsters = 0;
                return;
            }

            // Proximity threat within 25 grid units (~270 world units)
            this.ThreatInProximity = tm.GetThreatInRadius(playerGrid, 25f);
            this.IsHighDangerArea = this.ThreatInProximity >= HighDangerThreshold;
            this.DensestMonsterPack = tm.GetDensestAliveChunk(playerGrid, 15f);
            this.NearestMonsterCluster = tm.GetNearestAliveChunk(playerGrid, 15f);
            this.TotalAliveTrackedMonsters = tm.TotalAlive;
        }
    }

    /// <summary>
    /// Exploration Vision: Tracks fog-of-war coverage, unvisited frontiers, and map exits/transitions.
    /// </summary>
    public class ExplorationVision
    {
        public float MapCoverage { get; private set; }

        public Vector2? NextUnexploredTarget { get; private set; }

        public int DiscoveredTransitionsCount { get; private set; }

        public bool IsMapFullyExplored => this.MapCoverage >= 90f;

        public void Scan(BotContext ctx, Vector2 playerGrid)
        {
            var em = ctx.Exploration;
            if (em == null || !em.IsInitialized)
            {
                this.MapCoverage = 0f;
                this.NextUnexploredTarget = null;
                this.DiscoveredTransitionsCount = 0;
                return;
            }

            this.MapCoverage = em.Coverage;
            this.NextUnexploredTarget = em.GetNextExplorationTarget(playerGrid);
            this.DiscoveredTransitionsCount = em.KnownTransitions.Count;
        }
    }

    /// <summary>
    /// Entity Vision: Detailed observation of leader, self, nearby hostiles, and interactive objects.
    /// </summary>
    public class EntityVision
    {
        public Entity? LeaderEntity { get; private set; }

        public Vector2 LeaderGrid { get; private set; }

        public Vector2 LeaderHeading { get; private set; } = new Vector2(0, -1);

        public bool IsLeaderDead { get; private set; }

        public float LeaderHpPercent { get; private set; } = 100f;

        public bool IsLeaderSprinting { get; private set; }

        public Entity? FollowerEntity { get; private set; }

        public Vector2 FollowerGrid { get; private set; }

        public bool IsFollowerDead { get; private set; }

        public float FollowerHpPercent { get; private set; } = 100f;

        public float FollowerManaPercent { get; private set; } = 100f;

        public int FollowerAnimId { get; private set; }

        public int NearbyEnemyCount { get; private set; }

        public float ClosestEnemyDistance { get; private set; } = float.MaxValue;

        public Entity? BestCombatTarget { get; private set; }

        public bool HasDangerousRareOrBoss { get; private set; }

        public Entity? NearestPortal { get; private set; }

        public void Scan(
            BotContext ctx,
            Entity? leader,
            Vector2 leaderGrid,
            Entity? follower,
            Vector2 followerGrid,
            Vector2 leaderHeading,
            int followerAnimId,
            int nearbyEnemies,
            float closestDist,
            Entity? bestTarget)
        {
            this.LeaderEntity = leader;
            this.LeaderGrid = leaderGrid;
            this.LeaderHeading = leaderHeading;
            this.IsLeaderSprinting = ctx.CoopGamepad.IsLeaderSprinting;

            if (leader != null && leader.TryGetComponent<Life>(out var lLife) && lLife.Health.Total > 0)
            {
                this.IsLeaderDead = lLife.Health.Current <= 0;
                this.LeaderHpPercent = (float)lLife.Health.Current / lLife.Health.Total * 100f;
            }
            else
            {
                this.IsLeaderDead = false;
                this.LeaderHpPercent = 100f;
            }

            this.FollowerEntity = follower;
            this.FollowerGrid = followerGrid;
            this.FollowerAnimId = followerAnimId;

            if (follower != null && follower.TryGetComponent<Life>(out var fLife) && fLife.Health.Total > 0)
            {
                this.IsFollowerDead = fLife.Health.Current <= 0;
                this.FollowerHpPercent = (float)fLife.Health.Current / fLife.Health.Total * 100f;
                this.FollowerManaPercent = fLife.Mana.Total > 0 ? ((float)fLife.Mana.Current / fLife.Mana.Total * 100f) : 100f;
            }
            else
            {
                this.IsFollowerDead = false;
                this.FollowerHpPercent = 100f;
                this.FollowerManaPercent = 100f;
            }

            this.NearbyEnemyCount = nearbyEnemies;
            this.ClosestEnemyDistance = closestDist;
            this.BestCombatTarget = bestTarget;

            // Check for high-threat entities (Rare, Unique, Boss)
            this.HasDangerousRareOrBoss = false;
            if (bestTarget != null && bestTarget.TryGetComponent<ObjectMagicProperties>(out var omp))
            {
                this.HasDangerousRareOrBoss = omp.Rarity is Rarity.Rare or Rarity.Unique;
            }

            // Scan for exit portal within 60 units
            this.NearestPortal = ModeHelpers.FindNearestPortal(ctx.Area, followerGrid, 60f);
        }
    }

    /// <summary>
    /// Spatial &amp; Line-of-Sight Vision: Raycast collision checks and distance metrics.
    /// </summary>
    public class SpatialVision
    {
        public float DistanceToLeader { get; private set; }

        public float DistanceToFormation { get; private set; }

        public bool HasLosToLeader { get; private set; }

        public bool HasLosToFormation { get; private set; }

        public void Scan(BotContext ctx, Vector2 followerGrid, Vector2 leaderGrid, Vector2 formationTarget)
        {
            this.DistanceToLeader = Vector2.Distance(followerGrid, leaderGrid);
            this.DistanceToFormation = Vector2.Distance(followerGrid, formationTarget);

            var area = ctx.Area;
            var walkable = area?.GridWalkableData;
            int bpr = area?.TerrainMetadata.BytesPerRow ?? 0;

            if (walkable != null && bpr > 0)
            {
                int rows = walkable.Length / bpr;
                int cols = bpr * 2;

                this.HasLosToFormation = Pathfinding.HasLineOfSight(walkable, bpr, followerGrid, formationTarget, rows, cols, 3);
                this.HasLosToLeader = Pathfinding.HasLineOfSight(walkable, bpr, followerGrid, leaderGrid, rows, cols, 3);
            }
            else
            {
                this.HasLosToFormation = true;
                this.HasLosToLeader = true;
            }
        }
    }
}
