namespace ExpeditionPathOptimizer
{
    using System;
    using System.Collections.Generic;
    using ClickableTransparentOverlay.Win32;
    using TEHhub.Plugin;

    public enum OptimizationMode
    {
        /// <summary>
        /// Balanced: current behavior — optimizes remnant hits, rune slots, recipes, and chests.
        /// </summary>
        Balanced = 0,

        /// <summary>
        /// Pillar Loot Only: optimizes actual pillar recipe value only.
        /// Suppresses generic farming incentives (RemnantHitBaseScore, RuneSlotMultiplier,
        /// FinalTargetBonus, ChestHitBaseScore). Zeros chest influence entirely.
        /// </summary>
        PillarLootOnly = 1,
    }

    public sealed class ExpeditionPathOptimizerSettings : IPSettings
    {
        public bool Enable { get; set; } = true;
        public bool AutoStartOnAreaChange { get; set; } = true;
        public VK ScanAndStartHotkey { get; set; } = (VK)0;

        // Optimization Mode (default = Balanced; old JSON without this key deserializes to Balanced = 0)
        public OptimizationMode OptimizationMode { get; set; } = OptimizationMode.Balanced;

        // Price Service Settings
        public string League { get; set; } = "Forbidden Rites";
        public int PriceSource { get; set; } = 1; // 0 = poe2scout, 1 = poe.ninja
        public int AutoRefreshMinutes { get; set; } = 30;

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

        // V1 & V2 Scoring Parameters
        public double RemnantHitBaseScore { get; set; } = 150.0;
        public double RuneSlotMultiplier { get; set; } = 100.0;
        public double FinalTargetBonus { get; set; } = 1000.0;
        public double UsefulBridgePenalty { get; set; } = 20.0; // Bridge ไปหาเป้าหมายได้ = -20
        public double EmptyBombPenalty { get; set; } = 150.0; // Bridge ที่ไม่ช่วยอะไร = -150
        public double TravelPenaltyMultiplier { get; set; } = 0.0;
        public double ShortBridgePenaltyThreshold { get; set; } = 0.60; // ลงโทษเฉพาะ bridge ที่สั้นกว่า 60% ของ ExplosionRange
        public double ShortBridgePenaltyMultiplier { get; set; } = 100.0;
        public double BacktrackPenaltyPerGrid { get; set; } = 12.0;

        // Route Recipe Economics & Persistent Oath Parameters
        public double RecipePriceScoreMultiplier { get; set; } = 2.0;
        public double OathPerSlotExposurePenalty { get; set; } = 20.0;

        // Chest Scoring Parameters (V2 Phase 1)
        public double ChestHitBaseScore { get; set; } = 40.0;

        // Authoritative Rune Base Weights
        public Dictionary<string, double> RuneWeights { get; set; } = new(StringComparer.OrdinalIgnoreCase)
        {
            // Tier S
            { "Opulent", 150.0 },
            { "Bond", 120.0 },
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
            { "Oath", 20.0 },
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

        // Propagated Rune Score Overrides (e.g. Oath has -150 penalty only when in GoldenSlot)
        public Dictionary<string, double> PropagationScoreOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase)
        {
            { "Oath", -150.0 }
        };

        public double GetRuneWeight(string? runeName)
        {
            if (string.IsNullOrEmpty(runeName)) return 0.0;
            return this.RuneWeights != null && this.RuneWeights.TryGetValue(runeName, out var w) ? w : 20.0;
        }

        public double GetPropagatedRuneScore(string? runeName)
        {
            if (string.IsNullOrEmpty(runeName)) return 0.0;
            if (this.PropagationScoreOverrides != null && this.PropagationScoreOverrides.TryGetValue(runeName, out var ov))
                return ov;
            return this.RuneWeights != null && this.RuneWeights.TryGetValue(runeName, out var w) ? w : 20.0;
        }

        public ExpeditionPathOptimizerSettings CloneForSearch()
        {
            var clone = (ExpeditionPathOptimizerSettings)this.MemberwiseClone();
            clone.RuneWeights = this.RuneWeights != null
                ? new Dictionary<string, double>(this.RuneWeights, StringComparer.OrdinalIgnoreCase)
                : new(StringComparer.OrdinalIgnoreCase);
            clone.PropagationScoreOverrides = this.PropagationScoreOverrides != null
                ? new Dictionary<string, double>(this.PropagationScoreOverrides, StringComparer.OrdinalIgnoreCase)
                : new(StringComparer.OrdinalIgnoreCase);
            return clone;
        }
    }

    public static class ExpeditionUiConstants
    {
        public static readonly string[] PriceSourceLabels = new[] { "poe2scout", "poe.ninja" };
        public const string PriceSourceComboString = "poe2scout\0poe.ninja\0\0";

        // Optimization Mode
        public static readonly string[] OptimizationModeLabels = new[] { "Balanced", "Pillar Loot Only" };
        public const string OptimizationModeComboString = "Balanced\0Pillar Loot Only\0\0";

        // Search Algorithm Ranges
        public const int SearchThreadsMin = 1;
        public const int SearchThreadsMax = 12;

        public const float SearchDurationMinSec = 1.0f;
        public const float SearchDurationMaxSec = 10.0f;

        public const int PopulationSizeMin = 20;
        public const int PopulationSizeMax = 500;

        public const float MutateChanceMin = 0.0f;
        public const float MutateChanceMax = 1.0f;

        public const float RandomPathInjectionMin = 0.0f;
        public const float RandomPathInjectionMax = 2.0f;

        // Scoring & Penalties Ranges
        public const float RecipePriceMultiplierMin = 0.0f;
        public const float RecipePriceMultiplierMax = 5.0f;

        public const float OathPerSlotPenaltyMin = 0.0f;
        public const float OathPerSlotPenaltyMax = 100.0f;

        public const float BacktrackPenaltyMin = 0.0f;
        public const float BacktrackPenaltyMax = 20.0f;

        public const float ChestScoreMin = 0.0f;
        public const float ChestScoreMax = 150.0f;

        public const float UsefulBridgePenaltyMin = 0.0f;
        public const float UsefulBridgePenaltyMax = 100.0f;

        public const float EmptyBombPenaltyMin = 50.0f;
        public const float EmptyBombPenaltyMax = 300.0f;

        public const float ShortBridgeReachPctMin = 20.0f;
        public const float ShortBridgeReachPctMax = 90.0f;

        public const float ShortBridgePenaltyMin = 0.0f;
        public const float ShortBridgePenaltyMax = 300.0f;

        public const float TravelPenaltyMin = 0.0f;
        public const float TravelPenaltyMax = 100.0f;

        // Rune Weights Ranges & Visual Groupings
        public const float RuneWeightMin = 0.0f;
        public const float RuneWeightMax = 500.0f;

        public static readonly string[] TierSRunes = new[] { "Opulent", "Bond", "Power", "Death" };
        public static readonly string[] TierARunes = new[] { "Time", "Rebirth" };
        public static readonly string[] TierBRunes = new[] { "Arcane", "Prismatic", "Soul", "Vision", "Celestial", "Rage", "Wisdom", "Momentum", "Electrocuting" };
        public static readonly string[] TierCRunes = new[] { "Oath", "Earth", "Sky", "Life", "Ward", "Fire", "Cold", "Lightning", "Tempest", "Bloodletting", "Stone", "Adaptive", "Toxic", "Protective", "Cyclonic", "Tidal", "Gasp", "Moon", "Bait" };

        // Price Service Ranges
        public const int AutoRefreshMinutesMin = 5;
        public const int AutoRefreshMinutesMax = 120;
    }
}


