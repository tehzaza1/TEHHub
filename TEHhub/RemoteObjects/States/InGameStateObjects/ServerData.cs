// <copyright file="ServerData.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace TEHhub.RemoteObjects.States.InGameStateObjects
{
    using System;
    using System.Collections.Generic;
    using Coroutine;
    using TEHhub.Offsets.Natives;
    using TEHhub.Offsets.Objects.Components;
    using TEHhub.Offsets.Objects.States.InGameState;
    using ImGuiNET;
    using RemoteEnums;
    using RemoteObjects.Components;
    using Utils;

    /// <summary>
    ///     Points to the InGameState -> ServerData object.
    /// </summary>
    public class ServerData : RemoteObjectBase
    {
        private InventoryName selectedInvName = InventoryName.NoInvSelected;
        private int lastDiscoveredModsOffset = -1;

        /// <summary>
        ///     Initializes a new instance of the <see cref="ServerData" /> class.
        /// </summary>
        /// <param name="address">address of the remote memory object.</param>
        internal ServerData(IntPtr address)
            : base(address)
        {
            // Feel free to uncomment this if we ever add stuff like latency.
            //Core.CoroutinesRegistrar.Add(CoroutineHandler.Start(
            //    this.OnTimeTick(), "[ServerData] Update ServerData", int.MaxValue - 3));
        }

        /// <summary>
        ///     Gets an object that points to the flask inventory.
        /// </summary>
        public Inventory FlaskInventory { get; } = new(IntPtr.Zero, "Flask");

        /// <summary>
        ///     Gets the inventory to debug.
        /// </summary>
        internal Inventory SelectedInv { get; } = new(IntPtr.Zero, "CurrentlySelected");

        /// <summary>
        ///     Gets the inventories associated with the player.
        /// </summary>
        internal Dictionary<InventoryName, IntPtr> PlayerInventories { get; } = new();

        /// <summary>
        ///     Gets the active area / map modifiers.
        /// </summary>
        public List<AreaMod> AreaMods { get; } = new();

        /// <summary>
        ///     Gets the set of active area / map modifier raw names.
        /// </summary>
        public HashSet<string> AreaModNames { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <inheritdoc />
        internal override void ToImGui()
        {
            if (this.selectedInvName != InventoryName.NoInvSelected &&
                !this.PlayerInventories.ContainsKey(this.selectedInvName))
            {
                this.ClearCurrentlySelectedInventory();
            }

            ImGuiHelper.IntPtrToImGui("Address", this.Address);

            if (ImGui.TreeNode($"Area / Map Modifiers ({this.AreaMods.Count})###AreaModsNode"))
            {
                if (this.AreaMods.Count == 0)
                {
                    ImGui.TextDisabled("No active area modifiers detected (or in town/hideout).");
                }
                else
                {
                    for (var i = 0; i < this.AreaMods.Count; i++)
                    {
                        var mod = this.AreaMods[i];
                        var text = float.IsNaN(mod.Values.Value0)
                            ? mod.DisplayName
                            : float.IsNaN(mod.Values.Value1)
                                ? $"{mod.DisplayName}: {mod.Values.Value0}"
                                : $"{mod.DisplayName}: {mod.Values.Value0} - {mod.Values.Value1}";
                        ImGui.PushID(i);
                        ImGuiHelper.DisplayTextAndCopyOnClick(text, mod.DisplayName);
                        ImGui.PopID();
                    }
                }

                if (ImGui.Button("Force Rescan Area Mod Offsets"))
                {
                    this.lastDiscoveredModsOffset = -1;
                    this.UpdateData(true);
                }

                ImGui.TreePop();
            }

            if (ImGui.TreeNode("FlaskInventory"))
            {
                this.FlaskInventory.ToImGui();
                ImGui.TreePop();
            }

            ImGui.Text("please click Clear Selected before leaving this window.");
            if (ImGuiHelper.IEnumerableComboBox(
                "###Inventory Selector",
                this.PlayerInventories.Keys,
                ref this.selectedInvName))
            {
                this.SelectedInv.Address = this.PlayerInventories[this.selectedInvName];
            }

            ImGui.SameLine();
            if (ImGui.Button("Clear Selected"))
            {
                this.ClearCurrentlySelectedInventory();
            }

            if (this.selectedInvName != InventoryName.NoInvSelected)
            {
                if (ImGui.TreeNode("Currently Selected Inventory"))
                {
                    this.SelectedInv.ToImGui();
                    ImGui.TreePop();
                }
            }
        }

        /// <inheritdoc />
        protected override void CleanUpData()
        {
            this.ClearCurrentlySelectedInventory();
            this.PlayerInventories.Clear();
            this.FlaskInventory.Address = IntPtr.Zero;
            this.AreaMods.Clear();
            this.AreaModNames.Clear();
        }

        /// <inheritdoc />
        protected override void UpdateData(bool hasAddressChanged)
        {
            // only happens when area is changed.
            if (hasAddressChanged)
            {
                this.ClearCurrentlySelectedInventory();
                this.AreaMods.Clear();
                this.AreaModNames.Clear();
            }

            var reader = Core.Process.Handle;
            var data = reader.ReadMemory<ServerDataOffsets>(this.Address);
            var playerDataArray = reader.ReadStdVector<IntPtr>(data.PlayerServerDataPtr);
            if (playerDataArray.Length == 0)
            {
                return;
            }

            var playerServerDataAddress = playerDataArray[0];
            if (playerServerDataAddress == IntPtr.Zero)
            {
                return;
            }

            var playerData = reader.ReadMemory<ServerDataStructure>(playerServerDataAddress);
            var inventoryData = reader.ReadStdVector<InventoryArrayStruct>(playerData.PlayerInventories);
            this.PlayerInventories.Clear();
            for (var i = 0; i < inventoryData.Length; i++)
            {
                var invName = (InventoryName)inventoryData[i].InventoryId;
                var invAddr = inventoryData[i].InventoryPtr0;
                this.PlayerInventories[invName] = invAddr;
                switch (invName)
                {
                    case InventoryName.Flask1:
                        this.FlaskInventory.Address = invAddr;
                        break;
                }
            }

            // Update Area / Map Modifiers on area change or when empty
            if (hasAddressChanged || this.AreaMods.Count == 0)
            {
                this.UpdateAreaMods(reader, playerServerDataAddress, playerData);
            }
        }

        private void UpdateAreaMods(SafeMemoryHandle reader, IntPtr playerServerDataAddress, ServerDataStructure playerData)
        {
            this.AreaMods.Clear();
            this.AreaModNames.Clear();

            // 1. Try known/cached offset first
            if (this.lastDiscoveredModsOffset >= 0)
            {
                var cachedVec = reader.ReadMemory<StdVector>(playerServerDataAddress + this.lastDiscoveredModsOffset);
                if (this.TryReadModsFromVector(reader, cachedVec))
                {
                    return;
                }

                this.lastDiscoveredModsOffset = -1;
            }

            // 2. Try default struct offset (0x8A8)
            if (this.TryReadModsFromVector(reader, playerData.WorldAreaMods))
            {
                this.lastDiscoveredModsOffset = 0x8A8;
                return;
            }

            // 3. Adaptive Probing: scan ServerDataStructure memory for candidate StdVector containing ModArrayStruct
            for (var offset = 0x0; offset <= 0xC00; offset += 8)
            {
                var candidateVec = reader.ReadMemory<StdVector>(playerServerDataAddress + offset);
                var elemCount = candidateVec.TotalElements(0x40);
                if (elemCount is > 0 and < 150)
                {
                    if (this.TryReadModsFromVector(reader, candidateVec))
                    {
                        this.lastDiscoveredModsOffset = offset;
                        return;
                    }
                }
            }
        }

        private bool TryReadModsFromVector(SafeMemoryHandle reader, StdVector vec)
        {
            var count = vec.TotalElements(0x40);
            if (count is <= 0 or > 150)
            {
                return false;
            }

            var mods = reader.ReadStdVector<ModArrayStruct>(vec);
            if (mods.Length == 0)
            {
                return false;
            }

            var validModsFound = 0;
            var parsedMods = new List<AreaMod>();

            for (var i = 0; i < mods.Length; i++)
            {
                var mod = mods[i];
                if (mod.ModsPtr == IntPtr.Zero || !SafeMemoryHandle.IsValidAddress(mod.ModsPtr))
                {
                    continue;
                }

                try
                {
                    var rawName = ObjectMagicProperties.GetModName(mod.ModsPtr);
                    if (!string.IsNullOrEmpty(rawName) && rawName.Length < 256)
                    {
                        var values = ObjectMagicProperties.GetValue(mod.Values, mod.Value0);
                        parsedMods.Add(new AreaMod(rawName, rawName, values, mod.ModsPtr));
                        validModsFound++;
                    }
                }
                catch
                {
                    // Invalid mod pointer candidate
                }
            }

            if (validModsFound > 0)
            {
                foreach (var mod in parsedMods)
                {
                    this.AreaMods.Add(mod);
                    this.AreaModNames.Add(mod.RawName);
                }

                return true;
            }

            return false;
        }

        private void ClearCurrentlySelectedInventory()
        {
            this.selectedInvName = InventoryName.NoInvSelected;
            this.SelectedInv.Address = IntPtr.Zero;
        }

        private IEnumerable<Wait> OnTimeTick()
        {
            while (true)
            {
                yield return new Wait(0.2d);
                if (this.Address != IntPtr.Zero)
                {
                    using var memoryReadRegion = TEHhub.Ui.MemoryReadDiagnostics.MeasureRegion("Core.ServerData");
                    this.UpdateData(false);
                }
            }
        }
    }
}
