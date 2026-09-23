// <copyright file="DataVisualization.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace TEHhub.Ui
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Numerics;
    using Coroutine;
    using CoroutineEvents;
    using ImGuiNET;
    using TEHhub.Ui.DvEngine;
    using TEHhub.Utils;

    /// <summary>
    ///     Visualizes remote objects, memory caches, offsets, and game state.
    ///     DV v2 provides instant search/jump, history (Back/Forward), pinned favorites,
    ///     and direct inspector views while preserving the complete legacy DV hierarchy.
    /// </summary>
    public static class DataVisualization
    {
        private static readonly DvNavigationContext NavContext = new(
            "serverdata.gold",
            Core.GHSettings.DataVisualizationFavoriteNodeIds);
        private static string searchQuery = string.Empty;

        /// <summary>
        ///     Initializes the co-routines.
        /// </summary>
        internal static void InitializeCoroutines()
        {
            CoroutineHandler.Start(DataVisualizationRenderCoRoutine(), priority: UiRenderPriority.CoreWindows);
        }

        /// <summary>
        ///     Draws the window for Data Visualization (DV v2).
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

                ImGui.SetNextWindowSize(new Vector2(950, 620), ImGuiCond.FirstUseEver);
                if (ImGui.Begin("Data Visualization (DV v2)###DataVisualizationWindow", ref Core.GHSettings.ShowDataVisualization))
                {
                    RenderTopBar();
                    ImGui.Separator();
                    RenderSplitContent();
                }

                ImGui.End();
            }
        }

        private static void RenderTopBar()
        {
            // 1. Back / Forward History Controls
            if (!NavContext.CanGoBack)
            {
                ImGuiHelper.DrawDisabledButton(" < Back ");
            }
            else
            {
                if (ImGui.Button(" < Back "))
                {
                    NavContext.GoBack();
                }
            }

            ImGui.SameLine();
            if (!NavContext.CanGoForward)
            {
                ImGuiHelper.DrawDisabledButton(" Forward > ");
            }
            else
            {
                if (ImGui.Button(" Forward > "))
                {
                    NavContext.GoForward();
                }
            }

            // 2. Search / Jump Input
            ImGui.SameLine(0, 15f);
            ImGui.PushItemWidth(260f);
            ImGui.InputTextWithHint("##DvSearch", "Search / Jump (e.g. hp, gold, buffs)...", ref searchQuery, 100);
            ImGui.PopItemWidth();

            if (!string.IsNullOrEmpty(searchQuery))
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("X##ClearSearch"))
                {
                    searchQuery = string.Empty;
                }
            }

            // 3. Breadcrumb Trail
            var currentNode = DvRegistry.FindById(NavContext.SelectedNodeId);
            if (currentNode != null)
            {
                ImGui.SameLine(0, 20f);
                ImGui.TextColored(new Vector4(0.6f, 0.8f, 1f, 1f), $"Path: {currentNode.Path}");
            }
        }

        private static void RenderSplitContent()
        {
            var avail = ImGui.GetContentRegionAvail();
            var leftWidth = Math.Clamp(avail.X * 0.32f, 260f, 360f);

            // If a search query is active, show search results in the left pane
            if (!string.IsNullOrWhiteSpace(searchQuery))
            {
                RenderSearchResultsPane(leftWidth);
            }
            else
            {
                RenderNavigatorPane(leftWidth);
            }

            ImGui.SameLine();

            // Right Pane: Inspector
            if (ImGui.BeginChild("##DvInspectorPane", new Vector2(0, 0), ImGuiChildFlags.Borders))
            {
                RenderInspectorContent();
            }

            ImGui.EndChild();
        }

        private static void RenderSearchResultsPane(float width)
        {
            if (ImGui.BeginChild("##DvSearchPane", new Vector2(width, 0), ImGuiChildFlags.Borders))
            {
                ImGui.TextColored(new Vector4(1f, 0.84f, 0f, 1f), $"Search Results for '{searchQuery}':");
                ImGui.Separator();

                var results = DvSearchEngine.Search(searchQuery);
                if (results.Count == 0)
                {
                    ImGui.TextDisabled("No matching navigation targets found.");
                }
                else
                {
                    foreach (var node in results)
                    {
                        var isSelected = string.Equals(NavContext.SelectedNodeId, node.Id, StringComparison.OrdinalIgnoreCase);
                        if (ImGui.Selectable($"{node.DisplayName}##search_{node.Id}", isSelected))
                        {
                            NavContext.NavigateTo(node.Id);
                            searchQuery = string.Empty;
                        }

                        ImGui.TextDisabled($"  [{node.Category}] {node.Path}");
                        ImGui.Spacing();
                    }
                }
            }

            ImGui.EndChild();
        }

        private static void RenderNavigatorPane(float width)
        {
            if (ImGui.BeginChild("##DvNavPane", new Vector2(width, 0), ImGuiChildFlags.Borders))
            {
                if (ImGui.BeginTabBar("##DvNavTabs"))
                {
                    // Tab 1: Favorites
                    if (ImGui.BeginTabItem("Favorites##DvTabFav"))
                    {
                        RenderFavoritesTab();
                        ImGui.EndTabItem();
                    }

                    // Tab 2: Recent
                    if (ImGui.BeginTabItem("Recent##DvTabRec"))
                    {
                        RenderRecentTab();
                        ImGui.EndTabItem();
                    }

                    // Tab 3: Browse
                    if (ImGui.BeginTabItem("Browse##DvTabBrowse"))
                    {
                        RenderBrowseTab();
                        ImGui.EndTabItem();
                    }

                    // Tab 4: Legacy
                    if (ImGui.BeginTabItem("Legacy##DvTabLeg"))
                    {
                        RenderLegacyTab();
                        ImGui.EndTabItem();
                    }

                    ImGui.EndTabBar();
                }
            }

            ImGui.EndChild();
        }

        private static void RenderFavoritesTab()
        {
            ImGui.TextDisabled("Pinned destinations for 1-click access:");
            ImGui.Separator();

            if (NavContext.FavoriteNodeIds.Count == 0)
            {
                ImGui.TextDisabled("No favorites pinned yet.");
                ImGui.TextDisabled("Click [Pin Favorite] on any node to pin it here.");
                return;
            }

            foreach (var id in NavContext.FavoriteNodeIds)
            {
                var node = DvRegistry.FindById(id);
                if (node == null) continue;

                var isSelected = string.Equals(NavContext.SelectedNodeId, node.Id, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable($"{node.DisplayName}##fav_{node.Id}", isSelected))
                {
                    NavContext.NavigateTo(node.Id);
                }

                if (ImGui.IsItemHovered())
                {
                    ImGuiHelper.ToolTip($"Path: {node.Path}\nCategory: {node.Category}");
                }
            }
        }

        private static void RenderRecentTab()
        {
            ImGui.TextDisabled("Recently visited destinations:");
            ImGui.Separator();

            if (NavContext.RecentNodeIds.Count == 0)
            {
                ImGui.TextDisabled("No recent history.");
                return;
            }

            foreach (var id in NavContext.RecentNodeIds)
            {
                var node = DvRegistry.FindById(id);
                if (node == null) continue;

                var isSelected = string.Equals(NavContext.SelectedNodeId, node.Id, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable($"{node.DisplayName}##rec_{node.Id}", isSelected))
                {
                    NavContext.NavigateTo(node.Id);
                }

                if (ImGui.IsItemHovered())
                {
                    ImGuiHelper.ToolTip($"Path: {node.Path}\nCategory: {node.Category}");
                }
            }
        }

        private static void RenderBrowseTab()
        {
            ImGui.TextDisabled("Categorized static destinations:");
            ImGui.Separator();

            foreach (var (category, nodes) in DvRegistry.Categories)
            {
                if (ImGui.TreeNode($"{category} ({nodes.Count})###cat_{category}"))
                {
                    foreach (var node in nodes)
                    {
                        var isSelected = string.Equals(NavContext.SelectedNodeId, node.Id, StringComparison.OrdinalIgnoreCase);
                        if (ImGui.Selectable($"  {node.DisplayName}##browse_{node.Id}", isSelected))
                        {
                            NavContext.NavigateTo(node.Id);
                        }

                        if (ImGui.IsItemHovered())
                        {
                            ImGuiHelper.ToolTip($"Path: {node.Path}\nTags: {string.Join(", ", node.Tags)}");
                        }
                    }

                    ImGui.TreePop();
                }
            }
        }

        private static void RenderLegacyTab()
        {
            ImGui.TextDisabled("Legacy Full Tree View:");
            ImGui.Separator();

            var isLegacySelected = string.Equals(NavContext.SelectedNodeId, "legacy.all", StringComparison.OrdinalIgnoreCase);
            if (ImGui.Selectable("Open Full Legacy Hierarchy View", isLegacySelected))
            {
                NavContext.NavigateTo("legacy.all");
            }

            ImGui.Spacing();
            ImGui.TextWrapped("The Legacy view renders the complete original DV tree hierarchy, including all collapsing headers, nested reflection trees, and debug actions.");
        }

        private static void RenderInspectorContent()
        {
            var node = DvRegistry.FindById(NavContext.SelectedNodeId);
            if (node == null)
            {
                ImGui.TextDisabled($"Selected target '{NavContext.SelectedNodeId}' is not found in registry.");
                return;
            }

            // Target Header Info Bar
            ImGui.TextColored(new Vector4(0.2f, 1f, 0.4f, 1f), node.DisplayName);
            ImGui.SameLine();
            ImGui.TextDisabled($"({node.Category})");

            var isFav = NavContext.IsFavorite(node.Id);
            ImGui.SameLine(ImGui.GetContentRegionAvail().X - 180f);
            if (ImGui.SmallButton(isFav ? "★ Unpin Favorite##favBtn" : "☆ Pin Favorite##favBtn"))
            {
                NavContext.ToggleFavorite(node.Id);
                Core.GHSettings.DataVisualizationFavoriteNodeIds = NavContext.FavoriteNodeIds.ToList();
                CoroutineHandler.RaiseEvent(TEHhubEvents.TimeToSaveAllSettings);
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("Copy ID##copyId"))
            {
                ImGui.SetClipboardText(node.Id);
            }

            ImGui.TextDisabled($"Path: {node.Path}");
            ImGui.Separator();

            // Render Body
            if (node.Kind == DvNodeKind.Legacy)
            {
                RenderLegacyBody();
                return;
            }

            if (node.Kind == DvNodeKind.CustomRenderer || node.Kind == DvNodeKind.Action)
            {
                node.CustomRenderer?.Invoke();
                return;
            }

            if (node.Kind == DvNodeKind.Container && node.CustomRenderer != null)
            {
                node.CustomRenderer.Invoke();
                return;
            }

            if (node.Kind == DvNodeKind.RemoteObject || node.Kind == DvNodeKind.Container)
            {
                var remoteObj = node.ObjectResolver?.Invoke();
                if (remoteObj != null && remoteObj.Address != IntPtr.Zero)
                {
                    remoteObj.ToImGui();
                }
                else
                {
                    ImGui.Spacing();
                    ImGui.TextColored(new Vector4(1f, 0.6f, 0.2f, 1f), "Target object is currently unavailable in game memory.");
                    ImGui.TextDisabled("Reason: Game process is not attached, player is not in an active zone, or the component is not present on player.");
                    ImGui.Spacing();
                    if (node.Id.StartsWith("player.", StringComparison.OrdinalIgnoreCase))
                    {
                        ImGui.TextWrapped("Once you load into a map with your character, this component data will populate automatically.");
                    }
                }
            }
        }

        /// <summary>
        ///     Renders the settings content exactly as preserved from pre-DV-v2.
        /// </summary>
        internal static void RenderSettingsContent()
        {
            var fields = Core.GHSettings.GetType().GetFields().ToList();
            for (var i = 0; i < fields.Count; i++)
            {
                var field = fields[i];
                ImGui.Text($"{field.Name}: {field.GetValue(Core.GHSettings)}");
            }

            ImGui.Text($"Current Window Size:{Core.Overlay.Size}");
            ImGui.Text($"Current Window Pos: {Core.Overlay.Position}");
        }

        /// <summary>
        ///     Renders the game process and static addresses content exactly as preserved from pre-DV-v2.
        /// </summary>
        internal static void RenderGameProcessContent()
        {
            if (Core.Process.Address != IntPtr.Zero)
            {
                ImGuiHelper.IntPtrToImGui("Base Address", Core.Process.Address);
                ImGui.Text($"Process: {Core.Process.Information}");
                ImGui.Text($"WindowArea: {Core.Process.WindowArea}");
                ImGui.Text($"Foreground: {Core.Process.Foreground}");
                if (ImGui.TreeNode("Static Addresses"))
                {
                    foreach (var saddr in Core.Process.StaticAddresses)
                    {
                        ImGuiHelper.IntPtrToImGui(saddr.Key, saddr.Value);
                    }

                    ImGui.TreePop();
                }
            }
            else
            {
                ImGui.Text("Game not found.");
            }
        }

        /// <summary>
        ///     Renders the complete original legacy DV body.
        ///     Preserves 100% of the old layout, collapsing headers, reflection trees, and diagnostic tools.
        /// </summary>
        internal static void RenderLegacyBody()
        {
            if (ImGui.CollapsingHeader("Settings"))
            {
                RenderSettingsContent();
            }

            Core.CacheImGui();

            if (ImGui.CollapsingHeader("Game Process"))
            {
                RenderGameProcessContent();
            }

            Core.RemoteObjectsToImGuiCollapsingHeader();
        }
    }
}
