// <copyright file="CentralBrain.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Brain
{
    using System;
    using System.Linq;
    using System.Numerics;
    using AutoExile2.Modes;

    /// <summary>
    /// Central Decision Brain: Evaluates the world perception and arbitrates priorities
    /// to choose the single active goal for the bot at any given moment.
    /// </summary>
    public class CentralBrain
    {
        public BotGoal CurrentGoal { get; private set; } = BotGoal.Idle();

        public BotGoalType LastGoalType { get; private set; } = BotGoalType.Idle;

        public DateTime LastGoalChangeTime { get; private set; } = DateTime.Now;

        public double SecondsInCurrentGoal => (DateTime.Now - this.LastGoalChangeTime).TotalSeconds;

        public void Reset()
        {
            this.CurrentGoal = BotGoal.Idle();
            this.LastGoalType = BotGoalType.Idle;
            this.LastGoalChangeTime = DateTime.Now;
        }

        /// <summary>
        /// Evaluates perception and settings for Co-op Follower mode to select the highest-priority goal.
        /// </summary>
        public BotGoal EvaluateCoopGoal(WorldPerception p, AutoExile2Settings s, bool isCurrentlySprinting)
        {
            BotGoal goal;

            // 1. Peaceful Zone (Town / Hideout) -> Idle
            if (p.IsPeacefulZone)
            {
                goal = BotGoal.Idle("In Town/Hideout — standing by");
                return this.UpdateGoal(goal);
            }

            // 2. Life / Death Priority (DeadOrRevive)
            if (p.IsFollowerDead)
            {
                goal = BotGoal.DeadOrRevive("Follower dead (HP: 0) — halting actions", p.FollowerEntity, p.FollowerGrid);
                return this.UpdateGoal(goal);
            }

            if (p.IsLeaderDead)
            {
                goal = BotGoal.DeadOrRevive("Leader dead (HP: 0) — ready to revive", p.LeaderEntity, p.LeaderGrid);
                return this.UpdateGoal(goal);
            }

            // 2.5 Danger Evade (High Hazard Threat + Low HP)
            if (p.Vision.Hazard.IsHighDangerArea && p.FollowerHpPercent < 50f)
            {
                goal = BotGoal.DangerEvade(
                    $"High threat hazard ({p.Vision.Hazard.ThreatInProximity:F0}, HP: {p.FollowerHpPercent:F0}%) — falling back to leader",
                    p.FormationTarget);
                return this.UpdateGoal(goal);
            }

            // 3. Emergency Distance Catchup (Sprint 50+ g)
            // Once engaged in HardCatchup, maintain sprint until reaching safe distance near leader.
            float sprintThreshold = Math.Max(50f, s.CoopSprintDistance);
            float safeStopDist = Math.Max(2f, s.CoopStopDistance);

            bool wasInHardCatchup = this.CurrentGoal.Type == BotGoalType.HardCatchup || isCurrentlySprinting;
            bool shouldContinueSprint = wasInHardCatchup && p.DistanceToLeader > safeStopDist;
            bool shouldStartSprint = p.DistanceToLeader >= sprintThreshold;

            if (shouldContinueSprint || shouldStartSprint)
            {
                goal = BotGoal.HardCatchup(
                    $"Leader is far ahead ({p.DistanceToLeader:F0}/{sprintThreshold:F0}g) — sprinting to catch up",
                    p.FormationTarget);
                return this.UpdateGoal(goal);
            }

            // 4. Combat / Culler Engagement
            // Active when within combat range (< 50g) and follower has ready combat or culler skills
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
                    string combatDetail = hasEligibleCuller
                        ? $"Culling ahead of leader ({p.DistanceToLeader:F0}g, {p.NearbyEnemyCount} enemies)"
                        : $"Targeting hostiles ({p.NearbyEnemyCount} enemies, closest: {p.ClosestEnemyDistance:F0}g)";

                    goal = BotGoal.Combat(combatDetail, p.BestCombatTarget, p.FormationTarget);
                    return this.UpdateGoal(goal);
                }
            }

            // 5. Formation Following (moving towards formation target outside safe distance)
            float followTriggerDist = Math.Max(safeStopDist + 1f, s.CoopFollowDistance);
            if (p.DistanceToLeader > followTriggerDist || p.DistanceToFormationTarget > safeStopDist)
            {
                goal = BotGoal.FormationFollow(
                    $"Moving to {s.FollowerPosition} ({p.DistanceToLeader:F0}g from leader)",
                    p.FormationTarget);
                return this.UpdateGoal(goal);
            }

            // 6. Holding Formation in Safe Zone
            goal = BotGoal.Idle($"In formation safe zone ({p.DistanceToLeader:F0}g <= {safeStopDist:F0}g)");
            return this.UpdateGoal(goal);
        }

        /// <summary>
        /// Evaluates perception and settings for Solo / Exploration / WaveFarm modes.
        /// </summary>
        public BotGoal EvaluateSoloGoal(WorldPerception p, AutoExile2Settings s, bool mapClearComplete = false)
        {
            BotGoal goal;

            if (p.IsPeacefulZone)
            {
                goal = BotGoal.Idle("In Town/Hideout — standing by");
                return this.UpdateGoal(goal);
            }

            if (p.IsFollowerDead)
            {
                goal = BotGoal.DeadOrRevive("Player dead (HP: 0) — halting actions", p.FollowerEntity, p.FollowerGrid);
                return this.UpdateGoal(goal);
            }

            // Hazard check: Evade if high danger and wounded
            if (p.Vision.Hazard.IsHighDangerArea && p.FollowerHpPercent < 45f)
            {
                var safePos = p.Vision.Hazard.NearestMonsterCluster != null
                    ? p.FollowerGrid + Vector2.Normalize(p.FollowerGrid - p.Vision.Hazard.NearestMonsterCluster.Value) * 15f
                    : p.FollowerGrid;

                goal = BotGoal.DangerEvade(
                    $"Critical threat level ({p.Vision.Hazard.ThreatInProximity:F0}) — disengaging",
                    safePos);
                return this.UpdateGoal(goal);
            }

            // Map Complete -> Exit Map
            if (mapClearComplete || (p.Vision.Exploration.IsMapFullyExplored && p.NearbyEnemyCount == 0))
            {
                goal = BotGoal.ExitMap("Map clear complete — opening exit portal", p.Vision.Entities.NearestPortal);
                return this.UpdateGoal(goal);
            }

            // Hostiles present -> Combat
            if (p.NearbyEnemyCount > 0 && p.BestCombatTarget != null)
            {
                goal = BotGoal.Combat(
                    $"Engaging hostiles ({p.NearbyEnemyCount} nearby, closest: {p.ClosestEnemyDistance:F0}g)",
                    p.BestCombatTarget,
                    p.BestCombatTarget.TryGetComponent<GameHelper.RemoteObjects.Components.Render>(out var r) ? new Vector2(r.GridPosition.X, r.GridPosition.Y) : null);
                return this.UpdateGoal(goal);
            }

            // Exploration Frontier
            if (p.Vision.Exploration.NextUnexploredTarget != null)
            {
                goal = BotGoal.Explore(
                    $"Exploring fog of war (Coverage: {p.Vision.Exploration.MapCoverage:F1}%)",
                    p.Vision.Exploration.NextUnexploredTarget.Value);
                return this.UpdateGoal(goal);
            }

            goal = BotGoal.Idle("No active exploration or combat target");
            return this.UpdateGoal(goal);
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
