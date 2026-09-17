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
        private int currentGeneration = 0;
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
            int gen = Interlocked.Increment(ref this.currentGeneration);
            this.Stop();
            this.finalBestPath = null;
            this.bestValues = null;
            this.cts = new CancellationTokenSource();
            this.searchTask = this.RunAsync(settings, env, gen, this.cts.Token);
        }

        private async Task RunAsync(ExpeditionPathOptimizerSettings settings, ExpeditionEnvironment env, int generationId, CancellationToken token)
        {
            var localPlanner = new PathPlanner(settings);
            localPlanner.Init(env);

            int threadCount = Math.Clamp(settings.SearchThreads, 1, 16);
            var localBestValues = new BestValue?[threadCount];

            if (this.currentGeneration == generationId)
            {
                this.environment = env;
                this.pathPlanner = localPlanner;
                this.bestValues = localBestValues;
            }

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
                            if (token.IsCancellationRequested || this.currentGeneration != generationId) return;

                            int iter = (localBestValues[threadIndex]?.Iteration ?? 0) + 1;
                            localBestValues[threadIndex] = new BestValue(bestPath.Points, bestPath.Score, iter, iterationSw.Elapsed.TotalMilliseconds);
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
                if (!token.IsCancellationRequested && this.currentGeneration == generationId)
                {
                    var absoluteBest = localBestValues.Where(x => x != null).MaxBy(x => x!.Score);
                    if (absoluteBest?.Path is { } bPath)
                    {
                        this.finalBestPath = localPlanner.GetDetailedScore(bPath, env);
                        if (this.finalBestPath != null && this.finalBestPath.PerPointScore.Count > 0)
                        {
                            double econTotal = this.finalBestPath.PerPointScore.Sum(p => p.RecipeEconomicScore);
                            double runeTotal = this.finalBestPath.PerPointScore.Sum(p => p.RuneScore);
                            double oathTotal = this.finalBestPath.PerPointScore.Sum(p => p.OathExposurePenalty);
                            double backtrackTotal = this.finalBestPath.PerPointScore.Sum(p => p.BacktrackPenalty);
                            PluginLog.Info(
                                "ExpeditionPathOptimizer",
                                $"Path search locked: Score={this.finalBestPath.TotalScore:F1}, Bombs={this.finalBestPath.PerPointScore.Count}, Pruned={this.finalBestPath.WasPruned}, Econ=+{econTotal:F0}c, Runes=+{runeTotal:F0}, Oath=-{oathTotal:F0}, Backtrack=-{backtrackTotal:F0}");
                        }
                        else
                        {
                            PluginLog.Info("ExpeditionPathOptimizer", "Path search completed (no valid path).");
                        }
                    }
                    else
                    {
                        PluginLog.Info("ExpeditionPathOptimizer", "Path search completed (no candidate found).");
                    }
                }
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
            Interlocked.Increment(ref this.currentGeneration);
            this.Stop();
            this.finalBestPath = null;
            this.bestValues = null;
            this.pathPlanner = null;
            this.environment = null;
        }
    }
}
