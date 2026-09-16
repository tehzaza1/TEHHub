namespace ExpeditionPathOptimizer
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using TEHhub.Plugin;

    public class RuneshapeRecipe
    {
        public int Row { get; set; }
        public string Id { get; set; } = string.Empty;
        public int Size { get; set; }
        public List<int> RuneIdx { get; set; } = new();
        public List<string> Runes { get; set; } = new();
        public string Reward { get; set; } = string.Empty;
        public int RewardCount { get; set; } = 1;
        public string Description { get; set; } = string.Empty;
        public int Category { get; set; }
        public int MinLevel { get; set; }
        public int MaxLevel { get; set; }
        public int ComboWeight { get; set; }
    }

    public class RuneshapeRecipeOffer
    {
        public string RecipeId { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Reward { get; set; } = string.Empty;
        public int RewardCount { get; set; } = 1;
        public IReadOnlyList<string> Runes { get; set; } = Array.Empty<string>();
        public IReadOnlyList<int> RuneIdx { get; set; } = Array.Empty<int>();
        public IReadOnlyList<string> PropagatedRunes { get; set; } = Array.Empty<string>();
        public float PriceChaos { get; set; }
        public float PriceDivine { get; set; }
        public float PriceExalt { get; set; }
        public bool IsPriced { get; set; }
        public int ComboWeight { get; set; }
        public int Size { get; set; }
        public int Category { get; set; }
    }

    public sealed class RuneshapeRecipePredictor
    {
        public static readonly string[] RuneNames = new string[]
        {
            "Fire", "Cold", "Lightning", "Tempest", "Momentum", "Bloodletting",
            "Stone", "Adaptive", "Arcane", "Toxic", "Electrocuting", "Protective",
            "Cyclonic", "Vision", "Tidal", "Rebirth", "Prismatic", "Gasp",
            "Moon", "Celestial", "Opulent", "Rage", "Wisdom", "Sky",
            "Earth", "Life", "Bond", "Ward", "Soul", "Death",
            "Oath", "Time", "Power", "Bait"
        };

        /// <summary>
        /// Game data definition of Runes that are eligible to propagate modifiers via GoldenSlots.
        /// Common / white / Tier-B Runes (e.g. Fire, Cold, Lightning, Stone, Toxic, etc.) cannot propagate.
        /// </summary>
        public static readonly HashSet<string> PropagatingRuneNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Opulent",
            "Power",
            "Bond",
            "Oath",
            "Death",
            "Time",
            "Rebirth",
            "Soul",
            "Celestial",
            "Vision",
            "Wisdom",
            "Rage",
            "Arcane",
            "Prismatic",
            "Earth",
            "Sky",
            "Life",
            "Ward",
        };

        public static bool CanPropagateRune(int runeIdx)
        {
            if (runeIdx < 0 || runeIdx >= RuneNames.Length) return false;
            return CanPropagateRune(RuneNames[runeIdx]);
        }

        public static bool CanPropagateRune(string? runeName)
        {
            if (string.IsNullOrEmpty(runeName)) return false;
            return PropagatingRuneNames.Contains(runeName);
        }

        private static readonly Dictionary<string, int> DefaultRuneWeights = new(StringComparer.OrdinalIgnoreCase)
        {
            { "Opulent", 500 },
            { "Bond", 120 },
            { "Oath", 110 },
            { "Power", 100 },
            { "Death", 100 },
            { "Time", 80 },
            { "Rebirth", 60 },
            { "Prismatic", 30 },
            { "Arcane", 30 },
            { "Soul", 25 },
            { "Celestial", 25 },
            { "Vision", 25 },
            { "Wisdom", 25 },
            { "Rage", 25 },
            { "Protective", 20 },
        };

        private readonly List<RuneshapeRecipe> recipes = new();
        private readonly Dictionary<long, int> partialMinLevel = new();

        public int RecipeCount => this.recipes.Count;
        public int RuneWeightsCount => this.partialMinLevel.Count;

        public void LoadRecipes(string? pluginDir)
        {
            this.recipes.Clear();
            this.partialMinLevel.Clear();

            string[] paths =
            {
                !string.IsNullOrEmpty(pluginDir) ? Path.Combine(pluginDir, "expedition2_recipes.json") : string.Empty,
                Path.Combine(AppContext.BaseDirectory, "Plugins", "ExpeditionPathOptimizer", "expedition2_recipes.json"),
                Path.Combine(AppContext.BaseDirectory, "Plugins", "NinjaPricer", "expedition2_recipes.json"),
                Path.Combine(AppContext.BaseDirectory, "Plugins", "ExpeditionPlanner", "expedition2_recipes.json"),
                Path.Combine(AppContext.BaseDirectory, "resources", "runeshape", "expedition2_recipes.json"),
            };

            foreach (var p in paths)
            {
                if (string.IsNullOrEmpty(p) || !File.Exists(p)) continue;

                try
                {
                    var json = File.ReadAllText(p);
                    using var doc = JsonDocument.Parse(json);

                    if (doc.RootElement.TryGetProperty("recipes", out var recipesElem) && recipesElem.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in recipesElem.EnumerateArray())
                        {
                            var r = new RuneshapeRecipe();
                            if (item.TryGetProperty("row", out var rowProp)) r.Row = rowProp.GetInt32();
                            if (item.TryGetProperty("id", out var idProp)) r.Id = idProp.GetString() ?? string.Empty;
                            if (item.TryGetProperty("size", out var sProp)) r.Size = sProp.GetInt32();
                            if (item.TryGetProperty("description", out var dProp)) r.Description = dProp.GetString() ?? string.Empty;
                            if (item.TryGetProperty("reward", out var rwProp))
                            {
                                if (rwProp.ValueKind == JsonValueKind.String)
                                {
                                    r.Reward = rwProp.GetString() ?? string.Empty;
                                }
                                else if (rwProp.ValueKind == JsonValueKind.Object)
                                {
                                    if (rwProp.TryGetProperty("name", out var nProp))
                                    {
                                        r.Reward = nProp.GetString() ?? string.Empty;
                                    }
                                }
                            }
                            if (item.TryGetProperty("rewardCount", out var rcProp)) r.RewardCount = Math.Max(1, rcProp.GetInt32());
                            if (item.TryGetProperty("category", out var cProp)) r.Category = cProp.GetInt32();
                            if (item.TryGetProperty("minLevel", out var mlProp)) r.MinLevel = mlProp.GetInt32();
                            if (item.TryGetProperty("maxLevel", out var xlProp)) r.MaxLevel = xlProp.GetInt32();

                            if (item.TryGetProperty("runeIdx", out var rIdxElem) && rIdxElem.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var idx in rIdxElem.EnumerateArray())
                                {
                                    r.RuneIdx.Add(idx.GetInt32());
                                }
                            }

                            if (item.TryGetProperty("runes", out var runesElem) && runesElem.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var rune in runesElem.EnumerateArray())
                                {
                                    var rName = rune.GetString();
                                    if (!string.IsNullOrEmpty(rName)) r.Runes.Add(rName);
                                }
                            }

                            r.ComboWeight = CalculateRecipeWeight(r.Runes);
                            this.recipes.Add(r);
                        }
                    }

                    if (doc.RootElement.TryGetProperty("runeWeights", out var rwElem) && rwElem.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var w in rwElem.EnumerateArray())
                        {
                            int r = w.GetProperty("rune").GetInt32();
                            int pos = w.GetProperty("pos").GetInt32();
                            int size = w.GetProperty("size").GetInt32();
                            int ml = w.GetProperty("minLevel").GetInt32();
                            long key = ((long)r << 16) | ((long)pos << 8) | (uint)size;
                            if (!this.partialMinLevel.TryGetValue(key, out var cur) || ml < cur)
                            {
                                this.partialMinLevel[key] = ml;
                            }
                        }
                    }

                    if (this.recipes.Count > 0)
                    {
                        PluginLog.Info("ExpeditionPathOptimizer", $"[ExpeditionPathOptimizer] Loaded {this.recipes.Count} Runeshape recipes and {this.partialMinLevel.Count} runeWeights from '{p}'");
                        break;
                    }
                }
                catch (Exception ex)
                {
                    PluginLog.Error("ExpeditionPathOptimizer", $"[ExpeditionPathOptimizer] Failed reading recipes from '{p}': {ex.Message}");
                }
            }
        }

        private static int CalculateRecipeWeight(IEnumerable<string> runes)
        {
            int total = 0;
            foreach (var r in runes)
            {
                if (DefaultRuneWeights.TryGetValue(r, out var defW)) total += defW;
                else total += 20;
            }
            return total;
        }

        public bool IsPartialAllowed(int runeIdx, int pos0Based, int recipeSize, int areaLevel)
        {
            long key = ((long)runeIdx << 16) | ((long)(pos0Based + 1) << 8) | (uint)recipeSize;
            if (!this.partialMinLevel.TryGetValue(key, out var minL)) return false;
            return areaLevel <= 0 || areaLevel >= minL;
        }

        public List<RuneshapeRecipeOffer> PredictOffers(
            int holeCount,
            int anchorIdx,
            int anchorPos,
            bool isUnique,
            List<int> goldenSlots,
            int areaLevel,
            ExpeditionPriceService? priceService)
        {
            var offers = new List<RuneshapeRecipeOffer>();

            foreach (var rec in this.recipes)
            {
                if (rec.Size > holeCount) continue;
                if (areaLevel > 0 && rec.MaxLevel > 0 && (areaLevel < rec.MinLevel || areaLevel > rec.MaxLevel)) continue;

                if (!isUnique && anchorIdx >= 0)
                {
                    if (rec.RuneIdx == null || rec.RuneIdx.Count <= anchorPos) continue;
                    if (rec.RuneIdx[anchorPos] != anchorIdx) continue;
                    if (rec.Size != holeCount && !this.IsPartialAllowed(anchorIdx, anchorPos, rec.Size, areaLevel)) continue;
                }

                // Propagated Runes come from GoldenSlots AND must be propagation-eligible (non-white / non-common)
                var propRunes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (goldenSlots != null && rec.RuneIdx != null)
                {
                    foreach (var goldenSlot in goldenSlots)
                    {
                        if (goldenSlot >= 0 && goldenSlot < rec.RuneIdx.Count)
                        {
                            int rIdx = rec.RuneIdx[goldenSlot];
                            if (CanPropagateRune(rIdx))
                            {
                                propRunes.Add(RuneNames[rIdx]);
                            }
                        }
                    }
                }

                string rewardName = !string.IsNullOrEmpty(rec.Reward) ? rec.Reward : rec.Description;
                int count = Math.Max(1, rec.RewardCount);
                bool isPriced = false;
                float priceChaos = 0f;
                float priceDivine = 0f;
                float priceExalt = 0f;

                if (priceService != null && priceService.TryLookupPrice(rewardName, out var price))
                {
                    isPriced = true;
                    priceChaos = price.Chaos * count;
                    priceDivine = price.Divine * count;
                    priceExalt = price.Exalt * count;
                }

                offers.Add(new RuneshapeRecipeOffer
                {
                    RecipeId = rec.Id,
                    Description = rec.Description,
                    Reward = rewardName,
                    RewardCount = count,
                    Runes = rec.Runes?.ToArray() ?? Array.Empty<string>(),
                    RuneIdx = rec.RuneIdx?.ToArray() ?? Array.Empty<int>(),
                    PropagatedRunes = propRunes.ToArray(),
                    PriceChaos = priceChaos,
                    PriceDivine = priceDivine,
                    PriceExalt = priceExalt,
                    IsPriced = isPriced,
                    ComboWeight = rec.ComboWeight,
                    Size = rec.Size,
                    Category = rec.Category
                });
            }

            // BestRecipe selection:
            // If at least one offer is priced:
            //   BestRecipe = highest PriceChaos, tie-break by ComboWeight
            // If no offers are priced:
            //   BestRecipe = highest ComboWeight, tie-break by RewardCount
            bool anyPriced = offers.Any(o => o.IsPriced);
            if (anyPriced)
            {
                offers.Sort((a, b) =>
                {
                    int pCmp = b.PriceChaos.CompareTo(a.PriceChaos);
                    if (pCmp != 0) return pCmp;
                    int wCmp = b.ComboWeight.CompareTo(a.ComboWeight);
                    if (wCmp != 0) return wCmp;
                    return b.RewardCount.CompareTo(a.RewardCount);
                });
            }
            else
            {
                offers.Sort((a, b) =>
                {
                    int wCmp = b.ComboWeight.CompareTo(a.ComboWeight);
                    if (wCmp != 0) return wCmp;
                    return b.RewardCount.CompareTo(a.RewardCount);
                });
            }

            return offers;
        }
    }
}
