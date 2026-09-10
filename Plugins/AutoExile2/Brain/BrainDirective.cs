// <copyright file="BrainDirective.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Brain
{
    using System;
    using System.Numerics;
    using GameHelper.RemoteObjects.States.InGameStateObjects;

    /// <summary>
    /// Types of dynamic evasion maneuvers the Locomotion channel can execute.
    /// </summary>
    public enum EvadeManeuver
    {
        None,

        /// <summary>Emergency roll perpendicular to incoming telegraphed slam or high hazard.</summary>
        MicroDodge,

        /// <summary>Phasing roll directly through monster hitboxes blocking the movement path.</summary>
        CorridorPhasingRoll,

        /// <summary>Tangential roll to escape physical terrain corner trap.</summary>
        UnstuckRoll,
    }

    /// <summary>
    /// Locomotion Channel Directive: Controls legs, destination, and evasive maneuvers.
    /// </summary>
    public class LocomotionDirective
    {
        /// <summary>Where the legs should walk/run to (Grid coordinates).</summary>
        public Vector2 Destination { get; set; } = Vector2.Zero;

        /// <summary>True if character should actively move this tick.</summary>
        public bool ShouldMove { get; set; } = true;

        /// <summary>True if character must enter all-out sprint (sheathes weapons, used only for hard catch-up).</summary>
        public bool Sprint { get; set; } = false;

        /// <summary>Evasive maneuver to perform (e.g. Micro-Dodge out of a slam).</summary>
        public EvadeManeuver Maneuver { get; set; } = EvadeManeuver.None;

        /// <summary>Direction vector for evasion roll (normalized screen/grid direction).</summary>
        public Vector2 EvadeDirection { get; set; } = Vector2.Zero;

        /// <summary>Reason / description of current locomotion intent.</summary>
        public string Reason { get; set; } = string.Empty;
    }

    /// <summary>
    /// Combat Channel Directive: Controls hands, aim vector, and skill casting.
    /// Can operate concurrently with Locomotion and Loot channels.
    /// </summary>
    public class CombatDirective
    {
        /// <summary>True if offensive attacks / skills should fire while moving or fighting.</summary>
        public bool ShouldAttack { get; set; } = false;

        /// <summary>Target entity for attacks.</summary>
        public Entity? TargetEntity { get; set; }

        /// <summary>Aim position (Grid coordinates) where skills / Right Stick should be aimed.</summary>
        public Vector2? AimPosition { get; set; }

        /// <summary>True if offensive casting is permitted (false only if currently sprinting).</summary>
        public bool AllowOffensiveCast { get; set; } = true;

        /// <summary>True if targeting a priority Culler enemy (Rare/Boss or low-HP frontline).</summary>
        public bool IsCullerPriority { get; set; } = false;

        /// <summary>Reason / description of current combat intent.</summary>
        public string Reason { get; set; } = string.Empty;
    }

    /// <summary>
    /// Loot Channel Directive: Controls ground item scanning, prioritization, and fly-by pickup.
    /// </summary>
    public class LootDirective
    {
        /// <summary>The target ground item entity.</summary>
        public Entity? TargetItem { get; set; }

        /// <summary>Grid position of the target item.</summary>
        public Vector2? ItemPosition { get; set; }

        /// <summary>Distance to the item in grid units.</summary>
        public float DistanceToItem { get; set; } = float.MaxValue;

        /// <summary>True if the character is close enough (e.g. &lt;= 7g) to press pickup right now.</summary>
        public bool CanPickupNow { get; set; } = false;

        /// <summary>Display name or type of the item being targeted.</summary>
        public string ItemName { get; set; } = string.Empty;

        /// <summary>Reason / description of current loot intent.</summary>
        public string Reason { get; set; } = string.Empty;
    }

    /// <summary>
    /// Reflex Channel Directive: Controls emergency self-preservation (flasks, guard buffs).
    /// </summary>
    public class ReflexDirective
    {
        public bool TriggerLifeFlask { get; set; } = false;

        public bool TriggerManaFlask { get; set; } = false;

        public bool TriggerGuardBuff { get; set; } = false;

        public string Reason { get; set; } = string.Empty;
    }

    /// <summary>
    /// Multi-Channel Concurrent Brain Directive:
    /// Issued by CentralBrain on each tick to coordinate all body systems simultaneously
    /// (Legs, Hands, Eyes/Pickup, Reflexes) mirroring human player multitasking.
    /// </summary>
    public class BrainDirective
    {
        public LocomotionDirective Locomotion { get; } = new();

        public CombatDirective Combat { get; } = new();

        public LootDirective Loot { get; } = new();

        public ReflexDirective Reflex { get; } = new();

        /// <summary>High-level summary of the concurrent action plan for telemetry/UI.</summary>
        public string Summary { get; set; } = "Standby";

        public void Reset()
        {
            this.Locomotion.Destination = Vector2.Zero;
            this.Locomotion.ShouldMove = false;
            this.Locomotion.Sprint = false;
            this.Locomotion.Maneuver = EvadeManeuver.None;
            this.Locomotion.EvadeDirection = Vector2.Zero;
            this.Locomotion.Reason = string.Empty;

            this.Combat.ShouldAttack = false;
            this.Combat.TargetEntity = null;
            this.Combat.AimPosition = null;
            this.Combat.AllowOffensiveCast = true;
            this.Combat.IsCullerPriority = false;
            this.Combat.Reason = string.Empty;

            this.Loot.TargetItem = null;
            this.Loot.ItemPosition = null;
            this.Loot.DistanceToItem = float.MaxValue;
            this.Loot.CanPickupNow = false;
            this.Loot.ItemName = string.Empty;
            this.Loot.Reason = string.Empty;

            this.Reflex.TriggerLifeFlask = false;
            this.Reflex.TriggerManaFlask = false;
            this.Reflex.TriggerGuardBuff = false;
            this.Reflex.Reason = string.Empty;

            this.Summary = "Standby";
        }
    }
}
