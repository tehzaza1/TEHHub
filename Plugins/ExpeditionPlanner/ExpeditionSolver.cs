namespace ExpeditionPlanner
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Numerics;
    using TEHhub.RemoteObjects.Components;
    using TEHhub.RemoteObjects.States.InGameStateObjects;

    public static class ExpeditionSolver
    {
        private const float GridToWorldRatio = 10.87f;

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

            // Anchor point for next placement
            var startAnchor = currentPlacedBombs.Count > 0
                ? currentPlacedBombs[^1].GridPosition
                : startDetonatorGrid;

            if (startAnchor.LengthSquared() < 1f && availableTargets.Count > 0)
            {
                var player = area?.Player;
                if (player != null && player.TryGetComponent<Render>(out var pRender, false))
                {
                    startAnchor = new Vector3(pRender.GridPosition.X, pRender.GridPosition.Y, pRender.GridPosition.Z);
                }
                else
                {
                    startAnchor = availableTargets[0].GridPosition;
                }
            }

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

            var accumulatedProliferatedRunes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Vector3 activeAnchor = startAnchor;
            float totalScore = 0f;
            var warnings = new List<string>();

            int neededSteps = Math.Min(3, budget - currentStep);
            for (int step = 1; step <= neededSteps; step++)
            {
                ProposedPlacement? bestPlacement = null;
                float bestStepScore = float.NegativeInfinity;

                // Dynamically generate candidate placement points oriented toward activeAnchor
                var candidatePoints = GenerateCandidatesForAnchor(activeAnchor, availableTargets, area, settings);
                if (candidatePoints.Count == 0)
                {
                    break;
                }

                foreach (var pt in candidatePoints)
                {
                    // 1. Legal placement check with pillar collision & detour verification
                    if (!LegalPlacement.IsPlaceable(pt.Grid, activeAnchor, area, settings, availableTargets))
                    {
                        continue;
                    }

                    // 2. Check effective distance including detour around pillars
                    var start2D = new Vector2(activeAnchor.X, activeAnchor.Y);
                    var end2D = new Vector2(pt.Grid.X, pt.Grid.Y);
                    var effectiveDist = LegalPlacement.CalculateEffectiveDistance(
                        start2D,
                        end2D,
                        availableTargets,
                        area,
                        out var isObstructed);

                    if (effectiveDist > settings.MaxPlacementRangeGrid)
                    {
                        continue; // Cannot reach after detouring around pillar/wall
                    }

                    var targetsInRadius = FindTargetsInRadius(pt.Grid, availableTargets, settings.BlastRadiusGrid)
                        .Where(t => !coveredEntityIds.Contains(t.EntityId))
                        .ToList();

                    float stepScore = 0f;
                    var stepRunes = new List<string>();
                    bool stepHasForbidden = false;

                    if (targetsInRadius.Count == 0)
                    {
                        // Bridging candidate: allows connecting anchor to distant target clusters when out of 1-bomb reach
                        if (step >= neededSteps) continue; // Final bomb must hit targets

                        var bestUncovered = availableTargets
                            .Where(t => !coveredEntityIds.Contains(t.EntityId))
                            .OrderByDescending(t => (t.ProliferatedRuneTier == RuneTier.Golden ? 2000f : 0f) +
                                                    (t.ProliferatedRuneTier == RuneTier.Purple_S ? 1000f : 0f) +
                                                    (t.BaseWeight > 0 ? t.BaseWeight : GetTargetValue(t, settings)))
                            .FirstOrDefault();

                        if (bestUncovered == null) continue;

                        var targetPos2D = new Vector2(bestUncovered.GridPosition.X, bestUncovered.GridPosition.Y);
                        float distBefore = Vector2.Distance(start2D, targetPos2D);
                        float distAfter = Vector2.Distance(end2D, targetPos2D);
                        float advance = distBefore - distAfter;

                        if (advance <= 3.0f) continue; // Must advance towards target

                        // Bridging score: rewards moving closer to high-priority targets while penalizing obstructions
                        stepScore = (advance * 6.0f) - (effectiveDist * 0.1f) - (isObstructed ? 40f : 0f);
                    }
                    else
                    {
                        // Moderate penalty if path is obstructed by pillar or wall:
                        // Prioritize clean open-ground lines of sight when available
                        if (isObstructed)
                        {
                            stepScore -= 40f;
                        }

                        // Slight preference for shorter, tighter placements
                        stepScore -= effectiveDist * 0.15f;

                        // Remaining bombs in the sequence that will benefit from runes detonated at this step
                        int remainingBombs = Math.Max(0, budget - (currentStep + step));

                        foreach (var target in targetsInRadius)
                        {
                            float val = GetTargetValue(target, settings);
                            if (target.Kind == TargetKind.RemnantPillar || target.Kind == TargetKind.VerisiumSentinel)
                            {
                                // "Runes do not stack. If a rune is already proliferated, another duplicate rune is useless."
                                bool isDuplicate = !string.IsNullOrEmpty(target.AnchorRuneName) &&
                                                   target.IsAnchorInGoldenSlot &&
                                                   accumulatedProliferatedRunes.Contains(target.AnchorRuneName);
                                target.IsDuplicateProliferation = isDuplicate;

                                float proliferationMultiplier = (!target.IsAnchorInGoldenSlot || isDuplicate)
                                    ? 1.0f
                                    : (1.0f + (1.5f * remainingBombs));
                                float baseVal = target.BaseWeight > 0 ? target.BaseWeight : settings.WeightRemnant;

                                // Opulent (Golden): SSS-Tier, doubles loot drops, must be prioritized first
                                if (target.ProliferatedRuneTier == RuneTier.Golden)
                                {
                                    baseVal += 800f;
                                }
                                else if (target.ProliferatedRuneTier == RuneTier.Purple_S)
                                {
                                    baseVal += 350f; // Power, Death, Bond, Oath
                                }
                                else if (target.ProliferatedRuneTier == RuneTier.Purple_A)
                                {
                                    baseVal += 180f; // Time, Rebirth
                                }

                                val = baseVal * proliferationMultiplier;
                            }

                            stepScore += val;

                            foreach (var mod in target.ModNames)
                            {
                                stepRunes.Add(mod);
                                if (settings.NeverTakeRunes.Contains(mod))
                                {
                                    stepHasForbidden = true;
                                    warnings.Add($"Forbidden rune detected: {mod}");
                                }

                                // In PoE 2 Grand Expedition: ONLY the Golden Slot rune (target.AnchorRuneName) proliferates!
                                // Other runes and mods in non-golden slots apply ONLY LOCALLY to this remnant (no proliferation multiplier).
                                bool isGoldenSlotRune = string.Equals(mod, target.AnchorRuneName, StringComparison.OrdinalIgnoreCase);
                                if (!isGoldenSlotRune)
                                {
                                    stepScore += GetRuneWeight(mod, settings);
                                }
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
                            WireDistance = effectiveDist,
                            IsObstructed = isObstructed,
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

                    if (bestPlacement.IsObstructed)
                    {
                        warnings.Add($"Step {bestPlacement.Step}: wire bends around pillar/wall ({bestPlacement.WireDistance:F0} grid). Keep on open ground.");
                    }

                    int remainingBombs = Math.Max(0, budget - bestPlacement.Step);
                    foreach (var t in bestPlacement.CoveredTargets)
                    {
                        t.ProliferationRemaining = t.IsAnchorInGoldenSlot ? remainingBombs : 0;
                        coveredEntityIds.Add(t.EntityId);
                        if (t.Kind == TargetKind.RemnantPillar && !string.IsNullOrEmpty(t.AnchorRuneName) && t.IsAnchorInGoldenSlot)
                        {
                            if (!accumulatedProliferatedRunes.Contains(t.AnchorRuneName))
                            {
                                accumulatedProliferatedRunes.Add(t.AnchorRuneName);
                                evaluation.ProliferatedStack.Add(t.AnchorRuneName);
                            }
                        }
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

            evaluation.RerollRemnantCount = availableTargets.Count(t => t.Kind == TargetKind.RemnantPillar && t.NeedsReroll);
            if (evaluation.RerollRemnantCount > 0)
            {
                warnings.Add($"{evaluation.RerollRemnantCount} Remnant(s) have no Purple/Gold runes. Consider rerolling before detonating!");
            }

            evaluation.Placements = chosenPlacements;
            evaluation.NetScore = totalScore;
            evaluation.IsUnsafe = warnings.Count > 0;
            evaluation.Warnings = warnings.Distinct().ToList();

            int totalCovered = chosenPlacements.Sum(p => p.CoveredTargets.Count);
            int remnantsHit = chosenPlacements.Sum(p => p.CoveredTargets.Count(t => t.Kind == TargetKind.RemnantPillar || t.Kind == TargetKind.VerisiumSentinel));
            bool anyObstructed = chosenPlacements.Any(p => p.IsObstructed);

            evaluation.Reason = chosenPlacements.Count > 0
                ? $"{settings.Profile} route ({chosenPlacements.Count} bombs) hitting {totalCovered} targets ({remnantsHit} remnants/sentinels). {(anyObstructed ? "Contains obstacle detour (check wire)." : "100% clean line of sight (no pillar blockage).")}"
                : "No valid sequential route found within legal reach without obstacle collision.";

            return evaluation;
        }

        /// <summary>
        /// Generates candidate placement points on open, walkable ground oriented toward <paramref name="anchorGrid"/>.
        /// For Remnant pillars and Sentinels, bomb points are placed on the FRONT hemisphere facing the anchor,
        /// ensuring the pillar is never between the anchor and the bomb!
        /// </summary>
        private static List<(Vector3 Grid, Vector3 World, float TerrainHeight)> GenerateCandidatesForAnchor(
            Vector3 anchorGrid,
            List<ExpeditionTarget> targets,
            AreaInstance? area,
            ExpeditionPlannerSettings settings)
        {
            var points = new List<(Vector3 Grid, Vector3 World, float TerrainHeight)>();
            var anchor2D = new Vector2(anchorGrid.X, anchorGrid.Y);

            void AddCandidate(float gx, float gy, ExpeditionTarget refTarget)
            {
                if (LegalPlacement.IsCellWalkable(area, (int)gx, (int)gy))
                {
                    var offsetGrid = new Vector3(gx, gy, refTarget.GridPosition.Z);
                    var offsetWorld = new Vector3(
                        refTarget.WorldPosition.X + ((gx - refTarget.GridPosition.X) * GridToWorldRatio),
                        refTarget.WorldPosition.Y + ((gy - refTarget.GridPosition.Y) * GridToWorldRatio),
                        refTarget.WorldPosition.Z);
                    points.Add((offsetGrid, offsetWorld, refTarget.TerrainHeight));
                }
            }

            foreach (var t in targets)
            {
                var target2D = new Vector2(t.GridPosition.X, t.GridPosition.Y);
                var fromAnchor = target2D - anchor2D;
                var distToAnchor = fromAnchor.Length();
                if (distToAnchor < 1e-3f) continue;

                var dirToTarget = fromAnchor / distToAnchor;
                var toAnchor = -dirToTarget;

                // 1. Direct approach points between Anchor and Target
                if (distToAnchor <= settings.MaxPlacementRangeGrid + settings.BlastRadiusGrid - 2.0f)
                {
                    float minD = MathF.Max(4.0f, distToAnchor - (settings.BlastRadiusGrid - 2.0f));
                    float maxD = MathF.Min(settings.MaxPlacementRangeGrid - 1.0f, distToAnchor + (settings.BlastRadiusGrid - 4.0f));
                    for (float d = minD; d <= maxD; d += 6.0f)
                    {
                        var gx = anchor2D.X + (dirToTarget.X * d);
                        var gy = anchor2D.Y + (dirToTarget.Y * d);
                        AddCandidate(gx, gy, t);
                    }
                }
                else
                {
                    // Bridging stepping stone points along approach vector to distant target
                    float[] bridgeSteps = [settings.MaxPlacementRangeGrid - 3.0f, settings.MaxPlacementRangeGrid - 12.0f, 70.0f, 55.0f];
                    float[] angleDeviations = [0f, -0.15f, 0.15f, -0.3f, 0.3f];
                    foreach (var bd in bridgeSteps)
                    {
                        foreach (var dev in angleDeviations)
                        {
                            var ang = MathF.Atan2(dirToTarget.Y, dirToTarget.X) + dev;
                            var bgx = anchor2D.X + (MathF.Cos(ang) * bd);
                            var bgy = anchor2D.Y + (MathF.Sin(ang) * bd);
                            AddCandidate(bgx, bgy, t);
                        }
                    }
                }

                // 2. Circular perimeter sweep around the target (for multi-target clustering)
                if (t.Kind == TargetKind.RemnantPillar || t.Kind == TargetKind.VerisiumSentinel)
                {
                    var baseAngle = MathF.Atan2(toAnchor.Y, toAnchor.X);
                    float[] angleOffsets = [0f, -0.28f, 0.28f, -0.56f, 0.56f, -0.84f, 0.84f];
                    float[] distances = [6.0f, 10.0f, 15.0f, 20.0f, 25.0f];

                    foreach (var dist in distances)
                    {
                        foreach (var offset in angleOffsets)
                        {
                            var ang = baseAngle + offset;
                            var gx = t.GridPosition.X + (MathF.Cos(ang) * dist);
                            var gy = t.GridPosition.Y + (MathF.Sin(ang) * dist);
                            AddCandidate(gx, gy, t);
                        }
                    }
                }
                else
                {
                    AddCandidate(t.GridPosition.X, t.GridPosition.Y, t);

                    if (distToAnchor > 10.0f)
                    {
                        var gx = t.GridPosition.X + (toAnchor.X * 8.0f);
                        var gy = t.GridPosition.Y + (toAnchor.Y * 8.0f);
                        AddCandidate(gx, gy, t);
                    }
                }
            }

            // 3. Midpoints between pairs of close targets to maximize blast overlap
            for (int i = 0; i < targets.Count && i < 25; i++)
            {
                for (int j = i + 1; j < targets.Count && j < 25; j++)
                {
                    var a = targets[i];
                    var b = targets[j];
                    var dx = a.GridPosition.X - b.GridPosition.X;
                    var dy = a.GridPosition.Y - b.GridPosition.Y;
                    var dist = MathF.Sqrt((dx * dx) + (dy * dy));

                    if (dist < settings.BlastRadiusGrid * 1.5f && dist > 10.0f)
                    {
                        var midGrid = (a.GridPosition + b.GridPosition) * 0.5f;
                        AddCandidate(midGrid.X, midGrid.Y, a);
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
