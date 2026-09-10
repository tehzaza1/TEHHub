// <copyright file="CentralBrain.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Brain
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Numerics;
    using AutoExile2.Modes;
    using GameHelper.RemoteObjects.Components;

    /// <summary>
    /// Cognitive Central Decision Brain (2.0):
    /// Advanced Utility-Based AI engine that arbitrates goals using dynamic utility scoring,
    /// action commitment buffers (hysteresis to eliminate oscillation), and autonomous stuck detection.
    /// </summary>
    public class CentralBrain
    {
        public BotGoal CurrentGoal { get; private set; } = BotGoal.Idle();

        public BotGoalType LastGoalType { get; private set; } = BotGoalType.Idle;

        public DateTime LastGoalChangeTime { get; private set; } = DateTime.Now;

        public double SecondsInCurrentGoal => (DateTime.Now - this.LastGoalChangeTime).TotalSeconds;

        public List<UtilityScore> LastEvaluationScores { get; } = new();

        // ── Hysteresis & Commitment Parameters ──
        private const float CommitmentBonus = 15f;
        private const double MinCommitmentSeconds = 0.35; // 350ms lock-in to prevent flickering

        // ── Autonomous Stuck Detection ──
        private Vector2 lastRecordedGrid = Vector2.Zero;
        private DateTime lastPositionChangeTime = DateTime.Now;
        private DateTime unstuckUntil = DateTime.MinValue;
        private Vector2 currentUnstuckTarget = Vector2.Zero;
        private int stuckOccurrenceCount = 0;

        public void Reset()
        {
            this.CurrentGoal = BotGoal.Idle();
            this.LastGoalType = BotGoalType.Idle;
            this.LastGoalChangeTime = DateTime.Now;
            this.lastRecordedGrid = Vector2.Zero;
            this.lastPositionChangeTime = DateTime.Now;
            this.unstuckUntil = DateTime.MinValue;
            this.currentUnstuckTarget = Vector2.Zero;
            this.stuckOccurrenceCount = 0;
            this.LastEvaluationScores.Clear();
        }

        /// <summary>
        /// Evaluates perception and settings for Co-op Follower mode using Utility AI arbitration.
        /// </summary>
        public BotGoal EvaluateCoopGoal(WorldPerception p, AutoExile2Settings s, bool isCurrentlySprinting)
        {
            var now = DateTime.Now;
            this.LastEvaluationScores.Clear();

            // 0. Peaceful Zone (Town / Hideout) -> Absolute Idle
            if (p.IsPeacefulZone)
            {
                var idleGoal = BotGoal.Idle("In Town/Hideout — standing by");
                return this.UpdateGoal(idleGoal);
            }

            // 1. Life & Death (Highest absolute priority: 100)
            if (p.IsFollowerDead)
            {
                var deadGoal = BotGoal.DeadOrRevive("Follower dead (HP: 0) — halting actions", p.FollowerEntity, p.FollowerGrid);
                return this.UpdateGoal(deadGoal);
            }

            if (p.IsLeaderDead)
            {
                var reviveGoal = BotGoal.DeadOrRevive("Leader dead (HP: 0) — ready to revive", p.LeaderEntity, p.LeaderGrid);
                return this.UpdateGoal(reviveGoal);
            }

            // 2. Stuck Detection Check
            this.UpdateStuckTracking(p.FollowerGrid, now);
            if (now < this.unstuckUntil)
            {
                var unstuckGoal = BotGoal.Unstuck("Obstacle collision detected — executing escape roll", this.currentUnstuckTarget);
                return this.UpdateGoal(unstuckGoal);
            }

            // 3. Compute Utility Scores for Candidate Goals
            var candidates = new List<(BotGoal Goal, float Score, string Reason)>();

            // 3.1 DangerEvade Utility (Hazard Threat + Low HP)
            float dangerScore = 0f;
            string dangerReason = string.Empty;
            if (p.Vision.Hazard.IsHighDangerArea)
            {
                float missingHpRatio = Math.Clamp((100f - p.FollowerHpPercent) / 100f, 0f, 1f);
                dangerScore = 60f + (missingHpRatio * 35f); // 60 to 95 utility
                dangerReason = $"High hazard threat ({p.Vision.Hazard.ThreatInProximity:F0}) & HP {p.FollowerHpPercent:F0}%";
            }

            if (dangerScore > 0f)
            {
                candidates.Add((BotGoal.DangerEvade(dangerReason, p.FormationTarget), dangerScore, dangerReason));
            }

            // 3.2 HardCatchup Utility (S-curve distance pull + Hysteresis)
            float sprintThreshold = Math.Max(50f, s.CoopSprintDistance);
            float safeStopDist = Math.Max(2f, s.CoopStopDistance);
            float catchupScore = 0f;
            string catchupReason = string.Empty;

            bool wasInSprint = this.CurrentGoal.Type == BotGoalType.HardCatchup || isCurrentlySprinting;

            if (wasInSprint && p.DistanceToLeader > safeStopDist)
            {
                // Hysteresis latch: once sprinting, hold high utility until arriving at leader safe distance
                catchupScore = 84f;
                catchupReason = $"Sprinting catch-up in progress ({p.DistanceToLeader:F0}/{safeStopDist:F0}g)";
            }
            else if (p.DistanceToLeader >= sprintThreshold)
            {
                // Dynamic distance scaling: 80 base + additional up to 96
                catchupScore = 80f + Math.Min(16f, (p.DistanceToLeader - sprintThreshold) * 0.4f);
                catchupReason = $"Leader far ahead ({p.DistanceToLeader:F0}/{sprintThreshold:F0}g)";
            }

            if (catchupScore > 0f)
            {
                candidates.Add((BotGoal.HardCatchup(catchupReason, p.FormationTarget), catchupScore, catchupReason));
            }

            // 3.3 Combat Utility (Culling + Hostiles engagement)
            float combatScore = 0f;
            string combatReason = string.Empty;
            if (s.P2EnableCombat && s.P2Skills != null)
            {
                bool hasEligibleCuller = false;
                foreach (var skill in s.P2Skills.Where(x => x.Enabled && (x.Category == "Culler" || x.Role == SkillRole.Culler)))
                {
                    float rawStart = skill.CullerStartAttackDistance > 0 ? skill.CullerStartAttackDistance : 35f;
                    float startGrid = rawStart > 150f ? (rawStart / p.GridToWorld) : rawStart;

                    if (p.DistanceToLeader <= startGrid)
                    {
                        if (!skill.CullerRequireMonsters || p.NearbyEnemyCount > 0)
                        {
                            hasEligibleCuller = true;
                            break;
                        }
                    }
                }

                bool hasTargetedCombat = p.BestCombatTarget != null && p.NearbyEnemyCount > 0;

                if (hasEligibleCuller || hasTargetedCombat)
                {
                    // Base combat utility: 62
                    combatScore = 62f;
                    if (p.NearbyEnemyCount > 0) combatScore += Math.Min(12f, p.NearbyEnemyCount * 2f);
                    if (p.Vision.Entities.HasDangerousRareOrBoss) combatScore += 6f;

                    combatReason = hasEligibleCuller
                        ? $"Culling front ({p.NearbyEnemyCount} enemies, {p.DistanceToLeader:F0}g from leader)"
                        : $"Targeting hostiles ({p.NearbyEnemyCount} enemies, closest {p.ClosestEnemyDistance:F0}g)";

                    candidates.Add((BotGoal.Combat(combatReason, p.BestCombatTarget, p.FormationTarget), combatScore, combatReason));
                }
            }

            // 3.4 Formation Follow Utility
            float followTriggerDist = Math.Max(safeStopDist + 1f, s.CoopFollowDistance);
            float followScore = 0f;
            string followReason = string.Empty;

            if (p.DistanceToLeader > followTriggerDist || p.DistanceToFormationTarget > safeStopDist)
            {
                followScore = 40f + Math.Min(15f, p.DistanceToFormationTarget * 0.5f);
                followReason = $"Escorting to {s.FollowerPosition} ({p.DistanceToLeader:F0}g from leader)";
                candidates.Add((BotGoal.FormationFollow(followReason, p.FormationTarget), followScore, followReason));
            }

            // 3.5 Idle Utility (in safe zone)
            float idleScore = 15f;
            string idleReason = $"Holding formation in safe zone ({p.DistanceToLeader:F0}g <= {safeStopDist:F0}g)";
            candidates.Add((BotGoal.Idle(idleReason), idleScore, idleReason));

            // 4. Apply Action Commitment Hysteresis Bonus
            // Gives the currently active goal an advantage to eliminate rapid oscillation
            double duration = (now - this.LastGoalChangeTime).TotalSeconds;
            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                float finalScore = c.Score;

                if (c.Goal.Type == this.CurrentGoal.Type && duration < MinCommitmentSeconds)
                {
                    finalScore += CommitmentBonus;
                }

                this.LastEvaluationScores.Add(new UtilityScore(c.Goal.Type, finalScore, c.Reason));
                candidates[i] = (c.Goal, finalScore, c.Reason);
            }

            // 5. Select Candidate with Maximum Utility Score
            var winner = candidates.OrderByDescending(c => c.Score).First();
            return this.UpdateGoal(winner.Goal);
        }

        /// <summary>
        /// Evaluates perception and settings for Solo / Exploration / WaveFarm modes.
        /// </summary>
        public BotGoal EvaluateSoloGoal(WorldPerception p, AutoExile2Settings s, bool mapClearComplete = false)
        {
            var now = DateTime.Now;

            if (p.IsPeacefulZone)
            {
                return this.UpdateGoal(BotGoal.Idle("In Town/Hideout — standing by"));
            }

            if (p.IsFollowerDead)
            {
                return this.UpdateGoal(BotGoal.DeadOrRevive("Player dead (HP: 0) — halting actions", p.FollowerEntity, p.FollowerGrid));
            }

            // Check Stuck
            this.UpdateStuckTracking(p.FollowerGrid, now);
            if (now < this.unstuckUntil)
            {
                return this.UpdateGoal(BotGoal.Unstuck("Obstacle collision detected — rolling unstuck", this.currentUnstuckTarget));
            }

            // Evade
            if (p.Vision.Hazard.IsHighDangerArea && p.FollowerHpPercent < 45f)
            {
                var safePos = p.Vision.Hazard.NearestMonsterCluster != null
                    ? p.FollowerGrid + (Vector2.Normalize(p.FollowerGrid - p.Vision.Hazard.NearestMonsterCluster.Value) * 15f)
                    : p.FollowerGrid;

                return this.UpdateGoal(BotGoal.DangerEvade(
                    $"Critical threat level ({p.Vision.Hazard.ThreatInProximity:F0}) — disengaging",
                    safePos));
            }

            // Map Complete -> Exit Map
            if (mapClearComplete || (p.Vision.Exploration.IsMapFullyExplored && p.NearbyEnemyCount == 0))
            {
                return this.UpdateGoal(BotGoal.ExitMap("Map clear complete — opening exit portal", p.Vision.Entities.NearestPortal));
            }

            // Combat
            if (p.NearbyEnemyCount > 0 && p.BestCombatTarget != null)
            {
                Vector2? targetPos = p.BestCombatTarget.TryGetComponent<Render>(out var r)
                    ? new Vector2(r.GridPosition.X, r.GridPosition.Y)
                    : null;

                return this.UpdateGoal(BotGoal.Combat(
                    $"Engaging hostiles ({p.NearbyEnemyCount} nearby, closest: {p.ClosestEnemyDistance:F0}g)",
                    p.BestCombatTarget,
                    targetPos));
            }

            // Explore
            if (p.Vision.Exploration.NextUnexploredTarget != null)
            {
                return this.UpdateGoal(BotGoal.Explore(
                    $"Exploring fog of war (Coverage: {p.Vision.Exploration.MapCoverage:F1}%)",
                    p.Vision.Exploration.NextUnexploredTarget.Value));
            }

            return this.UpdateGoal(BotGoal.Idle("No active exploration or combat target"));
        }

        /// <summary>
        /// Detects if the bot has been attempting to navigate but remains physically trapped.
        /// </summary>
        private void UpdateStuckTracking(Vector2 currentGrid, DateTime now)
        {
            // If already in an unstuck cycle, let it finish
            if (now < this.unstuckUntil)
            {
                return;
            }

            bool isMovingGoal = this.CurrentGoal.Type is BotGoalType.HardCatchup
                                or BotGoalType.FormationFollow
                                or BotGoalType.DangerEvade
                                or BotGoalType.Explore;

            if (!isMovingGoal)
            {
                this.lastRecordedGrid = currentGrid;
                this.lastPositionChangeTime = now;
                return;
            }

            float distMoved = Vector2.Distance(currentGrid, this.lastRecordedGrid);

            if (distMoved >= 2.0f)
            {
                // Successfully moving
                this.lastRecordedGrid = currentGrid;
                this.lastPositionChangeTime = now;
                this.stuckOccurrenceCount = 0;
            }
            else
            {
                // Not moving meaningfully
                double timeStationary = (now - this.lastPositionChangeTime).TotalSeconds;

                if (timeStationary >= 1.2)
                {
                    // Trigger Unstuck
                    this.stuckOccurrenceCount++;
                    this.unstuckUntil = now.AddMilliseconds(650);
                    this.lastPositionChangeTime = now;

                    // Calculate tangential / diagonal escape vector relative to desired target
                    Vector2 target = this.CurrentGoal.TargetPosition ?? (currentGrid + new Vector2(0, -10));
                    Vector2 toTarget = target - currentGrid;
                    if (toTarget.LengthSquared() < 0.1f)
                    {
                        toTarget = new Vector2(0, -1);
                    }

                    var norm = Vector2.Normalize(toTarget);
                    // Alternate clockwise / counter-clockwise based on occurrence count
                    Vector2 tangent = (this.stuckOccurrenceCount % 2 == 0)
                        ? new Vector2(-norm.Y, norm.X)
                        : new Vector2(norm.Y, -norm.X);

                    this.currentUnstuckTarget = currentGrid + (tangent * 14f);
                }
            }
        }

        private BotGoal UpdateGoal(BotGoal newGoal)
        {
            if (newGoal.Type != this.CurrentGoal.Type)
            {
                this.LastGoalType = this.CurrentGoal.Type;
                this.LastGoalChangeTime = DateTime.Now;
            }

            this.CurrentGoal = newGoal;
            return newGoal;
        }
    }
}
