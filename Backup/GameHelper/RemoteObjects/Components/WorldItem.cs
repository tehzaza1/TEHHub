// <copyright file="WorldItem.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace GameHelper.RemoteObjects.Components
{
    using System;
    using GameHelper.RemoteObjects.States.InGameStateObjects;
    using GameOffsets.Objects.Components;
    using ImGuiNET;

    /// <summary>
    ///     The <see cref="WorldItem" /> component — present on a dropped/ground item entity. The ground
    ///     entity is a wrapper; the real item (carrying <see cref="Mods" />, <see cref="Base" />,
    ///     <see cref="RenderItem" />, <see cref="Stack" />) lives at <see cref="ItemEntityAddress" />.
    /// </summary>
    public class WorldItem : ComponentBase
    {
        /// <summary>
        ///     Initializes a new instance of the <see cref="WorldItem" /> class.
        /// </summary>
        /// <param name="address">address of the <see cref="WorldItem" /> component.</param>
        public WorldItem(IntPtr address)
            : base(address) { }

        /// <summary>
        ///     Gets the address of the inner item entity (the one carrying the item components).
        ///     <see cref="IntPtr.Zero" /> when unavailable.
        /// </summary>
        public IntPtr ItemEntityAddress { get; private set; } = IntPtr.Zero;

        /// <summary>
        ///     Gets the inner item instance.
        /// </summary>
        public Item? Item { get; private set; }

        /// <summary>
        ///     Gets the item path / type (e.g. Metadata/Items/Currency/CurrencyUpgradeToRare).
        /// </summary>
        public string ItemPath => this.Item?.Path ?? string.Empty;

        /// <summary>
        ///     Gets the 2D art / icon path (e.g. Art/2DItems/Currency/CurrencyUpgradeToRare.dds).
        /// </summary>
        public string ItemIcon => this.Item != null && this.Item.TryGetComponent<RenderItem>(out var r) ? r.ResourcePath : string.Empty;

        /// <summary>
        ///     Gets the item display name (e.g. Chaos Orb / Regal Orb).
        /// </summary>
        public string ItemName => this.Item != null && this.Item.TryGetComponent<Base>(out var b) ? b.BaseItemName : string.Empty;

        /// <inheritdoc />
        protected override void CleanUpData()
        {
            base.CleanUpData();
            this.ItemEntityAddress = IntPtr.Zero;
            this.Item = null;
        }

        /// <summary>
        ///     Converts the <see cref="WorldItem" /> class data to ImGui.
        /// </summary>
        internal override void ToImGui()
        {
            base.ToImGui();
            ImGui.Text($"Item Entity: {this.ItemEntityAddress.ToInt64():X}");
            if (!string.IsNullOrEmpty(this.ItemName))
            {
                ImGui.Text($"Item Name: {this.ItemName}");
            }

            if (!string.IsNullOrEmpty(this.ItemPath))
            {
                ImGui.Text($"Item Path (Type): {this.ItemPath}");
            }

            if (!string.IsNullOrEmpty(this.ItemIcon))
            {
                ImGui.Text($"Item Icon: {this.ItemIcon}");
            }

            if (this.Item != null)
            {
                if (ImGui.TreeNode("Inner Item Details"))
                {
                    this.Item.ToImGui();
                    ImGui.TreePop();
                }
            }
        }

        /// <inheritdoc />
        protected override void UpdateData(bool hasAddressChanged)
        {
            var reader = Core.Process.Handle;
            var data = reader.ReadMemory<WorldItemOffsets>(this.Address);
            this.OwnerEntityAddress = data.Header.EntityPtr;
            this.ItemEntityAddress = data.ItemEntityPtr;

            if (this.ItemEntityAddress != IntPtr.Zero)
            {
                if (this.Item == null || this.Item.Address != this.ItemEntityAddress)
                {
                    this.Item = new Item(this.ItemEntityAddress);
                }
                else
                {
                    this.Item.UpdateItem(false);
                }
            }
            else
            {
                this.Item = null;
            }
        }
    }
}
