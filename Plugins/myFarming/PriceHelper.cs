namespace myFarming
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;
    using TEHhub.Plugin;

    public sealed class PriceHelper
    {
        private Dictionary<string, float> sourcePriceCache = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> sourceIconCache = new(StringComparer.OrdinalIgnoreCase);

        private ConcurrentDictionary<string, float> priceCache = new(DefaultCurrencyPrices, StringComparer.OrdinalIgnoreCase);
        private ConcurrentDictionary<string, string> iconCache = new(StringComparer.OrdinalIgnoreCase);
        private DateTime lastPriceLoadUtc = DateTime.MinValue;

        public float DivineInChaos { get; private set; } = 9.43f;
        public float ExaltedInChaos { get; private set; } = 1.0f;

        private static readonly Dictionary<string, float> DefaultCurrencyPrices = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Mirror of Kalandra"] = 1200f,
            ["Divine Orb"] = 9.43f,
            ["Exalted Orb"] = 1.0f,
            ["Chaos Orb"] = 1.0f,
            ["Vaal Orb"] = 0.5f,
            ["Regal Orb"] = 0.3f,
            ["Orb of Alchemy"] = 0.15f,
            ["Gemcutter's Prism"] = 0.8f,
            ["Glassblower's Bauble"] = 0.2f,
            ["Orb of Chance"] = 0.05f,
            ["Orb of Transmutation"] = 0.01f,
            ["Orb of Augmentation"] = 0.01f,
            ["Orb of Alteration"] = 0.05f,
            ["Artificer's Orb"] = 0.3f,
            ["Lesser Jeweller's Orb"] = 0.1f,
            ["Greater Jeweller's Orb"] = 0.5f,
            ["Perfect Jeweller's Orb"] = 2.0f,
            ["Scroll of Wisdom"] = 0.002f,
        };

        public PriceHelper()
        {
            this.RebuildEffectiveCaches(null);
        }

        public void ReloadPrices(Dictionary<string, float>? customPrices = null)
        {
            try
            {
                var ninjaPricePath = Path.Combine(AppContext.BaseDirectory, "configs", "plugins", "NinjaPricer", "ninja_prices.json");
                if (File.Exists(ninjaPricePath))
                {
                    var fi = new FileInfo(ninjaPricePath);
                    if (fi.LastWriteTimeUtc > this.lastPriceLoadUtc)
                    {
                        var json = File.ReadAllText(ninjaPricePath);
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;

                        var tempSourcePrices = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
                        var tempSourceIcons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        float? tempDiv = null;
                        float? tempEx = null;

                        if (root.TryGetProperty("DivineInChaos", out var divProp) && divProp.TryGetSingle(out var div))
                        {
                            if (div > 0) tempDiv = div;
                        }
                        if (root.TryGetProperty("ExaltedInChaos", out var exProp) && exProp.TryGetSingle(out var ex))
                        {
                            if (ex > 0) tempEx = ex;
                        }
                        if (root.TryGetProperty("Prices", out var pricesProp) && pricesProp.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var prop in pricesProp.EnumerateObject())
                            {
                                if (prop.Value.TryGetProperty("Chaos", out var cp) && cp.TryGetSingle(out var cVal))
                                {
                                    tempSourcePrices[prop.Name] = cVal;
                                }
                                if (prop.Value.TryGetProperty("ItemIcon", out var icProp))
                                {
                                    var icStr = icProp.GetString();
                                    if (!string.IsNullOrEmpty(icStr))
                                    {
                                        tempSourceIcons[prop.Name] = icStr;
                                    }
                                }
                            }
                        }

                        // Atomic publication of parsed source data
                        this.sourcePriceCache = tempSourcePrices;
                        this.sourceIconCache = tempSourceIcons;
                        if (tempDiv.HasValue) this.DivineInChaos = tempDiv.Value;
                        if (tempEx.HasValue) this.ExaltedInChaos = tempEx.Value;
                        this.lastPriceLoadUtc = fi.LastWriteTimeUtc;
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error("myFarming", $"Error loading price cache: {ex.Message}");
            }

            // Always rebuild effective caches from latest source snapshot + defaults + custom overrides
            this.RebuildEffectiveCaches(customPrices);
        }

        private void RebuildEffectiveCaches(Dictionary<string, float>? customPrices)
        {
            var newEffectivePrices = new Dictionary<string, float>(this.sourcePriceCache, StringComparer.OrdinalIgnoreCase);

            foreach (var kvp in DefaultCurrencyPrices)
            {
                if (!newEffectivePrices.ContainsKey(kvp.Key))
                {
                    newEffectivePrices[kvp.Key] = kvp.Value;
                }
            }

            if (customPrices != null)
            {
                foreach (var kvp in customPrices)
                {
                    newEffectivePrices[kvp.Key] = kvp.Value;
                }
            }

            var newEffectiveIcons = new Dictionary<string, string>(this.sourceIconCache, StringComparer.OrdinalIgnoreCase);

            this.priceCache = new ConcurrentDictionary<string, float>(newEffectivePrices, StringComparer.OrdinalIgnoreCase);
            this.iconCache = new ConcurrentDictionary<string, string>(newEffectiveIcons, StringComparer.OrdinalIgnoreCase);
        }

        public float LookupPrice(string name, out string iconPath)
        {
            iconPath = string.Empty;
            if (string.IsNullOrWhiteSpace(name)) return 0f;

            var clean = CleanItemName(name);
            var icons = this.iconCache;
            if (icons.TryGetValue(clean, out var ic) || icons.TryGetValue(name, out ic))
            {
                iconPath = ic;
            }

            var prices = this.priceCache;
            if (prices.TryGetValue(clean, out var price) || prices.TryGetValue(name, out price))
            {
                return price;
            }

            return 0f;
        }

        public static string CleanItemName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            var trimmed = name.Trim();
            if (trimmed.StartsWith("Superior ", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed.Substring(9).TrimStart();
            return trimmed;
        }
    }
}
