// <copyright file="IFarmPlan.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Modes.WaveFarm
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;

    /// <summary>
    /// A farm plan configures how the wave farming mode behaves.
    /// Ported directly from AutoExile 1 IFarmPlan.
    /// </summary>
    public interface IFarmPlan
    {
        string Name { get; }

        /// <summary>Wave behavior tuning for this plan.</summary>
        WaveConfig Config { get; }

        /// <summary>After map is fully cleared, what's next? Returns an action or null to exit.</summary>
        WaveAction? GetPostClearAction(BotContext ctx);

        /// <summary>Reset for new run.</summary>
        void Reset();
    }

    /// <summary>Wave behavior tuning.</summary>
    public class WaveConfig
    {
        /// <summary>Minimum monster density to slow down for combat. 0 = never pause.</summary>
        public int PauseDensity { get; set; } = 3;

        /// <summary>Suppress combat repositioning.</summary>
        public bool SuppressCombatPositioning { get; set; } = false;

        /// <summary>Chaos value threshold to justify backtracking for loot behind the player.</summary>
        public double BacktrackLootThreshold { get; set; } = 50.0;

        /// <summary>Dot product threshold for "ahead" check. 0 = forward hemisphere, 0.5 = ~60 deg cone.</summary>
        public float ForwardAngle { get; set; } = 0f;

        /// <summary>Minimum exploration coverage to consider map "cleared enough" (0.0 to 1.0).</summary>
        public float MinCoverage { get; set; } = 0.85f;

        /// <summary>Minimum kill ratio before considering the map done.</summary>
        public float MinKillRatio { get; set; } = 0f;
    }

    /// <summary>An action the wave tick loop should execute.</summary>
    public struct WaveAction
    {
        public WaveActionType Type;
        public Vector2 TargetGridPos;
        public uint TargetEntityId;

        public static WaveAction Explore(Vector2 target) =>
            new() { Type = WaveActionType.Explore, TargetGridPos = target };

        public static WaveAction PickupLoot(uint entityId, Vector2 pos) =>
            new() { Type = WaveActionType.PickupLoot, TargetEntityId = entityId, TargetGridPos = pos };

        public static WaveAction Interact(uint entityId, Vector2 pos) =>
            new() { Type = WaveActionType.Interact, TargetEntityId = entityId, TargetGridPos = pos };

        public static readonly WaveAction ExitMap = new() { Type = WaveActionType.ExitMap };
        public static readonly WaveAction None = new() { Type = WaveActionType.None };
    }

    public enum WaveActionType
    {
        None,
        Explore,
        PickupLoot,
        Interact,
        ExitMap,
    }
}
