// <copyright file="GameUiPanelDetector.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace TEHhub.RemoteObjects.UiElement
{
    using System;

    /// <summary>
    ///     Detects interaction panels from exact PoE2 UI evidence. The three states are intentionally
    ///     independent because stash and vendor screens also open the player inventory.
    /// </summary>
    internal static class GameUiPanelDetector
    {
        private const string InventoryTitle = "Inventory";
        private const string StashTitle = "Stash";
        private const string VendorTitle = "Buy or Sell";
        private static readonly int[] TitlePath = { 1 };

        internal static GameUiPanelDetection Detect(
            IntPtr gameUi,
            IntPtr leftPanel,
            IntPtr rightPanel,
            IntPtr cachedVendorPanel,
            bool allowVendorDiscovery)
        {
            var inventoryOpen = UiElementMemory.HasTextAtPath(rightPanel, TitlePath, InventoryTitle);
            var stashOpen = UiElementMemory.HasTextAtPath(leftPanel, TitlePath, StashTitle);
            var vendorPanel = IntPtr.Zero;

            if (inventoryOpen && !stashOpen)
            {
                if (cachedVendorPanel != IntPtr.Zero &&
                    UiElementMemory.TryFindVisibleText(
                        cachedVendorPanel,
                        VendorTitle,
                        maxDepth: 4,
                        maxNodes: 192,
                        out _))
                {
                    vendorPanel = cachedVendorPanel;
                }
                else if (allowVendorDiscovery && UiElementMemory.TryReadChildren(gameUi, out var roots))
                {
                    for (var i = 0; i < roots.Length; i++)
                    {
                        var candidate = roots[i];
                        if (candidate == IntPtr.Zero || candidate == leftPanel || candidate == rightPanel)
                        {
                            continue;
                        }

                        if (UiElementMemory.TryFindVisibleText(
                                candidate,
                                VendorTitle,
                                maxDepth: 4,
                                maxNodes: 192,
                                out _))
                        {
                            vendorPanel = candidate;
                            break;
                        }
                    }
                }
            }

            return new GameUiPanelDetection(
                inventoryOpen,
                stashOpen,
                vendorPanel != IntPtr.Zero,
                vendorPanel);
        }

        internal static GameUiPanelDetection ClassifyTitles(
            string? inventoryTitle,
            string? stashTitle,
            string? vendorTitle)
        {
            var inventoryOpen = IsTitle(inventoryTitle, InventoryTitle);
            var stashOpen = IsTitle(stashTitle, StashTitle);
            var vendorOpen = inventoryOpen && !stashOpen && IsTitle(vendorTitle, VendorTitle);
            return new GameUiPanelDetection(inventoryOpen, stashOpen, vendorOpen, IntPtr.Zero);
        }

        private static bool IsTitle(string? actual, string expected) =>
            string.Equals(actual?.Trim(), expected, StringComparison.OrdinalIgnoreCase);
    }

    internal readonly record struct GameUiPanelDetection(
        bool InventoryOpen,
        bool StashOpen,
        bool VendorOpen,
        IntPtr VendorPanelAddress);
}
