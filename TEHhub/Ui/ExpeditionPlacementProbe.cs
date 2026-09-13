#if DEBUG
namespace TEHhub.Ui;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TEHhub.RemoteObjects.Components;
using TEHhub.RemoteObjects.States.InGameStateObjects;

/// <summary>
/// On-demand, read-only capture of the entities and terrain immediately involved in an Expedition
/// explosive-placement screen. It records observations only; it does not infer placement rules.
/// </summary>
internal static class ExpeditionPlacementProbe
{
    private const int MaxAwakeEntities = 4096;
    private const int MaxSleepingEntities = 512;
    private const int MaxEntitiesPerKind = 256;
    private const int SampleRadius = 6;
    private const int MaxIndicators = 4;

    private const string DetonatorPath = "Metadata/MiscellaneousObjects/Expedition/ExpeditionDetonator";
    private const string ExplosivePath = "Metadata/MiscellaneousObjects/Expedition/ExpeditionExplosive";
    private const string ConnectorPolePath = "Metadata/MiscellaneousObjects/Expedition/ExpeditionConnectorPole";
    private const string FusePath = "Metadata/MiscellaneousObjects/Expedition/ExpeditionExplosiveFuse";
    private const string PlacementIndicatorPath = "Metadata/MiscellaneousObjects/Expedition/ExpeditionPlacementIndicator";
    private const string MarkerPath = "Metadata/MiscellaneousObjects/Expedition/ExpeditionMarker";
    private const string RelicPath = "Metadata/MiscellaneousObjects/Expedition/ExpeditionRelic";
    private const string EncounterPath = "Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter";

    private static TaskCompletionSource<ExpeditionPlacementSnapshot>? pending;
    private static ExpeditionPlacementSnapshot? snapshot;

    internal static Task<ExpeditionPlacementSnapshot>? Request()
    {
        var request = new TaskCompletionSource<ExpeditionPlacementSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        return Interlocked.CompareExchange(ref pending, request, null) == null ? request.Task : null;
    }

    internal static ExpeditionPlacementSnapshot? Snapshot => Volatile.Read(ref snapshot);

    /// <summary>Called on the render thread and only reads memory for a queued request.</summary>
    internal static void Collect()
    {
        var request = Interlocked.Exchange(ref pending, null);
        if (request == null) return;

        ExpeditionPlacementSnapshot result;
        try { result = Capture(); }
        catch (Exception error)
        {
            result = Empty("capture failed: " + error.GetType().Name);
        }

        Volatile.Write(ref snapshot, result);
        request.SetResult(result);
    }

    private static ExpeditionPlacementSnapshot Capture()
    {
        var area = Core.States.InGameStateObject?.CurrentAreaInstance;
        if (area == null || area.Address == IntPtr.Zero) return Empty("no active area");

        var detonators = new List<ExpeditionPlacementEntity>();
        var explosives = new List<ExpeditionPlacementEntity>();
        var connectorPoles = new List<ExpeditionPlacementEntity>();
        var fuses = new List<ExpeditionPlacementEntity>();
        var indicators = new List<ExpeditionPlacementEntity>();
        var targets = new List<ExpeditionPlacementEntity>();
        var seen = new HashSet<IntPtr>();

        var awakeScanned = Scan(area.AwakeEntities.Values, MaxAwakeEntities, seen, detonators, explosives, connectorPoles, fuses, indicators, targets);
        var sleepingScanned = Scan(area.SleepingEntities.Values, MaxSleepingEntities, seen, detonators, explosives, connectorPoles, fuses, indicators, targets);
        var grid = GetGrid(area);
        var samples = CaptureIndicatorSamples(indicators, area.GridHeightData, area.GridWalkableData, grid);
        var entitiesTruncated = new[] { detonators, explosives, connectorPoles, fuses, indicators, targets }
            .Any(values => values.Count >= MaxEntitiesPerKind);
        var truncated = awakeScanned >= MaxAwakeEntities || sleepingScanned >= MaxSleepingEntities || entitiesTruncated || indicators.Count > MaxIndicators;

        return new ExpeditionPlacementSnapshot(
            DateTime.UtcNow,
            "completed",
            area.AreaHash ?? string.Empty,
            awakeScanned,
            sleepingScanned,
            truncated,
            new ExpeditionDetonatorElement(new ExpeditionDetonatorInfo(
                "entity-observed",
                grid,
                detonators,
                explosives,
                connectorPoles,
                fuses,
                indicators,
                targets,
                samples)));
    }

