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
            List<ExpeditionChest> NewChests,
            string? ActiveRune,
            double RuneScore,
            bool IsUsefulBridge);

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
            if (this.TryEvaluatePath(path, env, collectDetails: false, out double totalScore, out _))
            {
                return totalScore;
            }

            return double.NegativeInfinity;
        }

        public DetailedLootScore GetDetailedScore(List<Vector2> path, ExpeditionEnvironment env)
        {
            if (this.TryEvaluatePath(path, env, collectDetails: true, out double totalScore, out var pointsScore) && pointsScore != null)
            {
                return new DetailedLootScore(pointsScore, totalScore, env);
            }

            return new DetailedLootScore(new List<PerPointScoreInfo>(), double.NegativeInfinity, env);
        }

        private bool TryEvaluatePath(
            List<Vector2> path,
            ExpeditionEnvironment env,
            bool collectDetails,
            out double totalScore,
            out List<PerPointScoreInfo>? pointsScore)
        {
            totalScore = double.NegativeInfinity;
            pointsScore = null;

            if (path == null || path.Count == 0 || path.Count > env.MaxExplosions)
            {
                return false;
            }

            var finalTarget = env.FinalTarget;
            if (finalTarget == null)
            {
                return false;
            }

            int n = path.Count;

            // Rule 1: Final bomb (path[n-1]) MUST hit finalTarget
            var lastBomb = path[n - 1];
            float distToFinal = Vector2.Distance(lastBomb, finalTarget.GridPos);
            if (distToFinal > env.ExplosionRadius)
            {
                return false;
            }

            var hitRemnants = new HashSet<ExpeditionRemnant>();
            var hitChests = new HashSet<ExpeditionChest>();
            double accumulatedScore = 0.0;
            var prevPoint = env.StartingPoint;
            string? activePropagatedRune = null;
            List<PerPointScoreInfo>? detailedList = collectDetails ? new List<PerPointScoreInfo>(n) : null;

            for (int i = 0; i < n; i++)
            {
                var curPoint = path[i];
                float stepDist = Vector2.Distance(prevPoint, curPoint);

                // Rule 2: Step distance <= ExplosionRange
                if (stepDist > env.ExplosionRange * 1.01f)
                {
                    return false;
                }

                // Terrain Check: Point must be walkable and wire must have line of sight
                if (!env.IsPointWalkable(curPoint))
                {
                    return false;
                }

                if (!env.HasLineOfSight(prevPoint, curPoint))
                {
                    return false;
                }

                int remainingSteps = n - 1 - i;

                // Rule 3: Intermediate bombs (0 .. n-2) MUST NOT hit finalTarget
                if (i < n - 1)
                {
                    if (Vector2.Distance(curPoint, finalTarget.GridPos) <= env.ExplosionRadius)
                    {
                        return false;
                    }

                    // Rule 4: Reachability to final target in remaining steps
                    float distRemaining = Vector2.Distance(curPoint, finalTarget.GridPos);
                    if (distRemaining > (remainingSteps * env.ExplosionRange) + env.ExplosionRadius + 0.1f)
                    {
                        return false;
                    }
                }

                double localScore = 0.0;
                double runeScore = 0.0;
                string? currentBombActiveRune = activePropagatedRune;
                var newHits = new List<ExpeditionRemnant>();
                var newChestHits = new List<ExpeditionChest>();

                // 1. Final Target Bonus and Final Rune Bonus on last bomb
                if (i == n - 1)
                {
                    localScore += this.settings.FinalTargetBonus;
                    if (!string.IsNullOrEmpty(activePropagatedRune))
                    {
                        localScore += this.settings.FinalRuneBonus;
                        runeScore += this.settings.FinalRuneBonus;
                    }
                }

                // 2. Find newly covered remnants
                foreach (var r in env.Remnants)
                {
                    if (Vector2.Distance(curPoint, r.GridPos) <= env.ExplosionRadius)
                    {
                        if (hitRemnants.Add(r))
                        {
                            newHits.Add(r);
                            // Remnant Hit Base Score + Slot Score
                            localScore += this.settings.RemnantHitBaseScore;
                            localScore += r.RuneSlots * this.settings.RuneSlotMultiplier;

                            // Rune Base Weight scored ONCE upon discovery
                            if (!string.IsNullOrEmpty(r.PropagatedRune))
                            {
                                double w = this.settings.RuneWeights.GetValueOrDefault(r.PropagatedRune, 20.0);
                                localScore += w;
                                runeScore += w;
                            }
                        }
                    }
                }

                // 2b. Find newly covered reward chests
                if (env.Chests != null)
                {
                    foreach (var c in env.Chests)
                    {
                        if (Vector2.Distance(curPoint, c.GridPos) <= env.ExplosionRadius)
                        {
                            if (hitChests.Add(c))
                            {
                                newChestHits.Add(c);
                                localScore += this.settings.ChestHitBaseScore;
                            }
                        }
                    }
                }

                // 3. Update Active Propagated Rune for subsequent bombs (with conflict check)
                var newRunes = newHits
                    .Select(x => x.PropagatedRune)
                    .Where(x => !string.IsNullOrEmpty(x))
                    .Distinct()
                    .ToList();

                if (newRunes.Count > 1)
                {
                    // Single bomb hits multiple conflicting runes in V1
                    return false;
                }
                else if (newRunes.Count == 1)
                {
                    activePropagatedRune = newRunes[0];
                }

                // 4. Empty Bomb vs Useful Bridge Penalty & Short Bridge Penalty
                bool isUsefulBridge = false;
                if (newHits.Count == 0 && newChestHits.Count == 0)
                {
                    // Short Bridge Penalty: penalize empty bombs that waste reach (< 60% of reach)
                    double usage = stepDist / env.ExplosionRange;
                    if (usage < this.settings.ShortBridgePenaltyThreshold)
                    {
                        localScore -= (this.settings.ShortBridgePenaltyThreshold - usage) * this.settings.ShortBridgePenaltyMultiplier;
                    }

                    // 4a. Check if actively bridging towards an unvisited reachable remnant
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

                    // 4b. Check if actively bridging towards an unvisited reachable chest
                    if (!isUsefulBridge && env.Chests != null)
                    {
                        foreach (var c in env.Chests)
                        {
                            if (!hitChests.Contains(c))
                            {
                                float distCurToC = Vector2.Distance(curPoint, c.GridPos);
                                float distPrevToC = Vector2.Distance(prevPoint, c.GridPos);
                                if (remainingSteps >= 2)
                                {
                                    if (distCurToC <= ((remainingSteps - 1) * env.ExplosionRange) + env.ExplosionRadius)
                                    {
                                        float distCToFinal = Vector2.Distance(c.GridPos, finalTarget.GridPos);
                                        if (distCToFinal <= ((remainingSteps - 2) * env.ExplosionRange) + env.ExplosionRadius)
                                        {
                                            if (distCurToC < distPrevToC - 3.0f)
                                            {
                                                isUsefulBridge = true;
                                                break;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // 4c. Check if actively bridging towards Final Target
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
                        localScore -= this.settings.UsefulBridgePenalty; // Bridge ไปหาเป้าหมายได้ = -20
                    }
                    else
                    {
                        localScore -= this.settings.EmptyBombPenalty; // Bridge ที่ไม่ช่วยอะไร = -150
                    }
                }

                // 5. Travel Penalty (if configured)
                if (this.settings.TravelPenaltyMultiplier > 0.0)
                {
                    double travelPenalty = (stepDist / env.ExplosionRange) * this.settings.TravelPenaltyMultiplier;
                    localScore -= travelPenalty;
                }

                accumulatedScore += localScore;

                if (collectDetails && detailedList != null)
                {
                    detailedList.Add(new PerPointScoreInfo(curPoint, localScore, newHits, newChestHits, currentBombActiveRune, runeScore, isUsefulBridge));
                }

                prevPoint = curPoint;
            }

            totalScore = accumulatedScore;
            pointsScore = detailedList;
            return true;
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

            float reach = environment.ExplosionRange;
            float radius = environment.ExplosionRadius;

            // Dynamic Bomb Count: do not force using all bombs if reaching final earlier is better
            float distStartToFinal = Vector2.Distance(environment.StartingPoint, finalTarget.GridPos);
            int minBombs = Math.Min(maxBombs, Math.Max(1, (int)MathF.Ceiling(Math.Max(0f, distStartToFinal - radius) / reach)));
            int bombCount = Random.Shared.Next(minBombs, maxBombs + 1);

            var path = new List<Vector2>(bombCount);
            var current = environment.StartingPoint;
            var remainingRemnants = environment.Remnants.Where(r => r != finalTarget).ToList();
            var remainingChests = environment.Chests != null ? environment.Chests.ToList() : new List<ExpeditionChest>();

            for (int i = 0; i < bombCount - 1; i++)
            {
                int remainingStepsAfterThis = bombCount - 1 - (i + 1);

                double CalculateCandidateCoverageScore(
                    Vector2 candPos,
                    List<ExpeditionRemnant> uncovRemnants,
                    List<ExpeditionChest> uncovChests,
                    float distFromCurrent)
                {
                    double score = 0.0;
                    int estBridges = Math.Max(0, (int)MathF.Ceiling(Math.Max(0f, distFromCurrent - radius) / reach));
                    score -= estBridges * this.settings.UsefulBridgePenalty;

                    foreach (var r in uncovRemnants)
                    {
                        if (Vector2.Distance(candPos, r.GridPos) <= radius)
                        {
                            score += this.settings.RemnantHitBaseScore +
                                     (r.RuneSlots * this.settings.RuneSlotMultiplier) +
                                     r.BaseRuneWeight;
                        }
                    }

                    foreach (var c in uncovChests)
                    {
                        if (Vector2.Distance(candPos, c.GridPos) <= radius)
                        {
                            score += this.settings.ChestHitBaseScore;
                        }
                    }

                    return score;
                }

                // 1. Remnant Candidates (Directly Reachable)
                var remnantCandidates = new List<(Vector2 Pos, double TargetValue)>();
                foreach (var r in remainingRemnants)
                {
                    var candidatesNearR = environment.GetWalkableCandidatesNearRemnant(r, current, radius);
                    Vector2? bestPointForR = null;
                    double bestScoreForR = double.MinValue;

                    foreach (var candPos in candidatesNearR)
                    {
                        float distToCand = Vector2.Distance(current, candPos);
                        if (distToCand <= reach)
                        {
                            float distCandToFinal = Vector2.Distance(candPos, finalTarget.GridPos);
                            if (distCandToFinal > radius + 3.0f)
                            {
                                int bombsAfterThis = bombCount - (i + 1);
                                int bombsCandToFinal = Math.Max(1, (int)MathF.Ceiling(Math.Max(0f, distCandToFinal - radius) / reach));
                                if (bombsCandToFinal <= bombsAfterThis)
                                {
                                    if (environment.HasLineOfSight(current, candPos))
                                    {
                                        double coverageScore = CalculateCandidateCoverageScore(
                                            candPos,
                                            remainingRemnants,
                                            remainingChests,
                                            distToCand);

                                        if (coverageScore > bestScoreForR)
                                        {
                                            bestScoreForR = coverageScore;
                                            bestPointForR = candPos;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    if (bestPointForR.HasValue)
                    {
                        remnantCandidates.Add((bestPointForR.Value, bestScoreForR));
                    }
                }

                // 2. Chest Candidates (Directly Reachable - Only considered when no Remnant candidates exist)
                var chestCandidates = new List<(Vector2 Pos, double TargetValue)>();
                if (remnantCandidates.Count == 0 && remainingChests.Count > 0)
                {
                    foreach (var c in remainingChests)
                    {
                        var candidatesNearC = environment.GetWalkableCandidatesNearChest(c, current, radius);
                        Vector2? bestPointForC = null;
                        double bestScoreForC = double.MinValue;

                        foreach (var candPos in candidatesNearC)
                        {
                            float distToCand = Vector2.Distance(current, candPos);
                            if (distToCand <= reach)
                            {
                                float distCandToFinal = Vector2.Distance(candPos, finalTarget.GridPos);
                                if (distCandToFinal > radius + 3.0f)
                                {
                                    int bombsAfterThis = bombCount - (i + 1);
                                    int bombsCandToFinal = Math.Max(1, (int)MathF.Ceiling(Math.Max(0f, distCandToFinal - radius) / reach));
                                    if (bombsCandToFinal <= bombsAfterThis)
                                    {
                                        if (environment.HasLineOfSight(current, candPos))
                                        {
                                            double coverageScore = CalculateCandidateCoverageScore(
                                                candPos,
                                                remainingRemnants,
                                                remainingChests,
                                                distToCand);

                                            if (coverageScore > bestScoreForC)
                                            {
                                                bestScoreForC = coverageScore;
                                                bestPointForC = candPos;
                                            }
                                        }
                                    }
                                }
                            }
                        }

                        if (bestPointForC.HasValue)
                        {
                            chestCandidates.Add((bestPointForC.Value, bestScoreForC));
                        }
                    }
                }

                Vector2 nextPos;
                if (remnantCandidates.Count > 0)
                {
                    // Priority 1: Choose Remnant (Weighted random selection based on TargetValue)
                    double minVal = remnantCandidates.Min(c => c.TargetValue);
                    double offset = minVal < 1.0 ? (1.0 - minVal) : 0.0;
                    double totalWeight = remnantCandidates.Sum(c => c.TargetValue + offset);

                    double roll = Random.Shared.NextDouble() * totalWeight;
                    double accum = 0.0;
                    var chosen = remnantCandidates[0];
                    foreach (var cand in remnantCandidates)
                    {
                        accum += cand.TargetValue + offset;
                        if (roll <= accum)
                        {
                            chosen = cand;
                            break;
                        }
                    }

                    nextPos = chosen.Pos;
                }
                else if (chestCandidates.Count > 0)
                {
                    // Priority 2: Choose Chest (Weighted random selection based on TargetValue)
                    double minVal = chestCandidates.Min(c => c.TargetValue);
                    double offset = minVal < 1.0 ? (1.0 - minVal) : 0.0;
                    double totalWeight = chestCandidates.Sum(c => c.TargetValue + offset);

                    double roll = Random.Shared.NextDouble() * totalWeight;
                    double accum = 0.0;
                    var chosen = chestCandidates[0];
                    foreach (var cand in chestCandidates)
                    {
                        accum += cand.TargetValue + offset;
                        if (roll <= accum)
                        {
                            chosen = cand;
                            break;
                        }
                    }

                    nextPos = chosen.Pos;
                }
                else
                {
                    // Priority 3: Bridge or Final
                    var targetPos = finalTarget.GridPos;
                    int bombsAvailable = bombCount - i;

                    var reachableRemnants = new List<(Vector2 Pos, double Value)>();
                    foreach (var r in remainingRemnants)
                    {
                        float dToR = Vector2.Distance(current, r.GridPos);
                        float dRToFinal = Vector2.Distance(r.GridPos, finalTarget.GridPos);
                        int bToR = Math.Max(1, (int)MathF.Ceiling(Math.Max(0f, dToR - radius) / reach));
                        int bRToFinal = Math.Max(1, (int)MathF.Ceiling(Math.Max(0f, dRToFinal - radius) / reach));
                        if (bToR + bRToFinal <= bombsAvailable)
                        {
                            int estBridges = Math.Max(0, (int)MathF.Ceiling(Math.Max(0f, dToR - radius) / reach));
                            double targetVal =
                                this.settings.RemnantHitBaseScore +
                                (r.RuneSlots * this.settings.RuneSlotMultiplier) +
                                r.BaseRuneWeight -
                                (estBridges * this.settings.UsefulBridgePenalty);
                            reachableRemnants.Add((r.GridPos, targetVal));
                        }
                    }

                    if (reachableRemnants.Count > 0)
                    {
                        // Aim towards highest-value / weighted reachable Remnant
                        double minVal = reachableRemnants.Min(c => c.Value);
                        double offset = minVal < 1.0 ? (1.0 - minVal) : 0.0;
                        double totalWeight = reachableRemnants.Sum(c => c.Value + offset);
                        double roll = Random.Shared.NextDouble() * totalWeight;
                        double accum = 0.0;
                        var chosenTarget = reachableRemnants[0];
                        foreach (var rt in reachableRemnants)
                        {
                            accum += rt.Value + offset;
                            if (roll <= accum)
                            {
                                chosenTarget = rt;
                                break;
                            }
                        }

                        targetPos = chosenTarget.Pos;
                    }
                    else
                    {
                        // Else check reachable Chests
                        var reachableChests = new List<(Vector2 Pos, double Value)>();
                        foreach (var c in remainingChests)
                        {
                            float dToC = Vector2.Distance(current, c.GridPos);
                            float dCToFinal = Vector2.Distance(c.GridPos, finalTarget.GridPos);
                            int bToC = Math.Max(1, (int)MathF.Ceiling(Math.Max(0f, dToC - radius) / reach));
                            int bCToFinal = Math.Max(1, (int)MathF.Ceiling(Math.Max(0f, dCToFinal - radius) / reach));
                            if (bToC + bCToFinal <= bombsAvailable)
                            {
                                int estBridges = Math.Max(0, (int)MathF.Ceiling(Math.Max(0f, dToC - radius) / reach));
                                double targetVal =
                                    this.settings.ChestHitBaseScore -
                                    (estBridges * this.settings.UsefulBridgePenalty);
                                reachableChests.Add((c.GridPos, targetVal));
                            }
                        }

                        if (reachableChests.Count > 0)
                        {
                            // Aim towards weighted reachable Chest
                            double minVal = reachableChests.Min(c => c.Value);
                            double offset = minVal < 1.0 ? (1.0 - minVal) : 0.0;
                            double totalWeight = reachableChests.Sum(c => c.Value + offset);
                            double roll = Random.Shared.NextDouble() * totalWeight;
                            double accum = 0.0;
                            var chosenTarget = reachableChests[0];
                            foreach (var ct in reachableChests)
                            {
                                accum += ct.Value + offset;
                                if (roll <= accum)
                                {
                                    chosenTarget = ct;
                                    break;
                                }
                            }

                            targetPos = chosenTarget.Pos;
                        }
                    }

                    var diff = targetPos - current;
                    float dist = diff.Length();
                    float baseAngle = dist > 0.001f ? MathF.Atan2(diff.Y, diff.X) : 0f;

                    float maxAllowedDistToFinal = ((remainingStepsAfterThis + 1) * reach) + radius;
                    float minAllowedDistToFinal = radius + 3.0f;

                    Vector2? bestNext = null;
                    float[] angleOffsets = { 0.0f, 0.3f, -0.3f, 0.6f, -0.6f, 1.0f, -1.0f, 1.5f, -1.5f, 2.0f, -2.0f };
                    float[] stepFractions = { 0.95f, 0.85f, 0.70f, 0.55f, 0.40f };

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
                        float s = reach * 0.75f;
                        var fallback = current + new Vector2(MathF.Cos(tanAng) * s, MathF.Sin(tanAng) * s);
                        bestNext = fallback;
                    }

                    nextPos = bestNext.Value;
                }

                var placedPoint = RoundPoint(nextPos);

                // Bomb ลูกนี้ถือว่าเก็บทุก Remnant ที่อยู่ใน blast radius แล้ว
                remainingRemnants.RemoveAll(r =>
                    Vector2.Distance(placedPoint, r.GridPos) <= radius);

                // และเก็บทุก Chest ที่อยู่ใน blast radius แล้ว
                remainingChests.RemoveAll(c =>
                    Vector2.Distance(placedPoint, c.GridPos) <= radius);

                path.Add(placedPoint);
                current = placedPoint;
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

                bool CoveredByOtherBomb(Vector2 targetPos)
                {
                    for (int j = 0; j < mutated.Count; j++)
                    {
                        if (j == idx)
                            continue;

                        if (Vector2.Distance(mutated[j], targetPos) <= radius)
                            return true;
                    }

                    return false;
                }

                var uncoveredRemnants = environment.Remnants
                    .Where(r => r != finalTarget && !CoveredByOtherBomb(r.GridPos))
                    .ToList();

                var uncoveredChests = environment.Chests != null
                    ? environment.Chests.Where(c => !CoveredByOtherBomb(c.GridPos)).ToList()
                    : new List<ExpeditionChest>();

                double CalculateMutationCoverageScore(Vector2 candPos)
                {
                    double score = 0.0;
                    foreach (var r in uncoveredRemnants)
                    {
                        if (Vector2.Distance(candPos, r.GridPos) <= radius)
                        {
                            score += this.settings.RemnantHitBaseScore +
                                     (r.RuneSlots * this.settings.RuneSlotMultiplier) +
                                     r.BaseRuneWeight;
                        }
                    }
                    foreach (var c in uncoveredChests)
                    {
                        if (Vector2.Distance(candPos, c.GridPos) <= radius)
                        {
                            score += this.settings.ChestHitBaseScore;
                        }
                    }
                    return score;
                }

                // Priority 1: Remnant candidates
                var remnantCandidates = new List<(Vector2 Pos, double Score)>();
                foreach (var r in uncoveredRemnants)
                {
                    var candidatesNearR = environment.GetWalkableCandidatesNearRemnant(r, prev, radius);
                    Vector2? bestPointForR = null;
                    double bestScoreForR = double.MinValue;

                    foreach (var candPos in candidatesNearR)
                    {
                        if (Vector2.Distance(prev, candPos) <= reach && Vector2.Distance(candPos, nxt) <= reach)
                        {
                            float dF = Vector2.Distance(candPos, finalTarget.GridPos);
                            if (dF > radius + 3.0f && dF <= ((remainingStepsAfterThis + 1) * reach) + radius)
                            {
                                if (environment.HasLineOfSight(prev, candPos) && environment.HasLineOfSight(candPos, nxt))
                                {
                                    double score = CalculateMutationCoverageScore(candPos);
                                    if (score > bestScoreForR)
                                    {
                                        bestScoreForR = score;
                                        bestPointForR = candPos;
                                    }
                                }
                            }
                        }
                    }

                    if (bestPointForR.HasValue)
                    {
                        remnantCandidates.Add((bestPointForR.Value, bestScoreForR));
                    }
                }

                // Priority 2: Chest candidates (only if no Remnant candidates)
                var chestCandidates = new List<(Vector2 Pos, double Score)>();
                if (remnantCandidates.Count == 0 && uncoveredChests.Count > 0)
                {
                    foreach (var c in uncoveredChests)
                    {
                        var candidatesNearC = environment.GetWalkableCandidatesNearChest(c, prev, radius);
                        Vector2? bestPointForC = null;
                        double bestScoreForC = double.MinValue;

                        foreach (var candPos in candidatesNearC)
                        {
                            if (Vector2.Distance(prev, candPos) <= reach && Vector2.Distance(candPos, nxt) <= reach)
                            {
                                float dF = Vector2.Distance(candPos, finalTarget.GridPos);
                                if (dF > radius + 3.0f && dF <= ((remainingStepsAfterThis + 1) * reach) + radius)
                                {
                                    if (environment.HasLineOfSight(prev, candPos) && environment.HasLineOfSight(candPos, nxt))
                                    {
                                        double score = CalculateMutationCoverageScore(candPos);
                                        if (score > bestScoreForC)
                                        {
                                            bestScoreForC = score;
                                            bestPointForC = candPos;
                                        }
                                    }
                                }
                            }
                        }

                        if (bestPointForC.HasValue)
                        {
                            chestCandidates.Add((bestPointForC.Value, bestScoreForC));
                        }
                    }
                }

                if (remnantCandidates.Count > 0 && Random.Shared.NextDouble() < 0.7)
                {
                    double minVal = remnantCandidates.Min(c => c.Score);
                    double offset = minVal < 1.0 ? (1.0 - minVal) : 0.0;
                    double totalWeight = remnantCandidates.Sum(c => c.Score + offset);
                    double roll = Random.Shared.NextDouble() * totalWeight;
                    double accum = 0.0;
                    var chosen = remnantCandidates[0];
                    foreach (var cand in remnantCandidates)
                    {
                        accum += cand.Score + offset;
                        if (roll <= accum)
                        {
                            chosen = cand;
                            break;
                        }
                    }
                    mutated[idx] = RoundPoint(chosen.Pos);
                }
                else if (chestCandidates.Count > 0 && Random.Shared.NextDouble() < 0.7)
                {
                    double minVal = chestCandidates.Min(c => c.Score);
                    double offset = minVal < 1.0 ? (1.0 - minVal) : 0.0;
                    double totalWeight = chestCandidates.Sum(c => c.Score + offset);
                    double roll = Random.Shared.NextDouble() * totalWeight;
                    double accum = 0.0;
                    var chosen = chestCandidates[0];
                    foreach (var cand in chestCandidates)
                    {
                        accum += cand.Score + offset;
                        if (roll <= accum)
                        {
                            chosen = cand;
                            break;
                        }
                    }
                    mutated[idx] = RoundPoint(chosen.Pos);
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
