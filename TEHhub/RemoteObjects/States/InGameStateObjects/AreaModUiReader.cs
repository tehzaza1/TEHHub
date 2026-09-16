// <copyright file="AreaModUiReader.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace TEHhub.RemoteObjects.States.InGameStateObjects
{
    using System;
    using System.Collections.Generic;
    using System.Text.RegularExpressions;
    using TEHhub.Offsets.Natives;
    using TEHhub.Offsets.Objects.UiElement;
    using TEHhub.Utils;

    /// <summary>
    ///     Reads and formats active map and area modifiers directly from the game's UI hierarchy.
    /// </summary>
    public static class AreaModUiReader
    {
        private static readonly int[] KeyboardMapModsPath = { 6, 2, 3, 0, 1 };
        private static readonly int[] ControllerMapModsPath1 = { 0, 3, 3, 0, 1 };
        private static readonly int[] ControllerMapModsPath2 = { 0, 2, 3, 0, 1 };

        private static readonly Regex WikiTagRegex = new(@"\[(?:[^\|\]]*\|)?([^\]]+)\]", RegexOptions.Compiled);
        private static readonly Regex FormattedTagRegex = new(@"<[^>]+>\{(.*?)\}", RegexOptions.Compiled);
        private static readonly Regex HtmlTagRegex = new(@"<[^>]+>", RegexOptions.Compiled);

        /// <summary>
        ///     Cleans raw PoE UI markup strings into clean, human-readable modifier text.
        /// </summary>
        /// <param name="raw">Raw string containing wiki brackets or formatting tags.</param>
        /// <returns>Cleaned modifier string.</returns>
        public static string CleanModifierText(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            var text = FormattedTagRegex.Replace(raw, "");
            text = WikiTagRegex.Replace(text, "");
            text = HtmlTagRegex.Replace(text, string.Empty);
            return text.Trim();
        }

        /// <summary>
        ///     Attempts to read active area modifiers from the map HUD UI container.
        /// </summary>
        /// <param name="reader">Safe memory handle for the game process.</param>
        /// <param name="uiRootAddress">UiRoot / GameUi base address.</param>
        /// <returns>List of parsed <see cref="AreaMod"/> objects, or an empty list if not found.</returns>
        public static List<AreaMod> ReadAreaMods(SafeMemoryHandle reader, IntPtr uiRootAddress)
        {
            var result = new List<AreaMod>();
            if (uiRootAddress == IntPtr.Zero)
            {
                return result;
            }

            // 1. Try known paths
            var containerAddr = ResolvePath(reader, uiRootAddress, KeyboardMapModsPath);
            if (containerAddr == IntPtr.Zero)
            {
                containerAddr = ResolvePath(reader, uiRootAddress, ControllerMapModsPath1);
            }

            if (containerAddr == IntPtr.Zero)
            {
                containerAddr = ResolvePath(reader, uiRootAddress, ControllerMapModsPath2);
            }

            // 2. If known paths fail, try heuristic scan under root.6 (KBM) or root.0 (Controller)
            if (containerAddr == IntPtr.Zero)
            {
                containerAddr = FindModsContainerHeuristic(reader, uiRootAddress);
            }

            if (containerAddr == IntPtr.Zero)
            {
                return result;
            }

            // Read children of the modifier container
            if (!reader.TryReadMemory<UiElementBaseOffset>(containerAddr, out var containerOff) ||
                containerOff.ChildrensPtr.First == IntPtr.Zero)
            {
                return result;
            }

            var count = (int)containerOff.ChildrensPtr.TotalElements(IntPtr.Size);
            if (count is <= 0 or > 200)
            {
                return result;
            }

            for (var i = 0; i < count; i++)
            {
                var childAddr = reader.ReadMemory<IntPtr>(containerOff.ChildrensPtr.First + (i * IntPtr.Size));
                if (childAddr == IntPtr.Zero || !SafeMemoryHandle.IsValidAddress(childAddr))
                {
                    continue;
                }

                var rawText = ReadChildText(reader, childAddr);
                if (string.IsNullOrWhiteSpace(rawText))
                {
                    continue;
                }

                var cleaned = CleanModifierText(rawText);
                if (!string.IsNullOrWhiteSpace(cleaned))
                {
                    result.Add(new AreaMod(cleaned, cleaned, (float.NaN, float.NaN), childAddr));
                }
            }

            return result;
        }

        private static string ReadChildText(SafeMemoryHandle reader, IntPtr childAddr)
        {
            // Primary text offset in PoE 2 UI elements
            if (reader.TryReadMemory<StdWString>(childAddr + 0x360, out var wstr1))
            {
                var s1 = reader.ReadStdWString(wstr1);
                if (!string.IsNullOrWhiteSpace(s1))
                {
                    return s1;
                }
            }

            // Fallback 1: offset 0x2E0
            if (reader.TryReadMemory<StdWString>(childAddr + 0x2E0, out var wstr2))
            {
                var s2 = reader.ReadStdWString(wstr2);
                if (!string.IsNullOrWhiteSpace(s2))
                {
                    return s2;
                }
            }

            // Fallback 2: StringIdPtr at 0x128
            if (reader.TryReadMemory<StdWString>(childAddr + 0x128, out var wstr3))
            {
                var s3 = reader.ReadStdWString(wstr3);
                if (!string.IsNullOrWhiteSpace(s3))
                {
                    return s3;
                }
            }

            return string.Empty;
        }

        private static IntPtr ResolvePath(SafeMemoryHandle reader, IntPtr rootAddress, int[] path)
        {
            var current = rootAddress;
            foreach (var idx in path)
            {
                if (current == IntPtr.Zero || idx < 0)
                {
                    return IntPtr.Zero;
                }

                if (!reader.TryReadMemory<UiElementBaseOffset>(current, out var elem) ||
                    elem.ChildrensPtr.First == IntPtr.Zero)
                {
                    return IntPtr.Zero;
                }

                var childCount = (int)elem.ChildrensPtr.TotalElements(IntPtr.Size);
                if (idx >= childCount)
                {
                    return IntPtr.Zero;
                }

                current = reader.ReadMemory<IntPtr>(elem.ChildrensPtr.First + (idx * IntPtr.Size));
            }

            return current;
        }

        private static IntPtr FindModsContainerHeuristic(SafeMemoryHandle reader, IntPtr rootAddress)
        {
            // Scan under root child 6 (KBM) or child 0 (Controller)
            int[] rootCandidates = { 6, 0 };
            foreach (var rootIdx in rootCandidates)
            {
                var mapRoot = ResolvePath(reader, rootAddress, new[] { rootIdx });
                if (mapRoot == IntPtr.Zero)
                {
                    continue;
                }

                var candidate = ScanForModifierContainer(reader, mapRoot, depth: 0, maxDepth: 5);
                if (candidate != IntPtr.Zero)
                {
                    return candidate;
                }
            }

            return IntPtr.Zero;
        }

        private static IntPtr ScanForModifierContainer(SafeMemoryHandle reader, IntPtr current, int depth, int maxDepth)
        {
            if (current == IntPtr.Zero || depth > maxDepth)
            {
                return IntPtr.Zero;
            }

            if (!reader.TryReadMemory<UiElementBaseOffset>(current, out var elem) ||
                elem.ChildrensPtr.First == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            var count = (int)elem.ChildrensPtr.TotalElements(IntPtr.Size);
            if (count is > 0 and <= 100)
            {
                // Check if first child has a valid modifier string at 0x360
                var firstChild = reader.ReadMemory<IntPtr>(elem.ChildrensPtr.First);
                if (firstChild != IntPtr.Zero && SafeMemoryHandle.IsValidAddress(firstChild))
                {
                    var text = ReadChildText(reader, firstChild);
                    if (!string.IsNullOrWhiteSpace(text) &&
                        (text.Contains('%') || text.Contains("Area", StringComparison.OrdinalIgnoreCase) ||
                         text.Contains("Monster", StringComparison.OrdinalIgnoreCase) || text.Contains("Player", StringComparison.OrdinalIgnoreCase) ||
                         text.Contains("chance", StringComparison.OrdinalIgnoreCase) || text.Contains("increased", StringComparison.OrdinalIgnoreCase)))
                    {
                        return current;
                    }
                }
            }

            // Recurse down children
            if (count > 0 && count <= 20)
            {
                for (var i = 0; i < count; i++)
                {
                    var child = reader.ReadMemory<IntPtr>(elem.ChildrensPtr.First + (i * IntPtr.Size));
                    var found = ScanForModifierContainer(reader, child, depth + 1, maxDepth);
                    if (found != IntPtr.Zero)
                    {
                        return found;
                    }
                }
            }

            return IntPtr.Zero;
        }
    }
}
