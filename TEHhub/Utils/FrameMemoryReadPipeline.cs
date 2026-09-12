// <copyright file="FrameMemoryReadPipeline.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace TEHhub.Utils
{
    using System;
    using System.Collections.Generic;
    using TEHhub.RemoteObjects.States.InGameStateObjects;

    /// <summary>
    ///     Owns the redesigned read phase for one entity frame. The pipeline deliberately keeps
    ///     planning, native reads and lifetime in one object so future readers can be moved here
    ///     without spreading feature-flag checks through every remote object.
    /// </summary>
    internal sealed class FrameMemoryReadPipeline : IDisposable
    {
        private const int MinComponentsPerRange = 3;
        private const int MaxGapBetweenComponents = 0x100;
        private const int RangeTailBytes = 0x800;
        private const int MaxRangeBytes = 0x8000;

        [ThreadStatic]
        private static FrameMemoryReadPipeline? current;

        private SafeMemoryHandle.ReadCachePlanScope planScope;
        private readonly FrameMemoryReadPipeline? previous;
        private bool disposed;

        private FrameMemoryReadPipeline(
            SafeMemoryHandle reader,
            SafeMemoryHandle.ReadCachePlanScope planScope,
            FrameMemoryReadPipeline? previous)
        {
            this.planScope = planScope;
            this.previous = previous;
        }

        internal static bool IsActive => current is not null;

        /// <summary>
        ///     Builds and activates a frame plan from component addresses already known by the
        ///     entity cache. New/rebound entities safely use the legacy path until the next frame.
        /// </summary>
        internal static FrameMemoryReadPipeline Start(
            SafeMemoryHandle reader,
            IEnumerable<Entity> entities)
        {
            ArgumentNullException.ThrowIfNull(reader);
            ArgumentNullException.ThrowIfNull(entities);
            var ranges = BuildRanges(entities);
            var pipeline = new FrameMemoryReadPipeline(
                reader,
                reader.BeginReadCachePlan(ranges, enableDynamicCache: true),
                current);
            current = pipeline;
            return pipeline;
        }

        public void Dispose()
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            this.planScope.Dispose();
            if (ReferenceEquals(current, this))
            {
                current = this.previous;
            }
        }

        private static List<SafeMemoryHandle.ReadCacheRange> BuildRanges(
            IEnumerable<Entity> entities)
        {
            var addresses = new List<IntPtr>();
            foreach (var entity in entities)
            {
                entity.AppendFrameSnapshotAddresses(addresses);
            }

            if (addresses.Count < MinComponentsPerRange)
            {
                return new();
            }

            addresses.Sort(static (left, right) => left.ToInt64().CompareTo(right.ToInt64()));
            var ranges = new List<SafeMemoryHandle.ReadCacheRange>(addresses.Count / MinComponentsPerRange);
            var groupStart = 0;
            var previousAddress = addresses[0].ToInt64();

            for (var i = 1; i <= addresses.Count; i++)
            {
                var atEnd = i == addresses.Count;
                var nextAddress = atEnd ? 0 : addresses[i].ToInt64();
                var gap = atEnd ? long.MaxValue : nextAddress - previousAddress;
                var span = atEnd ? 0 : nextAddress - addresses[groupStart].ToInt64() + RangeTailBytes;
                var fits = !atEnd && gap >= 0 && gap <= MaxGapBetweenComponents && span <= MaxRangeBytes;
                if (fits)
                {
                    previousAddress = nextAddress;
                    continue;
                }

                var componentCount = i - groupStart;
                if (componentCount >= MinComponentsPerRange)
                {
                    var firstAddress = addresses[groupStart].ToInt64();
                    var byteCount = checked((int)(previousAddress - firstAddress + RangeTailBytes));
                    ranges.Add(new SafeMemoryHandle.ReadCacheRange(new IntPtr(firstAddress), byteCount));
                }

                groupStart = i;
                if (!atEnd)
                {
                    previousAddress = nextAddress;
                }
            }

            return ranges;
        }
    }
}
