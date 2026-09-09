// <copyright file="AlchAndGoPlan.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Modes.WaveFarm.FarmPlans
{
    /// <summary>
    /// Simplest farm plan: clear map, loot forward items, engage monster clusters, exit.
    /// Pure alch-and-go mapping for PoE 2.
    /// Ported directly from AutoExile 1 AlchAndGoPlan.
    /// </summary>
    public class AlchAndGoPlan : IFarmPlan
    {
        public string Name => "Alch & Go";

        public WaveConfig Config { get; } = new()
        {
            PauseDensity = 3,
            BacktrackLootThreshold = 20.0,
            MinCoverage = 0.85f,
        };

        public WaveAction? GetPostClearAction(BotContext ctx)
        {
            // Nothing to do post-clear -> exit map
            return null;
        }

        public void Reset()
        {
        }
    }
}
