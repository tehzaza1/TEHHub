namespace ExpeditionPlanner
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using TEHhub;
    using TEHhub.Offsets.Natives;
    using TEHhub.RemoteObjects.Components;
    using TEHhub.RemoteObjects.States.InGameStateObjects;

    public static class RemnantRuneAdvisor
    {
        private const int ListenerVectorOffset = 0x20;
        private const int StationOwnerOffset = 0x10;
        private const int StationAnchorRefOffset = 0x28;
        private const int StationAnchorHolderOffset = 0x30;
        private const int StationHoleCountOffset = 0x38;
        private const int StationAnchorPosOffset = 0x3C;

        private static readonly string[] RuneNames =
        [
            "Fire", "Cold", "Lightning", "Tempest", "Momentum", "Bloodletting", "Stone", "Adaptive",
            "Arcane", "Toxic", "Electrocuting", "Protective", "Cyclonic", "Vision", "Tidal",
            "Rebirth", "Prismatic", "Gasp", "Moon", "Celestial", "Opulent", "Rage",
            "Wisdom", "Sky", "Earth", "Life", "Bond", "Ward", "Soul", "Death",
            "Oath", "Time", "Power", "Bait"
        ];

        private static readonly object SyncRoot = new();
        private static bool isLoaded = false;
        private static List<RecipeDef> loadedRecipes = new();

        public static void EnsureRecipesLoaded()
        {
            if (isLoaded) return;
            lock (SyncRoot)
            {
                if (isLoaded) return;

                var candidates = new List<string>
                {
                    Path.Combine(AppContext.BaseDirectory, "Plugins", "ExpeditionPlanner", "expedition2_recipes.json"),
                    Path.Combine("Plugins", "ExpeditionPlanner", "expedition2_recipes.json"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins", "ExpeditionPlanner", "expedition2_recipes.json"),
                    Path.Combine(AppContext.BaseDirectory, "expedition2_recipes.json")
                };

                string? targetFile = null;
                foreach (var path in candidates)
                {
                    if (File.Exists(path))
                    {
                        targetFile = path;
                        break;
                    }
                }

                if (targetFile != null)
                {
                    try
                    {
                        var json = File.ReadAllText(targetFile);
                        var file = JsonSerializer.Deserialize<CatalogFile>(json);
                        if (file?.recipes != null)
                        {
                            loadedRecipes = file.recipes;
                        }
                    }
                    catch
                    {
                        // Fallback to empty list
                    }
                }

                isLoaded = true;
            }
        }

        public static RuneTier GetRuneTier(string runeName)
        {
            if (string.Equals(runeName, "Opulent", StringComparison.OrdinalIgnoreCase))
            {
                return RuneTier.Golden;
            }

            if (string.Equals(runeName, "Power", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(runeName, "Death", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(runeName, "Bond", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(runeName, "Oath", StringComparison.OrdinalIgnoreCase))
            {
                return RuneTier.Purple_S;
            }

            if (string.Equals(runeName, "Time", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(runeName, "Rebirth", StringComparison.OrdinalIgnoreCase))
            {
                return RuneTier.Purple_A;
            }

            // Other purple runes in PoE 2
            if (string.Equals(runeName, "Arcane", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(runeName, "Protective", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(runeName, "Celestial", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(runeName, "Soul", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(runeName, "Vision", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(runeName, "Prismatic", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(runeName, "Moon", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(runeName, "Rage", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(runeName, "Wisdom", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(runeName, "Life", StringComparison.OrdinalIgnoreCase))
            {
                return RuneTier.Purple_B;
            }

            return RuneTier.Blue_C;
        }

        public static bool TryReadMonolith(
            Entity entity,
            int areaLevel,
            out int holeCount,
            out int anchorSlotIndex,
            out string anchorName,
            out string recommendedChoice,
            out string description,
            out float estimatedValue,
            out RuneTier proliferatedTier,
            out bool needsReroll,
            out string rerollReason)
        {
            holeCount = 0;
            anchorSlotIndex = -1;
            anchorName = string.Empty;
            recommendedChoice = string.Empty;
            description = string.Empty;
            estimatedValue = 30f;
            proliferatedTier = RuneTier.Blue_C;
            needsReroll = false;
            rerollReason = string.Empty;

            if (entity.Address == IntPtr.Zero) return false;
            if (!entity.TryGetComponent<StateMachine>(out var sm, false) || sm.Address == IntPtr.Zero) return false;

            var reader = Core.Process.Handle;
            var listeners = reader.ReadMemory<StdVector>(sm.Address + ListenerVectorOffset);
            var nodes = reader.ReadStdVector<long>(listeners);
            if (nodes == null || nodes.Length == 0 || nodes.Length > 256) return false;

            IntPtr station = IntPtr.Zero;
            foreach (var nodeValue in nodes)
            {
                if (nodeValue == 0) continue;
                var sub = reader.ReadMemory<IntPtr>(new IntPtr(nodeValue));
                if (sub == IntPtr.Zero) continue;

                var cand1 = sub - 0xA0;
                if (reader.ReadMemory<IntPtr>(cand1 + StationOwnerOffset) == entity.Address)
                {
                    station = cand1;
                    break;
                }

                var cand2 = sub - 0x98;
                if (reader.ReadMemory<IntPtr>(cand2 + StationOwnerOffset) == entity.Address)
                {
                    station = cand2;
                    break;
                }
            }

            if (station == IntPtr.Zero) return false;

            holeCount = reader.ReadMemory<int>(station + StationHoleCountOffset);
            if (holeCount is <= 0 or > 16) return false;

            var anchorPos = reader.ReadMemory<int>(station + StationAnchorPosOffset);
            anchorSlotIndex = anchorPos;
            var rowPtr = reader.ReadMemory<IntPtr>(station + StationAnchorRefOffset);
            bool isUnique = rowPtr == IntPtr.Zero;
            int anchorIdx = -1;

            if (!isUnique)
            {
                var holder = reader.ReadMemory<IntPtr>(station + StationAnchorHolderOffset);
                if (holder != IntPtr.Zero)
                {
                    var p1 = reader.ReadMemory<IntPtr>(holder + 0x28);
                    if (p1 != IntPtr.Zero)
                    {
                        var tableBase = reader.ReadMemory<long>(p1);
                        if (tableBase != 0)
                        {
                            var delta = rowPtr.ToInt64() - tableBase;
                            if (delta >= 0)
                            {
                                if (delta % 0x68 == 0) anchorIdx = (int)(delta / 0x68);
                                else if (delta % 0x6C == 0) anchorIdx = (int)(delta / 0x6C);
                            }

                            if (anchorIdx < 0 || anchorIdx >= RuneNames.Length) anchorIdx = -1;
                        }
                    }
                }
            }

            anchorName = isUnique ? "Unique Monolith" : (anchorIdx >= 0 && anchorIdx < RuneNames.Length ? RuneNames[anchorIdx] : "Rune Monolith");
            proliferatedTier = GetRuneTier(anchorName);

            // Reroll detection: "Remnants with no purple runes should always be rerolled"
            if (!isUnique && proliferatedTier == RuneTier.Blue_C)
            {
                needsReroll = true;
                rerollReason = $"Blue rune in golden slot ({anchorName}). Reroll recommended for Purple or Opulent!";
            }

            EnsureRecipesLoaded();
            var bestOffer = PickBestRecipe(anchorIdx, anchorPos, holeCount, isUnique, areaLevel, proliferatedTier);
            if (bestOffer != null)
            {
                recommendedChoice = bestOffer.Name;
                description = bestOffer.Description;
                estimatedValue = bestOffer.Score;
            }
            else
            {
                recommendedChoice = $"[{holeCount}x {anchorName}] Craft";
                description = "Excavate for Runic Monsters and proliferated modifier buffs";
                estimatedValue = GetTierBaseScore(proliferatedTier, holeCount);
            }

            return true;
        }

        private static float GetTierBaseScore(RuneTier tier, int holeCount)
        {
            return tier switch
            {
                RuneTier.Golden => 1000f + (holeCount * 35f),
                RuneTier.Purple_S => 450f + (holeCount * 25f),
                RuneTier.Purple_A => 250f + (holeCount * 20f),
                RuneTier.Purple_B => 100f + (holeCount * 15f),
                _ => 30f + (holeCount * 10f)
            };
        }

        private static OfferResult? PickBestRecipe(int anchorIdx, int anchorPos, int holeCount, bool isUnique, int areaLevel, RuneTier tier)
        {
            if (loadedRecipes.Count == 0) return null;

            OfferResult? best = null;
            float maxScore = float.NegativeInfinity;

            foreach (var rec in loadedRecipes)
            {
                if (rec.size > holeCount) continue;
                if (areaLevel > 0 && rec.maxLevel > 0 && (areaLevel < rec.minLevel || areaLevel > rec.maxLevel)) continue;

                if (!isUnique && anchorIdx >= 0)
                {
                    if (rec.runeIdx == null || rec.runeIdx.Count <= anchorPos || rec.runeIdx[anchorPos] != anchorIdx)
                    {
                        continue;
                    }
                }

                var name = rec.reward?.name ?? rec.description ?? rec.id ?? string.Empty;
                var desc = rec.description ?? string.Empty;

                float score = GetTierBaseScore(tier, rec.size);

                // Priority keywords
                if (name.Contains("Divine", StringComparison.OrdinalIgnoreCase) || name.Contains("Mirror", StringComparison.OrdinalIgnoreCase)) score += 300f;
                else if (name.Contains("Exalted", StringComparison.OrdinalIgnoreCase) || name.Contains("Chaos", StringComparison.OrdinalIgnoreCase)) score += 150f;
                else if (name.Contains("Greater", StringComparison.OrdinalIgnoreCase) || name.Contains("Grand", StringComparison.OrdinalIgnoreCase)) score += 100f;
                else if (name.Contains("Logbook", StringComparison.OrdinalIgnoreCase)) score += 140f;
                else if (name.Contains("Foundations", StringComparison.OrdinalIgnoreCase)) score += 80f;
                else if (name.Contains("Currency", StringComparison.OrdinalIgnoreCase)) score += 70f;

                if (score > maxScore)
                {
                    maxScore = score;
                    best = new OfferResult
                    {
                        Name = name,
                        Description = desc,
                        Score = score
                    };
                }
            }

            return best;
        }

        private sealed class OfferResult
        {
            public string Name { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public float Score { get; set; }
        }

        private sealed class CatalogFile
        {
            public Dictionary<string, string>? runes { get; set; }
            public List<RecipeDef>? recipes { get; set; }
        }

        private sealed class RecipeDef
        {
            public string? id { get; set; }
            public int size { get; set; }
            public int minLevel { get; set; }
            public int maxLevel { get; set; }
            public List<int>? runeIdx { get; set; }
            public List<string>? runes { get; set; }
            public RewardDef? reward { get; set; }
            public string? description { get; set; }
        }

        private sealed class RewardDef
        {
            public string? name { get; set; }
            public int count { get; set; }
        }
    }
}
