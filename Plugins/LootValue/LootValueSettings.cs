// <copyright file="LootValueSettings.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace LootValue
{
    using System.Numerics;
    using TEHhub.Plugin;

    /// <summary>
    /// <see cref="LootValue"/> plugin settings.
    /// </summary>
    public sealed class LootValueSettings : IPSettings
    {
        /// <summary>Draw value labels over dropped items on the ground.</summary>
        public bool ShowOverlay = true;

        /// <summary>Draw value labels over items in the open stash panel.</summary>
        public bool ShowStashOverlay = true;

        /// <summary>Draw value labels over items in the open inventory panel.</summary>
        public bool ShowInventoryOverlay = false;

        /// <summary>Draw value labels over items in the open Ritual reward window.</summary>
        public bool ShowRitualOverlay = true;

        /// <summary>Draw owned-stack values in the Currency Exchange item list.</summary>
        public bool ShowCurrencyExchangeOverlay = true;

        /// <summary>Hide all value overlays while the game is not the foreground window.</summary>
        public bool HideWhenGameInBackground = true;

        /// <summary>Hide stash and inventory labels while an item slot is hovered.</summary>
        public bool HideSlotPricesOnHover = true;

        /// <summary>Draw item-slot discovery diagnostics.</summary>
        public bool ShowSlotDebugInfo = false;

        /// <summary>Anchor value chips to the game's loot labels (avoids overlap when items pile up)
        /// instead of drawing free-floating world-space labels over each drop. Default mode.</summary>
        public bool AnchorToLootTags = true;

        /// <summary>Price source: <see cref="PoeNinjaPriceFetcher.SourcePoeNinja"/>.</summary>
        public int PriceSource = PoeNinjaPriceFetcher.SourcePoeNinja;

        /// <summary>PoE2 league name for price lookups.</summary>
        public string League = "Forbidden Rites";

        /// <summary>Version marker for one-time default-league migrations.</summary>
        public int? LeagueMigrationVersion;

        /// <summary>Automatic price refresh interval in minutes.</summary>
        public int RefreshIntervalMin = 30;

        /// <summary>Display currency: 0 = Divine, 1 = Exalted, 2 = Chaos.</summary>
        public int DisplayCurrency = 1;

        /// <summary>Minimum value for a drop to get a label (legacy/unused).</summary>
        public float MinValueEx = 0f;

        /// <summary>Value (in Exalted) at/above which a label is drawn in the highlight color.</summary>
        public float HighlightMinEx = 100f;

        /// <summary>Value (in Chaos) at/above which a label is drawn in the highlight color.</summary>
        public float HighlightMinChaos = 10f;

        /// <summary>Value (in Divine) at/above which a label is drawn in the highlight color.</summary>
        public float HighlightMinDiv = 1f;

        /// <summary>Play sound when a valuable drop is detected.</summary>
        public bool EnableAlertSound = true;

        /// <summary>Alert sound volume (0 to 100 percent).</summary>
        public int AlertVolumePercent = 50;

        /// <summary>Show an on-screen alert banner for valuable drops.</summary>
        public bool EnableAlertBanner = true;

        /// <summary>Show a beam / pointer to valuable drops on the ground.</summary>
        public bool EnableAlertBeam = true;

        /// <summary>Minimum value in current Display Currency to trigger drop alert.</summary>
        public float AlertMinDisplayValue = 1f;

        /// <summary>Duration in seconds for the on-screen alert banner.</summary>
        public float AlertBannerDurationSec = 6f;

        /// <summary>Reveal unidentified uniques by name (resolved from their icon art).</summary>
        public bool RevealUnidentifiedUniques = true;

        /// <summary>Show a diagnostics window: the ground-item detection funnel + sample reads.</summary>
        public bool DiagnosticsMode = false;

        /// <summary>Font size (pixels) for normal value labels.</summary>
        public float FontSize = 16f;

        /// <summary>Font size (pixels) for highlighted (high-value) labels.</summary>
        public float HighlightFontSize = 22f;

        /// <summary>Render highlighted labels bold (faux-bold via offset double-draw).</summary>
        public bool HighlightBold = true;

        /// <summary>Vertical pixel offset of the label from the item's world anchor.</summary>
        public float OffsetY = -10f;

        /// <summary>Font scale for stash and inventory labels.</summary>
        public float SlotFontScale = 1f;

        /// <summary>Horizontal pixel offset for stash and inventory labels.</summary>
        public float SlotOffsetX = 5f;

        /// <summary>Vertical pixel offset for stash and inventory labels.</summary>
        public float SlotOffsetY = -5f;

        /// <summary>Smooth label motion with a velocity-tracking (alpha-beta) filter so labels don't jitter
        /// while moving. Unlike a plain low-pass it has no steady-state lag during constant movement.
        /// Applies to both world-space and loot-label modes.</summary>
        public bool InterpolatePosition = true;

        /// <summary>Filter responsiveness, 1-1000 (= alpha x1000). Lower = smoother / more jitter rejection
        /// (still no motion lag thanks to velocity tracking); 1000 = effectively raw.</summary>
        public int InterpolationRate = 110;

        /// <summary>How often (ms) the item set + prices are re-detected. Positions still redraw every frame;
        /// this only controls how quickly new drops appear and how much CPU the scan uses.</summary>
        public int RescanIntervalMs = 200;

        /// <summary>How often (ms) open stash and inventory panels are rescanned and repriced.</summary>
        public int SlotRescanIntervalMs = 750;

        /// <summary>Normal label text color (RGBA 0-1).</summary>
        public Vector4 TextColor = new Vector4(1f, 235f / 255f, 140f / 255f, 1f);

        /// <summary>Highlight label text color (RGBA 0-1) for high-value drops.</summary>
        public Vector4 HighlightColor = new Vector4(0.4f, 1f, 0.4f, 1f);

        /// <summary>Normal background color (RGBA 0-1) for value label chips.</summary>
        public Vector4 BackgroundColor = new Vector4(0f, 0f, 0f, 0.7f);

        /// <summary>Highlight label background color (RGBA 0-1) for high-value items.</summary>
        public Vector4 HighlightBackgroundColor = new Vector4(0.08f, 0.28f, 0.12f, 0.85f);

        /// <summary>Show 3D world badge above PoE 2 Expedition Runeshape Monoliths.</summary>
        public bool EnableExpeditionWorldOverlay = true;

        /// <summary>Horizontal pixel offset for Expedition monolith world badges (Left/Right).</summary>
        public float ExpeditionBadgeOffsetX = 0f;

        /// <summary>Vertical pixel offset for Expedition monolith world badges (Up/Down).</summary>
        public float ExpeditionBadgeOffsetY = 0f;

        /// <summary>Show prices on the in-game Runeshape Combinations recipe panel.</summary>
        public bool EnableRuneshapeUiPrices = true;

        /// <summary>Horizontal pixel offset for Runeshape UI price chips.</summary>
        public float RuneshapeUiOffsetX = 0f;

        /// <summary>Vertical pixel offset for Runeshape UI price chips.</summary>
        public float RuneshapeUiOffsetY = 0f;

        /// <summary>Hide monolith badge if the encounter was already completed / claimed.</summary>
        public bool HideCompletedMonoliths = true;
    }
}
