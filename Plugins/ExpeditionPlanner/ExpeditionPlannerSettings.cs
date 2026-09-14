namespace ExpeditionPlanner
{
    using System.Collections.Generic;
    using ClickableTransparentOverlay.Win32;
    using TEHhub.Plugin;

    public sealed class ExpeditionPlannerSettings : IPSettings
    {
        public PlannerProfile Profile { get; set; } = PlannerProfile.PillarFirst;

        // Route calculation is intentionally manual: hotkey or button only.
        public VK CalculateHotkey { get; set; } = VK.F6;

        public float MaxPlacementRangeGrid { get; set; } = 90.0f;
        public float BlastRadiusGrid { get; set; } = 30.0f;
        public float BombClearanceRadiusGrid { get; set; } = 5.0f;
        public float CampExclusionRadiusGrid { get; set; } = 15.0f;
        public int MaxExplosiveBudget { get; set; } = 4;

        // Reward Marker Weights
        public float WeightChest { get; set; } = 50.0f;
        public float WeightElite { get; set; } = 35.0f;
        public float WeightMonster { get; set; } = 10.0f;
        public float WeightRemnant { get; set; } = 40.0f;
        public float WeightSentinel { get; set; } = 80.0f;
        public float WeightBoss { get; set; } = 150.0f;

        // Known Rune Priority Weights based on Grand Expedition Tier List
        public Dictionary<string, float> RuneWeights { get; set; } = new()
        {
            { "Opulent", 1000.0f }, // Golden: SSS-Tier, doubles loot drops, highest priority
            { "Bond", 450.0f },     // S-Tier Purple
            { "Oath", 420.0f },     // S-Tier Purple
            { "Power", 400.0f },    // S-Tier Purple
            { "Death", 400.0f },    // S-Tier Purple
            { "Time", 250.0f },     // A-Tier Purple
            { "Rebirth", 220.0f },  // A-Tier Blue
            { "Arcane", 90.0f },    // B-Tier Purple
            { "Soul", 80.0f },      // B-Tier Purple
            { "Celestial", 80.0f }, // B-Tier Purple
            { "Prismatic", 80.0f }, // B-Tier Purple
            { "Vision", 80.0f },    // B-Tier Purple
            { "Wisdom", 80.0f },    // B-Tier Purple
            { "Rage", 70.0f },      // B-Tier Purple
            { "Protective", 70.0f } // B-Tier Purple
        };

        public HashSet<string> NeverTakeRunes { get; set; } = new();

        // UI Overlay options
        public bool ShowBadges { get; set; } = true;
        public bool ShowReasonCard { get; set; } = true;
        public bool ShowBlastRadius { get; set; } = true;
        public bool ShowTargetScores { get; set; } = true;
        public bool ShowPillarOrderOnLargeMap { get; set; } = true;
        public float BadgeRadius { get; set; } = 22.0f;
    }
}

