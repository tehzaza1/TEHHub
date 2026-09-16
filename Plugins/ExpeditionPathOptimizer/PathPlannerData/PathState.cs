namespace ExpeditionPathOptimizer.PathPlannerData
{
    using System.Collections.Generic;
    using System.Numerics;

    public record PathState(List<Vector2> Points, double Score);
}
