namespace ExpeditionPathOptimizer
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Numerics;
    using ExpeditionPathOptimizer.PathPlannerData;

    public class PathPlanner
    {
        public record PlannedRecipeSelection(
            ExpeditionRemnant Remnant,
            RuneshapeRecipeOffer Recipe,
            IReadOnlyList<string> NewlyAcquiredRunes,
            double PropagatedRuneScore,
            double EconomicScore);

        public record PerPointScoreInfo(
            Vector2 Point,
            double ScoreDiff,
            List<ExpeditionRemnant> NewRemnants,
            List<ExpeditionChest> NewChests,
            IReadOnlyList<string> AcquiredRunes,
            double RuneScore,
            bool IsUsefulBridge,
            IReadOnlyList<PlannedRecipeSelection> PlannedRecipes,
            double RecipeEconomicScore = 0.0,
            double OathExposurePenalty = 0.0,
            double BacktrackPenalty = 0.0);

        public record DetailedLootScore(
            List<PerPointScoreInfo> PerPointScore,
            double TotalScore,
            ExpeditionEnvironment Environment,
            bool WasPruned = false,
            int DpStateCapUsed = SearchRecipeDpStateCap,
            int FinalistsReRanked = 1);

        public const int SearchRecipeDpStateCap = 256;
        public static readonly int[] FinalRefinementDpCapStages = new[] { 1024, 4096, 16384, 65536 };
        public const int FinalHardCeilingDpStateCap = 65536;
        public const int FinalistCount = 8;
        public const int MaxThreadCandidatePoolSize = FinalistCount * 4;

        private readonly ExpeditionPathOptimizerSettings settings;

        public PathPlanner(ExpeditionPathOptimizerSettings settings)
        {
            this.settings = settings;
        }

        public void Init(ExpeditionEnvironment environment)
        {
        }

        public static string GetPathSignature(IReadOnlyList<Vector2>? path)
        {
            if (path == null || path.Count == 0) return string.Empty;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < path.Count; i++)
            {
                if (i > 0) sb.Append(';');
                sb.Append(MathF.Round(path[i].X, 1).ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
                sb.Append(',');
                sb.Append(MathF.Round(path[i].Y, 1).ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        public double GetScore(
            List<Vector2> path,
            ExpeditionEnvironment env,
            int recipeDpStateCap = SearchRecipeDpStateCap,
            CancellationToken token = default,
            Func<bool>? isCancelledCheck = null)
        {
            if (this.TryEvaluatePath(path, env, collectDetails: false, out double totalScore, out _, out _, recipeDpStateCap, token, isCancelledCheck))
            {
                return totalScore;
            }

            return double.NegativeInfinity;
        }

        public DetailedLootScore GetDetailedScore(
            List<Vector2> path,
            ExpeditionEnvironment env,
            int recipeDpStateCap = SearchRecipeDpStateCap,
            int finalistsReRanked = 1,
            CancellationToken token = default,
            Func<bool>? isCancelledCheck = null)
        {
            if (this.TryEvaluatePath(path, env, collectDetails: true, out double totalScore, out var pointsScore, out bool wasPruned, recipeDpStateCap, token, isCancelledCheck) && pointsScore != null)
            {
                return new DetailedLootScore(pointsScore, totalScore, env, wasPruned, recipeDpStateCap, finalistsReRanked);
            }

            return new DetailedLootScore(new List<PerPointScoreInfo>(), double.NegativeInfinity, env, false, recipeDpStateCap, finalistsReRanked);
        }

        public DetailedLootScore RefineAndSelectBestPath(
            IEnumerable<List<Vector2>> candidatePaths,
            ExpeditionEnvironment env,
            CancellationToken token = default,
            Func<bool>? isCancelledCheck = null)
        {
            if (candidatePaths == null || env == null)
            {
                return new DetailedLootScore(new List<PerPointScoreInfo>(), double.NegativeInfinity, env!, false, SearchRecipeDpStateCap, 0);
            }

            var uniqueCandidates = new Dictionary<string, List<Vector2>>(StringComparer.Ordinal);
            foreach (var path in candidatePaths)
            {
                if (path == null || path.Count == 0) continue;
                string sig = GetPathSignature(path);
                if (!uniqueCandidates.ContainsKey(sig))
                {
                    uniqueCandidates[sig] = path;
                }
            }

            if (uniqueCandidates.Count == 0)
            {
                return new DetailedLootScore(new List<PerPointScoreInfo>(), double.NegativeInfinity, env, false, SearchRecipeDpStateCap, 0);
            }

            var initialRanked = new List<(List<Vector2> Path, double SearchScore, string Sig)>();
            foreach (var kvp in uniqueCandidates)
            {
                if (token.IsCancellationRequested || (isCancelledCheck != null && isCancelledCheck()))
                {
                    return new DetailedLootScore(new List<PerPointScoreInfo>(), double.NegativeInfinity, env, false, SearchRecipeDpStateCap, 0);
                }

                double score = this.GetScore(kvp.Value, env, SearchRecipeDpStateCap, token, isCancelledCheck);
                if (!double.IsNegativeInfinity(score))
                {
                    initialRanked.Add((kvp.Value, score, kvp.Key));
                }
            }

            if (token.IsCancellationRequested || (isCancelledCheck != null && isCancelledCheck()) || initialRanked.Count == 0)
            {
                return new DetailedLootScore(new List<PerPointScoreInfo>(), double.NegativeInfinity, env, false, SearchRecipeDpStateCap, 0);
            }

            var finalists = initialRanked
                .OrderByDescending(x => x.SearchScore)
                .ThenBy(x => x.Sig, StringComparer.Ordinal)
                .Take(FinalistCount)
                .ToList();

            int finalistCount = finalists.Count;
            var refinedResults = new List<DetailedLootScore>(finalistCount);

            foreach (var f in finalists)
            {
                if (token.IsCancellationRequested || (isCancelledCheck != null && isCancelledCheck()))
                {
                    return new DetailedLootScore(new List<PerPointScoreInfo>(), double.NegativeInfinity, env, false, SearchRecipeDpStateCap, 0);
                }

                DetailedLootScore detailed = this.GetDetailedScore(f.Path, env, FinalRefinementDpCapStages[0], finalistCount, token, isCancelledCheck);
                if (token.IsCancellationRequested || (isCancelledCheck != null && isCancelledCheck()))
                {
                    return new DetailedLootScore(new List<PerPointScoreInfo>(), double.NegativeInfinity, env, false, SearchRecipeDpStateCap, 0);
                }

                if (detailed.WasPruned)
                {
                    for (int stage = 1; stage < FinalRefinementDpCapStages.Length; stage++)
                    {
                        if (token.IsCancellationRequested || (isCancelledCheck != null && isCancelledCheck()))
                        {
                            return new DetailedLootScore(new List<PerPointScoreInfo>(), double.NegativeInfinity, env, false, SearchRecipeDpStateCap, 0);
                        }

                        int cap = FinalRefinementDpCapStages[stage];
                        detailed = this.GetDetailedScore(f.Path, env, cap, finalistCount, token, isCancelledCheck);
                        if (token.IsCancellationRequested || (isCancelledCheck != null && isCancelledCheck()))
                        {
                            return new DetailedLootScore(new List<PerPointScoreInfo>(), double.NegativeInfinity, env, false, SearchRecipeDpStateCap, 0);
                        }

                        if (!detailed.WasPruned)
                        {
                            break;
                        }
                    }
                }

                refinedResults.Add(detailed);
            }

            var winner = refinedResults
                .OrderByDescending(d => d.TotalScore)
                .ThenBy(d => GetPathSignature(d.PerPointScore?.Select(p => p.Point).ToList()), StringComparer.Ordinal)
                .FirstOrDefault();

            return winner ?? new DetailedLootScore(new List<PerPointScoreInfo>(), double.NegativeInfinity, env, false, SearchRecipeDpStateCap, finalistCount);
        }

        public bool TryEvaluatePath(
            List<Vector2> path,
            ExpeditionEnvironment env,
            bool collectDetails,
            out double totalScore,
            out List<PerPointScoreInfo>? pointsScore,
            int recipeDpStateCap = SearchRecipeDpStateCap)
        {
            return this.TryEvaluatePath(path, env, collectDetails, out totalScore, out pointsScore, out _, recipeDpStateCap, default, null);
        }

        public bool TryEvaluatePath(
            List<Vector2> path,
            ExpeditionEnvironment env,
            bool collectDetails,
            out double totalScore,
            out List<PerPointScoreInfo>? pointsScore,
            out bool wasPruned,
            int recipeDpStateCap = SearchRecipeDpStateCap,
            CancellationToken token = default,
            Func<bool>? isCancelledCheck = null)
        {
            totalScore = double.NegativeInfinity;
            pointsScore = null;
            wasPruned = false;

            bool hasCancelCheck = token.CanBeCanceled || isCancelledCheck != null;
            bool IsCancelled() => (token.CanBeCanceled && token.IsCancellationRequested) || (isCancelledCheck != null && isCancelledCheck());

            if (hasCancelCheck && IsCancelled())
            {
                return false;
            }

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
            var prevPoint = env.StartingPoint;
            var prevPrevPoint = env.StartingPoint;

            var bombNewHits = new List<List<ExpeditionRemnant>>(n);
            var bombNewChestHits = new List<List<ExpeditionChest>>(n);
            var bombBaseScores = new double[n];
            var bombBacktrackPenalties = new double[n];
            var bombIsUsefulBridge = new bool[n];

            for (int i = 0; i < n; i++)
            {
                if (hasCancelCheck && IsCancelled())
                {
                    totalScore = double.NegativeInfinity;
                    pointsScore = null;
                    wasPruned = false;
                    return false;
                }

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

                double localBaseScore = 0.0;
                var newHits = new List<ExpeditionRemnant>();
                var newChestHits = new List<ExpeditionChest>();

                // 1. Final Target Bonus on last bomb
                if (i == n - 1)
                {
                    localBaseScore += this.settings.FinalTargetBonus;
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
                            localBaseScore += this.settings.RemnantHitBaseScore;
                            localBaseScore += r.RuneSlots * this.settings.RuneSlotMultiplier;
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
                                localBaseScore += this.settings.ChestHitBaseScore;
                            }
                        }
                    }
                }

                // 4. Empty Bomb vs Useful Bridge Penalty & Short Bridge Penalty
                bool isUsefulBridge = false;
                if (newHits.Count == 0 && newChestHits.Count == 0)
                {
                    // Short Bridge Penalty: penalize empty bombs that waste reach (< 60% of reach)
                    double usage = stepDist / env.ExplosionRange;
                    if (usage < this.settings.ShortBridgePenaltyThreshold)
                    {
                        localBaseScore -= (this.settings.ShortBridgePenaltyThreshold - usage) * this.settings.ShortBridgePenaltyMultiplier;
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
                        localBaseScore -= this.settings.UsefulBridgePenalty; // Bridge ไปหาเป้าหมายได้ = -20
                    }
                    else
                    {
                        localBaseScore -= this.settings.EmptyBombPenalty; // Bridge ที่ไม่ช่วยอะไร = -150
                    }
                }

                // 5. Travel Penalty (if configured)
                if (this.settings.TravelPenaltyMultiplier > 0.0)
                {
                    double travelPenalty = (stepDist / env.ExplosionRange) * this.settings.TravelPenaltyMultiplier;
                    localBaseScore -= travelPenalty;
                }

                // 6. Dedicated Backtrack / Reversal Penalty
                double stepBacktrackPenalty = 0.0;
                if (i > 0)
                {
                    var vPrev = prevPoint - prevPrevPoint;
                    var vCurrent = curPoint - prevPoint;
                    float prevLen = vPrev.Length();
                    float currentLen = vCurrent.Length();
                    if (prevLen > 0.001f && currentLen > 0.001f)
                    {
                        float dot = Vector2.Dot(vPrev / prevLen, vCurrent / currentLen);
                        double reversalFactor = Math.Max(0.0, (double)-dot);
                        stepBacktrackPenalty = this.settings.BacktrackPenaltyPerGrid * (double)currentLen * reversalFactor;
                    }
                }

                localBaseScore -= stepBacktrackPenalty;

                bombNewHits.Add(newHits);
                bombNewChestHits.Add(newChestHits);
                bombBaseScores[i] = localBaseScore;
                bombBacktrackPenalties[i] = stepBacktrackPenalty;
                bombIsUsefulBridge[i] = isUsefulBridge;
                prevPrevPoint = prevPoint;
                prevPoint = curPoint;
            }

            // Route-wide Recipe Optimizer DP across bomb sequence
            var currentStates = new List<RouteRecipeState> { new(0UL, 0.0, 0, 0, -1, 0.0, null) };
            var stepHistory = collectDetails ? new List<List<RouteRecipeState>>(n) : null;
            int oathRuneIndex = RuneshapeRecipePredictor.GetRuneIndex("Oath");

            for (int i = 0; i < n; i++)
            {
                if (hasCancelCheck && IsCancelled())
                {
                    totalScore = double.NegativeInfinity;
                    pointsScore = null;
                    wasPruned = false;
                    return false;
                }

                var newlyHit = bombNewHits[i];
                var remnantsWithOffers = new List<ExpeditionRemnant>();
                for (int rIdx = 0; rIdx < newlyHit.Count; rIdx++)
                {
                    var rem = newlyHit[rIdx];
                    var offers = (rem.ReducedRecipeOffers != null && rem.ReducedRecipeOffers.Count > 0)
                        ? rem.ReducedRecipeOffers
                        : rem.RecipeOffers;
                    if (offers != null && offers.Count > 0)
                    {
                        remnantsWithOffers.Add(rem);
                    }
                }

                int totalNewlyHitRuneSlots = 0;
                for (int rIdx = 0; rIdx < newlyHit.Count; rIdx++)
                {
                    totalNewlyHitRuneSlots += newlyHit[rIdx].RuneSlots;
                }

                if (remnantsWithOffers.Count == 0)
                {
                    var nextPassStates = new List<RouteRecipeState>(currentStates.Count);
                    for (int s = 0; s < currentStates.Count; s++)
                    {
                        var curSt = currentStates[s];
                        bool oathWasActiveBeforeBomb = oathRuneIndex >= 0 && (curSt.Mask & (1UL << oathRuneIndex)) != 0;
                        double oathExposurePenalty = oathWasActiveBeforeBomb ? (totalNewlyHitRuneSlots * this.settings.OathPerSlotExposurePenalty) : 0.0;
                        nextPassStates.Add(new RouteRecipeState(
                            curSt.Mask,
                            curSt.Score - oathExposurePenalty,
                            curSt.ComboWeight,
                            curSt.RewardCount,
                            s,
                            oathExposurePenalty,
                            Array.Empty<PlannedRecipeSelection>()));
                    }
                    currentStates = nextPassStates;
                    if (collectDetails)
                    {
                        stepHistory!.Add(currentStates);
                    }
                }
                else
                {
                    var offersLists = new List<IReadOnlyList<RuneshapeRecipeOffer>>(remnantsWithOffers.Count);
                    for (int rIdx = 0; rIdx < remnantsWithOffers.Count; rIdx++)
                    {
                        var rem = remnantsWithOffers[rIdx];
                        var offers = (rem.ReducedRecipeOffers != null && rem.ReducedRecipeOffers.Count > 0)
                            ? rem.ReducedRecipeOffers
                            : rem.RecipeOffers;
                        offersLists.Add(offers!);
                    }

                    var nextByMask = new Dictionary<ulong, RouteRecipeState>();
                    bool cancelledDuringSearch = false;
                    int combinationNodeCounter = 0;

                    for (int sIdx = 0; sIdx < currentStates.Count; sIdx++)
                    {
                        if (hasCancelCheck && (sIdx & 31) == 0 && IsCancelled())
                        {
                            cancelledDuringSearch = true;
                            break;
                        }

                        var st = currentStates[sIdx];
                        bool oathWasActiveBeforeBomb = oathRuneIndex >= 0 && (st.Mask & (1UL << oathRuneIndex)) != 0;
                        double oathExposurePenalty = oathWasActiveBeforeBomb ? (totalNewlyHitRuneSlots * this.settings.OathPerSlotExposurePenalty) : 0.0;

                        void SearchCombinations(int remIdx, RuneshapeRecipeOffer[] currentOffers)
                        {
                            if (cancelledDuringSearch) return;

                            if (hasCancelCheck && ((++combinationNodeCounter & 127) == 0) && IsCancelled())
                            {
                                cancelledDuringSearch = true;
                                return;
                            }

                            if (remIdx == remnantsWithOffers.Count)
                            {
                                ulong curMask = st.Mask;
                                double addPropagatedScore = 0.0;
                                double addEconomicScore = 0.0;
                                int addCombo = 0;
                                int addReward = 0;
                                List<PlannedRecipeSelection>? plannedList = collectDetails ? new List<PlannedRecipeSelection>(remnantsWithOffers.Count) : null;

                                for (int k = 0; k < remnantsWithOffers.Count; k++)
                                {
                                    var off = currentOffers[k];
                                    double economicScore = (off.PriceChaos > 0f) ? (off.PriceChaos * this.settings.RecipePriceScoreMultiplier) : 0.0;
                                    addEconomicScore += economicScore;
                                    addCombo += off.ComboWeight;
                                    addReward += off.RewardCount;
                                    var rem = remnantsWithOffers[k];
                                    double remPropagatedScore = 0.0;
                                    List<string>? newRunes = collectDetails ? new List<string>() : null;

                                    if (off.PropagatedRunes != null)
                                    {
                                        for (int pIdx = 0; pIdx < off.PropagatedRunes.Count; pIdx++)
                                        {
                                            var rune = off.PropagatedRunes[pIdx];
                                            int rIndex = RuneshapeRecipePredictor.GetRuneIndex(rune);
                                            if (rIndex >= 0 && (curMask & (1UL << rIndex)) == 0)
                                            {
                                                curMask |= (1UL << rIndex);
                                                double rScore = this.settings.GetPropagatedRuneScore(rune);
                                                remPropagatedScore += rScore;
                                                addPropagatedScore += rScore;
                                                newRunes?.Add(rune);
                                            }
                                        }
                                    }

                                    plannedList?.Add(new PlannedRecipeSelection(rem, off, newRunes ?? (IReadOnlyList<string>)Array.Empty<string>(), remPropagatedScore, economicScore));
                                }

                                ulong candMask = curMask;
                                double candScore = st.Score + addPropagatedScore + addEconomicScore - oathExposurePenalty;
                                int candCombo = st.ComboWeight + addCombo;
                                int candReward = st.RewardCount + addReward;

                                if (!nextByMask.TryGetValue(candMask, out var existing))
                                {
                                    nextByMask[candMask] = new RouteRecipeState(candMask, candScore, candCombo, candReward, sIdx, oathExposurePenalty, plannedList);
                                }
                                else
                                {
                                    bool isBetter = false;
                                    if (candScore > existing.Score + 1e-6)
                                    {
                                        isBetter = true;
                                    }
                                    else if (Math.Abs(candScore - existing.Score) <= 1e-6)
                                    {
                                        if (candCombo > existing.ComboWeight)
                                        {
                                            isBetter = true;
                                        }
                                        else if (candCombo == existing.ComboWeight)
                                        {
                                            if (candReward > existing.RewardCount)
                                            {
                                                isBetter = true;
                                            }
                                        }
                                    }

                                    if (isBetter)
                                    {
                                        nextByMask[candMask] = new RouteRecipeState(candMask, candScore, candCombo, candReward, sIdx, oathExposurePenalty, plannedList);
                                    }
                                }
                                return;
                            }

                            var list = offersLists[remIdx];
                            for (int oIdx = 0; oIdx < list.Count; oIdx++)
                            {
                                currentOffers[remIdx] = list[oIdx];
                                SearchCombinations(remIdx + 1, currentOffers);
                                if (cancelledDuringSearch) return;
                            }
                        }

                        SearchCombinations(0, new RuneshapeRecipeOffer[remnantsWithOffers.Count]);
                        if (cancelledDuringSearch) break;
                    }

                    if (cancelledDuringSearch || (hasCancelCheck && IsCancelled()))
                    {
                        totalScore = double.NegativeInfinity;
                        pointsScore = null;
                        wasPruned = false;
                        return false;
                    }

                    var nextList = nextByMask.Values.ToList();
                    if (nextList.Count > recipeDpStateCap)
                    {
                        wasPruned = true;
                        if (hasCancelCheck && IsCancelled())
                        {
                            totalScore = double.NegativeInfinity;
                            pointsScore = null;
                            wasPruned = false;
                            return false;
                        }

                        nextList = nextList
                            .OrderByDescending(s => s.Score)
                            .ThenByDescending(s => s.ComboWeight)
                            .ThenByDescending(s => s.RewardCount)
                            .ThenBy(s => s.Mask)
                            .Take(recipeDpStateCap)
                            .ToList();
                    }

                    currentStates = nextList;
                    if (collectDetails)
                    {
                        stepHistory!.Add(currentStates);
                    }
                }
            }

            // Select optimal route-wide recipe combination with fully deterministic tie-breaking
            var bestState = currentStates[0];
            for (int s = 1; s < currentStates.Count; s++)
            {
                var cand = currentStates[s];
                if (cand.Score > bestState.Score + 1e-6)
                {
                    bestState = cand;
                }
                else if (Math.Abs(cand.Score - bestState.Score) <= 1e-6)
                {
                    if (cand.ComboWeight > bestState.ComboWeight)
                    {
                        bestState = cand;
                    }
                    else if (cand.ComboWeight == bestState.ComboWeight)
                    {
                        if (cand.RewardCount > bestState.RewardCount)
                        {
                            bestState = cand;
                        }
                        else if (cand.RewardCount == bestState.RewardCount)
                        {
                            if (cand.Mask < bestState.Mask)
                            {
                                bestState = cand;
                            }
                        }
                    }
                }
            }

            double baseSum = 0.0;
            for (int i = 0; i < n; i++) baseSum += bombBaseScores[i];
            totalScore = baseSum + bestState.Score;

            if (collectDetails && stepHistory != null)
            {
                var bombPlannedPerStep = new IReadOnlyList<PlannedRecipeSelection>[n];
                var bombOathExposurePerStep = new double[n];
                int traceStateIdx = stepHistory[n - 1].IndexOf(bestState);

                for (int step = n - 1; step >= 0; step--)
                {
                    if (traceStateIdx >= 0 && traceStateIdx < stepHistory[step].Count)
                    {
                        var st = stepHistory[step][traceStateIdx];
                        bombPlannedPerStep[step] = st.BombPlanned ?? Array.Empty<PlannedRecipeSelection>();
                        bombOathExposurePerStep[step] = st.StepOathExposure;
                        traceStateIdx = st.ParentIndex;
                    }
                    else
                    {
                        bombPlannedPerStep[step] = Array.Empty<PlannedRecipeSelection>();
                        bombOathExposurePerStep[step] = 0.0;
                    }
                }

                var detailedList = new List<PerPointScoreInfo>(n);
                var cumulativeRunes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (int step = 0; step < n; step++)
                {
                    double stepRuneScore = 0.0;
                    double stepEconomicScore = 0.0;
                    var stepPlans = bombPlannedPerStep[step];
                    for (int pIdx = 0; pIdx < stepPlans.Count; pIdx++)
                    {
                        var plan = stepPlans[pIdx];
                        stepRuneScore += plan.PropagatedRuneScore;
                        stepEconomicScore += plan.EconomicScore;
                        for (int rIdx = 0; rIdx < plan.NewlyAcquiredRunes.Count; rIdx++)
                        {
                            cumulativeRunes.Add(plan.NewlyAcquiredRunes[rIdx]);
                        }
                    }

                    double stepOathPenalty = bombOathExposurePerStep[step];
                    double stepBacktrack = bombBacktrackPenalties[step];
                    double stepTotalScore = bombBaseScores[step] + stepRuneScore + stepEconomicScore - stepOathPenalty;

                    detailedList.Add(new PerPointScoreInfo(
                        path[step],
                        stepTotalScore,
                        bombNewHits[step],
                        bombNewChestHits[step],
                        cumulativeRunes.ToList(),
                        stepRuneScore,
                        bombIsUsefulBridge[step],
                        stepPlans,
                        stepEconomicScore,
                        stepOathPenalty,
                        stepBacktrack));
                }

                pointsScore = detailedList;
            }

            return true;
        }

        private readonly struct RouteRecipeState
        {
            public readonly ulong Mask;
            public readonly double Score;
            public readonly int ComboWeight;
            public readonly int RewardCount;
            public readonly int ParentIndex;
            public readonly double StepOathExposure;
            public readonly IReadOnlyList<PlannedRecipeSelection>? BombPlanned;

            public RouteRecipeState(
                ulong mask,
                double score,
                int comboWeight,
                int rewardCount,
                int parentIndex = -1,
                double stepOathExposure = 0.0,
                IReadOnlyList<PlannedRecipeSelection>? bombPlanned = null)
            {
                this.Mask = mask;
                this.Score = score;
                this.ComboWeight = comboWeight;
                this.RewardCount = rewardCount;
                this.ParentIndex = parentIndex;
                this.StepOathExposure = stepOathExposure;
                this.BombPlanned = bombPlanned;
            }
        }

        public IEnumerable<PathState> GetBestPathSeries(
            ExpeditionEnvironment environment,
            Action<IReadOnlyList<(double Score, List<Vector2> Path)>>? onGenerationScored = null)
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

                if (batchWithValues.Count > 0)
                {
                    onGenerationScored?.Invoke(batchWithValues);

                    if (batchWithValues[0].Score > bestScore)
                    {
                        bestScore = batchWithValues[0].Score;
                        bestPath = batchWithValues[0].Path;
                    }
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
                                     r.CalculateBaseRuneWeight(this.settings);
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

                // 1. Unified Blast-Coverage Candidate Pool (Remnants + Chests)
                var rawCandidatePositions = new List<Vector2>();
                foreach (var r in remainingRemnants)
                {
                    rawCandidatePositions.AddRange(environment.GetWalkableCandidatesNearRemnant(r, current, radius));
                }
                foreach (var c in remainingChests)
                {
                    rawCandidatePositions.AddRange(environment.GetWalkableCandidatesNearChest(c, current, radius));
                }

                // Deduplicate candidate positions before scoring
                var seenPoints = new HashSet<(int, int)>();
                var uniqueCandidates = new List<Vector2>();
                foreach (var pt in rawCandidatePositions)
                {
                    var key = ((int)MathF.Round(pt.X * 10f), (int)MathF.Round(pt.Y * 10f));
                    if (seenPoints.Add(key))
                    {
                        uniqueCandidates.Add(pt);
                    }
                }

                var validCandidates = new List<(Vector2 Pos, double TargetValue)>();
                foreach (var candPos in uniqueCandidates)
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
                                if (environment.IsPointWalkable(candPos) && environment.HasLineOfSight(current, candPos))
                                {
                                    double coverageScore = CalculateCandidateCoverageScore(
                                        candPos,
                                        remainingRemnants,
                                        remainingChests,
                                        distToCand);

                                    if (coverageScore > 0.0)
                                    {
                                        validCandidates.Add((candPos, coverageScore));
                                    }
                                }
                            }
                        }
                    }
                }

                Vector2 nextPos;
                if (validCandidates.Count > 0)
                {
                    // Weighted random selection based on TargetValue
                    double minVal = validCandidates.Min(c => c.TargetValue);
                    double offset = minVal < 1.0 ? (1.0 - minVal) : 0.0;
                    double totalWeight = validCandidates.Sum(c => c.TargetValue + offset);

                    double roll = Random.Shared.NextDouble() * totalWeight;
                    double accum = 0.0;
                    var chosen = validCandidates[0];
                    foreach (var cand in validCandidates)
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
                                r.CalculateBaseRuneWeight(this.settings) -
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
            return this.SimplifyPath(path, environment);
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
                                     r.CalculateBaseRuneWeight(this.settings);
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

                // Unified Blast-Coverage Candidate Pool for Mutation
                var rawCandidatePositions = new List<Vector2>();
                foreach (var r in uncoveredRemnants)
                {
                    rawCandidatePositions.AddRange(environment.GetWalkableCandidatesNearRemnant(r, prev, radius));
                }
                foreach (var c in uncoveredChests)
                {
                    rawCandidatePositions.AddRange(environment.GetWalkableCandidatesNearChest(c, prev, radius));
                }

                // Deduplicate candidate positions before scoring
                var seenPoints = new HashSet<(int, int)>();
                var uniqueCandidates = new List<Vector2>();
                foreach (var pt in rawCandidatePositions)
                {
                    var key = ((int)MathF.Round(pt.X * 10f), (int)MathF.Round(pt.Y * 10f));
                    if (seenPoints.Add(key))
                    {
                        uniqueCandidates.Add(pt);
                    }
                }

                var validCandidates = new List<(Vector2 Pos, double Score)>();
                foreach (var candPos in uniqueCandidates)
                {
                    if (Vector2.Distance(prev, candPos) <= reach && Vector2.Distance(candPos, nxt) <= reach)
                    {
                        float dF = Vector2.Distance(candPos, finalTarget.GridPos);
                        if (dF > radius + 3.0f && dF <= ((remainingStepsAfterThis + 1) * reach) + radius)
                        {
                            if (environment.IsPointWalkable(candPos) &&
                                environment.HasLineOfSight(prev, candPos) &&
                                environment.HasLineOfSight(candPos, nxt))
                            {
                                double score = CalculateMutationCoverageScore(candPos);
                                if (score > 0.0)
                                {
                                    validCandidates.Add((candPos, score));
                                }
                            }
                        }
                    }
                }

                if (validCandidates.Count > 0 && Random.Shared.NextDouble() < 0.7)
                {
                    double minVal = validCandidates.Min(c => c.Score);
                    double offset = minVal < 1.0 ? (1.0 - minVal) : 0.0;
                    double totalWeight = validCandidates.Sum(c => c.Score + offset);
                    double roll = Random.Shared.NextDouble() * totalWeight;
                    double accum = 0.0;
                    var chosen = validCandidates[0];
                    foreach (var cand in validCandidates)
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

            return this.SimplifyPath(mutated, environment);
        }

        private List<Vector2> SimplifyPath(List<Vector2> path, ExpeditionEnvironment environment)
        {
            if (path == null || path.Count <= 1) return path ?? new List<Vector2>();

            float reach = environment.ExplosionRange;
            var currentPath = new List<Vector2>(path);
            double currentScore = this.GetScore(currentPath, environment);
            if (double.IsNegativeInfinity(currentScore)) return path;

            bool changed;
            do
            {
                changed = false;
                int n = currentPath.Count;
                if (n <= 1) break;

                // Try removing each intermediate bomb (0 to n - 2, do NOT remove final bomb at n - 1)
                for (int i = 0; i < n - 1; i++)
                {
                    var prev = (i == 0) ? environment.StartingPoint : currentPath[i - 1];
                    var next = currentPath[i + 1];

                    // Check direct connection validity from prev to next
                    if (Vector2.Distance(prev, next) > reach) continue;
                    if (!environment.IsPointWalkable(next)) continue;
                    if (!environment.HasLineOfSight(prev, next)) continue;

                    var pathWithoutI = new List<Vector2>(currentPath);
                    pathWithoutI.RemoveAt(i);

                    double candidateScore = this.GetScore(pathWithoutI, environment);
                    if (!double.IsNegativeInfinity(candidateScore) && candidateScore >= currentScore)
                    {
                        currentPath = pathWithoutI;
                        currentScore = candidateScore;
                        changed = true;
                        break;
                    }
                }
            }
            while (changed);

            return currentPath;
        }

        private static Vector2 RoundPoint(Vector2 v) => new(MathF.Round(v.X, 1), MathF.Round(v.Y, 1));
    }
}
