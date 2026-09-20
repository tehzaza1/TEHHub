// <copyright file="HideoutFlow.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Modes.Shared
{
    using System;
    using System.Linq;
    using System.Numerics;
    using AutoExile2.Systems;
    using TEHhub;
    using TEHhub.RemoteEnums;
    using TEHhub.RemoteObjects.States.InGameStateObjects;
    using TEHhub.RemoteObjects.UiElement;

    /// <summary>
    /// First PoE 2 hideout preparation flow: inspect inventory, open the personal stash when no
    /// eligible Waystone is present, and select the configured Waystone Tab. Item withdrawal is
    /// intentionally left to the next batch.
    /// </summary>
    internal sealed class HideoutFlow
    {
        private static readonly TimeSpan InventoryReadInterval = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
        private InventorySnapshot? inventorySnapshot;
        private DateTime lastInventoryReadUtc = DateTime.MinValue;
        private DateTime nextRetryUtc = DateTime.MinValue;
        private bool useTabScrollFallback;

        public string Status { get; private set; } = "Idle";

        public string Decision { get; private set; } = "Idle";

        public int EligibleWaystoneCount { get; private set; }

        public System.Collections.Generic.List<Vector2> CurrentNavPath =>
            this.interaction?.CurrentNavPath ?? EmptyPath;

        public int CurrentWaypointIndex => this.interaction?.CurrentWaypointIndex ?? 0;

        public Vector2? CurrentDestination => this.interaction?.CurrentDestination;

        private static System.Collections.Generic.List<Vector2> EmptyPath { get; } = new();

        private InteractionSystem? interaction;

        public void Reset(BotContext ctx)
        {
            ctx.Interaction.Cancel(ctx.Settings);
            this.interaction = ctx.Interaction;
            this.inventorySnapshot = null;
            this.lastInventoryReadUtc = DateTime.MinValue;
            this.nextRetryUtc = DateTime.MinValue;
            this.useTabScrollFallback = false;
            this.EligibleWaystoneCount = 0;
            this.Status = "Idle";
            this.Decision = "Idle";
        }

        public void Tick(BotContext ctx)
        {
            this.interaction = ctx.Interaction;

            if (!Core.Process.Foreground || ctx.GameUi.ChatParent?.IsChatActive == true)
            {
                ctx.Interaction.Cancel(ctx.Settings);
                this.Status = "Waiting for foreground game and inactive chat";
                this.Decision = "WaitForSafeInput";
                return;
            }

            if (!ctx.World.AreaDetails.IsHideout)
            {
                ctx.Interaction.Cancel(ctx.Settings);
                this.Status = "Town detected — hideout stash automation is disabled";
                this.Decision = "WaitForHideout";
                return;
            }

            if (ctx.GameUi.IsVendorOpen)
            {
                ctx.Interaction.Cancel(ctx.Settings);
                this.Status = "Vendor is open — waiting for a safe UI state";
                this.Decision = "WaitForVendorClose";
                return;
            }

            var inventory = this.ReadInventory(ctx);
            if (inventory.State != InventorySnapshotState.Ready)
            {
                ctx.Interaction.Cancel(ctx.Settings);
                this.Status = $"Inventory {inventory.State}: {inventory.Diagnostic}";
                this.Decision = "ReadInventory";
                return;
            }

            this.EligibleWaystoneCount = inventory.Items.Count(entry =>
                TryGetWaystoneTier(entry.Item.Path, out var tier) && tier <= Math.Clamp(ctx.Settings.MaxTier, 1, 16));
            if (this.EligibleWaystoneCount > 0)
            {
                ctx.Interaction.Cancel(ctx.Settings);
                this.Status = $"Inventory ready — {this.EligibleWaystoneCount} eligible Waystone(s)";
                this.Decision = "WaystoneReady";
                return;
            }

            var configuredTab = (ctx.Settings.WaystoneTab ?? string.Empty).Trim();
            if (configuredTab.Length == 0)
            {
                ctx.Interaction.Cancel(ctx.Settings);
                this.Status = "No eligible Waystone — configure Waystone Tab";
                this.Decision = "ConfigureWaystoneTab";
                return;
            }

            if (!ctx.GameUi.IsStashOpen)
            {
                if (ctx.GameUi.IsAnyLargePanelOpen)
                {
                    ctx.Interaction.Cancel(ctx.Settings);
                    this.Status = "Another game panel is open — waiting before stash interaction";
                    this.Decision = "WaitForPanelClose";
                    return;
                }

                this.TickOpenStash(ctx);
                return;
            }

            this.TickSelectWaystoneTab(ctx, configuredTab);
        }

        private InventorySnapshot ReadInventory(BotContext ctx)
        {
            var now = DateTime.UtcNow;
            if (this.inventorySnapshot == null || now - this.lastInventoryReadUtc >= InventoryReadInterval)
            {
                this.inventorySnapshot = ctx.Area.ServerDataObject.ReadInventorySnapshot(InventoryName.MainInventory1);
                this.lastInventoryReadUtc = now;
            }

            return this.inventorySnapshot;
        }

        private void TickOpenStash(BotContext ctx)
        {
            if (ctx.Interaction.Result == InteractionResult.Failed)
            {
                if (DateTime.UtcNow < this.nextRetryUtc)
                {
                    this.Status = ctx.Interaction.LastFailure;
                    this.Decision = "RetryOpenStash";
                    return;
                }

                ctx.Interaction.Reset();
            }

            if (ctx.Interaction.Result == InteractionResult.Succeeded)
            {
                ctx.Interaction.Reset();
            }

            if (!ctx.Interaction.IsBusy)
            {
                var stash = ModeHelpers.FindNearestStash(ctx.Area, ctx.PlayerGrid);
                if (stash == null)
                {
                    this.Status = "No targetable personal stash entity in the network bubble";
                    this.Decision = "FindStash";
                    return;
                }

                ctx.Interaction.BeginEntity(
                    ctx,
                    stash,
                    "personal stash",
                    current => current.GameUi.IsStashOpen);
            }

            var result = ctx.Interaction.Tick(ctx);
            this.Status = ctx.Interaction.Status;
            this.Decision = ctx.Interaction.Phase.ToString();
            if (result == InteractionResult.Failed)
            {
                this.nextRetryUtc = DateTime.UtcNow + RetryDelay;
            }
        }

        private void TickSelectWaystoneTab(BotContext ctx, string configuredTab)
        {
            if (ctx.Interaction.Result == InteractionResult.Failed)
            {
                if (DateTime.UtcNow < this.nextRetryUtc)
                {
                    this.Status = ctx.Interaction.LastFailure;
                    this.Decision = "RetryWaystoneTab";
                    return;
                }

                ctx.Interaction.Reset();
            }

            if (ctx.Interaction.IsBusy)
            {
                var result = ctx.Interaction.Tick(ctx);
                this.Status = ctx.Interaction.Status;
                this.Decision = ctx.Interaction.Phase.ToString();
                if (result == InteractionResult.Failed)
                {
                    this.useTabScrollFallback = !this.useTabScrollFallback;
                    this.nextRetryUtc = DateTime.UtcNow + RetryDelay;
                }
                return;
            }

            if (ctx.Interaction.Result == InteractionResult.Succeeded)
            {
                ctx.Interaction.Reset();
            }

            var snapshot = ctx.GameUi.Stash.ReadSnapshot();
            if (snapshot.State != StashSnapshotState.Ready)
            {
                this.Status = $"Stash {snapshot.State}: {snapshot.Diagnostic}";
                this.Decision = "ReadStash";
                return;
            }

            if (string.Equals(snapshot.CurrentTabName, configuredTab, StringComparison.OrdinalIgnoreCase))
            {
                this.useTabScrollFallback = false;
                this.Status = $"Waystone Tab ready: {snapshot.CurrentTabName}";
                this.Decision = "WaystoneTabReady";
                return;
            }

            var tab = snapshot.Tabs.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, configuredTab, StringComparison.OrdinalIgnoreCase));
            if (tab == null)
            {
                this.Status = $"Configured Waystone Tab '{configuredTab}' is not visible";
                this.Decision = "FindWaystoneTab";
                return;
            }

            var topTabUnavailable = tab.UiAddress == IntPtr.Zero;
            var beganInteraction = this.useTabScrollFallback || topTabUnavailable
                ? this.BeginTabScrollFallback(ctx, snapshot, tab, configuredTab)
                : ctx.Interaction.BeginUiElement(
                    tab.UiAddress,
                    $"Waystone Tab '{tab.Name}'",
                    current => IsConfiguredTabReady(current, configuredTab),
                    fallbackUiAddress: snapshot.IsAllTabsListOpen ? tab.FallbackUiAddress : IntPtr.Zero,
                    maxClickAttempts: 4);
            if (!beganInteraction)
            {
                if (this.useTabScrollFallback || topTabUnavailable)
                {
                    this.useTabScrollFallback = false;
                    this.nextRetryUtc = DateTime.UtcNow + RetryDelay;
                    this.Status = "Cannot Ctrl+scroll tabs because their all-tabs order is unavailable";
                    this.Decision = "ReadTabOrder";
                }
                else
                {
                    this.Status = "Interaction system is busy";
                    this.Decision = "WaitForInteraction";
                }

                return;
            }

            var initialResult = ctx.Interaction.Tick(ctx);
            this.Status = ctx.Interaction.Status;
            this.Decision = ctx.Interaction.Phase.ToString();
            if (initialResult == InteractionResult.Failed)
            {
                this.useTabScrollFallback = !this.useTabScrollFallback;
                this.nextRetryUtc = DateTime.UtcNow + RetryDelay;
            }
        }

        private bool BeginTabScrollFallback(
            BotContext ctx,
            StashSnapshot snapshot,
            StashTabInfo targetTab,
            string configuredTab)
        {
            var currentTab = snapshot.Tabs.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, snapshot.CurrentTabName, StringComparison.OrdinalIgnoreCase));
            if (currentTab == null || currentTab.AllTabsIndex < 0 || targetTab.AllTabsIndex < 0 ||
                currentTab.AllTabsIndex == targetTab.AllTabsIndex)
            {
                return false;
            }

            // In PoE 2's vertical tab order, wheel-up selects the preceding row (5 -> 4)
            // and wheel-down selects the following row (5 -> 6).
            var wheelDirection = targetTab.AllTabsIndex < currentTab.AllTabsIndex ? 1 : -1;
            var tabDistance = Math.Abs(targetTab.AllTabsIndex - currentTab.AllTabsIndex);
            var hoverAddress = currentTab.UiAddress;
            if (hoverAddress == IntPtr.Zero && snapshot.IsAllTabsListOpen)
            {
                hoverAddress = currentTab.FallbackUiAddress;
            }

            var fallbackHoverAddress = snapshot.IsAllTabsListOpen && hoverAddress != currentTab.FallbackUiAddress
                ? currentTab.FallbackUiAddress
                : IntPtr.Zero;
            return ctx.Interaction.BeginUiScroll(
                hoverAddress,
                $"Waystone Tab '{targetTab.Name}'",
                wheelDirection,
                current => IsConfiguredTabReady(current, configuredTab),
                fallbackHoverAddress,
                maxScrollAttempts: Math.Min(32, tabDistance + 2));
        }

        private static bool IsConfiguredTabReady(BotContext ctx, string configuredTab)
        {
            if (!ctx.GameUi.IsStashOpen)
            {
                return false;
            }

            var snapshot = ctx.GameUi.Stash.ReadSnapshot();
            return snapshot.State == StashSnapshotState.Ready &&
                   string.Equals(snapshot.CurrentTabName, configuredTab, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool TryGetWaystoneTier(string? path, out int tier)
        {
            const string prefix = "Metadata/Items/Maps/MapKeyTier";
            tier = 0;
            if (string.IsNullOrWhiteSpace(path) ||
                !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var suffix = path[prefix.Length..];
            return int.TryParse(suffix, out tier) && tier is >= 1 and <= 16;
        }
    }
}