    private static int Scan(
        IEnumerable<Entity> entities,
        int limit,
        HashSet<IntPtr> seen,
        List<ExpeditionPlacementEntity> detonators,
        List<ExpeditionPlacementEntity> explosives,
        List<ExpeditionPlacementEntity> connectorPoles,
        List<ExpeditionPlacementEntity> fuses,
        List<ExpeditionPlacementEntity> indicators,
        List<ExpeditionPlacementEntity> targets)
    {
        var scanned = 0;
        foreach (var entity in entities)
        {
            if (scanned++ >= limit) break;
            if (entity == null || entity.Address == IntPtr.Zero || !seen.Add(entity.Address)) continue;

            var destination = Classify(entity.Path);
            if (destination == null || destination.Count >= MaxEntitiesPerKind) continue;
            destination.Add(CaptureEntity(entity));
        }

        return scanned;

        List<ExpeditionPlacementEntity>? Classify(string? path) => path switch
        {
            DetonatorPath => detonators,
            ExplosivePath => explosives,
            ConnectorPolePath => connectorPoles,
            FusePath => fuses,
            PlacementIndicatorPath => indicators,
            MarkerPath or RelicPath or EncounterPath => targets,
            _ => null,
        };
    }

    private static ExpeditionPlacementEntity CaptureEntity(Entity entity)
    {
        var grid = new ExpeditionPlacementVector();
        var world = new ExpeditionPlacementVector();
        var terrainHeight = 0f;
        var hasPosition = false;
        if (entity.TryGetComponent<Render>(out var render, shouldCache: false))
        {
            hasPosition = true;
            grid = new ExpeditionPlacementVector(render.GridPosition.X, render.GridPosition.Y, render.GridPosition.Z);
            world = new ExpeditionPlacementVector(render.WorldPosition.X, render.WorldPosition.Y, render.WorldPosition.Z);
            terrainHeight = render.TerrainHeight;
        }

        var modelPath = entity.TryGetComponent<Animated>(out var animated, shouldCache: false)
            ? animated.ModelPath ?? string.Empty
            : string.Empty;
        var minimapIcon = entity.TryGetComponent<MinimapIcon>(out var minimap, shouldCache: false)
            ? minimap.IconName
            : null;
        return new ExpeditionPlacementEntity(
            entity.Id,
            "0x" + entity.Address.ToInt64().ToString("X"),
            entity.Path ?? string.Empty,
            hasPosition,
            grid,
            world,
            terrainHeight,
            modelPath,
            minimapIcon);
    }

    private static ExpeditionPlacementGrid GetGrid(AreaInstance area)
    {
        var heightData = area.GridHeightData;
        var walkabilityData = area.GridWalkableData;
        var height = heightData.Length;
        var width = height == 0 ? 0 : heightData.Max(row => row.Length);
        var walkabilityBytes = walkabilityData.Length;
        var expectedBytes = width > 0 && height > 0 ? (long)width * height : 0;
        var mapping = expectedBytes > 0 && walkabilityBytes == expectedBytes
            ? "row-major-grid"
            : "unmapped-raw-vector";
        return new ExpeditionPlacementGrid(width, height, walkabilityBytes, expectedBytes, mapping);
    }

