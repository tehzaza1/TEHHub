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
    using TEHhub.Plugin;

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

        private float divineInChaos = 9.43f;
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
                    this.statusMessage = "Fetching PoE2 prices...";
                    this.catsOk = 0;
                    this.catsPending = 0;
                    this.catsFailed = 0;

                    // Always fetch from poe.ninja (reliable official PoE2 endpoints)
                    await this.FetchPoeNinjaAsync(league, enabledCategories, token).ConfigureAwait(false);

                    this.isLoaded = this.priceDb.Count > 0;
                    this.statusMessage = $"Loaded {this.priceDb.Count} items (1D={this.divineInChaos:F1}c, 1E={this.exaltedInChaos:F2}c)";
                    PluginLog.Info("NinjaPricer", $"[NinjaPricer] Successfully loaded {this.priceDb.Count} PoE2 prices for league '{league}'");
                    this.SaveCache(league, source);
                }
                catch (OperationCanceledException)
                {
                    this.statusMessage = "Fetch canceled";
                }
                catch (Exception ex)
                {
                    this.statusMessage = $"Fetch error: {ex.Message}";
                    PluginLog.Error("NinjaPricer", $"[NinjaPricer] Price fetch failed: {ex}");
                }
            }, token);
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

            var leagueParam = Uri.EscapeDataString(league).Replace("%20", "+");

            // 1. Process exchange types (rates + exchange items)
            foreach (var type in exchangeTypes)
            {
                if (token.IsCancellationRequested) return;
                this.catsPending++;
                try
                {
                    var url = $"https://poe.ninja/poe2/api/economy/exchange/current/overview?league={leagueParam}&type={type}";
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.TryAddWithoutValidation("User-Agent", "TEHhub-NinjaPricer/1.0");

                    using var res = await Http.SendAsync(req, token).ConfigureAwait(false);
                    if (res.IsSuccessStatusCode)
                    {
                        var json = await res.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;

                        // Parse rates
                        if (root.TryGetProperty("core", out var core))
                        {
                            if (core.TryGetProperty("rates", out var rates))
                            {
                                if (rates.TryGetProperty("chaos", out var cProp) && cProp.TryGetSingle(out var cRate) && cRate > 0)
                                {
                                    this.divineInChaos = cRate;
                                }
                                if (rates.TryGetProperty("exalted", out var exProp) && exProp.TryGetSingle(out var exRate) && exRate > 0)
                                {
                                    this.exaltedInChaos = this.divineInChaos / exRate;
                                }
                            }
                            if (core.TryGetProperty("items", out var coreItems) && coreItems.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var ci in coreItems.EnumerateArray())
                                {
                                    if (ci.TryGetProperty("name", out var n) && ci.TryGetProperty("image", out var img))
                                    {
                                        var cName = n.GetString() ?? string.Empty;
                                        var cImg = img.GetString() ?? string.Empty;
                                        if (!string.IsNullOrEmpty(cName))
                                        {
                                            if (cName.Equals("Divine Orb", StringComparison.OrdinalIgnoreCase))
                                                this.priceDb[cName] = new PriceResult(this.divineInChaos, 1.0f, this.divineInChaos / (this.exaltedInChaos > 0 ? this.exaltedInChaos : 1.0f), cImg);
                                            else if (cName.Equals("Exalted Orb", StringComparison.OrdinalIgnoreCase))
                                                this.priceDb[cName] = new PriceResult(this.exaltedInChaos, this.exaltedInChaos / (this.divineInChaos > 0 ? this.divineInChaos : 1.0f), 1.0f, cImg);
                                            else if (cName.Equals("Chaos Orb", StringComparison.OrdinalIgnoreCase))
                                                this.priceDb[cName] = new PriceResult(1.0f, 1.0f / (this.divineInChaos > 0 ? this.divineInChaos : 1.0f), 1.0f / (this.exaltedInChaos > 0 ? this.exaltedInChaos : 1.0f), cImg);
                                        }
                                    }
                                }
                            }
                        }

                        // Map item id -> (name, image)
                        var itemMap = new Dictionary<string, (string Name, string Image)>(StringComparer.OrdinalIgnoreCase);
                        if (root.TryGetProperty("items", out var itemsElem) && itemsElem.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in itemsElem.EnumerateArray())
                            {
                                if (item.TryGetProperty("id", out var idProp) && item.TryGetProperty("name", out var nameProp))
                                {
                                    var id = idProp.GetString();
                                    var name = nameProp.GetString();
                                    string image = string.Empty;
                                    if (item.TryGetProperty("image", out var imgProp)) image = imgProp.GetString() ?? string.Empty;
                                    if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name))
                                    {
                                        itemMap[id] = (name, image);
                                    }
                                }
                            }
                        }

                        // Map lines -> prices (primaryValue is in Divine)
                        if (root.TryGetProperty("lines", out var linesElem) && linesElem.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var line in linesElem.EnumerateArray())
                            {
                                if (!line.TryGetProperty("id", out var idProp)) continue;
                                var id = idProp.GetString();
                                if (string.IsNullOrEmpty(id) || !itemMap.TryGetValue(id, out var info)) continue;

                                float primaryVal = 0f;
                                if (line.TryGetProperty("primaryValue", out var pvProp) && pvProp.TryGetSingle(out var pv))
                                {
                                    primaryVal = pv;
                                }

                                if (primaryVal > 0f)
                                {
                                    float divine = primaryVal;
                                    float chaos = divine * this.divineInChaos;
                                    float exalt = (this.exaltedInChaos > 0) ? (chaos / this.exaltedInChaos) : 0;
                                    this.priceDb[info.Name] = new PriceResult(chaos, divine, exalt, info.Image);
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

            // 2. Process stash types (unique items)
            foreach (var type in stashTypes)
            {
                if (token.IsCancellationRequested) return;
                this.catsPending++;
                try
                {
                    var url = $"https://poe.ninja/poe2/api/economy/stash/current/item/overview?league={leagueParam}&type={type}";
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.TryAddWithoutValidation("User-Agent", "TEHhub-NinjaPricer/1.0");

                    using var res = await Http.SendAsync(req, token).ConfigureAwait(false);
                    if (res.IsSuccessStatusCode)
                    {
                        var json = await res.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var line in lines.EnumerateArray())
                            {
                                if (!line.TryGetProperty("name", out var n)) continue;
                                var name = n.GetString();
                                if (string.IsNullOrEmpty(name)) continue;

                                float primaryVal = 0f;
                                if (line.TryGetProperty("primaryValue", out var pv) && pv.TryGetSingle(out var val))
                                {
                                    primaryVal = val;
                                }

                                string icon = string.Empty;
                                if (line.TryGetProperty("icon", out var ic)) icon = ic.GetString() ?? string.Empty;

                                if (primaryVal > 0f)
                                {
                                    float divine = primaryVal;
                                    float chaos = divine * this.divineInChaos;
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
                    this.divineInChaos = snapshot.DivineInChaos > 0 ? snapshot.DivineInChaos : 9.43f;
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
