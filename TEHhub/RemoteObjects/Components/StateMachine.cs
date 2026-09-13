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

                // Per-slot rune enumeration
                if (rs.SlotRunes != null && rs.SlotRunes.Length > 0)
                {
                    ImGui.TextColored(new System.Numerics.Vector4(0.6f, 0.9f, 1f, 1f), "Runes per slot:");
                    for (int si = 0; si < rs.SlotRunes.Length; si++)
                    {
                        var isGolden = si == rs.GoldenSlotIndex;
                        var marker = isGolden ? " ★" : "";
                        var col = isGolden
                            ? new System.Numerics.Vector4(1f, 0.84f, 0f, 1f)
                            : new System.Numerics.Vector4(0.8f, 0.8f, 0.8f, 1f);
                        ImGui.TextColored(col, $"  Slot #{si + 1}: {rs.SlotRunes[si]}{marker}");
                    }
                }
#if DEBUG
                else if (!rs.IsUnique)
                {
                    ImGui.TextColored(new System.Numerics.Vector4(1f, 0.6f, 0.2f, 1f), "Slot runes: (scan did not match)");
                    if (ImGui.TreeNode("Diagnostic: Station Vector Scan"))
                    {
                        if (ImGui.Button("Refresh station memory"))
                        {
                            this.stationVectorDiagnostic = this.CaptureStationVectorDiagnostic(rs.SocketCount);
                        }

                        ImGui.SameLine();
                        ImGui.TextDisabled("Reads once; it does not dereference candidate values.");
                        if (string.IsNullOrEmpty(this.stationVectorDiagnostic))
                        {
                            ImGui.TextDisabled("Press Refresh station memory while this station is open.");
                        }
                        else
                        {
                            ImGui.BeginChild("station-vector-results", new System.Numerics.Vector2(0, 260), ImGuiChildFlags.Borders);
                            ImGui.TextUnformatted(this.stationVectorDiagnostic);
                            ImGui.EndChild();
                        }

                        ImGui.TreePop();
                    }
                }
#endif
                ImGui.Separator();

            }

            ImGui.Text($"State Count: {States.Count}");
            for (int i = 0; i < States.Count; i++)
            {
                var state = States[i];
                ImGui.Text($"State[{i}]: Name='{state.Name}', Value={state.Value}");
            }
        }

