namespace myFarming
{
    using System.Collections.Generic;
    using System.Numerics;
    using TEHhub.Plugin;

    public enum DisplayCurrency
    {
        Chaos = 0,
        Divine = 1,
        Exalted = 2,
    }

    public sealed class MyFarmingSettings : IPSettings
    {
        public bool ShowOverlay { get; set; } = true;

        public bool HideWhenGameNotFocused { get; set; } = true;

        public bool LockOverlay { get; set; } = false;

        public float OverlayX { get; set; } = 150f;

        public float OverlayY { get; set; } = 250f;

        public float UiScale { get; set; } = 1.0f;

        public float TextScale { get; set; } = 1.0f;

        public bool ShowLootList { get; set; } = true;

        public int MaxLootListRows { get; set; } = 12;

        public bool ShowKills { get; set; } = true;

        public bool ShowProfitPerHour { get; set; } = true;

        public bool ShowSessionSummary { get; set; } = true;

        public bool ShowCurrentMapStats { get; set; } = true;

        public DisplayCurrency Currency { get; set; } = DisplayCurrency.Chaos;

        public float MinItemPriceToDisplay { get; set; } = 0.0f;

        public bool AutoPauseInTownOrHideout { get; set; } = true;

        public Dictionary<string, float> CustomPrices { get; set; } = new();
    }
}
