namespace ExpeditionPlanner
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
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
        private const int StationGoldenSlotsOffset = 0x40;

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
            out int goldenSlotIndex,
            out int anchorSlotIndex,
            out string anchorName,
            out string goldenRuneCandidate,
            out List<string> candidateRuneSequence,
            out string recommendedChoice,
            out string description,
            out float estimatedValue,
            out RuneTier proliferatedTier,
            out bool isAnchorInGoldenSlot,
            out bool needsReroll,
            out string rerollReason)
        {
            holeCount = 0;
            goldenSlotIndex = -1;
            anchorSlotIndex = -1;
            anchorName = string.Empty;
            goldenRuneCandidate = string.Empty;
            candidateRuneSequence = new List<string>();
            recommendedChoice = string.Empty;
            description = string.Empty;
            estimatedValue = 30f;
            proliferatedTier = RuneTier.Blue_C;
            isAnchorInGoldenSlot = true;
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

            // Authoritative Golden Crown socket index (stored in std::vector<int> at station + 0x40)
            var goldenVec = reader.ReadMemory<StdVector>(station + StationGoldenSlotsOffset);
            int goldenSlot = -1;
            var goldenCount = goldenVec.TotalElements(sizeof(int));
            if (goldenCount > 0 && goldenCount <= 16)
            {
                var goldenSlots = reader.ReadMemoryArray<int>(goldenVec.First, (int)goldenCount);
                if (goldenSlots != null && goldenSlots.Length > 0)
                {
                    goldenSlot = goldenSlots[0];
                }
            }

            // Fallback to anchorPos if no golden slot vector was found
            if (goldenSlot < 0)
            {
                goldenSlot = anchorPos;
            }

            goldenSlotIndex = goldenSlot;

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
            var anchorTier = GetRuneTier(anchorName);

            EnsureRecipesLoaded();

            // Crucial: Only the rune IN the Golden Slot proliferates to subsequent remnants!
            isAnchorInGoldenSlot = isUnique || (goldenSlotIndex == anchorSlotIndex);

            var matchingRecipes = new List<RecipeDef>();
            if (!isUnique && anchorIdx >= 0)
            {
                foreach (var rec in loadedRecipes)
                {
                    if (rec.size > holeCount) continue;
                    if (areaLevel > 0 && rec.maxLevel > 0 && (areaLevel < rec.minLevel || areaLevel > rec.maxLevel)) continue;
                    if (rec.runeIdx != null && rec.runeIdx.Count > anchorPos && rec.runeIdx[anchorPos] == anchorIdx)
                    {
                        matchingRecipes.Add(rec);
                    }
                }
            }

            if (isAnchorInGoldenSlot)
            {
                goldenRuneCandidate = anchorName;
                proliferatedTier = anchorTier;
            }
            else if (!isUnique && goldenSlotIndex >= 0 && anchorIdx >= 0)
            {
                int targetGoldenSlot = goldenSlotIndex;
                // Prefer recipes matching exact holeCount, or highest available size that contains goldenSlotIndex
                int maxMatchingSize = matchingRecipes.Count > 0 ? matchingRecipes.Max(r => r.size) : 0;
                var primeCandidates = matchingRecipes.Where(r => r.size == maxMatchingSize && r.runeIdx != null && r.runeIdx.Count > targetGoldenSlot).ToList();
                if (primeCandidates.Count == 0)
                {
                    primeCandidates = matchingRecipes.Where(r => r.runeIdx != null && r.runeIdx.Count > targetGoldenSlot).ToList();
                }

                var candidateRunes = new List<string>();
                RuneTier bestCandidateTier = RuneTier.Blue_C;
                string bestCandidateRune = string.Empty;

                foreach (var rec in primeCandidates)
                {
                    var gIdx = rec.runeIdx![goldenSlotIndex];
                    if (gIdx >= 0 && gIdx < RuneNames.Length)
                    {
                        var rName = RuneNames[gIdx];
                        if (!candidateRunes.Contains(rName))
                        {
                            candidateRunes.Add(rName);
                        }

                        var tier = GetRuneTier(rName);
                        if (tier < bestCandidateTier || string.IsNullOrEmpty(bestCandidateRune))
                        {
                            bestCandidateTier = tier;
                            bestCandidateRune = rName;
                        }
                    }
                }

                proliferatedTier = bestCandidateTier;
                if (candidateRunes.Count > 1)
                {
                    var otherCandidates = candidateRunes.Where(r => !string.Equals(r, bestCandidateRune, StringComparison.OrdinalIgnoreCase)).ToList();
                    goldenRuneCandidate = otherCandidates.Count > 0 ? $"{bestCandidateRune} / {otherCandidates[0]}" : bestCandidateRune;
                }
                else if (!string.IsNullOrEmpty(bestCandidateRune))
                {
                    goldenRuneCandidate = bestCandidateRune;
                }
                else
                {
                    goldenRuneCandidate = "Blue Rune";
                    proliferatedTier = RuneTier.Blue_C;
                }
            }
            else
            {
                goldenRuneCandidate = isUnique ? "Unique" : "Unknown";
                proliferatedTier = RuneTier.Blue_C;
            }

            // --- Smart Reroll Advisor Evaluation ---
            if (isUnique || string.IsNullOrEmpty(goldenRuneCandidate) || goldenRuneCandidate == "Unknown")
            {
                needsReroll = false;
                rerollReason = string.Empty;
            }
            // 1. SSS (Opulent) and S-Tier (Power, Death, Bond, Oath):
            // NEVER REROLL! Even with only 2-3 holes, proliferating SSS/S tier multiplier into subsequent remnants is priceless.
            else if (proliferatedTier == RuneTier.Golden || proliferatedTier == RuneTier.Purple_S)
            {
                needsReroll = false;
                rerollReason = string.Empty;
            }
            // 2. Blue rune in Golden Slot (Blue_C):
            else if (proliferatedTier == RuneTier.Blue_C)
            {
                if (holeCount >= 8)
                {
                    // High sockets (8+ holes): Do not reroll! Risk of losing many sockets.
                    needsReroll = false;
                    rerollReason = $"Blue in Golden slot, but has {holeCount} sockets (reroll risks losing sockets).";
                }
                else if (holeCount <= 4)
                {
                    // Low sockets (<= 4 holes) + Blue: Highly recommend reroll for both more sockets and better runes!
                    needsReroll = true;
                    rerollReason = $"Blue rune ({goldenRuneCandidate}) & low sockets ({holeCount} holes). Highly recommend Reroll!";
                }
                else
                {
                    // Medium sockets (5-7 holes) + Blue:
                    needsReroll = true;
                    rerollReason = $"Blue rune in Golden slot ({goldenRuneCandidate}). Reroll recommended for Purple or Opulent!";
                }
            }
            // 3. Purple_B (low-tier purple like Arcane, Protective, etc.) with very few sockets (<= 3 holes):
            else if (proliferatedTier == RuneTier.Purple_B && holeCount <= 3)
            {
                needsReroll = true;
                rerollReason = $"Low-tier Purple ({goldenRuneCandidate}) with only {holeCount} sockets. Reroll recommended for more sockets or S-tier!";
            }
            // 4. High-tier Purple (Purple_A like Time, Rebirth) or Purple_B with >= 4 sockets:
            else
            {
                needsReroll = false;
                rerollReason = string.Empty;
            }

            // Build the best recipe reference so the player knows WHICH ROW to click in the Runeshape Combinations dialog
            // Strategy: pick the best matching recipe (highest golden-slot tier, then highest rewardCount)
            RecipeDef? bestRecipe = null;
            if (!isUnique && matchingRecipes.Count > 0)
            {
                int capturedGoldenSlot = goldenSlotIndex; // capture out-param for use inside lambda
                bestRecipe = matchingRecipes
                    .Where(r => r.runeIdx != null && r.runeIdx.Count > capturedGoldenSlot)
                    .OrderBy(r =>
                    {
                        var gIdx = r.runeIdx![capturedGoldenSlot];
                        var rName = (gIdx >= 0 && gIdx < RuneNames.Length) ? RuneNames[gIdx] : string.Empty;
                        return (int)GetRuneTier(rName);
                    })
                    .ThenByDescending(r => r.rewardCount)
                    .ThenByDescending(r => r.size)
                    .FirstOrDefault();
            }

            // Build candidateRuneSequence from best recipe (player can match visually)
            if (bestRecipe?.runes != null && bestRecipe.runes.Count > 0)
            {
                candidateRuneSequence = bestRecipe.runes;
            }
            else if (bestRecipe?.runeIdx != null)
            {
                candidateRuneSequence = bestRecipe.runeIdx
                    .Select(idx => (idx >= 0 && idx < RuneNames.Length) ? RuneNames[idx] : "?")
                    .ToList();
            }
            else
            {
                candidateRuneSequence = new List<string>();
            }

            // Build the "Pick:" text that tells the player exactly what row to click
            string rewardLabel;
            if (bestRecipe?.reward != null && !string.IsNullOrEmpty(bestRecipe.reward.name))
            {
                rewardLabel = bestRecipe.rewardCount > 1
                    ? $"{bestRecipe.rewardCount}x {bestRecipe.reward.name}"
                    : bestRecipe.reward.name;
            }
            else if (!string.IsNullOrEmpty(bestRecipe?.description))
            {
                rewardLabel = bestRecipe!.description!;
            }
            else
            {
                rewardLabel = string.Empty;
            }

            string runeRow = candidateRuneSequence.Count > 0
                ? string.Join(" - ", candidateRuneSequence)
                : string.Empty;

            if (!string.IsNullOrEmpty(rewardLabel) && !string.IsNullOrEmpty(runeRow))
            {
                recommendedChoice = $"Pick: {rewardLabel}  [{runeRow}]";
            }
            else if (!string.IsNullOrEmpty(rewardLabel))
            {
                recommendedChoice = $"Pick: {rewardLabel}";
            }
            else if (isAnchorInGoldenSlot)
            {
                recommendedChoice = $"[{holeCount} Sockets] {anchorName}";
            }
            else
            {
                recommendedChoice = $"[{holeCount} Sockets] Golden: {goldenRuneCandidate}";
            }

            description = "Excavate for Runic Monsters and proliferated modifier buffs";
            estimatedValue = GetTierBaseScore(proliferatedTier, holeCount);

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

        private sealed class CatalogFile
        {
            public Dictionary<string, string>? runes { get; set; }
            public List<RecipeDef>? recipes { get; set; }
        }

        private sealed class RecipeDef
        {
            public int row { get; set; }
            public string? id { get; set; }
            public int size { get; set; }
            public int minLevel { get; set; }
            public int maxLevel { get; set; }
            public List<int>? runeIdx { get; set; }
            public List<string>? runes { get; set; }   // human-readable rune names per slot
            public RewardInfo? reward { get; set; }
            public int rewardCount { get; set; } = 1;
            public string? description { get; set; }
        }

        private sealed class RewardInfo
        {
            public string? id { get; set; }
            public string? name { get; set; }
        }
    }
}
