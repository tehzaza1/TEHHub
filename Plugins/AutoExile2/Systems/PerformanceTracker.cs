// <copyright file="PerformanceTracker.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Systems
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;

    /// <summary>
    /// Per-tick performance and failure instrumentation ported from AutoExile.
    /// Tracks execution time of critical systems (Pathfinding, Combat, Exploration) and counts failures.
    /// </summary>
    public class PerformanceTracker
    {
        private const int BufferSize = 128;
        private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

        private readonly Dictionary<string, RingBuffer> sections = new();
        private readonly Dictionary<string, Dictionary<string, int>> failures = new();
        private readonly object syncLock = new();

        /// <summary>
        /// Times a code block using: using var _ = perf.SectionScope("Pathfinding");
        /// </summary>
        public Section SectionScope(string name) => new(this, name);

        /// <summary>
        /// Records an execution time sample for a named section.
        /// </summary>
        public void RecordSample(string name, double ms)
        {
            lock (this.syncLock)
            {
                if (!this.sections.TryGetValue(name, out var buf))
                {
                    buf = new RingBuffer(BufferSize);
                    this.sections[name] = buf;
                }

                buf.Add(ms);
            }
        }

        /// <summary>
        /// Increments a failure counter under a category.
        /// </summary>
        public void RecordFailure(string category, string reason)
        {
            if (string.IsNullOrEmpty(reason)) reason = "(unknown)";
            lock (this.syncLock)
            {
                if (!this.failures.TryGetValue(category, out var dict))
                {
                    dict = new Dictionary<string, int>();
                    this.failures[category] = dict;
                }

                dict[reason] = dict.TryGetValue(reason, out int count) ? count + 1 : 1;
            }
        }

        /// <summary>
        /// Computes statistics for a named section.
        /// </summary>
        public SectionStats GetStats(string name)
        {
            lock (this.syncLock)
            {
                if (!this.sections.TryGetValue(name, out var buf) || buf.Count == 0)
                {
                    return default;
                }

                return buf.ComputeStats();
            }
        }

        /// <summary>
        /// Gets all section statistics for JSON export.
        /// </summary>
        public Dictionary<string, object> GetAllStats()
        {
            var result = new Dictionary<string, object>();
            lock (this.syncLock)
            {
                foreach (var (k, v) in this.sections)
                {
                    if (v.Count > 0)
                    {
                        var s = v.ComputeStats();
                        result[k] = new
                        {
                            avgMs = Math.Round(s.AvgMs, 2),
                            minMs = Math.Round(s.MinMs, 2),
                            maxMs = Math.Round(s.MaxMs, 2),
                            samples = s.Samples,
                        };
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Gets all failure counters for JSON export.
        /// </summary>
        public Dictionary<string, Dictionary<string, int>> GetAllFailures()
        {
            lock (this.syncLock)
            {
                var copy = new Dictionary<string, Dictionary<string, int>>();
                foreach (var (cat, dict) in this.failures)
                {
                    copy[cat] = new Dictionary<string, int>(dict);
                }
                return copy;
            }
        }

        /// <summary>
        /// Disposable struct for timing scopes.
        /// </summary>
        public readonly struct Section : IDisposable
        {
            private readonly PerformanceTracker tracker;
            private readonly string name;
            private readonly long startTicks;

            internal Section(PerformanceTracker tracker, string name)
            {
                this.tracker = tracker;
                this.name = name;
                this.startTicks = Stopwatch.GetTimestamp();
            }

            public void Dispose()
            {
                double elapsed = (Stopwatch.GetTimestamp() - this.startTicks) * TicksToMs;
                this.tracker.RecordSample(this.name, elapsed);
            }
        }

        public readonly struct SectionStats
        {
            public readonly double AvgMs;
            public readonly double MinMs;
            public readonly double MaxMs;
            public readonly int Samples;

            public SectionStats(double avgMs, double minMs, double maxMs, int samples)
            {
                this.AvgMs = avgMs;
                this.MinMs = minMs;
                this.MaxMs = maxMs;
                this.Samples = samples;
            }
        }

        private sealed class RingBuffer
        {
            private readonly double[] buffer;
            private int index;

            public RingBuffer(int capacity)
            {
                this.buffer = new double[capacity];
            }

            public int Count { get; private set; }

            public void Add(double value)
            {
                this.buffer[this.index] = value;
                this.index = (this.index + 1) % this.buffer.Length;
                if (this.Count < this.buffer.Length) this.Count++;
            }

            public SectionStats ComputeStats()
            {
                if (this.Count == 0) return default;
                double sum = 0, min = double.MaxValue, max = double.MinValue;
                for (int i = 0; i < this.Count; i++)
                {
                    double v = this.buffer[i];
                    sum += v;
                    if (v < min) min = v;
                    if (v > max) max = v;
                }
                return new SectionStats(sum / this.Count, min, max, this.Count);
            }
        }
    }
}
