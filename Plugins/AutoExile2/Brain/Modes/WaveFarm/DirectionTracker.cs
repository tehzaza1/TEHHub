// <copyright file="DirectionTracker.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Modes.WaveFarm
{
    using System.Numerics;

    /// <summary>
    /// Tracks the player's forward movement direction using EMA smoothing.
    /// Used by loot filtering and wave clearing to determine what's "ahead" vs "behind."
    /// Ported directly from AutoExile 1 DirectionTracker.
    /// </summary>
    public class DirectionTracker
    {
        /// <summary>Normalized forward direction. Zero if player hasn't moved yet.</summary>
        public Vector2 Forward { get; private set; }

        /// <summary>True once we have a stable direction (player has moved enough).</summary>
        public bool HasDirection => this.Forward.LengthSquared() > 0.5f;

        private Vector2 prevPos;
        private bool initialized;

        public void Reset()
        {
            this.Forward = Vector2.Zero;
            this.initialized = false;
        }

        /// <summary>Call every tick with the player's current grid position.</summary>
        public void Update(Vector2 currentPos)
        {
            if (!this.initialized)
            {
                this.prevPos = currentPos;
                this.initialized = true;
                return;
            }

            var delta = currentPos - this.prevPos;
            this.prevPos = currentPos;

            if (delta.LengthSquared() < 1f)
            {
                return; // ignore micro-jitter / idle
            }

            var dir = Vector2.Normalize(delta);
            this.Forward = this.Forward.LengthSquared() < 0.01f
                ? dir
                : Vector2.Normalize((this.Forward * 0.8f) + (dir * 0.2f));
        }

        /// <summary>
        /// Is targetPos ahead of playerPos relative to our forward direction?
        /// threshold=0 means forward hemisphere, 0.5 means within ~60 degrees.
        /// </summary>
        public bool IsAhead(Vector2 playerPos, Vector2 targetPos, float threshold = 0f)
        {
            if (!this.HasDirection)
            {
                return true; // no direction yet -> treat everything as ahead
            }

            var toTarget = targetPos - playerPos;
            if (toTarget.LengthSquared() < 4f)
            {
                return true; // very close -> always "ahead"
            }

            return Vector2.Dot(Vector2.Normalize(toTarget), this.Forward) > threshold;
        }
    }
}
