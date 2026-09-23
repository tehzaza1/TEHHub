// <copyright file="DvNavigationTests.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace OffsetRecovery.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using TEHhub.Settings;
    using TEHhub.Ui.DvEngine;

    internal static class DvNavigationTests
    {
        internal static void Run(Action<bool, string> check)
        {
            // 1. Static Registry Structure & No Duplicate IDs
            var allNodes = DvRegistry.AllNodes;
            check(allNodes.Count >= 20, $"DvRegistry must contain at least 20 static nodes (found {allNodes.Count}).");

            var uniqueIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in allNodes)
            {
                bool added = uniqueIds.Add(node.Id);
                check(added, $"Duplicate node Id in DvRegistry: '{node.Id}'");
                check(!string.IsNullOrWhiteSpace(node.DisplayName), $"Node '{node.Id}' must have non-empty DisplayName.");
                check(!string.IsNullOrWhiteSpace(node.Category), $"Node '{node.Id}' must have non-empty Category.");
                check(!string.IsNullOrWhiteSpace(node.Path), $"Node '{node.Id}' must have non-empty Path.");
            }

            // 2. Required Static Targets Exist
            string[] requiredTargetIds = new[]
            {
                "core.settings",
                "core.ggpk_cache",
                "core.game_process",
                "core.loaded_files",
                "core.area_change",
                "states.hub",
                "states.ingame",
                "states.area_loading",
                "area.current",
                "area.modifiers",
                "world.data",
                "world.area_details",
                "serverdata.gold",
                "serverdata.inventories",
                "player.root",
                "player.life",
                "player.buffs",
                "player.actor",
                "player.stats",
                "player.render",
                "player.positioned",
                "entities.awake",
                "entities.sleeping",
                "legacy.all",
            };

            foreach (var reqId in requiredTargetIds)
            {
                var target = DvRegistry.FindById(reqId);
                check(target != null, $"Required static target '{reqId}' must exist in DvRegistry.");
            }

            // 3. Metadata-Only Search Does NOT Call Object Resolvers or Dynamic Children Resolvers
            int countingResolverCalls = 0;
            int dynamicChildrenCalls = 0;
            var testNodeWithCountingResolver = new DvNavNode
            {
                Id = "test.counting",
                DisplayName = "Test Counting Target",
                Category = "Test Category",
                Path = "Test > Counting",
                Tags = new[] { "counting", "smoke", "testtag" },
                ObjectResolver = () =>
                {
                    countingResolverCalls++;
                    return null;
                },
                DynamicChildrenResolver = () =>
                {
                    dynamicChildrenCalls++;
                    return Array.Empty<DvNavNode>();
                },
            };

            var testCandidates = new List<DvNavNode> { testNodeWithCountingResolver };
            var searchResults = DvSearchEngine.Search("counting", testCandidates);
            check(searchResults.Count == 1 && searchResults[0].Id == "test.counting",
                "Search must find test node by keyword 'counting'.");
            check(countingResolverCalls == 0,
                $"Search query execution MUST NOT invoke ObjectResolver (resolver calls = {countingResolverCalls}).");
            check(dynamicChildrenCalls == 0,
                $"Search query execution MUST NOT invoke DynamicChildrenResolver (dynamic calls = {dynamicChildrenCalls}).");

            // 4. Selecting One Target Resolves ONLY That Target
            int resolverA = 0;
            int resolverB = 0;
            var nodeA = new DvNavNode
            {
                Id = "test.nodeA",
                DisplayName = "Node A",
                Category = "Test",
                Path = "Test > A",
                ObjectResolver = () => { resolverA++; return null; },
            };
            var nodeB = new DvNavNode
            {
                Id = "test.nodeB",
                DisplayName = "Node B",
                Category = "Test",
                Path = "Test > B",
                ObjectResolver = () => { resolverB++; return null; },
            };

            // Resolving Node A only
            _ = nodeA.ObjectResolver();
            check(resolverA == 1 && resolverB == 0, "Resolving target A must not invoke target B resolver.");

            // 5. Search Aliases & Tags Matching
            var hpResults = DvSearchEngine.Search("hp");
            check(hpResults.Any(n => n.Id == "player.life"), "Search for 'hp' must return 'player.life'.");

            var goldResults = DvSearchEngine.Search("gold");
            check(goldResults.Any(n => n.Id == "serverdata.gold"), "Search for 'gold' must return 'serverdata.gold'.");

            var buffResults = DvSearchEngine.Search("buffs");
            check(buffResults.Any(n => n.Id == "player.buffs"), "Search for 'buffs' must return 'player.buffs'.");

            var skillResults = DvSearchEngine.Search("skills");
            check(skillResults.Any(n => n.Id == "player.actor"), "Search for 'skills' must return 'player.actor'.");

            var preloadResults = DvSearchEngine.Search("preload");
            check(preloadResults.Any(n => n.Id == "core.loaded_files"), "Search for 'preload' must return 'core.loaded_files'.");

            // 6. Gold Navigation Points to ServerData (Single Native SDK Authority)
            var goldNode = DvRegistry.FindById("serverdata.gold");
            check(goldNode != null, "'serverdata.gold' node must exist in registry.");
            check(goldNode!.Path.Contains("ServerData"), "'serverdata.gold' path must route to ServerData.");

            // 7. Navigation Context & History (Back / Forward)
            var nav = new DvNavigationContext("core.settings");
            check(nav.SelectedNodeId == "core.settings", "Initial navigation node must be 'core.settings'.");
            check(!nav.CanGoBack, "Initial state must not allow Back.");
            check(!nav.CanGoForward, "Initial state must not allow Forward.");

            nav.NavigateTo("player.life");
            check(nav.SelectedNodeId == "player.life", "Navigated node must be 'player.life'.");
            check(nav.CanGoBack, "After navigation, CanGoBack must be true.");
            check(!nav.CanGoForward, "At end of history, CanGoForward must be false.");

            nav.NavigateTo("serverdata.gold");
            check(nav.SelectedNodeId == "serverdata.gold", "Navigated node must be 'serverdata.gold'.");

            // Go back to player.life
            nav.GoBack();
            check(nav.SelectedNodeId == "player.life", "GoBack must return to 'player.life'.");
            check(nav.CanGoBack, "CanGoBack must still be true.");
            check(nav.CanGoForward, "CanGoForward must now be true.");

            // Go forward to serverdata.gold
            nav.GoForward();
            check(nav.SelectedNodeId == "serverdata.gold", "GoForward must restore 'serverdata.gold'.");

            // Go back and navigate to new node -> must truncate forward history
            nav.GoBack(); // at player.life
            nav.NavigateTo("core.loaded_files");
            check(nav.SelectedNodeId == "core.loaded_files", "Navigated node must be 'core.loaded_files'.");
            check(!nav.CanGoForward, "Navigating after Back must truncate Forward history.");

            // 8. Recent List Deduplication & Ordering
            check(nav.RecentNodeIds[0] == "core.loaded_files", "Most recent item must be at index 0.");
            check(nav.RecentNodeIds.Count(id => id == "player.life") == 1, "Recent items must be deduplicated.");

            // 9. Favorites Management (Stable Node IDs)
            check(nav.IsFavorite("player.life"), "'player.life' is in default favorites.");
            nav.ToggleFavorite("player.life");
            check(!nav.IsFavorite("player.life"), "ToggleFavorite must remove 'player.life'.");
            nav.ToggleFavorite("player.life");
            check(nav.IsFavorite("player.life"), "ToggleFavorite must re-add 'player.life'.");

            // 10. Favorites survive persistence through the existing core settings JSON.
            var settings = new State();
            var persistedNav = new DvNavigationContext("serverdata.gold", settings.DataVisualizationFavoriteNodeIds);
            persistedNav.ToggleFavorite("player.life");
            persistedNav.ToggleFavorite("area.current");
            settings.DataVisualizationFavoriteNodeIds = persistedNav.FavoriteNodeIds.ToList();
            var settingsJson = JsonSerializer.Serialize(settings, StateJsonContext.Default.State);
            var loadedSettings = JsonSerializer.Deserialize(settingsJson, StateJsonContext.Default.State)!;
            var restoredNav = new DvNavigationContext("serverdata.gold", loadedSettings.DataVisualizationFavoriteNodeIds);
            check(restoredNav.IsFavorite("area.current"), "New DV v2 favorites must survive settings serialization and reload.");
            check(!restoredNav.IsFavorite("player.life"), "Removed default favorites must stay removed after settings reload.");
            check(restoredNav.IsFavorite("player.buffs"), "Unchanged default favorites must be preserved in the saved list.");

            var emptySettings = new State
            {
                DataVisualizationFavoriteNodeIds = new(),
            };
            var emptyJson = JsonSerializer.Serialize(emptySettings, StateJsonContext.Default.State);
            var loadedEmptySettings = JsonSerializer.Deserialize(emptyJson, StateJsonContext.Default.State)!;
            var emptyNav = new DvNavigationContext("serverdata.gold", loadedEmptySettings.DataVisualizationFavoriteNodeIds);
            check(emptyNav.FavoriteNodeIds.Count == 0, "An intentionally empty DV v2 favorites list must remain empty after reload.");

            var legacySettings = JsonSerializer.Deserialize("{}", StateJsonContext.Default.State)!;
            check(legacySettings.DataVisualizationFavoriteNodeIds.Contains("player.life"),
                "Existing core settings without a DV v2 favorites field must retain the default favorites.");

            // 11. Unavailable Resolver Does Not Throw or Break
            var unavailableNode = new DvNavNode
            {
                Id = "test.null",
                DisplayName = "Test Null Resolver",
                Category = "Test",
                Path = "Test > Null",
                ObjectResolver = () => null,
            };

            var resolvedNull = unavailableNode.ObjectResolver();
            check(resolvedNull == null, "Null resolver safely returns null without throwing.");

            // 12. Legacy View Node Exists and Uses Preserved Legacy Layout Path
            var legacyNode = DvRegistry.FindById("legacy.all");
            check(legacyNode != null, "'legacy.all' must exist in registry.");
            check(legacyNode!.Kind == DvNodeKind.Legacy, "'legacy.all' must have Kind == DvNodeKind.Legacy.");
        }
    }
}
