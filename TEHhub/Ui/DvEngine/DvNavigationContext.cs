// <copyright file="DvNavigationContext.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace TEHhub.Ui.DvEngine
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    ///     Manages active navigation state, history (Back/Forward), recent destinations,
    ///     and pinned favorites for DV v2.
    ///     Operates purely on stable node IDs without holding live object references or remote pointers.
    /// </summary>
    public sealed class DvNavigationContext
    {
        private const int MaxRecentCount = 15;

        private readonly List<string> history = new();
        private int historyIndex = -1;
        private readonly List<string> recentNodeIds = new();
        private readonly HashSet<string> favoriteNodeIds = new(StringComparer.OrdinalIgnoreCase)
        {
            "player.life",
            "player.buffs",
            "serverdata.gold",
            "area.modifiers",
            "core.loaded_files",
            "legacy.all",
        };

        /// <summary>
        ///     Initializes a new instance of the <see cref="DvNavigationContext"/> class.
        /// </summary>
        /// <param name="initialNodeId">Default starting navigation target.</param>
        public DvNavigationContext(string initialNodeId = "serverdata.gold")
        {
            this.SelectedNodeId = initialNodeId;
            this.history.Add(initialNodeId);
            this.historyIndex = 0;
            this.recentNodeIds.Add(initialNodeId);
        }

        /// <summary>
        ///     Gets the currently selected node identifier.
        /// </summary>
        public string SelectedNodeId { get; private set; }

        /// <summary>
        ///     Gets a value indicating whether backward navigation is possible.
        /// </summary>
        public bool CanGoBack => this.historyIndex > 0;

        /// <summary>
        ///     Gets a value indicating whether forward navigation is possible.
        /// </summary>
        public bool CanGoForward => this.historyIndex >= 0 && this.historyIndex < this.history.Count - 1;

        /// <summary>
        ///     Gets the list of recently visited node IDs (most recent first).
        /// </summary>
        public IReadOnlyList<string> RecentNodeIds => this.recentNodeIds;

        /// <summary>
        ///     Gets the set of pinned favorite node IDs.
        /// </summary>
        public IReadOnlySet<string> FavoriteNodeIds => this.favoriteNodeIds;

        /// <summary>
        ///     Navigates to the specified target node ID.
        /// </summary>
        /// <param name="nodeId">Stable target identifier.</param>
        public void NavigateTo(string nodeId)
        {
            if (string.IsNullOrWhiteSpace(nodeId))
            {
                return;
            }

            if (string.Equals(this.SelectedNodeId, nodeId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // If navigating after Back, truncate forward history
            if (this.historyIndex >= 0 && this.historyIndex < this.history.Count - 1)
            {
                this.history.RemoveRange(this.historyIndex + 1, this.history.Count - (this.historyIndex + 1));
            }

            this.history.Add(nodeId);
            this.historyIndex = this.history.Count - 1;
            this.SelectedNodeId = nodeId;

            // Update Recent
            this.recentNodeIds.RemoveAll(id => string.Equals(id, nodeId, StringComparison.OrdinalIgnoreCase));
            this.recentNodeIds.Insert(0, nodeId);
            if (this.recentNodeIds.Count > MaxRecentCount)
            {
                this.recentNodeIds.RemoveAt(this.recentNodeIds.Count - 1);
            }
        }

        /// <summary>
        ///     Navigates back to the previous target in history.
        /// </summary>
        public void GoBack()
        {
            if (!this.CanGoBack)
            {
                return;
            }

            this.historyIndex--;
            this.SelectedNodeId = this.history[this.historyIndex];
        }

        /// <summary>
        ///     Navigates forward to the next target in history.
        /// </summary>
        public void GoForward()
        {
            if (!this.CanGoForward)
            {
                return;
            }

            this.historyIndex++;
            this.SelectedNodeId = this.history[this.historyIndex];
        }

        /// <summary>
        ///     Toggles a node's favorite status.
        /// </summary>
        public void ToggleFavorite(string nodeId)
        {
            if (this.favoriteNodeIds.Contains(nodeId))
            {
                this.favoriteNodeIds.Remove(nodeId);
            }
            else
            {
                this.favoriteNodeIds.Add(nodeId);
            }
        }

        /// <summary>
        ///     Checks whether a node is pinned as favorite.
        /// </summary>
        public bool IsFavorite(string nodeId)
        {
            return this.favoriteNodeIds.Contains(nodeId);
        }
    }
}