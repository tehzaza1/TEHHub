namespace ExpeditionPlanner
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;

    public enum TargetKind
    {
        Unknown = 0,
        Detonator,
        PlacedExplosive,
        ConnectorPole,
        Fuse,
        ChestReward,
        EliteMonster,
        NormalMonster,
        RemnantPillar,
        VerisiumSentinel,
        ExpeditionBoss
    }

    public enum RuneTier
    {
        Golden = 0,     // Opulent (Increases monster rarity & loot, doubles drops)
        Purple_S,       // Power, Death, Bond, Oath
        Purple_A,       // Time, Rebirth
        Purple_B,       // Other purple runes
        Blue_C          // Blue runes (filler for quantity stack scaling)
    }

    public enum PlannerProfile
    {
        Safe = 0,
        Balanced,
        Greedy
    }

    public sealed class ExpeditionTarget
    {
        public uint EntityId { get; set; }
        public string Path { get; set; } = string.Empty;
        public TargetKind Kind { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public Vector3 GridPosition { get; set; }
        public Vector3 WorldPosition { get; set; }
        public float TerrainHeight { get; set; }
        public List<string> ModNames { get; set; } = new();
        public float BaseWeight { get; set; }
        public bool IsDangerous { get; set; }

        // Remnant Pillar / Monolith specific details
        public int HoleCount { get; set; }
        public string AnchorRuneName { get; set; } = string.Empty;
        public string RecommendedRuneChoice { get; set; } = string.Empty;
        public string RecipeDescription { get; set; } = string.Empty;
        public int ProliferationRemaining { get; set; }

        // Golden Slot & Proliferation Mechanics
        public string ProliferatedRuneName { get; set; } = string.Empty;
        public RuneTier ProliferatedRuneTier { get; set; } = RuneTier.Blue_C;
        public bool NeedsReroll { get; set; }
        public string RerollReason { get; set; } = string.Empty;
        public bool IsDuplicateProliferation { get; set; }
    }

    public sealed class PlacedBombInfo
    {
        public uint EntityId { get; set; }
        public Vector3 GridPosition { get; set; }
        public Vector3 WorldPosition { get; set; }
        public int Order { get; set; }
    }

    public sealed class ProposedPlacement
    {
        public int Step { get; set; }
        public Vector3 GridPosition { get; set; }
        public Vector3 WorldPosition { get; set; }
        public float TerrainHeight { get; set; }
        public float WireDistance { get; set; }
        public bool IsObstructed { get; set; }
        public List<ExpeditionTarget> CoveredTargets { get; set; } = new();
        public List<string> GainedRunes { get; set; } = new();
    }

    public sealed class RouteEvaluation
    {
        public List<ProposedPlacement> Placements { get; set; } = new();
        public float NetScore { get; set; }
        public string Profile { get; set; } = "Balanced";
        public string Reason { get; set; } = string.Empty;
        public bool IsUnsafe { get; set; }
        public List<string> Warnings { get; set; } = new();
        public List<string> ProliferatedStack { get; set; } = new();
        public int RerollRemnantCount { get; set; }
        public DateTime GeneratedUtc { get; set; } = DateTime.UtcNow;
    }
}
