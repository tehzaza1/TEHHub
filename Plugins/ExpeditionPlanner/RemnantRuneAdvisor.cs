namespace ExpeditionPlanner
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using TEHhub;
    using TEHhub.Offsets.Natives;
    using TEHhub.Offsets.Objects.UiElement;
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

        // Runeshape Combinations UI constants
        public static readonly uint[] PanelFlagFingerprints =
            { 0x00462EF1, 0x00502EF3, 0x00502EF7, 0x00542EF1, 0x00502EF1 };
        public const uint UiVisibleMask = 0x800;
        private const int UiElementTextOffset = 0x3F8;

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

            var bestOffer = PickBestRecipe(anchorIdx, anchorPos, holeCount, isUnique, areaLevel, anchorTier);

            if (isAnchorInGoldenSlot)
            {
                goldenRuneCandidate = anchorName;
                proliferatedTier = anchorTier;
                if (!isUnique && anchorTier == RuneTier.Blue_C)
                {
                    needsReroll = true;
                    rerollReason = $"Blue rune in golden slot ({anchorName}). Reroll recommended for Purple or Opulent!";
                }
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

                // If bestOffer's recipe has a golden slot rune, prioritize it
                if (bestOffer?.Recipe?.runeIdx != null && bestOffer.Recipe.runeIdx.Count > goldenSlotIndex)
                {
                    var gIdx = bestOffer.Recipe.runeIdx[goldenSlotIndex];
                    if (gIdx >= 0 && gIdx < RuneNames.Length)
                    {
                        var rName = RuneNames[gIdx];
                        bestCandidateRune = rName;
                        bestCandidateTier = GetRuneTier(rName);
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

                if (proliferatedTier == RuneTier.Blue_C)
                {
                    needsReroll = true;
                    rerollReason = $"Golden Slot #{goldenSlotIndex + 1} likely Blue ({goldenRuneCandidate}). Reroll recommended!";
                }
                else
                {
                    needsReroll = false;
                    rerollReason = string.Empty;
                }
            }
            else
            {
                goldenRuneCandidate = isUnique ? "Unique" : "Unknown";
                proliferatedTier = RuneTier.Blue_C;
            }

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
                        Score = score,
                        Recipe = rec
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
            public RecipeDef? Recipe { get; set; }
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

        /// <summary>
        /// Reads recipe texts directly from the live RuneshapeCombinationsPanel UI when open.
        /// </summary>
        public static bool TryReadOpenPanel(out List<string> recipes)
        {
            recipes = new List<string>();
            var gameUi = Core.States.InGameStateObject?.GameUi;
            if (gameUi == null || gameUi.Address == IntPtr.Zero) return false;

            var panel = gameUi.RuneshapeCombinationsPanel;
            if (panel.Address == IntPtr.Zero || !panel.IsVisible) return false;

            var reader = Core.Process.Handle;
            if (reader == null) return false;

            var container = WalkPanelUi(panel.Address, 1);
            if (container == IntPtr.Zero) return false;

            if (!reader.TryReadMemory<UiElementBaseOffset>(container, out var cOff)) return false;
            var rows = reader.ReadStdVector<IntPtr>(cOff.ChildrensPtr);
            if (rows == null || rows.Length == 0) return false;

            foreach (var row in rows)
            {
                if (row == IntPtr.Zero) continue;
                if (!reader.TryReadMemory<UiElementBaseOffset>(row, out var rowOff)) continue;
                if ((rowOff.Flags & UiVisibleMask) == 0) continue;

                var rowKids = reader.ReadStdVector<IntPtr>(rowOff.ChildrensPtr);
                if (rowKids == null || rowKids.Length == 0 || rowKids[0] == IntPtr.Zero) continue;

                try
                {
                    var ws = reader.ReadMemory<StdWString>(rowKids[0] + UiElementTextOffset);
                    var text = reader.ReadStdWString(ws);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        recipes.Add(text.Trim());
                    }
                }
                catch
                {
                    // Ignore transient memory read exceptions
                }
            }

            return recipes.Count > 0;
        }

        private static IntPtr WalkPanelUi(IntPtr parent, int step)
        {
            if (parent == IntPtr.Zero || step >= PanelFlagFingerprints.Length) return parent;

            var reader = Core.Process.Handle;
            if (!reader.TryReadMemory<UiElementBaseOffset>(parent, out var off)) return IntPtr.Zero;

            var kids = reader.ReadStdVector<IntPtr>(off.ChildrensPtr);
            if (kids == null || kids.Length == 0) return IntPtr.Zero;

            var targetFp = PanelFlagFingerprints[step] & ~UiVisibleMask;
            for (var pass = 0; pass < 2; pass++)
            {
                var wantVisible = pass == 0;
                foreach (var child in kids)
                {
                    if (child == IntPtr.Zero) continue;
                    if (!reader.TryReadMemory<UiElementBaseOffset>(child, out var coff)) continue;
                    var visible = (coff.Flags & UiVisibleMask) != 0;
                    if (visible != wantVisible) continue;
                    if ((coff.Flags & ~UiVisibleMask) == targetFp)
                    {
                        var res = WalkPanelUi(child, step + 1);
                        if (res != IntPtr.Zero) return res;
                    }
                }
            }

            return IntPtr.Zero;
        }
    }
}
