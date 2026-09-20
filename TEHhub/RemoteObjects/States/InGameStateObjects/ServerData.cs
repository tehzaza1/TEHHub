// <copyright file="ServerData.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace TEHhub.RemoteObjects.States.InGameStateObjects
{
    using System;
    using System.Collections.Concurrent;
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
        /// <summary>
        ///     Represents the source of the most recent successful native AreaMods read.
        /// </summary>
        public enum NativeAreaModSource
        {
            /// <summary>
            ///     No native source has succeeded yet.
            /// </summary>
            None,

            /// <summary>
            ///     Successfully read using previously discovered/cached offset.
            /// </summary>
            Cached,

            /// <summary>
            ///     Successfully read using default struct offset (0x8A8).
            /// </summary>
            Default0x8A8,

            /// <summary>
            ///     Successfully read using adaptive probing scan.
            /// </summary>
            Adaptive,
        }

        private InventoryName selectedInvName = InventoryName.NoInvSelected;
        private int lastDiscoveredModsOffset = -1;
        private NativeAreaModSource lastModSource = NativeAreaModSource.None;
        private int lastModOffset = -1;
        private int lastVectorElementCount = 0;
        private int lastParsedModCount = 0;
        private int lastAdaptiveCandidatesTested = -1;
        private StdVector lastSuccessfulVector = default;

        private NativeAreaModSource lastDiscoverySource = NativeAreaModSource.None;
        private int lastDiscoveryOffset = -1;
        private int lastDiscoveryVectorElementCount = 0;
        private int lastDiscoveryParsedModCount = 0;
        private int lastDiscoveryAdaptiveCandidatesTested = -1;
        private StdVector lastDiscoveryVector = default;

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
        internal ConcurrentDictionary<InventoryName, IntPtr> PlayerInventories { get; } = new();

        /// <summary>
        ///     Gets the address of PlayerServerData in remote memory.
        /// </summary>
        public IntPtr PlayerServerDataAddress { get; private set; } = IntPtr.Zero;

        /// <summary>
        ///     Tries to get one live player-inventory address by its Inventories.dat id.
        ///     A missing or null entry is normal when that inventory is not available in the current context.
        /// </summary>
        /// <param name="name">Inventory id to resolve.</param>
        /// <param name="address">Live InventoryStruct address, or zero when unavailable.</param>
        /// <returns><see langword="true" /> only for a present, non-zero entry.</returns>
        public bool TryGetInventoryAddress(InventoryName name, out IntPtr address)
        {
            return this.PlayerInventories.TryGetValue(name, out address) && address != IntPtr.Zero;
        }

        /// <summary>
        ///     Reads a bounded snapshot of one live inventory. Ready with zero items is an authoritative
        ///     empty result; Loading and Unavailable must not be treated as empty.
        /// </summary>
        /// <param name="name">Inventory id to read.</param>
        /// <returns>Read-only inventory snapshot.</returns>
        public InventorySnapshot ReadInventorySnapshot(InventoryName name) =>
            InventorySnapshotReader.Read(this, name);

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
            if (this.TryGetGold(out int currentGold))
            {
                ImGui.Text($"Current Gold: {currentGold:N0}");
            }
            else
            {
                ImGui.TextDisabled("Current Gold: Unavailable");
            }

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

                ImGui.Separator();
                ImGui.TextDisabled("Diagnostic Probe Info:");
                ImGui.Text("Current native read:");
                ImGui.Text($"  Source: {this.lastModSource}");
                ImGui.Text(this.lastModOffset >= 0 ? $"  Offset: 0x{this.lastModOffset:X}" : "  Offset: N/A");
                ImGui.Text($"  Vector elements: {this.lastVectorElementCount}");
                ImGui.Text($"  Parsed mods: {this.lastParsedModCount}");
                if (this.lastAdaptiveCandidatesTested < 0)
                {
                    ImGui.Text("  Adaptive candidates tested: N/A");
                }
                else
                {
                    ImGui.Text($"  Adaptive candidates tested: {this.lastAdaptiveCandidatesTested}");
                }

                if (this.lastModSource != NativeAreaModSource.None)
                {
                    ImGui.Text($"  Vector First/Last/End: 0x{this.lastSuccessfulVector.First.ToInt64():X} / 0x{this.lastSuccessfulVector.Last.ToInt64():X} / 0x{this.lastSuccessfulVector.End.ToInt64():X}");
                }

                ImGui.Separator();
                if (this.lastDiscoverySource == NativeAreaModSource.None)
                {
                    ImGui.Text("Last offset discovery: None");
                }
                else
                {
                    ImGui.Text("Last offset discovery:");
                    ImGui.Text($"  Source: {this.lastDiscoverySource}");
                    ImGui.Text(this.lastDiscoveryOffset >= 0 ? $"  Offset: 0x{this.lastDiscoveryOffset:X}" : "  Offset: N/A");
                    ImGui.Text($"  Vector elements: {this.lastDiscoveryVectorElementCount}");
                    ImGui.Text($"  Parsed mods: {this.lastDiscoveryParsedModCount}");
                    if (this.lastDiscoveryAdaptiveCandidatesTested < 0)
                    {
                        ImGui.Text("  Adaptive candidates tested: N/A");
                    }
                    else
                    {
                        ImGui.Text($"  Adaptive candidates tested: {this.lastDiscoveryAdaptiveCandidatesTested}");
                    }

                    ImGui.Text($"  Vector First/Last/End: 0x{this.lastDiscoveryVector.First.ToInt64():X} / 0x{this.lastDiscoveryVector.Last.ToInt64():X} / 0x{this.lastDiscoveryVector.End.ToInt64():X}");
                }

                ImGui.Separator();

                if (ImGui.Button("Force Rescan Area Mod Offsets"))
                {
                    this.lastDiscoveredModsOffset = -1;
                    this.lastDiscoverySource = NativeAreaModSource.None;
                    this.lastDiscoveryOffset = -1;
                    this.lastDiscoveryVectorElementCount = 0;
                    this.lastDiscoveryParsedModCount = 0;
                    this.lastDiscoveryAdaptiveCandidatesTested = -1;
                    this.lastDiscoveryVector = default;
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
                this.SelectedInv.Address = this.PlayerInventories.TryGetValue(this.selectedInvName, out var address)
                    ? address
                    : IntPtr.Zero;
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
            this.PlayerServerDataAddress = IntPtr.Zero;
            this.FlaskInventory.Address = IntPtr.Zero;
            this.AreaMods.Clear();
            this.AreaModNames.Clear();
            this.lastModSource = NativeAreaModSource.None;
            this.lastModOffset = -1;
            this.lastVectorElementCount = 0;
            this.lastParsedModCount = 0;
            this.lastAdaptiveCandidatesTested = -1;
            this.lastSuccessfulVector = default;

            this.lastDiscoveredModsOffset = -1;
            this.lastDiscoverySource = NativeAreaModSource.None;
            this.lastDiscoveryOffset = -1;
            this.lastDiscoveryVectorElementCount = 0;
            this.lastDiscoveryParsedModCount = 0;
            this.lastDiscoveryAdaptiveCandidatesTested = -1;
            this.lastDiscoveryVector = default;
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
                this.PlayerServerDataAddress = IntPtr.Zero;
                this.lastModSource = NativeAreaModSource.None;
                this.lastModOffset = -1;
                this.lastVectorElementCount = 0;
                this.lastParsedModCount = 0;
                this.lastAdaptiveCandidatesTested = -1;
                this.lastSuccessfulVector = default;

                this.lastDiscoveredModsOffset = -1;
                this.lastDiscoverySource = NativeAreaModSource.None;
                this.lastDiscoveryOffset = -1;
                this.lastDiscoveryVectorElementCount = 0;
                this.lastDiscoveryParsedModCount = 0;
                this.lastDiscoveryAdaptiveCandidatesTested = -1;
                this.lastDiscoveryVector = default;
            }

            var reader = Core.Process.Handle;
            if (reader == null || reader.IsInvalid)
            {
                return;
            }

            var data = reader.ReadMemory<ServerDataOffsets>(this.Address);
            var playerDataArray = reader.ReadStdVector<IntPtr>(data.PlayerServerDataPtr);
            if (playerDataArray.Length == 0)
            {
                this.PlayerServerDataAddress = IntPtr.Zero;
                this.PlayerInventories.Clear();
                return;
            }

            var playerServerDataAddress = playerDataArray[0];
            if (playerServerDataAddress == IntPtr.Zero)
            {
                this.PlayerServerDataAddress = IntPtr.Zero;
                this.PlayerInventories.Clear();
                return;
            }

            this.PlayerServerDataAddress = playerServerDataAddress;
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
            this.lastModSource = NativeAreaModSource.None;
            this.lastModOffset = -1;
            this.lastVectorElementCount = 0;
            this.lastParsedModCount = 0;
            this.lastAdaptiveCandidatesTested = -1;
            this.lastSuccessfulVector = default;

            // 1. Try known/cached offset first
            if (this.lastDiscoveredModsOffset >= 0)
            {
                var cachedVec = reader.ReadMemory<StdVector>(playerServerDataAddress + this.lastDiscoveredModsOffset);
                if (this.TryReadModsFromVector(reader, cachedVec))
                {
                    this.lastModSource = NativeAreaModSource.Cached;
                    this.lastModOffset = this.lastDiscoveredModsOffset;
                    this.lastVectorElementCount = (int)cachedVec.TotalElements(0x40);
                    this.lastParsedModCount = this.AreaMods.Count;
                    this.lastSuccessfulVector = cachedVec;
                    return;
                }

                this.lastDiscoveredModsOffset = -1;
            }

            // 2. Try default struct offset (0x8A8)
            if (this.TryReadModsFromVector(reader, playerData.WorldAreaMods))
            {
                this.lastDiscoveredModsOffset = 0x8A8;
                this.lastModSource = NativeAreaModSource.Default0x8A8;
                this.lastModOffset = 0x8A8;
                this.lastVectorElementCount = (int)playerData.WorldAreaMods.TotalElements(0x40);
                this.lastParsedModCount = this.AreaMods.Count;
                this.lastSuccessfulVector = playerData.WorldAreaMods;

                this.lastDiscoverySource = NativeAreaModSource.Default0x8A8;
                this.lastDiscoveryOffset = 0x8A8;
                this.lastDiscoveryVectorElementCount = this.lastVectorElementCount;
                this.lastDiscoveryParsedModCount = this.lastParsedModCount;
                this.lastDiscoveryAdaptiveCandidatesTested = -1;
                this.lastDiscoveryVector = playerData.WorldAreaMods;
                return;
            }

            // 3. Adaptive Probing: scan ServerDataStructure memory for candidate StdVector containing ModArrayStruct
            this.lastAdaptiveCandidatesTested = 0;
            for (var offset = 0x0; offset <= 0xC00; offset += 8)
            {
                var candidateVec = reader.ReadMemory<StdVector>(playerServerDataAddress + offset);
                var elemCount = candidateVec.TotalElements(0x40);
                if (elemCount is > 0 and < 150)
                {
                    this.lastAdaptiveCandidatesTested++;
                    if (this.TryReadModsFromVector(reader, candidateVec))
                    {
                        this.lastDiscoveredModsOffset = offset;
                        this.lastModSource = NativeAreaModSource.Adaptive;
                        this.lastModOffset = offset;
                        this.lastVectorElementCount = (int)elemCount;
                        this.lastParsedModCount = this.AreaMods.Count;
                        this.lastSuccessfulVector = candidateVec;

                        this.lastDiscoverySource = NativeAreaModSource.Adaptive;
                        this.lastDiscoveryOffset = offset;
                        this.lastDiscoveryVectorElementCount = (int)elemCount;
                        this.lastDiscoveryParsedModCount = this.AreaMods.Count;
                        this.lastDiscoveryAdaptiveCandidatesTested = this.lastAdaptiveCandidatesTested;
                        this.lastDiscoveryVector = candidateVec;
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

        /// <summary>
        ///     Attempts to read the player's current gold balance from PlayerServerData.
        ///     The underlying field is a verified native 32-bit integer.
        /// </summary>
        /// <param name="gold">When this method returns true, contains the player's current gold balance.</param>
        /// <returns>True if the memory read succeeded (including for a legitimate 0 balance); false if memory was unavailable or invalid.</returns>
        public bool TryGetGold(out int gold)
        {
            gold = 0;
            var reader = Core.Process?.Handle;
            if (reader == null || reader.IsInvalid)
            {
                return false;
            }

            if (!this.TryGetPlayerServerDataAddress(reader, out var pServer))
            {
                return false;
            }

            return TryReadGold(reader, pServer, out gold);
        }

        /// <summary>
        ///     Attempts to read the player's current gold balance as an unsigned integer from PlayerServerData.
        ///     The underlying field is a verified native 32-bit integer.
        /// </summary>
        /// <param name="gold">When this method returns true, contains the player's current gold balance.</param>
        /// <returns>True if the memory read succeeded (including for a legitimate 0 balance); false if memory was unavailable or invalid.</returns>
        public bool TryGetGold(out uint gold)
        {
            if (this.TryGetGold(out int signedGold))
            {
                gold = (uint)signedGold;
                return true;
            }

            gold = 0;
            return false;
        }

        /// <summary>
        ///     Directly reads the player's native 32-bit gold balance given a valid PlayerServerData pointer.
        /// </summary>
        /// <param name="reader">Process memory reader handle.</param>
        /// <param name="playerServerDataAddress">Pointer to PlayerServerData.</param>
        /// <param name="gold">When this method returns true, contains the read 32-bit gold value.</param>
        /// <returns>True if the read succeeded (including for a legitimate 0 balance); false otherwise.</returns>
        public static bool TryReadGold(SafeMemoryHandle reader, IntPtr playerServerDataAddress, out int gold)
        {
            gold = 0;
            if (reader == null || reader.IsInvalid || playerServerDataAddress == IntPtr.Zero || !SafeMemoryHandle.IsValidAddress(playerServerDataAddress))
            {
                return false;
            }

            if (!reader.TryReadMemory<IntPtr>(playerServerDataAddress + PlayerServerDataOffsets.GoldRecordPtrSlot, out var recordPtr))
            {
                return false;
            }

            if (recordPtr == IntPtr.Zero || !SafeMemoryHandle.IsValidAddress(recordPtr))
            {
                return false;
            }

            if (!reader.TryReadMemory<int>(recordPtr + PlayerServerDataOffsets.GoldFieldOffset, out var goldVal))
            {
                return false;
            }

            gold = goldVal;
            return true;
        }

        private bool TryGetPlayerServerDataAddress(SafeMemoryHandle reader, out IntPtr playerServerDataAddress)
        {
            playerServerDataAddress = IntPtr.Zero;
            if (this.Address == IntPtr.Zero || !SafeMemoryHandle.IsValidAddress(this.Address))
            {
                return false;
            }

            if (this.PlayerServerDataAddress != IntPtr.Zero && SafeMemoryHandle.IsValidAddress(this.PlayerServerDataAddress))
            {
                playerServerDataAddress = this.PlayerServerDataAddress;
                return true;
            }

            if (reader.TryReadMemory<ServerDataOffsets>(this.Address, out var sDataOffsets))
            {
                var playerDataArray = reader.ReadStdVector<IntPtr>(sDataOffsets.PlayerServerDataPtr);
                if (playerDataArray.Length > 0 && playerDataArray[0] != IntPtr.Zero && SafeMemoryHandle.IsValidAddress(playerDataArray[0]))
                {
                    playerServerDataAddress = playerDataArray[0];
                    return true;
                }
            }

            return false;
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
