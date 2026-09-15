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
        private readonly ConcurrentDictionary<string, float> priceCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> iconCache = new(StringComparer.OrdinalIgnoreCase);
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
                        if (root.TryGetProperty("DivineInChaos", out var divProp) && divProp.TryGetSingle(out var div))
                        {
                            if (div > 0) this.DivineInChaos = div;
                        }
                        if (root.TryGetProperty("ExaltedInChaos", out var exProp) && exProp.TryGetSingle(out var ex))
                        {
                            if (ex > 0) this.ExaltedInChaos = ex;
                        }
                        if (root.TryGetProperty("Prices", out var pricesProp) && pricesProp.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var prop in pricesProp.EnumerateObject())
                            {
                                if (prop.Value.TryGetProperty("Chaos", out var cp) && cp.TryGetSingle(out var cVal))
                                {
                                    this.priceCache[prop.Name] = cVal;
                                }
                                if (prop.Value.TryGetProperty("ItemIcon", out var icProp))
                                {
                                    var icStr = icProp.GetString();
                                    if (!string.IsNullOrEmpty(icStr)) this.iconCache[prop.Name] = icStr;
                                }
                            }
                        }
                        this.lastPriceLoadUtc = fi.LastWriteTimeUtc;
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error("myFarming", $"Error loading price cache: {ex.Message}");
            }

            // Apply defaults for any missing core currency
            foreach (var kvp in DefaultCurrencyPrices)
            {
                if (!this.priceCache.ContainsKey(kvp.Key))
                {
                    this.priceCache[kvp.Key] = kvp.Value;
                }
            }

            // Apply user custom prices
            if (customPrices != null)
            {
                foreach (var kvp in customPrices)
                {
                    this.priceCache[kvp.Key] = kvp.Value;
                }
            }
        }

        public float LookupPrice(string name, out string iconPath)
        {
            iconPath = string.Empty;
            if (string.IsNullOrWhiteSpace(name)) return 0f;

            var clean = CleanItemName(name);
            if (this.iconCache.TryGetValue(clean, out var ic) || this.iconCache.TryGetValue(name, out ic))
            {
                iconPath = ic;
            }

            if (this.priceCache.TryGetValue(clean, out var price) || this.priceCache.TryGetValue(name, out price))
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
