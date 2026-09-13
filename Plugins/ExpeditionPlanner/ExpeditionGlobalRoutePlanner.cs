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
            var candidates = BuildCandidates(targets);
            var best = new RouteEvaluation { Profile = settings.Profile.ToString() };
            var random = new Random(17);
            int count = Math.Max(1, settings.MaxExplosiveBudget - placed.Count);
            for (int attempt = 0; attempt < 96; attempt++)
            {
                var route = BuildRoute(startGrid, startWorld, placed, targets, candidates, area, settings, count, random);
                if (route.NetScore > best.NetScore) best = route;
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
            for (int step = 1; step <= count; step++)
            {
                Vector3? best = null; float bestScore = float.NegativeInfinity; List<ExpeditionTarget>? hitBest = null;
                foreach (var point in candidates.OrderBy(_ => random.Next()).Take(160))
                {
                    if (!LegalPlacement.IsPlaceable(point, anchor, area, settings, targets, startGrid, committed)) continue;
                    var hit = targets.Where(t => !covered.Contains(t.EntityId) && Vector2.Distance(new Vector2(point.X, point.Y), new Vector2(t.GridPosition.X, t.GridPosition.Y)) <= settings.BlastRadiusGrid).ToList();
                    if (hit.Count == 0 && step == count) continue;
                    float score = hit.Sum(t => Value(t, settings));
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
            return result;
        }

        private static List<Vector3> BuildCandidates(List<ExpeditionTarget> targets)
        {
            var result = new List<Vector3>();
            foreach (var t in targets)
            {
                if (t.Kind is TargetKind.RemnantPillar or TargetKind.VerisiumSentinel)
                    for (int i = 0; i < 8; i++) { float a = i * MathF.PI / 4; result.Add(t.GridPosition + new Vector3(MathF.Cos(a) * 14, MathF.Sin(a) * 14, 0)); }
                else result.Add(t.GridPosition);
            }
            return result.Distinct().ToList();
        }

        private static float Value(ExpeditionTarget t, ExpeditionPlannerSettings s)
        {
            var rune = !string.IsNullOrWhiteSpace(t.ProliferatedRuneName)
                ? t.ProliferatedRuneName
                : t.AnchorRuneName;

            // Farming order: establish the largest loot multiplier first, then the
            // strongest proliferated modifiers. Lower-value runes naturally drop out
            // of a short explosive budget because their score cannot beat these picks.
            if (rune.Equals("Opulent", StringComparison.OrdinalIgnoreCase)) return 15_000f;
            if (rune.Equals("Bond", StringComparison.OrdinalIgnoreCase)) return 10_000f;
            if (rune.Equals("Oath", StringComparison.OrdinalIgnoreCase)) return 8_000f;
            if (rune.Equals("Power", StringComparison.OrdinalIgnoreCase)) return 6_000f;

            return t.ProliferatedRuneTier switch
            {
                RuneTier.Purple_S => 3_500f,
                RuneTier.Purple_A => 1_200f,
                RuneTier.Purple_B => 500f,
                _ => t.BaseWeight > 0 ? t.BaseWeight : 20f,
            };
        }
    }
}


