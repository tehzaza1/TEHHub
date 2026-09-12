// <copyright file="AmanamuVoidAlertSettings.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AmanamuVoidAlert
{
    using System.Numerics;
    using TEHhub.Plugin;

    /// <summary>
    /// Settings for the Amanamu Void Alert plugin.
    /// </summary>
    public sealed class AmanamuVoidAlertSettings : IPSettings
    {
        /// <summary>Enable/disable overlay rendering.</summary>
        public bool EnableOverlay = true;

        /// <summary>Show debug window with tracked monsters info.</summary>
        public bool ShowDebugWindow = false;

        /// <summary>Hide overlay while the game is unfocused or paused.</summary>
        public bool HideWhenGameUnfocusedOrPaused = true;

        /// <summary>Draw labels on screen above monsters.</summary>
        public bool DrawOnScreenLabels = true;

        /// <summary>Draw edge arrows for off-screen monsters.</summary>
        public bool DrawOffscreenArrows = true;

        /// <summary>Draw edge arrow even when monster is on screen.</summary>
        public bool DrawEdgeArrowForOnScreenMonsters = true;

        /// <summary>Draw circle around monster.</summary>
        public bool DrawCircle = true;

        /// <summary>Only track Rare or Unique monsters.</summary>
        public bool OnlyRareOrUnique = true;

        /// <summary>Log newly detected monsters to TEHhub console/log.</summary>
        public bool LogNewDetections = true;

        /// <summary>Maximum tracking distance from player (grid units / distance units).</summary>
        public float MaxDistance = 3500f;

        /// <summary>Forget tracked monster after seconds of not seeing it.</summary>
        public float ForgetAfterSeconds = 15f;

        /// <summary>Forget missing live entity after seconds.</summary>
        public float MissingEntityForgetSeconds = 1.25f;

        /// <summary>Label Y offset in pixels above monster.</summary>
        public float LabelYOffset = 70f;

        /// <summary>Screen circle radius in pixels.</summary>
        public float CircleRadius = 34f;

        /// <summary>Circle outline thickness in pixels.</summary>
        public float CircleThickness = 3f;

        /// <summary>Arrow margin from screen edges in pixels.</summary>
        public float ArrowEdgeMargin = 95f;

        /// <summary>Color when monster is inside the void cloud.</summary>
        public Vector4 InsideCloudColor = new(0.71f, 0.31f, 1.0f, 1.0f);

        /// <summary>Color when monster is outside the void cloud.</summary>
        public Vector4 OutsideCloudColor = new(0.31f, 1.0f, 0.47f, 1.0f);
    }
}