#if DEBUG
        private string? stationVectorDiagnostic;

        /// <summary>
        /// Captures the bounded Rune Station diagnostic for the local Debug API. The capture is
        /// explicitly on-demand and is called from the render-thread diagnostic queue.
        /// </summary>
        internal string CaptureRuneStationDiagnostic()
        {
            return this.TryGetRuneStationDetails(out var details) && details != null
                ? this.CaptureStationVectorDiagnostic(details.SocketCount)
                : "This StateMachine does not resolve to a live Rune Station.";
        }

        private string CaptureStationVectorDiagnostic(int socketCount)
        {
            var lines = new List<string>();
            try
            {
                var reader = Core.Process.Handle;
                if (!reader.TryReadMemory<StdVector>(this.Address + ListenerVectorOffset, out var listeners))
                {
                    return "Could not read the listener vector.";
                }

                var listenerBytes = listeners.Last.ToInt64() - listeners.First.ToInt64();
                if (listeners.First == IntPtr.Zero || listenerBytes <= 0 || listenerBytes % IntPtr.Size != 0 || listenerBytes / IntPtr.Size > 256)
                {
                    return $"Listener vector is not usable: {listeners}.";
                }

                var nodes = new long[(int)(listenerBytes / IntPtr.Size)];
                if (!reader.TryReadMemoryArray(listeners.First, nodes, out _))
                {
                    return "Could not read listener nodes.";
                }

                IntPtr station = IntPtr.Zero;
                foreach (var nodeValue in nodes)
                {
                    if (nodeValue == 0 || !reader.TryReadMemory<IntPtr>(new IntPtr(nodeValue), out var listener) || listener == IntPtr.Zero)
                    {
                        continue;
                    }

                    var firstCandidate = listener - 0xA0;
                    if (reader.TryReadMemory<IntPtr>(firstCandidate + StationDeviceBackPtr, out var firstOwner) && firstOwner == this.OwnerEntityAddress)
                    {
                        station = firstCandidate;
                        break;
                    }

                    var secondCandidate = listener - StationFromListener;
                    if (reader.TryReadMemory<IntPtr>(secondCandidate + StationDeviceBackPtr, out var secondOwner) && secondOwner == this.OwnerEntityAddress)
                    {
                        station = secondCandidate;
                        break;
                    }
                }

                if (station == IntPtr.Zero)
                {
                    return "Could not resolve the station address from its listeners.";
                }

                lines.Add($"Station: 0x{station.ToInt64():X}; sockets: {socketCount}");

                var anchorRow = IntPtr.Zero;
                var anchorHolder = IntPtr.Zero;
                long tableBase = 0;
                var rowStride = 0;
                reader.TryReadMemory(station + StationAnchorRefOffset, out anchorRow, recordFailure: false);
                reader.TryReadMemory(station + StationAnchorHolderOffset, out anchorHolder, recordFailure: false);
                if (anchorHolder != IntPtr.Zero && reader.TryReadMemory<IntPtr>(anchorHolder + 0x28, out var tableHolder, recordFailure: false) &&
                    tableHolder != IntPtr.Zero && reader.TryReadMemory<long>(tableHolder, out tableBase, recordFailure: false) && tableBase != 0)
                {
                    var delta = anchorRow.ToInt64() - tableBase;
                    if (delta >= 0 && delta % 0x68 == 0 && delta / 0x68 < RuneNames.Length)
                    {
                        rowStride = 0x68;
                    }
                    else if (delta >= 0 && delta % 0x6C == 0 && delta / 0x6C < RuneNames.Length)
                    {
                        rowStride = 0x6C;
                    }
                }

                lines.Add($"Anchor row: 0x{anchorRow.ToInt64():X}; holder: 0x{anchorHolder.ToInt64():X}; Rune DAT: 0x{tableBase:X}; stride: {(rowStride > 0 ? $"0x{rowStride:X}" : "unknown")}");
                lines.Add($"Listener nodes: {nodes.Length} (showing up to 24)");
                var linkedOwnerAddresses = new HashSet<long>();
                var diagnosticRoots = new List<(string Name, IntPtr Address)> { ("Station", station), ("Anchor holder", anchorHolder) };
                for (var listenerIndex = 0; listenerIndex < nodes.Length && listenerIndex < 24; listenerIndex++)
                {
                    var nodeValue = nodes[listenerIndex];
                    if (nodeValue == 0 || !reader.TryReadMemory<IntPtr>(new IntPtr(nodeValue), out var listener, recordFailure: false) || listener == IntPtr.Zero)
                    {
                        lines.Add($"Listener[{listenerIndex}]: unreadable");
                        continue;
                    }

                    var candidateA0 = listener - 0xA0;
                    var candidate98 = listener - StationFromListener;
                    reader.TryReadMemory(candidateA0 + StationDeviceBackPtr, out IntPtr ownerA0, recordFailure: false);
                    reader.TryReadMemory(candidate98 + StationDeviceBackPtr, out IntPtr owner98, recordFailure: false);
                    var owned = ownerA0 == this.OwnerEntityAddress ? " (-0xA0 owns station)" : owner98 == this.OwnerEntityAddress ? " (-0x98 owns station)" : string.Empty;
                    lines.Add($"Listener[{listenerIndex}]: node=0x{nodeValue:X}; ptr=0x{listener.ToInt64():X}; owner@-0xA0=0x{ownerA0.ToInt64():X}; owner@-0x98=0x{owner98.ToInt64():X}{owned}");
                    if (ownerA0 != IntPtr.Zero && ownerA0 != this.OwnerEntityAddress)
                    {
                        linkedOwnerAddresses.Add(ownerA0.ToInt64());
                        diagnosticRoots.Add(($"Listener[{listenerIndex}]-0xA0", candidateA0));
                    }
                    if (owner98 != IntPtr.Zero && owner98 != this.OwnerEntityAddress && owner98.ToInt64() < 0x0000800000000000)
                    {
                        linkedOwnerAddresses.Add(owner98.ToInt64());
                        diagnosticRoots.Add(($"Listener[{listenerIndex}]-0x98", candidate98));
                    }
                }

                var linkedOwnerCount = 0;
                foreach (var ownerAddress in linkedOwnerAddresses)
                {
                    if (linkedOwnerCount++ >= 8) break;
                    try
                    {
                        var linkedEntity = new TEHhub.RemoteObjects.States.InGameStateObjects.Entity(new IntPtr(ownerAddress));
                        linkedEntity.RefreshDataNow();
                        if (linkedEntity.IsValid)
                        {
                            lines.Add($"Linked owner 0x{ownerAddress:X}: id={linkedEntity.Id}; path={linkedEntity.Path}; components={string.Join(", ", linkedEntity.GetComponentNames())}");
                        }
                        else
                        {
                            lines.Add($"Linked owner 0x{ownerAddress:X}: not a valid live Entity.");
                        }
                    }
                    catch (Exception ex)
                    {
                        lines.Add($"Linked owner 0x{ownerAddress:X}: entity read failed ({ex.GetType().Name}).");
                    }
                }

                if (rowStride > 0)
                {
                    var directRows = 0;
                    foreach (var target in diagnosticRoots)
                    {
                        if (target.Address == IntPtr.Zero)
                        {
                            continue;
                        }

                        for (var offset = 0; offset <= 0x400; offset += IntPtr.Size)
                        {
                            if (!reader.TryReadMemory<IntPtr>(target.Address + offset, out var possibleRow, recordFailure: false))
                            {
                                continue;
                            }

                            var rowDelta = possibleRow.ToInt64() - tableBase;
                            if (rowDelta < 0 || rowDelta % rowStride != 0)
                            {
                                continue;
                            }

                            var runeIndex = rowDelta / rowStride;
                            if (runeIndex < 0 || runeIndex >= RuneNames.Length)
                            {
                                continue;
                            }

                            lines.Add($"Rune ref: {target.Name}+0x{offset:X3} -> {RuneNames[runeIndex]} (index {runeIndex})");
                            directRows++;
                        }
                    }

                    if (directRows == 0)
                    {
                        lines.Add("No direct Rune DAT rows in Station/Anchor Holder first 0x400 bytes.");
                    }

                    var nestedRows = 0;
                    var inspectedPointers = 0;
                    foreach (var source in diagnosticRoots)
                    {
                        if (source.Address == IntPtr.Zero)
                        {
                            continue;
                        }

                        for (var sourceOffset = 0; sourceOffset <= 0x400 && inspectedPointers < 96; sourceOffset += IntPtr.Size)
                        {
                            if (!reader.TryReadMemory<IntPtr>(source.Address + sourceOffset, out var target, recordFailure: false) || target == IntPtr.Zero)
                            {
                                continue;
                            }

                            inspectedPointers++;
                            for (var targetOffset = 0; targetOffset <= 0x200; targetOffset += IntPtr.Size)
                            {
                                if (!reader.TryReadMemory<IntPtr>(target + targetOffset, out var possibleRow, recordFailure: false))
                                {
                                    continue;
                                }

                                var rowDelta = possibleRow.ToInt64() - tableBase;
                                if (rowDelta < 0 || rowDelta % rowStride != 0)
                                {
                                    continue;
                                }

                                var runeIndex = rowDelta / rowStride;
                                if (runeIndex < 0 || runeIndex >= RuneNames.Length)
                                {
                                    continue;
                                }

                                lines.Add($"Nested rune ref: {source.Name}+0x{sourceOffset:X3} -> 0x{target.ToInt64():X}+0x{targetOffset:X3} -> {RuneNames[runeIndex]} (index {runeIndex})");
                                nestedRows++;
                            }
                        }
                    }

                    if (nestedRows == 0)
                    {
                        lines.Add("No one-hop Rune DAT references from Station/Anchor Holder within the bounded scan.");
                    }
                }

                lines.Add("Only valid, non-empty vector headers are listed. Raw data is capped at 128 bytes.");
                var candidates = 0;
                for (var offset = 0; offset <= 0x200; offset += IntPtr.Size)
                {
                    if (!reader.TryReadMemory<StdVector>(station + offset, out var vector))
                    {
                        continue;
                    }

                    var first = vector.First.ToInt64();
                    var last = vector.Last.ToInt64();
                    var end = vector.End.ToInt64();
                    if (first == 0 || last < first || end < last)
                    {
                        continue;
                    }

                    var usedBytes = last - first;
                    var capacityBytes = end - first;
                    if (usedBytes <= 0 || usedBytes > 4096 || capacityBytes > 16384)
                    {
                        continue;
                    }

                    var strides = new List<string>();
                    foreach (var stride in new[] { 4, 8, 16, 24, 32 })
                    {
                        if (usedBytes % stride == 0)
                        {
                            strides.Add($"{stride}B×{usedBytes / stride}");
                        }
                    }

                    var previewLength = (int)Math.Min(128, usedBytes);
                    var preview = new byte[previewLength];
                    var readSucceeded = reader.TryReadMemoryArray(vector.First, preview, out var bytesRead);
                    var raw = readSucceeded
                        ? Convert.ToHexString(preview.AsSpan(0, Math.Min(previewLength, checked((int)bytesRead))))
                        : $"<payload read failed; {bytesRead} bytes>";
                    lines.Add($"0x{offset:X3}: First=0x{first:X} Last=0x{last:X} End=0x{end:X}; used={usedBytes}, capacity={capacityBytes}; {string.Join(", ", strides)}");
                    lines.Add($"  raw: {raw}");
                    candidates++;
                }

                if (candidates == 0)
                {
                    lines.Add("No valid non-empty vector headers in 0x000-0x200. This proves neither a missing station nor a missing rune; the slot layout is different.");
                }
            }
            catch (Exception ex)
            {
                lines.Add($"Diagnostic exception: {ex.GetType().Name}: {ex.Message}");
            }

            return string.Join(Environment.NewLine, lines);
        }
