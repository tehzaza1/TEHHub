// <copyright file="IBotMode.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Modes
{
    using System.Collections.Generic;
    using System.Numerics;

    /// <summary>
    /// A bot mode controls high-level behavior: what the bot is trying to accomplish.
    /// Ported directly from AutoExile 1 Modes architecture (IBotMode).
    /// </summary>
    public interface IBotMode
    {
        /// <summary>Display name of this mode.</summary>
        string Name { get; }

        /// <summary>Display description of this mode for web UI.</summary>
        string Description => "Standard bot mode";

        /// <summary>Display icon for web UI.</summary>
        string Icon => "🤖";

        /// <summary>Enum type for this mode.</summary>
        AutoExileMode ModeType { get; }

        /// <summary>Current lifecycle/operational state description.</summary>
        string CurrentState { get; }

        /// <summary>Current active tactical action description.</summary>
        string CurrentAction { get; }

        /// <summary>Current active navigation path waypoints.</summary>
        List<Vector2> CurrentNavPath { get; }

        /// <summary>Current index in the navigation path.</summary>
        int CurrentWaypointIndex { get; }

        /// <summary>Current ultimate destination coordinate.</summary>
        Vector2? CurrentDestination { get; }

        /// <summary>Called once when this mode becomes active.</summary>
        void OnEnter(BotContext ctx);

        /// <summary>Called once when switching away from this mode.</summary>
        void OnExit(BotContext ctx);

        /// <summary>Called every game tick while this mode is active and the bot is running.</summary>
        void Tick(BotContext ctx);

        /// <summary>Optional: render debug / in-game visual overlay for this mode.</summary>
        void Render(BotContext ctx);
    }
}
