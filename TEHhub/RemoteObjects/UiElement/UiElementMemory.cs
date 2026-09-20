// <copyright file="UiElementMemory.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace TEHhub.RemoteObjects.UiElement
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using TEHhub.Offsets.Natives;
    using TEHhub.Offsets.Objects.UiElement;

    /// <summary>
    ///     Bounded, read-only helpers for UI evidence that is not represented by a stable cache pointer.
    /// </summary>
    internal static class UiElementMemory
    {
        internal const int DisplayTextOffset = 0x360;
        private const int MaxChildrenPerNode = 2048;

        internal static bool TryResolvePath(IntPtr root, ReadOnlySpan<int> path, out IntPtr address)
        {
            address = root;
            for (var i = 0; i < path.Length; i++)
            {
                if (!TryReadChildren(address, out var children) ||
                    path[i] < 0 || path[i] >= children.Length)
                {
                    address = IntPtr.Zero;
                    return false;
                }

                address = children[path[i]];
                if (address == IntPtr.Zero)
                {
                    return false;
                }
            }

            return address != IntPtr.Zero;
        }

        internal static bool TryReadChildren(IntPtr address, out IntPtr[] children)
        {
            children = Array.Empty<IntPtr>();
            var reader = Core.Process?.Handle;
            if (reader == null || address == IntPtr.Zero ||
                !reader.TryReadMemory<UiElementBaseOffset>(address, out var element) ||
                (element.Self != IntPtr.Zero && element.Self != address))
            {
                return false;
            }

            var count = element.ChildrensPtr.TotalElements(IntPtr.Size);
            if (count < 0 || count > MaxChildrenPerNode ||
                (count > 0 && element.ChildrensPtr.First == IntPtr.Zero))
            {
                return false;
            }

            if (count == 0)
            {
                return true;
            }

            children = new IntPtr[(int)count];
            return reader.TryReadMemoryArray(element.ChildrensPtr.First, children, out _);
        }

        internal static bool TryReadDisplayText(IntPtr element, out string text) =>
            TryReadText(element, DisplayTextOffset, out text);

        internal static bool TryReadText(IntPtr element, int offset, out string text)
        {
            text = string.Empty;
            var reader = Core.Process?.Handle;
            var stringAddress = element + offset;
            if (reader == null || element == IntPtr.Zero ||
                !reader.TryReadMemory<StdWString>(stringAddress, out var value) ||
                value.PAD_14 != 0 || value.PAD_1C != 0 ||
                value.Length <= 0 || value.Length > 1024 ||
                value.Capacity < value.Length || value.Capacity > 65536)
            {
                return false;
            }

            var bytes = new byte[value.Length * sizeof(char)];
            var source = value.Capacity <= 8 ? stringAddress : value.Buffer;
            if (!reader.TryReadMemoryArray(source, bytes, out _))
            {
                return false;
            }

            var candidate = Encoding.Unicode.GetString(bytes);
            for (var i = 0; i < candidate.Length; i++)
            {
                var c = candidate[i];
                if (char.IsControl(c) && c is not ('\n' or '\r' or '\t'))
                {
                    return false;
                }
            }

            text = candidate;
            return true;
        }

        internal static bool HasTextAtPath(
            IntPtr root,
            ReadOnlySpan<int> path,
            string expected)
        {
            return TryResolvePath(root, path, out var element) &&
                   TryReadDisplayText(element, out var text) &&
                   string.Equals(text.Trim(), expected, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool TryFindVisibleText(
            IntPtr root,
            string expected,
            int maxDepth,
            int maxNodes,
            out IntPtr match)
        {
            match = IntPtr.Zero;
            if (root == IntPtr.Zero || maxDepth < 0 || maxNodes <= 0)
            {
                return false;
            }

            var reader = Core.Process?.Handle;
            if (reader == null)
            {
                return false;
            }

            var pending = new Queue<(IntPtr Address, int Depth)>();
            var visited = new HashSet<IntPtr>();
            pending.Enqueue((root, 0));
            while (pending.Count > 0 && visited.Count < maxNodes)
            {
                var current = pending.Dequeue();
                if (current.Address == IntPtr.Zero || !visited.Add(current.Address) ||
                    !reader.TryReadMemory<UiElementBaseOffset>(current.Address, out var element) ||
                    (element.Self != IntPtr.Zero && element.Self != current.Address) ||
                    !UiElementBaseFuncs.IsVisibleChecker(element.Flags))
                {
                    continue;
                }

                if (TryReadDisplayText(current.Address, out var text) &&
                    string.Equals(text.Trim(), expected, StringComparison.OrdinalIgnoreCase))
                {
                    match = current.Address;
                    return true;
                }

                if (current.Depth >= maxDepth || !TryReadChildren(current.Address, out var children))
                {
                    continue;
                }

                for (var i = 0; i < children.Length; i++)
                {
                    pending.Enqueue((children[i], current.Depth + 1));
                }
            }

            return false;
        }

        internal static bool TryFindFirstDisplayText(
            IntPtr root,
            int maxDepth,
            int maxNodes,
            out string text,
            out IntPtr textElement)
        {
            text = string.Empty;
            textElement = IntPtr.Zero;
            if (root == IntPtr.Zero || maxDepth < 0 || maxNodes <= 0)
            {
                return false;
            }

            var pending = new Queue<(IntPtr Address, int Depth)>();
            var visited = new HashSet<IntPtr>();
            pending.Enqueue((root, 0));
            while (pending.Count > 0 && visited.Count < maxNodes)
            {
                var current = pending.Dequeue();
                if (current.Address == IntPtr.Zero || !visited.Add(current.Address))
                {
                    continue;
                }

                if (TryReadDisplayText(current.Address, out text) && !string.IsNullOrWhiteSpace(text))
                {
                    textElement = current.Address;
                    return true;
                }

                if (current.Depth >= maxDepth || !TryReadChildren(current.Address, out var children))
                {
                    continue;
                }

                for (var i = 0; i < children.Length; i++)
                {
                    pending.Enqueue((children[i], current.Depth + 1));
                }
            }

            text = string.Empty;
            return false;
        }
    }
}
