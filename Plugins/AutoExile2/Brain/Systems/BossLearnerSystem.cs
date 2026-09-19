// <copyright file="BossLearnerSystem.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Systems
{
    using System;
    using System.Numerics;
    using TEHhub.RemoteObjects.Components;
    using AutoExile2.Modes;

    /// <summary>
    /// Tracks Boss Checkpoint and Fog Door encounters in WaveFarm mode.
    /// </summary>
    public class BossLearnerSystem
    {
        public Vector2? BossCheckpointPos { get; private set; }
        public Vector2? BossDoorPos { get; private set; }
        public bool HasBossTarget => this.BossDoorPos.HasValue || this.BossCheckpointPos.HasValue;
        public bool BossDoorReached { get; private set; }
        public bool BossReached { get; private set; }

        public void Reset()
        {
            this.BossCheckpointPos = null;
            this.BossDoorPos = null;
            this.BossDoorReached = false;
            this.BossReached = false;
        }

        public void Update(BotContext ctx, Vector2 playerPos)
        {
            var area = ctx.Area;
            if (area?.AwakeEntities == null)
            {
                return;
            }

            // 1. Scan AwakeEntities for Boss Checkpoint & Door
            foreach (var entity in area.AwakeEntities.Values)
            {
                string path = entity.Path ?? string.Empty;

                if (!this.BossCheckpointPos.HasValue && 
                    (path.Contains("Checkpoint_Endgame_Boss", StringComparison.OrdinalIgnoreCase) ||
                     (entity.TryGetComponent<MinimapIcon>(out var mi) && mi.IconName != null && mi.IconName.Contains("BossCheckpoint", StringComparison.OrdinalIgnoreCase))))
                {
                    if (entity.TryGetComponent<Render>(out var pos))
                    {
                        this.BossCheckpointPos = new Vector2(pos.GridPosition.X, pos.GridPosition.Y);
                        ctx.Log($"[BossSystem] Detected Boss Checkpoint at ({this.BossCheckpointPos.Value.X:F0}, {this.BossCheckpointPos.Value.Y:F0})");
                    }
                }

                if (!this.BossDoorPos.HasValue && path.Contains("BossForceFieldDoorVisuals", StringComparison.OrdinalIgnoreCase))
                {
                    if (entity.TryGetComponent<Render>(out var pos))
                    {
                        this.BossDoorPos = new Vector2(pos.GridPosition.X, pos.GridPosition.Y);
                        ctx.Log($"[BossSystem] Detected Boss Fog Door at ({this.BossDoorPos.Value.X:F0}, {this.BossDoorPos.Value.Y:F0})");
                    }
                }
            }

            // 2. Track progression explicitly: reaching the fog door is not the same as
            // reaching the boss checkpoint inside/after the transition.
            if (this.BossDoorPos.HasValue && Vector2.Distance(playerPos, this.BossDoorPos.Value) <= 15f)
            {
                this.BossDoorReached = true;
            }

            if (this.BossCheckpointPos.HasValue && Vector2.Distance(playerPos, this.BossCheckpointPos.Value) <= 25f)
            {
                this.BossReached = true;
            }
        }
    }
}
