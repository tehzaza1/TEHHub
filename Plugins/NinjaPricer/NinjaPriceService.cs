namespace NinjaPricer
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Net.Http;
    using System.Text.Encodings.Web;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    public record struct PriceResult(float Chaos, float Divine, float Exalt, string ItemIcon = "");

    public struct PriceStatus
    {
        public bool Loaded;
        public int TotalItems;
        public float DivineInChaos;
        public float ExaltedInChaos;
        public int CatsOk;
        public int CatsPending;
        public int CatsFailed;
        public string Message;
    }

    public sealed class NinjaPriceService : IDisposable
    {
        private static readonly HttpClient Http = new()
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        private readonly string cacheFilePath;
        private readonly ConcurrentDictionary<string, PriceResult> priceDb = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> iconDb = new(StringComparer.OrdinalIgnoreCase);

        private float divineInChaos = 1.0f;
        private float exaltedInChaos = 1.0f;
        private int catsOk = 0;
        private int catsPending = 0;
        private int catsFailed = 0;
        private string statusMessage = "Initializing";
        private bool isLoaded = false;
        private CancellationTokenSource? fetchCts;

        public NinjaPriceService(string configDirectory)
        {
            this.cacheFilePath = Path.Combine(configDirectory, "ninja_prices.json");
            this.LoadCache();
        }

        public float DivineInChaos => this.divineInChaos;
        public float ExaltedInChaos => this.exaltedInChaos;

        public PriceStatus GetStatus()
        {
            return new PriceStatus
            {
                Loaded = this.isLoaded,
                TotalItems = this.priceDb.Count,
                DivineInChaos = this.divineInChaos,
                ExaltedInChaos = this.exaltedInChaos,
                CatsOk = this.catsOk,
                CatsPending = this.catsPending,
                CatsFailed = this.catsFailed,
                Message = this.statusMessage
            };
        }

        public bool TryLookupPrice(string name, out PriceResult result)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                result = default;
                return false;
            }

            var clean = CleanItemName(name);
            if (this.priceDb.TryGetValue(clean, out result))
            {
                return true;
            }

            // Fallback: check exact name without clean
            if (this.priceDb.TryGetValue(name.Trim(), out result))
            {
                return true;
            }

            result = default;
            return false;
        }

        public static string CleanItemName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;

            // Remove stack prefix (e.g. "2x Divine Orb" -> "Divine Orb")
            var trimmed = name.Trim();
            int xIdx = trimmed.IndexOf('x');
            if (xIdx > 0 && xIdx < 4 && char.IsDigit(trimmed[0]))
            {
                trimmed = trimmed.Substring(xIdx + 1).Trim();
            }

            return trimmed;
        }

        public void TriggerRefresh(string league, int source, Dictionary<string, bool>? enabledCategories = null)
        {
            this.fetchCts?.Cancel();
            this.fetchCts = new CancellationTokenSource();
            var token = this.fetchCts.Token;

            Task.Run(async () =>
            {
                try
                {
                    this.statusMessage = "Fetching prices...";
                    this.catsOk = 0;
                    this.catsPending = 0;
                    this.catsFailed = 0;

                    if (source == 0) // poe2scout
                    {
                        await this.FetchPoe2ScoutAsync(league, enabledCategories, token);
                    }
                    else // poe.ninja
                    {
                        await this.FetchPoeNinjaAsync(league, enabledCategories, token);
                    }

                    this.isLoaded = this.priceDb.Count > 0;
                    this.statusMessage = $"Loaded {this.priceDb.Count} items";
                    this.SaveCache(league, source);
                }
                catch (OperationCanceledException)
                {
                    this.statusMessage = "Fetch canceled";
                }
                catch (Exception ex)
                {
                    this.statusMessage = $"Fetch error: {ex.Message}";
                }
            }, token);
        }

        private async Task FetchPoe2ScoutAsync(string league, Dictionary<string, bool>? enabledCategories, CancellationToken token)
        {
            string[] categories =
            {
                "currency", "ritual", "runes", "idol", "essences", "fragments", "abyss", "breach",
                "delirium", "expedition", "incursion", "ultimatum", "vaal", "vaultkeys", "verisium",
                "uncutgems", "lineagesupportgems", "weapon", "armour", "accessory", "flask", "jewel", "map", "sanctum"
            };

            foreach (var cat in categories)
            {
                if (token.IsCancellationRequested) return;
                if (enabledCategories != null && enabledCategories.TryGetValue(cat, out var enabled) && !enabled)
                {
                    continue;
                }

                this.catsPending++;
                try
                {
                    var url = $"https://poe2scout.com/api/items?league={Uri.EscapeDataString(league)}&category={cat}";
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.TryAddWithoutValidation("User-Agent", "TEHhub-NinjaPricer/1.0");

                    using var res = await Http.SendAsync(req, token);
                    if (res.IsSuccessStatusCode)
                    {
                        var json = await res.Content.ReadAsStringAsync(token);
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;

                        JsonElement itemsElem;
                        if (root.ValueKind == JsonValueKind.Array)
                        {
                            itemsElem = root;
                        }
                        else if (root.TryGetProperty("items", out var prop))
                        {
                            itemsElem = prop;
                        }
                        else
                        {
                            itemsElem = root;
                        }

                        if (itemsElem.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in itemsElem.EnumerateArray())
                            {
                                if (!item.TryGetProperty("name", out var nameProp)) continue;
                                var name = nameProp.GetString();
                                if (string.IsNullOrEmpty(name)) continue;

                                float chaos = 0f;
                                if (item.TryGetProperty("price", out var priceProp) && priceProp.TryGetSingle(out var p))
                                {
                                    chaos = p;
                                }
                                else if (item.TryGetProperty("currentPrice", out var cp) && cp.TryGetSingle(out var cVal))
                                {
                                    chaos = cVal;
                                }

                                string icon = string.Empty;
                                if (item.TryGetProperty("icon", out var iconProp))
                                {
                                    icon = iconProp.GetString() ?? string.Empty;
                                }

                                if (chaos > 0f)
                                {
                                    if (name.Equals("Divine Orb", StringComparison.OrdinalIgnoreCase))
                                    {
                                        this.divineInChaos = chaos;
                                    }
                                    else if (name.Equals("Exalted Orb", StringComparison.OrdinalIgnoreCase))
                                    {
                                        this.exaltedInChaos = chaos;
                                    }

                                    float divine = (this.divineInChaos > 0) ? (chaos / this.divineInChaos) : 0;
                                    float exalt = (this.exaltedInChaos > 0) ? (chaos / this.exaltedInChaos) : 0;
                                    this.priceDb[name] = new PriceResult(chaos, divine, exalt, icon);
                                }
                            }
                        }

                        this.catsOk++;
                    }
                    else
                    {
                        this.catsFailed++;
                    }
                }
                catch
                {
                    this.catsFailed++;
                }
                finally
                {
                    this.catsPending--;
                }
            }

            // Update all divine & exalt equivalents after rates are established
            if (this.divineInChaos > 0 || this.exaltedInChaos > 0)
            {
                foreach (var kvp in this.priceDb)
                {
                    var pr = kvp.Value;
                    float divine = (this.divineInChaos > 0) ? (pr.Chaos / this.divineInChaos) : 0;
                    float exalt = (this.exaltedInChaos > 0) ? (pr.Chaos / this.exaltedInChaos) : 0;
                    this.priceDb[kvp.Key] = new PriceResult(pr.Chaos, divine, exalt, pr.ItemIcon);
                }
            }
        }

        private async Task FetchPoeNinjaAsync(string league, Dictionary<string, bool>? enabledCategories, CancellationToken token)
        {
            string[] exchangeTypes =
            {
                "Currency", "Fragments", "UncutGems", "Essences", "SoulCores", "Idols", "Runes",
                "Expedition", "Verisium", "Ritual", "Delirium", "Breach", "Abyss", "LineageSupportGems"
            };

            string[] stashTypes =
            {
                "UniqueWeapons", "UniqueArmours", "UniqueAccessories", "UniqueFlasks",
                "UniqueCharms", "UniqueJewels", "UniqueTablets", "PrecursorTablets"
            };

            foreach (var type in exchangeTypes)
            {
                if (token.IsCancellationRequested) return;
                this.catsPending++;
                try
                {
                    var url = $"https://poe.ninja/api/data/currencyoverview?league={Uri.EscapeDataString(league)}&type={type}";
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.TryAddWithoutValidation("User-Agent", "TEHhub-NinjaPricer/1.0");

                    using var res = await Http.SendAsync(req, token);
                    if (res.IsSuccessStatusCode)
                    {
                        var json = await res.Content.ReadAsStringAsync(token);
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var line in lines.EnumerateArray())
                            {
                                string name = string.Empty;
                                if (line.TryGetProperty("currencyTypeName", out var n1)) name = n1.GetString() ?? string.Empty;
                                else if (line.TryGetProperty("detailsId", out var n2)) name = n2.GetString() ?? string.Empty;

                                if (string.IsNullOrEmpty(name)) continue;

                                float chaos = 0f;
                                if (line.TryGetProperty("chaosEquivalent", out var ce) && ce.TryGetSingle(out var val))
                                {
                                    chaos = val;
                                }

                                if (chaos > 0f)
                                {
                                    if (name.Equals("Divine Orb", StringComparison.OrdinalIgnoreCase))
                                    {
                                        this.divineInChaos = chaos;
                                    }
                                    else if (name.Equals("Exalted Orb", StringComparison.OrdinalIgnoreCase))
                                    {
                                        this.exaltedInChaos = chaos;
                                    }

                                    float divine = (this.divineInChaos > 0) ? (chaos / this.divineInChaos) : 0;
                                    float exalt = (this.exaltedInChaos > 0) ? (chaos / this.exaltedInChaos) : 0;
                                    this.priceDb[name] = new PriceResult(chaos, divine, exalt);
                                }
                            }
                        }
                        this.catsOk++;
                    }
                    else
                    {
                        this.catsFailed++;
                    }
                }
                catch
                {
                    this.catsFailed++;
                }
                finally
                {
                    this.catsPending--;
                }
            }

            foreach (var type in stashTypes)
            {
                if (token.IsCancellationRequested) return;
                this.catsPending++;
                try
                {
                    var url = $"https://poe.ninja/api/data/itemoverview?league={Uri.EscapeDataString(league)}&type={type}";
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.TryAddWithoutValidation("User-Agent", "TEHhub-NinjaPricer/1.0");

                    using var res = await Http.SendAsync(req, token);
                    if (res.IsSuccessStatusCode)
                    {
                        var json = await res.Content.ReadAsStringAsync(token);
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var line in lines.EnumerateArray())
                            {
                                if (!line.TryGetProperty("name", out var n)) continue;
                                var name = n.GetString();
                                if (string.IsNullOrEmpty(name)) continue;

                                float chaos = 0f;
                                if (line.TryGetProperty("chaosValue", out var cv) && cv.TryGetSingle(out var val))
                                {
                                    chaos = val;
                                }

                                string icon = string.Empty;
                                if (line.TryGetProperty("icon", out var ic)) icon = ic.GetString() ?? string.Empty;

                                if (chaos > 0f)
                                {
                                    float divine = (this.divineInChaos > 0) ? (chaos / this.divineInChaos) : 0;
                                    float exalt = (this.exaltedInChaos > 0) ? (chaos / this.exaltedInChaos) : 0;
                                    this.priceDb[name] = new PriceResult(chaos, divine, exalt, icon);
                                }
                            }
                        }
                        this.catsOk++;
                    }
                    else
                    {
                        this.catsFailed++;
                    }
                }
                catch
                {
                    this.catsFailed++;
                }
                finally
                {
                    this.catsPending--;
                }
            }
        }

        private void SaveCache(string league, int source)
        {
            try
            {
                var dir = Path.GetDirectoryName(this.cacheFilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var snapshot = new PriceCacheFile
                {
                    League = league,
                    Source = source,
                    DivineInChaos = this.divineInChaos,
                    ExaltedInChaos = this.exaltedInChaos,
                    SavedUtc = DateTime.UtcNow,
                    Prices = new Dictionary<string, PriceResult>(this.priceDb)
                };

                var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
                File.WriteAllText(this.cacheFilePath, json);
            }
            catch
            {
                // Ignore cache write errors
            }
        }

        private void LoadCache()
        {
            if (!File.Exists(this.cacheFilePath)) return;

            try
            {
                var json = File.ReadAllText(this.cacheFilePath);
                var snapshot = JsonSerializer.Deserialize<PriceCacheFile>(json);
                if (snapshot != null && snapshot.Prices != null && snapshot.Prices.Count > 0)
                {
                    this.divineInChaos = snapshot.DivineInChaos > 0 ? snapshot.DivineInChaos : 1.0f;
                    this.exaltedInChaos = snapshot.ExaltedInChaos > 0 ? snapshot.ExaltedInChaos : 1.0f;
                    foreach (var kvp in snapshot.Prices)
                    {
                        this.priceDb[kvp.Key] = kvp.Value;
                    }
                    this.isLoaded = true;
                    this.statusMessage = $"Loaded {this.priceDb.Count} items from cache";
                }
            }
            catch
            {
                // Fallback to empty
            }
        }

        public void Dispose()
        {
            this.fetchCts?.Cancel();
            this.fetchCts?.Dispose();
        }

        private class PriceCacheFile
        {
            public string League { get; set; } = string.Empty;
            public int Source { get; set; }
            public float DivineInChaos { get; set; }
            public float ExaltedInChaos { get; set; }
            public DateTime SavedUtc { get; set; }
            public Dictionary<string, PriceResult> Prices { get; set; } = new();
        }
    }
}
