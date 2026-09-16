namespace ExpeditionPathOptimizer.PathPlannerData
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;

    public class ExpeditionRemnant
    {
        public uint EntityId { get; set; }
        public IntPtr EntityAddress { get; set; }
        public Vector3 WorldPos { get; set; }
        public Vector2 GridPos { get; set; }
        public int RuneSlots { get; set; } = 4;
        public string PropagatedRune { get; set; } = string.Empty;
        public double BaseRuneWeight { get; set; } = 20.0;

        public ExpeditionRemnant()
        {
        }

        public ExpeditionRemnant(uint entityId, IntPtr entityAddress, Vector3 worldPos, Vector2 gridPos, int runeSlots, string propagatedRune)
        {
            this.EntityId = entityId;
            this.EntityAddress = entityAddress;
            this.WorldPos = worldPos;
            this.GridPos = gridPos;
            this.RuneSlots = Math.Clamp(runeSlots, 1, 16);
            this.PropagatedRune = propagatedRune ?? string.Empty;
        }

        public void UpdateBaseRuneWeight(Dictionary<string, double> weights)
        {
            if (!string.IsNullOrEmpty(this.PropagatedRune) && weights.TryGetValue(this.PropagatedRune, out var w))
            {
                this.BaseRuneWeight = w;
            }
            else
            {
                this.BaseRuneWeight = 20.0;
            }
        }
    }
}
