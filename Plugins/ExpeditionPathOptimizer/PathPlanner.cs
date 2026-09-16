namespace ExpeditionPathOptimizer
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Numerics;
    using ExpeditionPathOptimizer.PathPlannerData;

    public class PathPlanner
    {
        public record PerPointScoreInfo(
            Vector2 Point,
            double ScoreDiff,
            List<ExpeditionRemnant> NewRemnants,
            string? ActiveRune,
            double RuneScore,
            bool IsUsefulBridge,
            double FuturePotential);

        public record DetailedLootScore(List<PerPointScoreInfo> PerPointScore, double TotalScore, ExpeditionEnvironment Environment);

        private readonly ExpeditionPathOptimizerSettings settings;

        public PathPlanner(ExpeditionPathOptimizerSettings settings)
        {
            this.settings = settings;
        }

        public void Init(ExpeditionEnvironment environment)
        {
            foreach (var r in environment.Remnants)
            {
                r.UpdateBaseRuneWeight(this.settings.RuneWeights);
            }
        }

        public double GetScore(List<Vector2> path, ExpeditionEnvironment env)
        {
            if (path == null || path.Count == 0 || path.Count > env.MaxExplosions)
            {
                return double.NegativeInfinity;
            }

            int n = path.Count;
            var finalTarget = env.FinalTarget;
            if (finalTarget == null) return double.NegativeInfinity;

            // Rule 1: Final bomb (path[n-1]) MUST hit finalTarget
            var lastBomb = path[n - 1];
            float distToFinal = Vector2.Distance(lastBomb, finalTarget.GridPos);
            if (distToFinal > env.ExplosionRadius)
            {
                return double.NegativeInfinity;
            }

            var hitRemnants = new HashSet<ExpeditionRemnant>();
            double totalScore = 0.0;
            var prevPoint = env.StartingPoint;
            string? activePropagatedRune = null;

            for (int i = 0; i < n; i++)
            {
                var curPoint = path[i];
                float stepDist = Vector2.Distance(prevPoint, curPoint);

                // Rule 2: Step distance <= ExplosionRange
                if (stepDist > env.ExplosionRange * 1.01f)
                {
                    return double.NegativeInfinity;
                }

                // Terrain Check: Point must be walkable and wire must have line of sight
                if (!env.IsPointWalkable(curPoint))
                {
                    return double.NegativeInfinity;
                }

                if (!env.HasLineOfSight(prevPoint, curPoint))
                {
                    return double.NegativeInfinity;
                }

                int remainingSteps = n - 1 - i;

                // Rule 3: Intermediate bombs (0 .. n-2) MUST NOT hit finalTarget
                if (i < n - 1)
                {
                    if (Vector2.Distance(curPoint, finalTarget.GridPos) <= env.ExplosionRadius)
                    {
                        return double.NegativeInfinity;
                    }

                    // Rule 4: Reachability to final target in remaining steps
                    float distRemaining = Vector2.Distance(curPoint, finalTarget.GridPos);
                    if (distRemaining > (remainingSteps * env.ExplosionRange) + env.ExplosionRadius + 0.1f)
                    {
                        return double.NegativeInfinity;
                    }
                }

                // Find newly covered remnants
                int newRemnantsCount = 0;
                foreach (var r in env.Remnants)
                {
                    if (Vector2.Distance(curPoint, r.GridPos) <= env.ExplosionRadius)
                    {
                        if (hitRemnants.Add(r))
                        {
                            newRemnantsCount++;
                            // 1. Remnant Hit Base Score
                            totalScore += this.settings.RemnantHitBaseScore;
                            // 2. Slot Score
                            totalScore += r.RuneSlots * this.settings.RuneSlotMultiplier;
                            // 3. Update Active Propagated Rune
                            if (!string.IsNullOrEmpty(r.PropagatedRune))
                            {
                                activePropagatedRune = r.PropagatedRune;
                            }
                        }
                    }
                }

                // 4. Rune Score (Base Weight, plus FinalRuneBonus on the last bomb entering Final)
                if (!string.IsNullOrEmpty(activePropagatedRune))
                {
                    double baseW = this.settings.RuneWeights.GetValueOrDefault(activePropagatedRune, 20.0);
                    double runeScore = (i == n - 1) ? (baseW + this.settings.FinalRuneBonus) : baseW;
                    totalScore += runeScore;
                }

                // 5. Final Target Bonus on last bomb
                if (i == n - 1)
                {
                    totalScore += this.settings.FinalTargetBonus;
                }

                // 6. Empty Bomb vs Useful Bridge Penalty
                if (newRemnantsCount == 0)
                {
                    bool isUsefulBridge = false;

                    // 6a. Check if actively bridging towards an unvisited reachable remnant
                    foreach (var r in env.Remnants)
                    {
                        if (!hitRemnants.Contains(r) && r != finalTarget)
                        {
                            float distCurToR = Vector2.Distance(curPoint, r.GridPos);
                            float distPrevToR = Vector2.Distance(prevPoint, r.GridPos);
                            if (remainingSteps >= 2)
                            {
                                if (distCurToR <= ((remainingSteps - 1) * env.ExplosionRange) + env.ExplosionRadius)
                                {
                                    float distRToFinal = Vector2.Distance(r.GridPos, finalTarget.GridPos);
                                    if (distRToFinal <= ((remainingSteps - 2) * env.ExplosionRange) + env.ExplosionRadius)
                                    {
                                        if (distCurToR < distPrevToR - 3.0f)
                                        {
                                            isUsefulBridge = true;
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // 6b. Check if actively bridging towards Final Target
                    if (!isUsefulBridge)
                    {
                        float distCurToFinal = Vector2.Distance(curPoint, finalTarget.GridPos);
                        float distPrevToFinal = Vector2.Distance(prevPoint, finalTarget.GridPos);
                        if (distCurToFinal < distPrevToFinal - 3.0f && distCurToFinal <= (remainingSteps * env.ExplosionRange) + env.ExplosionRadius)
                        {
                            isUsefulBridge = true;
                        }
                    }

                    if (isUsefulBridge)
                    {
                        totalScore -= this.settings.UsefulBridgePenalty; // Bridge ไปหาเป้าหมายได้ = -20
                    }
                    else
                    {
                        totalScore -= this.settings.EmptyBombPenalty; // Bridge ที่ไม่ช่วยอะไร = -150
                    }
                }

                // 7. Future Potential Bonus: ระเบิดยังเหลือเยอะ + มี Remnant ที่ยัง Reachable
                if (i < n - 1 && remainingSteps >= 2)
                {
                    double stepPotential = 0.0;
                    foreach (var r in env.Remnants)
                    {
                        if (!hitRemnants.Contains(r) && r != finalTarget)
                        {
                            float distCurToR = Vector2.Distance(curPoint, r.GridPos);
                            float distRToFinal = Vector2.Distance(r.GridPos, finalTarget.GridPos);
                            if (distCurToR <= ((remainingSteps - 1) * env.ExplosionRange) + env.ExplosionRadius &&
                                distRToFinal <= ((remainingSteps - 2) * env.ExplosionRange) + env.ExplosionRadius)
                            {
                                double rValue = r.BaseRuneWeight + (r.RuneSlots * 10.0);
                                stepPotential += rValue * (this.settings.FuturePotentialBonusMultiplier / 100.0);
                            }
                        }
                    }
                    totalScore += stepPotential;
                }

                // 8. Travel Penalty
                double travelPenalty = (stepDist / env.ExplosionRange) * this.settings.TravelPenaltyMultiplier;
                totalScore -= travelPenalty;

                prevPoint = curPoint;
            }

            return totalScore;
        }

        public DetailedLootScore GetDetailedScore(List<Vector2> path, ExpeditionEnvironment env)
        {
            var pointsScore = new List<PerPointScoreInfo>();
            if (path == null || path.Count == 0 || env.FinalTarget == null)
            {
                return new DetailedLootScore(pointsScore, 0.0, env);
            }

            int n = path.Count;
            var finalTarget = env.FinalTarget;
            var hitRemnants = new HashSet<ExpeditionRemnant>();
            double totalScore = 0.0;
            var prevPoint = env.StartingPoint;
            string? activePropagatedRune = null;

            for (int i = 0; i < n; i++)
            {
                var curPoint = path[i];
                float stepDist = Vector2.Distance(prevPoint, curPoint);
                double localScore = 0.0;
                var newHits = new List<ExpeditionRemnant>();
                int remainingSteps = n - 1 - i;

                foreach (var r in env.Remnants)
                {
                    if (Vector2.Distance(curPoint, r.GridPos) <= env.ExplosionRadius)
                    {
                        if (hitRemnants.Add(r))
                        {
                            newHits.Add(r);
                            localScore += this.settings.RemnantHitBaseScore;
                            localScore += r.RuneSlots * this.settings.RuneSlotMultiplier;
                            if (!string.IsNullOrEmpty(r.PropagatedRune))
                            {
                                activePropagatedRune = r.PropagatedRune;
                            }
                        }
                    }
                }

                double runeScore = 0.0;
                if (!string.IsNullOrEmpty(activePropagatedRune))
                {
                    double baseW = this.settings.RuneWeights.GetValueOrDefault(activePropagatedRune, 20.0);
                    runeScore = (i == n - 1) ? (baseW + this.settings.FinalRuneBonus) : baseW;
                    localScore += runeScore;
                }

                if (i == n - 1)
                {
                    localScore += this.settings.FinalTargetBonus;
                }

                bool isUsefulBridge = false;
                if (newHits.Count == 0)
                {
                    // 6a. Check if actively bridging towards an unvisited reachable remnant
                    foreach (var r in env.Remnants)
                    {
                        if (!hitRemnants.Contains(r) && r != finalTarget)
                        {
                            float distCurToR = Vector2.Distance(curPoint, r.GridPos);
                            float distPrevToR = Vector2.Distance(prevPoint, r.GridPos);
                            if (remainingSteps >= 2)
                            {
                                if (distCurToR <= ((remainingSteps - 1) * env.ExplosionRange) + env.ExplosionRadius)
                                {
                                    float distRToFinal = Vector2.Distance(r.GridPos, finalTarget.GridPos);
                                    if (distRToFinal <= ((remainingSteps - 2) * env.ExplosionRange) + env.ExplosionRadius)
                                    {
                                        if (distCurToR < distPrevToR - 3.0f)
                                        {
                                            isUsefulBridge = true;
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // 6b. Check if actively bridging towards Final Target
                    if (!isUsefulBridge)
                    {
                        float distCurToFinal = Vector2.Distance(curPoint, finalTarget.GridPos);
                        float distPrevToFinal = Vector2.Distance(prevPoint, finalTarget.GridPos);
                        if (distCurToFinal < distPrevToFinal - 3.0f && distCurToFinal <= (remainingSteps * env.ExplosionRange) + env.ExplosionRadius)
                        {
                            isUsefulBridge = true;
                        }
                    }

                    if (isUsefulBridge)
                    {
                        localScore -= this.settings.UsefulBridgePenalty; // -20
                    }
                    else
                    {
                        localScore -= this.settings.EmptyBombPenalty; // -150
                    }
                }

                double stepPotential = 0.0;
                if (i < n - 1 && remainingSteps >= 2)
                {
                    foreach (var r in env.Remnants)
                    {
                        if (!hitRemnants.Contains(r) && r != finalTarget)
                        {
                            float distCurToR = Vector2.Distance(curPoint, r.GridPos);
                            float distRToFinal = Vector2.Distance(r.GridPos, finalTarget.GridPos);
                            if (distCurToR <= ((remainingSteps - 1) * env.ExplosionRange) + env.ExplosionRadius &&
                                distRToFinal <= ((remainingSteps - 2) * env.ExplosionRange) + env.ExplosionRadius)
                            {
                                double rValue = r.BaseRuneWeight + (r.RuneSlots * 10.0);
                                stepPotential += rValue * (this.settings.FuturePotentialBonusMultiplier / 100.0);
                            }
                        }
                    }
                    localScore += stepPotential;
                }

                double travelPenalty = (stepDist / env.ExplosionRange) * this.settings.TravelPenaltyMultiplier;
                localScore -= travelPenalty;

                totalScore += localScore;
                pointsScore.Add(new PerPointScoreInfo(curPoint, localScore, newHits, activePropagatedRune, runeScore, isUsefulBridge, stepPotential));
                prevPoint = curPoint;
            }

            return new DetailedLootScore(pointsScore, totalScore, env);
        }

        public IEnumerable<PathState> GetBestPathSeries(ExpeditionEnvironment environment)
        {
            if (environment.MaxExplosions <= 0 || environment.FinalTarget == null)
            {
                yield return new PathState(new List<Vector2>(), 0);
                yield break;
            }

            var bestPath = this.BuildPath(environment);
            double bestScore = this.GetScore(bestPath, environment);
            var batch = Enumerable.Range(0, Math.Max(20, this.settings.PathGenerationSize * 2))
                .Select(_ => this.BuildPath(environment))
                .ToList();

            while (true)
            {
                var batchWithValues = batch
                    .Select(x => (Score: this.GetScore(x, environment), Path: x))
                    .Where(x => !double.IsNegativeInfinity(x.Score))
                    .OrderByDescending(x => x.Score)
                    .Take(this.settings.PathGenerationSize)
                    .ToList();

                if (batchWithValues.Count > 0 && batchWithValues[0].Score > bestScore)
                {
                    bestScore = batchWithValues[0].Score;
                    bestPath = batchWithValues[0].Path;
                }

                var mixedAndMutated = batchWithValues
                    .Concat(batchWithValues)
                    .Select(i => i.Path)
                    .Select(x => Random.Shared.NextDouble() > this.settings.PathMutateChance
                        ? x
                        : this.MutatePath(x, environment));

                var newPaths = Enumerable.Range(0, (int)(this.settings.PathGenerationSize * this.settings.NewRandomPathInjectionRate))
                    .Select(_ => this.BuildPath(environment));

                var newBatch = mixedAndMutated.Append(bestPath).Concat(newPaths).ToList();

                yield return new PathState(bestPath, Math.Max(0, bestScore));
                batch = newBatch;
            }
        }

        private List<Vector2> BuildPath(ExpeditionEnvironment environment)
        {
            int maxBombs = environment.MaxExplosions;
            var finalTarget = environment.FinalTarget;
            if (finalTarget == null || maxBombs <= 0) return new List<Vector2>();

            var path = new List<Vector2>(maxBombs);
            var current = environment.StartingPoint;
            var remainingRemnants = environment.Remnants.Where(r => r != finalTarget).ToList();

            float reach = environment.ExplosionRange;
            float radius = environment.ExplosionRadius;

            for (int i = 0; i < maxBombs - 1; i++)
            {
                int remainingStepsAfterThis = maxBombs - 1 - (i + 1);
                var validCandidates = new List<(ExpeditionRemnant Remnant, Vector2 Pos)>();

                foreach (var r in remainingRemnants)
                {
                    var candPos = environment.FindWalkableCandidateNearRemnant(r, current, radius);
                    float distToCand = Vector2.Distance(current, candPos);
                    if (distToCand <= reach)
                    {
                        float distCandToFinal = Vector2.Distance(candPos, finalTarget.GridPos);
                        if (distCandToFinal <= ((remainingStepsAfterThis + 1) * reach) + radius && distCandToFinal > radius + 3.0f)
                        {
                            if (environment.HasLineOfSight(current, candPos))
                            {
                                validCandidates.Add((r, candPos));
                            }
                        }
                    }
                }

                Vector2 nextPos;
                if (validCandidates.Count > 0 && Random.Shared.NextDouble() < 0.75)
                {
                    var chosen = validCandidates[Random.Shared.Next(validCandidates.Count)];
                    remainingRemnants.Remove(chosen.Remnant);
                    nextPos = chosen.Pos;
                }
                else
                {
                    // Target direction: if remaining unvisited remnants exist, aim towards one of them, else finalTarget
                    var targetPos = finalTarget.GridPos;
                    if (remainingRemnants.Count > 0)
                    {
                        targetPos = remainingRemnants[Random.Shared.Next(remainingRemnants.Count)].GridPos;
                    }

                    var diff = targetPos - current;
                    float dist = diff.Length();
                    float baseAngle = dist > 0.001f ? MathF.Atan2(diff.Y, diff.X) : 0f;

                    float maxAllowedDistToFinal = ((remainingStepsAfterThis + 1) * reach) + radius;
                    float minAllowedDistToFinal = radius + 3.0f;

                    Vector2? bestNext = null;
                    float[] angleOffsets = { 0.0f, 0.3f, -0.3f, 0.6f, -0.6f, 1.0f, -1.0f, 1.5f, -1.5f, 2.0f, -2.0f };
                    float[] stepFractions = { 0.90f, 0.75f, 0.60f, 0.45f, 0.30f };

                    foreach (var angOffset in angleOffsets)
                    {
                        float ang = baseAngle + angOffset;
                        foreach (var sFrac in stepFractions)
                        {
                            float s = reach * sFrac;
                            var cand = current + new Vector2(MathF.Cos(ang) * s, MathF.Sin(ang) * s);
                            float dF = Vector2.Distance(cand, finalTarget.GridPos);

                            if (dF > minAllowedDistToFinal && dF <= maxAllowedDistToFinal)
                            {
                                if (environment.IsPointWalkable(cand) && environment.HasLineOfSight(current, cand))
                                {
                                    bestNext = cand;
                                    break;
                                }
                            }
                        }

                        if (bestNext.HasValue) break;
                    }

                    if (!bestNext.HasValue)
                    {
                        // Safe tangential orbit around final target
                        var fDiff = finalTarget.GridPos - current;
                        float fAng = MathF.Atan2(fDiff.Y, fDiff.X);
                        float tanAng = fAng + (MathF.PI * 0.5f);
                        float s = Math.Min(reach * 0.7f, 40.0f);
                        var fallback = current + new Vector2(MathF.Cos(tanAng) * s, MathF.Sin(tanAng) * s);
                        bestNext = fallback;
                    }

                    nextPos = bestNext.Value;
                }

                path.Add(RoundPoint(nextPos));
                current = nextPos;
            }

            // Final Bomb: step to a walkable candidate near final target
            var finalBombPos = environment.FindWalkableCandidateNearRemnant(finalTarget, current, radius);
            path.Add(RoundPoint(finalBombPos));
            return path;
        }

        private List<Vector2> MutatePath(List<Vector2> path, ExpeditionEnvironment environment)
        {
            if (path.Count <= 1) return path;
            var mutated = new List<Vector2>(path);
            int n = mutated.Count;
            var finalTarget = environment.FinalTarget;
            if (finalTarget == null) return mutated;

            float reach = environment.ExplosionRange;
            float radius = environment.ExplosionRadius;

            int idx = Random.Shared.Next(0, n);
            if (idx == n - 1)
            {
                // Mutate final bomb
                var prev = n > 1 ? mutated[n - 2] : environment.StartingPoint;
                var cand = environment.FindWalkableCandidateNearRemnant(finalTarget, prev, radius);
                if (Vector2.Distance(prev, cand) <= reach && environment.HasLineOfSight(prev, cand))
                {
                    mutated[idx] = RoundPoint(cand);
                }
            }
            else
            {
                // Mutate intermediate bomb
                var prev = idx == 0 ? environment.StartingPoint : mutated[idx - 1];
                var nxt = mutated[idx + 1];
                int remainingStepsAfterThis = n - 1 - (idx + 1);

                var candidates = new List<Vector2>();
                foreach (var r in environment.Remnants.Where(r => r != finalTarget))
                {
                    var candPos = environment.FindWalkableCandidateNearRemnant(r, prev, radius);
                    if (Vector2.Distance(prev, candPos) <= reach && Vector2.Distance(candPos, nxt) <= reach)
                    {
                        float dF = Vector2.Distance(candPos, finalTarget.GridPos);
                        if (dF > radius + 3.0f && dF <= ((remainingStepsAfterThis + 1) * reach) + radius)
                        {
                            if (environment.HasLineOfSight(prev, candPos) && environment.HasLineOfSight(candPos, nxt))
                            {
                                candidates.Add(candPos);
                            }
                        }
                    }
                }

                if (candidates.Count > 0 && Random.Shared.NextDouble() < 0.6)
                {
                    mutated[idx] = RoundPoint(candidates[Random.Shared.Next(candidates.Count)]);
                }
                else
                {
                    var curPt = mutated[idx];
                    for (int attempt = 0; attempt < 8; attempt++)
                    {
                        float angle = Random.Shared.NextSingle() * MathF.PI * 2f;
                        float step = (reach * 0.1f) + (Random.Shared.NextSingle() * reach * 0.35f);
                        var cand = curPt + new Vector2(MathF.Cos(angle) * step, MathF.Sin(angle) * step);

                        if (Vector2.Distance(prev, cand) <= reach && Vector2.Distance(cand, nxt) <= reach)
                        {
                            float dF = Vector2.Distance(cand, finalTarget.GridPos);
                            if (dF > radius + 3.0f && dF <= ((remainingStepsAfterThis + 1) * reach) + radius)
                            {
                                if (environment.IsPointWalkable(cand) &&
                                    environment.HasLineOfSight(prev, cand) &&
                                    environment.HasLineOfSight(cand, nxt))
                                {
                                    mutated[idx] = RoundPoint(cand);
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            return mutated;
        }

        private static Vector2 RoundPoint(Vector2 v) => new(MathF.Round(v.X, 1), MathF.Round(v.Y, 1));
    }
}
