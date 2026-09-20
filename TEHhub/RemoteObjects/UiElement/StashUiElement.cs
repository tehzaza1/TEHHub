// <copyright file="StashUiElement.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace TEHhub.RemoteObjects.UiElement
{
    using System;
    using System.Collections.Generic;
    using TEHhub.Cache;
    using TEHhub.RemoteEnums;
    using TEHhub.RemoteObjects.States.InGameStateObjects;

    /// <summary>
    ///     Read state of the PoE2 stash panel.
    /// </summary>
    public enum StashSnapshotState
    {
        /// <summary>The stash panel is not open.</summary>
        Closed,

        /// <summary>The panel is open, but its UI or server inventory has not stabilized yet.</summary>
        Loading,

        /// <summary>The selected tab and its server-backed inventory are coherent.</summary>
        Ready,
    }

    /// <summary>
    ///     One visible stash tab control discovered in the PoE2 tab bar.
    /// </summary>
    /// <param name="Name">Displayed tab name.</param>
    /// <param name="UiAddress">Clickable tab-root UiElement address.</param>
    /// <param name="IsSelected">Whether this is the selected control in the captured tab bar.</param>
    /// <param name="FallbackUiAddress">Clickable row in the vertical all-tabs list, or zero when unavailable.</param>
    /// <param name="AllTabsIndex">Zero-based position in the vertical all-tabs list, or -1 when unavailable.</param>
    public sealed record StashTabInfo(
        string Name,
        IntPtr UiAddress,
        bool IsSelected,
        IntPtr FallbackUiAddress = default,
        int AllTabsIndex = -1);

    /// <summary>
    ///     One Tier control in a specialized PoE2 Waystone stash tab.
    /// </summary>
    /// <param name="Name">Displayed Roman-numeral Tier label.</param>
    /// <param name="Count">Number displayed above the Tier control.</param>
    /// <param name="UiAddress">Clickable Tier-root UiElement address.</param>
    public sealed record StashTierInfo(string Name, int Count, IntPtr UiAddress);

    /// <summary>
    ///     One stable, read-only view of the currently selected PoE2 stash tab.
    /// </summary>
    /// <param name="State">Closed, Loading, or Ready.</param>
    /// <param name="CurrentTabName">Selected top-level stash tab name.</param>
    /// <param name="CurrentPageName">Selected page inside a specialized tab, such as Waystone page 1-6.</param>
    /// <param name="TopTabBarUiAddress">Visible horizontal top-tab container used as the Ctrl+scroll hover target.</param>
    /// <param name="Tabs">Top-level tab controls currently materialized by the UI.</param>
    /// <param name="Pages">Specialized-tab page controls currently materialized by the UI.</param>
    /// <param name="Tiers">Waystone Tier I-XVI controls and their displayed counts.</param>
    /// <param name="Inventory">ServerData StashInventoryId snapshot for the selected tab.</param>
    /// <param name="IsAllTabsListOpen">Whether the vertical all-tabs list is visible through its parent chain.</param>
    /// <param name="Diagnostic">Short explanation of the state.</param>
    public sealed record StashSnapshot(
        StashSnapshotState State,
        string CurrentTabName,
        string CurrentPageName,
        IntPtr TopTabBarUiAddress,
        IReadOnlyList<StashTabInfo> Tabs,
        IReadOnlyList<StashTabInfo> Pages,
        IReadOnlyList<StashTierInfo> Tiers,
        InventorySnapshot Inventory,
        bool IsAllTabsListOpen,
        string Diagnostic)
    {
        /// <summary>Gets a value indicating whether a ready selected tab contains no items.</summary>
        public bool IsEmpty => this.State == StashSnapshotState.Ready && this.Inventory.IsEmpty;
    }

    /// <summary>
    ///     PoE2 stash SDK entry point. UI evidence identifies the selected tab while ServerData inventory 27
    ///     remains authoritative for item contents, including specialized Waystone tabs.
    /// </summary>
    public sealed class StashUiElement : UiElementBase
    {
        // PoE2 0.5.x evidence captured by UiDump:
        // Stash -> content -> tab host -> tab bar. The selected tab is reinserted as the final child.
        private static readonly int[] TabBarPath = { 2, 0, 0, 0, 1, 0 };

        // The vertical all-tabs list remains materialized beside the stash. It is the reliable
        // fallback when many tabs make a top-bar control clipped or otherwise non-clickable.
        // UiDump evidence: Stash -> 2 -> 0 -> 0 -> 0 -> 1 -> 4 -> 2.
        private static readonly int[] AllTabsListPath = { 2, 0, 0, 0, 1, 4, 2 };

        // Specialized Waystone tabs add a second 1-6 page bar. Like the main bar, the selected
        // page is reinserted as the final child. Absence is normal for ordinary stash tabs.
        private static readonly int[] SpecializedPageBarPath = { 2, 0, 0, 0, 1, 1, 2, 0, 1, 0, 0, 0 };

        // Specialized Waystone tabs expose all Tier I-XVI controls and their counts even though
        // item contents are materialized only for the selected Tier/page.
        private static readonly int[] SpecializedTierBarPath = { 2, 0, 0, 0, 1, 1, 2, 0, 0 };
        private static readonly string[] TierNames =
        {
            "I", "II", "III", "IV", "V", "VI", "VII", "VIII",
            "IX", "X", "XI", "XII", "XIII", "XIV", "XV", "XVI",
        };
        private readonly System.Threading.Lock snapshotLock = new();
        private readonly StashSnapshotStabilityGate stability = new();

        internal StashUiElement(IntPtr address, UiElementParents parents)
            : base(address, parents)
        {
        }

        /// <summary>
        ///     Reads the currently selected stash tab. Consumers must wait for Ready before acting;
        ///     the first coherent observation is Loading and the second identical observation is Ready.
        /// </summary>
        /// <returns>Current read-only stash snapshot.</returns>
        public StashSnapshot ReadSnapshot()
        {
            lock (this.snapshotLock)
            {
                var serverData = Core.States.InGameStateObject.CurrentAreaInstance.ServerDataObject;
                if (this.Address == IntPtr.Zero ||
                    !UiElementMemory.HasTextAtPath(this.Address, [1], "Stash"))
                {
                    this.stability.Reset();
                    var unavailableInventory = serverData.ReadInventorySnapshot(InventoryName.StashInventoryId);
                    return new StashSnapshot(
                        StashSnapshotState.Closed,
                        string.Empty,
                        string.Empty,
                        IntPtr.Zero,
                        Array.Empty<StashTabInfo>(),
                        Array.Empty<StashTabInfo>(),
                        Array.Empty<StashTierInfo>(),
                        unavailableInventory,
                        false,
                        "Stash panel is closed.");
                }

                var topTabBarUiAddress = UiElementMemory.TryResolvePath(this.Address, TabBarPath, out var topTabBar) &&
                                         UiElementMemory.IsVisibleThroughParents(topTabBar)
                    ? topTabBar
                    : IntPtr.Zero;
                var tabs = ReadTabBar(this.Address, TabBarPath, out var currentTab);
                var allTabsListOpen = UiElementMemory.TryResolvePath(this.Address, AllTabsListPath, out var allTabsList) &&
                                      UiElementMemory.IsVisibleThroughParents(allTabsList);
                tabs = MergeAllTabsFallbacks(
                    tabs,
                    ReadNamedControls(this.Address, AllTabsListPath),
                    currentTab,
                    allTabsListOpen);
                var pages = ReadTabBar(this.Address, SpecializedPageBarPath, out var currentPage);
                var tiers = ReadWaystoneTiers(this.Address);
                var inventory = serverData.ReadInventorySnapshot(InventoryName.StashInventoryId);
                if (string.IsNullOrWhiteSpace(currentTab))
                {
                    this.stability.Reset();
                    return new StashSnapshot(
                        StashSnapshotState.Loading,
                        string.Empty,
                        currentPage,
                        topTabBarUiAddress,
                        tabs,
                        pages,
                        tiers,
                        inventory,
                        allTabsListOpen,
                        "Stash is open, but the selected tab label is not materialized yet.");
                }

                if (inventory.State != InventorySnapshotState.Ready)
                {
                    this.stability.Reset();
                    return new StashSnapshot(
                        StashSnapshotState.Loading,
                        currentTab,
                        currentPage,
                        topTabBarUiAddress,
                        tabs,
                        pages,
                        tiers,
                        inventory,
                        allTabsListOpen,
                        $"Stash inventory is {inventory.State}: {inventory.Diagnostic}");
                }

                var tierRevision = string.Join(',', tiers.ConvertAll(tier => $"{tier.Name}:{tier.Count}"));
                var revision = $"{currentTab}\u001F{currentPage}\u001F{allTabsListOpen}\u001F{tierRevision}\u001F{inventory.Address.ToInt64():X}\u001F{inventory.ServerRequestCounter}\u001F{inventory.Revision:X16}";
                if (!this.stability.Observe(revision))
                {
                    return new StashSnapshot(
                        StashSnapshotState.Loading,
                        currentTab,
                        currentPage,
                        topTabBarUiAddress,
                        tabs,
                        pages,
                        tiers,
                        inventory,
                        allTabsListOpen,
                        "Waiting for identical UI and ServerData observations across the stability window.");
                }

                return new StashSnapshot(
                    StashSnapshotState.Ready,
                    currentTab,
                    currentPage,
                    topTabBarUiAddress,
                    tabs,
                    pages,
                    tiers,
                    inventory,
                    allTabsListOpen,
                    inventory.IsEmpty ? "Selected tab is ready and empty." : $"Selected tab is ready with {inventory.Items.Count} item(s).");
            }
        }

        private static IReadOnlyList<StashTabInfo> ReadTabBar(
            IntPtr stashRoot,
            ReadOnlySpan<int> path,
            out string selectedName)
        {
            selectedName = string.Empty;
            if (!UiElementMemory.TryResolvePath(stashRoot, path, out var bar) ||
                !UiElementMemory.TryReadChildren(bar, out var controls) || controls.Length == 0)
            {
                return Array.Empty<StashTabInfo>();
            }

            var selectedIndex = controls.Length - 1;
            var output = new List<StashTabInfo>(controls.Length);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < controls.Length; i++)
            {
                var control = controls[i];
                if (!UiElementMemory.TryFindFirstDisplayText(
                        control,
                        maxDepth: 3,
                        maxNodes: 16,
                        out var name,
                        out _))
                {
                    continue;
                }

                name = name.Trim();
                if (name.Length == 0 || !names.Add(name))
                {
                    continue;
                }

                var selected = i == selectedIndex;
                output.Add(new StashTabInfo(name, control, selected));
                if (selected)
                {
                    selectedName = name;
                }
            }

            return output.ToArray();
        }

        private static IReadOnlyList<StashTabInfo> ReadNamedControls(
            IntPtr stashRoot,
            ReadOnlySpan<int> path)
        {
            if (!UiElementMemory.TryResolvePath(stashRoot, path, out var list) ||
                !UiElementMemory.TryReadChildren(list, out var controls) || controls.Length == 0)
            {
                return Array.Empty<StashTabInfo>();
            }

            var output = new List<StashTabInfo>(controls.Length);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < controls.Length; i++)
            {
                var control = controls[i];
                if (!UiElementMemory.TryFindFirstDisplayText(
                        control,
                        maxDepth: 3,
                        maxNodes: 16,
                        out var name,
                        out _))
                {
                    continue;
                }

                name = name.Trim();
                if (name.Length == 0 || !names.Add(name))
                {
                    continue;
                }

                output.Add(new StashTabInfo(name, control, false, AllTabsIndex: i));
            }

            return output.ToArray();
        }

        internal static IReadOnlyList<StashTabInfo> MergeAllTabsFallbacks(
            IReadOnlyList<StashTabInfo> topTabs,
            IReadOnlyList<StashTabInfo> allTabs,
            string currentTab,
            bool allTabsListOpen = true)
        {
            if (allTabs.Count == 0)
            {
                return topTabs;
            }

            var merged = new List<StashTabInfo>(Math.Max(topTabs.Count, allTabs.Count));
            var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var topTab in topTabs)
            {
                byName[topTab.Name] = merged.Count;
                merged.Add(topTab);
            }

            foreach (var listTab in allTabs)
            {
                if (byName.TryGetValue(listTab.Name, out var index))
                {
                    merged[index] = merged[index] with
                    {
                        FallbackUiAddress = allTabsListOpen ? listTab.UiAddress : IntPtr.Zero,
                        AllTabsIndex = listTab.AllTabsIndex,
                    };
                    continue;
                }

                byName[listTab.Name] = merged.Count;
                merged.Add(new StashTabInfo(
                    listTab.Name,
                    IntPtr.Zero,
                    string.Equals(listTab.Name, currentTab, StringComparison.OrdinalIgnoreCase),
                    FallbackUiAddress: allTabsListOpen ? listTab.UiAddress : IntPtr.Zero,
                    AllTabsIndex: listTab.AllTabsIndex));
            }

            return merged.ToArray();
        }

        private static List<StashTierInfo> ReadWaystoneTiers(IntPtr stashRoot)
        {
            var output = new List<StashTierInfo>(TierNames.Length);
            if (!UiElementMemory.TryResolvePath(stashRoot, SpecializedTierBarPath, out var bar) ||
                !UiElementMemory.TryReadChildren(bar, out var controls) ||
                controls.Length != TierNames.Length)
            {
                return output;
            }

            for (var i = 0; i < controls.Length; i++)
            {
                var control = controls[i];
                if (!UiElementMemory.TryResolvePath(control, [2], out var labelElement) ||
                    !UiElementMemory.TryReadDisplayText(labelElement, out var label) ||
                    !string.Equals(label.Trim(), TierNames[i], StringComparison.Ordinal) ||
                    !UiElementMemory.TryResolvePath(control, [3, 0], out var countElement) ||
                    !UiElementMemory.TryReadDisplayText(countElement, out var countText) ||
                    !int.TryParse(countText.Trim(), out var count) || count < 0)
                {
                    return new List<StashTierInfo>();
                }

                output.Add(new StashTierInfo(TierNames[i], count, control));
            }

            return output;
        }
    }

    internal sealed class StashSnapshotStabilityGate
    {
        private const long MinimumStableMilliseconds = 100;
        private string? lastRevision;
        private int observations;
        private long firstObservationMilliseconds;

        internal bool Observe(string revision) =>
            this.Observe(revision, Environment.TickCount64);

        internal bool Observe(string revision, long nowMilliseconds)
        {
            if (!string.Equals(this.lastRevision, revision, StringComparison.Ordinal))
            {
                this.lastRevision = revision;
                this.observations = 1;
                this.firstObservationMilliseconds = nowMilliseconds;
                return false;
            }

            this.observations++;
            return this.observations >= 2 &&
                   nowMilliseconds - this.firstObservationMilliseconds >= MinimumStableMilliseconds;
        }

        internal void Reset()
        {
            this.lastRevision = null;
            this.observations = 0;
            this.firstObservationMilliseconds = 0;
        }
    }
}
