namespace ExpeditionPathOptimizer.PathPlannerData
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;
    using System.Runtime.CompilerServices;

    public record ExpeditionEnvironment(
        List<ExpeditionRemnant> Remnants,
        ExpeditionRemnant? FinalTarget,
        float ExplosionRange,
        float ExplosionRadius,
        int MaxExplosions,
        Vector2 StartingPoint,
        byte[]? WalkableData,
        int BytesPerRow,
        bool IsLogbook)
    {
        public int GridRows => (this.WalkableData != null && this.BytesPerRow > 0) ? this.WalkableData.Length / this.BytesPerRow : 0;
        public int GridCols => this.BytesPerRow * 2;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetCellValue(int x, int y)
        {
            if (this.WalkableData == null || this.BytesPerRow <= 0 || x < 0 || y < 0) return 3;
            int byteIndex = (y * this.BytesPerRow) + (x / 2);
            if ((uint)byteIndex >= (uint)this.WalkableData.Length) return 0;
            int shift = ((x & 1) == 0) ? 0 : 4;
            return (this.WalkableData[byteIndex] >> shift) & 0xF;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsCellWalkable(int gx, int gy, int minWalkable = 2)
        {
            if (this.WalkableData == null || this.BytesPerRow <= 0) return true;
            return this.GetCellValue(gx, gy) >= minWalkable;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsPointWalkable(Vector2 p, int minWalkable = 2)
        {
            return this.IsCellWalkable((int)MathF.Round(p.X), (int)MathF.Round(p.Y), minWalkable);
        }

        public bool HasLineOfSight(Vector2 a, Vector2 b, int minWalkable = 2)
        {
            if (this.WalkableData == null || this.BytesPerRow <= 0) return true;

            int ax = (int)MathF.Round(a.X);
            int ay = (int)MathF.Round(a.Y);
            int bx = (int)MathF.Round(b.X);
            int by = (int)MathF.Round(b.Y);

            int dx = Math.Abs(bx - ax);
            int dy = Math.Abs(by - ay);
            int sx = ax < bx ? 1 : -1;
            int sy = ay < by ? 1 : -1;
            int err = dx - dy;

            int cx = ax;
            int cy = ay;
            int rows = this.GridRows;
            int cols = this.GridCols;

            while (cx != bx || cy != by)
            {
                if (cx < 0 || cx >= cols || cy < 0 || cy >= rows)
                {
                    return false;
                }

                if (this.GetCellValue(cx, cy) < minWalkable)
                {
                    return false;
                }

                int e2 = 2 * err;
                if (e2 > -dy)
                {
                    err -= dy;
                    cx += sx;
                }

                if (e2 < dx)
                {
                    err += dx;
                    cy += sy;
                }
            }

            return this.GetCellValue(bx, by) >= minWalkable;
        }

        public Vector2 FindWalkableCandidateNearRemnant(ExpeditionRemnant remnant, Vector2 anchor, float radius)
        {
            if (this.WalkableData == null || this.BytesPerRow <= 0)
            {
                return remnant.GridPos;
            }

            var rPos = remnant.GridPos;
            var diff = anchor - rPos;
            float d = diff.Length();
            var dir = d > 0.001f ? diff / d : Vector2.Zero;

            // 1. Try along line towards anchor within blast radius
            float[] offsetRatios = { 0.0f, 0.35f, 0.65f, 0.85f };
            foreach (var ratio in offsetRatios)
            {
                var cand = rPos + dir * (radius * 0.85f * ratio);
                if (this.IsPointWalkable(cand) && this.HasLineOfSight(anchor, cand))
                {
                    return cand;
                }
            }

            // 2. Search spiral around remnant
            int maxR = (int)(radius * 0.85f);
            for (int r = 2; r <= maxR; r += 4)
            {
                for (int angleDeg = 0; angleDeg < 360; angleDeg += 30)
                {
                    float rad = angleDeg * MathF.PI / 180f;
                    var cand = rPos + new Vector2(MathF.Cos(rad) * r, MathF.Sin(rad) * r);
                    if (this.IsPointWalkable(cand) && this.HasLineOfSight(anchor, cand))
                    {
                        return cand;
                    }
                }
            }

            return rPos;
        }
    }
}
