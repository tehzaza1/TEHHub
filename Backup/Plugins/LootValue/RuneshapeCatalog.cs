namespace LootValue
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;

    /// <summary>
    /// Represents one recipe offer available from a Runeshape Monolith encounter.
    /// </summary>
    public sealed class RecipeOffer
    {
        public string Name { get; set; } = string.Empty;
        public int Count { get; set; } = 1;
        public int Size { get; set; }
        public string RunesSummary { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public double DisplayPrice { get; set; }
        public string CurrencySymbol { get; set; } = "c";
        public double ChaosValue { get; set; }
    }

    /// <summary>
    /// Catalog of all 322 PoE 2 Runeshape recipes and logic for determining offered recipes.
    /// </summary>
    public sealed class RuneshapeCatalog
    {
        private static readonly object Gate = new();
        private static RuneshapeCatalog? instance;

        private readonly List<RecipeDef> recipes;
        private readonly Dictionary<int, string> runeNames;
        private readonly Dictionary<long, int> partialMinLevel;

        private RuneshapeCatalog(List<RecipeDef> recipes, Dictionary<int, string> runeNames, Dictionary<long, int> partial)
        {
            this.recipes = recipes;
            this.runeNames = runeNames;
            this.partialMinLevel = partial;
        }

        public static RuneshapeCatalog Instance
        {
            get
            {
                if (instance == null)
                {
                    lock (Gate)
                    {
                        instance ??= Load();
                    }
                }

                return instance;
            }
        }

        public static void Reload(string pluginDirectory)
        {
            lock (Gate)
            {
                instance = Load(pluginDirectory);
            }
        }

        public bool IsLoaded => this.recipes.Count > 0;

        public string GetRuneName(int idx) => this.runeNames.TryGetValue(idx, out var n) ? n : $"#{idx}";

        /// <summary>
        /// Calculates all recipe offers that a monolith will present.
        /// </summary>
        public List<RecipeOffer> GetOffers(int anchorIdx, int anchorPos, int holeCount, bool isUnique, int areaLevel, int displayCurrency = 0)
        {
            var result = new List<RecipeOffer>();
            if (holeCount <= 0 || this.recipes.Count == 0)
            {
                return result;
            }

            foreach (var rec in this.recipes)
            {
                if (rec.size > holeCount)
                {
                    continue;
                }

                if (areaLevel > 0 && rec.maxLevel > 0 && (areaLevel < rec.minLevel || areaLevel > rec.maxLevel))
                {
                    continue;
                }

                if (isUnique || anchorIdx < 0)
                {
                    // Unique / anchor-less monolith: all recipes with size <= holeCount are offered.
                    result.Add(this.BuildOffer(rec, displayCurrency));
                    continue;
                }

                if (rec.runeIdx == null || rec.runeIdx.Count <= anchorPos)
                {
                    continue;
                }

                if (rec.runeIdx[anchorPos] != anchorIdx)
                {
                    continue;
                }

                // size == N always offered; size < N only when runeWeights permits the partial at this area level.
                if (rec.size != holeCount && !this.IsPartialAllowed(anchorIdx, anchorPos, rec.size, areaLevel))
                {
                    continue;
                }

                result.Add(this.BuildOffer(rec, displayCurrency));
            }

            return result;
        }

        /// <summary>
        /// Finds the highest priced offer among all available recipes for this monolith.
        /// </summary>
        public RecipeOffer? GetBestOffer(int anchorIdx, int anchorPos, int holeCount, bool isUnique, int areaLevel, int displayCurrency = 0)
        {
            var offers = this.GetOffers(anchorIdx, anchorPos, holeCount, isUnique, areaLevel, displayCurrency);
            if (offers.Count == 0)
            {
                return null;
            }

            // Sort by chaos value descending so highest value is first
            offers.Sort((a, b) => b.ChaosValue.CompareTo(a.ChaosValue));
            return offers[0];
        }

        private RecipeOffer BuildOffer(RecipeDef rec, int displayCurrency)
        {
            var rawName = rec.reward?.name ?? string.Empty;
            var desc = rec.description ?? string.Empty;
            var itemName = !string.IsNullOrEmpty(rawName) ? rawName : desc;
            var count = Math.Max(1, rec.rewardCount);
            var runesStr = rec.runes != null ? string.Join(" · ", rec.runes) : string.Empty;

            var offer = new RecipeOffer
            {
                Name = itemName,
                Count = count,
                Size = rec.size,
                RunesSummary = runesStr,
                Description = desc,
                DisplayPrice = 0,
                CurrencySymbol = "c",
                ChaosValue = 0,
            };

            if (!string.IsNullOrWhiteSpace(rawName))
            {
                var price = PoeNinjaPriceFetcher.GetPrice(rawName);
                if (price != null && price.PriceChaos > 0)
                {
                    var totalChaos = price.PriceChaos * count;
                    offer.ChaosValue = totalChaos;

                    var (dispVal, dispCur) = PoeNinjaPriceFetcher.GetDisplayPrice(totalChaos, displayCurrency);
                    offer.DisplayPrice = dispVal;
                    offer.CurrencySymbol = dispCur;
                }
            }

            return offer;
        }

        private bool IsPartialAllowed(int idx, int pos, int size, int areaLevel)
        {
            if (!this.partialMinLevel.TryGetValue(PartialKey(idx, pos + 1, size), out var minL))
            {
                return false;
            }

            return areaLevel <= 0 || areaLevel >= minL;
        }

        private static long PartialKey(int rune, int pos1Based, int size)
            => ((long)rune << 16) | ((long)pos1Based << 8) | (uint)size;

        private static RuneshapeCatalog Load(string? overrideDir = null)
        {
            try
            {
                var candidates = new List<string>();
                if (!string.IsNullOrEmpty(overrideDir))
                {
                    candidates.Add(Path.Combine(overrideDir, "expedition2_recipes.json"));
                }

                candidates.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins", "LootValue", "expedition2_recipes.json"));
                candidates.Add(Path.Combine("Plugins", "LootValue", "expedition2_recipes.json"));
                candidates.Add(@"C:\Games\Hy-v Tool\DXPEOE\trade\resources\runeshape\expedition2_recipes.json");

                string? foundPath = null;
                foreach (var c in candidates)
                {
                    if (File.Exists(c))
                    {
                        foundPath = c;
                        break;
                    }
                }

                if (foundPath == null)
                {
                    Console.WriteLine("[RuneshapeCatalog] expedition2_recipes.json not found in candidates.");
                    return Empty();
                }

                var json = File.ReadAllText(foundPath);
                var file = JsonSerializer.Deserialize<CatalogFile>(json);
                if (file?.recipes == null)
                {
                    return Empty();
                }

                var runeNames = new Dictionary<int, string>();
                if (file.runes != null)
                {
                    foreach (var kv in file.runes)
                    {
                        if (int.TryParse(kv.Key, out var k) && kv.Value != null)
                        {
                            runeNames[k] = kv.Value;
                        }
                    }
                }

                var partial = new Dictionary<long, int>();
                if (file.runeWeights != null)
                {
                    foreach (var w in file.runeWeights)
                    {
                        var key = PartialKey(w.rune, w.pos, w.size);
                        if (!partial.TryGetValue(key, out var cur) || w.minLevel < cur)
                        {
                            partial[key] = w.minLevel;
                        }
                    }
                }

                Console.WriteLine($"[RuneshapeCatalog] Loaded {file.recipes.Count} recipes and {runeNames.Count} runes from {foundPath}");
                return new RuneshapeCatalog(file.recipes, runeNames, partial);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RuneshapeCatalog] Load failed: {ex.Message}");
                return Empty();
            }
        }

        private static RuneshapeCatalog Empty()
            => new(new List<RecipeDef>(), new Dictionary<int, string>(), new Dictionary<long, int>());

        private sealed class CatalogFile
        {
            public Dictionary<string, string>? runes { get; set; }
            public List<RecipeDef>? recipes { get; set; }
            public List<RuneWeightDef>? runeWeights { get; set; }
        }

        private sealed class RuneWeightDef
        {
            public int rune { get; set; }
            public int pos { get; set; }
            public int size { get; set; }
            public int minLevel { get; set; }
        }

        private sealed class RecipeDef
        {
            public int size { get; set; }
            public List<int>? runeIdx { get; set; }
            public List<string>? runes { get; set; }
            public RewardDef? reward { get; set; }
            public int rewardCount { get; set; }
            public string? description { get; set; }
            public int minLevel { get; set; }
            public int maxLevel { get; set; }
        }

        private sealed class RewardDef
        {
            public int idx { get; set; }
            public string? id { get; set; }
            public string? name { get; set; }
        }
    }
}
