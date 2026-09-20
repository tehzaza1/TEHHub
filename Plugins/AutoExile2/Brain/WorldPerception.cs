// <copyright file="WorldPerception.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Brain
{
    using System;
    using System.Numerics;
    using TEHhub.RemoteObjects;
    using TEHhub.RemoteObjects.Components;
    using TEHhub.RemoteObjects.States.InGameStateObjects;
    using AutoExile2.Modes;
    using AutoExile2.Systems;

    /// <summary>
    /// Captures a clean, unified perception snapshot of the game state for the Central Decision Brain.
    /// Incorporates the BotVision sensory aggregator.
    /// </summary>
    public class WorldPerception
    {
        public BotVision Vision { get; set; } = new();

        public bool IsPeacefulZone { get; set; }

        public Entity? LeaderEntity { get; set; }

        public Vector2 LeaderGrid { get; set; }

        public bool IsLeaderDead { get; set; }

        public float LeaderHpPercent { get; set; } = 100f;

        public Vector2 LeaderHeading { get; set; } = new Vector2(0, -1);

        public bool IsLeaderSprinting { get; set; }

        public Entity? FollowerEntity { get; set; }

        public Vector2 FollowerGrid { get; set; }

        public bool IsFollowerDead { get; set; }

        public float FollowerHpPercent { get; set; } = 100f;

        public float FollowerManaPercent { get; set; } = 100f;

        public int FollowerAnimId { get; set; }

        public float DistanceToLeader { get; set; }

        public float DistanceToLeaderWorld { get; set; }

        public Vector2 FormationTarget { get; set; }

        public float DistanceToFormationTarget { get; set; }

        public int NearbyEnemyCount { get; set; }

        public float ClosestEnemyDistance { get; set; } = float.MaxValue;

        public Entity? BestCombatTarget { get; set; }

        public Vector2 PackCenter { get; set; }

        public bool HasLosToFormation { get; set; }

        public bool HasBlockingMonstersInPath { get; set; }

        public byte[]? WalkableData { get; set; }

        public int BytesPerRow { get; set; }

        public int Rows { get; set; }

        public int Cols { get; set; }

        public float GridToWorld { get; set; } = 10.87f;

        // ── Loot Sensory Shortcuts ──
        public bool HasLootNearby => this.Vision.Loot.HasValuableLootNearby;

        public Entity? NearestLootEntity => this.Vision.Loot.NearestLootEntity;

        public Vector2? NearestLootPosition => this.Vision.Loot.NearestLootPosition;

        public float NearestLootDistance => this.Vision.Loot.NearestLootDistance;

        public string NearestLootName => this.Vision.Loot.NearestLootName;

        public bool CanPickupNow => this.Vision.Loot.CanPickupNow;

        // ── Telegraph & Slam Sensory Shortcuts ──
        public bool HasIncomingSlam => this.Vision.Telegraph.HasIncomingSlam;

        public Entity? SlamSourceEntity => this.Vision.Telegraph.SlamSourceEntity;

        public Vector2? SlamEpicenter => this.Vision.Telegraph.SlamEpicenter;

        public Vector2 SlamEvadeVector => this.Vision.Telegraph.EvadeVector;

        /// <summary>
        /// Collects current sensory data for Co-op Follower mode.
        /// </summary>
        public static WorldPerception CollectCoop(
            BotContext ctx,
            Entity leader,
            Vector2 leaderGrid,
            Entity? follower,
            Vector2 followerGrid,
            Vector2 leaderHeading,
            int followerAnimId,
            Vector2 formationTarget,
            int nearbyEnemies,
            float closestDist,
            Entity? bestTarget,
            Vector2 packCenter)
        {
            var p = new WorldPerception();
            var world = ctx.World;
            var area = ctx.Area;

            // 1. Run full sensory scan through BotVision (Eyes)
            p.Vision.Scan(
                ctx,
                leader,
                leaderGrid,
                follower,
                followerGrid,
                leaderHeading,
                followerAnimId,
                formationTarget,
                nearbyEnemies,
                closestDist,
                bestTarget);

            // 2. Mirror properties for backward compatibility
            p.IsPeacefulZone = world.AreaDetails.IsTown || world.AreaDetails.IsHideout;
            p.LeaderEntity = p.Vision.Entities.LeaderEntity;
            p.LeaderGrid = p.Vision.Entities.LeaderGrid;
            p.LeaderHeading = p.Vision.Entities.LeaderHeading;
            p.IsLeaderSprinting = p.Vision.Entities.IsLeaderSprinting;
            p.IsLeaderDead = p.Vision.Entities.IsLeaderDead;
            p.LeaderHpPercent = p.Vision.Entities.LeaderHpPercent;

            p.FollowerEntity = p.Vision.Entities.FollowerEntity;
            p.FollowerGrid = p.Vision.Entities.FollowerGrid;
            p.FollowerAnimId = p.Vision.Entities.FollowerAnimId;
            p.IsFollowerDead = p.Vision.Entities.IsFollowerDead;
            p.FollowerHpPercent = p.Vision.Entities.FollowerHpPercent;
            p.FollowerManaPercent = p.Vision.Entities.FollowerManaPercent;

            p.GridToWorld = area.WorldToGridConvertor > 0 ? area.WorldToGridConvertor : 10.87f;
            p.DistanceToLeader = p.Vision.Spatial.DistanceToLeader;
            p.DistanceToLeaderWorld = p.DistanceToLeader * p.GridToWorld;

            p.FormationTarget = formationTarget;
            p.DistanceToFormationTarget = p.Vision.Spatial.DistanceToFormation;

            p.NearbyEnemyCount = p.Vision.Entities.NearbyEnemyCount;
            p.ClosestEnemyDistance = p.Vision.Entities.ClosestEnemyDistance;
            p.BestCombatTarget = p.Vision.Entities.BestCombatTarget;
            p.PackCenter = packCenter;

            p.WalkableData = area.GridWalkableData;
            p.BytesPerRow = area.TerrainMetadata.BytesPerRow;
            p.Rows = p.WalkableData != null && p.BytesPerRow > 0 ? p.WalkableData.Length / p.BytesPerRow : 0;
            p.Cols = p.BytesPerRow * 2;
            p.HasLosToFormation = p.Vision.Spatial.HasLosToFormation;
            p.HasBlockingMonstersInPath = p.Vision.Spatial.HasBlockingMonstersInPath;

            return p;
        }

        /// <summary>
        /// Collects sensory data for Solo or WaveFarm modes.
        /// </summary>
        public static WorldPerception CollectSolo(
            BotContext ctx,
            Entity player,
            Vector2 playerGrid,
            int nearbyEnemies,
            float closestDist,
            Entity? bestTarget)
        {
            var p = new WorldPerception();
            var world = ctx.World;
            var area = ctx.Area;

            p.Vision.Scan(
                ctx,
                leader: null,
                leaderGrid: playerGrid,
                follower: player,
                followerGrid: playerGrid,
                leaderHeading: Vector2.Zero,
                followerAnimId: 0,
                formationTarget: playerGrid,
                nearbyEnemies: nearbyEnemies,
                closestDist: closestDist,
                bestTarget: bestTarget);

            p.IsPeacefulZone = world.AreaDetails.IsTown || world.AreaDetails.IsHideout;
            p.FollowerEntity = player;
            p.FollowerGrid = playerGrid;
            p.IsFollowerDead = p.Vision.Entities.IsFollowerDead;
            p.FollowerHpPercent = p.Vision.Entities.FollowerHpPercent;
            p.FollowerManaPercent = p.Vision.Entities.FollowerManaPercent;

            p.GridToWorld = area.WorldToGridConvertor > 0 ? area.WorldToGridConvertor : 10.87f;
            p.DistanceToLeader = 0f;
            p.DistanceToLeaderWorld = 0f;
            p.FormationTarget = playerGrid;
            p.DistanceToFormationTarget = 0f;

            p.NearbyEnemyCount = nearbyEnemies;
            p.ClosestEnemyDistance = closestDist;
            p.BestCombatTarget = bestTarget;

            p.WalkableData = area.GridWalkableData;
            p.BytesPerRow = area.TerrainMetadata.BytesPerRow;
            p.Rows = p.WalkableData != null && p.BytesPerRow > 0 ? p.WalkableData.Length / p.BytesPerRow : 0;
            p.Cols = p.BytesPerRow * 2;
            p.HasLosToFormation = true;

            return p;
        }
    }
}
