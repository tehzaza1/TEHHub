// <copyright file="WaveFarmMode.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Modes.WaveFarm
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.RemoteObjects.States.InGameStateObjects;
    using GameHelper.Utils;
    using GameOffsets.Natives;
    using ImGuiNET;
    using AutoExile2.Modes.WaveFarm.FarmPlans;
    using AutoExile2.Systems;

    /// <summary>
    /// Wave-based map farming mode for PoE 2.
    /// The bot is always a wave moving forward through the map. Combat happens continuously while exploring.
    /// Orchestrates: hideout standby -> map entry -> wave tick loop -> exit.
    /// Ported directly from AutoExile 1 WaveFarmMode.
    /// </summary>
    public class WaveFarmMode : IBotMode
    {
        public string Name => "MapFarm";

        public string Description => "Auto explore, clear & loot maps";

        public string Icon => "🗺️";

        public AutoExileMode ModeType => AutoExileMode.MapFarm;

        public string CurrentState => $"[{this.phase}] {this.Status}";

        public string CurrentAction => this.Decision;

        public List<Vector2> CurrentNavPath => this.wave.CurrentNavPath;

        public int CurrentWaypointIndex => this.wave.CurrentWaypointIndex;

        public Vector2? CurrentDestination => this.wave.CurrentDestination;

        private readonly WaveTick wave = new();
        private readonly ZoneStateCache zoneCache = new();
        private readonly ExitHandler exitHandler = new();
        private readonly Dictionary<string, IFarmPlan> plans = new();
        private IFarmPlan? activePlan;

        private WaveFarmPhase phase = WaveFarmPhase.Idle;
        private string lastAreaHash = string.Empty;
        private string lastAreaName = string.Empty;
        private int runsCompleted;
        private bool mapCompleted;

        public string Status { get; private set; } = string.Empty;
        public string Decision => this.wave.Decision;
        public int RunsCompleted => this.runsCompleted;

        public WaveFarmMode()
        {
            this.RegisterPlan(new AlchAndGoPlan());
        }

        public void RegisterPlan(IFarmPlan plan) => this.plans[plan.Name] = plan;

        public void OnEnter(BotContext ctx)
        {
            ctx.Log("Entering WaveFarm Mode");
            this.runsCompleted = 0;
            this.mapCompleted = false;
            this.exitHandler.Reset();

            if (this.activePlan == null)
            {
                this.activePlan = new AlchAndGoPlan();
            }

            var world = ctx.World;
            var area = ctx.Area;
            this.lastAreaName = world.AreaDetails.Name;
            this.lastAreaHash = area.AreaHash;

            if (world.AreaDetails.IsHideout || world.AreaDetails.IsTown)
            {
                this.phase = WaveFarmPhase.InHideout;
                this.Status = "In Town/Hideout — standing by";
            }
            else
            {
                this.InitMapState(ctx);
            }
        }

        public void OnExit(BotContext ctx)
        {
            ctx.Log("Exiting WaveFarm Mode");
            this.phase = WaveFarmPhase.Idle;
            this.wave.Reset();
            this.exitHandler.Reset();
            this.zoneCache.Clear();
            this.mapCompleted = false;
            BotInput.ReleaseAllMovementKeys(ctx.Settings);
        }

        public void Tick(BotContext ctx)
        {
            var area = ctx.Area;
            var world = ctx.World;

            // Area change detection
            string currentArea = world.AreaDetails.Name;
            string currentHash = area.AreaHash;

            if (currentArea != this.lastAreaName || currentHash != this.lastAreaHash)
            {
                this.OnAreaChanged(ctx, currentArea, currentHash);
                this.lastAreaName = currentArea;
                this.lastAreaHash = currentHash;
            }

            switch (this.phase)
            {
                case WaveFarmPhase.InHideout:
                    this.TickHideout(ctx);
                    break;
                case WaveFarmPhase.InMap:
                    this.TickInMap(ctx);
                    break;
                case WaveFarmPhase.ExitMap:
                    this.TickExitMap(ctx);
                    break;
            }
        }

        public void Render(BotContext ctx)
        {
            var drawList = ImGui.GetBackgroundDrawList();
            var io = ImGui.GetIO();

            // Overlay display in top-left
            float x = 20f;
            float y = 100f;
            const float lineH = 18f;

            uint colAccent = 0xFF00FF00; // LimeGreen
            uint colBright = 0xFFFFFFFF; // White
            uint colDim = 0xFFAAAAAA;    // Gray
            uint colWarn = 0xFF00FFFF;   // Yellow
            uint colBad = 0xFF0000FF;    // Red/Orange

            // Phase + Status
            drawList.AddText(new Vector2(x, y), colAccent, $"[WaveFarm: {this.phase}] {this.Status}");
            y += lineH;

            // Decision
            drawList.AddText(new Vector2(x, y), colBright, $"Decision: {this.Decision}");
            y += lineH;

            // Exploration
            float cov = ctx.Exploration.ActiveBlobCoverage;
            uint covCol = cov > 0.9f ? colAccent : cov > 0.5f ? colBright : colWarn;
            drawList.AddText(new Vector2(x, y), covCol, $"Coverage: {cov:P0}  Regions: {ctx.Exploration.ActiveBlob?.Regions.Count ?? 0}");
            y += lineH;

            // ThreatMap
            if (ctx.ThreatMap.IsInitialized)
            {
                var tm = ctx.ThreatMap;
                drawList.AddText(new Vector2(x, y), colDim, $"Threats: {tm.TotalAlive} alive / {tm.TotalTracked} seen / {tm.TotalDead} dead");
                y += lineH;
            }

            // ClearPlan
            var plan = this.wave.ClearPlan;
            if (plan.IsInitialized)
            {
                uint planCol = plan.IsComplete ? colAccent : plan.CurrentTarget.HasValue ? colBright : colWarn;
                drawList.AddText(new Vector2(x, y), planCol, $"ClearPlan: {plan.VisitedCount}/{plan.TotalCount} chunks — {plan.Status}");
                y += lineH;
            }

            // Boss Encounter
            var boss = this.wave.BossLearner;
            if (boss.HasBossTarget)
            {
                string bossDesc = boss.BossReached ? "Arena Entered (Engaging / Cleared)" : boss.BossDoorPos.HasValue ? "Approaching Fog Door -> Checkpoint" : "Navigating to Boss Checkpoint";
                uint bossCol = boss.BossReached ? colAccent : colWarn;
                drawList.AddText(new Vector2(x, y), bossCol, $"Boss: {bossDesc}");
                y += lineH;
            }

            // Combat
            var combat = ctx.Combat;
            if (combat.NearbyHostileCount > 0)
            {
                string targetDesc = !string.IsNullOrEmpty(combat.CurrentTargetName)
                    ? $"Target: [{combat.CurrentTargetRarity}] {combat.CurrentTargetName} (#{combat.CurrentTargetId})"
                    : $"Target ID: #{combat.CurrentTargetId}";
                drawList.AddText(new Vector2(x, y), colBad, $"Combat: {combat.NearbyHostileCount} enemies ({targetDesc}, dist: {combat.ClosestHostileDistance:F0}g)");
            }
            else
            {
                drawList.AddText(new Vector2(x, y), colDim, "Combat: idle");
            }
            y += lineH;

            // Nav
            if (this.wave.CurrentNavPath.Count > 0)
            {
                var dest = this.wave.CurrentDestination ?? Vector2.Zero;
                drawList.AddText(new Vector2(x, y), colBright, $"Nav: wp {this.wave.CurrentWaypointIndex}/{this.wave.CurrentNavPath.Count} -> ({dest.X:F0},{dest.Y:F0})");
            }
            else
            {
                drawList.AddText(new Vector2(x, y), colDim, "Nav: stopped");
            }
            y += lineH;

            // Runs completed
            drawList.AddText(new Vector2(x, y), colDim, $"Runs completed: {this.runsCompleted}");

            // Render Nav Path in world space
            if (this.wave.CurrentNavPath.Count > 0 && ctx.World != null && ctx.Player.TryGetComponent<GameHelper.RemoteObjects.Components.Render>(out var pRender))
            {
                float convertor = ctx.Area.WorldToGridConvertor;
                float playerTerrainZ = pRender.TerrainHeight;
                var heightData = ctx.Area.GridHeightData;

                // 1. Build points sequence: Player Position -> CurrentWaypoint -> Remaining Waypoints
                var renderPoints = new List<Vector2>();
                var playerPos = new Vector2(pRender.GridPosition.X, pRender.GridPosition.Y);
                renderPoints.Add(playerPos);

                int maxRenderWp = Math.Min(this.wave.CurrentNavPath.Count, this.wave.CurrentWaypointIndex + 25);
                for (int i = this.wave.CurrentWaypointIndex; i < maxRenderWp; i++)
                {
                    renderPoints.Add(this.wave.CurrentNavPath[i]);
                }

                // 2. Subdivide segments into small steps (~10 grid units) so long lines that cross the screen edge
                // clip gracefully instead of dropping the entire 100-unit segment!
                const float maxStep = 10f;
                uint colPathLine = 0xFF00FF00;      // LimeGreen line
                uint colWaypointDot = 0xFF00FFFF;   // Yellow dots for waypoints
                uint colDestStar = 0xFF00A5FF;      // Orange destination marker

                Vector2? prevScreen = null;

                for (int seg = 0; seg < renderPoints.Count - 1; seg++)
                {
                    var a = renderPoints[seg];
                    var b = renderPoints[seg + 1];
                    float segDist = Vector2.Distance(a, b);
                    int steps = Math.Max(1, (int)MathF.Ceiling(segDist / maxStep));

                    for (int step = 0; step <= steps; step++)
                    {
                        float t = (float)step / steps;
                        var pt = Vector2.Lerp(a, b, t);
                        float ptZ = Pathfinding.GetTerrainHeight(heightData, (int)pt.X, (int)pt.Y, playerTerrainZ);
                        var screenPt = ctx.World.WorldToScreen(new Vector2(pt.X * convertor, pt.Y * convertor), ptZ);

                        if (screenPt != Vector2.Zero && prevScreen.HasValue && prevScreen.Value != Vector2.Zero)
                        {
                            drawList.AddLine(prevScreen.Value, screenPt, colPathLine, 3.0f);
                        }

                        prevScreen = (screenPt != Vector2.Zero) ? screenPt : null;
                    }
                }

                // 3. Draw waypoint dots at each actual waypoint node
                for (int i = this.wave.CurrentWaypointIndex; i < maxRenderWp; i++)
                {
                    var wp = this.wave.CurrentNavPath[i];
                    float wpZ = Pathfinding.GetTerrainHeight(heightData, (int)wp.X, (int)wp.Y, playerTerrainZ);
                    var wpScreen = ctx.World.WorldToScreen(new Vector2(wp.X * convertor, wp.Y * convertor), wpZ);
                    if (wpScreen != Vector2.Zero)
                    {
                        bool isDest = (i == this.wave.CurrentNavPath.Count - 1);
                        if (isDest)
                        {
                            drawList.AddCircleFilled(wpScreen, 6f, colDestStar);
                            drawList.AddCircle(wpScreen, 9f, 0xFFFFFFFF, 12, 2f);
                        }
                        else
                        {
                            drawList.AddCircleFilled(wpScreen, 3.5f, colWaypointDot);
                        }
                    }
                }
            }
        }

        private void InitMapState(BotContext ctx)
        {
            this.phase = WaveFarmPhase.InMap;
            this.mapCompleted = false;
            this.exitHandler.Reset();

            if (this.activePlan == null)
            {
                this.activePlan = new AlchAndGoPlan();
            }

            this.wave.Initialize(this.activePlan);
            this.activePlan.Reset();

            var walkableData = ctx.Area.GridWalkableData;
            int bytesPerRow = ctx.Area.TerrainMetadata.BytesPerRow;
            string areaHash = ctx.Area.AreaHash;
            var playerPos = ctx.PlayerGrid;

            // Initialize systems
            ctx.Exploration.Initialize(walkableData, bytesPerRow, playerPos, areaHash);
            ctx.ThreatMap.Initialize(walkableData, bytesPerRow);
            ctx.ThreatMap.RebuildFromEntities(ctx.Area.AwakeEntities.Values);
            this.wave.ClearPlan.Initialize(ctx);

            this.Status = "In map — wave clearing";
            ctx.Log($"[WaveFarm] Map initialized: {ctx.World.AreaDetails.Name}");
        }

        private void TickHideout(BotContext ctx)
        {
            this.Status = "In Hideout/Town — ready";
            BotInput.ReleaseAllMovementKeys(ctx.Settings);
        }

        private void TickInMap(BotContext ctx)
        {
            var playerPos = ctx.PlayerGrid;

            // Update exploration coverage
            ctx.Exploration.Update(playerPos);

            // Reconcile monster threat tracking
            ctx.ThreatMap.Reconcile(playerPos, ctx.Area);

            // Wave tick
            bool done = this.wave.Tick(ctx);
            this.Status = this.wave.Status;

            if (done)
            {
                this.phase = WaveFarmPhase.ExitMap;
                this.exitHandler.Reset();
                this.Status = "Map complete — exiting";
                ctx.Log("[WaveFarm] Map clear complete, exiting");
            }
        }

        private void TickExitMap(BotContext ctx)
        {
            this.exitHandler.Tick(ctx);
            this.Status = this.exitHandler.Status;
        }

        private void OnAreaChanged(BotContext ctx, string newArea, string newHash)
        {
            var world = ctx.World;
            BotInput.ReleaseAllMovementKeys(ctx.Settings);

            if (world.AreaDetails.IsHideout || world.AreaDetails.IsTown)
            {
                this.wave.Reset();
                bool wasExiting = this.phase == WaveFarmPhase.ExitMap;
                this.phase = WaveFarmPhase.InHideout;

                if (this.mapCompleted || wasExiting)
                {
                    this.mapCompleted = false;
                    this.runsCompleted++;
                    ctx.Log($"[WaveFarm] Run #{this.runsCompleted} complete");
                }
                else
                {
                    this.Status = "In Hideout — standing by";
                }
            }
            else
            {
                ctx.Log($"[WaveFarm] Entered map: {newArea}");
                this.InitMapState(ctx);
            }
        }
    }

    internal enum WaveFarmPhase
    {
        Idle,
        InHideout,
        InMap,
        ExitMap,
    }
}
