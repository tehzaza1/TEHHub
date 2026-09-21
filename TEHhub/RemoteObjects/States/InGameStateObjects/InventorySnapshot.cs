// <copyright file="InventorySnapshot.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace TEHhub.RemoteObjects.States.InGameStateObjects
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using TEHhub.Offsets.Objects.States.InGameState;
    using TEHhub.Offsets.Shared;
    using TEHhub.Plugin;
    using TEHhub.RemoteEnums;
    using TEHhub.RemoteObjects.Components;

    /// <summary>
    ///     Result state for one read-only inventory snapshot.
    /// </summary>
    public enum InventorySnapshotState
    {
        /// <summary>The inventory is not present in the current game context.</summary>
        Unavailable,

        /// <summary>The inventory exists but changed or could not be read coherently.</summary>
        Loading,

        /// <summary>The inventory structure and all returned items were read coherently.</summary>
        Ready,
    }

    /// <summary>
    ///     Controls how much item metadata an inventory snapshot reads.
    /// </summary>
    public enum InventorySnapshotDetailLevel
    {
        /// <summary>Read coherent slot bounds and validated Item wrappers only.</summary>
        Basic,

        /// <summary>Also read names, rarity, stacks, charges, components, and item modifiers.</summary>
        Full,
    }

    /// <summary>
    ///     Stable, path-based inventory item category. Rarity is intentionally kept separate:
    ///     for example, a Unique Jewel remains Category=Jewel and Rarity=Unique.
    /// </summary>
    public enum InventoryItemCategory
    {
        /// <summary>The metadata path is valid but not covered by a known category.</summary>
        Other,

        /// <summary>A Tier 1-16 PoE2 Waystone.</summary>
        Waystone,

        /// <summary>A non-Waystone map item.</summary>
        Map,

        /// <summary>Currency, including microtransaction currency.</summary>
        Currency,

        /// <summary>Weapons, armour, rings, amulets, belts, or quivers.</summary>
        Equipment,

        /// <summary>Skill, support, or other gem item.</summary>
        Gem,

        /// <summary>Flask or charm stored under the Flask metadata family.</summary>
        FlaskOrCharm,

        /// <summary>Jewel item.</summary>
        Jewel,

        /// <summary>Precursor tablet / tower augment item.</summary>
        Tablet,

        /// <summary>Map fragment, scarab, pinnacle key, or ultimatum key.</summary>
        Fragment,

        /// <summary>Soul Core or another socketable item family.</summary>
        Socketable,

        /// <summary>Relic or Sanctum item.</summary>
        Relic,

        /// <summary>Quest item.</summary>
        Quest,

        /// <summary>League-specific inventory item.</summary>
        LeagueItem,
    }

    /// <summary>
    ///     Classifies inventory items from locale-independent metadata paths without inspecting
    ///     display names or item art.
    /// </summary>
    public static class InventoryItemClassifier
    {
        private const string WaystonePrefix = "Metadata/Items/Maps/MapKeyTier";

        /// <summary>Gets the stable category for an item metadata path.</summary>
        /// <param name="path">Item metadata path.</param>
        /// <returns>Known category, or <see cref="InventoryItemCategory.Other" />.</returns>
        public static InventoryItemCategory Classify(string? path)
        {
            if (TryGetWaystoneTier(path, out _))
            {
                return InventoryItemCategory.Waystone;
            }

            if (StartsWith(path, "Metadata/Items/Maps/"))
            {
                return InventoryItemCategory.Map;
            }

            if (StartsWith(path, "Metadata/Items/Currency/") ||
                StartsWith(path, "Metadata/Items/MicrotransactionCurrency/"))
            {
                return InventoryItemCategory.Currency;
            }

            if (StartsWith(path, "Metadata/Items/Armours/") ||
                StartsWith(path, "Metadata/Items/Weapons/") ||
                StartsWith(path, "Metadata/Items/Amulets/") ||
                StartsWith(path, "Metadata/Items/Belts/") ||
                StartsWith(path, "Metadata/Items/Quivers/") ||
                StartsWith(path, "Metadata/Items/Rings/"))
            {
                return InventoryItemCategory.Equipment;
            }

            if (StartsWith(path, "Metadata/Items/Gem/") ||
                StartsWith(path, "Metadata/Items/Gems/"))
            {
                return InventoryItemCategory.Gem;
            }

            if (StartsWith(path, "Metadata/Items/Flasks/"))
            {
                return InventoryItemCategory.FlaskOrCharm;
            }

            if (StartsWith(path, "Metadata/Items/Jewels/"))
            {
                return InventoryItemCategory.Jewel;
            }

            if (StartsWith(path, "Metadata/Items/TowerAugment/"))
            {
                return InventoryItemCategory.Tablet;
            }

            if (StartsWith(path, "Metadata/Items/MapFragments/") ||
                StartsWith(path, "Metadata/Items/Scarabs/") ||
                StartsWith(path, "Metadata/Items/Pinnacle/") ||
                StartsWith(path, "Metadata/Items/UltimatumKey/"))
            {
                return InventoryItemCategory.Fragment;
            }

            if (StartsWith(path, "Metadata/Items/SoulCores/"))
            {
                return InventoryItemCategory.Socketable;
            }

            if (StartsWith(path, "Metadata/Items/Relics/") ||
                StartsWith(path, "Metadata/Items/Sanctum/"))
            {
                return InventoryItemCategory.Relic;
            }

            if (StartsWith(path, "Metadata/Items/Quest/") ||
                StartsWith(path, "Metadata/Items/QuestItems/"))
            {
                return InventoryItemCategory.Quest;
            }

            if (StartsWith(path, "Metadata/Items/Expedition/") ||
                StartsWith(path, "Metadata/Items/Heist/") ||
                StartsWith(path, "Metadata/Items/Ultimatum/"))
            {
                return InventoryItemCategory.LeagueItem;
            }

            return InventoryItemCategory.Other;
        }

        /// <summary>Tries to parse an exact Tier 1-16 PoE2 Waystone metadata path.</summary>
        /// <param name="path">Item metadata path.</param>
        /// <param name="tier">Parsed Waystone tier.</param>
        /// <returns>True only for a recognized Waystone path.</returns>
        public static bool TryGetWaystoneTier(string? path, out int tier)
        {
            tier = 0;
            if (string.IsNullOrWhiteSpace(path) ||
                !path.StartsWith(WaystonePrefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var suffix = path[WaystonePrefix.Length..];
            return int.TryParse(suffix, out tier) && tier is >= 1 and <= 16;
        }

        private static bool StartsWith(string? path, string prefix) =>
            path?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    ///     One physical inventory cell. Empty cells have zero wrapper and item addresses.
    /// </summary>
    /// <param name="X">Zero-based inventory column.</param>
    /// <param name="Y">Zero-based inventory row.</param>
    /// <param name="WrapperAddress">InventoryItemStruct occupying the cell, or zero.</param>
    /// <param name="ItemAddress">Validated Item address occupying the cell, or zero.</param>
    public sealed record InventorySnapshotSlot(
        int X,
        int Y,
        IntPtr WrapperAddress,
        IntPtr ItemAddress);

    /// <summary>
    ///     One immutable modifier decoded from an item's Mods component.
    /// </summary>
    /// <param name="Name">Internal modifier name.</param>
    /// <param name="Value0">First value, or NaN when the modifier has no value.</param>
    /// <param name="Value1">Second value, or NaN when unused.</param>
    public sealed record InventorySnapshotMod(string Name, float Value0, float Value1);

    /// <summary>
    ///     Targeted read of one inventory cell. Crafting can re-read only its working slot instead
    ///     of rebuilding the entire inventory snapshot after every currency action.
    /// </summary>
    /// <param name="State">Unavailable, Loading, or Ready.</param>
    /// <param name="Name">Inventory id that owns the cell.</param>
    /// <param name="X">Zero-based column.</param>
    /// <param name="Y">Zero-based row.</param>
    /// <param name="ServerRequestCounter">Inventory request counter observed with the cell.</param>
    /// <param name="Item">Item occupying the cell, or null when the Ready cell is empty.</param>
    /// <param name="Diagnostic">Short read result.</param>
    public sealed record InventorySlotItemSnapshot(
        InventorySnapshotState State,
        InventoryName Name,
        int X,
        int Y,
        int ServerRequestCounter,
        InventorySnapshotItem? Item,
        string Diagnostic)
    {
        /// <summary>Gets a value indicating whether the requested cell was read and is empty.</summary>
        public bool IsEmpty => this.State == InventorySnapshotState.Ready && this.Item == null;

        /// <summary>
        ///     Gets the native inventory structure revision observed during this targeted read.
        /// </summary>
        public ulong SourceRevision { get; init; }
    }

    /// <summary>
    ///     One distinct inventory item and its native slot bounds.
    /// </summary>
    /// <param name="Item">Live read-only item wrapper.</param>
    /// <param name="WrapperAddress">Native InventoryItemStruct address.</param>
    /// <param name="SlotStartX">Inclusive native start column.</param>
    /// <param name="SlotStartY">Inclusive native start row.</param>
    /// <param name="SlotEndX">Native end column.</param>
    /// <param name="SlotEndY">Native end row.</param>
    public sealed record InventorySnapshotItem(
        Item Item,
        IntPtr WrapperAddress,
        int SlotStartX,
        int SlotStartY,
        int SlotEndX,
        int SlotEndY)
    {
        /// <summary>Gets the validated native Item address.</summary>
        public IntPtr ItemAddress => this.Item.Address;

        /// <summary>Gets the metadata path used for stable item classification.</summary>
        public string Path => this.Item.Path;

        /// <summary>Gets the number of occupied columns.</summary>
        public int Width => Math.Max(0, this.SlotEndX - this.SlotStartX);

        /// <summary>Gets the number of occupied rows.</summary>
        public int Height => Math.Max(0, this.SlotEndY - this.SlotStartY);

        /// <summary>
        ///     Gets the PoE2 Waystone tier encoded by the authoritative metadata path, or null when
        ///     this item is not a recognized Tier 1-16 Waystone.
        /// </summary>
        public int? WaystoneTier =>
            InventoryItemClassifier.TryGetWaystoneTier(this.Path, out var tier) ? tier : null;

        /// <summary>Gets a value indicating whether this item is a recognized PoE2 Waystone.</summary>
        public bool IsWaystone => this.WaystoneTier.HasValue;

        /// <summary>Gets the stable path-based item category.</summary>
        public InventoryItemCategory Category => InventoryItemClassifier.Classify(this.Path);

        /// <summary>Gets the localized base item name when Full details were requested.</summary>
        public string BaseItemName { get; init; } = string.Empty;

        /// <summary>Gets the locale-independent BaseItemTypes identifier.</summary>
        public string InternalName { get; init; } = string.Empty;

        /// <summary>
        ///     Gets item rarity, or null when the Mods component was absent or could not be read.
        ///     Unknown must never be interpreted as Normal.
        /// </summary>
        public Rarity? Rarity { get; init; }

        /// <summary>Gets the current stack count, or null when the item has no Stack component.</summary>
        public int? StackCount { get; init; }

        /// <summary>Gets the ordinary inventory stack limit, or null when unavailable.</summary>
        public int? MaxStack { get; init; }

        /// <summary>Gets the specialized-tab stack limit, or null when unavailable.</summary>
        public int? MaxStackTab { get; init; }

        /// <summary>Gets the current charge count, or null when the item has no Charges component.</summary>
        public int? CurrentCharges { get; init; }

        /// <summary>Gets the charge cost per use, or null when unavailable.</summary>
        public int? ChargesPerUse { get; init; }

        /// <summary>Gets all component names materialized on the Item.</summary>
        public IReadOnlyList<string> ComponentNames { get; init; } = Array.Empty<string>();

        /// <summary>Gets implicit modifiers copied from the Mods component.</summary>
        public IReadOnlyList<InventorySnapshotMod> ImplicitMods { get; init; } = Array.Empty<InventorySnapshotMod>();

        /// <summary>Gets explicit modifiers copied from the Mods component.</summary>
        public IReadOnlyList<InventorySnapshotMod> ExplicitMods { get; init; } = Array.Empty<InventorySnapshotMod>();

        /// <summary>
        ///     Gets display-ready explicit modifier text. Entries are index-aligned with
        ///     <see cref="ExplicitMods" /> and raw modifier ids remain available separately.
        /// </summary>
        public IReadOnlyList<string> ExplicitModsDisplay { get; init; } = Array.Empty<string>();

        /// <summary>Gets enchant modifiers copied from the Mods component.</summary>
        public IReadOnlyList<InventorySnapshotMod> EnchantMods { get; init; } = Array.Empty<InventorySnapshotMod>();

        /// <summary>Gets other supported modifier groups copied from the Mods component.</summary>
        public IReadOnlyList<InventorySnapshotMod> OtherMods { get; init; } = Array.Empty<InventorySnapshotMod>();

        /// <summary>
        ///     Gets aggregate Stats.dat values contributed by item modifiers. The full dictionary is
        ///     retained so AutoExile2 can map only the approved Waystone stats without rescanning.
        /// </summary>
        public IReadOnlyDictionary<GameStats, int> ModStats { get; init; } =
            new Dictionary<GameStats, int>();

        /// <summary>Gets the Waystone Revives Available count (6 - explicit mods count), or null if not a waystone.</summary>
        public int? WaystoneRevives =>
            this.IsWaystone ? Math.Max(0, 6 - this.ExplicitMods.Count) : null;

        /// <summary>Gets the Waystone Item Rarity percentage from aggregate ModStats, or null if absent.</summary>
        public int? WaystoneItemRarity =>
            this.ModStats.TryGetValue(GameStats.map_pack_size_positive_percentage_final_from_map, out var v) ? v : null;

        /// <summary>Gets the Waystone Pack Size percentage from aggregate ModStats, or null if absent.</summary>
        public int? WaystonePackSize =>
            this.ModStats.TryGetValue(GameStats.map_number_of_magic_and_rare_packs_positive_percentage_final_and_rare_monster_modifiers_chance_positive_percentage_final_from_map, out var v) ? v : null;

        /// <summary>Gets the Waystone Monster Rarity percentage from aggregate ModStats, or null if absent.</summary>
        public int? WaystoneMonsterRarity =>
            this.ModStats.TryGetValue(GameStats.map_monster_potency_positive_percentage_final_from_map, out var v) ? v : null;

        /// <summary>Gets the Waystone Monster Effectiveness percentage from aggregate ModStats, or null if absent.</summary>
        public int? WaystoneMonsterEffectiveness =>
            this.ModStats.TryGetValue(GameStats.map_map_item_drop_chance_positive_percentage_final_from_map, out var v) ? v : null;

        /// <summary>Gets the Waystone Drop Chance percentage from aggregate ModStats, or null if absent.</summary>
        public int? WaystoneDropChance =>
            this.ModStats.TryGetValue(GameStats.map_unique_item_drop_chance_positive_percentage, out var v) ? v : null;

        /// <summary>Gets optional-detail read diagnostics. Empty means no read error was observed.</summary>
        public string DetailDiagnostic { get; init; } = string.Empty;
    }

    /// <summary>
    ///     Coherent, read-only view of one live inventory. Ready with zero items means empty;
    ///     it is intentionally different from Loading or Unavailable.
    /// </summary>
    public sealed record InventorySnapshot(
        InventorySnapshotState State,
        InventoryName Name,
        IntPtr Address,
        int Columns,
        int Rows,
        int ServerRequestCounter,
        IReadOnlyList<InventorySnapshotItem> Items,
        ulong Revision,
        string Diagnostic)
    {
        /// <summary>Gets a value indicating whether the inventory was read and contains no items.</summary>
        public bool IsEmpty => this.State == InventorySnapshotState.Ready && this.Items.Count == 0;

        /// <summary>Gets the requested item-detail level.</summary>
        public InventorySnapshotDetailLevel DetailLevel { get; init; } = InventorySnapshotDetailLevel.Basic;

        /// <summary>Gets every physical inventory cell in row-major order, including empty cells.</summary>
        public IReadOnlyList<InventorySnapshotSlot> Slots { get; init; } = Array.Empty<InventorySnapshotSlot>();

        /// <summary>
        ///     Gets the cheap native structure revision used to reuse an unchanged snapshot without
        ///     scanning Item components again.
        /// </summary>
        public ulong SourceRevision { get; init; }
    }

    internal static class InventorySnapshotReader
    {
        private const int MaxSlotEntries = 65536;
        private const ulong FnvOffset = 14695981039346656037UL;
        private const ulong FnvPrime = 1099511628211UL;

        internal static InventorySnapshot Read(
            ServerData serverData,
            InventoryName name,
            InventorySnapshotDetailLevel detailLevel,
            InventorySnapshot? cachedSnapshot,
            bool forceRefresh)
        {
            if (!serverData.TryGetInventoryAddress(name, out var address) || address == IntPtr.Zero)
            {
                return Empty(
                    InventorySnapshotState.Unavailable,
                    name,
                    address,
                    detailLevel,
                    "Inventory is not present in live PlayerInventories.");
            }

            if (!CanonicalStructuralInvariants.IsCanonicalPointer(address))
            {
                return Empty(
                    InventorySnapshotState.Loading,
                    name,
                    address,
                    detailLevel,
                    "Inventory pointer is not canonical.");
            }

            var reader = Core.Process?.Handle;
            if (reader == null || !reader.TryReadMemory<InventoryStruct>(address, out var inventory))
            {
                return Empty(
                    InventorySnapshotState.Loading,
                    name,
                    address,
                    detailLevel,
                    "InventoryStruct is unreadable.");
            }

            var vector = CanonicalStructuralInvariants.ValidateStdVector(inventory.ItemList, IntPtr.Size);
            if (!vector.IsValid)
            {
                return new InventorySnapshot(
                    InventorySnapshotState.Loading,
                    name,
                    address,
                    inventory.TotalBoxes.X,
                    inventory.TotalBoxes.Y,
                    inventory.ServerRequestCounter,
                    Array.Empty<InventorySnapshotItem>(),
                    0,
                    $"ItemList is not coherent: {vector.Detail}")
                {
                    DetailLevel = detailLevel,
                };
            }

            var sourceRevision = ComputeSourceRevision(address, inventory);
            if (!forceRefresh &&
                cachedSnapshot?.State == InventorySnapshotState.Ready &&
                cachedSnapshot.SourceRevision == sourceRevision &&
                cachedSnapshot.DetailLevel >= detailLevel)
            {
                return cachedSnapshot;
            }

            var count = inventory.ItemList.TotalElements(IntPtr.Size);
            if (count < 0 || count > MaxSlotEntries)
            {
                return new InventorySnapshot(
                    InventorySnapshotState.Loading,
                    name,
                    address,
                    inventory.TotalBoxes.X,
                    inventory.TotalBoxes.Y,
                    inventory.ServerRequestCounter,
                    Array.Empty<InventorySnapshotItem>(),
                    0,
                    $"ItemList count {count} exceeds the {MaxSlotEntries} safety limit.")
                {
                    DetailLevel = detailLevel,
                    SourceRevision = sourceRevision,
                };
            }

            var slotPointers = count == 0 ? Array.Empty<IntPtr>() : new IntPtr[(int)count];
            if (count > 0 && !reader.TryReadMemoryArray(inventory.ItemList.First, slotPointers, out _))
            {
                return new InventorySnapshot(
                    InventorySnapshotState.Loading,
                    name,
                    address,
                    inventory.TotalBoxes.X,
                    inventory.TotalBoxes.Y,
                    inventory.ServerRequestCounter,
                    Array.Empty<InventorySnapshotItem>(),
                    0,
                    "ItemList contents are unreadable.")
                {
                    DetailLevel = detailLevel,
                    SourceRevision = sourceRevision,
                };
            }

            var items = new List<InventorySnapshotItem>();
            var validatedItemAddresses = new Dictionary<IntPtr, IntPtr>();
            var invalidEntries = 0;
            var revision = FnvOffset;
            revision = AddHash(revision, address.ToInt64());
            revision = AddHash(revision, inventory.ItemList.First.ToInt64());
            revision = AddHash(revision, inventory.ItemList.Last.ToInt64());
            revision = AddHash(revision, inventory.ServerRequestCounter);

            foreach (var wrapperAddress in slotPointers.Distinct())
            {
                revision = AddHash(revision, wrapperAddress.ToInt64());
                if (wrapperAddress == IntPtr.Zero)
                {
                    continue;
                }

                if (!CanonicalStructuralInvariants.IsCanonicalPointer(wrapperAddress) ||
                    !reader.TryReadMemory<InventoryItemStruct>(wrapperAddress, out var wrapper) ||
                    !PluginUiElementReflection.TryValidateItemAddress(wrapper.Item, out _, out _))
                {
                    invalidEntries++;
                    continue;
                }

                var item = new Item(wrapper.Item);
                if (!item.IsValid || string.IsNullOrEmpty(item.Path) ||
                    !item.Path.StartsWith("Metadata/Items/", StringComparison.OrdinalIgnoreCase))
                {
                    invalidEntries++;
                    continue;
                }

                revision = AddHash(revision, wrapper.Item.ToInt64());
                revision = AddHash(revision, wrapper.SlotStart.X);
                revision = AddHash(revision, wrapper.SlotStart.Y);
                revision = AddHash(revision, wrapper.SlotEnd.X);
                revision = AddHash(revision, wrapper.SlotEnd.Y);
                var snapshotItem = new InventorySnapshotItem(
                    item,
                    wrapperAddress,
                    wrapper.SlotStart.X,
                    wrapper.SlotStart.Y,
                    wrapper.SlotEnd.X,
                    wrapper.SlotEnd.Y);
                if (detailLevel == InventorySnapshotDetailLevel.Full)
                {
                    snapshotItem = ReadFullDetails(snapshotItem);
                }

                validatedItemAddresses[wrapperAddress] = wrapper.Item;
                items.Add(snapshotItem);
            }

            var slots = BuildSlots(
                inventory.TotalBoxes.X,
                inventory.TotalBoxes.Y,
                slotPointers,
                validatedItemAddresses);

            if (invalidEntries > 0)
            {
                return new InventorySnapshot(
                    InventorySnapshotState.Loading,
                    name,
                    address,
                    inventory.TotalBoxes.X,
                    inventory.TotalBoxes.Y,
                    inventory.ServerRequestCounter,
                    items.ToArray(),
                    revision,
                    $"{invalidEntries} item wrapper(s) were stale or invalid during this read.")
                {
                    DetailLevel = detailLevel,
                    Slots = slots,
                    SourceRevision = sourceRevision,
                };
            }

            return new InventorySnapshot(
                InventorySnapshotState.Ready,
                name,
                address,
                inventory.TotalBoxes.X,
                inventory.TotalBoxes.Y,
                inventory.ServerRequestCounter,
                items.ToArray(),
                revision,
                items.Count == 0 ? "Ready and empty." : $"Ready with {items.Count} distinct item(s).")
            {
                DetailLevel = detailLevel,
                Slots = slots,
                SourceRevision = sourceRevision,
            };
        }

        internal static InventorySlotItemSnapshot ReadItemAt(
            ServerData serverData,
            InventoryName name,
            int x,
            int y,
            InventorySnapshotDetailLevel detailLevel)
        {
            if (!serverData.TryGetInventoryAddress(name, out var address) || address == IntPtr.Zero)
            {
                return ItemAtResult(
                    InventorySnapshotState.Unavailable,
                    name,
                    x,
                    y,
                    0,
                    null,
                    "Inventory is not present in live PlayerInventories.");
            }

            var reader = Core.Process?.Handle;
            if (!CanonicalStructuralInvariants.IsCanonicalPointer(address) ||
                reader == null ||
                !reader.TryReadMemory<InventoryStruct>(address, out var inventory))
            {
                return ItemAtResult(
                    InventorySnapshotState.Loading,
                    name,
                    x,
                    y,
                    0,
                    null,
                    "InventoryStruct is unavailable or unreadable.");
            }

            if (x < 0 || y < 0 || x >= inventory.TotalBoxes.X || y >= inventory.TotalBoxes.Y)
            {
                return ItemAtResult(
                    InventorySnapshotState.Unavailable,
                    name,
                    x,
                    y,
                    inventory.ServerRequestCounter,
                    null,
                    $"Slot ({x},{y}) is outside the {inventory.TotalBoxes.X}x{inventory.TotalBoxes.Y} inventory.");
            }

            var sourceRevision = ComputeSourceRevision(address, inventory);
            var vector = CanonicalStructuralInvariants.ValidateStdVector(inventory.ItemList, IntPtr.Size);
            var slotIndex = (y * inventory.TotalBoxes.X) + x;
            if (!vector.IsValid || inventory.ItemList.TotalElements(IntPtr.Size) <= slotIndex)
            {
                return ItemAtResult(
                    InventorySnapshotState.Loading,
                    name,
                    x,
                    y,
                    inventory.ServerRequestCounter,
                    null,
                    $"Slot vector is incomplete: {vector.Detail}");
            }

            var slotAddress = IntPtr.Add(inventory.ItemList.First, slotIndex * IntPtr.Size);
            if (!reader.TryReadMemory<IntPtr>(slotAddress, out var wrapperAddress))
            {
                return ItemAtResult(
                    InventorySnapshotState.Loading,
                    name,
                    x,
                    y,
                    inventory.ServerRequestCounter,
                    null,
                    "Slot pointer is unreadable.");
            }

            if (wrapperAddress == IntPtr.Zero)
            {
                return ItemAtResult(
                    InventorySnapshotState.Ready,
                    name,
                    x,
                    y,
                    inventory.ServerRequestCounter,
                    null,
                    "Slot is ready and empty.",
                    sourceRevision);
            }

            if (!CanonicalStructuralInvariants.IsCanonicalPointer(wrapperAddress) ||
                !reader.TryReadMemory<InventoryItemStruct>(wrapperAddress, out var wrapper) ||
                !PluginUiElementReflection.TryValidateItemAddress(wrapper.Item, out _, out _))
            {
                return ItemAtResult(
                    InventorySnapshotState.Loading,
                    name,
                    x,
                    y,
                    inventory.ServerRequestCounter,
                    null,
                    "Slot item wrapper is stale or invalid.");
            }

            var item = new Item(wrapper.Item);
            if (!item.IsValid || string.IsNullOrEmpty(item.Path) ||
                !item.Path.StartsWith("Metadata/Items/", StringComparison.OrdinalIgnoreCase))
            {
                return ItemAtResult(
                    InventorySnapshotState.Loading,
                    name,
                    x,
                    y,
                    inventory.ServerRequestCounter,
                    null,
                    "Slot Item could not be validated.");
            }

            var snapshotItem = new InventorySnapshotItem(
                item,
                wrapperAddress,
                wrapper.SlotStart.X,
                wrapper.SlotStart.Y,
                wrapper.SlotEnd.X,
                wrapper.SlotEnd.Y);
            if (detailLevel == InventorySnapshotDetailLevel.Full)
            {
                snapshotItem = ReadFullDetails(snapshotItem);
            }

            return ItemAtResult(
                InventorySnapshotState.Ready,
                name,
                x,
                y,
                inventory.ServerRequestCounter,
                snapshotItem,
                $"Ready with {snapshotItem.Path}.",
                sourceRevision);
        }

        private static InventorySnapshot Empty(
            InventorySnapshotState state,
            InventoryName name,
            IntPtr address,
            InventorySnapshotDetailLevel detailLevel,
            string diagnostic)
        {
            return new InventorySnapshot(
                state,
                name,
                address,
                0,
                0,
                0,
                Array.Empty<InventorySnapshotItem>(),
                0,
                diagnostic)
            {
                DetailLevel = detailLevel,
            };
        }

        private static InventorySnapshotSlot[] BuildSlots(
            int columns,
            int rows,
            IReadOnlyList<IntPtr> slotPointers,
            IReadOnlyDictionary<IntPtr, IntPtr> validatedItemAddresses)
        {
            if (columns <= 0 || rows <= 0 || (long)columns * rows > MaxSlotEntries)
            {
                return Array.Empty<InventorySnapshotSlot>();
            }

            var slots = new InventorySnapshotSlot[columns * rows];
            for (var index = 0; index < slots.Length; index++)
            {
                var wrapperAddress = index < slotPointers.Count ? slotPointers[index] : IntPtr.Zero;
                var itemAddress = validatedItemAddresses.TryGetValue(wrapperAddress, out var validatedAddress)
                    ? validatedAddress
                    : IntPtr.Zero;
                slots[index] = new InventorySnapshotSlot(
                    index % columns,
                    index / columns,
                    wrapperAddress,
                    itemAddress);
            }

            return slots;
        }

        private static InventorySnapshotItem ReadFullDetails(InventorySnapshotItem snapshotItem)
        {
            var item = snapshotItem.Item;
            var diagnostics = new List<string>();
            var componentNames = Array.Empty<string>();
            var baseItemName = string.Empty;
            var internalName = string.Empty;
            Rarity? rarity = null;
            int? stackCount = null;
            int? maxStack = null;
            int? maxStackTab = null;
            int? currentCharges = null;
            int? chargesPerUse = null;
            IReadOnlyList<InventorySnapshotMod> implicitMods = Array.Empty<InventorySnapshotMod>();
            IReadOnlyList<InventorySnapshotMod> explicitMods = Array.Empty<InventorySnapshotMod>();
            IReadOnlyList<string> explicitModsDisplay = Array.Empty<string>();
            IReadOnlyList<InventorySnapshotMod> enchantMods = Array.Empty<InventorySnapshotMod>();
            IReadOnlyList<InventorySnapshotMod> otherMods = Array.Empty<InventorySnapshotMod>();
            IReadOnlyDictionary<GameStats, int> modStats = new Dictionary<GameStats, int>();

            try
            {
                componentNames = item.GetComponentNames().OrderBy(name => name, StringComparer.Ordinal).ToArray();
            }
            catch (Exception ex)
            {
                diagnostics.Add($"components:{ex.GetType().Name}");
            }

            try
            {
                if (item.TryGetComponent<Base>(out var baseComponent))
                {
                    baseItemName = baseComponent.BaseItemName;
                    internalName = baseComponent.InternalName;
                }
            }
            catch (Exception ex)
            {
                diagnostics.Add($"base:{ex.GetType().Name}");
            }

            try
            {
                if (item.TryGetComponent<Mods>(out var mods))
                {
                    rarity = mods.Rarity;
                    implicitMods = CopyMods(mods.ImplicitMods);
                    explicitMods = CopyMods(mods.ExplicitMods);
                    explicitModsDisplay = mods.ExplicitModsDisplay.ToArray();
                    enchantMods = CopyMods(mods.EnchantMods);
                    otherMods = CopyMods(mods.HellscapeMods);
                    modStats = new Dictionary<GameStats, int>(mods.ModStats);
                }
            }
            catch (Exception ex)
            {
                diagnostics.Add($"mods:{ex.GetType().Name}");
            }

            try
            {
                if (item.TryGetComponent<Stack>(out var stack))
                {
                    stackCount = stack.Count;
                    maxStack = stack.MaxStack;
                    maxStackTab = stack.MaxStackTab;
                }
            }
            catch (Exception ex)
            {
                diagnostics.Add($"stack:{ex.GetType().Name}");
            }

            try
            {
                if (item.TryGetComponent<Charges>(out var charges))
                {
                    currentCharges = charges.Current;
                    chargesPerUse = charges.PerUseCharge;
                }
            }
            catch (Exception ex)
            {
                diagnostics.Add($"charges:{ex.GetType().Name}");
            }

            return snapshotItem with
            {
                BaseItemName = baseItemName,
                InternalName = internalName,
                Rarity = rarity,
                StackCount = stackCount,
                MaxStack = maxStack,
                MaxStackTab = maxStackTab,
                CurrentCharges = currentCharges,
                ChargesPerUse = chargesPerUse,
                ComponentNames = componentNames,
                ImplicitMods = implicitMods,
                ExplicitMods = explicitMods,
                ExplicitModsDisplay = explicitModsDisplay,
                EnchantMods = enchantMods,
                OtherMods = otherMods,
                ModStats = modStats,
                DetailDiagnostic = string.Join(", ", diagnostics),
            };
        }

        private static InventorySnapshotMod[] CopyMods(
            IEnumerable<(string name, (float value0, float value1) values)> mods) =>
            mods.Select(mod => new InventorySnapshotMod(
                mod.name,
                mod.values.value0,
                mod.values.value1)).ToArray();

        private static InventorySlotItemSnapshot ItemAtResult(
            InventorySnapshotState state,
            InventoryName name,
            int x,
            int y,
            int serverRequestCounter,
            InventorySnapshotItem? item,
            string diagnostic,
            ulong sourceRevision = 0) =>
            new(state, name, x, y, serverRequestCounter, item, diagnostic)
            {
                SourceRevision = sourceRevision,
            };

        private static ulong ComputeSourceRevision(IntPtr address, InventoryStruct inventory)
        {
            var revision = FnvOffset;
            revision = AddHash(revision, address.ToInt64());
            revision = AddHash(revision, inventory.TotalBoxes.X);
            revision = AddHash(revision, inventory.TotalBoxes.Y);
            revision = AddHash(revision, inventory.ItemList.First.ToInt64());
            revision = AddHash(revision, inventory.ItemList.Last.ToInt64());
            revision = AddHash(revision, inventory.ItemList.End.ToInt64());
            return AddHash(revision, inventory.ServerRequestCounter);
        }

        private static ulong AddHash(ulong hash, long value)
        {
            unchecked
            {
                hash ^= (ulong)value;
                return hash * FnvPrime;
            }
        }
    }
}
