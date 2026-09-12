// <copyright file="BotContext.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Modes
{
    using System;
    using System.Numerics;
    using TEHhub.RemoteObjects.Components;
    using TEHhub.RemoteObjects.States.InGameStateObjects;
    using AutoExile2.Systems;

    /// <summary>
    /// Passed to bot modes each tick. Provides unified access to game state and shared systems
    /// without coupling modes directly to AutoExile2Core.
    /// Ported directly from AutoExile 1 BotContext.
    /// </summary>
    public sealed class BotContext
    {
        public AreaInstance Area { get; set; } = null!;
        public WorldData World { get; set; } = null!;
        public Entity Player { get; set; } = null!;
        public Vector2 PlayerGrid { get; set; }
        public float DeltaTime { get; set; }
        public AutoExile2Settings Settings { get; set; } = null!;
        public CombatSystem Combat { get; set; } = null!;
        public ExplorationMap Exploration { get; set; } = null!;
        public ThreatMap ThreatMap { get; set; } = null!;
        public PerformanceTracker Perf { get; set; } = null!;
        public RuntimeTracker Runtime { get; set; } = null!;
        public BotRecorder Recorder { get; set; } = null!;
        public CoopVirtualGamepad CoopGamepad { get; set; } = null!;
        public Action<string> Log { get; set; } = _ => { };
    }
}
