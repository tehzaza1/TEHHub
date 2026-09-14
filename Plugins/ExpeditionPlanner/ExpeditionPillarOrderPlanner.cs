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

            // 3. Dynamically discover the maximum hole count across all pillars on this map (never hardcode 7)
            int maxHoles = pillars.Max(p => p.HoleCount);

            // The pillar with the highest hole count is ALWAYS the final pillar ("Final stack pillar").
            // It absorbs all accumulated multipliers and bonuses from all previous pillars in the chain.
            // If multiple pillars have maxHoles, pick the one that does not proliferate (or has lower tier proliferation),
            // so pillars with powerful proliferating buffs (like Opulent or Purple S-tier) detonate earlier.
            var maxHoleCandidates = pillars.Where(p => p.HoleCount == maxHoles).ToList();
            var final = maxHoleCandidates
                .OrderBy(p => IsOpulent(p))       // Opulent should NOT be final if another candidate exists
                .ThenBy(p => p.CanProliferate)     // Prefer non-proliferating for final
                .ThenBy(p => RunePriority(p))      // Prefer lower tier for final
                .ThenBy(p => p.EntityId)
                .First();

            pillars.Remove(final);

            // Order the preceding pillars (1 to N-1):
            // - Opulent / proliferating runes first (Opulent always #1)
            // - Pillars that contribute beneficial stacks to subsequent pillars
            // - Higher proliferation tiers earlier (Golden > Purple S > A > B)
            // - More golden slots earlier
            // - Smaller hole count earlier among buff pillars (saving bigger hole pillars for higher stacks)
            var ordered = pillars
                .OrderByDescending(p => IsOpulent(p))
                .ThenByDescending(p => p.CanProliferate)
                .ThenByDescending(p => RunePriority(p))
                .ThenByDescending(p => p.GoldenSlotIndices.Count)
                .ThenBy(p => p.HoleCount)
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
            result.Reason = $"Pillar Order Advisor: {ordered.Count} Remnant pillars sequenced. Max holes on map = {maxHoles}. Final #{ordered.Count} is {finalRune} ({final.HoleCount} holes).";
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
                   target.GoldenRuneCandidate.Equals("Opulent", StringComparison.OrdinalIgnoreCase) ||
                   target.ProliferatedRuneName.Equals("Opulent", StringComparison.OrdinalIgnoreCase);
        }

        private static string EffectiveRuneName(ExpeditionTarget target)
        {
            if (!string.IsNullOrWhiteSpace(target.ProliferatedRuneName)) return target.ProliferatedRuneName;
            if (!string.IsNullOrWhiteSpace(target.GoldenRuneCandidate)) return target.GoldenRuneCandidate;
            return target.AnchorRuneName;
        }

        private static float RunePriority(ExpeditionTarget target)
        {
            if (IsOpulent(target)) return 10_000f;
            return target.ProliferatedRuneTier switch
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
