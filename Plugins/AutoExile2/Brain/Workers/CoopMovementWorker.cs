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

            // 2. Determine target destination based on goal
            Vector2 targetPos = goal.TargetPosition ?? p.FormationTarget;
            var now = DateTime.Now;

            // Direct Line-of-Sight check up to 60g (Open terrain)
            Vector2 steerGridPos;
            if (p.HasLosToFormation && p.DistanceToFormationTarget <= 60f)
            {
                this.CurrentNavPath.Clear();
                this.CurrentWaypointIndex = 0;
                this.CurrentDestination = targetPos;
                steerGridPos = targetPos;
            }
            else
            {
                // Obstacle Navigation via A* Pathfinding
                bool needRepath = false;
                double timeSinceRepath = (now - this.lastRepathTime).TotalMilliseconds;

                if (this.CurrentNavPath.Count == 0 || this.CurrentWaypointIndex >= this.CurrentNavPath.Count)
                {
                    needRepath = timeSinceRepath > 250;
                }
                else if (Vector2.Distance(this.lastRepathLeaderPos, targetPos) > 15f)
                {
                    needRepath = timeSinceRepath > 350;
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

                // Follow A* path with Look-Ahead Raycast (Path Smoothing / Corner Cutting)
                if (this.CurrentNavPath.Count > 0 && this.CurrentWaypointIndex < this.CurrentNavPath.Count)
                {
                    int maxLookAhead = Math.Min(this.CurrentNavPath.Count - 1, this.CurrentWaypointIndex + 6);
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

                    if (distToWp <= 8f)
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

            // 5. Attack & Roll Synergy (Dodge Roll Gap-Closing):
            // When moving to close distance without sprinting, tap Dodge Roll (B for 50ms) periodically (~850ms)
            // This propels the bot into attack range while keeping weapons drawn!
            float safeDist = Math.Max(2f, s.CoopStopDistance);
            float sprintThreshold = Math.Max(50f, s.CoopSprintDistance);
            bool isActuallySprintingAnim = p.FollowerAnimId == ANIM_SPRINT;

            if (!shouldSprint && !isActuallySprintingAnim && moveDir.LengthSquared() > 0.05f)
            {
                double msSinceRoll = (now - this.lastFollowerRollTime).TotalMilliseconds;
                if (p.DistanceToLeader > (safeDist + 3f) && p.DistanceToLeader < sprintThreshold && msSinceRoll >= 850)
                {
                    if (p.FollowerAnimId != ANIM_ROLL)
                    {
                        pad.TapFollowerDodgeRoll();
                        this.lastFollowerRollTime = now;
                    }
                }
            }
        }
    }
}
