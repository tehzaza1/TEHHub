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
    using TEHhub.RemoteObjects.Components;

    /// <summary>
    /// Cognitive Central Decision Brain (2.0):
    /// Advanced Utility-Based AI engine that arbitrates goals using dynamic utility scoring,
    /// action commitment buffers (hysteresis to eliminate oscillation), and autonomous stuck detection.
    /// </summary>
    public class CentralBrain
    {
        public BotGoal CurrentGoal { get; private set; } = BotGoal.Idle();

        public BotGoalType LastGoalType { get; private set; } = BotGoalType.Idle;

        public BrainDirective CurrentDirective { get; private set; } = new();

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
            this.CurrentDirective.Reset();
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

            // 1. Life & Death (Highest absolute priority: 100)
            if (p.IsFollowerDead)
            {
                var deadGoal = BotGoal.DeadOrRevive("Follower dead (HP: 0) — halting actions", p.FollowerEntity, p.FollowerGrid);
                this.SynthesizeCoopDirective(p, s, deadGoal, isCurrentlySprinting);
                return this.UpdateGoal(deadGoal);
            }

            if (p.IsLeaderDead)
            {
                var reviveGoal = BotGoal.DeadOrRevive("Leader dead (HP: 0) — ready to revive", p.LeaderEntity, p.LeaderGrid);
                this.SynthesizeCoopDirective(p, s, reviveGoal, isCurrentlySprinting);
                return this.UpdateGoal(reviveGoal);
            }

            // 2. Stuck Detection Check
            this.UpdateStuckTracking(p.FollowerGrid, now);
            if (now < this.unstuckUntil)
            {
                var unstuckGoal = BotGoal.Unstuck("Obstacle collision detected — executing escape roll", this.currentUnstuckTarget);
                this.SynthesizeCoopDirective(p, s, unstuckGoal, isCurrentlySprinting);
                return this.UpdateGoal(unstuckGoal);
            }

            // 3. Compute Utility Scores for Candidate Goals
            var candidates = new List<(BotGoal Goal, float Score, string Reason)>();

            // 3.1 DangerEvade Utility (Hazard Threat + Low HP)
            float dangerScore = 0f;
            string dangerReason = string.Empty;
            if (!p.IsPeacefulZone && p.Vision.Hazard.IsHighDangerArea)
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
            float sprintReleaseThreshold = Math.Min(sprintThreshold - 5f, Math.Max(safeStopDist + 10f, sprintThreshold - 15f));
            float catchupScore = 0f;
            string catchupReason = string.Empty;

            bool wasInHardCatchup = this.CurrentGoal.Type == BotGoalType.HardCatchup;

            if (wasInHardCatchup && p.DistanceToLeader > sprintReleaseThreshold)
            {
                // Hysteresis latch: once sprinting, hold high utility until safely inside catch-up range
                catchupScore = 84f;
                catchupReason = p.IsPeacefulZone
                    ? $"Town/Hideout: Sprinting catch-up in progress ({p.DistanceToLeader:F0}/{sprintReleaseThreshold:F0}g)"
                    : $"Sprinting catch-up in progress ({p.DistanceToLeader:F0}/{sprintReleaseThreshold:F0}g)";
            }
            else if (p.DistanceToLeader >= sprintThreshold)
            {
                // Dynamic distance scaling: 80 base + additional up to 96
                catchupScore = 80f + Math.Min(16f, (p.DistanceToLeader - sprintThreshold) * 0.4f);
                catchupReason = p.IsPeacefulZone
                    ? $"Town/Hideout: Leader far ahead ({p.DistanceToLeader:F0}/{sprintThreshold:F0}g)"
                    : $"Leader far ahead ({p.DistanceToLeader:F0}/{sprintThreshold:F0}g)";
            }

            if (catchupScore > 0f)
            {
                candidates.Add((BotGoal.HardCatchup(catchupReason, p.FormationTarget), catchupScore, catchupReason));
            }

            // 3.3 Combat Utility (Culling + Hostiles engagement)
            float combatScore = 0f;
            string combatReason = string.Empty;
            if (!p.IsPeacefulZone && s.P2EnableCombat && s.P2Skills != null)
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
            // Follower walks with Leader in ready-to-cast stance to maintain formation
            float followScore = 0f;
            string followReason = string.Empty;

            float followTriggerDist = Math.Max(safeStopDist + 1f, s.CoopFollowDistance);
            bool wasFollowing = this.CurrentGoal.Type == BotGoalType.FormationFollow;

            bool shouldFollow = wasFollowing
                ? (p.DistanceToLeader > safeStopDist)
                : (p.DistanceToLeader > followTriggerDist);

            if (shouldFollow)
            {
                followScore = 40f + Math.Min(15f, p.DistanceToFormationTarget * 0.5f);
                followReason = p.IsPeacefulZone
                    ? $"Town/Hideout: Walking with leader ({p.DistanceToLeader:F0}/{followTriggerDist:F0}g)"
                    : $"Walking in formation ({p.DistanceToLeader:F0}/{followTriggerDist:F0}g from leader)";
                candidates.Add((BotGoal.FormationFollow(followReason, p.FormationTarget), followScore, followReason));
            }

            // 3.5 Idle Utility (Only when physically at the exact formation spot)
            float idleScore = 15f;
            string idleReason = p.IsPeacefulZone
                ? $"Town/Hideout: Standing by leader ({p.DistanceToLeader:F0}g)"
                : $"Holding formation spot ({p.DistanceToLeader:F0}g)";
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
            var winnerGoal = winner.Goal;

            // Multitasking Enhancement (Human-like behavior):
            // If in combat and an incoming slam is detected, execute Micro-Dodge while keeping combat lock!
            if (p.HasIncomingSlam && winnerGoal.Type == BotGoalType.Combat)
            {
                winnerGoal = BotGoal.Combat($"Micro-dodging {p.Vision.Telegraph.SlamAnimation} while maintaining combat lock", p.BestCombatTarget, p.FormationTarget);
            }

            this.SynthesizeCoopDirective(p, s, winnerGoal, isCurrentlySprinting);
            return this.UpdateGoal(winnerGoal);
        }

        /// <summary>
        /// Synthesizes concurrent multi-channel directives (Legs, Hands, Reflexes)
        /// mirroring a human player multitasking in Co-op mode:
        /// - Walking in formation while continuously culling/shooting enemies ahead
        /// - Micro-dodging boss/monster slams without dropping combat lock
        /// (Note: In Co-op mode, Follower stays strictly in formation and leaves looting to Leader).
        /// </summary>
        private BrainDirective SynthesizeCoopDirective(
            WorldPerception p,
            AutoExile2Settings s,
            BotGoal narrativeGoal,
            bool isCurrentlySprinting)
        {
            var dir = this.CurrentDirective;
            dir.Reset();

            if (p.IsFollowerDead)
            {
                dir.Locomotion.ShouldMove = false;
                dir.Combat.ShouldAttack = false;
                dir.Summary = "Dead — Halting";
                return dir;
            }

            // 1. Reflex Channel (Flasks & Immediate Life Support - disabled in peaceful zone)
            if (!p.IsPeacefulZone)
            {
                if (p.FollowerHpPercent < s.P2LifeFlaskThresholdPercent)
                {
                    dir.Reflex.TriggerLifeFlask = true;
                    dir.Reflex.Reason = $"Low HP ({p.FollowerHpPercent:F0}% < {s.P2LifeFlaskThresholdPercent}%)";
                }

                if (p.FollowerManaPercent < s.P2ManaFlaskThresholdPercent)
                {
                    dir.Reflex.TriggerManaFlask = true;
                    dir.Reflex.Reason = $"Low Mana ({p.FollowerManaPercent:F0}% < {s.P2ManaFlaskThresholdPercent}%)";
                }
            }

            // 2. Combat Channel (Attack & Cast Intent - disabled in peaceful zone)
            bool sprintMode = narrativeGoal.Type == BotGoalType.HardCatchup;
            if (!p.IsPeacefulZone && s.P2EnableCombat && s.P2Skills != null && !sprintMode)
            {
                bool hasTargets = p.NearbyEnemyCount > 0 || p.BestCombatTarget != null;
                if (hasTargets)
                {
                    dir.Combat.ShouldAttack = true;
                    dir.Combat.TargetEntity = p.BestCombatTarget;
                    if (p.BestCombatTarget != null && p.BestCombatTarget.TryGetComponent<Render>(out var bR))
                    {
                        dir.Combat.AimPosition = new Vector2(bR.GridPosition.X, bR.GridPosition.Y);
                    }
                    else
                    {
                        dir.Combat.AimPosition = p.LeaderGrid + (p.LeaderHeading * 25f);
                    }

                    dir.Combat.AllowOffensiveCast = true;
                    dir.Combat.IsCullerPriority = p.Vision.Entities.HasDangerousRareOrBoss;
                    dir.Combat.Reason = $"Targeting {p.NearbyEnemyCount} enemies (Aim: {dir.Combat.AimPosition})";
                }
            }

            // 3. Locomotion Channel (Escort Formation & Tactical Evasion)
            float safeStopDist = Math.Max(2f, s.CoopStopDistance);
            bool isAtExactSpot = p.DistanceToFormationTarget <= 2.5f;

            if (narrativeGoal.Type == BotGoalType.Unstuck)
            {
                dir.Locomotion.ShouldMove = true;
                dir.Locomotion.Destination = this.currentUnstuckTarget;
                dir.Locomotion.Maneuver = p.IsPeacefulZone ? EvadeManeuver.None : EvadeManeuver.UnstuckRoll;
                dir.Locomotion.Reason = "Unstuck collision maneuver";
            }
            else if (sprintMode && p.DistanceToLeader > (safeStopDist + 15f))
            {
                dir.Locomotion.ShouldMove = true;
                dir.Locomotion.Destination = p.FormationTarget;
                dir.Locomotion.Sprint = true;
                dir.Locomotion.Maneuver = EvadeManeuver.None;
                dir.Locomotion.Reason = p.IsPeacefulZone
                    ? $"Town/Hideout: Sprinting catch-up to leader ({p.DistanceToLeader:F0}g)"
                    : $"Sprinting catch-up to leader ({p.DistanceToLeader:F0}g)";
            }
            else
            {
                // 3.1 Evasion Maneuver (Micro-Dodge slam, Hazard reposition, or Corridor Phasing - disabled in peaceful zone)
                if (!p.IsPeacefulZone)
                {
                    if (p.HasIncomingSlam)
                    {
                        dir.Locomotion.Maneuver = EvadeManeuver.MicroDodge;
                        dir.Locomotion.EvadeDirection = p.SlamEvadeVector;
                        dir.Locomotion.Reason = $"Micro-dodging slam {p.Vision.Telegraph.SlamAnimation}";
                    }
                    else if (p.HasBlockingMonstersInPath)
                    {
                        dir.Locomotion.Maneuver = EvadeManeuver.CorridorPhasingRoll;
                        dir.Locomotion.Reason = "Corridor phasing roll through monsters";
                    }
                    else if (p.Vision.Hazard.IsHighDangerArea)
                    {
                        dir.Locomotion.Maneuver = EvadeManeuver.MicroDodge;
                        Vector2 hazardDiff = p.Vision.Hazard.NearestMonsterCluster != null
                            ? p.FollowerGrid - p.Vision.Hazard.NearestMonsterCluster.Value
                            : Vector2.Zero;
                        dir.Locomotion.EvadeDirection = hazardDiff.LengthSquared() > 0.001f
                            ? Vector2.Normalize(hazardDiff)
                            : new Vector2(0, 1);
                        dir.Locomotion.Reason = "Repositioning out of hazard threat";
                    }
                }

                // 3.2 Destination: Follower walks with Leader only when outside safe stop distance / beyond follow distance
                float followTrigger = Math.Max(safeStopDist + 1f, s.CoopFollowDistance);
                bool needsMovement = narrativeGoal.Type switch
                {
                    BotGoalType.HardCatchup => true,
                    BotGoalType.DangerEvade => true,
                    BotGoalType.FormationFollow => p.DistanceToLeader > safeStopDist,
                    BotGoalType.Combat => p.DistanceToLeader > followTrigger,
                    _ => p.DistanceToLeader > followTrigger
                };

                if (needsMovement && !isAtExactSpot)
                {
                    dir.Locomotion.ShouldMove = true;
                    dir.Locomotion.Destination = p.FormationTarget;
                    dir.Locomotion.Sprint = false;
                    dir.Locomotion.Reason = p.IsPeacefulZone
                        ? $"Town/Hideout: Walking with leader ({p.DistanceToLeader:F0}g)"
                        : (dir.Combat.ShouldAttack
                            ? $"Combat cast-walking in formation ({p.DistanceToLeader:F0}g)"
                            : $"Walking in formation ({p.DistanceToLeader:F0}g)");
                }
                else
                {
                    dir.Locomotion.ShouldMove = false;
                    dir.Locomotion.Destination = p.FollowerGrid;
                    dir.Locomotion.Sprint = false;
                    dir.Locomotion.Reason = p.IsPeacefulZone
                        ? $"Town/Hideout: Standing by leader ({p.DistanceToLeader:F0}g <= {followTrigger:F0}g)"
                        : $"Holding formation ({p.DistanceToLeader:F0}g <= {followTrigger:F0}g)";
                }
            }

            string combatPart = dir.Combat.ShouldAttack ? $"Combat: #{dir.Combat.TargetEntity?.Id ?? 0}" : "Combat: Idle";
            string dodgePart = dir.Locomotion.Maneuver != EvadeManeuver.None ? $" | Dodge: {dir.Locomotion.Maneuver}" : "";
            dir.Summary = p.IsPeacefulZone
                ? $"{dir.Locomotion.Reason}"
                : $"{dir.Locomotion.Reason} | {combatPart}{dodgePart}";

            return dir;
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

            // 1. Slam Telegraph Evasion (Micro-dodge while maintaining combat lock)
            if (p.HasIncomingSlam && p.BestCombatTarget != null)
            {
                return this.UpdateGoal(BotGoal.Combat(
                    $"Micro-dodging {p.Vision.Telegraph.SlamAnimation} while maintaining combat lock",
                    p.BestCombatTarget,
                    p.FollowerGrid + (p.SlamEvadeVector * 15f)));
            }

            // 2. Critical Hazard Evade
            if (p.Vision.Hazard.IsHighDangerArea && p.FollowerHpPercent < 45f)
            {
                Vector2 hazardDiff = p.Vision.Hazard.NearestMonsterCluster != null
                    ? p.FollowerGrid - p.Vision.Hazard.NearestMonsterCluster.Value
                    : Vector2.Zero;
                Vector2 hazardDir = hazardDiff.LengthSquared() > 0.001f
                    ? Vector2.Normalize(hazardDiff)
                    : new Vector2(0, 1);
                var safePos = p.FollowerGrid + (hazardDir * 15f);

                return this.UpdateGoal(BotGoal.DangerEvade(
                    $"Critical threat level ({p.Vision.Hazard.ThreatInProximity:F0}) — disengaging",
                    safePos));
            }

            // 3. Attack-Move to Loot (Solo Mode human-like multitasking: walk to loot while firing at enemies)
            if (p.HasLootNearby && p.NearestLootPosition.HasValue && p.NearestLootDistance <= 40f)
            {
                string combatNote = p.NearbyEnemyCount > 0 ? " [Firing on hostiles]" : "";
                return this.UpdateGoal(BotGoal.Loot(
                    $"Attack-moving to loot [{p.NearestLootName}] ({p.NearestLootDistance:F0}g){combatNote}",
                    p.NearestLootEntity,
                    p.NearestLootPosition));
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
