namespace ExpeditionPathOptimizer
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

    public sealed class ExpeditionPriceService : IDisposable
    {
        private static readonly HttpClient Http = new()
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        public static readonly List<string> AvailableLeagues = new()
        {
            "Forbidden Rites",
            "HC Forbidden Rites",
            "Standard",
            "Rise of the Abyssal",
            "HC Rise of the Abyssal",
            "Runes of Aldur",
            "HC Runes of Aldur",
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

        public ExpeditionPriceService(string configDirectory)
        {
            this.cacheFilePath = Path.Combine(configDirectory, "expedition_prices.json");
            this.LoadCache();
        }

        public float DivineInChaos => this.divineInChaos;
        public float ExaltedInChaos => this.exaltedInChaos;
        public bool IsLoaded => this.isLoaded;

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

        public bool TryLookupPrice(string name, out PriceResult result) => this.TryLookupPrice(name, null, out result);

        public bool TryLookupPrice(string name, string? variant, out PriceResult result)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                result = default;
                return false;
            }

            var clean = CleanItemName(name);

            if (!string.IsNullOrEmpty(variant))
            {
                var vKey = $"{clean}:{variant.ToLowerInvariant()}";
                if (this.priceDb.TryGetValue(vKey, out result))
                {
                    return true;
                }
            }

            if (this.priceDb.TryGetValue(clean, out result))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(variant))
            {
                var vKey = $"{name.Trim()}:{variant.ToLowerInvariant()}";
                if (this.priceDb.TryGetValue(vKey, out result))
                {
                    return true;
                }
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
                    this.catsOk = 0;
                    this.catsPending = 0;
                    this.catsFailed = 0;

                    var tempDb = new ConcurrentDictionary<string, PriceResult>(StringComparer.OrdinalIgnoreCase);

                    if (source == 0)
                    {
                        this.statusMessage = "Fetching PoE2 prices from poe2scout...";
                        await this.FetchPoe2ScoutAsync(league, enabledCategories, tempDb, token).ConfigureAwait(false);
                    }
                    else
                    {
                        this.statusMessage = "Fetching PoE2 prices from poe.ninja...";
                        await this.FetchPoeNinjaAsync(league, enabledCategories, tempDb, token).ConfigureAwait(false);
                    }

                    if (tempDb.Count > 0)
                    {
                        this.priceDb.Clear();
                        foreach (var kvp in tempDb)
                        {
                            this.priceDb[kvp.Key] = kvp.Value;
                        }
                    }

                    this.isLoaded = this.priceDb.Count > 0;
                    var srcName = source == 0 ? "poe2scout" : "poe.ninja";
                    this.statusMessage = $"Loaded {this.priceDb.Count} items via {srcName} (1D={this.divineInChaos:F1}c, 1E={this.exaltedInChaos:F2}c)";
                    PluginLog.Info("ExpeditionPathOptimizer", $"[ExpeditionPathOptimizer] Successfully loaded {this.priceDb.Count} PoE2 prices via {srcName} for league '{league}'");
                    this.SaveCache(league, source);
                }
                catch (OperationCanceledException)
                {
                    this.statusMessage = "Fetch canceled";
                }
                catch (Exception ex)
                {
                    this.statusMessage = $"Fetch error: {ex.Message}";
                    PluginLog.Error("ExpeditionPathOptimizer", $"[ExpeditionPathOptimizer] Price fetch failed: {ex}");
                }
            }, token);
        }

        private static bool IsCategoryEnabled(Dictionary<string, bool>? enabledCategories, string category)
        {
            if (enabledCategories == null) return true;
            if (enabledCategories.TryGetValue(category.ToLowerInvariant(), out var enabled))
            {
                return enabled;
            }
            return true;
        }

        private static string MapNinjaStashCategory(string type) => type switch
        {
            "UniqueWeapons" => "weapon",
            "UniqueArmours" => "armour",
            "UniqueAccessories" => "accessory",
            "UniqueFlasks" => "flask",
            "UniqueCharms" => "accessory",
            "UniqueJewels" => "jewel",
            "UniqueTablets" => "map",
            "PrecursorTablets" => "map",
            _ => type.ToLowerInvariant()
        };

        private async Task FetchPoeNinjaAsync(
            string league,
            Dictionary<string, bool>? enabledCategories,
            ConcurrentDictionary<string, PriceResult> targetDb,
            CancellationToken token)
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

                var catKey = type.ToLowerInvariant();
                bool isEnabled = IsCategoryEnabled(enabledCategories, catKey);

                if (!isEnabled && !catKey.Equals("currency", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                this.catsPending++;
                try
                {
                    var url = $"https://poe.ninja/api/data/itemexchangeoverview?league={leagueParam}&type={type}";
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.TryAddWithoutValidation("User-Agent", "TEHhub-ExpeditionPathOptimizer/1.0");

                    using var res = await Http.SendAsync(req, token).ConfigureAwait(false);
                    if (res.IsSuccessStatusCode)
                    {
                        var json = await res.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;

                        if (type.Equals("Currency", StringComparison.OrdinalIgnoreCase) && root.TryGetProperty("lines", out var curLines) && curLines.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var line in curLines.EnumerateArray())
                            {
                                if (line.TryGetProperty("detailsId", out var dId) && dId.GetString() == "divine-orb")
                                {
                                    if (line.TryGetProperty("chaosEquivalent", out var ce) && ce.TryGetSingle(out var ceVal) && ceVal > 0)
                                    {
                                        this.divineInChaos = ceVal;
                                    }
                                }
                                if (line.TryGetProperty("detailsId", out var eId) && eId.GetString() == "exalted-orb")
                                {
                                    if (line.TryGetProperty("chaosEquivalent", out var ee) && ee.TryGetSingle(out var eeVal) && eeVal > 0)
                                    {
                                        this.exaltedInChaos = eeVal;
                                    }
                                }
                            }
                        }

                        if (this.divineInChaos <= 0) this.divineInChaos = 9.43f;
                        if (this.exaltedInChaos <= 0) this.exaltedInChaos = 1.0f;

                        targetDb["Divine Orb"] = new PriceResult(this.divineInChaos, 1.0f, this.divineInChaos / this.exaltedInChaos);
                        targetDb["Exalted Orb"] = new PriceResult(this.exaltedInChaos, this.exaltedInChaos / this.divineInChaos, 1.0f);
                        targetDb["Chaos Orb"] = new PriceResult(1.0f, 1.0f / this.divineInChaos, 1.0f / this.exaltedInChaos);

                        if (isEnabled)
                        {
                            var itemDict = new Dictionary<string, (string Name, string Icon)>(StringComparer.OrdinalIgnoreCase);
                            if (root.TryGetProperty("items", out var itemsElem) && itemsElem.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var item in itemsElem.EnumerateArray())
                                {
                                    string id = string.Empty;
                                    string name = string.Empty;
                                    string icon = string.Empty;
                                    if (item.TryGetProperty("id", out var idProp)) id = idProp.GetString() ?? string.Empty;
                                    if (item.TryGetProperty("name", out var nProp)) name = nProp.GetString() ?? string.Empty;
                                    if (item.TryGetProperty("icon", out var iProp)) icon = iProp.GetString() ?? string.Empty;

                                    if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name))
                                    {
                                        itemDict[id] = (name, icon);
                                    }
                                }
                            }

                            if (root.TryGetProperty("lines", out var linesElem) && linesElem.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var line in linesElem.EnumerateArray())
                                {
                                    string id = string.Empty;
                                    if (line.TryGetProperty("detailsId", out var dProp)) id = dProp.GetString() ?? string.Empty;
                                    else if (line.TryGetProperty("id", out var idProp)) id = idProp.GetString() ?? string.Empty;

                                    float chaosVal = 0f;
                                    if (line.TryGetProperty("chaosEquivalent", out var ceProp) && ceProp.TryGetSingle(out var cv))
                                    {
                                        chaosVal = cv;
                                    }
                                    else if (line.TryGetProperty("primaryValue", out var pvProp) && pvProp.TryGetSingle(out var pv))
                                    {
                                        chaosVal = pv;
                                    }

                                    if (chaosVal <= 0f) continue;

                                    if (itemDict.TryGetValue(id, out var info))
                                    {
                                        float divine = this.divineInChaos > 0 ? (chaosVal / this.divineInChaos) : 0;
                                        float exalt = this.exaltedInChaos > 0 ? (chaosVal / this.exaltedInChaos) : 0;
                                        targetDb[info.Name] = new PriceResult(chaosVal, divine, exalt, info.Icon);
                                    }
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

            // 2. Process stash types (uniques)
            foreach (var type in stashTypes)
            {
                if (token.IsCancellationRequested) return;

                var catKey = MapNinjaStashCategory(type);
                if (!IsCategoryEnabled(enabledCategories, catKey)) continue;

                this.catsPending++;
                try
                {
                    var url = $"https://poe.ninja/api/data/itemoverview?league={leagueParam}&type={type}";
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.TryAddWithoutValidation("User-Agent", "TEHhub-ExpeditionPathOptimizer/1.0");

                    using var res = await Http.SendAsync(req, token).ConfigureAwait(false);
                    if (res.IsSuccessStatusCode)
                    {
                        var json = await res.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;

                        if (root.TryGetProperty("lines", out var linesElem) && linesElem.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var line in linesElem.EnumerateArray())
                            {
                                string name = string.Empty;
                                if (line.TryGetProperty("name", out var nProp)) name = nProp.GetString() ?? string.Empty;
                                if (string.IsNullOrEmpty(name)) continue;

                                float chaosVal = 0f;
                                if (line.TryGetProperty("chaosValue", out var cvProp) && cvProp.TryGetSingle(out var cv))
                                {
                                    chaosVal = cv;
                                }
                                else if (line.TryGetProperty("price", out var pProp) && pProp.TryGetSingle(out var pv))
                                {
                                    chaosVal = pv;
                                }

                                if (chaosVal <= 0f) continue;

                                string icon = string.Empty;
                                if (line.TryGetProperty("icon", out var iProp)) icon = iProp.GetString() ?? string.Empty;

                                float divine = this.divineInChaos > 0 ? (chaosVal / this.divineInChaos) : 0;
                                float exalt = this.exaltedInChaos > 0 ? (chaosVal / this.exaltedInChaos) : 0;
                                var pr = new PriceResult(chaosVal, divine, exalt, icon);

                                targetDb[name] = pr;

                                if (line.TryGetProperty("baseType", out var btProp) && btProp.GetString() is { } bt && !string.IsNullOrEmpty(bt))
                                {
                                    targetDb[$"{name} ({bt})"] = pr;
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

        private async Task FetchPoe2ScoutAsync(
            string league,
            Dictionary<string, bool>? enabledCategories,
            ConcurrentDictionary<string, PriceResult> targetDb,
            CancellationToken token)
        {
            var leagueEscaped = Uri.EscapeDataString(league);

            // 1. Fetch rates
            try
            {
                var leaguesUrl = "https://api.poe2scout.com/poe2/Leagues";
                using var lReq = new HttpRequestMessage(HttpMethod.Get, leaguesUrl);
                lReq.Headers.TryAddWithoutValidation("User-Agent", "TEHhub-ExpeditionPathOptimizer/1.0");

                using var lRes = await Http.SendAsync(lReq, token).ConfigureAwait(false);
                if (lRes.IsSuccessStatusCode)
                {
                    var lJson = await lRes.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                    using var lDoc = JsonDocument.Parse(lJson);
                    var root = lDoc.RootElement;
                    var array = root.ValueKind == JsonValueKind.Array ? root : (root.TryGetProperty("value", out var v) ? v : default);
                    if (array.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var elem in array.EnumerateArray())
                        {
                            if (elem.TryGetProperty("Value", out var vProp) && string.Equals(vProp.GetString(), league, StringComparison.OrdinalIgnoreCase))
                            {
                                if (elem.TryGetProperty("ChaosDivinePrice", out var cdProp) && cdProp.TryGetSingle(out var cd) && cd > 0)
                                {
                                    this.divineInChaos = cd;
                                }
                                if (elem.TryGetProperty("DivinePrice", out var dpProp) && dpProp.TryGetSingle(out var dp) && dp > 0 && this.divineInChaos > 0)
                                {
                                    this.exaltedInChaos = this.divineInChaos / dp;
                                }
                                break;
                            }
                        }
                    }
                }
            }
            catch
            {
                // Fallback to existing rates
            }

            if (this.divineInChaos <= 0) this.divineInChaos = 9.43f;
            if (this.exaltedInChaos <= 0) this.exaltedInChaos = 1.0f;

            targetDb["Divine Orb"] = new PriceResult(this.divineInChaos, 1.0f, this.divineInChaos / this.exaltedInChaos);
            targetDb["Exalted Orb"] = new PriceResult(this.exaltedInChaos, this.exaltedInChaos / this.divineInChaos, 1.0f);
            targetDb["Chaos Orb"] = new PriceResult(1.0f, 1.0f / this.divineInChaos, 1.0f / this.exaltedInChaos);

            string[] scoutCurrencyCategories =
            {
                "currency", "ritual", "runes", "idol", "essences", "fragments", "abyss", "breach",
                "delirium", "expedition", "incursion", "ultimatum", "vaal", "vaultkeys", "verisium",
                "uncutgems", "lineagesupportgems"
            };

            string[] scoutUniqueCategories =
            {
                "weapon", "armour", "accessory", "flask", "jewel", "map", "sanctum"
            };

            // 2. Fetch Currencies
            foreach (var category in scoutCurrencyCategories)
            {
                if (token.IsCancellationRequested) return;
                if (!IsCategoryEnabled(enabledCategories, category)) continue;

                this.catsPending++;
                try
                {
                    int page = 1;
                    int totalPages = 1;
                    while (page <= totalPages && !token.IsCancellationRequested)
                    {
                        var url = $"https://api.poe2scout.com/poe2/Leagues/{leagueEscaped}/Currencies/ByCategory?Category={category}&ReferenceCurrency=chaos&PerPage=250&Page={page}";
                        using var req = new HttpRequestMessage(HttpMethod.Get, url);
                        req.Headers.TryAddWithoutValidation("User-Agent", "TEHhub-ExpeditionPathOptimizer/1.0");

                        using var res = await Http.SendAsync(req, token).ConfigureAwait(false);
                        if (!res.IsSuccessStatusCode) break;

                        var json = await res.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("Pages", out var pProp) && pProp.TryGetInt32(out var pVal) && pVal > 0)
                        {
                            totalPages = pVal;
                        }

                        if (root.TryGetProperty("Items", out var itemsElem) && itemsElem.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in itemsElem.EnumerateArray())
                            {
                                float currentPrice = 0f;
                                if (item.TryGetProperty("CurrentPrice", out var cpProp) && cpProp.TryGetSingle(out var cp))
                                {
                                    currentPrice = cp;
                                }
                                if (currentPrice <= 0f) continue;

                                string text = string.Empty;
                                if (item.TryGetProperty("Text", out var tProp)) text = tProp.GetString() ?? string.Empty;
                                string iconUrl = string.Empty;
                                if (item.TryGetProperty("IconUrl", out var icProp)) iconUrl = icProp.GetString() ?? string.Empty;

                                float chaos = currentPrice;
                                float divine = this.divineInChaos > 0 ? (chaos / this.divineInChaos) : 0;
                                float exalt = this.exaltedInChaos > 0 ? (chaos / this.exaltedInChaos) : 0;
                                var priceRes = new PriceResult(chaos, divine, exalt, iconUrl);

                                if (!string.IsNullOrEmpty(text)) targetDb[text] = priceRes;
                                if (item.TryGetProperty("ItemMetadata", out var meta))
                                {
                                    if (meta.TryGetProperty("name", out var mn) && mn.GetString() is { } mName && !string.IsNullOrEmpty(mName))
                                        targetDb[mName] = priceRes;
                                    if (meta.TryGetProperty("base_type", out var bt) && bt.GetString() is { } bType && !string.IsNullOrEmpty(bType))
                                        targetDb[bType] = priceRes;
                                }
                            }
                        }
                        page++;
                    }
                    this.catsOk++;
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

            // 3. Fetch Uniques
            foreach (var category in scoutUniqueCategories)
            {
                if (token.IsCancellationRequested) return;
                if (!IsCategoryEnabled(enabledCategories, category)) continue;

                this.catsPending++;
                try
                {
                    int page = 1;
                    int totalPages = 1;
                    while (page <= totalPages && !token.IsCancellationRequested)
                    {
                        var url = $"https://api.poe2scout.com/poe2/Leagues/{leagueEscaped}/Uniques/ByCategory?Category={category}&ReferenceCurrency=chaos&PerPage=250&Page={page}";
                        using var req = new HttpRequestMessage(HttpMethod.Get, url);
                        req.Headers.TryAddWithoutValidation("User-Agent", "TEHhub-ExpeditionPathOptimizer/1.0");

                        using var res = await Http.SendAsync(req, token).ConfigureAwait(false);
                        if (!res.IsSuccessStatusCode) break;

                        var json = await res.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("Pages", out var pProp) && pProp.TryGetInt32(out var pVal) && pVal > 0)
                        {
                            totalPages = pVal;
                        }

                        if (root.TryGetProperty("Items", out var itemsElem) && itemsElem.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in itemsElem.EnumerateArray())
                            {
                                float currentPrice = 0f;
                                if (item.TryGetProperty("CurrentPrice", out var cpProp) && cpProp.TryGetSingle(out var cp))
                                {
                                    currentPrice = cp;
                                }
                                if (currentPrice <= 0f) continue;

                                string name = string.Empty;
                                if (item.TryGetProperty("Name", out var nProp)) name = nProp.GetString() ?? string.Empty;
                                string iconUrl = string.Empty;
                                if (item.TryGetProperty("IconUrl", out var icProp)) iconUrl = icProp.GetString() ?? string.Empty;

                                float chaos = currentPrice;
                                float divine = this.divineInChaos > 0 ? (chaos / this.divineInChaos) : 0;
                                float exalt = this.exaltedInChaos > 0 ? (chaos / this.exaltedInChaos) : 0;
                                var priceRes = new PriceResult(chaos, divine, exalt, iconUrl);

                                if (!string.IsNullOrEmpty(name)) targetDb[name] = priceRes;
                            }
                        }
                        page++;
                    }
                    this.catsOk++;
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
