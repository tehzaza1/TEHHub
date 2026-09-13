namespace ExpeditionPlanner
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Numerics;

    // Global, whole-route search inspired by ExpeditionIcons: evaluate complete legal routes,
    // then retain the highest-value route rather than committing a greedy next point.
    internal static class ExpeditionGlobalRoutePlanner
    {
        public static RouteEvaluation Solve(Vector3 startGrid, Vector3 startWorld, List<PlacedBombInfo> placed, List<ExpeditionTarget> targets, ExpeditionTerrainSnapshot? terrain, ExpeditionPlannerSettings settings)
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
                // ExpeditionIcons evolves a bounded pool of complete paths. Keep this
                // similarly bounded; 12 seeds per length is enough for pillar-only mode
                // and cannot monopolize a CPU core for seconds.
                for (int attempt = 0; attempt < 12; attempt++)
                {
                    var route = BuildRoute(startGrid, startWorld, placed, targets, candidates, terrain, settings, count, random);
                    if (route.NetScore > best.NetScore) best = route;
                }
            }
            var coveredPillars = best.Placements.SelectMany(p => p.CoveredTargets)
                .Where(t => t.Kind is TargetKind.RemnantPillar or TargetKind.VerisiumSentinel)
                .Select(t => t.EntityId).Distinct().Count();
            best.Reason = $"Bounded global route search: {coveredPillars}/{pillarCount} pillars via {best.Placements.Count} explosives ({maxCount * 12} route seeds).";
            return best;
        }

        private static RouteEvaluation BuildRoute(Vector3 startGrid, Vector3 startWorld, List<PlacedBombInfo> placed, List<ExpeditionTarget> targets, List<Vector3> candidates, ExpeditionTerrainSnapshot? terrain, ExpeditionPlannerSettings settings, int count, Random random)
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
                // A pillar can be further than one fuse segment. ExpeditionIcons builds
                // paths by stepping from the current endpoint, so add forward bridge
                // points from the current anchor instead of requiring every placement
                // to be in the ring around a pillar.
                var routeCandidates = candidates
                    .Concat(BuildBridgeCandidates(anchor, targets, covered, settings))
                    .Distinct()
                    .OrderBy(_ => random.Next());
                foreach (var point in routeCandidates)
                {
                    if (!LegalPlacement.IsPlaceable(point, anchor, terrain, settings, targets, startGrid, committed)) continue;
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
                    // Score only new pillar coverage and its usable rune chain. A
                    // bridge has no value by itself; it exists only to reach a pillar.
                    float score = hit.Sum(t => Value(t, settings));
                    score += hit.Count * 10_000f;
                    bool hasOpulent = HasCoveredRune(targets, covered, "Opulent");
                    if (hit.Any(t => RuneName(t).Equals("Opulent", StringComparison.OrdinalIgnoreCase)) && !hasOpulent) score += 12_000f;
                    // The farm plan values a continuing proliferation chain over the local
                    // modifier text. Opening a propagating pillar early has more remaining
                    // explosions to carry its rune forward, so it earns a larger bonus.
                    int remainingExplosions = count - step;
                    score += hit.Count(t => t.CanProliferate) * (8_000f + (remainingExplosions * 750f));
                    if (finalStackPillar != null && hit.Any(t => t.EntityId == finalStackPillar.EntityId))
                    {
                        score += step == count ? 8_000f : 0f;
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
            // A route that cannot yet reach the widest pillar is still useful: it
            // shows the legal first bridge/stack steps. Rejecting it made Calculate
            // appear to do nothing on maps where that pillar needs multiple fuses.
            return result;
        }

        private static IEnumerable<Vector3> BuildBridgeCandidates(
            Vector3 anchor,
            IEnumerable<ExpeditionTarget> targets,
            HashSet<uint> covered,
            ExpeditionPlannerSettings settings)
        {
            float stepLength = MathF.Max(12f, settings.MaxPlacementRangeGrid - 3f);
            foreach (var target in targets.Where(t => !covered.Contains(t.EntityId) && t.Kind is TargetKind.RemnantPillar or TargetKind.VerisiumSentinel))
            {
                var direction = new Vector2(target.GridPosition.X - anchor.X, target.GridPosition.Y - anchor.Y);
                if (direction.LengthSquared() < 1f) continue;
                direction = Vector2.Normalize(direction);
                var perpendicular = new Vector2(-direction.Y, direction.X);
                foreach (var sideways in new[] { 0f, 10f, -10f })
                {
                    var point = new Vector2(anchor.X, anchor.Y) + (direction * stepLength) + (perpendicular * sideways);
                    yield return new Vector3(point.X, point.Y, target.GridPosition.Z);
                }
            }
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


