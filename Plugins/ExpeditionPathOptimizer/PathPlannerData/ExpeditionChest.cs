namespace ExpeditionPathOptimizer.PathPlannerData
{
    using System;
    using System.Numerics;

    public class ExpeditionChest
    {
        public uint EntityId { get; set; }
        public IntPtr EntityAddress { get; set; }
        public Vector3 WorldPos { get; set; }
        public Vector2 GridPos { get; set; }
        public string IconName { get; set; } = string.Empty;

        public ExpeditionChest()
        {
        }

        public ExpeditionChest(uint entityId, IntPtr entityAddress, Vector3 worldPos, Vector2 gridPos, string iconName)
        {
            this.EntityId = entityId;
            this.EntityAddress = entityAddress;
            this.WorldPos = worldPos;
            this.GridPos = gridPos;
            this.IconName = iconName ?? string.Empty;
        }
    }
}
