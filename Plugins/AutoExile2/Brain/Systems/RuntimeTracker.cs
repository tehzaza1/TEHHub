// <copyright file="RuntimeTracker.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Systems
{
    using System;

    /// <summary>
    /// Tracks how long the bot has been actively running this session.
    /// Ported from AutoExile. Time spent paused does not count toward active duration.
    /// </summary>
    public class RuntimeTracker
    {
        private DateTime sessionStart = DateTime.Now;
        private DateTime? pausedAt;
        private TimeSpan accumulatedPause = TimeSpan.Zero;
        private bool wasRunning;
        private bool firstTick = true;

        /// <summary>
        /// Wall-clock time the session started (or was last reset).
        /// </summary>
        public DateTime SessionStart => this.sessionStart;

        /// <summary>
        /// Total time spent in the running state since session start (excluding pause time).
        /// </summary>
        public TimeSpan ActiveDuration
        {
            get
            {
                var raw = DateTime.Now - this.sessionStart;
                var pendingPause = this.pausedAt.HasValue ? DateTime.Now - this.pausedAt.Value : TimeSpan.Zero;
                var total = raw - this.accumulatedPause - pendingPause;
                return total < TimeSpan.Zero ? TimeSpan.Zero : total;
            }
        }

        /// <summary>
        /// True when the tracker is currently paused.
        /// </summary>
        public bool IsPaused => this.pausedAt.HasValue;

        /// <summary>
        /// Update pause/run state. Call once per frame.
        /// </summary>
        public void Tick(bool isRunning)
        {
            if (this.firstTick)
            {
                this.firstTick = false;
                this.wasRunning = isRunning;
                if (!isRunning)
                {
                    this.pausedAt = DateTime.Now;
                }

                return;
            }

            if (isRunning == this.wasRunning)
            {
                return;
            }

            if (isRunning)
            {
                // Resuming - close pause window
                if (this.pausedAt.HasValue)
                {
                    this.accumulatedPause += DateTime.Now - this.pausedAt.Value;
                    this.pausedAt = null;
                }
            }
            else
            {
                // Pausing - open pause window
                this.pausedAt = DateTime.Now;
            }

            this.wasRunning = isRunning;
        }

        /// <summary>
        /// Returns formatted active duration string (e.g. "01:23:45").
        /// </summary>
        public string FormattedDuration => this.ActiveDuration.ToString(@"hh\:mm\:ss");

        /// <summary>
        /// Resets the tracker to zero.
        /// </summary>
        public void Reset()
        {
            this.sessionStart = DateTime.Now;
            this.accumulatedPause = TimeSpan.Zero;
            this.pausedAt = this.wasRunning ? null : DateTime.Now;
        }
    }
}
