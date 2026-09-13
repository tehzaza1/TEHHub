namespace ExpeditionPlanner
{
    using System.Collections.Generic;
    using TEHhub.Plugin;

    public sealed class ExpeditionPlannerSettings : IPSettings
    {
        public PlannerProfile Profile { get; set; } = PlannerProfile.Balanced;

        public float MaxPlacementRangeGrid { get; set; } = 90.0f;
        public float BlastRadiusGrid { get; set; } = 46.0f;
        public int MaxExplosiveBudget { get; set; } = 4;

        // Reward Marker Weights
        public float WeightChest { get; set; } = 50.0f;
        public float WeightElite { get; set; } = 35.0f;
        public float WeightMonster { get; set; } = 10.0f;
        public float WeightRemnant { get; set; } = 40.0f;
        public float WeightSentinel { get; set; } = 80.0f;
        public float WeightBoss { get; set; } = 150.0f;

        // Known Rune Priority Weights
        public Dictionary<string, float> RuneWeights { get; set; } = new()
        {
            { "Opulent", 100.0f },
            { "Power", 60.0f },
            { "Death", 60.0f },
            { "Bond", 60.0f },
            { "Oath", 60.0f },
            { "Time", 40.0f },
            { "Rebirth", 40.0f },
            { "Wisdom", 35.0f },
            { "Inspiration", 30.0f },
            { "Resolve", 30.0f }
        };

        public HashSet<string> NeverTakeRunes { get; set; } = new();

        // UI Overlay options
        public bool ShowBadges { get; set; } = true;
        public bool ShowReasonCard { get; set; } = true;
        public bool ShowBlastRadius { get; set; } = true;
        public bool ShowTargetScores { get; set; } = true;
        public float BadgeRadius { get; set; } = 22.0f;
    }
}
