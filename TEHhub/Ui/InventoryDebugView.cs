// <copyright file="InventoryDebugView.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace TEHhub.Ui
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Numerics;
    using System.Text.Json;
    using ImGuiNET;
    using TEHhub.Offsets.Objects.States.InGameState;
    using TEHhub.Offsets.Objects.UiElement;
    using TEHhub.RemoteEnums;
    using TEHhub.RemoteObjects.Components;
    using TEHhub.RemoteObjects.States.InGameStateObjects;
    using TEHhub.RemoteObjects.UiElement;
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
        private static string corruptionProbeLabel = "before";
        private static string corruptionProbeStatus = "No corruption probe captured yet.";
        private static string corruptionProbePath = string.Empty;

        // Capture a bounded, uninterpreted slice that includes the existing rarity probe and
        // Mods vectors. Do not decode new offsets from these bytes until a before/after pair is
        // reviewed. The click handler reads this range once for the currently selected item.
        private const int CorruptionProbeOffset = 0x80;
        private const int CorruptionProbeLength = 0xE0;
        // Raw item-root prefix includes the typed ItemStruct (+0x00..+0x27) and known
        // EntityOffsets identity/status fields at +0x88/+0x8C. Keep it uninterpreted.
        private const int CorruptionProbeItemRootLength = 0x90;
        private const int CorruptionProbeComponentPrefixLength = 0x100;
        private const int CorruptionProbeMaxComponents = 50;

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
            var displayName = string.IsNullOrWhiteSpace(item.BaseItemName)
                ? LastPathSegment(item.Path)
                : item.BaseItemName;
            var shortName = displayName.Length <= 9 ? displayName : $"{displayName[..8]}…";
            var label = isOrigin
                ? $"{shortName}##inv_{x}_{y}"
                : $"same item\n({item.SlotStartX},{item.SlotStartY})##inv_{x}_{y}";

            var selected = selectedItemAddress == item.ItemAddress;
            if (ImGui.Selectable(
                    label,
                    selected,
                    ImGuiSelectableFlags.None,
                    new Vector2(64f, 44f)))
            {
                selectedItemAddress = item.ItemAddress;
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip($"[{x},{y}] {displayName}\n{item.Path}\nItem 0x{item.ItemAddress.ToInt64():X}");
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
            ImGui.Text($"Category: {CategoryLabel(item)}");
            ImGui.Text($"Rarity: {RarityDetail(item)}");
            ImGui.Text($"Modifier rows: {item.ImplicitMods.Count + item.ExplicitMods.Count + item.EnchantMods.Count + item.OtherMods.Count} | Modifier stats: {item.ModStats.Count}");
            ImGui.TextDisabled("Identification state: unknown (capture probe before/after using Wisdom)");
            ImGuiHelper.DisplayTextAndCopyOnClick(
                $"Identification probe [Mods+0x90..0x9F]: {item.IdentificationStateProbe}",
                item.IdentificationStateProbe);
            ImGui.Text($"Slot: ({item.SlotStartX},{item.SlotStartY}) -> ({item.SlotEndX},{item.SlotEndY}) | Size: {item.Width} x {item.Height}");
            ImGuiHelper.IntPtrToImGui("Item address", item.ItemAddress);
            ImGuiHelper.IntPtrToImGui("Wrapper address", item.WrapperAddress);
            ImGuiHelper.DisplayTextAndCopyOnClick($"Path: {item.Path}", item.Path);
            ImGuiHelper.DisplayTextAndCopyOnClick(
                $"Internal name: {(string.IsNullOrWhiteSpace(item.InternalName) ? "(unavailable)" : item.InternalName)}",
                item.InternalName);

            if (item.IsWaystone)
            {
                RenderCorruptionProbe(item, current);
            }

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
                RenderDisplayMods("Map modifiers", item.ExplicitModsDisplay);
            }

            var displayedImplicitMods = item.IsWaystone && item.WaystoneImplicitMods.Count > 0
                ? item.WaystoneImplicitMods
                : item.ImplicitMods;
            RenderMods("Implicit mods", displayedImplicitMods);
            if (item.IsWaystone && item.ImplicitMods.Count > 0)
            {
                RenderMods("Raw implicit mods", item.ImplicitMods);
            }

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

        private static void RenderCorruptionProbe(InventorySnapshotItem item, InventorySnapshot current)
        {
            ImGui.SeparatorText("Waystone corruption probe (manual one-shot)");
            ImGui.TextWrapped(
                "Capture before corruption, corrupt this same Waystone, click Refresh now, then capture again. " +
                "Label the two files before/after and send both for comparison. Current game mouse-over evidence is sampled opportunistically and may be empty while clicking this button. " +
                "Raw component prefixes are diagnostic only; this does not interpret them as flags or decide eligibility.");
            ImGui.SetNextItemWidth(180f);
            ImGui.InputText("Sample label##InventoryDvCorruptionLabel", ref corruptionProbeLabel, 48);
            ImGui.SameLine();
            if (ImGui.Button("Capture selected Waystone##InventoryDvCorruptionCapture"))
            {
                CaptureCorruptionProbe(item, current);
            }

            ImGui.TextWrapped(corruptionProbeStatus);
            if (!string.IsNullOrWhiteSpace(corruptionProbePath))
            {
                ImGuiHelper.DisplayTextAndCopyOnClick(
                    $"Capture file: {corruptionProbePath}",
                    corruptionProbePath);
            }
        }

        private static void CaptureCorruptionProbe(InventorySnapshotItem item, InventorySnapshot current)
        {
            var reader = Core.Process?.Handle;
            if (reader == null || reader.IsInvalid)
            {
                corruptionProbeStatus = "Capture failed: process memory reader is unavailable.";
                corruptionProbePath = string.Empty;
                return;
            }

            try
            {
                if (!item.Item.TryGetComponent<Mods>(out var mods) || mods == null || mods.Address == IntPtr.Zero)
                {
                    corruptionProbeStatus = "Capture failed: selected item has no readable Mods component.";
                    corruptionProbePath = string.Empty;
                    return;
                }

                var rawBytes = new byte[CorruptionProbeLength];
                var rawReadSucceeded = reader.TryReadMemoryArray(
                    mods.Address + CorruptionProbeOffset,
                    rawBytes,
                    out var bytesRead);
                var itemStructReadSucceeded = reader.TryReadMemory<ItemStruct>(item.ItemAddress, out var itemStruct);
                var itemStructBytes = new byte[CorruptionProbeItemRootLength];
                var itemStructBytesReadSucceeded = reader.TryReadMemoryArray(
                    item.ItemAddress,
                    itemStructBytes,
                    out var itemStructBytesRead);
                var componentSnapshots = CaptureComponentSnapshots(item.Item, reader);
                var visibleItemControl = CaptureVisibleItemControl(item.ItemAddress);
                var mouseOverEntity = CaptureMouseOverEntity(item.ItemAddress);
                var corruptedStatPresent = item.ModStats.TryGetValue(
                    GameStats.map_is_corrupted_waystone,
                    out var corruptedStatValue);
                var stats = item.ModStats
                    .OrderBy(entry => entry.Key.ToString(), StringComparer.Ordinal)
                    .Select(entry => new CorruptionProbeStat(
                        (int)entry.Key,
                        entry.Key.ToString(),
                        entry.Value))
                    .ToArray();

                var capturedAtUtc = DateTime.UtcNow;
                var capture = new
                {
                    schemaVersion = 2,
                    capturedAtUtc,
                    label = string.IsNullOrWhiteSpace(corruptionProbeLabel)
                        ? "unspecified"
                        : corruptionProbeLabel.Trim(),
                    inventory = new
                    {
                        name = selectedInventory.ToString(),
                        address = FormatAddress(current.Address),
                        revision = $"0x{current.Revision:X16}",
                        sourceRevision = $"0x{current.SourceRevision:X16}",
                    },
                    selectedSlot = new
                    {
                        startX = item.SlotStartX,
                        startY = item.SlotStartY,
                        endX = item.SlotEndX,
                        endY = item.SlotEndY,
                    },
                    item = new
                    {
                        path = item.Path,
                        baseItemName = item.BaseItemName,
                        internalName = item.InternalName,
                        category = item.Category.ToString(),
                        waystoneTier = item.WaystoneTier,
                        itemAddress = FormatAddress(item.ItemAddress),
                        wrapperAddress = FormatAddress(item.WrapperAddress),
                        rarityFromSnapshot = item.Rarity?.ToString(),
                    },
                    itemStruct = new
                    {
                        address = FormatAddress(item.ItemAddress),
                        readSucceeded = itemStructReadSucceeded,
                        vTableAddress = itemStructReadSucceeded ? FormatAddress(itemStruct.VTablePtr) : string.Empty,
                        entityDetailsAddress = itemStructReadSucceeded ? FormatAddress(itemStruct.EntityDetailsPtr) : string.Empty,
                        componentListBegin = itemStructReadSucceeded ? FormatAddress(itemStruct.ComponentListPtr.First) : string.Empty,
                        componentListEnd = itemStructReadSucceeded ? FormatAddress(itemStruct.ComponentListPtr.Last) : string.Empty,
                        componentListStorageEnd = itemStructReadSucceeded ? FormatAddress(itemStruct.ComponentListPtr.End) : string.Empty,
                        rawBytes = new
                        {
                            offsetFromItem = "0x0",
                            length = CorruptionProbeItemRootLength,
                            readSucceeded = itemStructBytesReadSucceeded,
                            bytesRead = (ulong)itemStructBytesRead,
                            hex = itemStructBytesReadSucceeded ? Convert.ToHexString(itemStructBytes) : string.Empty,
                        },
                    },
                    componentSnapshots,
                    visibleItemControl,
                    mouseOverEntity,
                    modsComponent = new
                    {
                        address = FormatAddress(mods.Address),
                        rarity = mods.Rarity.ToString(),
                        rawBytes = new
                        {
                            offsetFromComponent = $"0x{CorruptionProbeOffset:X}",
                            length = CorruptionProbeLength,
                            readSucceeded = rawReadSucceeded,
                            bytesRead = (ulong)bytesRead,
                            hex = rawReadSucceeded ? Convert.ToHexString(rawBytes) : string.Empty,
                        },
                        modifiers = new
                        {
                            implicitMods = item.ImplicitMods.Select(CaptureModifier).ToArray(),
                            explicitMods = item.ExplicitMods.Select(CaptureModifier).ToArray(),
                            enchantMods = item.EnchantMods.Select(CaptureModifier).ToArray(),
                            otherMods = item.OtherMods.Select(CaptureModifier).ToArray(),
                            explicitDisplay = item.ExplicitModsDisplay.ToArray(),
                        },
                        stats,
                        mapIsCorruptedWaystone = new
                        {
                            id = (int)GameStats.map_is_corrupted_waystone,
                            name = nameof(GameStats.map_is_corrupted_waystone),
                            present = corruptedStatPresent,
                            value = corruptedStatPresent ? corruptedStatValue : (int?)null,
                        },
                    },
                    inventoryDetailDiagnostic = item.DetailDiagnostic,
                };

                var directory = Path.Combine(
                    AppContext.BaseDirectory,
                    "configs",
                    "waystone-corruption-probes");
                Directory.CreateDirectory(directory);
                var path = Path.GetFullPath(Path.Combine(
                    directory,
                    $"waystone-corruption-{capturedAtUtc:yyyyMMddTHHmmssfffffffZ}.json"));
                var json = JsonSerializer.Serialize(capture, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);

                corruptionProbePath = path;
                corruptionProbeStatus = rawReadSucceeded
                    ? $"Captured one sample ({corruptionProbeLabel.Trim()}) at {capturedAtUtc:O}. Raw bytes: {CorruptionProbeLength} bytes."
                    : $"Saved sample metadata, but the raw Mods read failed ({bytesRead} bytes read). Retry after refreshing.";
            }
            catch (Exception ex)
            {
                corruptionProbePath = string.Empty;
                corruptionProbeStatus = $"Capture failed: {ex.GetType().Name}: {ex.Message}";
            }
        }

        private static CorruptionProbeModifier CaptureModifier(InventorySnapshotMod mod) =>
            new(
                mod.Name,
                float.IsNaN(mod.Value0) ? null : mod.Value0,
                float.IsNaN(mod.Value1) ? null : mod.Value1);

        private static object[] CaptureComponentSnapshots(Item item, SafeMemoryHandle reader) =>
            item.GetComponentAddressPairs()
                .OrderBy(component => component.Key, StringComparer.Ordinal)
                .Take(CorruptionProbeMaxComponents)
                .Select(component =>
                {
                    var componentBytes = new byte[CorruptionProbeComponentPrefixLength];
                    nuint componentBytesRead = 0;
                    var readSucceeded = component.Value != IntPtr.Zero &&
                                        reader.TryReadMemoryArray(component.Value, componentBytes, out componentBytesRead);
                    return (object)new
                    {
                        name = component.Key,
                        address = FormatAddress(component.Value),
                        rawPrefix = new
                        {
                            offsetFromComponent = "0x0",
                            length = CorruptionProbeComponentPrefixLength,
                            readSucceeded,
                            bytesRead = (ulong)componentBytesRead,
                            hex = readSucceeded ? Convert.ToHexString(componentBytes) : string.Empty,
                        },
                    };
                })
                .ToArray();

        private static object CaptureVisibleItemControl(IntPtr itemAddress)
        {
            var gameUi = Core.States.InGameStateObject?.GameUi;
            var uiAddress = IntPtr.Zero;
            var found = gameUi != null && gameUi.TryGetVisibleInventoryItemUiAddress(itemAddress, out uiAddress);
            if (!found)
            {
                return new
                {
                    found = false,
                    reason = "No visible player-inventory control matched the selected item pointer.",
                };
            }

            var reader = Core.Process?.Handle;
            var ui = default(UiElementBaseOffset);
            var uiReadSucceeded = reader != null && reader.TryReadMemory(uiAddress, out ui);
            var hasDisplayText = UiElementMemory.TryReadDisplayText(uiAddress, out var displayText);
            return new
            {
                found = true,
                address = FormatAddress(uiAddress),
                visibleThroughParents = UiElementMemory.IsVisibleThroughParents(uiAddress),
                uiReadSucceeded,
                selfAddress = uiReadSucceeded ? FormatAddress(ui.Self) : string.Empty,
                parentAddress = uiReadSucceeded ? FormatAddress(ui.ParentPtr) : string.Empty,
                flags = uiReadSucceeded ? $"0x{ui.Flags:X8}" : string.Empty,
                positionModifier = uiReadSucceeded ? new { x = ui.PositionModifier.X, y = ui.PositionModifier.Y } : null,
                relativePosition = uiReadSucceeded ? new { x = ui.RelativePosition.X, y = ui.RelativePosition.Y } : null,
                unscaledSize = uiReadSucceeded ? new { x = ui.UnscaledSize.X, y = ui.UnscaledSize.Y } : null,
                displayTextReadSucceeded = hasDisplayText,
                displayText = hasDisplayText ? displayText : string.Empty,
            };
        }

        private static object? CaptureMouseOverEntity(IntPtr itemAddress)
        {
            var mouseOverEntity = Core.States.InGameStateObject?.MouseOverEntity;
            if (mouseOverEntity == null || !mouseOverEntity.IsValid)
            {
                return null;
            }

            var matchesSelectedItem = mouseOverEntity.Address == itemAddress;
            return new
            {
                address = FormatAddress(mouseOverEntity.Address),
                path = mouseOverEntity.Path,
                isSelectedItem = matchesSelectedItem,
                componentNames = matchesSelectedItem
                    ? mouseOverEntity.GetComponentNames().OrderBy(name => name, StringComparer.Ordinal).ToArray()
                    : Array.Empty<string>(),
            };
        }

        private static string FormatAddress(IntPtr address) => $"0x{address.ToInt64():X}";

        private sealed record CorruptionProbeModifier(string Id, float? Value0, float? Value1);

        private sealed record CorruptionProbeStat(int Id, string Name, int Value);

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

        private static void RenderDisplayMods(string label, IReadOnlyList<string> mods)
        {
            if (!ImGui.TreeNode($"{label} ({mods.Count})##InventoryDvDisplay{label}"))
            {
                return;
            }

            foreach (var mod in mods)
            {
                ImGuiHelper.DisplayTextAndCopyOnClick(mod, mod);
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
        ///     PoE2 stores waystone implicit values (Item Rarity, Pack Size, etc.) directly
        ///     in StatsFromMods rather than in the ImplicitMods vector.
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

            // Waystone implicit summary stats (displayed on tooltip)
            // NOTE: PoE2 reuses legacy PoE1 stat IDs with different display semantics.
            (GameStats stat, string label)[] waystoneSummaryStats =
            {
                (GameStats.map_pack_size_positive_percentage_final_from_map, "Item Rarity"),
                (GameStats.map_number_of_magic_and_rare_packs_positive_percentage_final_and_rare_monster_modifiers_chance_positive_percentage_final_from_map, "Pack Size"),
                (GameStats.map_monster_potency_positive_percentage_final_from_map, "Monster Rarity"),
                (GameStats.map_map_item_drop_chance_positive_percentage_final_from_map, "Monster Effectiveness"),
                (GameStats.map_unique_item_drop_chance_positive_percentage, "Waystone Drop Chance"),
            };

            ImGui.SeparatorText("Waystone Summary");
            var revives = Math.Max(0, 6 - item.ExplicitMods.Count);
            ImGui.Text($"Revives Available: {revives}");

            var foundSummary = false;
            foreach (var (stat, label) in waystoneSummaryStats)
            {
                if (stats.TryGetValue(stat, out var value))
                {
                    ImGui.Text($"{label}: +{value}%");
                    foundSummary = true;
                }
            }

            if (!foundSummary)
            {
                ImGui.TextColored(
                    new Vector4(1f, 0.6f, 0.2f, 1f),
                    "No known Waystone summary stats found. Stat IDs may have changed.");
            }

            // Explicit mod contributed stats (monster affixes)
            (GameStats stat, string label)[] explicitModStats =
            {
                (GameStats.map_monsters_stun_threshold_positive_percentage, "Monster Stun Threshold"),
                (GameStats.map_monsters_hit_damage_freeze_multiplier_positive_percentage, "Monster Freeze Damage"),
                (GameStats.map_monsters_shock_chance_positive_percentage, "Monster Extra Damage / Shock Chance"),
                (GameStats.map_monsters_ailment_threshold_positive_percentage, "Monster Ailment Threshold"),
                (GameStats.map_rare_monsters_drop_x_additional_rare_items, "Monster Speed Increase"),
                (GameStats.map_tier_bonus_permillage, "Cooldown Recovery Rate"),
                (GameStats.lineage_support_gem_limit_positive_, "Curse with Enfeeble"),
                (GameStats.wing_blast_cone_pullback_percentage, "Monster Skill Attack"),
                (GameStats.map_players_skill_area_of_effect_positive_percentage_final, "Player AoE"),
            };

            var foundExplicit = false;
            foreach (var (stat, label) in explicitModStats)
            {
                if (stats.TryGetValue(stat, out var value))
                {
                    if (!foundExplicit)
                    {
                        ImGui.SeparatorText("Explicit Mod Stats");
                        foundExplicit = true;
                    }

                    ImGui.TextDisabled($"{label}: {value}");
                }
            }

            // Show all remaining ModStats not covered above
            var knownKeys = new HashSet<GameStats>();
            foreach (var (stat, _) in waystoneSummaryStats)
            {
                knownKeys.Add(stat);
            }

            foreach (var (stat, _) in explicitModStats)
            {
                knownKeys.Add(stat);
            }

            knownKeys.Add(GameStats.dummy_stat_display_nothing);

            var unknownStats = stats
                .Where(entry => !knownKeys.Contains(entry.Key))
                .OrderBy(entry => (int)entry.Key)
                .ToList();
            if (unknownStats.Count > 0 && ImGui.TreeNode($"Unmapped stats ({unknownStats.Count})##InventoryDvUnmappedStats"))
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

        private static string CategoryLabel(InventorySnapshotItem item) => item.Category switch
        {
            InventoryItemCategory.Waystone when item.WaystoneTier is int tier => $"Waystone T{tier}",
            InventoryItemCategory.FlaskOrCharm => "Flask/Charm",
            InventoryItemCategory.LeagueItem => "League Item",
            _ => item.Category.ToString(),
        };

        private static string RarityDetail(InventorySnapshotItem item) =>
            item.Rarity?.ToString() ??
            (HasModsComponent(item) ? "Unavailable (Mods read failed)" : "N/A (no Mods component)");

        private static bool HasModsComponent(InventorySnapshotItem item) =>
            item.ComponentNames.Contains(nameof(Mods), StringComparer.Ordinal);

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
