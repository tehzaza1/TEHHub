namespace ExpeditionPathOptimizer.PathPlannerData
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;

    public record ExpeditionEnvironment(
        List<ExpeditionRemnant> Remnants,
        ExpeditionRemnant? FinalTarget,
        float ExplosionRange,
        float ExplosionRadius,
        int MaxExplosions,
        Vector2 StartingPoint,
        Func<Vector2, bool> IsValidPlacement,
        bool IsLogbook);
}
