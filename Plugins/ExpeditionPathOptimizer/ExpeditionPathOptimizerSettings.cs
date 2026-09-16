namespace ExpeditionPathOptimizer
{
    using System;
    using System.Collections.Generic;
    using TEHhub.Plugin;

    public sealed class ExpeditionPathOptimizerSettings : IPSettings
    {
        public bool Enable { get; set; } = true;
        public bool AutoStartOnAreaChange { get; set; } = true;

        // Visuals
        public bool ShowWorldMarkers { get; set; } = true;
        public bool ShowLargeMapMarkers { get; set; } = true;
        public bool ShowBlastRadius { get; set; } = true;
        public bool ShowPathLines { get; set; } = true;
        public bool ShowDetonatorMarker { get; set; } = true;
        public float BlastRadiusOpacity { get; set; } = 0.25f;
        public float PathLineWidth { get; set; } = 3.0f;
        public uint DetonatorBadgeBgColor { get; set; } = 0xFF27AE60u; // Emerald Green anchor (Badge 0)
        public uint FinalTargetBadgeBgColor { get; set; } = 0xFF00B4D8u; // Gold / Amber for Final Target (Badge N)
        public uint ExplosiveColor { get; set; } = 0xFF9900CCu; // Purple
        public uint PathLineColor { get; set; } = 0xFF00D7FFu; // Gold wire
        public uint BadgeBgColor { get; set; } = 0xFF141414u; // Dark charcoal

        // Search Algorithm Parameters
        public int SearchThreads { get; set; } = 4;
        public float MaximumGenerationTimeSeconds { get; set; } = 4.0f;
        public int PathGenerationSize { get; set; } = 100;
        public float PathMutateChance { get; set; } = 0.5f;
        public float NewRandomPathInjectionRate { get; set; } = 1.0f;

        // V1 Scoring Parameters
        public double RemnantHitBaseScore { get; set; } = 150.0;
        public double RuneSlotMultiplier { get; set; } = 100.0;
        public double FinalTargetBonus { get; set; } = 1000.0;
        public double FinalRuneBonus { get; set; } = 100.0;
        public double UsefulBridgePenalty { get; set; } = 20.0; // Bridge ไปหาเป้าหมายได้ = -20
        public double EmptyBombPenalty { get; set; } = 150.0; // Bridge ที่ไม่ช่วยอะไร = -150
        public double FuturePotentialBonusMultiplier { get; set; } = 15.0; // ระเบิดยังเหลือ + Remnant Reachable -> FuturePotential bonus
        public double TravelPenaltyMultiplier { get; set; } = 60.0;

        // Authoritative Rune Base Weights
        public Dictionary<string, double> RuneWeights { get; set; } = new(StringComparer.OrdinalIgnoreCase)
        {
            // Tier S
            { "Opulent", 150.0 },
            { "Bond", 120.0 },
            { "Oath", 110.0 },
            { "Power", 100.0 },
            { "Death", 100.0 },

            // Tier A
            { "Time", 80.0 },
            { "Rebirth", 60.0 },

            // Tier B
            { "Arcane", 30.0 },
            { "Prismatic", 30.0 },
            { "Soul", 25.0 },
            { "Vision", 25.0 },
            { "Celestial", 25.0 },
            { "Rage", 25.0 },
            { "Wisdom", 25.0 },

            // Tier C (20.0)
            { "Earth", 20.0 },
            { "Sky", 20.0 },
            { "Life", 20.0 },
            { "Ward", 20.0 },
            { "Fire", 20.0 },
            { "Cold", 20.0 },
            { "Lightning", 20.0 },
            { "Tempest", 20.0 },
            { "Bloodletting", 20.0 },
            { "Stone", 20.0 },
            { "Adaptive", 20.0 },
            { "Toxic", 20.0 },
            { "Protective", 20.0 },
            { "Cyclonic", 20.0 },
            { "Tidal", 20.0 },
            { "Gasp", 20.0 },
            { "Moon", 20.0 },
            { "Bait", 20.0 },

            // Momentum & Electrocuting
            { "Momentum", 25.0 },
            { "Electrocuting", 25.0 }
        };
    }
}
