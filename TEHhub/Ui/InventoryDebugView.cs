// <copyright file="InventoryDebugView.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace TEHhub.Ui
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Numerics;
    using ImGuiNET;
    using TEHhub.Offsets.Natives;
    using TEHhub.Offsets.Objects.Components;
    using TEHhub.Offsets.Shared;
    using TEHhub.RemoteEnums;
    using TEHhub.RemoteObjects.Components;
    using TEHhub.RemoteObjects.States.InGameStateObjects;
    using TEHhub.Utils;

    /// <summary>
    ///     Dedicated DV inspector for the live ServerData inventory SDK.
    /// </summary>
    internal static class InventoryDebugView
    {
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(250);
        private static InventoryName selectedInventory = InventoryName.MainInventory1;
        private static InventorySnapshot? snapshot;
        private static DateTime nextRefreshUtc = DateTime.MinValue;
        private static IntPtr selectedItemAddress = IntPtr.Zero;
        private static bool forceFullRefresh = true;
        private static bool autoRefreshChanges;

        internal static void Render()
        {
            var serverData = Core.States.InGameStateObject?.CurrentAreaInstance?.ServerDataObject;
            if (serverData == null || serverData.Address == IntPtr.Zero)
            {
                ImGui.TextColored(
                    new Vector4(1f, 0.6f, 0.2f, 1f),
                    "ServerData is unavailable. Enter the game before inspecting Inventory.");
                snapshot = null;
                selectedItemAddress = IntPtr.Zero;
                return;
            }

            var inventories = serverData.GetAvailableInventoryNames();
            if (inventories.Count == 0)
            {
                ImGui.TextDisabled("PlayerInventories has no materialized inventory entries.");
                snapshot = null;
                selectedItemAddress = IntPtr.Zero;
                return;
            }

            if (!inventories.Contains(selectedInventory))
            {
                selectedInventory = inventories.Contains(InventoryName.MainInventory1)
                    ? InventoryName.MainInventory1
                    : inventories[0];
                Invalidate();
            }

            RenderInventorySelector(inventories);
            ImGui.SameLine();
            if (ImGui.SmallButton("Refresh now##InventoryDvRefresh"))
            {
                nextRefreshUtc = DateTime.MinValue;
                forceFullRefresh = true;
            }

            ImGui.SameLine();
            ImGui.Checkbox("Auto refresh changes##InventoryDvAutoRefresh", ref autoRefreshChanges);

            var now = DateTime.UtcNow;
            if (snapshot == null ||
                snapshot.Name != selectedInventory ||
                forceFullRefresh ||
                (autoRefreshChanges && now >= nextRefreshUtc))
            {
                snapshot = serverData.ReadInventorySnapshot(
                    selectedInventory,
                    InventorySnapshotDetailLevel.Full,
                    forceRefresh: forceFullRefresh);
                forceFullRefresh = false;
                nextRefreshUtc = now + RefreshInterval;
            }

            RenderSnapshot(snapshot);
        }

        private static void RenderInventorySelector(IReadOnlyList<InventoryName> inventories)
        {
            ImGui.SetNextItemWidth(260f);
            if (!ImGui.BeginCombo("Inventory##InventoryDvSelector", selectedInventory.ToString()))
            {
                return;
            }

            foreach (var inventory in inventories)
            {
                var selected = inventory == selectedInventory;
                if (ImGui.Selectable(inventory.ToString(), selected))
                {
                    selectedInventory = inventory;
                    Invalidate();
                }

                if (selected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        private static void RenderSnapshot(InventorySnapshot current)
        {
            ImGui.Text($"State: {current.State} | Detail: {current.DetailLevel}");
            ImGui.Text($"Grid: {current.Columns} x {current.Rows} | Items: {current.Items.Count} | Slots: {current.Slots.Count}");
            ImGui.Text($"Server requests: {current.ServerRequestCounter} | Revision: 0x{current.Revision:X16}");
            ImGui.Text($"Source revision: 0x{current.SourceRevision:X16} (unchanged revisions reuse the cached snapshot)");
            if (!autoRefreshChanges)
            {
                ImGui.TextDisabled("One-shot view: click Refresh now after inventory or item data changes.");
            }
            ImGuiHelper.IntPtrToImGui("Inventory address", current.Address);
            ImGui.TextWrapped($"Diagnostic: {current.Diagnostic}");

            if (current.State != InventorySnapshotState.Ready)
            {
                ImGui.TextColored(
                    new Vector4(1f, 0.6f, 0.2f, 1f),
                    "Inventory is not coherent yet. DV will retry without treating it as empty.");
                return;
            }

            ImGui.SeparatorText("Slots");
            RenderGrid(current);
            ImGui.SeparatorText("Selected item");
            RenderSelectedItem(current);
        }

        private static void RenderGrid(InventorySnapshot current)
        {
            if (current.Columns <= 0 || current.Rows <= 0 || current.Slots.Count == 0)
            {
                ImGui.TextDisabled("No physical slot grid is available for this inventory.");
                return;
            }

            var itemsByAddress = current.Items
                .GroupBy(item => item.ItemAddress)
                .ToDictionary(group => group.Key, group => group.First());
            var slotsByPosition = current.Slots.ToDictionary(slot => (slot.X, slot.Y));
            var flags = ImGuiTableFlags.Borders |
                        ImGuiTableFlags.ScrollX |
                        ImGuiTableFlags.ScrollY |
                        ImGuiTableFlags.SizingFixedFit;
            var height = Math.Min(340f, 45f + (current.Rows * 54f));
            if (!ImGui.BeginTable(
                    "##InventoryDvGrid",
                    current.Columns + 1,
                    flags,
                    new Vector2(0f, height)))
            {
                return;
            }

            ImGui.TableSetupScrollFreeze(1, 1);
            ImGui.TableSetupColumn("Y/X", ImGuiTableColumnFlags.WidthFixed, 38f);
            for (var x = 0; x < current.Columns; x++)
            {
                ImGui.TableSetupColumn($"{x}", ImGuiTableColumnFlags.WidthFixed, 68f);
            }

            ImGui.TableHeadersRow();
            for (var y = 0; y < current.Rows; y++)
            {
                ImGui.TableNextRow(ImGuiTableRowFlags.None, 50f);
                ImGui.TableNextColumn();
                ImGui.TextDisabled($"{y}");
                for (var x = 0; x < current.Columns; x++)
                {
                    ImGui.TableNextColumn();
                    slotsByPosition.TryGetValue((x, y), out var slot);
                    InventorySnapshotItem? item = null;
                    if (slot != null && slot.ItemAddress != IntPtr.Zero)
                    {
                        itemsByAddress.TryGetValue(slot.ItemAddress, out item);
                    }

                    RenderSlot(x, y, item);
                }
            }

            ImGui.EndTable();
        }

        private static void RenderSlot(
            int x,
            int y,
            InventorySnapshotItem? item)
        {
            if (item == null)
            {
                ImGui.TextDisabled("empty");
                return;
            }

            var isOrigin = item.SlotStartX == x && item.SlotStartY == y;
            var rarityText = item.Rarity?.ToString() ?? "Unknown";
            var displayName = string.IsNullOrWhiteSpace(item.BaseItemName)
                ? LastPathSegment(item.Path)
                : item.BaseItemName;
            var shortName = displayName.Length <= 9 ? displayName : $"{displayName[..8]}…";
            var label = isOrigin
                ? $"{shortName}\n{rarityText}##inv_{x}_{y}"
                : $"same item\n({item.SlotStartX},{item.SlotStartY})##inv_{x}_{y}";

            ImGui.PushStyleColor(ImGuiCol.Text, RarityColor(item.Rarity));
            var selected = selectedItemAddress == item.ItemAddress;
            if (ImGui.Selectable(
                    label,
                    selected,
                    ImGuiSelectableFlags.None,
                    new Vector2(64f, 44f)))
            {
                selectedItemAddress = item.ItemAddress;
            }

            ImGui.PopStyleColor();
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip($"[{x},{y}] {displayName}\n{rarityText}\n{item.Path}\nItem 0x{item.ItemAddress.ToInt64():X}");
            }
        }

        private static void RenderSelectedItem(InventorySnapshot current)
        {
            if (selectedItemAddress == IntPtr.Zero)
            {
                ImGui.TextDisabled("Click an occupied slot to inspect its Item.");
                return;
            }

            var item = current.Items.FirstOrDefault(candidate => candidate.ItemAddress == selectedItemAddress);
            if (item == null)
            {
                selectedItemAddress = IntPtr.Zero;
                ImGui.TextDisabled("The selected item is no longer in this inventory.");
                return;
            }

            ImGui.TextWrapped($"Name: {(string.IsNullOrWhiteSpace(item.BaseItemName) ? "(unavailable)" : item.BaseItemName)}");
            ImGui.Text($"Rarity: {item.Rarity?.ToString() ?? "Unknown"}");
            ImGui.Text(item.WaystoneTier is int tier
                ? $"Classification: Waystone Tier {tier}"
                : "Classification: non-Waystone item");
            ImGui.Text($"Modifier rows: {item.ImplicitMods.Count + item.ExplicitMods.Count + item.EnchantMods.Count + item.OtherMods.Count} | Modifier stats: {item.ModStats.Count}");
            ImGui.TextDisabled("Identification state: unknown (no validated SDK field yet)");
            ImGui.Text($"Slot: ({item.SlotStartX},{item.SlotStartY}) -> ({item.SlotEndX},{item.SlotEndY}) | Size: {item.Width} x {item.Height}");
            ImGuiHelper.IntPtrToImGui("Item address", item.ItemAddress);
            ImGuiHelper.IntPtrToImGui("Wrapper address", item.WrapperAddress);
            ImGuiHelper.DisplayTextAndCopyOnClick($"Path: {item.Path}", item.Path);
            ImGuiHelper.DisplayTextAndCopyOnClick(
                $"Internal name: {(string.IsNullOrWhiteSpace(item.InternalName) ? "(unavailable)" : item.InternalName)}",
                item.InternalName);

            if (item.StackCount.HasValue)
            {
                ImGui.Text($"Stack: {item.StackCount}/{item.MaxStack} | Specialized tab max: {item.MaxStackTab}");
            }

            if (item.CurrentCharges.HasValue)
            {
                ImGui.Text($"Charges: {item.CurrentCharges} | Per use: {item.ChargesPerUse}");
            }

            if (!string.IsNullOrWhiteSpace(item.DetailDiagnostic))
            {
                ImGui.TextColored(new Vector4(1f, 0.6f, 0.2f, 1f), $"Partial detail read: {item.DetailDiagnostic}");
            }

            if (ImGui.TreeNode($"Components ({item.ComponentNames.Count})##InventoryDvComponents"))
            {
                foreach (var componentName in item.ComponentNames)
                {
                    ImGui.BulletText(componentName);
                }

                ImGui.TreePop();
            }

            if (item.IsWaystone)
            {
                RenderWaystoneStatsSummary(item);
            }

            RenderMods("Implicit mods", item.ImplicitMods);
            RenderMods("Explicit mods", item.ExplicitMods);
            RenderMods("Enchant mods", item.EnchantMods);
            RenderMods("Other mods", item.OtherMods);
            RenderModStats(item.ModStats);
            RenderImplicitModsProbe(item);

            if (ImGui.TreeNode("Raw Item SDK tree##InventoryDvRawItem"))
            {
                item.Item.ToImGui();
                ImGui.TreePop();
            }
        }

        private static void RenderMods(string label, IReadOnlyList<InventorySnapshotMod> mods)
        {
            if (!ImGui.TreeNode($"{label} ({mods.Count})##InventoryDv{label}"))
            {
                return;
            }

            foreach (var mod in mods)
            {
                var values = float.IsNaN(mod.Value0)
                    ? string.Empty
                    : float.IsNaN(mod.Value1)
                        ? $": {mod.Value0}"
                        : $": {mod.Value0}, {mod.Value1}";
                ImGuiHelper.DisplayTextAndCopyOnClick($"{mod.Name}{values}", mod.Name);
            }

            ImGui.TreePop();
        }

        private static void RenderModStats(IReadOnlyDictionary<GameStats, int> stats)
        {
            if (!ImGui.TreeNode($"Stats from modifiers ({stats.Count})##InventoryDvModStats"))
            {
                return;
            }

            foreach (var stat in stats.OrderBy(entry => entry.Key.ToString(), StringComparer.Ordinal))
            {
                var name = stat.Key.ToString();
                ImGuiHelper.DisplayTextAndCopyOnClick($"{name}: {stat.Value}", name);
            }

            ImGui.TreePop();
        }

        /// <summary>
        ///     Renders a human-readable summary of known Waystone stats extracted from ModStats.
        ///     This tests whether StatsFromMods (offset 0x148) contains the Waystone values.
        /// </summary>
        private static void RenderWaystoneStatsSummary(InventorySnapshotItem item)
        {
            if (!ImGui.TreeNode("Waystone stats (from ModStats)##InventoryDvWaystoneStats"))
            {
                return;
            }

            var stats = item.ModStats;
            if (stats.Count == 0)
            {
                ImGui.TextColored(
                    new Vector4(1f, 0.6f, 0.2f, 1f),
                    "ModStats is empty. StatsFromMods (0x148) may be unreadable or the offset has shifted.");
                ImGui.TreePop();
                return;
            }

            // Well-known Waystone stat mappings (GameStats enum -> display name)
            (GameStats stat, string label)[] waystoneStatMappings =
            {
                (GameStats.map_item_drop_rarity_positive_percentage, "Item Rarity"),
                (GameStats.map_pack_size_positive_percentage, "Pack Size"),
                (GameStats.map_monster_potency_positive_percentage, "Monster Effectiveness"),
                (GameStats.map_map_item_drop_chance_positive_percentage, "Waystone Drop Chance"),
                (GameStats.map_item_drop_quantity_positive_percentage, "Item Quantity"),
                (GameStats.map_item_drop_rarity_positive_percentage_final_from_map, "Item Rarity (final)"),
                (GameStats.map_pack_size_positive_percentage_final_from_map, "Pack Size (final)"),
                (GameStats.map_map_item_drop_chance_positive_percentage_final_from_map, "Waystone Drop Chance (final)"),
                (GameStats.map_monster_potency_positive_percentage_final_from_map, "Monster Effectiveness (final)"),
            };

            var foundAny = false;
            foreach (var (stat, label) in waystoneStatMappings)
            {
                if (stats.TryGetValue(stat, out var value))
                {
                    ImGui.Text($"{label}: +{value}%");
                    foundAny = true;
                }
            }

            if (!foundAny)
            {
                ImGui.TextColored(
                    new Vector4(1f, 0.6f, 0.2f, 1f),
                    "No known Waystone stats found in ModStats. Values may be under different stat IDs.");
            }

            // Show all remaining ModStats not already rendered above for discovery
            var knownKeys = new HashSet<GameStats>();
            foreach (var (stat, _) in waystoneStatMappings)
            {
                knownKeys.Add(stat);
            }

            var unknownStats = stats
                .Where(entry => !knownKeys.Contains(entry.Key))
                .OrderBy(entry => (int)entry.Key)
                .ToList();
            if (unknownStats.Count > 0 && ImGui.TreeNode($"Other ModStats ({unknownStats.Count})##InventoryDvOtherModStats"))
            {
                foreach (var entry in unknownStats)
                {
                    var name = entry.Key.ToString();
                    ImGuiHelper.DisplayTextAndCopyOnClick($"{name} ({(int)entry.Key}): {entry.Value}", name);
                }

                ImGui.TreePop();
            }

            ImGui.TreePop();
        }

        /// <summary>
        ///     Probes the Mods component memory around offset 0x60-0x180 looking for valid
        ///     StdVector entries that could be the real ImplicitMods vector.
        /// </summary>
        private static void RenderImplicitModsProbe(InventorySnapshotItem item)
        {
            if (!ImGui.TreeNode("ImplicitMods offset probe##InventoryDvImplicitProbe"))
            {
                return;
            }

            IntPtr modsComponentAddress;
            try
            {
                if (!item.Item.TryGetComponent<Mods>(out var mods))
                {
                    ImGui.TextDisabled("Item has no Mods component.");
                    ImGui.TreePop();
                    return;
                }

                modsComponentAddress = mods.Address;
            }
            catch
            {
                ImGui.TextDisabled("Could not resolve Mods component address.");
                ImGui.TreePop();
                return;
            }

            if (modsComponentAddress == IntPtr.Zero)
            {
                ImGui.TextDisabled("Mods component address is zero.");
                ImGui.TreePop();
                return;
            }

            ImGui.TextDisabled($"Mods component @ 0x{modsComponentAddress.ToInt64():X}");
            ImGui.TextDisabled("Scanning offsets 0x60-0x180 for valid StdVector<ModArrayStruct>...");

            var reader = Core.Process?.Handle;
            if (reader == null)
            {
                ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "Process handle is unavailable.");
                ImGui.TreePop();
                return;
            }

            // Known offsets that are already mapped (skip labeling them as candidates)
            // AllModsType at 0xA0: Implicit=0xA0, Explicit=0xB8, Enchant=0xD0, Hellscape=0xE8, Crucible=0x100
            var knownOffsets = new HashSet<int> { 0xA0, 0xB8, 0xD0, 0xE8, 0x100 };
            var probeResults = new List<(int offset, string label, int modCount, string firstMod)>();

            for (var offset = 0x60; offset <= 0x180; offset += 0x08)
            {
                var vectorAddress = IntPtr.Add(modsComponentAddress, offset);
                if (!reader.TryReadMemory<StdVector>(vectorAddress, out var vector))
                {
                    continue;
                }

                if (!CanonicalStructuralInvariants.IsCanonicalPointer(vector.First) ||
                    !CanonicalStructuralInvariants.IsCanonicalPointer(vector.Last))
                {
                    continue;
                }

                var elementSize = System.Runtime.InteropServices.Marshal.SizeOf<ModArrayStruct>();
                var byteLength = vector.Last.ToInt64() - vector.First.ToInt64();
                if (byteLength <= 0 || byteLength % elementSize != 0 || byteLength > 50000)
                {
                    continue;
                }

                var count = (int)(byteLength / elementSize);
                if (count is < 1 or > 200)
                {
                    continue;
                }

                // Try reading the mod array
                ModArrayStruct[] modArray;
                try
                {
                    modArray = reader.ReadStdVector<ModArrayStruct>(vector);
                }
                catch
                {
                    continue;
                }

                if (modArray.Length == 0)
                {
                    continue;
                }

                // Validate: the first entry should have a readable mod name
                var firstModName = "(unreadable)";
                try
                {
                    if (modArray[0].ModsPtr != IntPtr.Zero &&
                        CanonicalStructuralInvariants.IsCanonicalPointer(modArray[0].ModsPtr))
                    {
                        firstModName = ObjectMagicProperties.GetModName(modArray[0].ModsPtr);
                        if (string.IsNullOrWhiteSpace(firstModName))
                        {
                            continue;
                        }
                    }
                    else
                    {
                        continue;
                    }
                }
                catch
                {
                    continue;
                }

                var label = knownOffsets.Contains(offset)
                    ? offset switch
                    {
                        0xA0 => "[CURRENT ImplicitMods]",
                        0xB8 => "[CURRENT ExplicitMods]",
                        0xD0 => "[CURRENT EnchantMods]",
                        0xE8 => "[CURRENT HellscapeMods]",
                        0x100 => "[CURRENT CrucibleMods]",
                        _ => "[CURRENT]",
                    }
                    : "[CANDIDATE]";

                probeResults.Add((offset, label, modArray.Length, firstModName));
            }

            if (probeResults.Count == 0)
            {
                ImGui.TextColored(
                    new Vector4(1f, 0.6f, 0.2f, 1f),
                    "No valid StdVector<ModArrayStruct> found in scan range.");
            }
            else
            {
                foreach (var (offset, label, modCount, firstMod) in probeResults)
                {
                    var color = label.Contains("CANDIDATE")
                        ? new Vector4(0.2f, 1f, 0.4f, 1f)
                        : new Vector4(0.7f, 0.7f, 0.7f, 1f);
                    ImGui.TextColored(color, $"0x{offset:X3} {label} -- {modCount} mod(s), first: {firstMod}");
                }
            }

            ImGui.TreePop();
        }

        private static Vector4 RarityColor(Rarity? rarity) => rarity switch
        {
            Rarity.Normal => new Vector4(0.85f, 0.85f, 0.85f, 1f),
            Rarity.Magic => new Vector4(0.45f, 0.55f, 1f, 1f),
            Rarity.Rare => new Vector4(1f, 0.85f, 0.25f, 1f),
            Rarity.Unique => new Vector4(0.85f, 0.5f, 0.2f, 1f),
            _ => new Vector4(1f, 0.45f, 0.45f, 1f),
        };

        private static string LastPathSegment(string path)
        {
            var separator = path.LastIndexOf('/');
            return separator >= 0 && separator + 1 < path.Length ? path[(separator + 1)..] : path;
        }

        private static void Invalidate()
        {
            snapshot = null;
            selectedItemAddress = IntPtr.Zero;
            nextRefreshUtc = DateTime.MinValue;
            forceFullRefresh = true;
        }
    }
}
