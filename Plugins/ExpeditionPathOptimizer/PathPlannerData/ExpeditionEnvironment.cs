namespace ExpeditionPathOptimizer.PathPlannerData
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;

    public record ExpeditionEnvironment(
        List<(Vector2 Pos, IExpeditionRelic Relic)> Relics,
        List<(Vector2 Pos, IExpeditionLoot Loot)> Loot,
        float ExplosionRange,
        float ExplosionRadius,
        int MaxExplosions,
        Vector2 StartingPoint,
        Func<Vector2, bool> IsValidPlacement,
        (Vector2 Min, Vector2 Max) ExclusionArea,
        bool IsLogbook);
}
