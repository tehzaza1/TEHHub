namespace NinjaPricer
{
    using System.Collections.Generic;
    using System.Numerics;
    using TEHhub.Plugin;

    public enum DisplayCurrency
    {
        Divine = 0,
        Exalted = 1,
        Chaos = 2,
    }

    public enum PriceDisplayStyle
    {
        Image = 0,
        Text = 1,
    }

    public enum UiPricePosition
    {
        TopLeft = 0,
        TopRight = 1,
        BottomLeft = 2,
        BottomRight = 3,
    }

    public enum GroundPricePosition
    {
        Top = 0,
        Bottom = 1,
        Left = 2,
        Right = 3,
    }

    public class NinjaPricerSettings : IPSettings
    {
        // Data Source
        public string League { get; set; } = "Forbidden Rites";
        public int PriceSource { get; set; } = 1; // 0 = poe2scout, 1 = poe.ninja
        public int AutoRefreshMinutes { get; set; } = 30;

        // Display
        public DisplayCurrency DisplayCurrency { get; set; } = DisplayCurrency.Divine;
        public PriceDisplayStyle PriceDisplayStyle { get; set; } = PriceDisplayStyle.Image;
        public float TextScale { get; set; } = 1.0f;
        public UiPricePosition UiPricePosition { get; set; } = UiPricePosition.BottomRight;
        public GroundPricePosition GroundPricePosition { get; set; } = GroundPricePosition.Top;

        // Overlay Toggles
        public bool ShowGroundPrices { get; set; } = true;
        public bool ShowInventoryPrices { get; set; } = true;
        public bool ShowOtherInventoryPrices { get; set; } = true; // Stash
        public bool ShowRitualPrices { get; set; } = true;
        public bool ShowRuneshapePrices { get; set; } = true;
        public bool ShowRuneshapeWeights { get; set; } = true;
        public bool ShowItemIcons { get; set; } = true;
        public bool HideWhenUnfocused { get; set; } = false;
        public int HideHotkey { get; set; } = 0;
        public float MinPriceChaos { get; set; } = 0.0f;
        public int ScanIntervalMs { get; set; } = 100;

        // Valuable Drop Alerts
        public bool EnableAlertSound { get; set; } = true;
        public int AlertVolumePercent { get; set; } = 50;
        public bool EnableAlertBanner { get; set; } = true;
        public float AlertBannerDurationSec { get; set; } = 6.0f;
        public bool EnableAlertBeam { get; set; } = true;
        public float AlertMinDisplayValue { get; set; } = 1.0f;

        // Runeshape Movable Window
        public bool ShowRuneshapeWindow { get; set; } = false;
        public bool ShowRuneshapeWorldMarkers { get; set; } = true;
        public float RuneshapeWinX { get; set; } = 100.0f;
        public float RuneshapeWinY { get; set; } = 100.0f;
        public float RuneshapeWinAlpha { get; set; } = 0.85f;
        public bool RuneshapeWinCollapsed { get; set; } = false;
        public int RuneshapeWinHotkey { get; set; } = 0x76; // F7
        public bool RuneshapeWinHideOnHover { get; set; } = false;
        public PriceDisplayStyle RuneshapeWinPriceStyle { get; set; } = PriceDisplayStyle.Image;
        public bool RsShowHdrColor { get; set; } = true;
        public bool RsShowHdrRunes { get; set; } = true;
        public bool RsShowHdrBest { get; set; } = true;
        public bool RsShowRowIcon { get; set; } = true;
        public bool RsShowRowName { get; set; } = true;
        public bool RsShowRowQty { get; set; } = true;
        public bool RsShowRowPrice { get; set; } = true;
        public bool RsShowRowPropRunes { get; set; } = true;
        public HashSet<uint> RuneshapeCollapsed { get; set; } = new();

        // Category Toggles
        public Dictionary<string, bool> EnabledCategories { get; set; } = new()
        {
            { "currency", true },
            { "fragments", true },
            { "uncutgems", true },
            { "essences", true },
            { "soulcores", true },
            { "idols", true },
            { "runes", true },
            { "expedition", true },
            { "verisium", true },
            { "ritual", true },
            { "delirium", true },
            { "breach", true },
            { "abyss", true },
            { "lineagesupportgems", true },
            { "vaultkeys", true },
            { "incursion", true },
            { "ultimatum", true },
            { "vaal", true },
            { "weapon", true },
            { "armour", true },
            { "accessory", true },
            { "flask", true },
            { "jewel", true },
            { "map", true },
            { "sanctum", true },
        };
    }
}
