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
    using TEHhub.RemoteEnums;
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

            RenderMods("Implicit mods", item.ImplicitMods);
            RenderMods("Explicit mods", item.ExplicitMods);
            RenderMods("Enchant mods", item.EnchantMods);
            RenderMods("Other mods", item.OtherMods);
            RenderModStats(item.ModStats);

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
