namespace TEHhub.RemoteObjects.Components
{
    using TEHhub.Offsets.Natives;
    using TEHhub.Offsets.Objects.Components;
    using ImGuiNET;
    using System;
    using System.Collections.Generic;

    public class StateMachine : ComponentBase
    {
        public StateMachine(IntPtr address) : base(address) { }

        private const int StateStructSize = 0xC0;

        public IReadOnlyList<StateMachineState> States { get; private set; } = [];

        internal override void ToImGui()
        {
            base.ToImGui();
            if (this.TryGetRuneStationDetails(out var rs) && rs != null)
            {
                ImGui.Separator();
                ImGui.TextColored(new System.Numerics.Vector4(1f, 0.84f, 0f, 1f), "[Expedition Rune Station]");
                ImGui.Text($"Sockets: {rs.SocketCount}");
                ImGui.Text($"Golden Crown Slot: #{rs.GoldenSlotIndex + 1} (index: {rs.GoldenSlotIndex})");
                ImGui.Text($"Recipe Anchor Rune: {rs.AnchorRuneName} at Slot #{rs.AnchorSlotIndex + 1} (index: {rs.AnchorSlotIndex})");
                if (rs.IsAnchorInGoldenSlot)
                {
                    ImGui.TextColored(new System.Numerics.Vector4(0.2f, 1f, 0.4f, 1f), $"Proliferates: YES ({rs.AnchorRuneName} in Golden Slot)");
                }
                else
                {
                    ImGui.TextColored(new System.Numerics.Vector4(1f, 0.4f, 0.4f, 1f), $"Proliferates: NO (Golden Slot #{rs.GoldenSlotIndex + 1} is Blue rune; {rs.AnchorRuneName} is local-only)");
                }
                ImGui.Separator();
            }

            ImGui.Text($"State Count: {States.Count}");
            for (int i = 0; i < States.Count; i++)
            {
                var state = States[i];
                ImGui.Text($"State[{i}]: Name='{state.Name}', Value={state.Value}");
            }
        }

        private const int MaxStateMachineStates = 256;

        protected override void UpdateData(bool hasAddressChanged)
        {
            var reader = Core.Process.Handle;
            var data = reader.ReadMemory<StateMachineComponentOffsets>(this.Address);
            this.OwnerEntityAddress = data.Header.EntityPtr;

            var stateValues = reader.ReadStdVector<long>(data.StatesValues);
            var statesCount = stateValues.Length;
            if (statesCount > MaxStateMachineStates)
            {
                Console.WriteLine($"[StateMachine] Suspicious state count {statesCount} at 0x{this.Address.ToInt64():X}; capping to {MaxStateMachineStates} (audit F-120).");
                statesCount = MaxStateMachineStates;
            }

            var statesPtr = reader.ReadMemory<IntPtr>(data.StatesPtr + 0x10);
            if (statesPtr == IntPtr.Zero)
            {
                this.States = [];
                return;
            }

            var statesList = new List<StateMachineState>(statesCount);
            for (var i = 0; i < statesCount; i++)
            {
                var stateNameAddr = statesPtr + i * StateStructSize;
                var nativeContainer = reader.ReadMemory<StdString>(stateNameAddr);
                var stateName = reader.ReadStdString(nativeContainer);
                statesList.Add(new StateMachineState(stateName, stateValues[i]));
            }

            this.States = statesList;
        }

        // Offsets for resolving the authoritative socket/hole count and RuneStation details
        // that listens on this StateMachine (the "sockets" state caps at the model's 6 physical
        // socket props and under-reports recipes that use more, e.g. 7-hole Transcendent Alloy).
        private const int ListenerVectorOffset = 0x20;       // SM + 0x20 : std::vector<listener*> {begin,end,cap}
        private const int StationFromListener = 0x98;         // station = *(node) - 0x98
        private const int StationDeviceBackPtr = 0x10;        // station + 0x10 : back-ptr to device entity
        private const int StationAnchorRefOffset = 0x28;      // station + 0x28 : anchor rune row ptr
        private const int StationAnchorHolderOffset = 0x30;   // station + 0x30 : anchor holder struct
        private const int StationSocketCount = 0x38;          // station + 0x38 : int socket/hole count
        private const int StationAnchorPosOffset = 0x3C;      // station + 0x3C : anchor slot index (0-based)
        private const int StationGoldenSlotsOffset = 0x40;    // station + 0x40 : std::vector<int> golden slot indices

        private static readonly string[] RuneNames =
        [
            "Fire", "Cold", "Lightning", "Tempest", "Momentum", "Bloodletting", "Stone", "Adaptive",
            "Arcane", "Toxic", "Electrocuting", "Protective", "Cyclonic", "Vision", "Tidal",
            "Rebirth", "Prismatic", "Gasp", "Moon", "Celestial", "Opulent", "Rage",
            "Wisdom", "Sky", "Earth", "Life", "Bond", "Ward", "Soul", "Death",
            "Oath", "Time", "Power", "Bait"
        ];

