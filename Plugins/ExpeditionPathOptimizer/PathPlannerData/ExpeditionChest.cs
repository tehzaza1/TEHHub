namespace ExpeditionPathOptimizer.PathPlannerData
{
    using System;
    using System.Numerics;

    public class ExpeditionChest : IChest
    {
        public uint EntityId { get; set; }
        public IntPtr EntityAddress { get; set; }
        public Vector3 WorldPos { get; set; }
        public Vector2 GridPos { get; set; }
        public string IconName { get; set; } = string.Empty;
        public double BaseScore { get; set; } = 40.0;

        public ExpeditionChest()
        {
        }

        public ExpeditionChest(uint entityId, IntPtr entityAddress, Vector3 worldPos, Vector2 gridPos, string iconName, double baseScore = 40.0)
        {
            this.EntityId = entityId;
            this.EntityAddress = entityAddress;
            this.WorldPos = worldPos;
            this.GridPos = gridPos;
            this.IconName = iconName ?? string.Empty;
            this.BaseScore = baseScore;
        }
    }
}
