// <copyright file="WorldPerception.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Brain
{
    using System;
    using System.Numerics;
    using GameHelper.RemoteObjects;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.RemoteObjects.States.InGameStateObjects;
    using AutoExile2.Modes;
    using AutoExile2.Systems;

    /// <summary>
    /// Captures a clean, unified perception snapshot of the game state for the Central Decision Brain.
    /// </summary>
    public class WorldPerception
    {
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

        public bool HasLosToFormation { get; set; }

        public byte[]? WalkableData { get; set; }

        public int BytesPerRow { get; set; }

        public int Rows { get; set; }

        public int Cols { get; set; }

        public float GridToWorld { get; set; } = 10.87f;

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
            Entity? bestTarget)
        {
            var p = new WorldPerception();
            var world = ctx.World;
            var area = ctx.Area;

            p.IsPeacefulZone = world.AreaDetails.IsTown || world.AreaDetails.IsHideout;
            p.LeaderEntity = leader;
            p.LeaderGrid = leaderGrid;
            p.LeaderHeading = leaderHeading;
            p.IsLeaderSprinting = ctx.CoopGamepad.IsLeaderSprinting;
            p.GridToWorld = area.WorldToGridConvertor > 0 ? area.WorldToGridConvertor : 10.87f;

            if (leader.TryGetComponent<Life>(out var lLife) && lLife.Health.Total > 0)
            {
                p.IsLeaderDead = lLife.Health.Current <= 0;
                p.LeaderHpPercent = (float)lLife.Health.Current / lLife.Health.Total * 100f;
            }

            p.FollowerEntity = follower;
            p.FollowerGrid = followerGrid;
            p.FollowerAnimId = followerAnimId;

            if (follower != null && follower.TryGetComponent<Life>(out var fLife) && fLife.Health.Total > 0)
            {
                p.IsFollowerDead = fLife.Health.Current <= 0;
                p.FollowerHpPercent = (float)fLife.Health.Current / fLife.Health.Total * 100f;
                p.FollowerManaPercent = fLife.Mana.Total > 0 ? ((float)fLife.Mana.Current / fLife.Mana.Total * 100f) : 100f;
            }

            p.DistanceToLeader = Vector2.Distance(followerGrid, leaderGrid);
            p.DistanceToLeaderWorld = p.DistanceToLeader * p.GridToWorld;

            p.FormationTarget = formationTarget;
            p.DistanceToFormationTarget = Vector2.Distance(followerGrid, formationTarget);

            p.NearbyEnemyCount = nearbyEnemies;
            p.ClosestEnemyDistance = closestDist;
            p.BestCombatTarget = bestTarget;

            p.WalkableData = area.GridWalkableData;
            p.BytesPerRow = area.TerrainMetadata.BytesPerRow;
            p.Rows = p.WalkableData != null && p.BytesPerRow > 0 ? p.WalkableData.Length / p.BytesPerRow : 0;
            p.Cols = p.BytesPerRow * 2;

            if (p.WalkableData != null && p.BytesPerRow > 0)
            {
                p.HasLosToFormation = Pathfinding.HasLineOfSight(p.WalkableData, p.BytesPerRow, followerGrid, formationTarget, p.Rows, p.Cols, 3);
            }

            return p;
        }
    }
}
