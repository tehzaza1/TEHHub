// <copyright file="CoopConfig.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2
{
    using System;
    using System.Collections.Generic;
    using AutoExile2.Systems;
    using Newtonsoft.Json;

    /// <summary>
    /// Configuration for an automated Buff / Guard skill on Player 1 (Leader).
    /// Injected seamlessly into Virtual Controller #1 while Player 1 runs/attacks manually.
    /// </summary>
    public class CoopLeaderBuffConfig
    {
        public bool Enabled { get; set; } = true;

        public string Name { get; set; } = "Steelskin";

        /// <summary>Controller button to press on Controller #1.</summary>
        public CoopPadButton Button { get; set; } = CoopPadButton.RightShoulder;

        public int CooldownMs { get; set; } = 4000;

        public bool OnlyWhenBuffMissing { get; set; } = true;

        public string BuffName { get; set; } = "Steelskin";

        public bool OnlyOnLowHp { get; set; } = false;

        public float LowHpThresholdPercent { get; set; } = 60f;

        [JsonIgnore]
        public DateTime LastCastAt { get; set; } = DateTime.MinValue;

        public static List<CoopLeaderBuffConfig> GetDefaults()
        {
            return new List<CoopLeaderBuffConfig>
            {
                new CoopLeaderBuffConfig
                {
                    Name = "Guard Skill (Steelskin)",
                    Button = CoopPadButton.RightShoulder,
                    BuffName = "Steelskin",
                    CooldownMs = 4000,
                    OnlyWhenBuffMissing = true,
                    OnlyOnLowHp = true,
                    LowHpThresholdPercent = 65f,
                },
                new CoopLeaderBuffConfig
                {
                    Name = "Warcry (Enduring Cry)",
                    Button = CoopPadButton.Y,
                    BuffName = "Enduring Cry",
                    CooldownMs = 8000,
                    OnlyWhenBuffMissing = true,
                    OnlyOnLowHp = false,
                },
            };
        }
    }

    /// <summary>
    /// Configuration for an automated combat or support skill on Player 2 (Follower).
    /// Executed autonomously on Virtual Controller #2 (Right Stick aim + Gamepad buttons).
    /// </summary>
    public class CoopFollowerSkillConfig
    {
        public bool Enabled { get; set; } = true;

        public string Name { get; set; } = "Main Attack";

        /// <summary>Controller button to press on Controller #2.</summary>
        public CoopPadButton Button { get; set; } = CoopPadButton.RightShoulder;

        public int Priority { get; set; } = 5;

        public int CooldownMs { get; set; } = 250;

        public int HoldMs { get; set; } = 100;

        public SkillRole Role { get; set; } = SkillRole.EnemyTargeted;

        public float MaxTargetRange { get; set; } = 65f;

        public bool OnlyWhenBuffMissing { get; set; } = false;

        public string BuffDebuffName { get; set; } = string.Empty;

        public bool OnlyOnLowHp { get; set; } = false;

        public float LowHpThresholdPercent { get; set; } = 60f;

        [JsonIgnore]
        public DateTime LastCastAt { get; set; } = DateTime.MinValue;

        public static List<CoopFollowerSkillConfig> GetDefaults()
        {
            return new List<CoopFollowerSkillConfig>
            {
                new CoopFollowerSkillConfig
                {
                    Name = "Primary Attack / Spell",
                    Button = CoopPadButton.RightShoulder,
                    Priority = 5,
                    CooldownMs = 250,
                    HoldMs = 100,
                    Role = SkillRole.EnemyTargeted,
                    MaxTargetRange = 65f,
                },
                new CoopFollowerSkillConfig
                {
                    Name = "Support Curse / Totem",
                    Button = CoopPadButton.Y,
                    Priority = 8,
                    CooldownMs = 5000,
                    HoldMs = 100,
                    Role = SkillRole.PackTargeted,
                    MaxTargetRange = 60f,
                    OnlyWhenBuffMissing = true,
                },
            };
        }
    }
}
