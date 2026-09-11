// <copyright file="CoopMovementWorker.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Brain.Workers
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;
    using AutoExile2.Brain;
    using AutoExile2.Modes;
    using AutoExile2.Systems;

    /// <summary>
    /// Specialized Worker Subsystem for Player 2 (Follower) locomotion, steering,
    /// A* pathfinding, look-ahead path smoothing, Dodge Roll gap-closing, and sprinting.
    /// </summary>
    public class CoopMovementWorker
    {
        public const int ANIM_ROLL = 0x10C;   // 268 decimal: Dodge Roll (Tap B)
        public const int ANIM_SPRINT = 0x368; // 872 decimal: Sprint (Hold B)

        private const double SprintActivationGraceMs = 1500;
        private const double SprintAnimationLossGraceMs = 300;
        private const double SprintRetryReleaseMs = 80;

        public List<Vector2> CurrentNavPath { get; } = new();

        public int CurrentWaypointIndex { get; private set; }

        public Vector2? CurrentDestination { get; private set; }

        public bool IsSprinting { get; private set; }

        private DateTime lastRepathTime = DateTime.MinValue;
        private Vector2 lastRepathLeaderPos = Vector2.Zero;
        private DateTime lastFollowerRollTime = DateTime.MinValue;
        private DateTime lastPickupTapTime = DateTime.MinValue;
        private DateTime sprintStartUtc = DateTime.MinValue;
        private DateTime sprintAnimationLostUtc = DateTime.MinValue;
        private DateTime sprintReleaseUntilUtc = DateTime.MinValue;
        private bool wasSprintRequested = false;
        private bool sprintAnimationConfirmed = false;

        public void Reset()
        {
            this.CurrentNavPath.Clear();
            this.CurrentWaypointIndex = 0;
            this.CurrentDestination = null;
            this.IsSprinting = false;
            this.lastRepathTime = DateTime.MinValue;
            this.lastRepathLeaderPos = Vector2.Zero;
            this.lastFollowerRollTime = DateTime.MinValue;
            this.lastPickupTapTime = DateTime.MinValue;
            this.ClearSprintState();
        }

        /// <summary>
        /// Executes movement commands based on the active BotGoal and BrainDirective from the Central Brain.
        /// </summary>
        public void Execute(
            BotGoal goal,
            WorldPerception p,
            CoopVirtualGamepad pad,
            AutoExile2Settings s,
            BotContext ctx,
            bool didCombat,
            BrainDirective? directive = null)
        {
            // 0. Manual Player Keyboard Override (Arrow Keys)
            if (pad.IsFollowerManualMoving)
            {
                this.CurrentNavPath.Clear();
                this.CurrentWaypointIndex = 0;
                this.CurrentDestination = null;
                this.StopSprint(pad);
                return;
            }

            // 1. Idle or Dead -> Halt movement
            if (goal.Type == BotGoalType.Idle || goal.Type == BotGoalType.DeadOrRevive || p.FollowerEntity == null)
            {
                this.CurrentNavPath.Clear();
                this.CurrentWaypointIndex = 0;
                this.CurrentDestination = null;
                pad.SetFollowerMovement(Vector2.Zero);
                this.StopSprint(pad);
                return;
            }

            var now = DateTime.Now;

            // 1.2 Stop if directive indicates no movement desired (unless unstuck)
            if (directive != null && !directive.Locomotion.ShouldMove && goal.Type != BotGoalType.Unstuck)
            {
                this.CurrentNavPath.Clear();
                this.CurrentWaypointIndex = 0;
                this.CurrentDestination = null;
                pad.SetFollowerMovement(Vector2.Zero);
                this.StopSprint(pad);
                return;
            }

            // 1.3 Fly-by Ground Item Pickup (Button A - disabled in peaceful zone)
            bool canPickup = !p.IsPeacefulZone && ((directive != null && directive.Loot.CanPickupNow) || (goal.Type == BotGoalType.Loot && p.CanPickupNow));
            if (canPickup && (now - this.lastPickupTapTime).TotalMilliseconds >= 250)
            {
                pad.PressFollowerButton(CoopPadButton.A, 40);
                this.lastPickupTapTime = now;
            }

            Vector2 targetPos = (directive != null && directive.Locomotion.Destination != Vector2.Zero)
                ? directive.Locomotion.Destination
                : (goal.TargetPosition ?? p.FormationTarget);

            // 1.5 Autonomous Unstuck Maneuver (Evasive Dodge Roll out of corner/trap)
            if (goal.Type == BotGoalType.Unstuck)
            {
                this.CurrentNavPath.Clear();
                this.CurrentWaypointIndex = 0;
                this.CurrentDestination = targetPos;
                var unstuckDir = BotInput.GridToScreenDirection(ctx.World, p.FollowerEntity, targetPos, p.FollowerGrid, p.GridToWorld);
                pad.SetFollowerMovement(unstuckDir);
                this.StopSprint(pad);
                if (!p.IsPeacefulZone && p.FollowerAnimId != ANIM_ROLL)
                {
                    pad.TapFollowerDodgeRoll();
                    this.lastFollowerRollTime = now;
                }
                return;
            }

            // Direct Line-of-Sight check up to 80g (Open terrain & wide chambers)
            Vector2 steerGridPos;
            if (p.HasLosToFormation && p.DistanceToFormationTarget <= 80f)
            {
                this.CurrentNavPath.Clear();
                this.CurrentWaypointIndex = 0;
                this.CurrentDestination = targetPos;
                steerGridPos = targetPos;
            }
            else
            {
                // Obstacle Navigation via A* Pathfinding across entire map
                bool needRepath = false;
                double timeSinceRepath = (now - this.lastRepathTime).TotalMilliseconds;

                if (this.CurrentNavPath.Count == 0 || this.CurrentWaypointIndex >= this.CurrentNavPath.Count)
                {
                    needRepath = timeSinceRepath > 200;
                }
                else if (Vector2.Distance(this.lastRepathLeaderPos, targetPos) > 12f)
                {
                    needRepath = timeSinceRepath > 300;
                }

                if (needRepath && p.WalkableData != null && p.BytesPerRow > 0)
                {
                    var path = Pathfinding.FindPath(p.WalkableData, p.BytesPerRow, p.FollowerGrid, targetPos);
                    this.lastRepathTime = now;
                    this.lastRepathLeaderPos = targetPos;
                    this.CurrentNavPath.Clear();
                    this.CurrentWaypointIndex = 0;

                    if (path != null && path.Count > 0)
                    {
                        this.CurrentNavPath.AddRange(path);
                        this.CurrentDestination = targetPos;
                    }
                }

                // Follow A* path with Look-Ahead Raycast (up to 10 waypoints for seamless corner cutting)
                if (this.CurrentNavPath.Count > 0 && this.CurrentWaypointIndex < this.CurrentNavPath.Count)
                {
                    int maxLookAhead = Math.Min(this.CurrentNavPath.Count - 1, this.CurrentWaypointIndex + 10);
                    for (int i = maxLookAhead; i > this.CurrentWaypointIndex; i--)
                    {
                        if (Pathfinding.HasLineOfSight(p.WalkableData, p.BytesPerRow, p.FollowerGrid, this.CurrentNavPath[i], p.Rows, p.Cols, 3))
                        {
                            this.CurrentWaypointIndex = i;
                            break;
                        }
                    }

                    var wp = this.CurrentNavPath[this.CurrentWaypointIndex];
                    float distToWp = Vector2.Distance(p.FollowerGrid, wp);

                    if (distToWp <= 10f)
                    {
                        this.CurrentWaypointIndex++;
                        if (this.CurrentWaypointIndex < this.CurrentNavPath.Count)
                        {
                            wp = this.CurrentNavPath[this.CurrentWaypointIndex];
                        }
                        else
                        {
                            wp = targetPos;
                        }
                    }

                    steerGridPos = wp;
                }
                else
                {
                    steerGridPos = targetPos;
                }
            }

            // 3. Sprint State Control:
            // Sprint is strictly reserved for HardCatchup (50+ units away)
            bool shouldSprint = (directive != null)
                ? (directive.Locomotion.Sprint && goal.Type == BotGoalType.HardCatchup)
                : (goal.Type == BotGoalType.HardCatchup);
            this.IsSprinting = shouldSprint;

            // PoE 2 can take about one second to enter its sprint animation after B is held.
            // Do not let recovery logic interrupt that normal activation delay. Once sprint has
            // been observed, only re-arm B after the animation has been missing continuously.
            bool effectiveSprint = this.UpdateSprintHold(shouldSprint, p.FollowerAnimId, now);

            // 4. Send steering vector to gamepad
            var moveDir = BotInput.GridToScreenDirection(ctx.World, p.FollowerEntity, steerGridPos, p.FollowerGrid, p.GridToWorld);

            // Analog speed modulation: slow down smoothly when approaching destination (ready-to-cast walk)
            float distToTarget = Vector2.Distance(p.FollowerGrid, targetPos);
            if (!shouldSprint)
            {
                if (distToTarget <= 2.0f)
                {
                    moveDir = Vector2.Zero;
                }
                else if (didCombat || (directive != null && directive.Combat.ShouldAttack))
                {
                    // While attacking or ready to cast, maintain deliberate cast-walking speed
                    float speedFactor = Math.Clamp(distToTarget / 6f, 0.35f, 0.70f);
                    moveDir *= speedFactor;
                }
                else if (distToTarget < 10f)
                {
                    float speedFactor = Math.Clamp(distToTarget / 8f, 0.35f, 1.0f);
                    moveDir *= speedFactor;
                }
            }

            pad.SetFollowerMovement(moveDir);
            pad.SetFollowerSprint(effectiveSprint);

            // 5. Attack, Roll & Evasion Synergy (Multitasking Defense):
            // - Micro-Dodge Slam: Dodge roll perpendicular/away from monster slam telegraphs
            // - Phasing Roll: If hostiles are blocking the movement corridor ahead, Dodge Roll phases straight through them!
            // - Danger Evade Roll: Roll out of lethal ground damage (~600ms)
            // - Formation Gap-Closing Roll: Periodic roll into combat without sprint (~1200ms)
            float safeDist = Math.Max(2f, s.CoopStopDistance);
            float sprintThreshold = Math.Max(50f, s.CoopSprintDistance);
            bool isActuallySprintingAnim = p.FollowerAnimId == ANIM_SPRINT;

            if (!p.IsPeacefulZone && !isActuallySprintingAnim && moveDir.LengthSquared() > 0.05f)
            {
                double msSinceRoll = (now - this.lastFollowerRollTime).TotalMilliseconds;
                bool isMicroDodge = ((directive != null && directive.Locomotion.Maneuver == EvadeManeuver.MicroDodge) || p.HasIncomingSlam) && msSinceRoll >= 450;
                bool isCorridorBlockedRoll = ((directive != null && directive.Locomotion.Maneuver == EvadeManeuver.CorridorPhasingRoll) || p.HasBlockingMonstersInPath) && msSinceRoll >= 500;
                bool isDangerEvadeRoll = goal.Type == BotGoalType.DangerEvade && msSinceRoll >= 600;

                if ((isMicroDodge || isCorridorBlockedRoll || isDangerEvadeRoll) && p.FollowerAnimId != ANIM_ROLL)
                {
                    if (isMicroDodge && directive != null && directive.Locomotion.EvadeDirection != Vector2.Zero)
                    {
                        var evadeTarget = p.FollowerGrid + (directive.Locomotion.EvadeDirection * 15f);
                        var evadeScreenDir = BotInput.GridToScreenDirection(ctx.World, p.FollowerEntity, evadeTarget, p.FollowerGrid, p.GridToWorld);
                        if (evadeScreenDir.LengthSquared() > 0.01f)
                        {
                            pad.SetFollowerMovement(evadeScreenDir);
                        }
                    }

                    pad.TapFollowerDodgeRoll();
                    this.lastFollowerRollTime = now;
                }
            }
        }

        private bool UpdateSprintHold(bool shouldSprint, int followerAnimId, DateTime now)
        {
            if (!shouldSprint)
            {
                this.ClearSprintState();
                return false;
            }

            if (!this.wasSprintRequested)
            {
                this.BeginSprintAttempt(now);
            }

            // Keep B released long enough for the virtual controller and game to observe it,
            // then begin a completely fresh hold with a new activation grace period.
            if (this.sprintReleaseUntilUtc != DateTime.MinValue)
            {
                if (now < this.sprintReleaseUntilUtc)
                {
                    return false;
                }

                this.BeginSprintAttempt(now);
            }

            if (followerAnimId == ANIM_SPRINT)
            {
                this.sprintAnimationConfirmed = true;
                this.sprintAnimationLostUtc = DateTime.MinValue;
                return true;
            }

            if (this.sprintAnimationConfirmed)
            {
                if (this.sprintAnimationLostUtc == DateTime.MinValue)
                {
                    this.sprintAnimationLostUtc = now;
                }

                if ((now - this.sprintAnimationLostUtc).TotalMilliseconds < SprintAnimationLossGraceMs)
                {
                    return true;
                }

                this.BeginSprintRelease(now);
                return false;
            }

            if ((now - this.sprintStartUtc).TotalMilliseconds < SprintActivationGraceMs)
            {
                return true;
            }

            this.BeginSprintRelease(now);
            return false;
        }

        private void BeginSprintAttempt(DateTime now)
        {
            this.wasSprintRequested = true;
            this.sprintStartUtc = now;
            this.sprintAnimationLostUtc = DateTime.MinValue;
            this.sprintReleaseUntilUtc = DateTime.MinValue;
            this.sprintAnimationConfirmed = false;
        }

        private void BeginSprintRelease(DateTime now)
        {
            this.sprintAnimationConfirmed = false;
            this.sprintAnimationLostUtc = DateTime.MinValue;
            this.sprintReleaseUntilUtc = now.AddMilliseconds(SprintRetryReleaseMs);
        }

        private void StopSprint(CoopVirtualGamepad pad)
        {
            this.IsSprinting = false;
            this.ClearSprintState();
            pad.SetFollowerSprint(false);
        }

        private void ClearSprintState()
        {
            this.sprintStartUtc = DateTime.MinValue;
            this.sprintAnimationLostUtc = DateTime.MinValue;
            this.sprintReleaseUntilUtc = DateTime.MinValue;
            this.wasSprintRequested = false;
            this.sprintAnimationConfirmed = false;
        }
    }
}
