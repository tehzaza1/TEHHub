// <copyright file="SkillSlotConfig.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2
{
    using System;
    using System.Collections.Generic;
    using ClickableTransparentOverlay.Win32;
    using GameHelper.RemoteObjects.Components;
    using Newtonsoft.Json;

    /// <summary>
    /// Functional role for an individual skill slot in combat.
    /// Controls where the cursor aims and how the skill behaves.
    /// </summary>
    public enum SkillRole
    {
        Disabled = 0,

        /// <summary>Aim cursor directly at target monster (direct attack, projectile, single target).</summary>
        EnemyTargeted = 1,

        /// <summary>Aim cursor at pack center / cluster (AoE spells, curses, debuffs).</summary>
        PackTargeted = 2,

        /// <summary>Deploy totem or minion toward enemies with recast interval.</summary>
        TotemOrMinion = 3,

        /// <summary>Self-cast buff, guard (Steelskin, Molten Shell, Enduring Cry, auras) without cursor movement.</summary>
        SelfBuffGuard = 4,

        /// <summary>Aim at nearest corpse (Detonate Dead, Offerings).</summary>
        CorpseTargeted = 5,

        /// <summary>Culler (Focused Fire ahead of Leader / Cull low HP hostiles).</summary>
        Culler = 6,
    }

    /// <summary>
    /// Target monster rarity restriction for skill execution.
    /// </summary>
    public enum SkillTargetFilter
    {
        /// <summary>Cast on any hostile monster.</summary>
        Any = 0,

        /// <summary>Only cast on Normal (White) monsters.</summary>
        NormalOnly = 1,

        /// <summary>Cast on Magic (Blue), Rare (Yellow), and Unique (Orange) monsters.</summary>
        MagicOrAbove = 2,

        /// <summary>Cast on Rare (Yellow) and Unique (Orange) monsters only.</summary>
        RareOrAbove = 3,

        /// <summary>Cast on Unique (Bosses) only.</summary>
        UniqueOnly = 4,
    }

    /// <summary>
    /// Vital type to check for low life/guard skill trigger (HP, ES, or Combined).
    /// </summary>
    public enum VitalConditionType
    {
        /// <summary>Check Life (Health) only.</summary>
        HpOnly = 0,

        /// <summary>Check Energy Shield (ES) only.</summary>
        EsOnly = 1,

        /// <summary>Check Combined Effective Health Pool (HP + ES).</summary>
        CombinedHpEs = 2,
    }

    /// <summary>
    /// Configuration for an individual skill slot.
    /// Gives complete, per-skill control over keybindings, roles, priorities, cooldowns, and conditions.
    /// </summary>
    public class SkillSlotConfig
    {
        /// <summary>Whether this skill slot is active.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Vital pool type to check when OnlyOnLowHp is true (HpOnly, EsOnly, CombinedHpEs).</summary>
        public VitalConditionType VitalCondition { get; set; } = VitalConditionType.CombinedHpEs;

        /// <summary>User-friendly label for this skill slot.</summary>
        public string Name { get; set; } = "Skill Slot";

        /// <summary>Specific in-game skill gem name mapped to this slot (e.g. "Spark", "Steelskin").</summary>
        public string AssignedSkillName { get; set; } = "";

        /// <summary>Skill category (Attack, Buff, Curse, Totem, Guard, Warcry, Minion, Movement, Custom).</summary>
        public string Category { get; set; } = "Attack";

        /// <summary>Combat role for cursor positioning and execution mode.</summary>
        public SkillRole Role { get; set; } = SkillRole.EnemyTargeted;

        /// <summary>Input type: Mouse Right, Mouse Left, Mouse Middle, or Keyboard Key.</summary>
        public AttackInputType InputType { get; set; } = AttackInputType.MouseRight;

        /// <summary>Keyboard virtual key if InputType is KeyboardKey.</summary>
        public VK Key { get; set; } = VK.KEY_Q;

        /// <summary>Gamepad button to press if executed via Virtual Gamepad (Co-op mode).</summary>
        public AutoExile2.Systems.CoopPadButton GamepadButton { get; set; } = AutoExile2.Systems.CoopPadButton.RightShoulder;

        /// <summary>Execution priority (1 to 10; higher priority skills are evaluated first).</summary>
        public int Priority { get; set; } = 5;

        /// <summary>Monster rarity filter for when this skill is allowed to trigger.</summary>
        public SkillTargetFilter TargetFilter { get; set; } = SkillTargetFilter.Any;

        /// <summary>Minimum cooldown / interval in milliseconds between casts of this skill.</summary>
        public int MinCastIntervalMs { get; set; } = 250;

        /// <summary>Input hold duration in milliseconds.</summary>
        public int HoldDurationMs { get; set; } = 150;

        /// <summary>Maximum target range in grid units (0 = use global combat range).</summary>
        public float MaxTargetRange { get; set; } = 0f;

        /// <summary>Minimum alive monsters in combat range to trigger (0 = no minimum).</summary>
        public int MinNearbyEnemies { get; set; } = 0;

        /// <summary>Only cast when player HP is below LowHpThresholdPercent.</summary>
        public bool OnlyOnLowHp { get; set; } = false;

        /// <summary>Player HP percentage (1-100) trigger threshold for guard/panic skills.</summary>
        public float LowHpThresholdPercent { get; set; } = 60f;

        /// <summary>Minimum player mana percentage required to cast (0 = ignore mana).</summary>
        public float MinManaPercent { get; set; } = 0f;

        /// <summary>Channel skill: hold key down continuously while target exists.</summary>
        public bool IsChannel { get; set; } = false;

        /// <summary>Only cast if the player does not currently have this buff (or target does not have debuff).</summary>
        public bool OnlyWhenBuffMissing { get; set; } = false;

        /// <summary>Buff or debuff name substring to check (case-insensitive). If empty, uses AssignedSkillName.</summary>
        public string BuffDebuffName { get; set; } = "";

        /// <summary>Maximum active totems allowed before halting recast (default 1).</summary>
        public int MaxTotemCount { get; set; } = 1;

        /// <summary>Maximum active minions allowed before halting recast (default 3).</summary>
        public int MaxMinionCount { get; set; } = 3;

        /// <summary>Culler: Distance in front of host to aim (Grid units, default 75g).</summary>
        public float CullerAimDistance { get; set; } = 75f;

        /// <summary>Culler: Distance from host to start attacking (Grid units, default 35g).</summary>
        public float CullerStartAttackDistance { get; set; } = 35f;

        /// <summary>Culler: Require alive monsters nearby before attacking (true = require monsters, false = fire continuously near host).</summary>
        public bool CullerRequireMonsters { get; set; } = false;

        /// <summary>Runtime timestamp of last cast (not persisted).</summary>
        [JsonIgnore]
        public DateTime LastCastAt { get; set; } = DateTime.MinValue;

        /// <summary>
        /// Generates standard default skill slots for PoE 2 (empty by default so user configures manually).
        /// </summary>
        public static List<SkillSlotConfig> GetDefaultSlots()
        {
            return new List<SkillSlotConfig>();
        }

        /// <summary>
        /// Default automated buff &amp; guard skills for Player 1 (Leader) in Co-op mode.
        /// Empty by default so user can configure from scratch or click quick presets / detected chips.
        /// </summary>
        public static List<SkillSlotConfig> GetDefaultP1Skills()
        {
            return new List<SkillSlotConfig>();
        }

        /// <summary>
        /// Default autonomous skills for Player 2 (Follower) in Co-op mode.
        /// Empty by default so user can configure from scratch or click quick presets / detected chips.
        /// </summary>
        public static List<SkillSlotConfig> GetDefaultP2Skills()
        {
            return new List<SkillSlotConfig>();
        }
    }

    /// <summary>
    /// Real-time snapshot of player/follower vitals (HP, ES, Mana, Combined Pool).
    /// Properly handles reserved vitals (Unreserved vs Total) and characters without Energy Shield.
    /// </summary>
    public readonly struct PlayerVitals
    {
        public readonly float HpPercent;
        public readonly float EsPercent;
        public readonly float CombinedPercent;
        public readonly float ManaPercent;
        public readonly bool HasEs;
        public readonly int CurrentHp;
        public readonly int MaxHp;
        public readonly int CurrentEs;
        public readonly int MaxEs;
        public readonly int CurrentMana;
        public readonly int MaxMana;

        public PlayerVitals(Life? life)
        {
            if (life == null)
            {
                this.HpPercent = 100f;
                this.EsPercent = 100f;
                this.CombinedPercent = 100f;
                this.ManaPercent = 100f;
                this.HasEs = false;
                this.CurrentHp = 0;
                this.MaxHp = 0;
                this.CurrentEs = 0;
                this.MaxEs = 0;
                this.CurrentMana = 0;
                this.MaxMana = 0;
                return;
            }

            // Unreserved Life (fallback to Total if Unreserved <= 0)
            int unreservedHp = life.Health.Unreserved > 0 ? life.Health.Unreserved : life.Health.Total;
            this.MaxHp = Math.Max(0, unreservedHp);
            this.CurrentHp = Math.Clamp(life.Health.Current, 0, this.MaxHp);
            this.HpPercent = this.MaxHp > 0 ? ((float)this.CurrentHp / this.MaxHp) * 100f : 100f;

            // Unreserved Energy Shield
            int unreservedEs = life.EnergyShield.Unreserved > 0 ? life.EnergyShield.Unreserved : life.EnergyShield.Total;
            this.HasEs = unreservedEs > 0;
            this.MaxEs = this.HasEs ? unreservedEs : 0;
            this.CurrentEs = this.HasEs ? Math.Clamp(life.EnergyShield.Current, 0, this.MaxEs) : 0;
            this.EsPercent = this.HasEs && this.MaxEs > 0 ? ((float)this.CurrentEs / this.MaxEs) * 100f : 100f;

            // Combined effective health pool (unreserved HP + unreserved ES)
            int totalMaxPool = this.MaxHp + this.MaxEs;
            int totalCurPool = this.CurrentHp + this.CurrentEs;
            this.CombinedPercent = totalMaxPool > 0 ? ((float)totalCurPool / totalMaxPool) * 100f : 100f;

            // Unreserved Mana
            int unreservedMana = life.Mana.Unreserved > 0 ? life.Mana.Unreserved : life.Mana.Total;
            this.MaxMana = Math.Max(0, unreservedMana);
            this.CurrentMana = Math.Clamp(life.Mana.Current, 0, this.MaxMana);
            this.ManaPercent = this.MaxMana > 0 ? ((float)this.CurrentMana / this.MaxMana) * 100f : 100f;
        }

        public bool IsLowVital(SkillSlotConfig slot)
        {
            if (!slot.OnlyOnLowHp)
            {
                return false;
            }

            float eval = slot.VitalCondition switch
            {
                VitalConditionType.HpOnly => this.HpPercent,
                VitalConditionType.EsOnly => this.HasEs ? this.EsPercent : 100f,
                _ => this.CombinedPercent,
            };

            return eval <= slot.LowHpThresholdPercent;
        }
    }
}
