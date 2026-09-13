namespace ExpeditionPlanner
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Numerics;
    using TEHhub.RemoteObjects.States.InGameStateObjects;

    // Global, whole-route search inspired by ExpeditionIcons: evaluate complete legal routes,
    // then retain the highest-value route rather than committing a greedy next point.
    internal static class ExpeditionGlobalRoutePlanner
    {
        public static RouteEvaluation Solve(Vector3 startGrid, Vector3 startWorld, List<PlacedBombInfo> placed, List<ExpeditionTarget> targets, AreaInstance area, ExpeditionPlannerSettings settings)
        {
            var candidates = BuildCandidates(targets, settings);
            var best = new RouteEvaluation { Profile = settings.Profile.ToString() };
            var random = new Random(17);
            int pillarCount = targets.Count(t => t.Kind is TargetKind.RemnantPillar or TargetKind.VerisiumSentinel);
            int maxCount = Math.Max(1, Math.Min(settings.MaxExplosiveBudget - placed.Count, pillarCount));
            // The stack receiver must be the last placement actually chosen, not an
            // artificial "bomb #20". Try every usable route length and retain the best.
            for (int count = 1; count <= maxCount; count++)
            {
                for (int attempt = 0; attempt < 32; attempt++)
                {
                    var route = BuildRoute(startGrid, startWorld, placed, targets, candidates, area, settings, count, random);
                    if (route.NetScore > best.NetScore) best = route;
                }
            }
            best.Reason = $"Global route search ({best.Placements.Count} bombs, 96 full-route candidates).";
            return best;
        }

        private static RouteEvaluation BuildRoute(Vector3 startGrid, Vector3 startWorld, List<PlacedBombInfo> placed, List<ExpeditionTarget> targets, List<Vector3> candidates, AreaInstance area, ExpeditionPlannerSettings settings, int count, Random random)
        {
            var result = new RouteEvaluation { Profile = settings.Profile.ToString() };
            var covered = new HashSet<uint>();
            var committed = placed.Select(x => x.GridPosition).ToList();
            var anchor = placed.Count > 0 ? placed[^1].GridPosition : startGrid;
            var finalStackPillar = targets
                .Where(t => t.Kind is TargetKind.RemnantPillar or TargetKind.VerisiumSentinel)
                .OrderByDescending(t => t.HoleCount)
                .ThenByDescending(t => Value(t, settings))
                .FirstOrDefault();
            for (int step = 1; step <= count; step++)
            {
                Vector3? best = null; float bestScore = float.NegativeInfinity; List<ExpeditionTarget>? hitBest = null;
                foreach (var point in candidates.OrderBy(_ => random.Next()).Take(160))
                {
                    if (!LegalPlacement.IsPlaceable(point, anchor, area, settings, targets, startGrid, committed)) continue;
                    var hit = targets.Where(t => !covered.Contains(t.EntityId) &&
                        (t.Kind is TargetKind.RemnantPillar or TargetKind.VerisiumSentinel) &&
                        Vector2.Distance(new Vector2(point.X, point.Y), new Vector2(t.GridPosition.X, t.GridPosition.Y)) <= settings.BlastRadiusGrid).ToList();
                    if (hit.Count == 0 && step == count) continue;
                    // The widest non-Opulent pillar is the stack receiver. A shared radius
                    // must not consume it early: split the two placements or leave it for
                    // the final activation.
                    if (finalStackPillar != null && step < count &&
                        !RuneName(finalStackPillar).Equals("Opulent", StringComparison.OrdinalIgnoreCase) &&
                        hit.Any(t => t.EntityId == finalStackPillar.EntityId))
                    {
                        continue;
                    }
                    float score = hit.Sum(t => Value(t, settings));
                    bool hasOpulent = HasCoveredRune(targets, covered, "Opulent");
                    if (hit.Any(t => RuneName(t).Equals("Opulent", StringComparison.OrdinalIgnoreCase)) && !hasOpulent) score += 12_000f;
                    // The farm plan values a continuing proliferation chain over the local
                    // modifier text. Opening a propagating pillar early has more remaining
                    // explosions to carry its rune forward, so it earns a larger bonus.
                    int remainingExplosions = count - step;
                    score += hit.Count(t => t.CanProliferate) * (4_000f + (remainingExplosions * 500f));
                    if (finalStackPillar != null && hit.Any(t => t.EntityId == finalStackPillar.EntityId))
                    {
                        score += step == count ? 30_000f : 0f;
                    }
                    // A whole route may use bridge points, but they must head toward an uncovered target.
                    if (hit.Count == 0)
                    {
                        var next = targets.Where(t => !covered.Contains(t.EntityId)).OrderBy(t => Vector2.Distance(new Vector2(point.X, point.Y), new Vector2(t.GridPosition.X, t.GridPosition.Y))).FirstOrDefault();
                        if (next == null) continue;
                        score = Vector2.Distance(new Vector2(anchor.X, anchor.Y), new Vector2(next.GridPosition.X, next.GridPosition.Y)) - Vector2.Distance(new Vector2(point.X, point.Y), new Vector2(next.GridPosition.X, next.GridPosition.Y));
                    }
                    if (score > bestScore) { bestScore = score; best = point; hitBest = hit; }
                }
                if (best == null) break;
                var p = best.Value; var selectedHits = hitBest!;
                result.Placements.Add(new ProposedPlacement { Step = placed.Count + step, GridPosition = p, WorldPosition = startWorld + ((p - startGrid) * 10.87f), WireDistance = Vector2.Distance(new Vector2(anchor.X, anchor.Y), new Vector2(p.X, p.Y)), CoveredTargets = selectedHits });
                foreach (var t in selectedHits) covered.Add(t.EntityId);
                result.NetScore += bestScore; committed.Add(p); anchor = p;
            }
            if (finalStackPillar != null && !covered.Contains(finalStackPillar.EntityId))
            {
                result.NetScore = float.NegativeInfinity;
            }
            return result;
        }

        private static List<Vector3> BuildCandidates(List<ExpeditionTarget> targets, ExpeditionPlannerSettings settings)
        {
            var result = new List<Vector3>();
            foreach (var t in targets)
            {
                if (t.Kind is not (TargetKind.RemnantPillar or TargetKind.VerisiumSentinel)) continue;
                // Inner points evaluate the shared blast. Outer points near the blast edge
                // let the search evaluate hitting this pillar without its overlapping neighbour.
                float[] radii = [14f, MathF.Max(14f, settings.BlastRadiusGrid - 2f)];
                foreach (var radius in radii)
                    for (int i = 0; i < 12; i++) { float a = i * MathF.PI / 6; result.Add(t.GridPosition + new Vector3(MathF.Cos(a) * radius, MathF.Sin(a) * radius, 0)); }
            }
            return result.Distinct().ToList();
        }

        private static float Value(ExpeditionTarget t, ExpeditionPlannerSettings s)
        {
            var rune = RuneName(t);

            // Farming order: establish the largest loot multiplier first, then the
            // strongest proliferated modifiers. Lower-value runes naturally drop out
            // of a short explosive budget because their score cannot beat these picks.
            if (rune.Equals("Opulent", StringComparison.OrdinalIgnoreCase)) return 15_000f;
            return t.ProliferatedRuneTier switch
            {
                RuneTier.Purple_S => 500f,
                RuneTier.Purple_A => 400f,
                RuneTier.Purple_B => 300f,
                _ => t.BaseWeight > 0 ? t.BaseWeight : 20f,
            };
        }

        private static string RuneName(ExpeditionTarget target) => !string.IsNullOrWhiteSpace(target.ProliferatedRuneName)
            ? target.ProliferatedRuneName : target.AnchorRuneName;

        private static bool HasCoveredRune(IEnumerable<ExpeditionTarget> targets, HashSet<uint> covered, string rune) =>
            targets.Any(t => covered.Contains(t.EntityId) && RuneName(t).Equals(rune, StringComparison.OrdinalIgnoreCase));
    }
}


