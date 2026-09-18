// <copyright file="DvNode.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace TEHhub.Ui.DvEngine
{
    using System;
    using TEHhub.RemoteObjects;

    /// <summary>
    ///     Specifies the rendering and resolution strategy for a navigation node in DV v2.
    /// </summary>
    public enum DvNodeKind
    {
        /// <summary>
        ///     Standard RemoteObject whose ToImGui() method is invoked when resolved.
        /// </summary>
        RemoteObject,

        /// <summary>
        ///     Custom renderer delegate (e.g. Settings, GGPK Cache, Process info).
        /// </summary>
        CustomRenderer,

        /// <summary>
        ///     Collection or container of child entities / elements.
        /// </summary>
        Container,

        /// <summary>
        ///     Executable command or action trigger.
        /// </summary>
        Action,

        /// <summary>
        ///     Legacy view that renders the complete original DV hierarchy.
        /// </summary>
        Legacy,
    }

    /// <summary>
    ///     Represents a metadata descriptor for a navigation target in DV v2.
    ///     All search, browsing, history, and favorites are driven by this metadata without
    ///     eagerly evaluating live RemoteObjects or invoking memory reads.
    /// </summary>
    public sealed class DvNavNode
    {
        /// <summary>
        ///     Gets the unique, stable identifier for this node (e.g. "player.life", "serverdata.gold").
        /// </summary>
        public required string Id { get; init; }

        /// <summary>
        ///     Gets the human-readable display name (e.g. "Life (Health / ES / Mana)").
        /// </summary>
        public required string DisplayName { get; init; }

        /// <summary>
        ///     Gets the broad category grouping for browsing (e.g. "Player Components", "Core &amp; System").
        /// </summary>
        public required string Category { get; init; }

        /// <summary>
        ///     Gets the logical breadcrumb path.
        /// </summary>
        public required string Path { get; init; }

        /// <summary>
        ///     Gets the search aliases and keyword tags for fuzzy matching.
        /// </summary>
        public string[] Tags { get; init; } = Array.Empty<string>();

        /// <summary>
        ///     Gets the node kind.
        /// </summary>
        public DvNodeKind Kind { get; init; } = DvNodeKind.RemoteObject;

        /// <summary>
        ///     Gets the lazy resolver delegate that retrieves the live RemoteObject when inspected.
        ///     Must NEVER be invoked during search indexing or list rendering.
        /// </summary>
        public Func<RemoteObjectBase?>? ObjectResolver { get; init; }

        /// <summary>
        ///     Gets the custom ImGui render delegate for non-RemoteObject targets.
        /// </summary>
        public Action? CustomRenderer { get; init; }

        /// <summary>
        ///     Gets the optional dynamic children resolver delegate.
        ///     Must NEVER be invoked during search, indexing, or collapsed browse rendering.
        ///     Invoked strictly upon explicit interaction or inspection.
        /// </summary>
        public Func<System.Collections.Generic.IReadOnlyList<DvNavNode>>? DynamicChildrenResolver { get; init; }

        /// <summary>
        ///     Gets an optional predicate indicating if the target is currently available in the active game state.
        /// </summary>
        public Func<bool>? IsAvailable { get; init; }
    }
}