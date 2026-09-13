// <copyright file="IdleMode.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Modes
{
    using System.Collections.Generic;
    using System.Numerics;
    using AutoExile2.Systems;

    /// <summary>
    /// Default idle mode — pauses bot operations and releases all movement inputs.
    /// Ported directly from AutoExile 1 IdleMode.
    /// </summary>
    public sealed class IdleMode : IBotMode
    {
        public string Name => "Idle";

        public string Description => "Standby mode";

        public string Icon => "💤";

        public AutoExileMode ModeType => AutoExileMode.Idle;

        public string CurrentState => "Idle";

        public string CurrentAction => "Standing by (Paused)";

        public List<Vector2> CurrentNavPath { get; } = new();

        public int CurrentWaypointIndex => 0;

        public Vector2? CurrentDestination => null;

        public void OnEnter(BotContext ctx)
        {
            ctx.Log("Entering Idle Mode");
            BotInput.ReleaseAllMovementKeys(ctx.Settings);
        }

        public void OnExit(BotContext ctx)
        {
            ctx.Log("Exiting Idle Mode");
        }

        public void Tick(BotContext ctx)
        {
            BotInput.ReleaseAllMovementKeys(ctx.Settings);
        }

        public void Render(BotContext ctx)
        {
        }
    }
}
