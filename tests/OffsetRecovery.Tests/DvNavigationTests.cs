// <copyright file="DvNavigationTests.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace OffsetRecovery.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
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

            // 2. Metadata-Only Search Does NOT Call Object Resolvers
            int countingResolverCalls = 0;
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
            };

            var testCandidates = new List<DvNavNode> { testNodeWithCountingResolver };
            var searchResults = DvSearchEngine.Search("counting", testCandidates);
            check(searchResults.Count == 1 && searchResults[0].Id == "test.counting",
                "Search must find test node by keyword 'counting'.");
            check(countingResolverCalls == 0,
                $"Search query execution MUST NOT invoke ObjectResolver (resolver calls = {countingResolverCalls}).");

            // 3. Search Aliases & Tags Matching
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

            // 4. Gold Navigation Points to ServerData (Single Native SDK Authority)
            var goldNode = DvRegistry.FindById("serverdata.gold");
            check(goldNode != null, "'serverdata.gold' node must exist in registry.");
            check(goldNode!.Path.Contains("ServerData"), "'serverdata.gold' path must route to ServerData.");

            // 5. Navigation Context & History (Back / Forward)
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

            // 6. Recent List Deduplication & Ordering
            check(nav.RecentNodeIds[0] == "core.loaded_files", "Most recent item must be at index 0.");
            check(nav.RecentNodeIds.Count(id => id == "player.life") == 1, "Recent items must be deduplicated.");

            // 7. Favorites Management (Stable Node IDs)
            check(nav.IsFavorite("player.life"), "'player.life' is in default favorites.");
            nav.ToggleFavorite("player.life");
            check(!nav.IsFavorite("player.life"), "ToggleFavorite must remove 'player.life'.");
            nav.ToggleFavorite("player.life");
            check(nav.IsFavorite("player.life"), "ToggleFavorite must re-add 'player.life'.");

            // 8. Unavailable Resolver Does Not Throw or Break
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
        }
    }
}