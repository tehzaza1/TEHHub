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
        public static RouteEvaluation Solve(
            Vector3 startGrid,
            Vector3 startWorld,
            List<PlacedBombInfo> placed,
            List<ExpeditionTarget> targets,
            ExpeditionTerrainSnapshot? terrain,
            ExpeditionPlannerSettings settings,
            bool isRealDetonator = false)
        {
            // Pre-compute which targets are already covered by placed bombs so that
            // BuildCandidates can exclude them — preventing the solver from wasting
            // additional explosives to "re-cover" a target that is already claimed.
            var initialCovered = new HashSet<uint>();
            foreach (var bomb in placed)
            {
                float r = settings.BlastRadiusGrid;
                foreach (var target in targets)
                {
                    float dx = target.GridPosition.X - bomb.GridPosition.X;
                    float dy = target.GridPosition.Y - bomb.GridPosition.Y;
                    if ((dx * dx) + (dy * dy) <= r * r)
                    {
                        initialCovered.Add(target.EntityId);
                    }
                }
            }

            var candidates = BuildCandidates(targets, terrain, settings, initialCovered);
            int pillarCount = targets.Count(t => t.Kind is TargetKind.RemnantPillar or TargetKind.VerisiumSentinel);
            int remainingBudget = settings.MaxExplosiveBudget - placed.Count;
            if (remainingBudget <= 0)
            {
                return new RouteEvaluation
                {
                    Profile = settings.Profile.ToString(),
                    Reason = "All available explosives have been placed."
                };
            }

            int maxCount = remainingBudget;
            // The final explosion carries the finished rune stack. Pick its receiver
            // from live data: most sockets first, then value as a deterministic tie-break.
            var finalStackPillar = targets
                .Where(t => t.Kind is TargetKind.RemnantPillar or TargetKind.VerisiumSentinel)
                .OrderByDescending(t => t.HoleCount)
                .ThenByDescending(t => GetTargetValue(t, settings))
                .FirstOrDefault();
            var best = SearchRouteBeam(startGrid, startWorld, placed, targets, candidates, terrain, settings, maxCount, isRealDetonator, finalStackPillar)
                ?? new RouteEvaluation { Profile = settings.Profile.ToString() };
            if (finalStackPillar != null)
            {
                best.Warnings.Add($"Terminal target: {RuneName(finalStackPillar)} ({finalStackPillar.HoleCount} slots) must be hit by explosive #{placed.Count + maxCount}.");
            }

            // Calculate ProliferationRemaining & ProliferatedStack
            for (int i = 0; i < best.Placements.Count; i++)
            {
                var p = best.Placements[i];
                int remainingBombs = Math.Max(0, remainingBudget - p.Step);
                foreach (var target in p.CoveredTargets)
                {
                    target.ProliferationRemaining = target.CanProliferate ? remainingBombs : 0;
                    var pRune = !string.IsNullOrEmpty(target.ProliferatedRuneName) ? target.ProliferatedRuneName : target.AnchorRuneName;
                    if (target.Kind is TargetKind.RemnantPillar && !string.IsNullOrEmpty(pRune) && target.CanProliferate)
                    {
                        if (!best.ProliferatedStack.Contains(pRune))
                        {
                            best.ProliferatedStack.Add(pRune);
                        }
                    }
                }
            }

            best.RerollRemnantCount = targets.Count(t => t.Kind == TargetKind.RemnantPillar && t.NeedsReroll);
            if (best.RerollRemnantCount > 0)
            {
                best.Warnings.Add($"{best.RerollRemnantCount} Remnant(s) have low-tier runes in Golden Slot. Reroll recommended before detonating!");
            }

            var coveredPillars = best.Placements.SelectMany(p => p.CoveredTargets)
                .Where(t => t.Kind is TargetKind.RemnantPillar or TargetKind.VerisiumSentinel)
                .Select(t => t.EntityId).Distinct().Count();
            var coveredChests = best.Placements.SelectMany(p => p.CoveredTargets)
                .Where(t => t.Kind == TargetKind.ChestReward)
                .Select(t => t.EntityId).Distinct().Count();
            var coveredMonsters = best.Placements.SelectMany(p => p.CoveredTargets)
                .Where(t => t.Kind is TargetKind.EliteMonster or TargetKind.NormalMonster)
                .Select(t => t.EntityId).Distinct().Count();

            best.Reason = $"Global route search: {coveredPillars}/{pillarCount} Remnants, {coveredChests} Chests, {coveredMonsters} Monsters via {best.Placements.Count} explosives (Score: {best.NetScore:F0}).";
            AppendUncoveredPillarDiagnostics(best, targets, startGrid, placed.Count, terrain, settings);
            return best;
        }

        private static IEnumerable<Vector3> BuildBridgeCandidates(
            Vector3 anchor,
            IEnumerable<ExpeditionTarget> targets,
            HashSet<uint> covered,
            ExpeditionPlannerSettings settings)
        {
            float maxStep = MathF.Max(12f, settings.MaxPlacementRangeGrid - 2f);
            float[] stepFractions = [0.95f, 0.80f, 0.65f, 0.50f];
            float[] sidewaysOffsets = [0f, 8f, -8f, 16f, -16f];

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
                    foreach (var sideways in sidewaysOffsets)
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
            public int CoveredPillars { get; init; }
            public int Bridges { get; init; }
            public float TotalScore { get; init; }
            public float ProliferationMultiplier { get; init; } = 1.0f;
        }

        private static RouteEvaluation? SearchRouteBeam(
            Vector3 startGrid,
            Vector3 startWorld,
            List<PlacedBombInfo> placed,
            List<ExpeditionTarget> targets,
            List<Vector3> staticCandidates,
            ExpeditionTerrainSnapshot? terrain,
            ExpeditionPlannerSettings settings,
            int maxSteps,
            bool isRealDetonator,
            ExpeditionTarget? finalStackPillar)
        {
            const int beamWidth = 64;
            var pillarTargets = targets.Where(t => t.Kind is TargetKind.RemnantPillar or TargetKind.VerisiumSentinel).ToList();
            var allTargets = targets;

            var initialAnchor = placed.Count > 0 ? placed[^1].GridPosition : startGrid;
            var initialCovered = new HashSet<uint>();

            foreach (var bomb in placed)
            {
                foreach (var target in allTargets)
                {
                    if (Vector2.Distance(new Vector2(bomb.GridPosition.X, bomb.GridPosition.Y), new Vector2(target.GridPosition.X, target.GridPosition.Y)) <= settings.BlastRadiusGrid)
                    {
                        initialCovered.Add(target.EntityId);
                    }
                }
            }

            int initialCoveredPillars = initialCovered.Count(id => pillarTargets.Any(p => p.EntityId == id));

            var beam = new List<BeamState>
            {
                new()
                {
                    Anchor = initialAnchor,
                    Committed = placed.Select(p => p.GridPosition).ToList(),
                    Covered = initialCovered,
                    CoveredPillars = initialCoveredPillars,
                    TotalScore = 0f,
                    ProliferationMultiplier = 1.0f
                }
            };

            BeamState? bestPrefix = beam[0];
            float maxRangeSq = settings.MaxPlacementRangeGrid * settings.MaxPlacementRangeGrid;
            float minRangeSq = 12f * 12f;

            for (int depth = 1; depth <= maxSteps && beam.Count > 0; depth++)
            {
                var next = new List<BeamState>();
                int remainingSteps = maxSteps - depth;

                foreach (var state in beam)
                {
                    bool hasSuccessor = false;
                    var candidatePoints = staticCandidates
                        .Concat(BuildBridgeCandidates(state.Anchor, pillarTargets, state.Covered, settings))
                        .Distinct();

                    foreach (var point in candidatePoints)
                    {
                        float dX = point.X - state.Anchor.X;
                        float dY = point.Y - state.Anchor.Y;
                        float distSq = (dX * dX) + (dY * dY);
                        if (distSq > maxRangeSq || distSq < minRangeSq) continue;

                        if (!LegalPlacement.IsPlaceable(point, state.Anchor, terrain, settings, pillarTargets, isRealDetonator ? startGrid : null, state.Committed)) continue;

                        var hit = allTargets.Where(t => !state.Covered.Contains(t.EntityId) &&
                            Vector2.Distance(new Vector2(point.X, point.Y), new Vector2(t.GridPosition.X, t.GridPosition.Y)) <= settings.BlastRadiusGrid).ToList();

                        // This is a route constraint, not a score bonus: while building
                        // a 15-explosive plan, the widest pillar must receive the final
                        // explosion. It is discovered from the current map, never hard-coded.
                        bool hitsFinalStack = finalStackPillar != null && hit.Any(t => t.EntityId == finalStackPillar.EntityId);
                        if (finalStackPillar != null)
                        {
                            if (depth < maxSteps && hitsFinalStack) continue;
                            if (depth == maxSteps && !hitsFinalStack) continue;
                        }

                        float stepScore = 0f;
                        float newProlifMult = state.ProliferationMultiplier;
                        int hitPillars = 0;

                        foreach (var target in hit)
                        {
                            float val = GetTargetValue(target, settings);
                            if (target.Kind is TargetKind.RemnantPillar or TargetKind.VerisiumSentinel)
                            {
                                hitPillars++;
                                float prolifBonus = target.ProliferatedRuneTier switch
                                {
                                    RuneTier.Golden => 1.5f,
                                    RuneTier.Purple_S => 1.2f,
                                    RuneTier.Purple_A => 0.8f,
                                    RuneTier.Purple_B => 0.4f,
                                    _ => 0.15f
                                };
                                stepScore += val * (1.0f + (prolifBonus * remainingSteps));
                                newProlifMult += prolifBonus * 0.35f;
                            }
                            else
                            {
                                stepScore += val * state.ProliferationMultiplier;
                            }
                        }

                        float directDist = MathF.Sqrt(distSq);
                        stepScore -= directDist * 0.12f;

                        if (hit.Count == 0)
                        {
                            if (depth == maxSteps) continue; // Terminal explosive should not be an empty bridge
                            stepScore -= 60f;

                            // A bridge point must make forward progress toward at least one uncovered pillar
                            bool makesProgress = false;
                            foreach (var target in pillarTargets.Where(t => !state.Covered.Contains(t.EntityId)))
                            {
                                var before = Vector2.Distance(new Vector2(state.Anchor.X, state.Anchor.Y), new Vector2(target.GridPosition.X, target.GridPosition.Y));
                                var after = Vector2.Distance(new Vector2(point.X, point.Y), new Vector2(target.GridPosition.X, target.GridPosition.Y));
                                if (after < before - 1.5f)
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
                                Step = placed.Count + depth,
                                GridPosition = point,
                                WorldPosition = new Vector3(
                                    startWorld.X + (point.X - startGrid.X) * 10.87f,
                                    startWorld.Y + (point.Y - startGrid.Y) * 10.87f,
                                    startWorld.Z),  // Z stays at ground level (grid Z ≠ world Z scale)
                                WireDistance = directDist,
                                CoveredTargets = hit
                            }
                        };

                        var successor = new BeamState
                        {
                            Anchor = point,
                            Committed = state.Committed.Append(point).ToList(),
                            Placements = placements,
                            Covered = covered,
                            CoveredPillars = state.CoveredPillars + hitPillars,
                            Bridges = state.Bridges + (hit.Count == 0 ? 1 : 0),
                            TotalScore = state.TotalScore + stepScore,
                            ProliferationMultiplier = newProlifMult
                        };

                        next.Add(successor);
                        hasSuccessor = true;
                    }

                    // Always inject bridge steps toward high-value uncovered far pillars.
                    // Previously this was gated on !hasSuccessor, which caused solver to stop
                    // bridging the moment it found any nearby low-value target (e.g. a chest),
                    // leaving high-slot pillars permanently unreachable.
                    if (depth < maxSteps)
                    {
                        // Prioritise pillars by slot count (more slots = more value), then by distance
                        var bridgeTargets = pillarTargets
                            .Where(t => !state.Covered.Contains(t.EntityId))
                            .Where(t =>
                            {
                                float d = Vector2.Distance(new Vector2(state.Anchor.X, state.Anchor.Y), new Vector2(t.GridPosition.X, t.GridPosition.Y));
                                return d > settings.BlastRadiusGrid + settings.MaxPlacementRangeGrid; // only pillars unreachable in ONE hop
                            })
                            .OrderByDescending(t => t.HoleCount)
                            .ThenBy(t => Vector2.Distance(new Vector2(state.Anchor.X, state.Anchor.Y), new Vector2(t.GridPosition.X, t.GridPosition.Y)))
                            .Take(hasSuccessor ? 2 : 4); // be more aggressive when stuck

                        foreach (var bridgeTarget in bridgeTargets)
                        {
                            if (LegalPlacement.TryFindTerrainDetourWaypoint(
                                    terrain,
                                    new Vector2(state.Anchor.X, state.Anchor.Y),
                                    new Vector2(bridgeTarget.GridPosition.X, bridgeTarget.GridPosition.Y),
                                    settings.MaxPlacementRangeGrid,
                                    out var waypoint))
                            {
                                var point = new Vector3(waypoint.X, waypoint.Y, bridgeTarget.GridPosition.Z);
                                if (LegalPlacement.IsPlaceable(point, state.Anchor, terrain, settings, pillarTargets, isRealDetonator ? startGrid : null, state.Committed))
                                {
                                    float dDist = Vector2.Distance(new Vector2(state.Anchor.X, state.Anchor.Y), new Vector2(point.X, point.Y));
                                    // Penalty scales inversely with pillar value so high-slot pillars are pursued harder
                                    float bridgePenalty = hasSuccessor ? (80f - bridgeTarget.HoleCount * 8f) : 40f;
                                    next.Add(new BeamState
                                    {
                                        Anchor = point,
                                        Committed = state.Committed.Append(point).ToList(),
                                        Placements = state.Placements.Append(new ProposedPlacement
                                        {
                                            Step = placed.Count + depth,
                                            GridPosition = point,
                                            WorldPosition = new Vector3(
                                                startWorld.X + (point.X - startGrid.X) * 10.87f,
                                                startWorld.Y + (point.Y - startGrid.Y) * 10.87f,
                                                startWorld.Z),
                                            WireDistance = dDist
                                        }).ToList(),
                                        Covered = new HashSet<uint>(state.Covered),
                                        CoveredPillars = state.CoveredPillars,
                                        Bridges = state.Bridges + 1,
                                        TotalScore = state.TotalScore - bridgePenalty,
                                        ProliferationMultiplier = state.ProliferationMultiplier
                                    });
                                    break;
                                }
                            }
                        }
                    }
                }

                beam = PruneBeam(next, pillarTargets, beamWidth);
                var prefix = beam
                    .OrderByDescending(s => s.CoveredPillars)
                    .ThenByDescending(s => s.TotalScore)
                    .ThenByDescending(s => s.Covered.Count)
                    .FirstOrDefault();

                if (prefix != null && (bestPrefix == null || prefix.CoveredPillars > bestPrefix.CoveredPillars ||
                    (prefix.CoveredPillars == bestPrefix.CoveredPillars && prefix.TotalScore > bestPrefix.TotalScore)))
                {
                    bestPrefix = prefix;
                }
            }

            var winner = beam
                .OrderByDescending(s => s.CoveredPillars)
                .ThenByDescending(s => s.TotalScore)
                .ThenByDescending(s => s.Covered.Count)
                .FirstOrDefault();

            winner ??= bestPrefix;
            if (winner == null) return null;

            return new RouteEvaluation
            {
                Profile = settings.Profile.ToString(),
                Placements = winner.Placements,
                NetScore = winner.TotalScore
            };
        }

        private static List<BeamState> PruneBeam(List<BeamState> states, List<ExpeditionTarget> pillarTargets, int width)
        {
            var unique = states
                .GroupBy(s => $"{string.Join(',', s.Covered.Order())}|{MathF.Round(s.Anchor.X / 15f)}:{MathF.Round(s.Anchor.Y / 15f)}")
                .Select(g => g.OrderByDescending(s => s.CoveredPillars).ThenByDescending(s => s.TotalScore).First())
                .ToList();

            var chosen = unique
                .OrderByDescending(s => s.CoveredPillars)
                .ThenByDescending(s => s.TotalScore)
                .ThenByDescending(s => s.Covered.Count)
                .Take(Math.Max(8, width / 2))
                .ToList();

            // Keep bridge frontiers alive for every uncovered pillar.
            // Without this, a state walking toward a remote pillar has zero new coverage for a step
            // and gets discarded in favour of nearby completed pillars.
            foreach (var pillar in pillarTargets)
            {
                var candidate = unique
                    .Where(s => !s.Covered.Contains(pillar.EntityId))
                    .OrderBy(s => Vector2.Distance(new Vector2(s.Anchor.X, s.Anchor.Y), new Vector2(pillar.GridPosition.X, pillar.GridPosition.Y)))
                    .ThenByDescending(s => s.CoveredPillars)
                    .ThenByDescending(s => s.TotalScore)
                    .FirstOrDefault();

                if (candidate != null && !chosen.Contains(candidate))
                {
                    chosen.Add(candidate);
                }
            }

            return chosen
                .OrderByDescending(s => s.CoveredPillars)
                .ThenByDescending(s => s.TotalScore)
                .ThenByDescending(s => s.Covered.Count)
                .Take(width)
                .ToList();
        }

        private static void AppendUncoveredPillarDiagnostics(
            RouteEvaluation route,
            IEnumerable<ExpeditionTarget> targets,
            Vector3 startGrid,
            int alreadyPlaced,
            ExpeditionTerrainSnapshot? terrain,
            ExpeditionPlannerSettings settings)
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
                else
                {
                    route.Warnings.Add($"{label} ({target.HoleCount} slots): {DiagnosePillarExclusion(target, anchors, startGrid, terrain, settings)}");
                }
            }
        }

        private static string DiagnosePillarExclusion(ExpeditionTarget target, IReadOnlyList<Vector3> anchors, Vector3 detonator, ExpeditionTerrainSnapshot? terrain, ExpeditionPlannerSettings settings)
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

            return "excluded because other pillars/chests yielded higher overall score within the available explosives budget";
        }

        private static List<Vector3> BuildCandidates(List<ExpeditionTarget> targets, ExpeditionTerrainSnapshot? terrain, ExpeditionPlannerSettings settings, HashSet<uint>? alreadyCovered = null)
        {
            alreadyCovered ??= new HashSet<uint>();
            var result = new List<Vector3>();
            // Exclude targets already claimed by a placed bomb — no need to generate
            // candidates around them; the beam search already knows they are covered.
            var pillars = targets.Where(t => t.Kind is TargetKind.RemnantPillar or TargetKind.VerisiumSentinel
                                            && !alreadyCovered.Contains(t.EntityId)).ToList();
            var chests = targets.Where(t => t.Kind == TargetKind.ChestReward
                                           && !alreadyCovered.Contains(t.EntityId)).ToList();

            // 1. Concentric rings around every Remnant Pillar
            float[] pillarRadii = [12f, 18f, 26f];
            foreach (var p in pillars)
            {
                foreach (var radius in pillarRadii)
                {
                    for (int i = 0; i < 16; i++)
                    {
                        float a = i * MathF.PI / 8f;
                        var pt = p.GridPosition + new Vector3(MathF.Cos(a) * radius, MathF.Sin(a) * radius, 0);
                        if (LegalPlacement.HasWalkableClearance(terrain, pt.X, pt.Y, settings.BombClearanceRadiusGrid))
                        {
                            result.Add(pt);
                        }
                    }
                }
            }

            // 2. Concentric rings around high-value chests
            float[] chestRadii = [0f, 14f, 24f];
            foreach (var c in chests)
            {
                foreach (var radius in chestRadii)
                {
                    if (radius == 0f)
                    {
                        if (LegalPlacement.HasWalkableClearance(terrain, c.GridPosition.X, c.GridPosition.Y, settings.BombClearanceRadiusGrid))
                        {
                            result.Add(c.GridPosition);
                        }
                    }
                    else
                    {
                        for (int i = 0; i < 8; i++)
                        {
                            float a = i * MathF.PI / 4f;
                            var pt = c.GridPosition + new Vector3(MathF.Cos(a) * radius, MathF.Sin(a) * radius, 0);
                            if (LegalPlacement.HasWalkableClearance(terrain, pt.X, pt.Y, settings.BombClearanceRadiusGrid))
                            {
                                result.Add(pt);
                            }
                        }
                    }
                }
            }

            // 3. Inter-pillar bridge waypoints (dense walkable bridge network between all pillars)
            float[] lateralOffsets = [0f, 6f, -6f, 12f, -12f];
            for (int i = 0; i < pillars.Count; i++)
            {
                for (int j = i + 1; j < pillars.Count; j++)
                {
                    var p1 = pillars[i].GridPosition;
                    var p2 = pillars[j].GridPosition;
                    float dist = Vector2.Distance(new Vector2(p1.X, p1.Y), new Vector2(p2.X, p2.Y));
                    if (dist > 220f) continue;

                    int steps = (int)MathF.Ceiling(dist / 40f);
                    var dir = Vector2.Normalize(new Vector2(p2.X - p1.X, p2.Y - p1.Y));
                    var perp = new Vector2(-dir.Y, dir.X);

                    for (int s = 1; s < steps; s++)
                    {
                        float frac = (float)s / steps;
                        var basePoint = Vector3.Lerp(p1, p2, frac);

                        foreach (var lat in lateralOffsets)
                        {
                            var pt = new Vector3(basePoint.X + (perp.X * lat), basePoint.Y + (perp.Y * lat), basePoint.Z);
                            if (LegalPlacement.HasWalkableClearance(terrain, pt.X, pt.Y, settings.BombClearanceRadiusGrid))
                            {
                                result.Add(pt);
                                break;
                            }
                        }
                    }
                }
            }

            // 4. Sweet-spot intersections between (Pillar, Chest) and (Chest, Chest)
            float maxOverlap = (2f * settings.BlastRadiusGrid) - 2f;
            foreach (var p in pillars)
            {
                foreach (var c in chests)
                {
                    float dist = Vector2.Distance(new Vector2(p.GridPosition.X, p.GridPosition.Y), new Vector2(c.GridPosition.X, c.GridPosition.Y));
                    if (dist < maxOverlap)
                    {
                        var mid = Vector3.Lerp(p.GridPosition, c.GridPosition, 0.5f);
                        if (LegalPlacement.HasWalkableClearance(terrain, mid.X, mid.Y, settings.BombClearanceRadiusGrid))
                        {
                            result.Add(mid);
                        }
                    }
                }
            }

            // Ensure no candidate directly clips into a pillar's physical base
            return result
                .Where(pt => pillars.All(p => Vector2.Distance(new Vector2(pt.X, pt.Y), new Vector2(p.GridPosition.X, p.GridPosition.Y)) >= LegalPlacement.PillarPhysicalRadius))
                .Distinct()
                .ToList();
        }

        private static float GetTargetValue(ExpeditionTarget target, ExpeditionPlannerSettings settings)
        {
            if (target.Kind is TargetKind.RemnantPillar or TargetKind.VerisiumSentinel)
            {
                var rune = RuneName(target);
                if (rune.Equals("Opulent", StringComparison.OrdinalIgnoreCase) || target.ProliferatedRuneTier == RuneTier.Golden)
                {
                    return 3500f;
                }

                return target.ProliferatedRuneTier switch
                {
                    RuneTier.Purple_S => 2000f,
                    RuneTier.Purple_A => 1200f,
                    RuneTier.Purple_B => 600f,
                    _ => 300f + (target.HoleCount * 40f)
                };
            }

            if (target.Kind == TargetKind.ChestReward)
            {
                var name = target.DisplayName ?? string.Empty;
                var mods = target.ModNames != null ? string.Join(' ', target.ModNames) : string.Empty;

                if (name.Contains("Currency", StringComparison.OrdinalIgnoreCase) || mods.Contains("Currency", StringComparison.OrdinalIgnoreCase))
                {
                    return 250f;
                }
                if (name.Contains("Map", StringComparison.OrdinalIgnoreCase) || mods.Contains("Map", StringComparison.OrdinalIgnoreCase))
                {
                    return 160f;
                }
                if (name.Contains("Unique", StringComparison.OrdinalIgnoreCase) || mods.Contains("Unique", StringComparison.OrdinalIgnoreCase))
                {
                    return 100f;
                }
                return MathF.Max(60f, settings.WeightChest);
            }

            if (target.Kind == TargetKind.ExpeditionBoss) return MathF.Max(2000f, settings.WeightBoss);
            if (target.Kind == TargetKind.VerisiumSentinel) return MathF.Max(500f, settings.WeightSentinel);
            if (target.Kind == TargetKind.EliteMonster) return MathF.Max(50f, settings.WeightElite);
            if (target.Kind == TargetKind.NormalMonster) return MathF.Max(15f, settings.WeightMonster);

            return target.BaseWeight > 0 ? target.BaseWeight : 20f;
        }

        private static string RuneName(ExpeditionTarget target) => !string.IsNullOrWhiteSpace(target.ProliferatedRuneName)
            ? target.ProliferatedRuneName : target.AnchorRuneName;
    }
}
