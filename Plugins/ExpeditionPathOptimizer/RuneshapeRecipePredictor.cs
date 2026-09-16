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
        /// Explicit game data definition of Runes that are eligible to propagate modifiers via GoldenSlots.
        /// EXACTLY the S + A tiers: Opulent, Bond, Oath, Power, Death, Time, Rebirth.
        /// Tier B (Arcane, Prismatic, Soul, Vision, Celestial, Rage, Wisdom) and all white/Tier C runes CANNOT propagate.
        /// </summary>
        public static readonly HashSet<string> PropagatingRuneNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Opulent",
            "Bond",
            "Oath",
            "Power",
            "Death",
            "Time",
            "Rebirth"
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

        public static int CalculateRecipeWeight(IEnumerable<string>? runes, IReadOnlyDictionary<string, double>? runeWeights)
        {
            if (runes == null) return 0;
            double total = 0.0;
            foreach (var r in runes)
            {
                if (runeWeights != null && runeWeights.TryGetValue(r, out var w))
                {
                    total += w;
                }
                else
                {
                    total += 20.0;
                }
            }
            return (int)Math.Round(total);
        }

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
            ExpeditionPriceService? priceService,
            IReadOnlyDictionary<string, double>? runeWeights)
        {
            if (holeCount <= 0) return new List<RuneshapeRecipeOffer>();

            // For non-unique monolith, require valid anchor index and position within holeCount
            if (!isUnique)
            {
                if (anchorIdx < 0 || anchorIdx >= RuneNames.Length || anchorPos < 0 || anchorPos >= holeCount)
                {
                    return new List<RuneshapeRecipeOffer>();
                }
            }

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

                // Propagated Runes come from GoldenSlots AND must be propagation-eligible (S + A tier only)
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

                int comboWeight = CalculateRecipeWeight(rec.Runes, runeWeights);

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
                    ComboWeight = comboWeight,
                    Size = rec.Size,
                    Category = rec.Category
                });
            }

            return offers;
        }

        public RuneshapeRecipeOffer? SelectBestPriceRecipe(IReadOnlyList<RuneshapeRecipeOffer>? offers)
        {
            if (offers == null || offers.Count == 0) return null;

            bool anyPriced = offers.Any(o => o.IsPriced);
            if (anyPriced)
            {
                return offers
                    .OrderByDescending(o => o.PriceChaos)
                    .ThenByDescending(o => o.ComboWeight)
                    .ThenByDescending(o => o.RewardCount)
                    .First();
            }

            return offers
                .OrderByDescending(o => o.ComboWeight)
                .ThenByDescending(o => o.RewardCount)
                .First();
        }

        public RuneshapeRecipeOffer? SelectBestRuneRecipe(IReadOnlyList<RuneshapeRecipeOffer>? offers, IReadOnlyDictionary<string, double>? runeWeights)
        {
            if (offers == null || offers.Count == 0) return null;

            double GetDistinctPropagatedRuneWeight(RuneshapeRecipeOffer offer)
            {
                if (offer.PropagatedRunes == null || offer.PropagatedRunes.Count == 0) return 0.0;
                double sum = 0.0;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in offer.PropagatedRunes)
                {
                    if (seen.Add(r))
                    {
                        sum += runeWeights?.GetValueOrDefault(r, 20.0) ?? 20.0;
                    }
                }
                return sum;
            }

            return offers
                .OrderByDescending(o => GetDistinctPropagatedRuneWeight(o))
                .ThenByDescending(o => o.ComboWeight)
                .ThenByDescending(o => o.RewardCount)
                .First();
        }
    }
}
