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
    ///     Data Visualization 2.0: Comprehensive Real-time Game Inspector and Debugger Suite.
    /// </summary>
    public static class DataVisualization
    {
        private static string buffSearchFilter = string.Empty;
        private static string entitySearchFilter = string.Empty;
        private static byte entityFilterMode = 0; // 0: Id, 1: Path, 2: Rarity
        private static Rarity entityRarityFilter = Rarity.Normal;
        private static InventoryName selectedInventory = InventoryName.NoInvSelected;
        private static Vector3 testWorldCoords = Vector3.Zero;
        private static string componentSearchFilter = string.Empty;
        private static int selectedUiChildIndex = 0;

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

                ImGui.SetNextWindowSize(new Vector2(940, 650), ImGuiCond.FirstUseEver);
                if (ImGui.Begin("Data Visualization 2.0", ref Core.GHSettings.ShowDataVisualization))
                {
                    if (ImGui.BeginTabBar("DataVisualizationTabBar", ImGuiTabBarFlags.Reorderable | ImGuiTabBarFlags.FittingPolicyScroll))
                    {
                        if (ImGui.BeginTabItem("Area & Vitals"))
                        {
                            DrawAreaAndVitalsTab();
                            ImGui.EndTabItem();
                        }

                        if (ImGui.BeginTabItem("Buffs"))
                        {
                            DrawBuffsTab();
                            ImGui.EndTabItem();
                        }

                        if (ImGui.BeginTabItem("Entities"))
                        {
                            DrawEntitiesTab();
                            ImGui.EndTabItem();
                        }

                        if (ImGui.BeginTabItem("Inventories"))
                        {
                            DrawInventoriesTab();
                            ImGui.EndTabItem();
                        }

                        if (ImGui.BeginTabItem("Flasks & Charms"))
                        {
                            DrawFlasksAndCharmsTab();
                            ImGui.EndTabItem();
                        }

                        if (ImGui.BeginTabItem("Memory"))
                        {
                            DrawMemoryTab();
                            ImGui.EndTabItem();
                        }

                        if (ImGui.BeginTabItem("UI Explorer"))
                        {
                            DrawUiExplorerTab();
                            ImGui.EndTabItem();
                        }

                        if (ImGui.BeginTabItem("Components"))
                        {
                            DrawComponentsTab();
                            ImGui.EndTabItem();
                        }

                        if (ImGui.BeginTabItem("Render"))
                        {
                            DrawRenderTab();
                            ImGui.EndTabItem();
                        }

                        if (ImGui.BeginTabItem("Terrain"))
                        {
                            DrawTerrainTab();
                            ImGui.EndTabItem();
                        }

                        if (ImGui.BeginTabItem("Events"))
                        {
                            DrawEventsTab();
                            ImGui.EndTabItem();
                        }

                        if (ImGui.BeginTabItem("Skills & Timing"))
                        {
                            DrawSkillsAndTimingTab();
                            ImGui.EndTabItem();
                        }

                        ImGui.EndTabBar();
                    }
                }

                ImGui.End();
            }
        }

        // ==========================================
        // TAB 1: Area & Vitals
        // ==========================================
        private static void DrawAreaAndVitalsTab()
        {
            var inGame = Core.States.InGameStateObject;
            var area = inGame.CurrentAreaInstance;
            var world = inGame.CurrentWorldInstance;
            var player = area.Player;

            ImGui.TextColored(new Vector4(0.3f, 0.9f, 1.0f, 1.0f), "=== CURRENT AREA INFO ===");
            ImGui.Text($"Area Name: {world.AreaDetails.Name} (ID: {world.AreaDetails.Id})");
            ImGui.Text($"Monster Level: {area.CurrentAreaLevel} | Act: {world.AreaDetails.Act}");
            ImGui.Text($"Area Hash: {area.AreaHash} | IsTown: {world.AreaDetails.IsTown} | IsHideout: {world.AreaDetails.IsHideout}");
            ImGuiHelper.IntPtrToImGui("AreaInstance Address", area.Address);
            ImGuiHelper.IntPtrToImGui("ServerData Address", area.ServerDataObject.Address);

            ImGui.Separator();
            ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.3f, 1.0f), $"=== ACTIVE AREA / MAP MODIFIERS ({area.AreaMods.Count}) ===");
            if (area.AreaMods.Count == 0)
            {
                ImGui.TextDisabled("No active area modifiers detected (or in town/hideout).");
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
                    ImGuiHelper.DisplayTextAndCopyOnClick($"• {mod.RawName}{valStr}", mod.RawName);
                }
            }

            ImGui.Separator();
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), "=== PLAYER VITALS & POSITION ===");
            if (player.IsValid)
            {
                ImGuiHelper.IntPtrToImGui("Player Address", player.Address);
                if (player.TryGetComponent<Life>(out var life))
                {
                    ImGuiHelper.IntPtrToImGui("Life Component", life.Address);
                    var hp = life.Health;
                    var mp = life.Mana;
                    var es = life.EnergyShield;
                    var spirit = life.Spirit;

                    var hpRatio = hp.Total > 0 ? (float)hp.Current / hp.Total : 0f;
                    ImGui.ProgressBar(hpRatio, new Vector2(320, 20), $"HP: {hp.Current} / {hp.Total} ({hp.ReservedTotal} reserved)");

                    var mpRatio = mp.Total > 0 ? (float)mp.Current / mp.Total : 0f;
                    ImGui.ProgressBar(mpRatio, new Vector2(320, 20), $"MP: {mp.Current} / {mp.Total} ({mp.ReservedTotal} reserved)");

                    if (es.Total > 0)
                    {
                        var esRatio = (float)es.Current / es.Total;
                        ImGui.ProgressBar(esRatio, new Vector2(320, 20), $"ES: {es.Current} / {es.Total}");
                    }

                    if (spirit.Total > 0)
                    {
                        ImGui.Text($"Spirit: {spirit.Current} / {spirit.Total} (Reserved: {spirit.ReservedTotal})");
                    }

                    ImGui.Text($"IsAlive: {life.IsAlive} | Ward: {life.Ward.Current} | Divinity: {life.Divinity.Current}");
                }

                if (player.TryGetComponent<Render>(out var render))
                {
                    ImGui.Text($"World Pos: ({render.WorldPosition.X:F1}, {render.WorldPosition.Y:F1}, {render.WorldPosition.Z:F1})");
                    ImGui.Text($"Grid Pos:  ({render.GridPosition.X:F1}, {render.GridPosition.Y:F1})");
                    ImGui.Text($"Terrain Height: {render.TerrainHeight:F1}");
                }
            }
            else
            {
                ImGui.TextDisabled("Local player entity not loaded.");
            }
        }

        // ==========================================
        // TAB 2: Buffs
        // ==========================================
        private static void DrawBuffsTab()
        {
            var player = Core.States.InGameStateObject.CurrentAreaInstance.Player;
            if (!player.IsValid || !player.TryGetComponent<Buffs>(out var buffs))
            {
                ImGui.TextDisabled("No active buffs or player component unavailable.");
                return;
            }

            ImGui.InputTextWithHint("##BuffSearch", "Search buffs...", ref buffSearchFilter, 100);
            ImGui.SameLine();
            if (ImGui.Button("Clear")) buffSearchFilter = string.Empty;

            ImGui.Separator();
            var statusEffects = buffs.StatusEffects;
            ImGui.Text($"Total Active Status Effects: {statusEffects.Count}");

            if (ImGui.BeginTable("BuffsTable", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
            {
                ImGui.TableSetupColumn("Buff Name", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Charges/Stacks", ImGuiTableColumnFlags.WidthFixed, 100);
                ImGui.TableSetupColumn("Stage", ImGuiTableColumnFlags.WidthFixed, 80);
                ImGui.TableSetupColumn("Time Left", ImGuiTableColumnFlags.WidthFixed, 90);
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
                    ImGui.Text($"{se.TimeLeft:F1}s");

                    ImGui.TableNextColumn();
                    if (ImGui.SmallButton($"Copy##{name}"))
                    {
                        ImGui.SetClipboardText(name);
                    }
                }

                ImGui.EndTable();
            }
        }

        // ==========================================
        // TAB 3: Entities
        // ==========================================
        private static void DrawEntitiesTab()
        {
            var area = Core.States.InGameStateObject.CurrentAreaInstance;
            ImGui.Text($"Awake: {area.AwakeEntities.Count} | Sleeping: {area.SleepingEntities.Count} | In Network Bubble: {area.NetworkBubbleEntityCount}");

            if (ImGui.RadioButton("Filter by ID", entityFilterMode == 0)) entityFilterMode = 0;
            ImGui.SameLine();
            if (ImGui.RadioButton("Filter by Path", entityFilterMode == 1)) entityFilterMode = 1;
            ImGui.SameLine();
            if (ImGui.RadioButton("Filter by Rarity", entityFilterMode == 2)) entityFilterMode = 2;

            switch (entityFilterMode)
            {
                case 0:
                    ImGui.InputTextWithHint("##EntIdFilter", "Filter by Entity ID...", ref entitySearchFilter, 20, ImGuiInputTextFlags.CharsDecimal);
                    break;
                case 1:
                    ImGui.InputTextWithHint("##EntPathFilter", "Filter by Entity Path (e.g. Monster, Chest, Expedition)...", ref entitySearchFilter, 120);
                    break;
                case 2:
                    ImGuiHelper.EnumComboBox("Rarity Filter", ref entityRarityFilter);
                    break;
            }

            if (ImGui.Button("Scan Sleeping Entities"))
            {
                area.ScanAllSleepingEntities();
            }

            ImGui.Separator();
            if (ImGui.BeginTabBar("EntitiesSubTab"))
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

            foreach (var kv in entities)
            {
                var entity = kv.Value;
                if (!entity.IsValid) continue;

                switch (entityFilterMode)
                {
                    case 0:
                        if (!string.IsNullOrEmpty(entitySearchFilter) && !$"{entity.Id}".Contains(entitySearchFilter)) continue;
                        break;
                    case 1:
                        if (!string.IsNullOrEmpty(entitySearchFilter) && !entity.Path.Contains(entitySearchFilter, StringComparison.OrdinalIgnoreCase)) continue;
                        break;
                    case 2:
                        if (entity.TryGetComponent<ObjectMagicProperties>(out var omp) && omp.Rarity != entityRarityFilter) continue;
                        break;
                }

                if (++rendered > maxDisplay)
                {
                    ImGui.TextDisabled($"... (Showing first {maxDisplay} entities, use Filter to narrow down)");
                    break;
                }

                var label = $"[{entity.Id}] {entity.Path}";
                var opened = ImGui.TreeNode(label);
                ImGui.SameLine();
                if (ImGui.SmallButton($"Dump JSON##{entity.Id}"))
                {
                    DumpEntityJson(kv.Key, entity);
                }

                if (opened)
                {
                    entity.ToImGui();
                    ImGui.TreePop();
                }
            }
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
        // TAB 4: Inventories
        // ==========================================
        private static void DrawInventoriesTab()
        {
            var serverData = Core.States.InGameStateObject.CurrentAreaInstance.ServerDataObject;
            var inventories = serverData.PlayerInventories;

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
                ImGui.TextDisabled("Select an inventory from the dropdown above to inspect its items and slots.");
            }
        }

        // ==========================================
        // TAB 5: Flasks & Charms
        // ==========================================
        private static void DrawFlasksAndCharmsTab()
        {
            var serverData = Core.States.InGameStateObject.CurrentAreaInstance.ServerDataObject;
            var flaskInv = serverData.FlaskInventory;

            ImGui.TextColored(new Vector4(0.4f, 0.9f, 1.0f, 1.0f), "=== FLASK SLOTS (1 - 5) ===");
            ImGuiHelper.IntPtrToImGui("Flask Inventory Address", flaskInv.Address);

            if (flaskInv.Address != IntPtr.Zero)
            {
                flaskInv.ToImGui();
            }
            else
            {
                ImGui.TextDisabled("Flask inventory not available or in town.");
            }
        }

        // ==========================================
        // TAB 6: Memory
        // ==========================================
        private static void DrawMemoryTab()
        {
            var proc = Core.Process;
            ImGui.TextColored(new Vector4(0.3f, 0.9f, 1.0f, 1.0f), "=== PROCESS INFORMATION ===");
            ImGuiHelper.IntPtrToImGui("Base Address", proc.Address);
            ImGui.Text($"Process: {proc.Information} (PID: {proc.Pid})");
            ImGui.Text($"Window Area: {proc.WindowArea} | Foreground: {proc.Foreground}");

            ImGui.Separator();
            ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.3f, 1.0f), "=== STATIC ADDRESSES ===");
            foreach (var kv in proc.StaticAddresses)
            {
                ImGuiHelper.IntPtrToImGui(kv.Key, kv.Value);
            }

            ImGui.Separator();
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), "=== GGPK CACHES ===");
            Core.CacheImGui();

            ImGui.Separator();
            ImGui.TextColored(new Vector4(1.0f, 0.5f, 0.8f, 1.0f), "=== MEMORY DIAGNOSTICS & SETTINGS ===");
            if (ImGui.Button("Open Full Memory Read Diagnostics Window"))
            {
                Core.GHSettings.ShowMemoryDiagnostics = true;
            }

            ImGui.Checkbox("Enable New Memory Read Pipeline", ref Core.GHSettings.EnableNewMemoryRead);
            ImGui.Checkbox("Enable Stale Entity Cleanup", ref Core.GHSettings.EnableStaleEntityCleanup);
            ImGui.Checkbox("Hide Overlays When Game Inactive", ref Core.GHSettings.HideOverlaysWhenGameInactive);
        }

        // ==========================================
        // TAB 7: UI Explorer
        // ==========================================
        private static void DrawUiExplorerTab()
        {
            var inGame = Core.States.InGameStateObject;
            var gameUi = inGame.GameUi;

            ImGui.Text($"UiRoot Address: 0x{inGame.UiRootAddress.ToInt64():X} | GameUi Address: 0x{gameUi.Address.ToInt64():X}");
            ImGui.Text($"Controller Mode: {Core.GHSettings.EnableControllerMode}");

            ImGui.Separator();
            if (ImGui.TreeNode("Important UI Elements Panels"))
            {
                gameUi.ToImGui();
                ImGui.TreePop();
            }

            if (gameUi.Address != IntPtr.Zero)
            {
                var reader = Core.Process.Handle;
                var rootElem = reader.ReadMemory<UiElementBaseOffset>(gameUi.Address);
                var childCount = (int)rootElem.ChildrensPtr.TotalElements(8);
                ImGui.Text($"Top-level UI Children Count: {childCount}");

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
                            ImGui.Text($"Flags: 0x{childElem.Flags:X8} | Visible: {isVisible}");
                            ImGui.Text($"RelPos: ({childElem.RelativePosition.X}, {childElem.RelativePosition.Y}) | UnscaledSize: ({childElem.UnscaledSize.X} x {childElem.UnscaledSize.Y})");
                            ImGui.Text($"Children: {childElem.ChildrensPtr.TotalElements(8)}");
                        }
                    }
                }
            }
        }

        // ==========================================
        // TAB 8: Components
        // ==========================================
        private static void DrawComponentsTab()
        {
            var inGame = Core.States.InGameStateObject;
            var player = inGame.CurrentAreaInstance.Player;
            var mouseOver = inGame.MouseOverEntity;

            ImGui.InputTextWithHint("##CompSearch", "Filter component names...", ref componentSearchFilter, 50);

            ImGui.Separator();
            if (ImGui.CollapsingHeader("Local Player Components", ImGuiTreeNodeFlags.DefaultOpen))
            {
                if (player.IsValid)
                {
                    DrawEntityComponents(player);
                }
                else
                {
                    ImGui.TextDisabled("Local player not available.");
                }
            }

            if (ImGui.CollapsingHeader("Mouse-Over Entity Components"))
            {
                if (mouseOver.IsValid)
                {
                    ImGui.Text($"Hovered Entity ID: {mouseOver.Id} | Path: {mouseOver.Path}");
                    DrawEntityComponents(mouseOver);
                }
                else
                {
                    ImGui.TextDisabled("No entity currently hovered under mouse cursor.");
                }
            }
        }

        private static void DrawEntityComponents(Entity entity)
        {
            var componentNames = entity.GetComponentNames();
            if (componentNames == null) return;

            foreach (var name in componentNames)
            {
                if (!string.IsNullOrEmpty(componentSearchFilter) &&
                    !name.Contains(componentSearchFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (ImGui.TreeNode(name))
                {
                    switch (name)
                    {
                        case "Life":
                            if (entity.TryGetComponent<Life>(out var life)) life.ToImGui();
                            break;
                        case "Render":
                            if (entity.TryGetComponent<Render>(out var render)) render.ToImGui();
                            break;
                        case "Actor":
                            if (entity.TryGetComponent<Actor>(out var actor)) actor.ToImGui();
                            break;
                        case "Buffs":
                            if (entity.TryGetComponent<Buffs>(out var buffs)) buffs.ToImGui();
                            break;
                        case "Stats":
                            if (entity.TryGetComponent<Stats>(out var stats)) stats.ToImGui();
                            break;
                        case "Positioned":
                            if (entity.TryGetComponent<Positioned>(out var pos)) pos.ToImGui();
                            break;
                        case "ObjectMagicProperties":
                            if (entity.TryGetComponent<ObjectMagicProperties>(out var omp)) omp.ToImGui();
                            break;
                        case "StateMachine":
                            if (entity.TryGetComponent<StateMachine>(out var sm)) sm.ToImGui();
                            break;
                        default:
                            ImGui.TextDisabled($"No custom ImGui widget implemented for {name}.");
                            break;
                    }

                    ImGui.TreePop();
                }
            }
        }

        // ==========================================
        // TAB 9: Render
        // ==========================================
        private static void DrawRenderTab()
        {
            var inGame = Core.States.InGameStateObject;
            var world = inGame.CurrentWorldInstance;

            ImGui.TextColored(new Vector4(0.3f, 0.9f, 1.0f, 1.0f), "=== GAME VIEWPORT & RESOLUTION ===");
            ImGui.Text($"Window Area: {Core.Process.WindowArea}");
            ImGui.Text($"Overlay Size: {Core.Overlay.Size}");

            ImGui.Separator();
            ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.3f, 1.0f), "=== WORLD TO SCREEN MATRIX ===");
            world.ToImGui();

            ImGui.Separator();
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), "=== WORLD TO SCREEN TESTER ===");
            ImGui.InputFloat3("World Coords (X, Y, Z)", ref testWorldCoords);
            var screenPos = world.WorldToScreen(new StdTuple3D<float> { X = testWorldCoords.X, Y = testWorldCoords.Y, Z = testWorldCoords.Z });
            ImGui.Text($"Calculated Screen Pos: ({screenPos.X:F1}, {screenPos.Y:F1})");
        }

        // ==========================================
        // TAB 10: Terrain
        // ==========================================
        private static void DrawTerrainTab()
        {
            var area = Core.States.InGameStateObject.CurrentAreaInstance;
            var metadata = area.TerrainMetadata;

            ImGui.TextColored(new Vector4(0.3f, 0.9f, 1.0f, 1.0f), "=== TERRAIN METADATA ===");
            ImGui.Text($"Total Tiles: {metadata.TotalTiles}");
            ImGuiHelper.IntPtrToImGui("Tile Details Pointer", metadata.TileDetailsPtr.First);
            ImGui.Text($"Tile Height Multiplier: {metadata.TileHeightMultiplier}");
            ImGui.Text($"Grid Walkable Data Size: {area.GridWalkableData.Length} bytes");
            ImGui.Text($"Grid Height Data Rows: {area.GridHeightData.Length}");
            ImGui.Text($"Tgt Named Tile Locations: {area.TgtTilesLocations.Count}");

            ImGui.Separator();
            if (ImGui.TreeNode($"All Tgt Tile Types ({area.TgtTilesLocations.Count})"))
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

                ImGui.TreePop();
            }
        }

        // ==========================================
        // TAB 11: Events
        // ==========================================
        private static void DrawEventsTab()
        {
            ImGui.TextColored(new Vector4(0.3f, 0.9f, 1.0f, 1.0f), "=== COROUTINES & EVENT TICKS ===");
            ImGui.Text("Active Core Events & Ticks:");
            ImGui.BulletText("OnRender: UI Rendering Loop (Overlay FPS)");
            ImGui.BulletText("PerFrameDataUpdate: Memory entities and player state scanning");
            ImGui.BulletText("PostPerFrameDataUpdate: WorldToScreen matrix and camera tracking");
            ImGui.BulletText("OnAreaChange: Area instance loading trigger");

            ImGui.Separator();
            ImGui.Text("Memory Diagnostics Status:");
            ImGui.Text($"Is Recording: {MemoryReadDiagnostics.IsRecording}");
        }

        // ==========================================
        // TAB 12: Skills & Timing
        // ==========================================
        private static void DrawSkillsAndTimingTab()
        {
            var player = Core.States.InGameStateObject.CurrentAreaInstance.Player;
            if (!player.IsValid || !player.TryGetComponent<Actor>(out var actor))
            {
                ImGui.TextDisabled("Actor component unavailable on local player.");
                return;
            }

            ImGui.TextColored(new Vector4(0.3f, 0.9f, 1.0f, 1.0f), "=== PLAYER ACTOR & ACTIVE SKILLS ===");
            ImGui.Text($"Current Animation: {actor.Animation}");
            ImGui.Text($"Total Active Skills: {actor.ActiveSkills.Count}");

            if (ImGui.BeginTable("SkillsTable", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
            {
                ImGui.TableSetupColumn("Skill Name", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Usable?", ImGuiTableColumnFlags.WidthFixed, 80);
                ImGui.TableSetupColumn("Use Stage", ImGuiTableColumnFlags.WidthFixed, 90);
                ImGui.TableSetupColumn("Cast Type", ImGuiTableColumnFlags.WidthFixed, 100);
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
    }
}
