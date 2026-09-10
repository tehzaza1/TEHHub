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

        public List<Vector2> CurrentNavPath { get; } = new();

        public int CurrentWaypointIndex { get; private set; }

        public Vector2? CurrentDestination { get; private set; }

        public bool IsSprinting { get; private set; }

        private DateTime lastRepathTime = DateTime.MinValue;
        private Vector2 lastRepathLeaderPos = Vector2.Zero;
        private DateTime lastFollowerRollTime = DateTime.MinValue;

        public void Reset()
        {
            this.CurrentNavPath.Clear();
            this.CurrentWaypointIndex = 0;
            this.CurrentDestination = null;
            this.IsSprinting = false;
            this.lastRepathTime = DateTime.MinValue;
            this.lastRepathLeaderPos = Vector2.Zero;
            this.lastFollowerRollTime = DateTime.MinValue;
        }

        /// <summary>
        /// Executes movement commands based on the active BotGoal from the Central Brain.
        /// </summary>
        public void Execute(
            BotGoal goal,
            WorldPerception p,
            CoopVirtualGamepad pad,
            AutoExile2Settings s,
            BotContext ctx,
            bool didCombat)
        {
            // 0. Manual Player Keyboard Override (Arrow Keys)
            if (pad.IsFollowerManualMoving)
            {
                this.CurrentNavPath.Clear();
                this.CurrentWaypointIndex = 0;
                this.CurrentDestination = null;
                this.IsSprinting = false;
                pad.SetFollowerSprint(false);
                return;
            }

            // 1. Idle or Dead -> Halt movement
            if (goal.Type == BotGoalType.Idle || goal.Type == BotGoalType.DeadOrRevive || p.FollowerEntity == null)
            {
                this.CurrentNavPath.Clear();
                this.CurrentWaypointIndex = 0;
                this.CurrentDestination = null;
                this.IsSprinting = false;
                pad.SetFollowerMovement(Vector2.Zero);
                pad.SetFollowerSprint(false);
                return;
            }

            Vector2 targetPos = goal.TargetPosition ?? p.FormationTarget;
            var now = DateTime.Now;

            // 1.5 Autonomous Unstuck Maneuver (Evasive Dodge Roll out of corner/trap)
            if (goal.Type == BotGoalType.Unstuck)
            {
                this.CurrentNavPath.Clear();
                this.CurrentWaypointIndex = 0;
                this.CurrentDestination = targetPos;
                var unstuckDir = BotInput.GridToScreenDirection(ctx.World, p.FollowerEntity, targetPos, p.FollowerGrid, p.GridToWorld);
                pad.SetFollowerMovement(unstuckDir);
                pad.SetFollowerSprint(false);
                if (p.FollowerAnimId != ANIM_ROLL)
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
            bool shouldSprint = goal.Type == BotGoalType.HardCatchup;
            this.IsSprinting = shouldSprint;

            // 4. Send steering vector to gamepad
            var moveDir = BotInput.GridToScreenDirection(ctx.World, p.FollowerEntity, steerGridPos, p.FollowerGrid, p.GridToWorld);
            pad.SetFollowerMovement(moveDir);
            pad.SetFollowerSprint(shouldSprint);

            // 5. Attack, Roll & Corridor Phasing Synergy:
            // - Phasing Roll: If hostiles are blocking the movement corridor ahead, Dodge Roll phases straight through them!
            // - Danger Evade Roll: Roll out of lethal ground damage (~600ms)
            // - Formation Gap-Closing Roll: Periodic roll into combat without sprint (~850ms)
            float safeDist = Math.Max(2f, s.CoopStopDistance);
            float sprintThreshold = Math.Max(50f, s.CoopSprintDistance);
            bool isActuallySprintingAnim = p.FollowerAnimId == ANIM_SPRINT;

            if (!isActuallySprintingAnim && moveDir.LengthSquared() > 0.05f)
            {
                double msSinceRoll = (now - this.lastFollowerRollTime).TotalMilliseconds;
                bool isCorridorBlockedRoll = p.HasBlockingMonstersInPath && msSinceRoll >= 500;
                bool isDangerEvadeRoll = goal.Type == BotGoalType.DangerEvade && msSinceRoll >= 600;
                bool isFormationRoll = !shouldSprint && p.DistanceToLeader > (safeDist + 3f) && p.DistanceToLeader < sprintThreshold && msSinceRoll >= 850;

                if ((isCorridorBlockedRoll || isDangerEvadeRoll || isFormationRoll) && p.FollowerAnimId != ANIM_ROLL)
                {
                    pad.TapFollowerDodgeRoll();
                    this.lastFollowerRollTime = now;
                }
            }
        }
    }
}
