namespace ExpeditionPathOptimizer
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Numerics;
    using ExpeditionPathOptimizer.PathPlannerData;

    public class PathPlanner
    {
        public record PerPointLootScore(Vector2 Point, double ScoreDiff, int NewRelics, int Loot);
        public record DetailedLootScore(List<PerPointLootScore> PerPointScore, double TotalScore, ExpeditionEnvironment Environment);

        private readonly Dictionary<object, double> lootValueTable = new(ReferenceEqualityComparer.Instance);
        private readonly ExpeditionPathOptimizerSettings settings;
        private readonly int validatedPoints;

        public PathPlanner(ExpeditionPathOptimizerSettings settings)
        {
            this.settings = settings;
            this.validatedPoints = this.settings.ValidatedIntermediatePoints + 1;
        }

        public void Init(ExpeditionEnvironment environment)
        {
            this.lootValueTable.Clear();
            foreach (var (_, loot) in environment.Loot)
            {
                this.lootValueTable[loot] = loot switch
                {
                    RunicMonster => environment.IsLogbook ? this.settings.RunicMonsterLogbookWeight : this.settings.RunicMonsterWeight,
                    Chest chest => this.settings.ChestWeights.GetValueOrDefault(chest.Type, 1.0f),
                    NormalMonster => this.settings.NormalMonsterWeight,
                    _ => 1.0
                };
            }
            this.lootValueTable.TrimExcess();
        }

        public double GetScore(List<Vector2> state, ExpeditionEnvironment environment)
        {
            var relics = new HashSet<IExpeditionRelic>();
            var lootList = new HashSet<IExpeditionLoot>();
            double score = 0.0;

            foreach (var explosionPoint in state)
            {
                foreach (var (_, relic) in environment.Relics.Where(x => Vector2.Distance(x.Pos, explosionPoint) <= environment.ExplosionRadius))
                {
                    relics.Add(relic);
                }

                double localScore = 0.0;
                foreach (var (_, loot) in environment.Loot
                             .Where(x => Vector2.Distance(x.Pos, explosionPoint) <= environment.ExplosionRadius)
                             .Where(x => lootList.Add(x.Loot)))
                {
                    var (multiplier, sum) = relics
                        .Select(x => x.GetScoreMultiplier(loot))
                        .Aggregate((mult: 1.0, sum: 0.0), (a, b) => (a.mult * b.Multiplier, a.sum + b.Increase));

                    if (this.lootValueTable.TryGetValue(loot, out var val))
                    {
                        localScore += val * multiplier * (1.0 + sum);
                    }
                    else
                    {
                        localScore += 1.0 * multiplier * (1.0 + sum);
                    }
                }

                score += localScore;
            }

            return score;
        }

        public DetailedLootScore GetDetailedScore(List<Vector2> state, ExpeditionEnvironment environment)
        {
            var relics = new HashSet<IExpeditionRelic>();
            var lootList = new HashSet<IExpeditionLoot>();
            var scorePerPoint = new List<PerPointLootScore>();
            double score = 0.0;

            foreach (var explosionPoint in state)
            {
                int newRelics = 0;
                int newLoot = 0;

                foreach (var (_, relic) in environment.Relics.Where(x => Vector2.Distance(x.Pos, explosionPoint) <= environment.ExplosionRadius))
                {
                    if (relics.Add(relic))
                    {
                        newRelics++;
                    }
                }

                double localScore = 0.0;
                foreach (var (_, loot) in environment.Loot
                             .Where(x => Vector2.Distance(x.Pos, explosionPoint) <= environment.ExplosionRadius)
                             .Where(x => lootList.Add(x.Loot)))
                {
                    newLoot++;
                    var (multiplier, sum) = relics
                        .Select(x => x.GetScoreMultiplier(loot))
                        .Aggregate((mult: 1.0, sum: 0.0), (a, b) => (a.mult * b.Multiplier, a.sum + b.Increase));

                    if (this.lootValueTable.TryGetValue(loot, out var val))
                    {
                        localScore += val * multiplier * (1.0 + sum);
                    }
                    else
                    {
                        localScore += 1.0 * multiplier * (1.0 + sum);
                    }
                }

                scorePerPoint.Add(new PerPointLootScore(explosionPoint, localScore, newRelics, newLoot));
                score += localScore;
            }

            return new DetailedLootScore(scorePerPoint, score, environment);
        }

        public IEnumerable<PathState> GetBestPathSeries(ExpeditionEnvironment environment)
        {
            if (environment.MaxExplosions <= 0)
            {
                yield return new PathState(new List<Vector2>(), 0);
                yield break;
            }

            var bestPath = this.BuildPath(environment);
            double bestScore = this.GetScore(bestPath, environment);
            var batch = Enumerable.Range(0, Math.Max(20, this.settings.PathGenerationSize * 2)).Select(_ => this.BuildPath(environment)).ToList();

            while (true)
            {
                var batchWithValues = batch
                    .Select(x => (Score: this.GetScore(x, environment), Path: x))
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
                        : this.MutatePath(environment.StartingPoint, environment.ExplosionRange, x, environment));

                var newPaths = Enumerable.Range(0, (int)(this.settings.PathGenerationSize * this.settings.NewRandomPathInjectionRate))
                    .Select(_ => this.BuildPath(environment));

                var newBatch = mixedAndMutated.Append(bestPath).Concat(newPaths).ToList();

                yield return new PathState(bestPath, bestScore);
                batch = newBatch;
            }
        }

        private List<Vector2> BuildPath(ExpeditionEnvironment environment)
        {
            var path = new List<Vector2>(environment.MaxExplosions);
            var current = environment.StartingPoint;

            var targets = new List<Vector2>();
            foreach (var (pos, _) in environment.Relics) targets.Add(pos);
            foreach (var (pos, _) in environment.Loot) targets.Add(pos);

            if (targets.Count > 0)
            {
                var target = targets[Random.Shared.Next(targets.Count)];
                while (path.Count < environment.MaxExplosions && Vector2.Distance(current, target) > environment.ExplosionRadius)
                {
                    var diff = target - current;
                    float dist = diff.Length();
                    Vector2 nextPoint;
                    if (dist <= environment.ExplosionRange)
                    {
                        nextPoint = RoundPoint(target);
                    }
                    else
                    {
                        float stepDist = environment.ExplosionRange * (0.80f + 0.18f * Random.Shared.NextSingle());
                        nextPoint = RoundPoint(current + Vector2.Normalize(diff) * stepDist);
                    }

                    if (this.IsValidPlacement(current, environment, nextPoint))
                    {
                        path.Add(nextPoint);
                        current = nextPoint;
                    }
                    else
                    {
                        nextPoint = this.GetNextPosition(current, current, environment.ExplosionRange, environment);
                        path.Add(nextPoint);
                        current = nextPoint;
                    }
                }
            }

            while (path.Count < environment.MaxExplosions)
            {
                if (targets.Count > 0 && Random.Shared.Next(2) == 0)
                {
                    var target = targets[Random.Shared.Next(targets.Count)];
                    var diff = target - current;
                    float dist = diff.Length();
                    Vector2 nextPoint;
                    if (dist > 0.1f && dist <= environment.ExplosionRange)
                    {
                        nextPoint = RoundPoint(target);
                    }
                    else if (dist > 0.1f)
                    {
                        nextPoint = RoundPoint(current + Vector2.Normalize(diff) * environment.ExplosionRange * (0.8f + 0.18f * Random.Shared.NextSingle()));
                    }
                    else
                    {
                        nextPoint = this.GetNextPosition(current, current, environment.ExplosionRange, environment);
                    }

                    if (this.IsValidPlacement(current, environment, nextPoint))
                    {
                        path.Add(nextPoint);
                        current = nextPoint;
                        continue;
                    }
                }

                var randPoint = this.GetNextPosition(current, current, environment.ExplosionRange, environment);
                path.Add(randPoint);
                current = randPoint;
            }

            return path;
        }

        private List<Vector2> MutatePath(Vector2 startingPoint, float radius, List<Vector2> originalPath, ExpeditionEnvironment environment)
        {
            int mutateTimes = Random.Shared.Next(1, 4);
            var newPath = originalPath.ToList();

            for (int mutation = 0; mutation < mutateTimes; mutation++)
            {
                if (Random.Shared.Next(2) == 0 && this.TryApplySkipMutation(newPath, environment))
                {
                    continue;
                }

                if (Random.Shared.Next(2) == 0 && this.TryApplySwapMutation(newPath, environment))
                {
                    continue;
                }

                if (newPath.Count == 0) break;
                int changeIndex = Random.Shared.Next(newPath.Count);
                Vector2 changedPoint;
                var previousPoint = changeIndex == 0 ? startingPoint : newPath[changeIndex - 1];
                var changingPoint = newPath[changeIndex];
                int tries = 0;
                bool isValidChange;

                do
                {
                    if (Random.Shared.Next(2) == 0)
                    {
                        changedPoint = this.GetNextPosition(previousPoint, previousPoint, radius, environment);
                    }
                    else
                    {
                        float allowedMoveRadius = Math.Max(radius - Vector2.Distance(previousPoint, changingPoint), radius / 5f);
                        changedPoint = this.GetNextPosition(changingPoint, previousPoint, allowedMoveRadius, environment);
                    }

                    isValidChange = Vector2.Distance(previousPoint, changedPoint) <= radius &&
                                    (changeIndex == newPath.Count - 1 ||
                                     this.IsValidPlacement(changedPoint, environment, newPath[changeIndex + 1]));
                } while (!isValidChange && tries++ < 10);

                if (isValidChange)
                {
                    newPath[changeIndex] = changedPoint;
                }
            }

            return newPath;
        }

        private bool TryApplySkipMutation(List<Vector2> path, ExpeditionEnvironment environment)
        {
            int pathCount = path.Count - 2;
            if (pathCount <= 0) return false;

            int searchStartOffset = Random.Shared.Next(0, pathCount + 1);
            if (searchStartOffset == pathCount)
            {
                int injectionIndex = Random.Shared.Next(0, pathCount);
                var midpoint = RoundPoint((path[injectionIndex] + path[injectionIndex + 1]) / 2f);

                if (this.IsValidPlacement(path[injectionIndex], environment, midpoint) &&
                    this.IsValidPlacement(midpoint, environment, path[injectionIndex + 1]))
                {
                    path.RemoveAt(path.Count - 1);
                    path.Insert(injectionIndex + 1, midpoint);
                    return true;
                }

                searchStartOffset = 0;
            }

            for (int i = 0; i < pathCount; i++)
            {
                int checkIndex = 1 + (i + searchStartOffset) % pathCount;
                if (this.IsValidPlacement(path[checkIndex - 1], environment, path[checkIndex + 1]))
                {
                    path.RemoveAt(checkIndex);
                    path.Add(this.GetNextPosition(path.Last(), path.Last(), environment.ExplosionRange, environment));
                    return true;
                }
            }

            return false;
        }

        private bool TryApplySwapMutation(List<Vector2> path, ExpeditionEnvironment environment)
        {
            int pathCount = path.Count - 3;
            if (pathCount <= 0) return false;

            int searchStartOffset = Random.Shared.Next(0, pathCount);
            for (int i = 0; i < pathCount; i++)
            {
                int checkIndex = 1 + (i + searchStartOffset) % pathCount;
                if (this.IsValidPlacement(path[checkIndex - 1], environment, path[checkIndex + 1]) &&
                    this.IsValidPlacement(path[checkIndex], environment, path[checkIndex + 2]))
                {
                    (path[checkIndex + 1], path[checkIndex]) = (path[checkIndex], path[checkIndex + 1]);
                    return true;
                }
            }

            return false;
        }

        private Vector2 GetNextPosition(Vector2 position, Vector2 previousPosition, float radius, ExpeditionEnvironment environment)
        {
            radius = Math.Max(5f, radius);
            for (int i = 0; i < 40; i++)
            {
                float angle = Random.Shared.NextSingle() * MathF.PI * 2f;
                float length = (0.5f + 0.5f * Random.Shared.NextSingle()) * radius;
                var (sin, cos) = MathF.SinCos(angle);
                var pt = RoundPoint(position + new Vector2(cos * length, sin * length));
                if (this.IsValidPlacement(previousPosition, environment, pt))
                {
                    return pt;
                }
            }
            return position;
        }

        private bool IsValidPlacement(Vector2 previousPosition, ExpeditionEnvironment environment, Vector2 position)
        {
            if (Vector2.Distance(previousPosition, position) > environment.ExplosionRange)
                return false;

            if (environment.ExclusionArea.Min != environment.ExclusionArea.Max)
            {
                if (position.X >= environment.ExclusionArea.Min.X && position.X <= environment.ExclusionArea.Max.X &&
                    position.Y >= environment.ExclusionArea.Min.Y && position.Y <= environment.ExclusionArea.Max.Y)
                {
                    return false;
                }
            }

            return true;
        }

        private static Vector2 RoundPoint(Vector2 rawPoint)
        {
            return new Vector2(MathF.Round(rawPoint.X), MathF.Round(rawPoint.Y));
        }
    }
}
