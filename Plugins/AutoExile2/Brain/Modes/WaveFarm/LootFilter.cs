// <copyright file="LootFilter.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Modes.WaveFarm
{
    using System.Collections.Generic;
    using System.Numerics;

    /// <summary>
    /// Candidate item on the ground for looting.
    /// </summary>
    public class LootCandidate
    {
        public uint EntityId { get; set; }
        public string ItemName { get; set; } = string.Empty;
        public Vector2 GridPos { get; set; }
        public double ChaosValue { get; set; }
        public float Distance { get; set; }
    }

    /// <summary>
    /// Wraps loot candidates with directional awareness for wave farming.
    /// Forward loot is always grabbed. Backtrack loot only if above value threshold.
    /// Tracks pickup metrics for efficiency analysis.
    /// Ported directly from AutoExile 1 LootFilter.
    /// </summary>
    public class LootFilter
    {
        public int PickupAttempts { get; private set; }
        public int PickupSuccesses { get; private set; }
        public int PickupsFailed { get; private set; }
        public float SuccessRate => this.PickupAttempts > 0 ? (float)this.PickupSuccesses / this.PickupAttempts : 0;

        private float grabRadius = 25f;

        /// <summary>
        /// Find the best forward loot candidate (ahead of player or within grab radius).
        /// Returns null if nothing worth picking up ahead.
        /// </summary>
        public LootCandidate? GetForwardLoot(
            IReadOnlyList<LootCandidate> candidates,
            Vector2 playerPos,
            DirectionTracker dir,
            float forwardAngle)
        {
            foreach (var c in candidates)
            {
                var itemPos = c.GridPos;
                var dist = Vector2.Distance(playerPos, itemPos);

                // Always grab if right next to us
                if (dist <= this.grabRadius)
                {
                    return c;
                }

                // Otherwise must be ahead
                if (dir.IsAhead(playerPos, itemPos, forwardAngle))
                {
                    return c;
                }
            }

            return null;
        }

        /// <summary>
        /// Find high-value loot behind the player that justifies backtracking.
        /// Returns null if nothing behind exceeds the threshold.
        /// </summary>
        public LootCandidate? GetBacktrackLoot(
            IReadOnlyList<LootCandidate> candidates,
            Vector2 playerPos,
            DirectionTracker dir,
            float forwardAngle,
            double valueThreshold)
        {
            LootCandidate? best = null;
            double bestValue = valueThreshold;

            foreach (var c in candidates)
            {
                if (c.ChaosValue < valueThreshold)
                {
                    continue;
                }

                var itemPos = c.GridPos;
                // Only consider items that are behind us
                if (!dir.IsAhead(playerPos, itemPos, forwardAngle) && c.ChaosValue > bestValue)
                {
                    bestValue = c.ChaosValue;
                    best = c;
                }
            }

            return best;
        }

        public void RecordAttempt() => this.PickupAttempts++;
        public void RecordSuccess() => this.PickupSuccesses++;
        public void RecordFailure() => this.PickupsFailed++;

        public void RestoreCounters(int attempts, int successes, int failures)
        {
            this.PickupAttempts = attempts;
            this.PickupSuccesses = successes;
            this.PickupsFailed = failures;
        }

        public void Reset()
        {
            this.PickupAttempts = 0;
            this.PickupSuccesses = 0;
            this.PickupsFailed = 0;
        }
    }
}