        /// <summary>
        ///     Attempts to read the authoritative socket/hole count from the RuneStation object
        ///     that listens on this StateMachine. Walks the listener vector at SM + 0x20, resolves
        ///     each listener back to its station, and verifies the station's device back-pointer
        ///     matches this component's owner entity before reading the count.
        /// </summary>
        /// <param name="count">The resolved socket/hole count, when found.</param>
        /// <returns>True if a matching RuneStation was resolved; otherwise false.</returns>
        public bool TryGetRuneStationSocketCount(out int count)
        {
            if (this.TryGetRuneStationDetails(out var details) && details != null)
            {
                count = details.SocketCount;
                return true;
            }

            count = 0;
            return false;
        }

        /// <summary>
        ///     Attempts to resolve authoritative Expedition RuneStation details (sockets, golden crown slot,
        ///     anchor rune and position, proliferation status) from the listening station object.
        /// </summary>
        /// <param name="details">Resolved details when found; otherwise null.</param>
        /// <returns>True if a matching RuneStation was resolved; otherwise false.</returns>
        public bool TryGetRuneStationDetails(out RuneStationDetails? details)
        {
            details = null;
            if (this.Address == IntPtr.Zero || this.OwnerEntityAddress == IntPtr.Zero)
            {
                return false;
            }

            var reader = Core.Process.Handle;
            var listeners = reader.ReadMemory<StdVector>(this.Address + ListenerVectorOffset);
            var nodes = reader.ReadStdVector<long>(listeners);
            if (nodes == null || nodes.Length == 0 || nodes.Length > 256)
            {
                return false;
            }

            IntPtr station = IntPtr.Zero;
            foreach (var nodeValue in nodes)
            {
                if (nodeValue == 0) continue;
                var sub = reader.ReadMemory<IntPtr>(new IntPtr(nodeValue));
                if (sub == IntPtr.Zero) continue;

                var cand1 = sub - 0xA0;
                if (reader.ReadMemory<IntPtr>(cand1 + StationDeviceBackPtr) == this.OwnerEntityAddress)
                {
                    station = cand1;
                    break;
                }

                var cand2 = sub - StationFromListener; // 0x98
                if (reader.ReadMemory<IntPtr>(cand2 + StationDeviceBackPtr) == this.OwnerEntityAddress)
                {
                    station = cand2;
                    break;
                }
            }

            if (station == IntPtr.Zero)
            {
                return false;
            }

            var socketCount = reader.ReadMemory<int>(station + StationSocketCount);
            if (socketCount is <= 0 or > 16)
            {
                return false;
            }

            var anchorPos = reader.ReadMemory<int>(station + StationAnchorPosOffset);
            var goldenVec = reader.ReadMemory<StdVector>(station + StationGoldenSlotsOffset);
            int goldenSlot = -1;
            var goldenCount = goldenVec.TotalElements(sizeof(int));
            if (goldenCount > 0 && goldenCount <= 16)
            {
                var goldenSlots = reader.ReadMemoryArray<int>(goldenVec.First, (int)goldenCount);
                if (goldenSlots != null && goldenSlots.Length > 0)
                {
                    goldenSlot = goldenSlots[0];
                }
            }

            if (goldenSlot < 0)
            {
                goldenSlot = anchorPos;
            }

            var rowPtr = reader.ReadMemory<IntPtr>(station + StationAnchorRefOffset);
            bool isUnique = rowPtr == IntPtr.Zero;
            int anchorIdx = -1;

            if (!isUnique)
            {
                var holder = reader.ReadMemory<IntPtr>(station + StationAnchorHolderOffset);
                if (holder != IntPtr.Zero)
                {
                    var p1 = reader.ReadMemory<IntPtr>(holder + 0x28);
                    if (p1 != IntPtr.Zero)
                    {
                        var tableBase = reader.ReadMemory<long>(p1);
                        if (tableBase != 0)
                        {
                            var delta = rowPtr.ToInt64() - tableBase;
                            if (delta >= 0)
                            {
                                if (delta % 0x68 == 0) anchorIdx = (int)(delta / 0x68);
                                else if (delta % 0x6C == 0) anchorIdx = (int)(delta / 0x6C);
                            }

                            if (anchorIdx < 0 || anchorIdx >= RuneNames.Length) anchorIdx = -1;
                        }
                    }
                }
            }

            var anchorName = isUnique ? "Unique Monolith" : (anchorIdx >= 0 && anchorIdx < RuneNames.Length ? RuneNames[anchorIdx] : "Rune Monolith");
            var isAnchorGolden = isUnique || (goldenSlot == anchorPos);

            details = new RuneStationDetails
            {
                SocketCount = socketCount,
                GoldenSlotIndex = goldenSlot,
                AnchorSlotIndex = anchorPos,
                AnchorRuneName = anchorName,
                IsUnique = isUnique,
                IsAnchorInGoldenSlot = isAnchorGolden
            };
            return true;
        }
    }

    /// <summary>
    ///     Authoritative Expedition RuneStation details resolved from live memory.
    /// </summary>
    public sealed class RuneStationDetails
    {
        public int SocketCount { get; init; }
        public int GoldenSlotIndex { get; init; } = -1;
        public int AnchorSlotIndex { get; init; } = -1;
        public string AnchorRuneName { get; init; } = string.Empty;
        public bool IsUnique { get; init; }
        public bool IsAnchorInGoldenSlot { get; init; }
        public bool Proliferates => this.IsUnique || this.IsAnchorInGoldenSlot;
    }

    public class StateMachineState(string name, long value)
    {
        public string Name { get; } = name;
        public long Value { get; } = value;

        public override string ToString() => $"{Name}: {Value}";
    }
}

