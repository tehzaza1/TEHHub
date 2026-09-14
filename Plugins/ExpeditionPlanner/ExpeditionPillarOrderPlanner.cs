namespace ExpeditionPlanner
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    /// <summary>
    /// Pillar Order Advisor: calculates ONLY the optimal sequence in which to detonate Remnant pillars.
    /// It intentionally avoids calculating bomb coordinates, fuse wire lengths, A* paths, bridges, or blast radii.
    /// </summary>
    internal static class ExpeditionPillarOrderPlanner
    {
        internal static RouteEvaluation Build(IEnumerable<ExpeditionTarget> targets, ExpeditionPlannerSettings settings)
        {
            // 1. Read only real Remnant Pillars from the map
            var pillars = targets
                .Where(t => t.Kind == TargetKind.RemnantPillar)
                .ToList();

            var result = new RouteEvaluation
            {
                Profile = "PillarOrderAdvisor",
                IsPillarOrder = true,
                GeneratedUtc = DateTime.UtcNow
            };

            if (pillars.Count == 0)
            {
                result.Reason = "No Remnant pillars discovered yet. Explore the area to locate pillars.";
                return result;
            }

            // 2. Identify proliferation capabilities for each pillar:
            // Rules from game mechanics & user instructions:
            // 1) "รูนฟ้าไม่ว่าจะซ้ำไม่ซ้ำก็จะสืบทอดไม่ได้": Blue runes NEVER proliferate (whether duplicate or unique).
            // 2) "รูนที่สืบทอดมันจะซ้ำไม่ได้": Runes already in the proliferated chain cannot be inherited again (no duplicate stacks).
            // 3) "การเลียงควรรูเลียงจากน้อยไปมาก ยกเว้นจะเจอ รูน SSS S A": HoleCount ascending except SSS/S/A.
            // 4) "เสาที่มีจำนวนรูมากที่สุดเป็นเสาสุดท้ายเสมอ": Max hole count pillar is final stack.
            int maxHoles = pillars.Max(p => p.HoleCount);

            // Count frequency of each non-blue rune across all pillars on this map
            var runeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in pillars)
            {
                var r = GetProliferatingRuneName(p);
                if (!string.IsNullOrEmpty(r) && !IsBlueRune(r))
                {
                    runeCounts[r] = runeCounts.GetValueOrDefault(r, 0) + 1;
                }
            }

            // Determine which pillars hold UNIQUE high-tier runes (count == 1).
            // If a rune appears on multiple pillars (count > 1), only ONE pillar needs to proliferate it early (the one with fewer holes),
            // while the duplicate instance cannot proliferate and can serve as a later stacking pillar or even the final stack!
            bool IsUniqueHighTierPillar(ExpeditionTarget target)
            {
                var r = GetProliferatingRuneName(target);
                if (string.IsNullOrEmpty(r) || IsBlueRune(r)) return false;
                var tier = GetBestRuneTier(target);
                if (tier is RuneTier.Golden or RuneTier.Purple_S or RuneTier.Purple_A)
                {
                    return runeCounts.GetValueOrDefault(r, 0) == 1;
                }
                return false;
            }

            // 3. Select the FINAL STACK pillar:
            // Must have max holes on the map, but should NOT waste a unique SSS/S/A proliferation by putting it last.
            // Eligible candidates: Blue rune pillars, duplicate high-tier pillars, or Purple B pillars.
            var nonUniqueHighPillars = pillars.Where(p => !IsUniqueHighTierPillar(p)).ToList();
            ExpeditionTarget final;

            if (nonUniqueHighPillars.Count > 0)
            {
                int maxEligibleHoles = nonUniqueHighPillars.Max(p => p.HoleCount);
                var finalCandidates = nonUniqueHighPillars.Where(p => p.HoleCount == maxEligibleHoles).ToList();
                final = finalCandidates
                    .OrderBy(p => IsBlueRune(GetProliferatingRuneName(p)) ? 0 : 1) // Prefer Blue runes (zero proliferation lost)
                    .ThenBy(p => p.NeedsReroll)
                    .ThenBy(p => RunePriority(p))
                    .ThenBy(p => p.EntityId)
                    .First();
            }
            else
            {
                // If every pillar on the map holds a unique SSS/S/A, pick max holes and lowest tier (A over S, S over SSS)
                var finalCandidates = pillars.Where(p => p.HoleCount == maxHoles).ToList();
                final = finalCandidates
                    .OrderByDescending(p => GetRuneTierRank(p))
                    .ThenBy(p => p.EntityId)
                    .First();
            }

            pillars.Remove(final);

            // 4. Partition remaining pillars into Proliferation Wave vs Stacking Wave:
            // - Proliferation Wave: Unique non-blue runes (SSS -> S -> A -> Purple B)
            //   If a high-tier rune appears on multiple pillars, only the one with FEWEST holes enters Proliferation Wave.
            // - Stacking Wave: Non-proliferating pillars (Blue runes, and duplicate high-tier pillars)
            //   Ordered strictly by HoleCount ascending (น้อยไปมาก: 2 -> 3 -> 4...)
            var prolifWave = new List<ExpeditionTarget>();
            var stackWave = new List<ExpeditionTarget>();
            var registeredRunes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Sort candidate pillars by tier rank first, then by HoleCount ascending
            var sortedCandidates = pillars
                .OrderBy(p => GetRuneTierRank(p))
                .ThenBy(p => p.HoleCount)
                .ThenBy(p => p.EntityId)
                .ToList();

            foreach (var p in sortedCandidates)
            {
                var r = GetProliferatingRuneName(p);
                bool isBlue = IsBlueRune(r) || p.ProliferatedRuneTier == RuneTier.Blue_C;

                if (!isBlue && !string.IsNullOrEmpty(r) && !registeredRunes.Contains(r))
                {
                    // First time encountering this non-blue rune: it will proliferate!
                    prolifWave.Add(p);
                    registeredRunes.Add(r);
                }
                else
                {
                    // Blue rune (cannot proliferate) or duplicate rune (already proliferated earlier):
                    // Joins the Stacking Wave!
                    stackWave.Add(p);
                }
            }

            // Stacking wave is ordered strictly by HoleCount ascending (น้อยไปมาก)
            stackWave = stackWave
                .OrderBy(p => p.HoleCount)
                .ThenBy(p => p.EntityId)
                .ToList();

            // Combined ordered list: Proliferation Wave -> Stacking Wave -> Final Stack
            var ordered = prolifWave
                .Concat(stackWave)
                .Append(final)
                .ToList();

            // 5. Track active proliferation chain and mark duplicate statuses
            var activeChain = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var runningStack = new List<string>();
            int rerollCount = 0;

            for (int i = 0; i < ordered.Count; i++)
            {
                var pillar = ordered[i];
                bool isFinal = (i == ordered.Count - 1);
                var r = GetProliferatingRuneName(pillar);
                bool isBlue = IsBlueRune(r) || pillar.ProliferatedRuneTier == RuneTier.Blue_C;

                var gained = new List<string>();

                if (isFinal)
                {
                    // Final stack absorbs all bonuses, does not proliferate
                    pillar.IsDuplicateProliferation = !isBlue && !string.IsNullOrEmpty(r) && activeChain.Contains(r);
                }
                else if (isBlue || string.IsNullOrEmpty(r))
                {
                    // Blue runes NEVER proliferate (neither unique nor duplicate)
                    pillar.IsDuplicateProliferation = false;
                }
                else if (activeChain.Contains(r))
                {
                    // Duplicate rune: cannot be inherited again!
                    pillar.IsDuplicateProliferation = true;
                }
                else
                {
                    // Unique non-blue rune: proliferates!
                    pillar.IsDuplicateProliferation = false;
                    activeChain.Add(r);
                    runningStack.Add(r);
                    gained.Add(r);
                }

                if (pillar.NeedsReroll)
                {
                    rerollCount++;
                }

                result.Placements.Add(new ProposedPlacement
                {
                    Step = i + 1,
                    GridPosition = pillar.GridPosition,
                    WorldPosition = pillar.WorldPosition,
                    TerrainHeight = pillar.TerrainHeight,
                    CoveredTargets = new List<ExpeditionTarget> { pillar },
                    GainedRunes = gained
                });
            }

            result.ProliferatedStack = new List<string>(runningStack);
            result.RerollRemnantCount = rerollCount;

            string finalRune = EffectiveRuneName(final);
            result.Reason = $"Pillar Order Advisor: {ordered.Count} Remnant pillars sequenced. Max holes = {maxHoles}. Final #{ordered.Count} is {finalRune} ({final.HoleCount} holes).";
            result.Warnings.Add($"Final stack pillar: #{ordered.Count} ({final.HoleCount} holes, {finalRune}). Keep this pillar for the end to reap the full accumulated rune multiplier!");

            if (rerollCount > 0)
            {
                result.Warnings.Add($"{rerollCount} Remnant(s) require reroll before detonating (low-value golden slot).");
            }

            return result;
        }

        public static bool IsBlueRune(string? runeName)
        {
            if (string.IsNullOrWhiteSpace(runeName)) return true;
            return RemnantRuneAdvisor.GetRuneTier(runeName) == RuneTier.Blue_C;
        }

        public static string GetProliferatingRuneName(ExpeditionTarget target)
        {
            if (IsOpulent(target)) return "Opulent";

            if (!string.IsNullOrWhiteSpace(target.ProliferatedRuneName))
            {
                var name = target.ProliferatedRuneName;
                foreach (var r in RemnantRuneAdvisor.RuneNames)
                {
                    if (string.Equals(name, r, StringComparison.OrdinalIgnoreCase)) return r;
                }
                foreach (var r in RemnantRuneAdvisor.RuneNames)
                {
                    if (name.Contains(r, StringComparison.OrdinalIgnoreCase)) return r;
                }
            }

            if (!string.IsNullOrWhiteSpace(target.GoldenRuneCandidate) && target.GoldenRuneCandidate != "Unknown")
            {
                foreach (var r in RemnantRuneAdvisor.RuneNames)
                {
                    if (target.GoldenRuneCandidate.Contains(r, StringComparison.OrdinalIgnoreCase)) return r;
                }
            }

            if (target.IsAnchorInGoldenSlot && !string.IsNullOrWhiteSpace(target.AnchorRuneName))
            {
                foreach (var r in RemnantRuneAdvisor.RuneNames)
                {
                    if (string.Equals(target.AnchorRuneName, r, StringComparison.OrdinalIgnoreCase)) return r;
                }
            }

            return string.Empty;
        }

        private static bool IsOpulent(ExpeditionTarget target)
        {
            return target.AnchorRuneName.Equals("Opulent", StringComparison.OrdinalIgnoreCase) ||
                   target.GoldenRuneCandidate.Contains("Opulent", StringComparison.OrdinalIgnoreCase) ||
                   target.ProliferatedRuneName.Equals("Opulent", StringComparison.OrdinalIgnoreCase);
        }

        private static string EffectiveRuneName(ExpeditionTarget target)
        {
            var prolif = GetProliferatingRuneName(target);
            if (!string.IsNullOrWhiteSpace(prolif)) return prolif;
            if (!string.IsNullOrWhiteSpace(target.ProliferatedRuneName)) return target.ProliferatedRuneName;
            if (!string.IsNullOrWhiteSpace(target.GoldenRuneCandidate)) return target.GoldenRuneCandidate;
            return target.AnchorRuneName;
        }

        private static RuneTier GetBestRuneTier(ExpeditionTarget target)
        {
            if (IsOpulent(target)) return RuneTier.Golden;
            var rune = GetProliferatingRuneName(target);
            if (!string.IsNullOrEmpty(rune))
            {
                return RemnantRuneAdvisor.GetRuneTier(rune);
            }
            return target.ProliferatedRuneTier;
        }

        private static int GetRuneTierRank(ExpeditionTarget target)
        {
            // SSS / S / A take top priority before hole count ordering:
            // SSS (Opulent / Golden): Rank 1
            // S-tier (Power, Death, Bond, Oath): Rank 2
            // A-tier (Time, Rebirth): Rank 3
            // Purple B: Rank 4
            // Regular pillars (Blue C, etc.): Rank 999 (sorted by hole count ascending)
            var bestTier = GetBestRuneTier(target);
            return bestTier switch
            {
                RuneTier.Golden => 1,   // SSS
                RuneTier.Purple_S => 2, // S
                RuneTier.Purple_A => 3, // A
                RuneTier.Purple_B => 4, // B
                _ => 999                // Regular / Blue (sorted by hole count ascending)
            };
        }

        private static float RunePriority(ExpeditionTarget target)
        {
            if (IsOpulent(target)) return 10_000f;
            var bestTier = GetBestRuneTier(target);
            return bestTier switch
            {
                RuneTier.Golden => 9_000f,
                RuneTier.Purple_S => 2_000f,
                RuneTier.Purple_A => 1_200f,
                RuneTier.Purple_B => 600f,
                RuneTier.Blue_C => 100f,
                _ => 50f
            };
        }
    }
}
