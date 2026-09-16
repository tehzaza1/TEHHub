namespace ExpeditionPathOptimizer
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Numerics;
    using ExpeditionPathOptimizer.PathPlannerData;

    public class PathPlanner
    {
        public record PerPointScoreInfo(Vector2 Point, double ScoreDiff, List<ExpeditionRemnant> NewRemnants, string? ActiveRune, double RuneScore);
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

                // Rule 3: Intermediate bombs (0 .. n-2) MUST NOT hit finalTarget
                if (i < n - 1)
                {
                    if (Vector2.Distance(curPoint, finalTarget.GridPos) <= env.ExplosionRadius)
                    {
                        return double.NegativeInfinity;
                    }

                    // Rule 4: Reachability to final target in remaining steps
                    int remainingSteps = n - 1 - i;
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

                // 6. Empty Bomb Penalty
                if (newRemnantsCount == 0)
                {
                    totalScore -= this.settings.EmptyBombPenalty;
                }

                // 7. Travel Penalty
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

                if (newHits.Count == 0)
                {
                    localScore -= this.settings.EmptyBombPenalty;
                }

                double travelPenalty = (stepDist / env.ExplosionRange) * this.settings.TravelPenaltyMultiplier;
                localScore -= travelPenalty;

                totalScore += localScore;
                pointsScore.Add(new PerPointScoreInfo(curPoint, localScore, newHits, activePropagatedRune, runeScore));
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
            var intermediateRemnants = environment.Remnants.Where(r => r != finalTarget).ToList();

            float reach = environment.ExplosionRange;
            float radius = environment.ExplosionRadius;

            for (int i = 0; i < maxBombs - 1; i++)
            {
                int remainingSteps = maxBombs - 1 - i;
                var validCandidates = new List<ExpeditionRemnant>();

                foreach (var r in intermediateRemnants)
                {
                    float distToR = Vector2.Distance(current, r.GridPos);
                    if (distToR <= reach + radius)
                    {
                        float distRToFinal = Vector2.Distance(r.GridPos, finalTarget.GridPos);
                        if (distRToFinal <= ((remainingSteps - 1) * reach) + radius)
                        {
                            validCandidates.Add(r);
                        }
                    }
                }

                Vector2 nextPos;
                if (validCandidates.Count > 0 && Random.Shared.NextDouble() < 0.8)
                {
                    var chosen = validCandidates[Random.Shared.Next(validCandidates.Count)];
                    var diff = chosen.GridPos - current;
                    float dist = diff.Length();
                    if (dist <= reach)
                    {
                        nextPos = chosen.GridPos;
                    }
                    else
                    {
                        float scale = reach * (0.80f + 0.18f * Random.Shared.NextSingle());
                        nextPos = current + Vector2.Normalize(diff) * scale;
                    }
                }
                else
                {
                    var diff = finalTarget.GridPos - current;
                    float dist = diff.Length();
                    float maxStep = Math.Min(reach, dist - radius * 0.5f);
                    float step = Math.Max(5f, maxStep * (0.70f + 0.28f * Random.Shared.NextSingle()));
                    nextPos = current + (dist > 0.001f ? Vector2.Normalize(diff) * step : Vector2.Zero);
                }

                path.Add(RoundPoint(nextPos));
                current = nextPos;
            }

            // Final Bomb: step to final target
            var finalDiff = finalTarget.GridPos - current;
            float finalDist = finalDiff.Length();
            Vector2 finalBombPos;
            if (finalDist <= reach)
            {
                finalBombPos = finalTarget.GridPos;
            }
            else
            {
                float scale = Math.Min(reach, finalDist);
                finalBombPos = current + (finalDist > 0.001f ? Vector2.Normalize(finalDiff) * scale : Vector2.Zero);
            }

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

            int idx = Random.Shared.Next(0, n - 1);
            var prev = idx == 0 ? environment.StartingPoint : mutated[idx - 1];
            float reach = environment.ExplosionRange;
            float radius = environment.ExplosionRadius;
            int remainingSteps = n - 1 - idx;

            var candidates = environment.Remnants
                .Where(r => r != finalTarget && Vector2.Distance(prev, r.GridPos) <= reach + radius)
                .Where(r => Vector2.Distance(r.GridPos, finalTarget.GridPos) <= ((remainingSteps - 1) * reach) + radius)
                .ToList();

            Vector2 newPoint;
            if (candidates.Count > 0 && Random.Shared.NextDouble() < 0.6)
            {
                var chosen = candidates[Random.Shared.Next(candidates.Count)];
                var diff = chosen.GridPos - prev;
                float d = diff.Length();
                newPoint = d <= reach ? chosen.GridPos : prev + Vector2.Normalize(diff) * reach * (0.85f + 0.14f * Random.Shared.NextSingle());
            }
            else
            {
                float angle = Random.Shared.NextSingle() * MathF.PI * 2f;
                float rDist = reach * (0.5f + 0.49f * Random.Shared.NextSingle());
                newPoint = prev + new Vector2(MathF.Cos(angle) * rDist, MathF.Sin(angle) * rDist);
            }

            if (Vector2.Distance(newPoint, finalTarget.GridPos) <= (remainingSteps * reach) + radius)
            {
                mutated[idx] = RoundPoint(newPoint);
            }

            return mutated;
        }

        private static Vector2 RoundPoint(Vector2 v) => new(MathF.Round(v.X, 1), MathF.Round(v.Y, 1));
    }
}
