// <copyright file="DataVisualization.cs" company="None">
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
    using Coroutine;
    using CoroutineEvents;
    using TEHhub.Offsets.Natives;
    using TEHhub.Offsets.Objects.States.InGameState;
    using TEHhub.Offsets.Objects.UiElement;
    using TEHhub.RemoteEnums;
    using TEHhub.RemoteObjects;
    using TEHhub.RemoteObjects.Components;
    using TEHhub.RemoteObjects.States;
    using TEHhub.RemoteObjects.States.InGameStateObjects;
    using TEHhub.RemoteObjects.UiElement;
    using ImGuiNET;
    using Utils;

    /// <summary>
    ///     Data Visualization 2.0: Streamlined Real-time Game Inspector and Debugger Suite.
    /// </summary>
    public static class DataVisualization
    {
        private static string buffSearchFilter = string.Empty;
        private static string entitySearchFilter = string.Empty;
        private static byte entityFilterMode = 1; // 0: Id, 1: Path, 2: Rarity
        private static Rarity entityRarityFilter = Rarity.Normal;
        private static InventoryName selectedInventory = InventoryName.NoInvSelected;
        private static Vector3 testWorldCoords = Vector3.Zero;
        private static string componentSearchFilter = string.Empty;
        private static int selectedUiChildIndex = 0;
        private static float inWorldLabelMaxDistance = 3000f;

        /// <summary>
        ///     Initializes the co-routines.
        /// </summary>
        internal static void InitializeCoroutines()
        {
            CoroutineHandler.Start(DataVisualizationRenderCoRoutine(), priority: UiRenderPriority.CoreWindows);
        }

        /// <summary>
        ///     Draws the window for Data Visualization 2.0.
        /// </summary>
        /// <returns>co-routine IWait.</returns>
        private static IEnumerator<Wait> DataVisualizationRenderCoRoutine()
        {
            while (true)
            {
                yield return new Wait(TEHhubEvents.OnRender);
                if (!Core.GHSettings.ShowDataVisualization)
                {
                    continue;
                }

                ImGui.SetNextWindowSize(new Vector2(980, 680), ImGuiCond.FirstUseEver);
                if (ImGui.Begin("Data Visualization 2.0", ref Core.GHSettings.ShowDataVisualization))
                {
                    if (ImGui.BeginTabBar("DataVisualizationTabBar", ImGuiTabBarFlags.Reorderable | ImGuiTabBarFlags.FittingPolicyScroll))
                    {
                        if (ImGui.BeginTabItem("Player & Area"))
                        {
                            DrawPlayerAndAreaTab();
                            ImGui.EndTabItem();
                        }

                        if (ImGui.BeginTabItem("Entities & World"))
                        {
                            if (Core.States.GameCurrentState == GameStateTypes.InGameState)
                            {
                                DrawInWorldEntityLabels();
                            }

                            DrawEntitiesAndWorldTab();
                            ImGui.EndTabItem();
                        }

                        if (ImGui.BeginTabItem("Inventories & Items"))
                        {
                            DrawInventoriesAndItemsTab();
                            ImGui.EndTabItem();
                        }

                        if (ImGui.BeginTabItem("Map & Terrain"))
                        {
                            DrawMapAndTerrainTab();
                            ImGui.EndTabItem();
                        }

                        if (ImGui.BeginTabItem("UI & System"))
                        {
                            DrawUiAndSystemTab();
                            ImGui.EndTabItem();
                        }

                        ImGui.EndTabBar();
                    }
                }

                ImGui.End();
            }
        }

        // ==========================================
        // TAB 1: Player & Area
        // ==========================================
        private static void DrawPlayerAndAreaTab()
        {
            var inGame = Core.States.InGameStateObject;
            var area = inGame.CurrentAreaInstance;
            var world = inGame.CurrentWorldInstance;
            var player = area.Player;

            // Section 1: Area Information
            if (ImGui.CollapsingHeader("Area Information", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.TextColored(new Vector4(0.3f, 0.9f, 1.0f, 1.0f), $"Area: {world.AreaDetails.Name} (ID: {world.AreaDetails.Id})");
                ImGui.Text($"Monster Level: {area.CurrentAreaLevel} | Act: {world.AreaDetails.Act}");
                ImGui.Text($"Area Hash: {area.AreaHash} | IsTown: {world.AreaDetails.IsTown} | IsHideout: {world.AreaDetails.IsHideout}");
                ImGuiHelper.IntPtrToImGui("AreaInstance Address", area.Address);
                ImGuiHelper.IntPtrToImGui("ServerData Address", area.ServerDataObject.Address);

                // Display dynamic Expedition Config if present in area
                var exp = area.ExpeditionConfig;
                if (exp.IsGrandExpedition || exp.IncreasedExplosivesPercent > 0f || world.AreaDetails.Id.Contains("Expedition", StringComparison.OrdinalIgnoreCase))
                {
                    ImGui.Separator();
                    ImGui.TextColored(new Vector4(1.0f, 0.75f, 0.2f, 1.0f), $"Expedition Config: {exp}");
                }
            }

            // Section 2: Active Area Modifiers (Separate Header)
            if (ImGui.CollapsingHeader($"Active Area / Map Modifiers ({area.AreaMods.Count})###AreaModsHeader", ImGuiTreeNodeFlags.DefaultOpen))
            {
                if (area.AreaMods.Count == 0)
                {
                    ImGui.TextDisabled("No active area modifiers (Town / Hideout).");
                }
                else
                {
                    for (var i = 0; i < area.AreaMods.Count; i++)
                    {
                        var mod = area.AreaMods[i];
                        var valStr = float.IsNaN(mod.Values.Value0)
                            ? ""
                            : float.IsNaN(mod.Values.Value1)
                                ? $" ({mod.Values.Value0})"
                                : $" ({mod.Values.Value0} - {mod.Values.Value1})";
                        ImGui.PushID(i);
                        ImGuiHelper.DisplayTextAndCopyOnClick($"• {mod.DisplayName}{valStr}", mod.DisplayName);
                        ImGui.PopID();
                    }
                }
            }

            // Section 2: Player Vitals & Status
            if (ImGui.CollapsingHeader("Player Vitals & Position", ImGuiTreeNodeFlags.DefaultOpen))
            {
                if (player.IsValid)
                {
                    if (player.TryGetComponent<Life>(out var life))
                    {
                        var hp = life.Health;
                        var mp = life.Mana;
                        var es = life.EnergyShield;
                        var spirit = life.Spirit;

                        ImGui.Columns(2, "VitalsCol", false);
                        ImGui.SetColumnWidth(0, 360);

                        var hpRatio = hp.Total > 0 ? (float)hp.Current / hp.Total : 0f;
                        ImGui.ProgressBar(hpRatio, new Vector2(340, 20), $"HP: {hp.Current} / {hp.Total} ({hp.ReservedTotal} res)");

                        var mpRatio = mp.Total > 0 ? (float)mp.Current / mp.Total : 0f;
                        ImGui.ProgressBar(mpRatio, new Vector2(340, 20), $"MP: {mp.Current} / {mp.Total} ({mp.ReservedTotal} res)");

                        if (es.Total > 0)
                        {
                            var esRatio = (float)es.Current / es.Total;
                            ImGui.ProgressBar(esRatio, new Vector2(340, 20), $"ES: {es.Current} / {es.Total}");
                        }

                        if (spirit.Total > 0)
                        {
                            var spRatio = (float)spirit.Current / spirit.Total;
                            ImGui.ProgressBar(spRatio, new Vector2(340, 20), $"Spirit: {spirit.Current} / {spirit.Total} ({spirit.ReservedTotal} res)");
                        }

                        ImGui.NextColumn();
                        ImGui.Text($"IsAlive: {life.IsAlive} | Ward: {life.Ward.Current} | Divinity: {life.Divinity.Current}");
                        ImGuiHelper.IntPtrToImGui("Player Entity", player.Address);
                        ImGuiHelper.IntPtrToImGui("Life Component", life.Address);

                        if (player.TryGetComponent<Render>(out var render))
                        {
                            ImGui.Text($"World Pos: ({render.WorldPosition.X:F1}, {render.WorldPosition.Y:F1}, {render.WorldPosition.Z:F1})");
                            ImGui.Text($"Grid Pos:  ({render.GridPosition.X:F1}, {render.GridPosition.Y:F1}) | Height: {render.TerrainHeight:F1}");
                        }

                        ImGui.Columns(1);
                    }
                }
                else
                {
                    ImGui.TextDisabled("Local player entity not loaded.");
                }
            }

            // Section 3: Active Buffs & Status Effects
            if (ImGui.CollapsingHeader("Active Status Effects & Buffs", ImGuiTreeNodeFlags.DefaultOpen))
            {
                if (player.IsValid && player.TryGetComponent<Buffs>(out var buffs))
                {
                    var statusEffects = buffs.StatusEffects;
                    ImGui.InputTextWithHint("##BuffSearch", "Filter buffs by name...", ref buffSearchFilter, 100);
                    ImGui.SameLine();
                    if (ImGui.Button("Clear##Buff")) buffSearchFilter = string.Empty;
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), $"Total Active: {statusEffects.Count}");

                    if (ImGui.BeginTable("BuffsTable", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
                    {
                        ImGui.TableSetupColumn("Buff Name", ImGuiTableColumnFlags.WidthStretch);
                        ImGui.TableSetupColumn("Stacks", ImGuiTableColumnFlags.WidthFixed, 70);
                        ImGui.TableSetupColumn("Stage", ImGuiTableColumnFlags.WidthFixed, 70);
                        ImGui.TableSetupColumn("Time Left", ImGuiTableColumnFlags.WidthFixed, 80);
                        ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 60);
                        ImGui.TableHeadersRow();

                        foreach (var (name, se) in statusEffects)
                        {
                            if (!string.IsNullOrEmpty(buffSearchFilter) &&
                                !name.Contains(buffSearchFilter, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            ImGui.TableNextRow();
                            ImGui.TableNextColumn();
                            ImGui.Text(name);

                            ImGui.TableNextColumn();
                            ImGui.Text(se.Charges.ToString());

                            ImGui.TableNextColumn();
                            ImGui.Text(se.RawStage.ToString());

                            ImGui.TableNextColumn();
                            if (float.IsInfinity(se.TimeLeft) || float.IsNaN(se.TimeLeft) || se.TimeLeft >= 86400f)
                            {
                                ImGui.TextDisabled("inf");
                            }
                            else
                            {
                                ImGui.Text($"{se.TimeLeft:F1}s");
                            }

                            ImGui.TableNextColumn();
                            if (ImGui.SmallButton($"Copy##{name}"))
                            {
                                ImGui.SetClipboardText(name);
                            }
                        }

                        ImGui.EndTable();
                    }
                }
                else
                {
                    ImGui.TextDisabled("Buffs component unavailable on player.");
                }
            }

            // Section 4: Active Skills & Animations
            if (ImGui.CollapsingHeader("Player Active Skills & Animation"))
            {
                if (player.IsValid && player.TryGetComponent<Actor>(out var actor))
                {
                    ImGui.Text($"Current Animation: {actor.Animation} | Total Active Skills: {actor.ActiveSkills.Count}");

                    if (ImGui.BeginTable("SkillsTable", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
                    {
                        ImGui.TableSetupColumn("Skill Name", ImGuiTableColumnFlags.WidthStretch);
                        ImGui.TableSetupColumn("Usable?", ImGuiTableColumnFlags.WidthFixed, 75);
                        ImGui.TableSetupColumn("Use Stage", ImGuiTableColumnFlags.WidthFixed, 80);
                        ImGui.TableSetupColumn("Cast Type", ImGuiTableColumnFlags.WidthFixed, 90);
                        ImGui.TableHeadersRow();

                        foreach (var (name, details) in actor.ActiveSkills)
                        {
                            var isUsable = actor.IsSkillUsable.Contains(name);
                            ImGui.TableNextRow();
                            ImGui.TableNextColumn();
                            ImGui.Text(name);

                            ImGui.TableNextColumn();
                            if (isUsable)
                            {
                                ImGui.TextColored(new Vector4(0, 1, 0, 1), "YES");
                            }
                            else
                            {
                                ImGui.TextColored(new Vector4(1, 0.3f, 0.3f, 1), "NO");
                            }

                            ImGui.TableNextColumn();
                            ImGui.Text(details.UseStage.ToString());

                            ImGui.TableNextColumn();
                            ImGui.Text(details.CastType.ToString());
                        }

                        ImGui.EndTable();
                    }
                }
                else
                {
                    ImGui.TextDisabled("Actor component unavailable on player.");
                }
            }
        }

        // ==========================================
        // TAB 2: Entities & World
        // ==========================================
        private static void DrawEntitiesAndWorldTab()
        {
            var inGame = Core.States.InGameStateObject;
            var area = inGame.CurrentAreaInstance;
            var mouseOver = inGame.MouseOverEntity;

            // Hovered Entity Bar
            if (mouseOver.IsValid)
            {
                ImGui.TextColored(new Vector4(1.0f, 0.9f, 0.2f, 1.0f), $"[Mouse-Over] ID: {mouseOver.Id} | Path: {mouseOver.Path}");
                ImGui.SameLine();
                if (ImGui.SmallButton("Inspect Hovered Components"))
                {
                    ImGui.OpenPopup("HoveredEntityModal");
                }

                if (ImGui.BeginPopup("HoveredEntityModal"))
                {
                    mouseOver.ToImGui();
                    if (ImGui.Button("Close")) ImGui.CloseCurrentPopup();
                    ImGui.EndPopup();
                }
                ImGui.Separator();
            }

            // Entity Controls
            ImGui.Text($"Awake: {area.AwakeEntities.Count} | Sleeping: {area.SleepingEntities.Count} | Network Bubble: {area.NetworkBubbleEntityCount}");

            if (ImGui.RadioButton("Filter ID", entityFilterMode == 0)) entityFilterMode = 0;
            ImGui.SameLine();
            if (ImGui.RadioButton("Filter Path", entityFilterMode == 1)) entityFilterMode = 1;
            ImGui.SameLine();
            if (ImGui.RadioButton("Filter Rarity", entityFilterMode == 2)) entityFilterMode = 2;

            ImGui.SameLine();
            switch (entityFilterMode)
            {
                case 0:
                    ImGui.SetNextItemWidth(180);
                    ImGui.InputTextWithHint("##EntIdFilter", "Entity ID...", ref entitySearchFilter, 20, ImGuiInputTextFlags.CharsDecimal);
                    break;
                case 1:
                    ImGui.SetNextItemWidth(260);
                    ImGui.InputTextWithHint("##EntPathFilter", "Filter path (Monster, Chest, Expedition)...", ref entitySearchFilter, 120);
                    break;
                case 2:
                    ImGui.SetNextItemWidth(120);
                    ImGuiHelper.EnumComboBox("##RarityFilter", ref entityRarityFilter);
                    break;
            }

            ImGui.SameLine();
            if (ImGui.Button("Scan Sleeping Entities"))
            {
                area.ScanAllSleepingEntities();
            }

            ImGui.Separator();
            if (ImGui.BeginTabBar("EntitiesSubTabBar"))
            {
                if (ImGui.BeginTabItem($"Awake Entities ({area.AwakeEntities.Count})"))
                {
                    DrawEntityList(area.AwakeEntities);
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem($"Sleeping Entities ({area.SleepingEntities.Count})"))
                {
                    DrawEntityList(area.SleepingEntities);
                    ImGui.EndTabItem();
                }

                ImGui.EndTabBar();
            }
        }

        private static void DrawEntityList(System.Collections.Concurrent.ConcurrentDictionary<EntityNodeKey, Entity> entities)
        {
            var rendered = 0;
            const int maxDisplay = 300;

            ImGui.BeginChild("EntityListScroll", new Vector2(0, 0), ImGuiChildFlags.None);
            foreach (var kv in entities)
            {
                var entity = kv.Value;
                if (!entity.IsValid) continue;

                // Extract friendly name, item info, and rarity
                string extraInfo = string.Empty;
                Rarity? entityRarity = null;

                if (entity.TryGetComponent<WorldItem>(out var wi))
                {
                    var itemName = !string.IsNullOrWhiteSpace(wi.ItemName) ? wi.ItemName : wi.ItemPath;
                    if (!string.IsNullOrWhiteSpace(itemName))
                    {
                        var countStr = string.Empty;
                        if (wi.Item != null && wi.Item.TryGetComponent<TEHhub.RemoteObjects.Components.Stack>(out var st) && st.Count > 1)
                        {
                            countStr = $" x{st.Count}";
                        }

                        if (wi.Item != null && wi.Item.TryGetComponent<Mods>(out var m))
                        {
                            entityRarity = m.Rarity;
                            extraInfo = $" -> WorldItem: {itemName}{countStr} [{m.Rarity}]";
                        }
                        else
                        {
                            entityRarity = Rarity.Normal;
                            extraInfo = $" -> WorldItem: {itemName}{countStr}";
                        }
                    }
                }
                else if (entity.TryGetComponent<ObjectMagicProperties>(out var omp))
                {
                    entityRarity = omp.Rarity;
                    extraInfo = $" [{omp.Rarity}]";
                }

                // Filtering logic
                switch (entityFilterMode)
                {
                    case 0: // Filter ID
                        if (!string.IsNullOrEmpty(entitySearchFilter) && !$"{entity.Id}".Contains(entitySearchFilter))
                        {
                            continue;
                        }
                        break;

                    case 1: // Filter Path / Name
                        if (!string.IsNullOrEmpty(entitySearchFilter))
                        {
                            bool matchPath = entity.Path.Contains(entitySearchFilter, StringComparison.OrdinalIgnoreCase);
                            bool matchExtra = extraInfo.Contains(entitySearchFilter, StringComparison.OrdinalIgnoreCase);
                            if (!matchPath && !matchExtra)
                            {
                                continue;
                            }
                        }
                        break;

                    case 2: // Filter Rarity
                        if (entityRarity == null || entityRarity != entityRarityFilter)
                        {
                            continue;
                        }
                        break;
                }

                if (++rendered > maxDisplay)
                {
                    ImGui.TextDisabled($"... (Showing first {maxDisplay} entities, use Filter to narrow down)");
                    break;
                }

                var label = $"[{entity.Id}] {entity.Path}{extraInfo}";
                ImGui.PushID((int)entity.Id);
                var opened = ImGui.TreeNode(label);
                ImGui.SameLine();
                if (ImGui.SmallButton("JSON"))
                {
                    DumpEntityJson(kv.Key, entity);
                }

                if (opened)
                {
                    entity.ToImGui();
                    ImGui.TreePop();
                }
                ImGui.PopID();
            }
            ImGui.EndChild();
        }

        private static void DumpEntityJson(EntityNodeKey key, Entity entity)
        {
            try
            {
                var payload = new
                {
                    EntityId = entity.Id,
                    Key = key.id,
                    Path = entity.Path,
                    IsValid = entity.IsValid,
                    Components = entity.GetComponentNames()?.ToList()
                };
                var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
                Directory.CreateDirectory("entity_dumps");
                var path = Path.Combine("entity_dumps", $"entity_{entity.Id}.json");
                File.WriteAllText(path, json);
                ImGui.SetClipboardText(json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DumpEntityJson] Failed: {ex.Message}");
            }
        }

        // ==========================================
        // TAB 3: Inventories & Items
        // ==========================================
        private static void DrawInventoriesAndItemsTab()
        {
            var serverData = Core.States.InGameStateObject.CurrentAreaInstance.ServerDataObject;
            var inventories = serverData.PlayerInventories;
            var flaskInv = serverData.FlaskInventory;

            // Section 1: Flasks & Charms
            if (ImGui.CollapsingHeader("Flask & Charm Slots (1 - 5)", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGuiHelper.IntPtrToImGui("Flask Inventory Address", flaskInv.Address);
                if (flaskInv.Address != IntPtr.Zero)
                {
                    flaskInv.ToImGui();
                }
                else
                {
                    ImGui.TextDisabled("Flask inventory unavailable (or in town).");
                }
            }

            // Section 2: Player & Stash Inventories
            if (ImGui.CollapsingHeader("Player Inventories & Stash", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.Text($"Available Inventories: {inventories.Count}");
                if (ImGuiHelper.IEnumerableComboBox("Select Inventory", inventories.Keys, ref selectedInventory))
                {
                    serverData.SelectedInv.Address = inventories.TryGetValue(selectedInventory, out var addr) ? addr : IntPtr.Zero;
                }

                ImGui.SameLine();
                if (ImGui.Button("Clear Selection"))
                {
                    selectedInventory = InventoryName.NoInvSelected;
                    serverData.SelectedInv.Address = IntPtr.Zero;
                }

                ImGui.Separator();
                if (selectedInventory != InventoryName.NoInvSelected && serverData.SelectedInv.Address != IntPtr.Zero)
                {
                    serverData.SelectedInv.ToImGui();
                }
                else
                {
                    ImGui.TextDisabled("Select an inventory from the dropdown above to inspect items and slot layout.");
                }
            }
        }

        // ==========================================
        // TAB 4: Map & Terrain
        // ==========================================
        private static void DrawMapAndTerrainTab()
        {
            var inGame = Core.States.InGameStateObject;
            var area = inGame.CurrentAreaInstance;
            var world = inGame.CurrentWorldInstance;
            var metadata = area.TerrainMetadata;

            // Section 1: Terrain Info
            if (ImGui.CollapsingHeader("Terrain Metadata & Grid Data", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.Columns(2, "TerrainCol", false);
                ImGui.Text($"Total Tiles: {metadata.TotalTiles}");
                ImGuiHelper.IntPtrToImGui("Tile Details Ptr", metadata.TileDetailsPtr.First);
                ImGui.Text($"Tile Height Multiplier: {metadata.TileHeightMultiplier}");

                ImGui.NextColumn();
                ImGui.Text($"Grid Walkable Data: {area.GridWalkableData.Length} bytes");
                ImGui.Text($"Grid Height Rows: {area.GridHeightData.Length}");
                ImGui.Text($"Tgt Named Tile Types: {area.TgtTilesLocations.Count}");
                ImGui.Columns(1);
            }

            // Section 2: TGT Tile Locations
            if (ImGui.CollapsingHeader($"TGT Named Tile Locations ({area.TgtTilesLocations.Count})", ImGuiTreeNodeFlags.DefaultOpen))
            {
                if (area.TgtTilesLocations.Count == 0)
                {
                    ImGui.TextDisabled("No named TGT tile markers in this area.");
                }
                else
                {
                    foreach (var kv in area.TgtTilesLocations)
                    {
                        if (ImGui.TreeNode($"{kv.Key} ({kv.Value.Count} instances)"))
                        {
                            foreach (var loc in kv.Value)
                            {
                                ImGui.Text($"  Grid: ({loc.X}, {loc.Y})");
                            }

                            ImGui.TreePop();
                        }
                    }
                }
            }

            // Section 3: World to Screen & Projection Matrix
            if (ImGui.CollapsingHeader("World-to-Screen Projection Matrix"))
            {
                world.ToImGui();

                ImGui.Separator();
                ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), "World-to-Screen Calculator:");
                ImGui.InputFloat3("World Coords (X, Y, Z)", ref testWorldCoords);
                var screenPos = world.WorldToScreen(new StdTuple3D<float> { X = testWorldCoords.X, Y = testWorldCoords.Y, Z = testWorldCoords.Z });
                ImGui.Text($"Calculated Screen Pos: ({screenPos.X:F1}, {screenPos.Y:F1}) | Viewport: {Core.Process.WindowArea}");
            }
        }

        // ==========================================
        // TAB 5: UI & System
        // ==========================================
        private static void DrawUiAndSystemTab()
        {
            var inGame = Core.States.InGameStateObject;
            var gameUi = inGame.GameUi;
            var proc = Core.Process;

            // Section 1: Process & Static Addresses
            if (ImGui.CollapsingHeader("Process & Static Addresses", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.Columns(2, "ProcCol", false);
                ImGui.Text($"Process: {proc.Information} (PID: {proc.Pid})");
                ImGuiHelper.IntPtrToImGui("Base Address", proc.Address);
                ImGui.Text($"Window Area: {proc.WindowArea}");
                ImGui.Text($"Foreground: {proc.Foreground}");

                ImGui.NextColumn();
                ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.3f, 1.0f), "Static Offsets Found:");
                foreach (var kv in proc.StaticAddresses)
                {
                    ImGuiHelper.IntPtrToImGui(kv.Key, kv.Value);
                }
                ImGui.Columns(1);
            }

            // Section 2: UI Explorer
            if (ImGui.CollapsingHeader("Game UI Tree & Panels", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.Text($"UiRoot: 0x{inGame.UiRootAddress.ToInt64():X} | GameUi: 0x{gameUi.Address.ToInt64():X} | Controller Mode: {Core.GHSettings.EnableControllerMode}");

                if (ImGui.TreeNode("Important UI Panels"))
                {
                    gameUi.ToImGui();
                    ImGui.TreePop();
                }

                if (gameUi.Address != IntPtr.Zero)
                {
                    var reader = Core.Process.Handle;
                    var rootElem = reader.ReadMemory<UiElementBaseOffset>(gameUi.Address);
                    var childCount = (int)rootElem.ChildrensPtr.TotalElements(8);
                    ImGui.Text($"Top-level UI Children: {childCount}");

                    if (childCount > 0)
                    {
                        ImGui.SliderInt("Child Index", ref selectedUiChildIndex, 0, Math.Max(0, childCount - 1));
                        var children = reader.ReadStdVector<IntPtr>(rootElem.ChildrensPtr);
                        if (selectedUiChildIndex >= 0 && selectedUiChildIndex < children.Length)
                        {
                            var childAddr = children[selectedUiChildIndex];
                            ImGuiHelper.IntPtrToImGui($"Child[{selectedUiChildIndex}] Address", childAddr);
                            if (childAddr != IntPtr.Zero)
                            {
                                var childElem = reader.ReadMemory<UiElementBaseOffset>(childAddr);
                                var isVisible = UiElementBaseFuncs.IsVisibleChecker(childElem.Flags);
                                ImGui.Text($"Flags: 0x{childElem.Flags:X8} | Visible: {isVisible} | Children: {childElem.ChildrensPtr.TotalElements(8)}");
                                ImGui.Text($"RelPos: ({childElem.RelativePosition.X}, {childElem.RelativePosition.Y}) | UnscaledSize: ({childElem.UnscaledSize.X} x {childElem.UnscaledSize.Y})");
                            }
                        }
                    }
                }
            }

            // Section 3: GGPK Caches & Diagnostics
            if (ImGui.CollapsingHeader("GGPK Caches & Diagnostics Settings"))
            {
                Core.CacheImGui();

                ImGui.Separator();
                if (ImGui.Button("Open Full Memory Read Diagnostics Window"))
                {
                    Core.GHSettings.ShowMemoryDiagnostics = true;
                }

                ImGui.Checkbox("Enable New Memory Read Pipeline", ref Core.GHSettings.EnableNewMemoryRead);
                ImGui.Checkbox("Enable Stale Entity Cleanup", ref Core.GHSettings.EnableStaleEntityCleanup);
                ImGui.Checkbox("Hide Overlays When Game Inactive", ref Core.GHSettings.HideOverlaysWhenGameInactive);
            }
        }

        private static void DrawInWorldEntityLabels()
        {
            var inGame = Core.States.InGameStateObject;
            var area = inGame?.CurrentAreaInstance;
            var world = inGame?.CurrentWorldInstance;
            if (area == null || world == null || area.AwakeEntities.IsEmpty)
            {
                return;
            }

            var player = area.Player;
            Vector3 playerPos = Vector3.Zero;
            bool hasPlayerPos = false;
            if (player.IsValid && player.TryGetComponent<Render>(out var pRender))
            {
                playerPos = new Vector3(pRender.WorldPosition.X, pRender.WorldPosition.Y, pRender.WorldPosition.Z);
                hasPlayerPos = true;
            }

            var drawList = ImGui.GetBackgroundDrawList();
            var maxDistSq = inWorldLabelMaxDistance * inWorldLabelMaxDistance;

            foreach (var kv in area.AwakeEntities)
            {
                var entity = kv.Value;
                if (!entity.IsValid)
                {
                    continue;
                }

                if (!entity.TryGetComponent<Render>(out var render))
                {
                    continue;
                }

                var entPos = new Vector3(render.WorldPosition.X, render.WorldPosition.Y, render.WorldPosition.Z);
                if (hasPlayerPos && Vector3.DistanceSquared(playerPos, entPos) > maxDistSq)
                {
                    continue;
                }

                // Determine entity rarity and colors
                Rarity entityRarity = Rarity.Normal;
                uint textColor = 0xFFFFFFFF; // White
                uint borderColor = 0xFF888888;
                string friendlyName = string.Empty;
                string countStr = string.Empty;
                bool isTargetedType = false;

                if (entity.TryGetComponent<WorldItem>(out var wi))
                {
                    isTargetedType = true;
                    var itemName = !string.IsNullOrWhiteSpace(wi.ItemName) ? wi.ItemName : (!string.IsNullOrWhiteSpace(wi.ItemPath) ? wi.ItemPath : "Item");
                    if (wi.Item != null && wi.Item.TryGetComponent<TEHhub.RemoteObjects.Components.Stack>(out var st) && st.Count > 1)
                    {
                        countStr = $" x{st.Count}";
                    }

                    if (wi.Item != null && wi.Item.TryGetComponent<Mods>(out var m))
                    {
                        entityRarity = m.Rarity;
                    }

                    switch (entityRarity)
                    {
                        case Rarity.Unique:
                            textColor = 0xFF3090F0; // Orange / Brown
                            borderColor = 0xFF3090F0;
                            break;
                        case Rarity.Rare:
                            textColor = 0xFF50D0F0; // Yellow
                            borderColor = 0xFF50D0F0;
                            break;
                        case Rarity.Magic:
                            textColor = 0xFFFFB070; // Blue
                            borderColor = 0xFFFFB070;
                            break;
                        default:
                            textColor = 0xFFFFFFFF; // White
                            borderColor = 0xFFAAAAAA;
                            break;
                    }

                    friendlyName = $"{itemName}{countStr}";
                }
                else if (entity.Path.StartsWith("Metadata/Monsters", StringComparison.OrdinalIgnoreCase))
                {
                    isTargetedType = true;
                    if (entity.TryGetComponent<Life>(out var life) && !life.IsAlive)
                    {
                        continue;
                    }

                    if (entity.TryGetComponent<ObjectMagicProperties>(out var omp))
                    {
                        entityRarity = omp.Rarity;
                    }

                    switch (entityRarity)
                    {
                        case Rarity.Unique:
                            textColor = 0xFF3090F0;
                            borderColor = 0xFF3090F0;
                            break;
                        case Rarity.Rare:
                            textColor = 0xFF50D0F0;
                            borderColor = 0xFF50D0F0;
                            break;
                        case Rarity.Magic:
                            textColor = 0xFFFFB070;
                            borderColor = 0xFFFFB070;
                            break;
                        default:
                            textColor = 0xFFE0E0E0;
                            borderColor = 0xFF666666;
                            break;
                    }

                    var nameParts = entity.Path.Split('/');
                    friendlyName = nameParts.Length > 0 ? nameParts[^1] : entity.Path;
                }
                else if (entity.TryGetComponent<Chest>(out _) || entity.Path.StartsWith("Metadata/Chests", StringComparison.OrdinalIgnoreCase))
                {
                    isTargetedType = true;
                    textColor = 0xFF80E0FF; // Soft Cyan
                    borderColor = 0xFF40A0C0;
                    var nameParts = entity.Path.Split('/');
                    friendlyName = nameParts.Length > 0 ? nameParts[^1] : "Chest";
                }
                else if (entity.Path.Contains("Expedition", StringComparison.OrdinalIgnoreCase) ||
                         entity.Path.Contains("Delve", StringComparison.OrdinalIgnoreCase) ||
                         entity.Path.Contains("NPC", StringComparison.OrdinalIgnoreCase) ||
                         entity.Path.Contains("Shrine", StringComparison.OrdinalIgnoreCase) ||
                         entity.Path.Contains("Portal", StringComparison.OrdinalIgnoreCase) ||
                         entity.Path.Contains("Waypoint", StringComparison.OrdinalIgnoreCase))
                {
                    isTargetedType = true;
                    textColor = 0xFF50FF80; // Greenish
                    borderColor = 0xFF20A040;
                    var nameParts = entity.Path.Split('/');
                    friendlyName = nameParts.Length > 0 ? nameParts[^1] : entity.Path;
                }
                else if (!string.IsNullOrEmpty(entitySearchFilter))
                {
                    isTargetedType = true;
                    var nameParts = entity.Path.Split('/');
                    friendlyName = nameParts.Length > 0 ? nameParts[^1] : entity.Path;
                }
                else
                {
                    continue;
                }

                // Filtering logic based on active Filter mode
                switch (entityFilterMode)
                {
                    case 0: // Filter ID
                        if (!string.IsNullOrEmpty(entitySearchFilter) && !$"{entity.Id}".Contains(entitySearchFilter))
                        {
                            continue;
                        }
                        break;
                    case 1: // Filter Path
                        if (!string.IsNullOrEmpty(entitySearchFilter) && !entity.Path.Contains(entitySearchFilter, StringComparison.OrdinalIgnoreCase) && !friendlyName.Contains(entitySearchFilter, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        break;
                    case 2: // Filter Rarity
                        if (entityRarity != entityRarityFilter)
                        {
                            continue;
                        }
                        break;
                }

                if (!isTargetedType)
                {
                    continue;
                }

                // Format label text according to mode:
                // 0 (Filter ID): show only the ID number
                // 1 (Filter Path): show [ID] + full path
                // 2 (Filter Rarity): show [ID] + friendly name/item name
                string labelText = entityFilterMode switch
                {
                    0 => $"{entity.Id}",
                    1 => $"[{entity.Id}] {entity.Path}",
                    _ => $"[{entity.Id}] {friendlyName}"
                };

                if (string.IsNullOrEmpty(labelText))
                {
                    continue;
                }

                var screenPos = world.WorldToScreen(new StdTuple3D<float> { X = render.WorldPosition.X, Y = render.WorldPosition.Y, Z = render.WorldPosition.Z });
                if (screenPos.X < -100 || screenPos.Y < -100 || screenPos.X > 5000 || screenPos.Y > 5000)
                {
                    continue;
                }

                var textSize = ImGui.CalcTextSize(labelText);
                var pad = new Vector2(4, 2);
                var min = screenPos - (textSize / 2) - pad;
                var max = screenPos + (textSize / 2) + pad;

                drawList.AddRectFilled(min, max, 0xDD101010, 3.0f);
                drawList.AddRect(min, max, borderColor, 3.0f);
                drawList.AddText(min + pad, textColor, labelText);
            }
        }
    }
}

