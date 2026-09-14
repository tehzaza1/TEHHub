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

            // 2. Separate regular pillars from top-tier (SSS / S / A) runes:
            // Requirement: "การเลียงควรรูเลียงจากน้อยไปมาก ยกเว้นจะเจอ รูน SSS S A"
            // - SSS, S, and A pillars MUST detonate early to proliferate their powerful multipliers to all subsequent pillars.
            // - The final stack pillar should be selected from the regular pillars with the maximum hole count.
            var regularPillars = pillars.Where(p => GetRuneTierRank(p) >= 999).ToList();
            ExpeditionTarget final;
            int maxHoles = pillars.Max(p => p.HoleCount);

            if (regularPillars.Count > 0)
            {
                int maxRegularHoles = regularPillars.Max(p => p.HoleCount);
                var finalCandidates = regularPillars.Where(p => p.HoleCount == maxRegularHoles).ToList();
                final = finalCandidates
                    .OrderBy(p => p.CanProliferate)     // Prefer non-proliferating for final
                    .ThenBy(p => p.NeedsReroll)          // Prefer not needing reroll for final
                    .ThenBy(p => RunePriority(p))        // Prefer lower priority for final
                    .ThenBy(p => p.EntityId)
                    .First();
            }
            else
            {
                // If all pillars on the map are top-tier (SSS / S / A), pick the one with max holes and lowest tier
                var finalCandidates = pillars.Where(p => p.HoleCount == maxHoles).ToList();
                final = finalCandidates
                    .OrderByDescending(p => GetRuneTierRank(p)) // Lower tier (A over S, S over SSS)
                    .ThenBy(p => p.CanProliferate)
                    .ThenBy(p => p.EntityId)
                    .First();
            }

            pillars.Remove(final);

            // 3. Order preceding pillars (1 to N-1):
            // - SSS (Rank 1: Opulent) -> S (Rank 2: Power, Death, Bond, Oath) -> A (Rank 3: Time, Rebirth)
            // - Regular pillars ordered by HoleCount ascending (น้อยไปมาก: 2 -> 3 -> 4...)
            // - Final stack pillar appended at the end
            var ordered = pillars
                .OrderBy(p => GetRuneTierRank(p))
                .ThenBy(p => p.HoleCount)
                .ThenBy(p => p.CanProliferate ? 0 : 1)
                .ThenBy(p => p.EntityId)
                .Append(final)
                .ToList();

            var runningStack = new List<string>();
            int rerollCount = 0;

            for (int i = 0; i < ordered.Count; i++)
            {
                var pillar = ordered[i];
                bool isFinal = (i == ordered.Count - 1);
                var effectiveRune = EffectiveRuneName(pillar);

                var gained = new List<string>();
                if (!string.IsNullOrWhiteSpace(effectiveRune) && pillar.CanProliferate && !isFinal)
                {
                    gained.Add(effectiveRune);
                    runningStack.Add(effectiveRune);
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

        private static bool IsOpulent(ExpeditionTarget target)
        {
            return target.AnchorRuneName.Equals("Opulent", StringComparison.OrdinalIgnoreCase) ||
                   target.GoldenRuneCandidate.Contains("Opulent", StringComparison.OrdinalIgnoreCase) ||
                   target.ProliferatedRuneName.Equals("Opulent", StringComparison.OrdinalIgnoreCase);
        }

        private static string EffectiveRuneName(ExpeditionTarget target)
        {
            if (!string.IsNullOrWhiteSpace(target.ProliferatedRuneName)) return target.ProliferatedRuneName;
            if (!string.IsNullOrWhiteSpace(target.GoldenRuneCandidate)) return target.GoldenRuneCandidate;
            return target.AnchorRuneName;
        }

        private static RuneTier GetBestRuneTier(ExpeditionTarget target)
        {
            if (IsOpulent(target)) return RuneTier.Golden;
            var tier = target.ProliferatedRuneTier;

            var aTier = RemnantRuneAdvisor.GetRuneTier(target.AnchorRuneName);
            if (aTier < tier) tier = aTier;

            var pTier = RemnantRuneAdvisor.GetRuneTier(target.ProliferatedRuneName);
            if (pTier < tier) tier = pTier;

            string combined = $"{target.GoldenRuneCandidate} {target.RecommendedRuneChoice}";
            if (combined.Contains("Opulent", StringComparison.OrdinalIgnoreCase))
            {
                return RuneTier.Golden;
            }
            if (combined.Contains("Power", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("Death", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("Bond", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("Oath", StringComparison.OrdinalIgnoreCase))
            {
                if (RuneTier.Purple_S < tier) tier = RuneTier.Purple_S;
            }
            if (combined.Contains("Time", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("Rebirth", StringComparison.OrdinalIgnoreCase))
            {
                if (RuneTier.Purple_A < tier) tier = RuneTier.Purple_A;
            }

            return tier;
        }

        private static int GetRuneTierRank(ExpeditionTarget target)
        {
            // SSS / S / A take top priority before hole count ordering:
            // SSS (Opulent / Golden): Rank 1
            // S-tier (Power, Death, Bond, Oath): Rank 2
            // A-tier (Time, Rebirth): Rank 3
            // Regular pillars (B-tier, Blue C, etc.): Rank 999 (sorted by hole count ascending)
            var bestTier = GetBestRuneTier(target);
            return bestTier switch
            {
                RuneTier.Golden => 1,   // SSS
                RuneTier.Purple_S => 2, // S
                RuneTier.Purple_A => 3, // A
                _ => 999                // Regular (sorted by hole count ascending)
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
