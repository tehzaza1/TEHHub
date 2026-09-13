namespace ExpeditionPlanner
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Numerics;
    using TEHhub.RemoteObjects.States.InGameStateObjects;

    public static class ExpeditionSolver
    {
        public static RouteEvaluation Solve(
            Vector3 startDetonatorGrid,
            Vector3 startDetonatorWorld,
            List<PlacedBombInfo> currentPlacedBombs,
            List<ExpeditionTarget> availableTargets,
            AreaInstance? area,
            ExpeditionPlannerSettings settings)
        {
            var evaluation = new RouteEvaluation
            {
                Profile = settings.Profile.ToString()
            };

            if (availableTargets.Count == 0)
            {
                evaluation.Reason = "No active Expedition markers or remnants found.";
                return evaluation;
            }

            int budget = Math.Max(1, settings.MaxExplosiveBudget);
            int currentStep = currentPlacedBombs.Count;

            // Generate candidate placement positions from cluster centers of high-value targets
            var candidatePoints = GenerateCandidatePoints(availableTargets, settings);
            if (candidatePoints.Count == 0)
            {
                evaluation.Reason = "No legal placement points could reach markers.";
                return evaluation;
            }

            // Stateful search for best sequential route starting from last placed bomb or detonator
            var startAnchor = currentPlacedBombs.Count > 0
                ? currentPlacedBombs[^1].GridPosition
                : startDetonatorGrid;

            var currentAnchorWorld = currentPlacedBombs.Count > 0
                ? currentPlacedBombs[^1].WorldPosition
                : startDetonatorWorld;

            var chosenPlacements = new List<ProposedPlacement>();
            var accumulatedRunes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var coveredEntityIds = new HashSet<uint>();

            // Account for already placed bombs
            foreach (var b in currentPlacedBombs)
            {
                var hitTargets = FindTargetsInRadius(b.GridPosition, availableTargets, settings.BlastRadiusGrid);
                foreach (var t in hitTargets)
                {
                    coveredEntityIds.Add(t.EntityId);
                    foreach (var m in t.ModNames) accumulatedRunes.Add(m);
                }
            }

            Vector3 activeAnchor = startAnchor;
            float totalScore = 0f;
            bool routeUnsafe = false;
            var warnings = new List<string>();

            int neededSteps = Math.Min(3, budget - currentStep);
            for (int step = 1; step <= neededSteps; step++)
            {
                ProposedPlacement? bestPlacement = null;
                float bestStepScore = float.NegativeInfinity;

                foreach (var pt in candidatePoints)
                {
                    if (!LegalPlacement.IsPlaceable(pt.Grid, activeAnchor, area, settings))
                    {
                        continue;
                    }

                    var targetsInRadius = FindTargetsInRadius(pt.Grid, availableTargets, settings.BlastRadiusGrid)
                        .Where(t => !coveredEntityIds.Contains(t.EntityId))
                        .ToList();

                    if (targetsInRadius.Count == 0) continue;

                    float stepScore = 0f;
                    var stepRunes = new List<string>();
                    bool stepHasForbidden = false;

                    foreach (var target in targetsInRadius)
                    {
                        float val = GetTargetValue(target, settings);
                        stepScore += val;

                        foreach (var mod in target.ModNames)
                        {
                            stepRunes.Add(mod);
                            if (settings.NeverTakeRunes.Contains(mod))
                            {
                                stepHasForbidden = true;
                                warnings.Add($"Forbidden rune detected: {mod}");
                            }

                            if (!accumulatedRunes.Contains(mod))
                            {
                                stepScore += GetRuneWeight(mod, settings);
                            }
                        }
                    }

                    if (stepHasForbidden)
                    {
                        if (settings.Profile == PlannerProfile.Safe)
                        {
                            continue; // Exclude entirely
                        }
                        stepScore -= 200f; // Large penalty
                    }

                    if (stepScore > bestStepScore)
                    {
                        bestStepScore = stepScore;
                        bestPlacement = new ProposedPlacement
                        {
                            Step = currentStep + step,
                            GridPosition = pt.Grid,
                            WorldPosition = pt.World,
                            TerrainHeight = pt.TerrainHeight,
                            CoveredTargets = targetsInRadius,
                            GainedRunes = stepRunes
                        };
                    }
                }

                if (bestPlacement != null)
                {
                    chosenPlacements.Add(bestPlacement);
                    activeAnchor = bestPlacement.GridPosition;
                    totalScore += bestStepScore;

                    foreach (var t in bestPlacement.CoveredTargets)
                    {
                        coveredEntityIds.Add(t.EntityId);
                    }
                    foreach (var r in bestPlacement.GainedRunes)
                    {
                        accumulatedRunes.Add(r);
                    }
                }
                else
                {
                    break;
                }
            }

            evaluation.Placements = chosenPlacements;
            evaluation.NetScore = totalScore;
            evaluation.IsUnsafe = routeUnsafe || warnings.Count > 0;
            evaluation.Warnings = warnings.Distinct().ToList();

            int totalCovered = chosenPlacements.Sum(p => p.CoveredTargets.Count);
            int remnantsHit = chosenPlacements.Sum(p => p.CoveredTargets.Count(t => t.Kind == TargetKind.RemnantPillar || t.Kind == TargetKind.VerisiumSentinel));
            evaluation.Reason = chosenPlacements.Count > 0
                ? $"{settings.Profile} path ({chosenPlacements.Count} bombs) hitting {totalCovered} targets including {remnantsHit} remnants/sentinels."
                : "No valid sequential route found within legal placement range.";

            return evaluation;
        }

        private static List<(Vector3 Grid, Vector3 World, float TerrainHeight)> GenerateCandidatePoints(
            List<ExpeditionTarget> targets,
            ExpeditionPlannerSettings settings)
        {
            var points = new List<(Vector3 Grid, Vector3 World, float TerrainHeight)>();
            foreach (var t in targets)
            {
                points.Add((t.GridPosition, t.WorldPosition, t.TerrainHeight));
            }

            // Also add midpoints between close pairs of targets to maximize blast overlap
            for (int i = 0; i < targets.Count && i < 30; i++)
            {
                for (int j = i + 1; j < targets.Count && j < 30; j++)
                {
                    var a = targets[i];
                    var b = targets[j];
                    var dx = a.GridPosition.X - b.GridPosition.X;
                    var dy = a.GridPosition.Y - b.GridPosition.Y;
                    var dist = MathF.Sqrt((dx * dx) + (dy * dy));
                    if (dist < settings.BlastRadiusGrid * 1.5f && dist > 5.0f)
                    {
                        var midGrid = (a.GridPosition + b.GridPosition) * 0.5f;
                        var midWorld = (a.WorldPosition + b.WorldPosition) * 0.5f;
                        var midHeight = (a.TerrainHeight + b.TerrainHeight) * 0.5f;
                        points.Add((midGrid, midWorld, midHeight));
                    }
                }
            }

            return points;
        }

        private static List<ExpeditionTarget> FindTargetsInRadius(
            Vector3 centerGrid,
            List<ExpeditionTarget> targets,
            float radius)
        {
            var result = new List<ExpeditionTarget>();
            float rSq = radius * radius;
            foreach (var t in targets)
            {
                var dx = t.GridPosition.X - centerGrid.X;
                var dy = t.GridPosition.Y - centerGrid.Y;
                if ((dx * dx) + (dy * dy) <= rSq)
                {
                    result.Add(t);
                }
            }
            return result;
        }

        private static float GetTargetValue(ExpeditionTarget target, ExpeditionPlannerSettings settings)
        {
            return target.Kind switch
            {
                TargetKind.ChestReward => settings.WeightChest,
                TargetKind.EliteMonster => settings.WeightElite,
                TargetKind.NormalMonster => settings.WeightMonster,
                TargetKind.RemnantPillar => settings.WeightRemnant,
                TargetKind.VerisiumSentinel => settings.WeightSentinel,
                TargetKind.ExpeditionBoss => settings.WeightBoss,
                _ => 10f,
            };
        }

        private static float GetRuneWeight(string modName, ExpeditionPlannerSettings settings)
        {
            foreach (var kv in settings.RuneWeights)
            {
                if (modName.Contains(kv.Key, StringComparison.OrdinalIgnoreCase))
                {
                    return kv.Value;
                }
            }
            return 20f;
        }
    }
}
