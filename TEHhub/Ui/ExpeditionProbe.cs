#if DEBUG
namespace TEHhub.Ui
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using TEHhub.RemoteObjects.Components;
    using TEHhub.RemoteObjects.States.InGameStateObjects;

    /// <summary>
    /// On-demand, read-only evidence capture for undiscovered Expedition 2 interaction objects.
    /// All collection occurs on the render thread through <see cref="Collect"/>.
    /// </summary>
    internal static class ExpeditionProbe
    {
        private const int MaxAwakeEntities = 4096;
        private const int MaxSleepingEntities = 512;
        private const int MaxCandidates = 128;
        private const int MaxComponentsPerCandidate = 48;
        private const int MaxModsPerCandidate = 32;
        private static readonly string[] PathTerms =
        {
            "Expedition", "Explosive", "Detonator", "Fuse", "Blast", "Runeshape", "Placement",
        };

        private static TaskCompletionSource<ExpeditionProbeSnapshot>? pending;
        private static ExpeditionProbeSnapshot? snapshot;

        /// <summary>Queues one collection. Returns null when a collection is already pending.</summary>
        internal static Task<ExpeditionProbeSnapshot>? Request()
        {
            var request = new TaskCompletionSource<ExpeditionProbeSnapshot>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            return Interlocked.CompareExchange(ref pending, request, null) == null ? request.Task : null;
        }

        /// <summary>Returns the last completed snapshot, if any.</summary>
        internal static ExpeditionProbeSnapshot? Snapshot => Volatile.Read(ref snapshot);

        /// <summary>Called from the render-frame path; does nothing unless a request is pending.</summary>
        internal static void Collect()
        {
            var request = Interlocked.Exchange(ref pending, null);
            if (request == null) return;

            ExpeditionProbeSnapshot result;
            try
            {
                result = Capture();
            }
            catch (Exception ex)
            {
                result = new ExpeditionProbeSnapshot(
                    DateTime.UtcNow,
                    "capture failed: " + ex.GetType().Name,
                    string.Empty,
                    0,
                    0,
                    false,
                    new List<ExpeditionProbeCandidate>());
            }

            Volatile.Write(ref snapshot, result);
            request.SetResult(result);
        }

        private static ExpeditionProbeSnapshot Capture()
        {
            var area = Core.States.InGameStateObject?.CurrentAreaInstance;
            if (area == null || area.Address == IntPtr.Zero)
            {
                return new ExpeditionProbeSnapshot(
                    DateTime.UtcNow,
                    "no active area",
                    string.Empty,
                    0,
                    0,
                    false,
                    new List<ExpeditionProbeCandidate>());
            }

            var candidates = new List<ExpeditionProbeCandidate>();
            var awakeScanned = ScanEntities(area.AwakeEntities.Values, MaxAwakeEntities, candidates);
            var sleepingScanned = ScanEntities(area.SleepingEntities.Values, MaxSleepingEntities, candidates);
            var truncated = awakeScanned >= MaxAwakeEntities || sleepingScanned >= MaxSleepingEntities ||
                            candidates.Count >= MaxCandidates;
            return new ExpeditionProbeSnapshot(
                DateTime.UtcNow,
                "completed",
                area.AreaHash ?? string.Empty,
                awakeScanned,
                sleepingScanned,
                truncated,
                candidates);
        }

        private static int ScanEntities(
            IEnumerable<Entity> entities,
            int limit,
            List<ExpeditionProbeCandidate> candidates)
        {
            var scanned = 0;
            foreach (var entity in entities)
            {
                if (scanned++ >= limit || candidates.Count >= MaxCandidates) break;
                if (entity == null || entity.Address == IntPtr.Zero || !IsCandidatePath(entity.Path)) continue;
                candidates.Add(CaptureCandidate(entity));
            }

            return scanned;
        }

        private static bool IsCandidatePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            return PathTerms.Any(term => path.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        private static ExpeditionProbeCandidate CaptureCandidate(Entity entity)
        {
            var componentNames = entity.GetComponentNames()
                .Take(MaxComponentsPerCandidate)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
            var grid = new ExpeditionProbeVector();
            var world = new ExpeditionProbeVector();
            var terrainHeight = 0f;
            if (entity.TryGetComponent<Render>(out var render, shouldCache: false))
            {
                grid = new ExpeditionProbeVector(render.GridPosition.X, render.GridPosition.Y, render.GridPosition.Z);
                world = new ExpeditionProbeVector(render.WorldPosition.X, render.WorldPosition.Y, render.WorldPosition.Z);
                terrainHeight = render.TerrainHeight;
            }

            var modelPath = string.Empty;
            if (entity.TryGetComponent<Animated>(out var animated, shouldCache: false)) modelPath = animated.ModelPath;

            string? minimapIcon = null;
            if (entity.TryGetComponent<MinimapIcon>(out var minimap, shouldCache: false)) minimapIcon = minimap.IconName;

            var modNames = new List<string>();
            if (entity.TryGetComponent<ObjectMagicProperties>(out var magic, shouldCache: false))
            {
                modNames = magic.ModNames.Take(MaxModsPerCandidate).OrderBy(name => name, StringComparer.Ordinal).ToList();
            }

            var buffNames = new List<string>();
            if (entity.TryGetComponent<Buffs>(out var buffs, shouldCache: false))
            {
                buffNames = buffs.StatusEffects.Keys.Take(MaxModsPerCandidate).OrderBy(name => name, StringComparer.Ordinal).ToList();
            }

            var stats = new List<ExpeditionProbeStat>();
            if (entity.TryGetComponent<Stats>(out var statsComp, shouldCache: false))
            {
                foreach (var kv in statsComp.StatsChangedByBuffAndActions.Take(MaxModsPerCandidate))
                {
                    stats.Add(new ExpeditionProbeStat(kv.Key.ToString(), kv.Value));
                }
            }

            return new ExpeditionProbeCandidate(
                entity.Id,
                "0x" + entity.Address.ToInt64().ToString("X"),
                entity.Path ?? string.Empty,
                entity.EntityCustomGroup,
                grid,
                world,
                terrainHeight,
                modelPath ?? string.Empty,
                minimapIcon,
                modNames,
                componentNames,
                buffNames,
                stats);
        }
    }

    /// <summary>JSON-friendly output for one completed Expedition evidence capture.</summary>
    internal sealed record ExpeditionProbeSnapshot(
        DateTime Utc,
        string Status,
        string AreaHash,
        int AwakeScanned,
        int SleepingScanned,
        bool Truncated,
        List<ExpeditionProbeCandidate> Candidates);

    /// <summary>One Expedition-related entity and data already exposed by existing wrappers.</summary>
    internal sealed record ExpeditionProbeCandidate(
        uint EntityId,
        string Address,
        string Path,
        int CustomGroup,
        ExpeditionProbeVector Grid,
        ExpeditionProbeVector World,
        float TerrainHeight,
        string AnimatedModelPath,
        string? MinimapIconName,
        List<string> ModNames,
        List<string> ComponentNames,
        List<string> BuffNames,
        List<ExpeditionProbeStat> Stats);

    internal sealed record ExpeditionProbeStat(string Name, int Value);

    /// <summary>Simple coordinate DTO that does not expose native tuple implementation details.</summary>
    internal sealed record ExpeditionProbeVector(float X, float Y, float Z)
    {
        internal ExpeditionProbeVector()
            : this(0f, 0f, 0f) { }
    }
}
#endif
