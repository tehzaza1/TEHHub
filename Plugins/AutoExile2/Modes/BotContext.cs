// <copyright file="BotContext.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Modes
{
    using System;
    using System.Numerics;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.RemoteObjects.States.InGameStateObjects;
    using AutoExile2.Systems;

    /// <summary>
    /// Passed to bot modes each tick. Provides unified access to game state and shared systems
    /// without coupling modes directly to AutoExile2Core.
    /// Ported directly from AutoExile 1 BotContext.
    /// </summary>
    public sealed class BotContext
    {
        public required AreaInstance Area { get; init; }
        public required WorldData World { get; init; }
        public required Entity Player { get; init; }
        public required Vector2 PlayerGrid { get; init; }
        public required float DeltaTime { get; init; }
        public required AutoExile2Settings Settings { get; init; }
        public required CombatSystem Combat { get; init; }
        public required ExplorationMap Exploration { get; init; }
        public required ThreatMap ThreatMap { get; init; }
        public required PerformanceTracker Perf { get; init; }
        public required RuntimeTracker Runtime { get; init; }
        public required BotRecorder Recorder { get; init; }
        public required CoopVirtualGamepad CoopGamepad { get; init; }
        public Action<string> Log { get; init; } = _ => { };
    }
}
