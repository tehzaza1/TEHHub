// <copyright file="WaystoneCrafting.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2
{
    using System;
    using TEHhub.RemoteEnums;
    using TEHhub.RemoteObjects.States.InGameStateObjects;

    /// <summary>Next bounded action required to prepare one inventory Waystone.</summary>
    public enum WaystoneCraftingAction
    {
        Reject,
        Ready,
        Identify,
        Alchemy,
        Exalted,
    }

    /// <summary>
    ///     Small PoE2 Waystone-only crafting policy. Identification intentionally uses the agreed
    ///     temporary heuristic: Normal is usable without identification; Magic/Rare with no
    ///     explicit modifiers is treated as unidentified.
    /// </summary>
    public static class WaystoneCrafting
    {
        public const string WisdomPath = "Metadata/Items/Currency/CurrencyIdentification";
        public const string AlchemyPath = "Metadata/Items/Currency/CurrencyUpgradeToRare";
        public const string ExaltedPath = "Metadata/Items/Currency/CurrencyAddModToRare";

        /// <summary>Returns the next required action without issuing input.</summary>
        public static WaystoneCraftingAction GetNextAction(
            InventorySnapshotItem item,
            AutoExile2Settings settings,
            out string reason)
        {
            if (!item.IsWaystone || !item.Rarity.HasValue)
            {
                reason = item.IsWaystone ? "rarity unavailable" : "not a Waystone";
                return WaystoneCraftingAction.Reject;
            }

            var action = GetUnprotectedNextAction(item, settings, out var actionReason);
            if (item.WaystoneCorrupted == true)
            {
                if (action == WaystoneCraftingAction.Ready)
                {
                    reason = "Corrupted Waystone meets configured filters and is ready";
                    return WaystoneCraftingAction.Ready;
                }

                reason = action is WaystoneCraftingAction.Identify or
                    WaystoneCraftingAction.Alchemy or WaystoneCraftingAction.Exalted
                    ? $"Corrupted Waystone cannot be crafted; {GetCurrencyLabel(action)} would be required"
                    : $"Corrupted Waystone rejected: {actionReason}";
                return WaystoneCraftingAction.Reject;
            }

            if (!item.WaystoneCorrupted.HasValue &&
                action is (WaystoneCraftingAction.Identify or WaystoneCraftingAction.Alchemy or WaystoneCraftingAction.Exalted))
            {
                reason = "Waystone Corrupted status is unavailable; refusing to use currency";
                return WaystoneCraftingAction.Reject;
            }

            reason = actionReason;
            return action;
        }

        private static WaystoneCraftingAction GetUnprotectedNextAction(
            InventorySnapshotItem item,
            AutoExile2Settings settings,
            out string reason)
        {
            if (!item.Rarity.HasValue)
            {
                reason = "rarity unavailable";
                return WaystoneCraftingAction.Reject;
            }

            var targetMods = Math.Clamp(settings.MaxWaystoneMods, 4, 6);
            switch (item.Rarity.Value)
            {
                case Rarity.Normal:
                    reason = "Normal Waystone requires Alchemy";
                    return WaystoneCraftingAction.Alchemy;

                case Rarity.Magic:
                    if (item.ExplicitMods.Count == 0)
                    {
                        reason = "Magic Waystone has no visible mods and requires Wisdom";
                        return WaystoneCraftingAction.Identify;
                    }

                    reason = "Magic Waystone requires Alchemy";
                    return WaystoneCraftingAction.Alchemy;

                case Rarity.Rare:
                    if (item.ExplicitMods.Count == 0)
                    {
                        reason = "Rare Waystone has no visible mods and requires Wisdom";
                        return WaystoneCraftingAction.Identify;
                    }

                    if (item.ExplicitMods.Count < targetMods)
                    {
                        reason = $"Rare Waystone requires Exalted ({item.ExplicitMods.Count}/{targetMods})";
                        return WaystoneCraftingAction.Exalted;
                    }

                    if (item.ExplicitMods.Count > targetMods)
                    {
                        reason = $"Waystone has {item.ExplicitMods.Count} mods above target {targetMods}";
                        return WaystoneCraftingAction.Reject;
                    }

                    if (WaystoneFilter.IsEligible(item, settings, out var rejection))
                    {
                        reason = "Waystone is ready";
                        return WaystoneCraftingAction.Ready;
                    }

                    reason = rejection;
                    return WaystoneCraftingAction.Reject;

                default:
                    reason = $"unsupported rarity {item.Rarity.Value}";
                    return WaystoneCraftingAction.Reject;
            }
        }

        /// <summary>Returns the exact ordinary currency metadata path for an action.</summary>
        public static string GetCurrencyPath(WaystoneCraftingAction action) => action switch
        {
            WaystoneCraftingAction.Identify => WisdomPath,
            WaystoneCraftingAction.Alchemy => AlchemyPath,
            WaystoneCraftingAction.Exalted => ExaltedPath,
            _ => string.Empty,
        };

        /// <summary>Human-readable currency label for runtime status.</summary>
        public static string GetCurrencyLabel(WaystoneCraftingAction action) => action switch
        {
            WaystoneCraftingAction.Identify => "Scroll of Wisdom",
            WaystoneCraftingAction.Alchemy => "Orb of Alchemy",
            WaystoneCraftingAction.Exalted => "Exalted Orb",
            _ => action.ToString(),
        };
    }
}
