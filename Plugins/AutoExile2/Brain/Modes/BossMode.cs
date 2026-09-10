// <copyright file="BossMode.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Modes
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;
    using GameHelper.RemoteEnums;
    using GameHelper.RemoteEnums.Entity;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.RemoteObjects.States.InGameStateObjects;
    using GameHelper.Utils;
    using GameOffsets.Natives;
    using ImGuiNET;
    using AutoExile2.Systems;

    /// <summary>
    /// Dedicated Boss Encounter Mode for PoE 2.
    /// Focuses on high-priority Unique bosses, tactical combat spacing, and boss arena encounters.
    /// Ported directly from AutoExile 1 BossMode architecture.
    /// </summary>
    public sealed class BossMode : IBotMode
    {
        public string Name => "Boss";

        public string Description => "Arena boss targeting & spacing";

        public string Icon => "⚔️";

        public AutoExileMode ModeType => AutoExileMode.Boss;

        public string CurrentState { get; private set; } = "Searching";

        public string CurrentAction { get; private set; } = "Locating boss";

        public List<Vector2> CurrentNavPath { get; private set; } = new();

        public int CurrentWaypointIndex { get; private set; } = 0;

        public Vector2? CurrentDestination { get; private set; }

        private Vector2? currentBossGrid;
        public Vector2? CurrentBossGrid => this.currentBossGrid;
        private string bossName = string.Empty;
        private int bossHpPct = 100;

        public void OnEnter(BotContext ctx)
        {
            ctx.Log("Entering Boss Mode");
            this.CurrentNavPath.Clear();
            this.CurrentWaypointIndex = 0;
            this.CurrentDestination = null;
            this.currentBossGrid = null;
            this.bossName = string.Empty;
            this.CurrentState = "Searching";
            this.CurrentAction = "Searching for boss encounter";
        }

        public void OnExit(BotContext ctx)
        {
            ctx.Log("Exiting Boss Mode");
            BotInput.ReleaseAllMovementKeys(ctx.Settings);
        }

        public void Tick(BotContext ctx)
        {
            var area = ctx.Area;
            var world = ctx.World;
            var player = ctx.Player;
            var playerGrid = ctx.PlayerGrid;

            // 1. Search for active Unique Boss in AwakeEntities
            Entity? targetBoss = null;
            float closestBossDist = float.MaxValue;

            foreach (var kvp in area.AwakeEntities)
            {
                var ent = kvp.Value;
                if (!ent.IsValid || ent.Address == player.Address)
                {
                    continue;
                }

                if (!ent.TryGetComponent<Life>(out var life) || life.Health.Current <= 0)
                {
                    continue;
                }

                if (ent.TryGetComponent<ObjectMagicProperties>(out var omp) && omp.Rarity == Rarity.Unique)
                {
                    if (ent.TryGetComponent<Render>(out var bRender))
                    {
                        var bGrid = new Vector2(bRender.GridPosition.X, bRender.GridPosition.Y);
                        float dist = Vector2.Distance(playerGrid, bGrid);
                        if (dist < closestBossDist)
                        {
                            closestBossDist = dist;
                            targetBoss = ent;
                        }
                    }
                }
            }

            // 2. Engage Boss
            if (targetBoss != null && targetBoss.TryGetComponent<Render>(out var bossRender))
            {
                this.currentBossGrid = new Vector2(bossRender.GridPosition.X, bossRender.GridPosition.Y);
                if (targetBoss.TryGetComponent<Life>(out var bLife) && bLife.Health.Total > 0)
                {
                    this.bossHpPct = (int)((float)bLife.Health.Current / bLife.Health.Total * 100f);
                }

                string path = targetBoss.Path ?? "Boss";
                this.bossName = path.Contains('/') ? path.Substring(path.LastIndexOf('/') + 1) : path;

                this.CurrentState = "Boss Fight";
                this.CurrentAction = $"Engaging {this.bossName} ({this.bossHpPct}% HP, {closestBossDist:F0}g)";

                // Execute combat attack
                using (ctx.Perf.SectionScope("Combat"))
                {
                    ctx.Combat.TickCombat(area, world, player, ctx.Settings);
                }

                // Tactical spacing: if too close (< 20g) to a lethal boss, back up slightly
                if (closestBossDist < 18f)
                {
                    var awayGrid = playerGrid + Vector2.Normalize(playerGrid - this.currentBossGrid.Value) * 10f;
                    var screenDir = BotInput.GridToScreenDirection(world, player, awayGrid, playerGrid, area.WorldToGridConvertor);
                    BotInput.WasdMove(screenDir, ctx.Settings);
                }
                else if (closestBossDist > ctx.Settings.CombatRange)
                {
                    // Move closer into attack range
                    var screenDir = BotInput.GridToScreenDirection(world, player, this.currentBossGrid.Value, playerGrid, area.WorldToGridConvertor);
                    BotInput.WasdMove(screenDir, ctx.Settings);
                }
                else
                {
                    // In good combat pocket: release movement keys to aim and fire
                    BotInput.ReleaseAllMovementKeys(ctx.Settings);
                }
            }
            else
            {
                // Boss not spotted or already dead
                this.CurrentState = "Cleared / Searching";
                this.CurrentAction = "Boss not found in immediate range";
                this.currentBossGrid = null;

                // Fallback to clearing minions or standing by
                bool inCombat = false;
                using (ctx.Perf.SectionScope("Combat"))
                {
                    inCombat = ctx.Combat.TickCombat(area, world, player, ctx.Settings);
                }

                if (!inCombat)
                {
                    BotInput.ReleaseAllMovementKeys(ctx.Settings);
                }
            }
        }

        public void Render(BotContext ctx)
        {
            if (ctx.World == null || !this.currentBossGrid.HasValue)
            {
                return;
            }

            var draw = ImGui.GetForegroundDrawList();
            float convertor = ctx.Area.WorldToGridConvertor;

            var sPos = ctx.World.WorldToScreen(new StdTuple3D<float> { X = this.currentBossGrid.Value.X * convertor, Y = this.currentBossGrid.Value.Y * convertor, Z = 0 });
            if (sPos != Vector2.Zero)
            {
                draw.AddCircle(sPos, 22f, ImGuiHelper.Color(255, 60, 40, 240), 24, 3f);
                string text = $"BOSS: {this.bossName} ({this.bossHpPct}%)";
                draw.AddText(sPos + new Vector2(-40, -35), ImGuiHelper.Color(255, 220, 80, 255), text);
            }
        }
    }
}
