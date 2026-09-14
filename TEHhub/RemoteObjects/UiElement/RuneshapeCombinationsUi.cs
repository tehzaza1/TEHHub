namespace TEHhub.RemoteObjects.UiElement
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;
    using TEHhub.Utils;

    /// <summary>
    /// Live wrapper for the Runeshape Combinations panel.
    /// Discovery starts from a known UI root and follows live parent/child links;
    /// it does not depend on the unstable absolute path shown by the explorer.
    /// </summary>
    public sealed class RuneshapeCombinationsUi
    {
        private const int MaxDepth = 20;
        private const int MaxChildren = 512;
        private const int MaxRows = 400;
        private const int MaxSlotsPerRow = 32;

        private RuneshapeCombinationsUi(UiElementBase panel, IReadOnlyList<UiElementBase> rows)
        {
            this.Panel = panel;
            this.Rows = rows;
        }

        public UiElementBase Panel { get; }

        public IReadOnlyList<UiElementBase> Rows { get; }

        public bool IsVisible => this.Panel.IsVisible;

        public static bool TryFind(UiElementBase root, out RuneshapeCombinationsUi? result)
        {
            result = null;
            if (root == null || root.Address == IntPtr.Zero)
            {
                return false;
            }

            var visited = new HashSet<IntPtr>();
            return TryFindRecursive(root, 0, visited, out result);
        }

        public static IReadOnlyList<UiElementBase> GetSlots(UiElementBase row)
        {
            var slots = new List<UiElementBase>(Math.Min(row.TotalChildrens, MaxSlotsPerRow));
            for (var i = 0; i < row.TotalChildrens && slots.Count < MaxSlotsPerRow; i++)
            {
                var child = row[i];
                if (child != null && LooksLikeSlot(child))
                {
                    slots.Add(child);
                }
            }

            return slots;
        }

        private static bool TryFindRecursive(
            UiElementBase current,
            int depth,
            HashSet<IntPtr> visited,
            out RuneshapeCombinationsUi? result)
        {
            result = null;
            if (depth > MaxDepth || !visited.Add(current.Address))
            {
                return false;
            }

            if (LooksLikePanel(current, out var rows))
            {
                result = new RuneshapeCombinationsUi(current, rows);
                return true;
            }

            var count = Math.Min(current.TotalChildrens, MaxChildren);
            for (var i = 0; i < count; i++)
            {
                var child = current[i];
                if (child != null && TryFindRecursive(child, depth + 1, visited, out result))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool LooksLikePanel(UiElementBase element, out IReadOnlyList<UiElementBase> rows)
        {
            rows = Array.Empty<UiElementBase>();
            if (!element.IsVisible || element.TotalChildrens < 1 || element.TotalChildrens > MaxChildren)
            {
                return false;
            }

            var candidates = new List<UiElementBase>(Math.Min(element.TotalChildrens, MaxRows));
            for (var i = 0; i < element.TotalChildrens && candidates.Count < MaxRows; i++)
            {
                var child = element[i];
                if (child != null && GetSlots(child).Count >= 4)
                {
                    candidates.Add(child);
                }
            }

            if (candidates.Count == 0)
            {
                return false;
            }

            rows = candidates;
            return true;
        }

        private static bool LooksLikeSlot(UiElementBase element)
        {
            var size = element.Size;
            if (size.X < 40 || size.X > 110 || size.Y < 40 || size.Y > 110)
            {
                return false;
            }

            var ratio = MathF.Abs(size.X - size.Y) / MathF.Max(size.X, size.Y);
            return ratio <= 0.35f;
        }
    }
}
