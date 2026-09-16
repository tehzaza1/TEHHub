namespace ExpeditionPathOptimizer
{
    using System.Collections.Generic;
    using System.Numerics;
    using ExpeditionPathOptimizer.PathPlannerData;
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
        public float BlastRadiusOpacity { get; set; } = 0.25f;
        public float PathLineWidth { get; set; } = 3.0f;
        public bool ShowDetonatorMarker { get; set; } = true;
        public uint DetonatorBadgeBgColor { get; set; } = 0xFF27AE60u; // Emerald Green anchor (Badge 0)
        public uint ExplosiveColor { get; set; } = 0xFF9900CCu; // Purple
        public uint PathLineColor { get; set; } = 0xFF00D7FFu; // Gold
        public uint BadgeBgColor { get; set; } = 0xFF141414u;

        // Search Algorithm Parameters
        public int SearchThreads { get; set; } = 4;
        public float MaximumGenerationTimeSeconds { get; set; } = 4.0f;
        public int PathGenerationSize { get; set; } = 100;
        public float PathMutateChance { get; set; } = 0.5f;
        public float NewRandomPathInjectionRate { get; set; } = 1.0f;
        public int ValidatedIntermediatePoints { get; set; } = 1;

        // Weights
        public float RunicMonsterWeight { get; set; } = 3.0f;
        public float RunicMonsterLogbookWeight { get; set; } = 3.0f;
        public float NormalMonsterWeight { get; set; } = 0.2f;

        public Dictionary<ExpeditionChestType, float> ChestWeights { get; set; } = new()
        {
            { ExpeditionChestType.Currency, 3.0f },
            { ExpeditionChestType.Artifact, 2.5f },
            { ExpeditionChestType.Map, 2.0f },
            { ExpeditionChestType.Fragment, 2.0f },
            { ExpeditionChestType.Unique, 1.5f },
            { ExpeditionChestType.Gem, 1.5f },
            { ExpeditionChestType.Equipment, 1.0f },
            { ExpeditionChestType.Generic, 1.0f }
        };
    }
}
