// <copyright file="BotGoal.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Brain
{
    using System.Numerics;
    using TEHhub.RemoteObjects;
    using TEHhub.RemoteObjects.States.InGameStateObjects;

    /// <summary>
    /// Enumerates high-level goals evaluated and selected by the Central Decision Brain.
    /// </summary>
    public enum BotGoalType
    {
        /// <summary>Standing by in peaceful areas or waiting.</summary>
        Idle,

        /// <summary>Highest priority: Player is dead (respawn) or partner is dead (revive).</summary>
        DeadOrRevive,

        /// <summary>Standing in extreme hazard or critical damage zone -> Reposition to safe ground immediately.</summary>
        DangerEvade,

        /// <summary>Leader is far ahead (> 50g) -> Prioritize sprint catch-up over combat.</summary>
        HardCatchup,

        /// <summary>Hostiles present within engagement range -> Continuous Culler attack &amp; combat positioning.</summary>
        Combat,

        /// <summary>Valuable loot detected on ground and surrounding area is safe to loot.</summary>
        Loot,

        /// <summary>Maintaining safe escort / formation position relative to leader.</summary>
        FormationFollow,

        /// <summary>No immediate hostiles -> Navigate towards unexplored fog-of-war frontiers.</summary>
        Explore,

        /// <summary>Bot detected as physically stuck/blocked -> Perform evasive roll or unstuck pulse.</summary>
        Unstuck,

        /// <summary>Map objective completed -> Open portal and return to hideout.</summary>
        ExitMap,
    }

    /// <summary>
    /// Represents an active goal selected by the Central Decision Brain, along with its context.
    /// </summary>
    public class BotGoal
    {
        public BotGoalType Type { get; set; } = BotGoalType.Idle;

        public int Priority { get; set; } = 0;

        public string Reason { get; set; } = string.Empty;

        public Vector2? TargetPosition { get; set; }

        public Entity? TargetEntity { get; set; }

        public static BotGoal Idle(string reason = "Idle") =>
            new() { Type = BotGoalType.Idle, Priority = 0, Reason = reason };

        public static BotGoal DeadOrRevive(string reason, Entity? target = null, Vector2? pos = null) =>
            new() { Type = BotGoalType.DeadOrRevive, Priority = 100, Reason = reason, TargetEntity = target, TargetPosition = pos };

        public static BotGoal DangerEvade(string reason, Vector2 safePos) =>
            new() { Type = BotGoalType.DangerEvade, Priority = 90, Reason = reason, TargetPosition = safePos };

        public static BotGoal HardCatchup(string reason, Vector2 targetPos) =>
            new() { Type = BotGoalType.HardCatchup, Priority = 80, Reason = reason, TargetPosition = targetPos };

        public static BotGoal Combat(string reason, Entity? target = null, Vector2? pos = null) =>
            new() { Type = BotGoalType.Combat, Priority = 60, Reason = reason, TargetEntity = target, TargetPosition = pos };

        public static BotGoal Loot(string reason, Entity? item = null, Vector2? pos = null) =>
            new() { Type = BotGoalType.Loot, Priority = 50, Reason = reason, TargetEntity = item, TargetPosition = pos };

        public static BotGoal FormationFollow(string reason, Vector2 targetPos) =>
            new() { Type = BotGoalType.FormationFollow, Priority = 40, Reason = reason, TargetPosition = targetPos };

        public static BotGoal Explore(string reason, Vector2 frontierPos) =>
            new() { Type = BotGoalType.Explore, Priority = 35, Reason = reason, TargetPosition = frontierPos };

        public static BotGoal Unstuck(string reason, Vector2 escapePos) =>
            new() { Type = BotGoalType.Unstuck, Priority = 85, Reason = reason, TargetPosition = escapePos };

        public static BotGoal ExitMap(string reason, Entity? portal = null) =>
            new() { Type = BotGoalType.ExitMap, Priority = 30, Reason = reason, TargetEntity = portal };

        public override string ToString() => $"[{this.Type}] {this.Reason} (Prio: {this.Priority})";
    }
}
