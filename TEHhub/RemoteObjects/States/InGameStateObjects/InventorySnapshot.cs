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
        int SlotEndY);

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
    }

    internal static class InventorySnapshotReader
    {
        private const int MaxSlotEntries = 65536;
        private const ulong FnvOffset = 14695981039346656037UL;
        private const ulong FnvPrime = 1099511628211UL;

        internal static InventorySnapshot Read(ServerData serverData, InventoryName name)
        {
            if (!serverData.TryGetInventoryAddress(name, out var address) || address == IntPtr.Zero)
            {
                return Empty(InventorySnapshotState.Unavailable, name, address, "Inventory is not present in live PlayerInventories.");
            }

            if (!CanonicalStructuralInvariants.IsCanonicalPointer(address))
            {
                return Empty(InventorySnapshotState.Loading, name, address, "Inventory pointer is not canonical.");
            }

            var reader = Core.Process?.Handle;
            if (reader == null || !reader.TryReadMemory<InventoryStruct>(address, out var inventory))
            {
                return Empty(InventorySnapshotState.Loading, name, address, "InventoryStruct is unreadable.");
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
                    $"ItemList is not coherent: {vector.Detail}");
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
                    $"ItemList count {count} exceeds the {MaxSlotEntries} safety limit.");
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
                    "ItemList contents are unreadable.");
            }

            var items = new List<InventorySnapshotItem>();
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
                items.Add(new InventorySnapshotItem(
                    item,
                    wrapperAddress,
                    wrapper.SlotStart.X,
                    wrapper.SlotStart.Y,
                    wrapper.SlotEnd.X,
                    wrapper.SlotEnd.Y));
            }

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
                    $"{invalidEntries} item wrapper(s) were stale or invalid during this read.");
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
                items.Count == 0 ? "Ready and empty." : $"Ready with {items.Count} distinct item(s).");
        }

        private static InventorySnapshot Empty(
            InventorySnapshotState state,
            InventoryName name,
            IntPtr address,
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
                diagnostic);
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
