// <copyright file="HideoutFlow.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Modes.Shared
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Numerics;
    using AutoExile2.Systems;
    using TEHhub;
    using TEHhub.RemoteEnums;
    using TEHhub.RemoteObjects.States.InGameStateObjects;
    using TEHhub.RemoteObjects.UiElement;

    /// <summary>
    /// First PoE 2 hideout preparation flow: inspect main inventory, open the personal stash,
    /// select the configured Waystone Tab, withdraw exactly one Waystone in the configured Tier
    /// range, and use Wisdom/Alchemy/Exalted directly from the configured Currency Tab.
    /// </summary>
    internal sealed class HideoutFlow
    {
        private static readonly TimeSpan InventoryReadInterval = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
        private InventorySnapshot? inventorySnapshot;
        private DateTime lastInventoryReadUtc = DateTime.MinValue;
        private DateTime nextRetryUtc = DateTime.MinValue;
        private DateTime tabActionNotBeforeUtc = DateTime.MinValue;
        private string delayedTabTarget = string.Empty;
        private string activeConfiguredTab = string.Empty;
        private string activeFilterSignature = string.Empty;
        private int activeMinTier;
        private int activeMaxTier;
        private readonly HashSet<int> exhaustedWaystoneTiers = new();
        private readonly HashSet<IntPtr> stoppedCraftItems = new();
        private bool useTabScrollFallback;
        private bool withdrawalAttempted;
        private IntPtr activeCraftItemAddress;
        private int targetWaystoneTier;
        private int nextSpecializedPage = 1;
        private int pendingPageNumber;
        private StashPhase stashPhase = StashPhase.SelectTab;
        private PendingInteraction pendingInteraction = PendingInteraction.None;
        private StashPhase phaseAfterPageSelection = StashPhase.FindItem;

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
            this.activeConfiguredTab = string.Empty;
            this.activeFilterSignature = string.Empty;
            this.activeMinTier = 0;
            this.activeMaxTier = 0;
            this.ResetStashWorkflow();
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

            if (ctx.Interaction.CurrencyMayBeActive ||
                this.pendingInteraction == PendingInteraction.CursorCleanup)
            {
                this.TickCurrencyCursorSafety(ctx);
                return;
            }

            var cursorSlot = ctx.Area.ServerDataObject.ReadInventoryItemAt(
                InventoryName.Cursor1,
                0,
                0,
                InventorySnapshotDetailLevel.Basic);
            if (cursorSlot.State == InventorySnapshotState.Loading)
            {
                this.Status = "Cursor1 is changing — waiting";
                this.Decision = "ReadCursor1";
                return;
            }

            if (cursorSlot.State != InventorySnapshotState.Ready)
            {
                this.Status = $"Cursor1 is {cursorSlot.State} — automation stopped";
                this.Decision = "Cursor1Unavailable";
                return;
            }

            if (cursorSlot.Item != null)
            {
                this.Status = $"Unexpected item on Cursor1: {cursorSlot.Item.Path}";
                this.Decision = "ClearCursorManually";
                return;
            }

            var (minTier, maxTier) = GetTierRange(ctx.Settings);
            var inventory = this.ReadInventory(ctx);
            if (inventory.State != InventorySnapshotState.Ready)
            {
                ctx.Interaction.Cancel(ctx.Settings);
                this.Status = $"Inventory {inventory.State}: {inventory.Diagnostic}";
                this.Decision = "ReadInventory";
                return;
            }

            var inventoryWaystones = inventory.Items
                .Where(item => item.WaystoneTier is int tier && tier >= minTier && tier <= maxTier)
                .OrderByDescending(item => item.WaystoneTier)
                .ThenBy(item => item.SlotStartY)
                .ThenBy(item => item.SlotStartX)
                .ToArray();
            var actionable = inventoryWaystones
                .Select(item =>
                {
                    var action = WaystoneCrafting.GetNextAction(item, ctx.Settings, out var reason);
                    return new
                    {
                        Item = item,
                        Action = action,
                        Reason = reason,
                    };
                })
                .Where(candidate => candidate.Action != WaystoneCraftingAction.Reject &&
                                    !this.stoppedCraftItems.Contains(candidate.Item.ItemAddress))
                .FirstOrDefault();
            this.EligibleWaystoneCount = inventoryWaystones.Count(item =>
                WaystoneCrafting.GetNextAction(item, ctx.Settings, out _) == WaystoneCraftingAction.Ready);
            if (actionable?.Action == WaystoneCraftingAction.Ready)
            {
                ctx.Interaction.Cancel(ctx.Settings);
                this.Status = $"Inventory ready — {this.EligibleWaystoneCount} Waystone(s) in Tier {minTier}-{maxTier}";
                this.Decision = "WaystoneReady";
                return;
            }

            if (actionable != null)
            {
                this.TickCraftWaystone(ctx, actionable.Item, actionable.Action, actionable.Reason);
                return;
            }

            if (inventoryWaystones.Length > 0)
            {
                ctx.Interaction.Cancel(ctx.Settings);
                this.Status = "Inventory Waystone(s) cannot be prepared or did not pass the active filter";
                this.Decision = "NoInventoryWaystonePassedFilters";
                return;
            }

            var configuredTab = (ctx.Settings.WaystoneTab ?? string.Empty).Trim();
            if (configuredTab.Length == 0)
            {
                ctx.Interaction.Cancel(ctx.Settings);
                this.Status = $"No Waystone in Tier {minTier}-{maxTier} — configure Waystone Tab";
                this.Decision = "ConfigureWaystoneTab";
                return;
            }

            var filterSignature = WaystoneFilter.GetSettingsSignature(ctx.Settings);
            if (!string.Equals(this.activeConfiguredTab, configuredTab, StringComparison.OrdinalIgnoreCase) ||
                this.activeMinTier != minTier || this.activeMaxTier != maxTier ||
                !string.Equals(this.activeFilterSignature, filterSignature, StringComparison.Ordinal))
            {
                ctx.Interaction.Cancel(ctx.Settings);
                this.activeConfiguredTab = configuredTab;
                this.activeMinTier = minTier;
                this.activeMaxTier = maxTier;
                this.activeFilterSignature = filterSignature;
                this.ResetStashWorkflow();
            }

            if (!ctx.GameUi.IsStashOpen)
            {
                if (this.pendingInteraction != PendingInteraction.None)
                {
                    ctx.Interaction.Cancel(ctx.Settings);
                }

                this.ResetStashWorkflow(preserveWithdrawalGuard: true);
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

            this.TickStash(ctx, configuredTab, minTier, maxTier);
        }

        private InventorySnapshot ReadInventory(BotContext ctx)
        {
            var now = DateTime.UtcNow;
            if (this.inventorySnapshot == null || now - this.lastInventoryReadUtc >= InventoryReadInterval)
            {
                this.inventorySnapshot = ctx.Area.ServerDataObject.ReadInventorySnapshot(
                    InventoryName.MainInventory1,
                    InventorySnapshotDetailLevel.Full);
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

        private void TickCraftWaystone(
            BotContext ctx,
            InventorySnapshotItem waystone,
            WaystoneCraftingAction action,
            string reason)
        {
            if (ctx.Interaction.IsBusy)
            {
                var result = ctx.Interaction.Tick(ctx);
                this.Status = ctx.Interaction.Status;
                this.Decision = ctx.Interaction.Phase.ToString();
                if (result == InteractionResult.Failed)
                {
                    this.HandleInteractionFailure(ctx);
                }

                return;
            }

            if (ctx.Interaction.Result == InteractionResult.Succeeded)
            {
                this.HandleInteractionSuccess(ctx);
                return;
            }

            if (DateTime.UtcNow < this.nextRetryUtc)
            {
                this.Status = $"Waiting before crafting action ({(this.nextRetryUtc - DateTime.UtcNow).TotalSeconds:F1}s)";
                this.Decision = "RetryCraftingAction";
                return;
            }

            // The stash exposes the currency control; the open player inventory exposes the Waystone.
            if (!ctx.GameUi.IsStashOpen)
            {
                if (ctx.GameUi.IsAnyLargePanelOpen)
                {
                    ctx.Interaction.Cancel(ctx.Settings);
                    this.Status = "Another game panel is open — close it before Waystone crafting";
                    this.Decision = "WaitForPanelClose";
                    return;
                }

                this.TickOpenStash(ctx);
                return;
            }

            var currencyPath = WaystoneCrafting.GetCurrencyPath(action);
            var currencyLabel = WaystoneCrafting.GetCurrencyLabel(action);
            var configuredTab = (ctx.Settings.CurrencyTab ?? string.Empty).Trim();
            if (configuredTab.Length == 0)
            {
                this.Status = "Configure Currency Tab for Waystone crafting";
                this.Decision = "ConfigureCurrencyTab";
                return;
            }

            var snapshot = ctx.GameUi.Stash.ReadSnapshot(InventorySnapshotDetailLevel.Full);
            if (snapshot.State != StashSnapshotState.Ready)
            {
                this.Status = $"Currency stash {snapshot.State}: {snapshot.Diagnostic}";
                this.Decision = "ReadCurrencyTab";
                return;
            }

            if (!string.Equals(snapshot.CurrentTabName, configuredTab, StringComparison.OrdinalIgnoreCase))
            {
                this.BeginSelectCurrencyTab(ctx, snapshot, configuredTab);
                return;
            }

            var control = snapshot.VisibleItems.FirstOrDefault(item =>
                string.Equals(item.ItemPath, currencyPath, StringComparison.OrdinalIgnoreCase) &&
                snapshot.Inventory.Items.Any(entry => entry.ItemAddress == item.ItemAddress &&
                                                      entry.StackCount is > 0));
            var currency = control == null ? null : snapshot.Inventory.Items.FirstOrDefault(item =>
                item.ItemAddress == control.ItemAddress);
            if (currency == null)
            {
                this.Status = $"No readable {currencyLabel} stack in Currency Tab '{configuredTab}'";
                this.Decision = "CurrencyUnavailable";
                return;
            }

            if (!ctx.GameUi.TryGetVisibleInventoryItemUiAddress(waystone.ItemAddress, out var waystoneUi))
            {
                this.Status = "Waystone inventory UI is not materialized yet";
                this.Decision = "ReadInventoryUi";
                return;
            }

            var oldRarity = waystone.Rarity;
            var oldModCount = waystone.ExplicitMods.Count;
            var slotX = waystone.SlotStartX;
            var slotY = waystone.SlotStartY;
            var currencySlotX = currency.SlotStartX;
            var currencySlotY = currency.SlotStartY;
            var currencyStackCount = currency.StackCount;
            if (!ctx.Interaction.BeginUiCurrencyUse(
                    control!.UiAddress,
                    waystoneUi,
                    $"{currencyLabel} on Tier {waystone.WaystoneTier} Waystone",
                    current => CurrencyWasConsumed(
                                   current,
                                   configuredTab,
                                   currencyPath,
                                   currencySlotX,
                                   currencySlotY,
                                   currencyStackCount.Value) &&
                               CraftingActionChangedSlot(
                                   current,
                                   action,
                                   slotX,
                                   slotY,
                                   oldRarity,
                                   oldModCount)))
            {
                this.Status = "Crafting UI is unavailable";
                this.Decision = "UseCraftingCurrency";
                return;
            }

            this.activeCraftItemAddress = waystone.ItemAddress;
            this.pendingInteraction = action switch
            {
                WaystoneCraftingAction.Identify => PendingInteraction.UseWisdom,
                WaystoneCraftingAction.Alchemy => PendingInteraction.UseAlchemy,
                WaystoneCraftingAction.Exalted => PendingInteraction.UseExalted,
                _ => PendingInteraction.None,
            };
            this.Status = reason;
            this.TickStartedInteraction(ctx);
        }

        private void TickCurrencyCursorSafety(BotContext ctx)
        {
            if (ctx.Interaction.IsBusy)
            {
                var result = ctx.Interaction.Tick(ctx);
                this.Status = ctx.Interaction.Status;
                this.Decision = ctx.Interaction.Phase.ToString();
                if (result == InteractionResult.Failed)
                {
                    this.HandleInteractionFailure(ctx);
                }
                else if (result == InteractionResult.Succeeded)
                {
                    this.HandleInteractionSuccess(ctx);
                }

                return;
            }

            if (!ctx.Interaction.CurrencyMayBeActive)
            {
                if (ctx.Interaction.Result == InteractionResult.Succeeded)
                {
                    this.HandleInteractionSuccess(ctx);
                }

                return;
            }

            if (!ctx.GameUi.IsStashOpen ||
                !ctx.GameUi.Stash.TryGetSafeCursorCancelUiAddress(out var safeUi))
            {
                this.Status = "Right-click currency may still be active — reopen Stash for safe cleanup";
                this.Decision = "CursorCleanupBlocked";
                return;
            }

            ctx.Interaction.Reset();
            if (!ctx.Interaction.BeginUiCurrencyCursorCleanup(safeUi))
            {
                this.Status = "Cannot start safe right-click currency cleanup";
                this.Decision = "CursorCleanupBlocked";
                return;
            }

            this.pendingInteraction = PendingInteraction.CursorCleanup;
            this.TickStartedInteraction(ctx);
        }

        private void TickStash(BotContext ctx, string configuredTab, int minTier, int maxTier)
        {
            if (ctx.Interaction.IsBusy)
            {
                var result = ctx.Interaction.Tick(ctx);
                this.Status = ctx.Interaction.Status;
                this.Decision = ctx.Interaction.Phase.ToString();
                if (result == InteractionResult.Failed)
                {
                    this.HandleInteractionFailure(ctx);
                }

                return;
            }

            if (ctx.Interaction.Result == InteractionResult.Succeeded)
            {
                this.HandleInteractionSuccess(ctx);
            }

            if (this.withdrawalAttempted && this.pendingInteraction == PendingInteraction.None)
            {
                this.Status = "Waystone withdrawal was not confirmed — stopped to avoid withdrawing a second item";
                this.Decision = "WithdrawalUnconfirmed";
                return;
            }

            if (DateTime.UtcNow < this.nextRetryUtc)
            {
                this.Status = $"Waiting before retrying stash action ({(this.nextRetryUtc - DateTime.UtcNow).TotalSeconds:F1}s)";
                this.Decision = "RetryStashAction";
                return;
            }

            var snapshot = ctx.GameUi.Stash.ReadSnapshot(InventorySnapshotDetailLevel.Full);
            if (snapshot.State != StashSnapshotState.Ready)
            {
                this.Status = $"Stash {snapshot.State}: {snapshot.Diagnostic}";
                this.Decision = "ReadStash";
                return;
            }

            if (!string.Equals(snapshot.CurrentTabName, configuredTab, StringComparison.OrdinalIgnoreCase))
            {
                this.stashPhase = StashPhase.SelectTab;
                this.targetWaystoneTier = 0;
                this.BeginSelectWaystoneTab(ctx, snapshot, configuredTab);
                return;
            }

            this.tabActionNotBeforeUtc = DateTime.MinValue;
            this.delayedTabTarget = string.Empty;
            this.useTabScrollFallback = false;
            if (this.stashPhase == StashPhase.SelectTab)
            {
                this.stashPhase = StashPhase.SelectTier;
                this.nextSpecializedPage = 1;
            }

            var isSpecializedWaystoneTab = snapshot.Tiers.Count == 16;
            if (!isSpecializedWaystoneTab)
            {
                this.stashPhase = StashPhase.FindItem;
                this.TickWithdrawFromSnapshot(ctx, snapshot, minTier, maxTier, targetTier: 0);
                return;
            }

            var target = snapshot.Tiers
                .Select((tier, index) => new { Info = tier, Tier = index + 1 })
                .Where(candidate => candidate.Tier >= minTier && candidate.Tier <= maxTier &&
                                    candidate.Info.Count > 0 &&
                                    !this.exhaustedWaystoneTiers.Contains(candidate.Tier))
                .OrderByDescending(candidate => candidate.Tier)
                .FirstOrDefault();
            if (target == null)
            {
                this.targetWaystoneTier = 0;
                this.Status = this.exhaustedWaystoneTiers.Count > 0
                    ? $"No Waystone in Tier {minTier}-{maxTier} passed the active filters"
                    : $"Waystone Tab has no Waystone in Tier {minTier}-{maxTier}";
                this.Decision = this.exhaustedWaystoneTiers.Count > 0
                    ? "NoWaystonePassedFilters"
                    : "NoWaystoneInRange";
                return;
            }

            if (this.targetWaystoneTier != target.Tier)
            {
                this.targetWaystoneTier = target.Tier;
                this.nextSpecializedPage = 1;
                this.stashPhase = StashPhase.SelectTier;
            }

            switch (this.stashPhase)
            {
                case StashPhase.SelectPage:
                    if (snapshot.Pages.Count != 6)
                    {
                        this.Status = "Waystone page controls are incomplete — waiting for the active Tier panel";
                        this.Decision = "ReadStash";
                        return;
                    }

                    if (string.Equals(snapshot.CurrentPageName, "1", StringComparison.Ordinal))
                    {
                        this.nextSpecializedPage = 2;
                        this.stashPhase = StashPhase.FindItem;
                        this.Status = $"Waystone Tier {this.targetWaystoneTier}, page 1 ready";
                        this.Decision = "FindClickableWaystone";
                        return;
                    }

                    this.BeginSelectPage(
                        ctx,
                        snapshot,
                        pageNumber: 1,
                        phaseAfterSelection: StashPhase.FindItem);
                    return;

                case StashPhase.SelectTier:
                    var tierControl = snapshot.Tiers[this.targetWaystoneTier - 1];
                    if (tierControl.Count <= 0)
                    {
                        var emptyTier = this.targetWaystoneTier;
                        this.targetWaystoneTier = 0;
                        this.Status = $"Tier {emptyTier} has no Waystone — selection cancelled";
                        this.Decision = "NoWaystoneInRange";
                        return;
                    }

                    if (string.Equals(
                            snapshot.CurrentTierName,
                            tierControl.Name,
                            StringComparison.Ordinal))
                    {
                        this.stashPhase = StashPhase.SelectPage;
                        this.Status = $"Waystone Tier {this.targetWaystoneTier} selected — checking page 1";
                        this.Decision = "SelectWaystonePage";
                        return;
                    }

                    if (!ctx.Interaction.BeginUiElement(
                            tierControl.UiAddress,
                            $"Waystone Tier {this.targetWaystoneTier}",
                            current => IsConfiguredTierReady(
                                current,
                                this.activeConfiguredTab,
                                tierControl.Name),
                            maxClickAttempts: 3,
                            uiSource: "tier control"))
                    {
                        this.Status = "Waystone Tier control is unavailable";
                        this.Decision = "SelectWaystoneTier";
                        return;
                    }

                    this.pendingInteraction = PendingInteraction.SelectTier;
                    this.TickStartedInteraction(ctx);
                    return;

                case StashPhase.FindItem:
                    if (snapshot.Tiers[this.targetWaystoneTier - 1].Count <= 0)
                    {
                        this.targetWaystoneTier = 0;
                        this.Status = $"Waystone stock changed — rechecking Tier {minTier}-{maxTier}";
                        this.Decision = "RecheckWaystoneTier";
                        return;
                    }

                    if (this.TickWithdrawFromSnapshot(
                            ctx,
                            snapshot,
                            minTier,
                            maxTier,
                            this.targetWaystoneTier))
                    {
                        return;
                    }

                    if (snapshot.Pages.Count > 0 && this.nextSpecializedPage <= 6)
                    {
                        this.BeginSelectPage(
                            ctx,
                            snapshot,
                            this.nextSpecializedPage,
                            StashPhase.FindItem);
                        return;
                    }

                    this.exhaustedWaystoneTiers.Add(this.targetWaystoneTier);
                    this.Status = $"No Waystone in Tier {this.targetWaystoneTier} passed filters — checking lower Tier";
                    this.Decision = "CheckNextWaystoneTier";
                    this.targetWaystoneTier = 0;
                    this.nextSpecializedPage = 1;
                    this.stashPhase = StashPhase.SelectTier;
                    return;
            }
        }

        private void BeginSelectWaystoneTab(BotContext ctx, StashSnapshot snapshot, string configuredTab)
        {
            var tab = snapshot.Tabs.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, configuredTab, StringComparison.OrdinalIgnoreCase));
            if (tab == null)
            {
                this.Status = $"Configured Waystone Tab '{configuredTab}' is not visible";
                this.Decision = "FindWaystoneTab";
                return;
            }

            var now = DateTime.UtcNow;
            if (!string.Equals(this.delayedTabTarget, configuredTab, StringComparison.OrdinalIgnoreCase))
            {
                this.delayedTabTarget = configuredTab;
                this.tabActionNotBeforeUtc = now + TimeSpan.FromMilliseconds(Random.Shared.Next(450, 901));
            }

            if (now < this.tabActionNotBeforeUtc)
            {
                this.Status = $"Pausing before Waystone Tab '{tab.Name}' ({(this.tabActionNotBeforeUtc - now).TotalSeconds:F1}s)";
                this.Decision = "HumanTabDelay";
                return;
            }

            var sideListFirst = snapshot.IsAllTabsListOpen && tab.FallbackUiAddress != IntPtr.Zero;
            var primaryAddress = sideListFirst ? tab.FallbackUiAddress : tab.UiAddress;
            var secondaryAddress = sideListFirst ? tab.UiAddress : IntPtr.Zero;
            var directTabUnavailable = primaryAddress == IntPtr.Zero;
            var beganInteraction = this.useTabScrollFallback || directTabUnavailable
                ? this.BeginTabScrollFallback(ctx, snapshot, tab, configuredTab)
                : ctx.Interaction.BeginUiElement(
                    primaryAddress,
                    $"Waystone Tab '{tab.Name}'",
                    current => IsConfiguredTabReady(current, configuredTab),
                    fallbackUiAddress: secondaryAddress,
                    maxClickAttempts: 4,
                    uiSource: sideListFirst ? "all-tabs list" : "top tab",
                    fallbackUiSource: "top tab");
            if (!beganInteraction)
            {
                if (this.useTabScrollFallback || directTabUnavailable)
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

            this.pendingInteraction = PendingInteraction.SelectTab;
            this.TickStartedInteraction(ctx);
        }

        private void BeginSelectCurrencyTab(BotContext ctx, StashSnapshot snapshot, string configuredTab)
        {
            var tab = snapshot.Tabs.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, configuredTab, StringComparison.OrdinalIgnoreCase));
            if (tab == null)
            {
                this.Status = $"Configured Currency Tab '{configuredTab}' is not visible";
                this.Decision = "FindCurrencyTab";
                return;
            }

            var now = DateTime.UtcNow;
            var delayKey = $"currency:{configuredTab}";
            if (!string.Equals(this.delayedTabTarget, delayKey, StringComparison.OrdinalIgnoreCase))
            {
                this.delayedTabTarget = delayKey;
                this.tabActionNotBeforeUtc = now + TimeSpan.FromMilliseconds(Random.Shared.Next(450, 901));
            }

            if (now < this.tabActionNotBeforeUtc)
            {
                this.Status = $"Pausing before Currency Tab '{tab.Name}' ({(this.tabActionNotBeforeUtc - now).TotalSeconds:F1}s)";
                this.Decision = "HumanTabDelay";
                return;
            }

            var sideListFirst = snapshot.IsAllTabsListOpen && tab.FallbackUiAddress != IntPtr.Zero;
            var primaryAddress = sideListFirst ? tab.FallbackUiAddress : tab.UiAddress;
            var secondaryAddress = sideListFirst ? tab.UiAddress : IntPtr.Zero;
            var began = primaryAddress != IntPtr.Zero
                ? ctx.Interaction.BeginUiElement(
                    primaryAddress,
                    $"Currency Tab '{tab.Name}'",
                    current => IsConfiguredTabReady(current, configuredTab),
                    fallbackUiAddress: secondaryAddress,
                    maxClickAttempts: 4,
                    uiSource: sideListFirst ? "all-tabs list" : "top tab",
                    fallbackUiSource: "top tab")
                : this.BeginTabScrollFallback(ctx, snapshot, tab, configuredTab);
            if (!began)
            {
                this.Status = "Currency Tab control is unavailable";
                this.Decision = "SelectCurrencyTab";
                return;
            }

            this.pendingInteraction = PendingInteraction.SelectCurrencyTab;
            this.TickStartedInteraction(ctx);
        }

        private void BeginSelectPage(
            BotContext ctx,
            StashSnapshot snapshot,
            int pageNumber,
            StashPhase phaseAfterSelection)
        {
            var pageName = pageNumber.ToString();
            if (string.Equals(snapshot.CurrentPageName, pageName, StringComparison.Ordinal))
            {
                this.nextSpecializedPage = Math.Max(this.nextSpecializedPage, pageNumber + 1);
                this.stashPhase = phaseAfterSelection;
                return;
            }

            var page = snapshot.Pages.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, pageName, StringComparison.Ordinal));
            if (page == null || page.UiAddress == IntPtr.Zero ||
                !ctx.Interaction.BeginUiElement(
                    page.UiAddress,
                    $"Waystone page {pageName}",
                    current => IsConfiguredPageReady(
                        current,
                        this.activeConfiguredTab,
                        snapshot.CurrentTierName,
                        pageName),
                    maxClickAttempts: 3,
                    uiSource: "page tab"))
            {
                this.Status = $"Waystone page {pageName} control is unavailable";
                this.Decision = "SelectWaystonePage";
                return;
            }

            this.pendingPageNumber = pageNumber;
            this.phaseAfterPageSelection = phaseAfterSelection;
            this.pendingInteraction = PendingInteraction.SelectPage;
            this.TickStartedInteraction(ctx);
        }

        private bool TickWithdrawFromSnapshot(
            BotContext ctx,
            StashSnapshot snapshot,
            int minTier,
            int maxTier,
            int targetTier)
        {
            var candidates = snapshot.VisibleItems
                .Select(control => new
                {
                    Ui = control,
                    Tier = TryGetWaystoneTier(control.ItemPath, out var tier) ? tier : 0,
                    Item = snapshot.Inventory.Items.FirstOrDefault(item => item.ItemAddress == control.ItemAddress),
                })
                .Where(candidate => candidate.Tier >= minTier && candidate.Tier <= maxTier &&
                                    (targetTier == 0 || candidate.Tier == targetTier) &&
                                    candidate.Item != null &&
                                    WaystoneCrafting.GetNextAction(candidate.Item, ctx.Settings, out _) !=
                                    WaystoneCraftingAction.Reject)
                .OrderByDescending(candidate => candidate.Tier)
                .ThenBy(candidate => candidate.Ui.UiAddress.ToInt64())
                .ToArray();
            if (candidates.Length == 0)
            {
                this.Status = targetTier > 0
                    ? $"Scanning Waystone page {snapshot.CurrentPageName} for Tier {targetTier}"
                    : $"No clickable Waystone in Tier {minTier}-{maxTier} on the selected ordinary tab";
                this.Decision = "FindClickableWaystone";
                return false;
            }

            var candidate = candidates[0];
            var baselineCount = CountWaystonesInRange(this.ReadInventory(ctx), minTier, maxTier);
            var itemAddress = candidate.Ui.ItemAddress;
            if (!ctx.Interaction.BeginUiCtrlClick(
                    candidate.Ui.UiAddress,
                    $"Tier {candidate.Tier} Waystone",
                    current =>
                    {
                        var currentInventory = this.ReadInventory(current);
                        return currentInventory.State == InventorySnapshotState.Ready &&
                               (currentInventory.Items.Any(item => item.Item.Address == itemAddress) ||
                                CountWaystonesInRange(currentInventory, minTier, maxTier) > baselineCount);
                    }))
            {
                this.Status = "Waystone item UI is unavailable";
                this.Decision = "WithdrawWaystone";
                return true;
            }

            this.withdrawalAttempted = true;
            this.pendingInteraction = PendingInteraction.Withdraw;
            this.TickStartedInteraction(ctx);
            return true;
        }

        private void TickStartedInteraction(BotContext ctx)
        {
            var result = ctx.Interaction.Tick(ctx);
            this.Status = ctx.Interaction.Status;
            this.Decision = ctx.Interaction.Phase.ToString();
            if (result == InteractionResult.Failed)
            {
                this.HandleInteractionFailure(ctx);
            }
        }

        private void HandleInteractionSuccess(BotContext ctx)
        {
            var completed = this.pendingInteraction;
            ctx.Interaction.Reset();
            this.pendingInteraction = PendingInteraction.None;
            this.nextRetryUtc = DateTime.MinValue;
            switch (completed)
            {
                case PendingInteraction.SelectTab:
                    this.stashPhase = StashPhase.SelectTier;
                    this.targetWaystoneTier = 0;
                    this.nextSpecializedPage = 1;
                    break;
                case PendingInteraction.SelectPage:
                    this.nextSpecializedPage = Math.Max(this.nextSpecializedPage, this.pendingPageNumber + 1);
                    this.pendingPageNumber = 0;
                    this.stashPhase = this.phaseAfterPageSelection;
                    break;
                case PendingInteraction.SelectTier:
                    this.stashPhase = StashPhase.SelectPage;
                    break;
                case PendingInteraction.Withdraw:
                    // The success predicate already proved that main inventory received the item.
                    this.inventorySnapshot = null;
                    this.lastInventoryReadUtc = DateTime.MinValue;
                    break;
                case PendingInteraction.SelectCurrencyTab:
                    this.tabActionNotBeforeUtc = DateTime.MinValue;
                    this.delayedTabTarget = string.Empty;
                    break;
                case PendingInteraction.UseWisdom:
                case PendingInteraction.UseAlchemy:
                case PendingInteraction.UseExalted:
                    this.activeCraftItemAddress = IntPtr.Zero;
                    this.inventorySnapshot = null;
                    this.lastInventoryReadUtc = DateTime.MinValue;
                    break;
                case PendingInteraction.CursorCleanup:
                    break;
            }
        }

        private void HandleInteractionFailure(BotContext ctx)
        {
            var failed = this.pendingInteraction;
            var failure = ctx.Interaction.LastFailure;
            ctx.Interaction.Reset();
            this.pendingInteraction = PendingInteraction.None;
            if (failed is PendingInteraction.UseWisdom or
                PendingInteraction.UseAlchemy or
                PendingInteraction.UseExalted)
            {
                if (this.activeCraftItemAddress != IntPtr.Zero)
                {
                    this.stoppedCraftItems.Add(this.activeCraftItemAddress);
                }

                this.activeCraftItemAddress = IntPtr.Zero;
                this.Status = string.IsNullOrWhiteSpace(failure)
                    ? "Waystone crafting was not confirmed — item stopped"
                    : $"{failure} — item stopped";
                this.Decision = "CraftingUnconfirmed";
                return;
            }

            if (failed == PendingInteraction.CursorCleanup)
            {
                this.Status = string.IsNullOrWhiteSpace(failure)
                    ? "Currency cursor cleanup was not confirmed"
                    : failure;
                this.Decision = "CursorCleanupBlocked";
                return;
            }

            if (failed == PendingInteraction.Withdraw)
            {
                this.Status = string.IsNullOrWhiteSpace(failure)
                    ? "Waystone withdrawal was not confirmed"
                    : failure;
                this.Decision = "WithdrawalUnconfirmed";
                return;
            }

            if (failed == PendingInteraction.SelectTab)
            {
                this.useTabScrollFallback = !this.useTabScrollFallback;
                this.stashPhase = StashPhase.SelectTab;
            }
            else if (failed == PendingInteraction.SelectCurrencyTab)
            {
                this.tabActionNotBeforeUtc = DateTime.MinValue;
                this.delayedTabTarget = string.Empty;
            }
            else if (failed == PendingInteraction.SelectTier)
            {
                this.stashPhase = StashPhase.SelectTier;
            }
            else if (failed == PendingInteraction.SelectPage)
            {
                this.stashPhase = this.pendingPageNumber == 1
                    ? StashPhase.SelectPage
                    : this.phaseAfterPageSelection;
            }

            this.nextRetryUtc = DateTime.UtcNow + RetryDelay;
            this.Status = failure;
            this.Decision = "RetryStashAction";
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
            return ctx.Interaction.BeginUiScroll(
                snapshot.TopTabBarUiAddress,
                $"Waystone Tab '{targetTab.Name}'",
                wheelDirection,
                current => IsConfiguredTabReady(current, configuredTab),
                maxScrollAttempts: Math.Min(32, tabDistance + 2));
        }

        private void ResetStashWorkflow(bool preserveWithdrawalGuard = false)
        {
            this.tabActionNotBeforeUtc = DateTime.MinValue;
            this.delayedTabTarget = string.Empty;
            this.useTabScrollFallback = false;
            this.targetWaystoneTier = 0;
            this.nextSpecializedPage = 1;
            this.exhaustedWaystoneTiers.Clear();
            this.pendingPageNumber = 0;
            this.stashPhase = StashPhase.SelectTab;
            this.pendingInteraction = PendingInteraction.None;
            this.phaseAfterPageSelection = StashPhase.FindItem;
            if (!preserveWithdrawalGuard)
            {
                this.withdrawalAttempted = false;
                this.stoppedCraftItems.Clear();
            }
        }

        private static (int MinTier, int MaxTier) GetTierRange(AutoExile2Settings settings)
        {
            var minTier = Math.Clamp(settings.MinTier, 1, 16);
            var maxTier = Math.Clamp(settings.MaxTier, 1, 16);
            return minTier <= maxTier ? (minTier, maxTier) : (maxTier, minTier);
        }

        private static int CountWaystonesInRange(InventorySnapshot inventory, int minTier, int maxTier) =>
            inventory.Items.Count(entry =>
                entry.WaystoneTier is int tier && tier >= minTier && tier <= maxTier);

        private static bool CurrencyWasConsumed(
            BotContext ctx,
            string configuredTab,
            string currencyPath,
            int slotX,
            int slotY,
            int oldStackCount)
        {
            if (!ctx.GameUi.IsStashOpen)
            {
                return false;
            }

            var snapshot = ctx.GameUi.Stash.ReadSnapshot(InventorySnapshotDetailLevel.Basic);
            if (snapshot.State != StashSnapshotState.Ready ||
                !string.Equals(snapshot.CurrentTabName, configuredTab, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var result = ctx.Area.ServerDataObject.ReadInventoryItemAt(
                InventoryName.StashInventoryId,
                slotX,
                slotY,
                InventorySnapshotDetailLevel.Full);
            if (result.State != InventorySnapshotState.Ready)
            {
                return false;
            }

            if (result.Item == null)
            {
                return true;
            }

            if (!string.Equals(result.Item.Path, currencyPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return result.Item.StackCount.HasValue && result.Item.StackCount.Value < oldStackCount;
        }

        private static bool CraftingActionChangedSlot(
            BotContext ctx,
            WaystoneCraftingAction action,
            int slotX,
            int slotY,
            Rarity? oldRarity,
            int oldModCount)
        {
            var result = ctx.Area.ServerDataObject.ReadInventoryItemAt(
                InventoryName.MainInventory1,
                slotX,
                slotY,
                InventorySnapshotDetailLevel.Full);
            var item = result.Item;
            if (result.State != InventorySnapshotState.Ready || item == null || !item.IsWaystone)
            {
                return false;
            }

            return action switch
            {
                WaystoneCraftingAction.Identify => item.ExplicitMods.Count > 0,
                WaystoneCraftingAction.Alchemy =>
                    item.Rarity == Rarity.Rare &&
                    (oldRarity != Rarity.Rare || item.ExplicitMods.Count > oldModCount),
                WaystoneCraftingAction.Exalted => item.ExplicitMods.Count > oldModCount,
                _ => false,
            };
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

        private static bool IsConfiguredPageReady(
            BotContext ctx,
            string configuredTab,
            string tierName,
            string pageName)
        {
            if (!ctx.GameUi.IsStashOpen)
            {
                return false;
            }

            var snapshot = ctx.GameUi.Stash.ReadSnapshot();
            return snapshot.State == StashSnapshotState.Ready &&
                   string.Equals(snapshot.CurrentTabName, configuredTab, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(snapshot.CurrentTierName, tierName, StringComparison.Ordinal) &&
                   string.Equals(snapshot.CurrentPageName, pageName, StringComparison.Ordinal);
        }

        private static bool IsConfiguredTierReady(BotContext ctx, string configuredTab, string tierName)
        {
            if (!ctx.GameUi.IsStashOpen)
            {
                return false;
            }

            var snapshot = ctx.GameUi.Stash.ReadSnapshot();
            return snapshot.State == StashSnapshotState.Ready &&
                   string.Equals(snapshot.CurrentTabName, configuredTab, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(snapshot.CurrentTierName, tierName, StringComparison.Ordinal);
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

        private enum StashPhase
        {
            SelectTab,
            SelectPage,
            SelectTier,
            FindItem,
        }

        private enum PendingInteraction
        {
            None,
            SelectTab,
            SelectPage,
            SelectTier,
            Withdraw,
            SelectCurrencyTab,
            UseWisdom,
            UseAlchemy,
            UseExalted,
            CursorCleanup,
        }
    }
}