    private static List<ExpeditionTerrainSample> CaptureIndicatorSamples(
        IReadOnlyList<ExpeditionPlacementEntity> indicators,
        float[][] heightData,
        byte[] walkabilityData,
        ExpeditionPlacementGrid grid)
    {
        var samples = new List<ExpeditionTerrainSample>();
        foreach (var indicator in indicators.Where(i => i.HasPosition).Take(MaxIndicators))
        {
            var centerX = (int)MathF.Round(indicator.Grid.X);
            var centerY = (int)MathF.Round(indicator.Grid.Y);
            for (var y = centerY - SampleRadius; y <= centerY + SampleRadius; y++)
            for (var x = centerX - SampleRadius; x <= centerX + SampleRadius; x++)
            {
                if (y < 0 || x < 0 || y >= heightData.Length || x >= heightData[y].Length) continue;
                byte? rawWalkability = null;
                if (grid.WalkabilityMapping == "row-major-grid")
                {
                    var index = (long)y * grid.Width + x;
                    if (index >= 0 && index < walkabilityData.Length) rawWalkability = walkabilityData[index];
                }

                samples.Add(new ExpeditionTerrainSample(
                    indicator.EntityId,
                    x,
                    y,
                    heightData[y][x],
                    rawWalkability));
            }
        }

        return samples;
    }

    private static ExpeditionPlacementSnapshot Empty(string status) => new(
        DateTime.UtcNow,
        status,
        string.Empty,
        0,
        0,
        false,
        new ExpeditionDetonatorElement(new ExpeditionDetonatorInfo(
            "unavailable",
            new ExpeditionPlacementGrid(0, 0, 0, 0, "unavailable"),
            new List<ExpeditionPlacementEntity>(),
            new List<ExpeditionPlacementEntity>(),
            new List<ExpeditionPlacementEntity>(),
            new List<ExpeditionPlacementEntity>(),
            new List<ExpeditionPlacementEntity>(),
            new List<ExpeditionPlacementEntity>(),
            new List<ExpeditionTerrainSample>())));
}

internal sealed record ExpeditionPlacementSnapshot(
    DateTime Utc,
    string Status,
    string AreaHash,
    int AwakeScanned,
    int SleepingScanned,
    bool Truncated,
    ExpeditionDetonatorElement Element);

/// <summary>
/// Entity-observed Expedition data shaped like the game's detonator element. This is not a native
/// UI wrapper or an offset claim: every field in <see cref="Info"/> is derived from safe entity and terrain reads.
/// </summary>
internal sealed record ExpeditionDetonatorElement(ExpeditionDetonatorInfo Info);

/// <summary>Entity-observed data for a placement screen, intentionally without inferred gameplay constants.</summary>
internal sealed record ExpeditionDetonatorInfo(
    string EvidenceKind,
    ExpeditionPlacementGrid Grid,
    List<ExpeditionPlacementEntity> Detonators,
    List<ExpeditionPlacementEntity> PlacedBombs,
    List<ExpeditionPlacementEntity> ConnectorPoles,
    List<ExpeditionPlacementEntity> Fuses,
    List<ExpeditionPlacementEntity> PlacementIndicators,
    List<ExpeditionPlacementEntity> Targets,
    List<ExpeditionTerrainSample> IndicatorTerrainSamples);

/// <summary>Describes the available terrain arrays without assigning gameplay meaning to raw bytes.</summary>
internal sealed record ExpeditionPlacementGrid(int Width, int Height, int WalkabilityBytes, long ExpectedGridBytes,
    string WalkabilityMapping);

internal sealed record ExpeditionPlacementEntity(uint EntityId, string Address, string Path, bool HasPosition,
    ExpeditionPlacementVector Grid, ExpeditionPlacementVector World, float TerrainHeight, string AnimatedModelPath,
    string? MinimapIconName);

internal sealed record ExpeditionPlacementVector(float X, float Y, float Z)
{
    internal ExpeditionPlacementVector()
        : this(0f, 0f, 0f) { }
}

/// <summary>Terrain values around an observed placement indicator; null raw walkability means its layout is unverified.</summary>
internal sealed record ExpeditionTerrainSample(uint IndicatorEntityId, int X, int Y, float Height, byte? RawWalkability);
#endif