#endif
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
            long tableBase = 0;
            int rowStride = 0;

            if (!isUnique)
            {
                var holder = reader.ReadMemory<IntPtr>(station + StationAnchorHolderOffset);
                if (holder != IntPtr.Zero)
                {
                    var p1 = reader.ReadMemory<IntPtr>(holder + 0x28);
                    if (p1 != IntPtr.Zero)
                    {
                        tableBase = reader.ReadMemory<long>(p1);
                        if (tableBase != 0)
                        {
                            var delta = rowPtr.ToInt64() - tableBase;
                            if (delta >= 0)
                            {
                                if (delta % 0x68 == 0) { anchorIdx = (int)(delta / 0x68); rowStride = 0x68; }
                                else if (delta % 0x6C == 0) { anchorIdx = (int)(delta / 0x6C); rowStride = 0x6C; }
                            }

                            if (anchorIdx < 0 || anchorIdx >= RuneNames.Length) { anchorIdx = -1; rowStride = 0; }
                        }
                    }
                }
            }

            var anchorName = isUnique ? "Unique Monolith" : (anchorIdx >= 0 && anchorIdx < RuneNames.Length ? RuneNames[anchorIdx] : "Rune Monolith");
            var isAnchorGolden = isUnique || (goldenSlot == anchorPos);

            // --- Per-slot rune discovery ---
            // Scan station offsets after the golden-slots vector (0x40 + 24 = 0x58) looking for
            // a std::vector<IntPtr> with exactly socketCount elements, each resolvable to a valid
            // rune index via the same dat table base used for the anchor rune.
            string[]? slotRunes = null;
            if (!isUnique && socketCount > 0 && socketCount <= 16 && tableBase != 0)
            {
                // Try multiple rowStride values if the anchor resolution used a specific one;
                // also include common dat row sizes observed across game patches.
                var strides = rowStride > 0
                    ? new[] { rowStride, 0x60, 0x64, 0x68, 0x6C, 0x70, 0x78, 0x80 }
                    : new[] { 0x60, 0x64, 0x68, 0x6C, 0x70, 0x78, 0x80 };

                // Scan a wider range of offsets from the station struct (0x58 through 0x120).
                foreach (var scanOffset in new[]
                {
                    0x58, 0x60, 0x68, 0x70, 0x78, 0x80,
                    0x88, 0x90, 0x98, 0xA0, 0xA8, 0xB0,
                    0xB8, 0xC0, 0xC8, 0xD0, 0xD8, 0xE0,
                    0xE8, 0xF0, 0xF8, 0x100, 0x108, 0x110, 0x118, 0x120
                })
                {
                    if (slotRunes != null) break;
                    try
                    {
                        var runeVec = reader.ReadMemory<StdVector>(station + scanOffset);
                        var runeCount = runeVec.TotalElements(sizeof(long)); // IntPtr is 8 bytes
                        if (runeCount != socketCount || runeCount <= 0)
                            continue;

                        var runePtrs = reader.ReadMemoryArray<long>(runeVec.First, (int)runeCount);
                        if (runePtrs == null || runePtrs.Length != socketCount)
                            continue;

                        // Try each candidate row stride against the dat table base.
                        foreach (var tryStride in strides)
                        {
                            var names = new string[socketCount];
                            bool allValid = true;
                            for (int ri = 0; ri < socketCount; ri++)
                            {
                                if (runePtrs[ri] == 0) { allValid = false; break; }
                                var d = runePtrs[ri] - tableBase;
                                if (d < 0 || d % tryStride != 0) { allValid = false; break; }
                                int idx = (int)(d / tryStride);
                                if (idx < 0 || idx >= RuneNames.Length) { allValid = false; break; }
                                names[ri] = RuneNames[idx];
                            }

                            if (allValid)
                            {
                                slotRunes = names;
                                break;
                            }
                        }
                    }
                    catch
                    {
                        // Ignore probe failures at this offset
                    }
                }
            }

            details = new RuneStationDetails
            {
                SocketCount = socketCount,
                GoldenSlotIndex = goldenSlot,
                AnchorSlotIndex = anchorPos,
                AnchorRuneName = anchorName,
                IsUnique = isUnique,
                IsAnchorInGoldenSlot = isAnchorGolden,
                SlotRunes = slotRunes
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

        /// <summary>
        ///     Per-slot rune names resolved via heuristic memory scanning.
        ///     Null when the per-slot vector was not discovered or the monolith is unique.
        /// </summary>
        public string[]? SlotRunes { get; init; }

        /// <summary>
        ///     Formatted summary of runes per slot for compact display.
        ///     Example: "[1]Fire★ [2]Bond [3]Soul [4]Power"
        /// </summary>
        public string SlotRunesSummary
        {
            get
            {
                if (SlotRunes == null || SlotRunes.Length == 0)
                    return string.Empty;

                var parts = new string[SlotRunes.Length];
                for (int i = 0; i < SlotRunes.Length; i++)
                {
                    var golden = i == GoldenSlotIndex ? "★" : "";
                    parts[i] = $"[{i + 1}]{SlotRunes[i]}{golden}";
                }
                return string.Join(" ", parts);
            }
        }
    }

    public class StateMachineState(string name, long value)
    {
        public string Name { get; } = name;
        public long Value { get; } = value;

        public override string ToString() => $"{Name}: {Value}";
    }
}

