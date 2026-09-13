namespace ExpeditionPlanner
{
    using System;
    using System.Numerics;
    using TEHhub.RemoteObjects.States.InGameStateObjects;

    public static class LegalPlacement
    {
        /// <summary>
        /// Evaluates whether placing an explosive at <paramref name="candidateGrid"/> is legal,
        /// checking distance from <paramref name="anchorGrid"/> (previous bomb or detonator).
        /// </summary>
        public static bool IsPlaceable(
            Vector3 candidateGrid,
            Vector3? anchorGrid,
            AreaInstance? area,
            ExpeditionPlannerSettings settings)
        {
            if (anchorGrid.HasValue)
            {
                var dx = candidateGrid.X - anchorGrid.Value.X;
                var dy = candidateGrid.Y - anchorGrid.Value.Y;
                var dist = MathF.Sqrt((dx * dx) + (dy * dy));
                if (dist > settings.MaxPlacementRangeGrid || dist < 1.0f)
                {
                    return false;
                }
            }

            if (area != null && area.GridHeightData.Length > 0)
            {
                var gy = (int)candidateGrid.Y;
                var gx = (int)candidateGrid.X;
                if (gy < 0 || gy >= area.GridHeightData.Length) return false;
                var row = area.GridHeightData[gy];
                if (row == null || gx < 0 || gx >= row.Length) return false;
            }

            return true;
        }
    }
}
