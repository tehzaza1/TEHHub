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
            int pillarCount = targets.Count(t => t.Kind is TargetKind.RemnantPillar or TargetKind.VerisiumSentinel);
            var finalStackPillar = FindFinalStackPillar(targets, settings);
            // Bridge bombs consume slots too. Do not cap the route by pillar count:
            // a remote pillar may need several legal fuse segments before it can be hit.
            int maxCount = Math.Max(1, settings.MaxExplosiveBudget - placed.Count);
            var best = SearchRouteBeam(startGrid, startWorld, placed, targets, candidates, terrain, settings, maxCount, finalStackPillar)
                ?? new RouteEvaluation { Profile = settings.Profile.ToString() };
            var coveredPillars = best.Placements.SelectMany(p => p.CoveredTargets)
                .Where(t => t.Kind is TargetKind.RemnantPillar or TargetKind.VerisiumSentinel)
                .Select(t => t.EntityId).Distinct().Count();
            best.Reason = $"Bounded global route search: {coveredPillars}/{pillarCount} pillars via {best.Placements.Count} explosives ({maxCount * 32} beam paths).";
            AppendUncoveredPillarDiagnostics(best, targets, startGrid, placed.Count, terrain, settings, finalStackPillar);
            return best;
        }

        private static IEnumerable<Vector3> BuildBridgeCandidates(
            Vector3 anchor,
            IEnumerable<ExpeditionTarget> targets,
            HashSet<uint> covered,
            ExpeditionPlannerSettings settings)
        {
            float maxStep = MathF.Max(12f, settings.MaxPlacementRangeGrid - 3f);
            float[] stepFractions = [1.0f, 0.65f];
            foreach (var target in targets.Where(t => !covered.Contains(t.EntityId) &&
                         t.Kind is (TargetKind.RemnantPillar or TargetKind.VerisiumSentinel)))
            {
                var direction = new Vector2(target.GridPosition.X - anchor.X, target.GridPosition.Y - anchor.Y);
                float dist = direction.Length();
                if (dist < 1f) continue;
                direction /= dist;
                var perpendicular = new Vector2(-direction.Y, direction.X);
                foreach (var frac in stepFractions)
                {
                    float stepLength = MathF.Min(maxStep * frac, MathF.Max(12f, dist - 10f));
                    foreach (var sideways in new[] { 0f, 10f, -10f })
                    {
                        var point = new Vector2(anchor.X, anchor.Y) + (direction * stepLength) + (perpendicular * sideways);
                        yield return new Vector3(point.X, point.Y, target.GridPosition.Z);
                    }
                }
            }
        }

        private sealed class BeamState
        {
            public Vector3 Anchor { get; init; }
            public List<Vector3> Committed { get; init; } = new();
            public List<ProposedPlacement> Placements { get; init; } = new();
            public HashSet<uint> Covered { get; init; } = new();
            public int Bridges { get; init; }
        }

        private static RouteEvaluation? SearchRouteBeam(
            Vector3 startGrid, Vector3 startWorld, List<PlacedBombInfo> placed, List<ExpeditionTarget> targets,
            List<Vector3> staticCandidates, ExpeditionTerrainSnapshot? terrain, ExpeditionPlannerSettings settings,
            int maxSteps, ExpeditionTarget? finalStackPillar)
        {
            const int beamWidth = 32;
            var pillarTargets = targets.Where(t => t.Kind is TargetKind.RemnantPillar or TargetKind.VerisiumSentinel).ToList();
            var initialAnchor = placed.Count > 0 ? placed[^1].GridPosition : startGrid;
            var beam = new List<BeamState>
            {
                new() { Anchor = initialAnchor, Committed = placed.Select(p => p.GridPosition).ToList() }
            };
            var terminals = new List<BeamState>();
            BeamState? bestPrefix = beam[0];
            for (int depth = 1; depth <= maxSteps && beam.Count > 0; depth++)
            {
                var next = new List<BeamState>();
                foreach (var state in beam)
                {
                    bool hasSuccessor = false;
                    var candidatePoints = staticCandidates
                        .Concat(BuildBridgeCandidates(state.Anchor, pillarTargets, state.Covered, settings))
                        .Distinct();

                    foreach (var point in candidatePoints)
                    {
                        if (!LegalPlacement.IsPlaceable(point, state.Anchor, terrain, settings, pillarTargets, startGrid, state.Committed)) continue;
                        var hit = pillarTargets.Where(t => !state.Covered.Contains(t.EntityId) &&
                            Vector2.Distance(new Vector2(point.X, point.Y), new Vector2(t.GridPosition.X, t.GridPosition.Y)) <= settings.BlastRadiusGrid).ToList();
                        bool hitsFinal = finalStackPillar != null && hit.Any(t => t.EntityId == finalStackPillar.EntityId);
                        int otherUncovered = pillarTargets.Count(t => t.EntityId != finalStackPillar?.EntityId && !state.Covered.Contains(t.EntityId));

                        // The final stack pillar should receive as many stacked remnant bonuses as possible.
                        // Only allow hitting it early if all other pillars are already covered.
                        // Otherwise, defer hitting it until the route's final explosive.
                        if (hitsFinal && depth < maxSteps && otherUncovered > 0) continue;

                        if (hit.Count == 0)
                        {
                            if (depth == maxSteps) continue;
                            // A bridge point must make forward progress toward at least one uncovered target.
                            bool makesProgress = false;
                            foreach (var target in pillarTargets.Where(t => !state.Covered.Contains(t.EntityId)))
                            {
                                var before = Vector2.Distance(new Vector2(state.Anchor.X, state.Anchor.Y), new Vector2(target.GridPosition.X, target.GridPosition.Y));
                                var after = Vector2.Distance(new Vector2(point.X, point.Y), new Vector2(target.GridPosition.X, target.GridPosition.Y));
                                if (after < before - 1f)
                                {
                                    makesProgress = true;
                                    break;
                                }
                            }
                            if (!makesProgress) continue;
                        }

                        var covered = new HashSet<uint>(state.Covered);
                        foreach (var target in hit) covered.Add(target.EntityId);
                        var placements = new List<ProposedPlacement>(state.Placements)
                        {
                            new()
                            {
                                Step = placed.Count + depth, GridPosition = point,
                                WorldPosition = startWorld + ((point - startGrid) * 10.87f),
                                WireDistance = Vector2.Distance(new Vector2(state.Anchor.X, state.Anchor.Y), new Vector2(point.X, point.Y)),
                                CoveredTargets = hit
                            }
                        };
                        var successor = new BeamState
                        {
                            Anchor = point,
                            Committed = state.Committed.Append(point).ToList(),
                            Placements = placements,
                            Covered = covered,
                            Bridges = state.Bridges + (hit.Count == 0 ? 1 : 0)
                        };
                        if (hitsFinal) terminals.Add(successor); else next.Add(successor);
                        hasSuccessor = true;
                    }

                    // A* terrain detour fallback: when normal candidates cannot extend this state,
                    // search for walkable detour waypoints toward uncovered pillars.
                    if (!hasSuccessor && depth < maxSteps)
                    {
                        var detourTargets = pillarTargets
                            .Where(t => !state.Covered.Contains(t.EntityId))
                            .OrderBy(t => t.EntityId == finalStackPillar?.EntityId ? 1 : 0)
                            .ThenBy(t => Vector2.Distance(new Vector2(state.Anchor.X, state.Anchor.Y), new Vector2(t.GridPosition.X, t.GridPosition.Y)))
                            .Take(3);

                        foreach (var detourTarget in detourTargets)
                        {
                            if (LegalPlacement.TryFindTerrainDetourWaypoint(
                                    terrain,
                                    new Vector2(state.Anchor.X, state.Anchor.Y),
                                    new Vector2(detourTarget.GridPosition.X, detourTarget.GridPosition.Y),
                                    settings.MaxPlacementRangeGrid,
                                    out var waypoint))
                            {
                                var point = new Vector3(waypoint.X, waypoint.Y, detourTarget.GridPosition.Z);
                                if (LegalPlacement.IsPlaceable(point, state.Anchor, terrain, settings, pillarTargets, startGrid, state.Committed))
                                {
                                    next.Add(new BeamState
                                    {
                                        Anchor = point,
                                        Committed = state.Committed.Append(point).ToList(),
                                        Placements = state.Placements.Append(new ProposedPlacement
                                        {
                                            Step = placed.Count + depth, GridPosition = point,
                                            WorldPosition = startWorld + ((point - startGrid) * 10.87f),
                                            WireDistance = Vector2.Distance(new Vector2(state.Anchor.X, state.Anchor.Y), new Vector2(point.X, point.Y))
                                        }).ToList(),
                                        Covered = new HashSet<uint>(state.Covered),
                                        Bridges = state.Bridges + 1
                                    });
                                    break;
                                }
                            }
                        }
                    }
                }
                beam = PruneBeam(next, pillarTargets, finalStackPillar, beamWidth);
                var prefix = beam.OrderByDescending(s => s.Covered.Count).ThenBy(s => FirstRuneStep(s.Placements, "Opulent")).ThenBy(s => s.Bridges).FirstOrDefault();
                if (prefix != null && (bestPrefix == null || prefix.Covered.Count > bestPrefix.Covered.Count ||
                    (prefix.Covered.Count == bestPrefix.Covered.Count && prefix.Bridges < bestPrefix.Bridges)))
                {
                    bestPrefix = prefix;
                }
            }
            var winner = terminals.OrderByDescending(s => s.Covered.Count).ThenBy(s => FirstRuneStep(s.Placements, "Opulent")).ThenBy(s => s.Bridges).FirstOrDefault();
            // If the final pillar is blocked or unreachable, preserve the strongest non-final chain.
            winner ??= bestPrefix;
            if (winner == null) return null;
            return new RouteEvaluation { Profile = settings.Profile.ToString(), Placements = winner.Placements, NetScore = winner.Covered.Count };
        }

        private static List<BeamState> PruneBeam(List<BeamState> states, List<ExpeditionTarget> targets, ExpeditionTarget? finalStackPillar, int width)
        {
            var unique = states
                .GroupBy(s => $"{string.Join(',', s.Covered.Order())}|{MathF.Round(s.Anchor.X / 10f)}:{MathF.Round(s.Anchor.Y / 10f)}")
                .Select(g => g.OrderBy(s => s.Bridges).First())
                .ToList();
            var chosen = unique
                .OrderByDescending(s => s.Covered.Count)
                .ThenBy(s => FirstRuneStep(s.Placements, "Opulent"))
                .ThenBy(s => s.Bridges)
                .Take(Math.Max(8, width / 2))
                .ToList();

            // Keep bridge frontiers alive for every uncovered pillar (including finalStackPillar).
            // Without this, a state walking toward a remote pillar has zero new coverage for a few
            // steps and is discarded in favour of nearby completed pillars.
            foreach (var target in targets)
            {
                foreach (var frontier in unique
                    .Where(s => !s.Covered.Contains(target.EntityId))
                    .OrderBy(s => Vector2.Distance(new Vector2(s.Anchor.X, s.Anchor.Y), new Vector2(target.GridPosition.X, target.GridPosition.Y)))
                    .ThenBy(s => s.Bridges)
                    .Take(2))
                {
                    if (!chosen.Contains(frontier)) chosen.Add(frontier);
                }
            }
            return chosen
                .OrderByDescending(s => s.Covered.Count)
                .ThenBy(s => FirstRuneStep(s.Placements, "Opulent"))
                .ThenBy(s => s.Bridges)
                .Take(width)
                .ToList();
        }

        private static int FirstRuneStep(IEnumerable<ProposedPlacement> placements, string rune)
        {
            int index = 0;
            foreach (var placement in placements)
            {
                if (placement.CoveredTargets.Any(t => RuneName(t).Equals(rune, StringComparison.OrdinalIgnoreCase))) return index;
                index++;
            }
            return int.MaxValue;
        }

        private static ExpeditionTarget? FindFinalStackPillar(IEnumerable<ExpeditionTarget> targets, ExpeditionPlannerSettings settings) => targets
            .Where(t => t.Kind is TargetKind.RemnantPillar or TargetKind.VerisiumSentinel)
            .OrderByDescending(t => t.HoleCount)
            .ThenByDescending(t => Value(t, settings))
            .FirstOrDefault();

        private static int CountCoveredPillars(RouteEvaluation route) => route.Placements.SelectMany(p => p.CoveredTargets)
            .Where(t => t.Kind is TargetKind.RemnantPillar or TargetKind.VerisiumSentinel)
            .Select(t => t.EntityId).Distinct().Count();

        private static bool EndsAt(RouteEvaluation route, ExpeditionTarget? target) => target != null && route.Placements.Count > 0 &&
            route.Placements[^1].CoveredTargets.Any(t => t.EntityId == target.EntityId);

        private static int FirstRuneStep(RouteEvaluation route, string rune)
        {
            for (int index = 0; index < route.Placements.Count; index++)
            {
                if (route.Placements[index].CoveredTargets.Any(t => RuneName(t).Equals(rune, StringComparison.OrdinalIgnoreCase))) return index;
            }
            return int.MaxValue;
        }

        private static void AppendUncoveredPillarDiagnostics(
            RouteEvaluation route,
            IEnumerable<ExpeditionTarget> targets,
            Vector3 startGrid,
            int alreadyPlaced,
            ExpeditionTerrainSnapshot? terrain,
            ExpeditionPlannerSettings settings,
            ExpeditionTarget? finalStackPillar)
        {
            var covered = route.Placements.SelectMany(p => p.CoveredTargets).Select(t => t.EntityId).ToHashSet();
            var uncovered = targets.Where(t => t.Kind is TargetKind.RemnantPillar or TargetKind.VerisiumSentinel)
                .Where(t => !covered.Contains(t.EntityId)).ToList();
            if (uncovered.Count == 0) return;

            route.Warnings.Add($"Why stopped: {uncovered.Count} pillar(s) remain outside the selected legal chain.");
            var anchors = new[] { startGrid }.Concat(route.Placements.Select(p => p.GridPosition)).ToList();
            foreach (var target in uncovered)
            {
                var distance = Vector2.Distance(new Vector2(startGrid.X, startGrid.Y), new Vector2(target.GridPosition.X, target.GridPosition.Y));
                var reachDistance = MathF.Max(0f, distance - settings.BlastRadiusGrid);
                int minimumSegments = (int)MathF.Ceiling(reachDistance / MathF.Max(1f, settings.MaxPlacementRangeGrid));
                var label = !string.IsNullOrWhiteSpace(RuneName(target)) ? RuneName(target) : target.DisplayName;
                if (minimumSegments > settings.MaxExplosiveBudget - alreadyPlaced)
                {
                    route.Warnings.Add($"{label} ({target.HoleCount} slots): needs at least {minimumSegments} fuse segments from the detonator; only {settings.MaxExplosiveBudget - alreadyPlaced} remain.");
                }
                else if (target.EntityId == finalStackPillar?.EntityId)
                {
                    route.Warnings.Add($"{label} ({target.HoleCount} slots): mandatory final pillar has no complete legal chain (terrain, camp clearance, or fuse range blocked it).");
                }
                else
                {
                    route.Warnings.Add($"{label} ({target.HoleCount} slots): {DiagnosePillarExclusion(target, anchors, startGrid, terrain, settings, finalStackPillar)}");
                }
            }
        }

        private static string DiagnosePillarExclusion(ExpeditionTarget target, IReadOnlyList<Vector3> anchors, Vector3 detonator, ExpeditionTerrainSnapshot? terrain, ExpeditionPlannerSettings settings, ExpeditionTarget? finalStackPillar)
        {
            var candidatePoints = new List<Vector3>();
            float radius = MathF.Max(14f, settings.BlastRadiusGrid - 2f);
            for (int i = 0; i < 12; i++)
            {
                float angle = i * MathF.PI / 6f;
                candidatePoints.Add(target.GridPosition + new Vector3(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius, 0));
            }

            var clearGround = candidatePoints.Where(p =>
                LegalPlacement.HasWalkableClearance(terrain, p.X, p.Y, settings.BombClearanceRadiusGrid) &&
                Vector2.Distance(new Vector2(p.X, p.Y), new Vector2(detonator.X, detonator.Y)) >= settings.CampExclusionRadiusGrid).ToList();
            if (clearGround.Count == 0) return "every tested blast point is blocked by terrain clearance or the campsite exclusion zone";

            var ranged = clearGround.Where(p => anchors.Any(a => Vector2.Distance(new Vector2(a.X, a.Y), new Vector2(p.X, p.Y)) <= settings.MaxPlacementRangeGrid)).ToList();
            if (ranged.Count == 0) return "it still needs a bridge chain; no selected anchor is within one legal fuse segment";

            var visible = ranged.Where(p => anchors.Any(a => LegalPlacement.IsLineClearOfTerrain(terrain, new Vector2(a.X, a.Y), new Vector2(p.X, p.Y), out _))).ToList();
            if (visible.Count == 0) return "terrain blocks every in-range fuse segment; an A* detour is required";

            return $"it was excluded to preserve a legal finish at the {finalStackPillar?.HoleCount ?? 0}-slot pillar";
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


