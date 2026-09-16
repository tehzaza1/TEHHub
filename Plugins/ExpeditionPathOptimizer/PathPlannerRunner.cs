namespace ExpeditionPathOptimizer
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Numerics;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using System.Threading.Tasks;
    using ExpeditionPathOptimizer.PathPlannerData;
    using TEHhub.Plugin;

    public record BestValue(List<Vector2> Path, double Score, int Iteration, double LastGenerationTime);

    public class PathPlannerRunner
    {
        private CancellationTokenSource? cts;
        private Task? searchTask;
        private PathPlanner? pathPlanner;
        private ExpeditionEnvironment? environment;
        private BestValue?[]? bestValues;
        private PathPlanner.DetailedLootScore? finalBestPath;
        private readonly ConditionalWeakTable<List<Vector2>, PathPlanner.DetailedLootScore> lootCache = new();

        public bool IsRunning => this.searchTask is { IsCompleted: false };

        public PathPlanner.DetailedLootScore? CurrentBestPath
        {
            get
            {
                if (this.finalBestPath != null)
                {
                    return this.finalBestPath;
                }

                if (this.bestValues == null) return null;
                var bestEntry = this.bestValues.Where(x => x != null).MaxBy(x => x!.Score);
                if (bestEntry?.Path is not { } bestPath) return null;

                if (this.lootCache.TryGetValue(bestPath, out var existingScore))
                {
                    return existingScore;
                }

                return (this.pathPlanner is { } pp && this.environment is { } env)
                    ? this.lootCache.GetValue(bestPath, p => pp.GetDetailedScore(p, env))
                    : null;
            }
        }

        public double CurrentBestScore
        {
            get
            {
                if (this.finalBestPath != null) return this.finalBestPath.TotalScore;
                return this.bestValues?.Max(x => x?.Score ?? 0) ?? 0;
            }
        }

        public void Start(ExpeditionPathOptimizerSettings settings, ExpeditionEnvironment env)
        {
            this.Stop();
            this.finalBestPath = null;
            this.cts = new CancellationTokenSource();
            this.searchTask = this.RunAsync(settings, env, this.cts.Token);
        }

        private async Task RunAsync(ExpeditionPathOptimizerSettings settings, ExpeditionEnvironment env, CancellationToken token)
        {
            this.environment = env;
            this.pathPlanner = new PathPlanner(settings);
            this.pathPlanner.Init(env);

            int threadCount = Math.Clamp(settings.SearchThreads, 1, 16);
            this.bestValues = new BestValue?[threadCount];
            var tasks = new List<Task>();

            for (int i = 0; i < threadCount; i++)
            {
                int threadIndex = i;
                tasks.Add(Task.Run(() =>
                {
                    try
                    {
                        var p = new PathPlanner(settings);
                        var sw = Stopwatch.StartNew();
                        var iterationSw = Stopwatch.StartNew();
                        p.Init(env);

                        foreach (var bestPath in p.GetBestPathSeries(env))
                        {
                            if (token.IsCancellationRequested) return;

                            int iter = (this.bestValues[threadIndex]?.Iteration ?? 0) + 1;
                            this.bestValues[threadIndex] = new BestValue(bestPath.Points, bestPath.Score, iter, iterationSw.Elapsed.TotalMilliseconds);
                            iterationSw.Restart();

                            if (sw.Elapsed.TotalSeconds >= settings.MaximumGenerationTimeSeconds)
                            {
                                return;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        PluginLog.Error("ExpeditionPathOptimizer", $"Search thread {threadIndex} exception: {ex.Message}");
                    }
                }, token));
            }

            try
            {
                await Task.WhenAll(tasks);
            }
            catch
            {
                // Ignore cancellation
            }
            finally
            {
                if (!token.IsCancellationRequested && this.bestValues != null && this.pathPlanner != null && this.environment != null)
                {
                    var absoluteBest = this.bestValues.Where(x => x != null).MaxBy(x => x!.Score);
                    if (absoluteBest?.Path is { } bPath)
                    {
                        this.finalBestPath = this.pathPlanner.GetDetailedScore(bPath, this.environment);
                    }
                }
                PluginLog.Info("ExpeditionPathOptimizer", "Path search completed and locked.");
            }
        }

        public void Stop()
        {
            this.cts?.Cancel();
            this.cts?.Dispose();
            this.cts = null;
        }

        public void Clear()
        {
            this.Stop();
            this.finalBestPath = null;
            this.bestValues = null;
            this.pathPlanner = null;
            this.environment = null;
        }
    }
}
