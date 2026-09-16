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

            var bestPath = Enumerable.Repeat(Vector2.Zero, environment.MaxExplosions).ToList();
            var batch = Enumerable.Range(0, this.settings.PathGenerationSize * 2).Select(_ => this.BuildPath(environment)).ToList();

            while (true)
            {
                var batchWithValues = batch
                    .Select(x => (Score: this.GetScore(x, environment), Path: x))
                    .OrderByDescending(x => x.Score)
                    .Take(this.settings.PathGenerationSize)
                    .ToList();

                var mixedAndMutated = batchWithValues
                    .Concat(batchWithValues)
                    .Select(i => i.Path)
                    .Select(x => Random.Shared.NextDouble() > this.settings.PathMutateChance
                        ? x
                        : this.MutatePath(environment.StartingPoint, environment.ExplosionRange, x, environment));

                var newPaths = Enumerable.Range(0, (int)(this.settings.PathGenerationSize * this.settings.NewRandomPathInjectionRate))
                    .Select(_ => this.BuildPath(environment));

                var newBatch = mixedAndMutated.Append(bestPath).Concat(newPaths).ToList();

                if (batchWithValues[0].Score > this.GetScore(bestPath, environment))
                {
                    bestPath = batchWithValues[0].Path;
                }

                yield return new PathState(bestPath, this.GetScore(bestPath, environment));
                batch = newBatch;
            }
        }

        private List<Vector2> BuildPath(ExpeditionEnvironment environment)
        {
            var path = new List<Vector2>(environment.MaxExplosions);
            if (Random.Shared.Next(2) != 0 && environment.Relics.Count > 0)
            {
                float environmentExplosionRange = environment.ExplosionRange * 0.9f;
                var relic = environment.Relics[Random.Shared.Next(environment.Relics.Count)];
                var current = environment.StartingPoint;

                do
                {
                    var diff = relic.Pos - current;
                    if (diff.Length() < environmentExplosionRange)
                    {
                        path.Add(RoundPoint(relic.Pos));
                    }
                    else
                    {
                        current += diff * (environmentExplosionRange / diff.Length());
                        path.Add(RoundPoint(current));
                    }

                    if (!this.IsValidPlacement(path.SkipLast(1).LastOrDefault(environment.StartingPoint), environment, path.Last()))
                    {
                        path.RemoveAt(path.Count - 1);
                        break;
                    }
                } while (Vector2.Distance(current, relic.Pos) > environment.ExplosionRadius &&
                         path.Count < environment.MaxExplosions);
            }

            var point = path.LastOrDefault(environment.StartingPoint);
            while (path.Count < environment.MaxExplosions)
            {
                path.Add(point = this.GetNextPosition(point, point, environment.ExplosionRange, environment));
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

                if (!isValidChange)
                {
                    continue;
                }

                newPath[changeIndex] = changedPoint;
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
            var positionEnumerable = Enumerable.Range(1, 1000).Select(i => GetNextMaybeInvalidPosition(position, radius * MathF.Pow(0.99f, i)));
            return positionEnumerable.FirstOrDefault(x => this.IsValidPlacement(previousPosition, environment, x), position);
        }

        private bool IsValidPlacement(Vector2 previousPosition, ExpeditionEnvironment environment, Vector2 position)
        {
            return Vector2.Distance(previousPosition, position) <= environment.ExplosionRange &&
                   Vector2.Clamp(position, environment.ExclusionArea.Min, environment.ExclusionArea.Max) != position &&
                   Enumerable.Range(1, this.validatedPoints)
                       .Select(i => i / (float)this.validatedPoints)
                       .Select(l => Vector2.Lerp(previousPosition, position, l))
                       .All(environment.IsValidPlacement);
        }

        private static Vector2 GetNextMaybeInvalidPosition(Vector2 position, float radius)
        {
            radius = Math.Max(1f, radius);
            float length = Math.Max(1f, Random.Shared.Next(2) == 0 ? radius : GetWeightedLength(radius));
            float angle = Random.Shared.NextSingle() * MathF.PI * 2f;
            var (sin, cos) = MathF.SinCos(angle);
            var rawPoint = position + new Vector2(cos * length, sin * length);
            var roundedPoint = RoundPoint(rawPoint);

            while (Vector2.Distance(roundedPoint, position) > radius)
            {
                var diff = roundedPoint - position;
                var maxDiffComponent = Math.Abs(diff.X) > Math.Abs(diff.Y)
                    ? new Vector2(Math.Sign(diff.X), 0)
                    : new Vector2(0, Math.Sign(diff.Y));
                roundedPoint -= maxDiffComponent;
            }

            return roundedPoint;
        }

        private static Vector2 RoundPoint(Vector2 rawPoint)
        {
            return new Vector2(MathF.Round(rawPoint.X), MathF.Round(rawPoint.Y));
        }

        private static float GetWeightedLength(float radius)
        {
            return Math.Max(Random.Shared.NextSingle(), Random.Shared.NextSingle()) * radius;
        }
    }
}
