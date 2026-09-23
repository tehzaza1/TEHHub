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
    /// select the configured Waystone Tab, withdraw Waystones in the configured Tier range, and
    /// use Wisdom/Alchemy/Exalted directly from the configured Currency Tab.
    /// </summary>
    internal sealed class HideoutFlow
    {
        private static readonly TimeSpan InventoryReadInterval = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan ActiveSnapshotRefreshInterval = TimeSpan.FromMilliseconds(200);
        private static readonly TimeSpan PageScanSettleDelay = TimeSpan.FromMilliseconds(400);
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
        private int activeBatchSize;
        private bool activeBatchEnabled;
        private string activeCurrencyTab = string.Empty;
        private bool waystoneBatchPrimed;
        private readonly HashSet<int> exhaustedWaystoneTiers = new();
        private readonly HashSet<IntPtr> stoppedCraftItems = new();
        private readonly object withdrawalCtrlHoldOwner = new();
        private readonly List<CraftActionBaseline> activeCraftActions = new();
        private readonly Dictionary<CraftRetryKey, int> craftRetryCounts = new();
        private CraftRetryRequest? pendingCraftRetry;
        private bool useTabScrollFallback;
        private bool withdrawalAttempted;
        private bool withdrawalNoClickRetryUsed;
        private bool withdrawalCtrlBatchActive;
        private int withdrawalCtrlBatchRemaining;
        private string withdrawalCtrlBatchTab = string.Empty;
        private string withdrawalCtrlBatchTier = string.Empty;
        private string withdrawalCtrlBatchPage = string.Empty;
        private WithdrawalBaseline? withdrawalBaseline;
        private bool withdrawalStashChanged;
        private bool withdrawalFinalVerificationPending;
        private DateTime withdrawalFinalVerificationNotBeforeUtc = DateTime.MinValue;
        private int withdrawalVerificationRetries;
        private DateTime nextActiveSnapshotRefreshUtc = DateTime.MinValue;
        private bool waystoneStockExhausted;
        private DateTime nextWaystoneStockRecheckUtc = DateTime.MinValue;
        private int targetWaystoneTier;
        private int nextSpecializedPage = 1;
        private int pendingPageNumber;
        private int delayedPageNumber;
        private DateTime pageClickNotBeforeUtc = DateTime.MinValue;
        private string settlingPageName = string.Empty;
        private DateTime pageScanNotBeforeUtc = DateTime.MinValue;
        private bool forcePageStashRefresh;
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

        public void Reset(BotContext ctx, bool preserveBatchPrimed = false)
        {
            ctx.Interaction.Cancel(ctx.Settings);
            this.interaction = ctx.Interaction;
            this.inventorySnapshot = null;
            this.lastInventoryReadUtc = DateTime.MinValue;
            this.nextRetryUtc = DateTime.MinValue;
            if (!preserveBatchPrimed)
            {
                this.activeConfiguredTab = string.Empty;
                this.activeFilterSignature = string.Empty;
                this.activeMinTier = 0;
                this.activeMaxTier = 0;
                this.activeBatchSize = 0;
                this.activeBatchEnabled = false;
                this.activeCurrencyTab = string.Empty;
                this.waystoneBatchPrimed = false;
            }

            this.activeCraftActions.Clear();
            this.pendingCraftRetry = null;
            if (!preserveBatchPrimed)
            {
                this.craftRetryCounts.Clear();
            }
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
                this.EndWithdrawalCtrlBatch(ctx);
                this.Status = "Waiting for foreground game and inactive chat";
                this.Decision = "WaitForSafeInput";
                return;
            }

            if (!ctx.World.AreaDetails.IsHideout)
            {
                ctx.Interaction.Cancel(ctx.Settings);
                this.EndWithdrawalCtrlBatch(ctx);
                this.Status = "Town detected — hideout stash automation is disabled";
                this.Decision = "WaitForHideout";
                return;
            }

            if (ctx.GameUi.IsVendorOpen)
            {
                ctx.Interaction.Cancel(ctx.Settings);
                this.EndWithdrawalCtrlBatch(ctx);
                this.Status = "Vendor is open — waiting for a safe UI state";
                this.Decision = "WaitForVendorClose";
                return;
            }

            if (ctx.Interaction.CurrencyMayBeActive || ctx.Interaction.CurrencyClickInFlight ||
                this.pendingInteraction == PendingInteraction.CursorCleanup)
            {
                this.EndWithdrawalCtrlBatch(ctx);
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
                this.EndWithdrawalCtrlBatch(ctx);
                this.Status = "Cursor1 is changing — waiting";
                this.Decision = "ReadCursor1";
                return;
            }

            if (cursorSlot.State != InventorySnapshotState.Ready)
            {
                this.EndWithdrawalCtrlBatch(ctx);
                this.Status = $"Cursor1 is {cursorSlot.State} — automation stopped";
                this.Decision = "Cursor1Unavailable";
                return;
            }

            if (cursorSlot.Item != null)
            {
                this.EndWithdrawalCtrlBatch(ctx);
                this.Status = $"Unexpected item on Cursor1: {cursorSlot.Item.Path}";
                this.Decision = "ClearCursorManually";
                return;
            }

            var (minTier, maxTier) = GetTierRange(ctx.Settings);
            var configuredTab = (ctx.Settings.WaystoneTab ?? string.Empty).Trim();
            var configuredCurrencyTab = (ctx.Settings.CurrencyTab ?? string.Empty).Trim();
            var filterSignature = WaystoneFilter.GetSettingsSignature(ctx.Settings);
            var batchEnabled = ctx.Settings.EnableWaystoneBatch;
            var batchSize = batchEnabled ? Math.Clamp(ctx.Settings.WaystoneBatchSize, 5, 10) : 1;
            if (!string.Equals(this.activeConfiguredTab, configuredTab, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(this.activeCurrencyTab, configuredCurrencyTab, StringComparison.OrdinalIgnoreCase) ||
                this.activeMinTier != minTier || this.activeMaxTier != maxTier ||
                !string.Equals(this.activeFilterSignature, filterSignature, StringComparison.Ordinal) ||
                this.activeBatchEnabled != batchEnabled || this.activeBatchSize != batchSize)
            {
                ctx.Interaction.Cancel(ctx.Settings);
                this.activeConfiguredTab = configuredTab;
                this.activeCurrencyTab = configuredCurrencyTab;
                this.activeMinTier = minTier;
                this.activeMaxTier = maxTier;
                this.activeFilterSignature = filterSignature;
                this.activeBatchEnabled = batchEnabled;
                this.activeBatchSize = batchSize;
                this.waystoneBatchPrimed = false;
                this.craftRetryCounts.Clear();
                this.pendingCraftRetry = null;
                this.ResetStashWorkflow();
                this.waystoneStockExhausted = false;
                this.nextWaystoneStockRecheckUtc = DateTime.MinValue;
            }

            if (this.pendingCraftRetry != null)
            {
                this.TickCraftRetry(ctx);
                return;
            }

            var inventory = this.ReadInventory(ctx);
            if (inventory.State != InventorySnapshotState.Ready)
            {
                ctx.Interaction.Cancel(ctx.Settings);
                this.EndWithdrawalCtrlBatch(ctx);
                this.Status = $"Inventory {inventory.State}: {inventory.Diagnostic}";
                this.Decision = "ReadInventory";
                return;
            }

            if (this.withdrawalAttempted && this.withdrawalBaseline is { } completedWithdrawal &&
                IsWithdrawalConfirmed(inventory, completedWithdrawal))
            {
                if (this.pendingInteraction == PendingInteraction.Withdraw)
                {
                    if (!ctx.Interaction.LastInputClickCompleted)
                    {
                        this.Status = "Waiting for Waystone Ctrl+click mouse-up before advancing the batch";
                        this.Decision = "WaitForWithdrawalClickCompletion";
                        return;
                    }

                    this.CompleteConfirmedWithdrawal(ctx);
                }
                else if (!ctx.Interaction.IsBusy && this.pendingInteraction == PendingInteraction.None)
                {
                    this.EndWithdrawalCtrlBatch(ctx);
                    this.ClearWithdrawalGuard();
                }
            }

            var inventoryWaystones = inventory.Items
                .Where(item => item.WaystoneTier is int tier && tier >= minTier && tier <= maxTier)
                .OrderByDescending(item => item.WaystoneTier)
                .ThenBy(item => item.SlotStartY)
                .ThenBy(item => item.SlotStartX)
                .ToArray();
            var assessedInventoryWaystones = inventoryWaystones
                .Select(item =>
                {
                    var action = WaystoneCrafting.GetNextAction(item, ctx.Settings, out var reason);
                    return new { Item = item, Action = action, Reason = reason };
                })
                .ToArray();
            var usableWaystoneCount = assessedInventoryWaystones.Count(candidate =>
                candidate.Action != WaystoneCraftingAction.Reject &&
                !this.stoppedCraftItems.Contains(candidate.Item.ItemAddress));
            var readyWaystoneCount = assessedInventoryWaystones.Count(candidate =>
                candidate.Action == WaystoneCraftingAction.Ready &&
                !this.stoppedCraftItems.Contains(candidate.Item.ItemAddress));
            if (batchEnabled && this.waystoneBatchPrimed && readyWaystoneCount > 1)
            {
                ctx.Interaction.Cancel(ctx.Settings);
                this.EndWithdrawalCtrlBatch(ctx);
                this.Status = $"Waystone batch ready — {readyWaystoneCount} remain; refill starts at 1";
                this.Decision = "WaystoneBatchHolding";
                return;
            }

            if (batchEnabled && this.waystoneBatchPrimed && readyWaystoneCount <= 1)
            {
                if (this.waystoneStockExhausted && DateTime.UtcNow < this.nextWaystoneStockRecheckUtc)
                {
                    ctx.Interaction.Cancel(ctx.Settings);
                    this.EndWithdrawalCtrlBatch(ctx);
                    this.Status = $"Only {readyWaystoneCount} ready Waystone(s) remain; waiting to recheck stash stock";
                    this.Decision = "WaystoneBatchHolding";
                    return;
                }

                this.waystoneBatchPrimed = false;
                this.waystoneStockExhausted = false;
                this.nextWaystoneStockRecheckUtc = DateTime.MinValue;
                this.ResetStashWorkflow();
            }

            if (usableWaystoneCount == 0 && this.waystoneStockExhausted &&
                DateTime.UtcNow >= this.nextWaystoneStockRecheckUtc)
            {
                this.waystoneStockExhausted = false;
                this.nextWaystoneStockRecheckUtc = DateTime.MinValue;
                this.ResetStashWorkflow();
            }

            // Plugin policy: prioritize already-ready items, then craftable ones. The SDK
            // only supplies item/UI state and does not decide which Waystone to take.
            var assessedWaystones = assessedInventoryWaystones
                .Where(candidate => candidate.Action != WaystoneCraftingAction.Reject &&
                                    !this.stoppedCraftItems.Contains(candidate.Item.ItemAddress))
                .OrderBy(candidate => candidate.Action == WaystoneCraftingAction.Ready ? 0 : 1)
                .ThenByDescending(candidate => candidate.Item.WaystoneTier)
                .ThenBy(candidate => candidate.Item.SlotStartY)
                .ThenBy(candidate => candidate.Item.SlotStartX)
                .Take(batchSize)
                .ToArray();
            var batchWaystones = assessedWaystones.Select(candidate => candidate.Item).ToArray();
            var actionable = assessedWaystones
                .Where(candidate => candidate.Action != WaystoneCraftingAction.Ready)
                .FirstOrDefault();
            this.EligibleWaystoneCount = assessedWaystones.Count(candidate =>
                candidate.Action == WaystoneCraftingAction.Ready);
            if (usableWaystoneCount >= batchSize || this.waystoneStockExhausted)
            {
                var batchReady = batchWaystones.Length > 0 && assessedWaystones.All(candidate =>
                    candidate.Action == WaystoneCraftingAction.Ready &&
                    !this.stoppedCraftItems.Contains(candidate.Item.ItemAddress));
                if (batchReady)
                {
                    ctx.Interaction.Cancel(ctx.Settings);
                    this.EndWithdrawalCtrlBatch(ctx);
                    this.waystoneBatchPrimed = true;
                    this.Status = $"Inventory ready — {this.EligibleWaystoneCount}/{batchSize} Waystone(s) in Tier {minTier}-{maxTier}";
                    this.Decision = "WaystoneReady";
                    return;
                }

                if (actionable != null)
                {
                    this.TickCraftWaystone(ctx, actionable.Item, actionable.Action, actionable.Reason, batchWaystones);
                    return;
                }

                if (inventoryWaystones.Length > 0)
                {
                    ctx.Interaction.Cancel(ctx.Settings);
                    this.EndWithdrawalCtrlBatch(ctx);
                    this.Status = "Inventory Waystone(s) cannot be prepared or did not pass the active filter";
                    this.Decision = "NoInventoryWaystonePassedFilters";
                    return;
                }

                ctx.Interaction.Cancel(ctx.Settings);
                this.EndWithdrawalCtrlBatch(ctx);
                this.Status = $"No Waystone in Tier {minTier}-{maxTier} is available to prepare";
                this.Decision = "NoWaystoneInRange";
                return;
            }

            if (configuredTab.Length == 0)
            {
                ctx.Interaction.Cancel(ctx.Settings);
                this.EndWithdrawalCtrlBatch(ctx);
                this.Status = $"No Waystone in Tier {minTier}-{maxTier} — configure Waystone Tab";
                this.Decision = "ConfigureWaystoneTab";
                return;
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
                    this.EndWithdrawalCtrlBatch(ctx);
                    this.Status = "Another game panel is open — waiting before stash interaction";
                    this.Decision = "WaitForPanelClose";
                    return;
                }

                this.TickOpenStash(ctx);
                return;
            }

            this.TickStash(ctx, configuredTab, minTier, maxTier);
        }

        private InventorySnapshot ReadInventory(BotContext ctx, bool forceRefresh = false)
        {
            var now = DateTime.UtcNow;
            if (forceRefresh || this.inventorySnapshot == null || now - this.lastInventoryReadUtc >= InventoryReadInterval)
            {
                this.inventorySnapshot = ctx.Area.ServerDataObject.ReadInventorySnapshot(
                    InventoryName.MainInventory1,
                    InventorySnapshotDetailLevel.Full,
                    forceRefresh);
                this.lastInventoryReadUtc = now;
            }

            return this.inventorySnapshot;
        }

        private StashSnapshot? RefreshActiveSnapshots(BotContext ctx)
        {
            var actionInProgress = this.pendingInteraction is PendingInteraction.Withdraw or
                PendingInteraction.UseWisdom or PendingInteraction.UseAlchemy or PendingInteraction.UseExalted or
                PendingInteraction.CursorCleanup;
            var scanningWaystonePages = this.targetWaystoneTier > 0 &&
                this.stashPhase is StashPhase.SelectPage or StashPhase.FindItem;
            if (!actionInProgress && !scanningWaystonePages)
            {
                return null;
            }

            var now = DateTime.UtcNow;
            if (now < this.nextActiveSnapshotRefreshUtc)
            {
                return null;
            }

            this.inventorySnapshot = ctx.Area.ServerDataObject.ReadInventorySnapshot(
                InventoryName.MainInventory1,
                InventorySnapshotDetailLevel.Full,
                forceRefresh: true);
            this.lastInventoryReadUtc = now;
            var stashSnapshot = ctx.GameUi.Stash.ReadSnapshot(
                InventorySnapshotDetailLevel.Full,
                forceInventoryRefresh: true);
            if (this.withdrawalBaseline is { } baseline && IsStashWithdrawalChanged(stashSnapshot, baseline))
            {
                this.withdrawalStashChanged = true;
            }

            this.nextActiveSnapshotRefreshUtc = now + ActiveSnapshotRefreshInterval;
            return stashSnapshot;
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
            string reason,
            IReadOnlyList<InventorySnapshotItem> inventoryWaystones)
        {
            this.EndWithdrawalCtrlBatch(ctx);
            if (ctx.Interaction.IsBusy)
            {
                this.RefreshActiveSnapshots(ctx);
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

            var currencyControls = snapshot.VisibleItems
                .Where(item => string.Equals(item.ItemPath, currencyPath, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            StashVisibleItemInfo? control = null;
            InventorySnapshotItem? currency = null;
            var currencySlotX = -1;
            var currencySlotY = -1;
            var matchingInventoryItems = 0;
            foreach (var visibleCurrency in currencyControls)
            {
                var inventoryCurrencyItem = snapshot.Inventory.Items.FirstOrDefault(item =>
                    item.ItemAddress == visibleCurrency.ItemAddress &&
                    string.Equals(item.Path, currencyPath, StringComparison.OrdinalIgnoreCase));
                if (inventoryCurrencyItem != null)
                {
                    matchingInventoryItems++;
                    if (inventoryCurrencyItem.StackCount is > 0 &&
                        inventoryCurrencyItem.SlotStartX >= 0 &&
                        inventoryCurrencyItem.SlotStartY >= 0)
                    {
                        control = visibleCurrency;
                        currency = inventoryCurrencyItem;
                        currencySlotX = inventoryCurrencyItem.SlotStartX;
                        currencySlotY = inventoryCurrencyItem.SlotStartY;
                        break;
                    }
                }

                var visibleCurrencyItem = ctx.GameUi.Stash.ReadVisibleItemDetails(visibleCurrency);
                if (visibleCurrencyItem?.StackCount is > 0 &&
                    visibleCurrencyItem.ItemAddress == visibleCurrency.ItemAddress &&
                    string.Equals(visibleCurrencyItem.Path, currencyPath, StringComparison.OrdinalIgnoreCase))
                {
                    control = visibleCurrency;
                    currency = visibleCurrencyItem;
                    // The UI confirms the item and its stack, but not its server-inventory
                    // coordinates. Keep -1/-1 so all later reads use this same visible item.
                    break;
                }
            }

            if (control == null || currency?.StackCount is not > 0)
            {
                this.Status = $"No readable {currencyLabel} stack in Currency Tab '{configuredTab}' " +
                              $"(visible controls: {currencyControls.Length}, " +
                              $"StashInventoryId matches: {matchingInventoryItems})";
                this.Decision = "CurrencyUnavailable";
                return;
            }

            var currencyStackCount = currency.StackCount.GetValueOrDefault();

            var targets = new List<CurrencyUseTarget>();
            var targetBaselines = new List<CraftActionBaseline>();
            var exaltedUsesByWaystone = new Dictionary<IntPtr, int>();
            var targetModCount = Math.Clamp(ctx.Settings.MaxWaystoneMods, 4, 6);
            foreach (var targetWaystone in inventoryWaystones)
            {
                var nextAction = WaystoneCrafting.GetNextAction(targetWaystone, ctx.Settings, out _);
                if (nextAction is WaystoneCraftingAction.Ready or WaystoneCraftingAction.Reject ||
                    nextAction != action ||
                    this.stoppedCraftItems.Contains(targetWaystone.ItemAddress))
                {
                    continue;
                }

                var usesNeeded = action == WaystoneCraftingAction.Exalted
                    ? Math.Max(0, targetModCount - targetWaystone.ExplicitMods.Count)
                    : 1;
                if (usesNeeded <= 0)
                {
                    continue;
                }

                for (var useIndex = 0; useIndex < usesNeeded && targets.Count < currencyStackCount; useIndex++)
                {
                    var currencyCountBeforeUse = currencyStackCount - targets.Count;
                    var modsBeforeUse = targetWaystone.ExplicitMods.Count +
                                        exaltedUsesByWaystone.GetValueOrDefault(targetWaystone.ItemAddress);
                    var slotX = targetWaystone.SlotStartX;
                    var slotY = targetWaystone.SlotStartY;
                    var rarityBeforeUse = targetWaystone.Rarity;
                    var waystoneTier = targetWaystone.WaystoneTier;
                    var baseline = new CraftActionBaseline(
                        targetWaystone.ItemAddress,
                        slotX,
                        slotY,
                        waystoneTier,
                        rarityBeforeUse,
                        modsBeforeUse,
                        action,
                        configuredTab,
                        currencyPath,
                        currency.ItemAddress,
                        currencySlotX,
                        currencySlotY,
                        currencyCountBeforeUse);
                    targets.Add(new CurrencyUseTarget(
                        IntPtr.Zero,
                        current => CraftingActionChangedSlot(
                            current,
                            baseline.Action,
                            baseline.WaystoneItemAddress,
                            baseline.WaystoneSlotX,
                            baseline.WaystoneSlotY,
                            baseline.WaystoneTier,
                            baseline.Rarity,
                            baseline.ModCountBeforeUse,
                            diagnostic => baseline.VerificationDiagnostic = diagnostic,
                            itemAddress =>
                            {
                                foreach (var relatedBaseline in targetBaselines.Where(candidate =>
                                             candidate.WaystoneSlotX == baseline.WaystoneSlotX &&
                                             candidate.WaystoneSlotY == baseline.WaystoneSlotY))
                                {
                                    relatedBaseline.WaystoneItemAddress = itemAddress;
                                }
                            }),
                        current =>
                        {
                            var currentCurrencyCount = ReadCurrencyStackCount(
                                current,
                                baseline.CurrencyTab,
                                baseline.CurrencyPath,
                                baseline.CurrencyItemAddress,
                                baseline.CurrencySlotX,
                                baseline.CurrencySlotY);
                            if (currentCurrencyCount != baseline.CurrencyStackCountBeforeUse)
                            {
                                throw new InvalidOperationException("Currency stack changed before its target click");
                            }

                            baseline.WaystoneItemAddress = ReadExpectedCraftTarget(current, baseline).ItemAddress;
                            baseline.ObservedCurrencyStackCountBeforeUse = currentCurrencyCount;
                        },
                        () => baseline.VerificationDiagnostic,
                        current => ResolveCraftTargetUiAddress(current, baseline),
                        () => baseline.WaystoneItemAddress));
                    targetBaselines.Add(baseline);
                    exaltedUsesByWaystone[targetWaystone.ItemAddress] =
                        exaltedUsesByWaystone.GetValueOrDefault(targetWaystone.ItemAddress) + 1;
                }

                if (targets.Count >= currencyStackCount)
                {
                    break;
                }
            }

            if (targets.Count == 0 || !ctx.Interaction.BeginUiCurrencyUseBatch(
                    control!.UiAddress,
                    targets,
                    $"{currencyLabel} on {targets.Count} Waystone action(s)"))
            {
                this.Status = "Crafting UI is unavailable";
                this.Decision = "UseCraftingCurrency";
                return;
            }

            this.activeCraftActions.Clear();
            this.activeCraftActions.AddRange(targetBaselines);
            this.pendingInteraction = action switch
            {
                WaystoneCraftingAction.Identify => PendingInteraction.UseWisdom,
                WaystoneCraftingAction.Alchemy => PendingInteraction.UseAlchemy,
                WaystoneCraftingAction.Exalted => PendingInteraction.UseExalted,
                _ => PendingInteraction.None,
            };
            this.Status = targets.Count > 1 ? $"{reason}; applying to {targets.Count} Waystone action(s)" : reason;
            this.nextActiveSnapshotRefreshUtc = DateTime.UtcNow + ActiveSnapshotRefreshInterval;
            this.TickStartedInteraction(ctx);
        }

        private void TickCraftRetry(BotContext ctx)
        {
            var retry = this.pendingCraftRetry;
            if (retry == null)
            {
                return;
            }

            if (ctx.Interaction.IsBusy || ctx.Interaction.CurrencyMayBeActive ||
                this.pendingInteraction == PendingInteraction.CursorCleanup ||
                ctx.Interaction.Result != InteractionResult.Idle)
            {
                this.Status = "Waiting for currency state to become idle before safe retry";
                this.Decision = "WaitForCurrencyIdle";
                return;
            }

            if (DateTime.UtcNow < retry.NotBeforeUtc)
            {
                this.Status = "Waiting for game inventory snapshots to settle before safe retry";
                this.Decision = "VerifyCraftRetry";
                return;
            }

            var baseline = retry.Baseline;
            if (!ctx.GameUi.IsStashOpen)
            {
                this.StopCraftRetry(retry, "Stash closed before retry verification — Waystone stopped to avoid duplicate currency");
                return;
            }

            var stash = ctx.GameUi.Stash.ReadSnapshot(InventorySnapshotDetailLevel.Full);
            if (stash.State != StashSnapshotState.Ready ||
                !string.Equals(stash.CurrentTabName, baseline.CurrencyTab, StringComparison.OrdinalIgnoreCase))
            {
                this.StopCraftRetry(retry, "Currency Tab could not be verified after interruption — Waystone stopped");
                return;
            }

            var currencyItem = ReadCurrencyItem(
                ctx,
                stash,
                baseline.CurrencyPath,
                baseline.CurrencyItemAddress,
                baseline.CurrencySlotX,
                baseline.CurrencySlotY);
            var waystoneSlot = ctx.Area.ServerDataObject.ReadInventoryItemAt(
                InventoryName.MainInventory1,
                baseline.WaystoneSlotX,
                baseline.WaystoneSlotY,
                InventorySnapshotDetailLevel.Full);
            if (currencyItem == null || waystoneSlot.State != InventorySnapshotState.Ready)
            {
                this.StopCraftRetry(retry, "Currency or Waystone slot is unreadable after interruption — Waystone stopped");
                return;
            }

            var waystoneItem = waystoneSlot.Item;
            var currencyUnchanged = currencyItem != null &&
                                    currencyItem.ItemAddress == baseline.CurrencyItemAddress &&
                                    string.Equals(currencyItem.Path, baseline.CurrencyPath, StringComparison.OrdinalIgnoreCase) &&
                                    currencyItem.StackCount == baseline.ObservedCurrencyStackCountBeforeUse;
            var waystoneAction = waystoneItem == null
                ? WaystoneCraftingAction.Reject
                : WaystoneCrafting.GetNextAction(waystoneItem, ctx.Settings, out _);
            var waystoneUnchanged = waystoneItem != null &&
                                    waystoneItem.ItemAddress == baseline.WaystoneItemAddress &&
                                    waystoneItem.WaystoneTier == baseline.WaystoneTier &&
                                    waystoneItem.Rarity == baseline.Rarity &&
                                    waystoneItem.ExplicitMods.Count == baseline.ModCountBeforeUse &&
                                    waystoneAction == baseline.Action &&
                                    waystoneAction is not WaystoneCraftingAction.Ready and not WaystoneCraftingAction.Reject;
            baseline.ObservedCurrencyStackCountBeforeUse = currencyItem?.StackCount;
            if (!currencyUnchanged || !waystoneUnchanged)
            {
                this.StopCraftRetry(retry, "Currency or Waystone slot changed or is uncertain — item stopped to prevent duplicate currency");
                return;
            }

            if (this.craftRetryCounts.GetValueOrDefault(retry.Key) >= 1)
            {
                this.StopCraftRetry(retry, "Safe Waystone retry limit reached — item stopped");
                return;
            }

            var currencyControl = stash.VisibleItems.FirstOrDefault(item =>
                item.ItemAddress == baseline.CurrencyItemAddress &&
                string.Equals(item.ItemPath, baseline.CurrencyPath, StringComparison.OrdinalIgnoreCase));
            if (currencyControl == null)
            {
                this.StopCraftRetry(retry, "Currency UI is unavailable after interruption — item stopped");
                return;
            }

            var retryTarget = new CurrencyUseTarget(
                IntPtr.Zero,
                current => CraftingActionChangedSlot(
                    current,
                    baseline.Action,
                    baseline.WaystoneItemAddress,
                    baseline.WaystoneSlotX,
                    baseline.WaystoneSlotY,
                    baseline.WaystoneTier,
                    baseline.Rarity,
                    baseline.ModCountBeforeUse,
                    diagnostic => baseline.VerificationDiagnostic = diagnostic,
                    itemAddress => baseline.WaystoneItemAddress = itemAddress),
                current =>
                {
                    if (!CraftSlotsAreUnchanged(current, baseline))
                    {
                        throw new InvalidOperationException("Craft slots changed before retry click");
                    }
                },
                () => baseline.VerificationDiagnostic,
                current => ResolveCraftTargetUiAddress(current, baseline),
                () => baseline.WaystoneItemAddress);
            if (!ctx.Interaction.BeginUiCurrencyUseBatch(
                    currencyControl.UiAddress,
                    new[] { retryTarget },
                    $"retry {WaystoneCrafting.GetCurrencyLabel(baseline.Action)} once"))
            {
                this.StopCraftRetry(retry, "Safe Waystone retry could not start — item stopped");
                return;
            }

            this.craftRetryCounts[retry.Key] = this.craftRetryCounts.GetValueOrDefault(retry.Key) + 1;
            this.pendingCraftRetry = null;
            this.activeCraftActions.Clear();
            this.activeCraftActions.Add(baseline);
            this.pendingInteraction = baseline.Action switch
            {
                WaystoneCraftingAction.Identify => PendingInteraction.UseWisdom,
                WaystoneCraftingAction.Alchemy => PendingInteraction.UseAlchemy,
                WaystoneCraftingAction.Exalted => PendingInteraction.UseExalted,
                _ => PendingInteraction.None,
            };
            this.Status = "Both slots are unchanged — retrying this currency action once";
            this.Decision = "RetryCraftingAction";
            this.TickStartedInteraction(ctx);
        }

        private void StopCraftRetry(CraftRetryRequest retry, string status)
        {
            this.stoppedCraftItems.Add(retry.Baseline.WaystoneItemAddress);
            this.pendingCraftRetry = null;
            this.activeCraftActions.Clear();
            this.Status = status;
            this.Decision = "CraftingUnconfirmed";
        }

        private static int? ReadCurrencyStackCount(
            BotContext ctx,
            string currencyTab,
            string currencyPath,
            IntPtr currencyItemAddress,
            int slotX,
            int slotY)
        {
            if (!ctx.GameUi.IsStashOpen)
            {
                return null;
            }

            var snapshot = ctx.GameUi.Stash.ReadSnapshot(InventorySnapshotDetailLevel.Basic);
            if (snapshot.State != StashSnapshotState.Ready ||
                !string.Equals(snapshot.CurrentTabName, currencyTab, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return ReadCurrencyItem(
                ctx,
                snapshot,
                currencyPath,
                currencyItemAddress,
                slotX,
                slotY)?.StackCount;
        }

        private static InventorySnapshotItem? ReadCurrencyItem(
            BotContext ctx,
            StashSnapshot snapshot,
            string currencyPath,
            IntPtr currencyItemAddress,
            int slotX,
            int slotY)
        {
            if (snapshot.State != StashSnapshotState.Ready ||
                !ctx.GameUi.IsStashOpen ||
                currencyItemAddress == IntPtr.Zero)
            {
                return null;
            }

            if (slotX >= 0 && slotY >= 0)
            {
                var result = ctx.Area.ServerDataObject.ReadInventoryItemAt(
                    InventoryName.StashInventoryId,
                    slotX,
                    slotY,
                    InventorySnapshotDetailLevel.Full);
                var item = result.Item;
                return result.State == InventorySnapshotState.Ready && item != null &&
                       item.ItemAddress == currencyItemAddress &&
                       string.Equals(item.Path, currencyPath, StringComparison.OrdinalIgnoreCase) &&
                       item.StackCount is > 0
                    ? item
                    : null;
            }

            if (slotX != -1 || slotY != -1)
            {
                return null;
            }

            var visibleItem = snapshot.VisibleItems.FirstOrDefault(item =>
                item.ItemAddress == currencyItemAddress &&
                string.Equals(item.ItemPath, currencyPath, StringComparison.OrdinalIgnoreCase));
            if (visibleItem == null)
            {
                return null;
            }

            var visibleCurrency = ctx.GameUi.Stash.ReadVisibleItemDetails(
                visibleItem,
                InventorySnapshotDetailLevel.Full);
            return visibleCurrency?.StackCount is > 0 &&
                   visibleCurrency.ItemAddress == currencyItemAddress &&
                   string.Equals(visibleCurrency.Path, currencyPath, StringComparison.OrdinalIgnoreCase)
                ? visibleCurrency
                : null;
        }

        private static bool CraftSlotsAreUnchanged(BotContext ctx, CraftActionBaseline baseline)
        {
            var currentCurrencyCount = ReadCurrencyStackCount(
                ctx,
                baseline.CurrencyTab,
                baseline.CurrencyPath,
                baseline.CurrencyItemAddress,
                baseline.CurrencySlotX,
                baseline.CurrencySlotY);
            if (!currentCurrencyCount.HasValue ||
                currentCurrencyCount != baseline.ObservedCurrencyStackCountBeforeUse)
            {
                return false;
            }

            var waystone = ctx.Area.ServerDataObject.ReadInventoryItemAt(
                InventoryName.MainInventory1,
                baseline.WaystoneSlotX,
                baseline.WaystoneSlotY,
                InventorySnapshotDetailLevel.Full);
            var item = waystone.Item;
            if (waystone.State != InventorySnapshotState.Ready || item == null ||
                item.ItemAddress != baseline.WaystoneItemAddress ||
                item.WaystoneTier != baseline.WaystoneTier ||
                item.Rarity != baseline.Rarity ||
                item.ExplicitMods.Count != baseline.ModCountBeforeUse)
            {
                return false;
            }

            var action = WaystoneCrafting.GetNextAction(item, ctx.Settings, out _);
            return action == baseline.Action &&
                   action is not WaystoneCraftingAction.Ready and not WaystoneCraftingAction.Reject;
        }

        private static InventorySnapshotItem ReadExpectedCraftTarget(
            BotContext ctx,
            CraftActionBaseline baseline)
        {
            var result = ctx.Area.ServerDataObject.ReadInventoryItemAt(
                InventoryName.MainInventory1,
                baseline.WaystoneSlotX,
                baseline.WaystoneSlotY,
                InventorySnapshotDetailLevel.Full);
            var item = result.Item;
            if (result.State != InventorySnapshotState.Ready || item == null || !item.IsWaystone ||
                item.WaystoneTier != baseline.WaystoneTier ||
                item.Rarity != baseline.Rarity ||
                item.ExplicitMods.Count != baseline.ModCountBeforeUse ||
                item.WaystoneCorrupted != false ||
                WaystoneCrafting.GetNextAction(item, ctx.Settings, out _) != baseline.Action)
            {
                throw new InvalidOperationException("Waystone slot no longer matches the expected crafting action");
            }

            return item;
        }

        private static IntPtr ResolveCraftTargetUiAddress(
            BotContext ctx,
            CraftActionBaseline baseline)
        {
            var item = ReadExpectedCraftTarget(ctx, baseline);
            baseline.WaystoneItemAddress = item.ItemAddress;
            return ctx.GameUi.TryGetVisibleInventoryItemUiAddress(item.ItemAddress, out var uiAddress)
                ? uiAddress
                : IntPtr.Zero;
        }

        private void TickCurrencyCursorSafety(BotContext ctx)
        {
            if (ctx.Interaction.IsBusy)
            {
                this.RefreshActiveSnapshots(ctx);
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

            if (ctx.Interaction.CurrencyClickInFlight)
            {
                this.Status = "Waiting for currency mouse button and Shift to release";
                this.Decision = "WaitForCurrencyClickRelease";
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
                this.RefreshActiveSnapshots(ctx);
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
                if (this.withdrawalFinalVerificationPending)
                {
                    this.TickFinalWithdrawalVerification(ctx);
                    return;
                }

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

            var activeSnapshot = this.RefreshActiveSnapshots(ctx);
            var snapshot = activeSnapshot ?? ctx.GameUi.Stash.ReadSnapshot(InventorySnapshotDetailLevel.Full);
            if (snapshot.State != StashSnapshotState.Ready)
            {
                this.EndWithdrawalCtrlBatch(ctx);
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
                if (!this.TickWithdrawFromSnapshot(ctx, snapshot, minTier, maxTier, targetTier: 0))
                {
                    this.waystoneStockExhausted = true;
                    this.nextWaystoneStockRecheckUtc = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                }

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
                this.waystoneStockExhausted = true;
                this.nextWaystoneStockRecheckUtc = DateTime.UtcNow + TimeSpan.FromSeconds(30);
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
                this.ResetPageClickDelay();
                this.settlingPageName = string.Empty;
                this.pageScanNotBeforeUtc = DateTime.MinValue;
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
                        this.BeginPageScanSettle("1");
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
                    if (this.WaitForPageScanSettle(ctx, snapshot))
                    {
                        return;
                    }

                    if (this.forcePageStashRefresh)
                    {
                        snapshot = activeSnapshot ?? ctx.GameUi.Stash.ReadSnapshot(
                            InventorySnapshotDetailLevel.Full,
                            forceInventoryRefresh: true);
                        if (snapshot.State != StashSnapshotState.Ready ||
                            !string.Equals(snapshot.CurrentPageName, this.settlingPageName, StringComparison.Ordinal))
                        {
                            this.Status = $"Waiting for fresh Waystone page {this.settlingPageName} contents";
                            this.Decision = "SettleWaystonePage";
                            return;
                        }

                        this.forcePageStashRefresh = false;
                        this.settlingPageName = string.Empty;
                        this.pageScanNotBeforeUtc = DateTime.MinValue;
                    }

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
                this.ResetPageClickDelay();
                this.nextSpecializedPage = Math.Max(this.nextSpecializedPage, pageNumber + 1);
                this.stashPhase = phaseAfterSelection;
                this.BeginPageScanSettle(pageName);
                return;
            }

            if (this.delayedPageNumber != pageNumber)
            {
                this.delayedPageNumber = pageNumber;
                this.pageClickNotBeforeUtc = DateTime.UtcNow + TimeSpan.FromMilliseconds(Random.Shared.Next(1000, 1301));
            }

            if (DateTime.UtcNow < this.pageClickNotBeforeUtc)
            {
                this.Status = $"Pausing before Waystone page {pageName} ({(this.pageClickNotBeforeUtc - DateTime.UtcNow).TotalSeconds:F1}s)";
                this.Decision = "SettleWaystonePage";
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
            this.ResetPageClickDelay();
            this.phaseAfterPageSelection = phaseAfterSelection;
            this.pendingInteraction = PendingInteraction.SelectPage;
            this.TickStartedInteraction(ctx);
        }

        private void BeginPageScanSettle(string pageName)
        {
            if (string.Equals(this.settlingPageName, pageName, StringComparison.Ordinal))
            {
                return;
            }

            this.settlingPageName = pageName;
            this.pageScanNotBeforeUtc = DateTime.UtcNow + PageScanSettleDelay;
            this.forcePageStashRefresh = true;
        }

        private bool WaitForPageScanSettle(BotContext ctx, StashSnapshot snapshot)
        {
            if (this.settlingPageName.Length == 0)
            {
                return false;
            }

            if (!string.Equals(snapshot.CurrentPageName, this.settlingPageName, StringComparison.Ordinal))
            {
                if (DateTime.UtcNow >= this.pageScanNotBeforeUtc + RetryDelay)
                {
                    var expectedPage = int.Parse(this.settlingPageName);
                    this.settlingPageName = string.Empty;
                    this.pageScanNotBeforeUtc = DateTime.MinValue;
                    this.forcePageStashRefresh = false;
                    this.BeginSelectPage(ctx, snapshot, expectedPage, StashPhase.FindItem);
                    return true;
                }

                this.Status = $"Waiting for Waystone page {this.settlingPageName} to become active";
                this.Decision = "SettleWaystonePage";
                return true;
            }

            if (DateTime.UtcNow < this.pageScanNotBeforeUtc)
            {
                this.Status = $"Waiting for Waystone page {this.settlingPageName} contents to settle " +
                              $"({(this.pageScanNotBeforeUtc - DateTime.UtcNow).TotalSeconds:F1}s)";
                this.Decision = "SettleWaystonePage";
                return true;
            }

            return false;
        }

        private void ResetPageClickDelay()
        {
            this.delayedPageNumber = 0;
            this.pageClickNotBeforeUtc = DateTime.MinValue;
        }

        private bool TickWithdrawFromSnapshot(
            BotContext ctx,
            StashSnapshot snapshot,
            int minTier,
            int maxTier,
            int targetTier)
        {
            var isSpecializedWaystoneTab = snapshot.Tiers.Count == 16;
            var candidates = snapshot.VisibleItems
                .Select(control => new
                {
                    Ui = control,
                    Tier = TryGetWaystoneTier(control.ItemPath, out var tier) ? tier : 0,
                    Item = snapshot.Inventory.Items.FirstOrDefault(item => item.ItemAddress == control.ItemAddress) ??
                           control.ItemDetails,
                })
                .Where(candidate => candidate.Tier >= minTier && candidate.Tier <= maxTier &&
                                    (targetTier == 0 || candidate.Tier == targetTier) &&
                                    (isSpecializedWaystoneTab || candidate.Item != null))
                .Select(candidate => new
                {
                    candidate.Ui,
                    candidate.Tier,
                    Action = candidate.Item is { Rarity: not null } item
                        ? (WaystoneCraftingAction?)WaystoneCrafting.GetNextAction(item, ctx.Settings, out _)
                        : null,
                })
                .Where(candidate => candidate.Action != WaystoneCraftingAction.Reject &&
                                    (isSpecializedWaystoneTab || candidate.Action.HasValue))
                // Only current visible page items are readable here; prefer a Ready item on
                // this page before a craftable one. If a specialized tab hides item details,
                // withdraw the validated Waystone and inspect it in the main inventory.
                .OrderBy(candidate => candidate.Action == WaystoneCraftingAction.Ready ? 0 :
                                      candidate.Action.HasValue ? 1 : 2)
                .ThenByDescending(candidate => candidate.Tier)
                .ThenBy(candidate => candidate.Ui.UiAddress.ToInt64())
                .ToArray();
            if (candidates.Length == 0)
            {
                this.EndWithdrawalCtrlBatch(ctx);
                var visibleWaystones = snapshot.VisibleItems.Count(control =>
                    TryGetWaystoneTier(control.ItemPath, out _));
                this.Status = targetTier > 0
                    ? $"No eligible Waystone on Tier {targetTier} page {snapshot.CurrentPageName} " +
                      $"({visibleWaystones} visible, {snapshot.Inventory.Items.Count} server items)"
                    : $"No clickable Waystone in Tier {minTier}-{maxTier} on the selected ordinary tab " +
                      $"({visibleWaystones} visible, {snapshot.Inventory.Items.Count} server items)";
                this.Decision = "FindClickableWaystone";
                return false;
            }

            var candidate = candidates[0];
            var baselineInventory = this.ReadInventory(ctx, forceRefresh: true);
            if (baselineInventory.State != InventorySnapshotState.Ready)
            {
                this.Status = $"Main inventory {baselineInventory.State}: waiting before Waystone withdrawal";
                this.Decision = "ReadInventoryBeforeWithdrawal";
                return true;
            }

            var itemAddress = candidate.Ui.ItemAddress;
            var baseline = new WithdrawalBaseline(
                itemAddress,
                candidate.Ui.UiAddress,
                candidate.Tier,
                CountWaystonesInTier(baselineInventory, candidate.Tier),
                baselineInventory.Items.Select(item => item.ItemAddress).ToHashSet(),
                snapshot.Tiers.Count >= candidate.Tier
                    ? snapshot.Tiers[candidate.Tier - 1].Count
                    : -1,
                snapshot.CurrentTabName,
                snapshot.CurrentTierName,
                snapshot.CurrentPageName,
                snapshot.VisibleItems.Any(item => item.ItemAddress == itemAddress));
            this.withdrawalBaseline = baseline;
            this.withdrawalNoClickRetryUsed = false;
            this.withdrawalStashChanged = false;
            this.withdrawalFinalVerificationPending = false;
            this.withdrawalVerificationRetries = 0;
            var ctrlBatch = this.PrepareWithdrawalCtrlBatch(ctx, baselineInventory, snapshot);
            var releaseCtrlAfterClick = ctrlBatch && this.withdrawalCtrlBatchRemaining <= 1;
            if (!ctx.Interaction.BeginUiCtrlClick(
                    candidate.Ui.UiAddress,
                    $"Tier {candidate.Tier} Waystone",
                    current => IsWithdrawalConfirmed(this.ReadInventory(current), baseline),
                    ctrlHoldOwner: ctrlBatch ? this.withdrawalCtrlHoldOwner : null,
                    releaseCtrlHoldAfterClick: releaseCtrlAfterClick))
            {
                this.EndWithdrawalCtrlBatch(ctx);
                this.Status = "Waystone item UI is unavailable";
                this.Decision = "WithdrawWaystone";
                return true;
            }

            this.withdrawalAttempted = true;
            this.pendingInteraction = PendingInteraction.Withdraw;
            this.nextActiveSnapshotRefreshUtc = DateTime.UtcNow + ActiveSnapshotRefreshInterval;
            this.TickStartedInteraction(ctx);
            return true;
        }

        private bool PrepareWithdrawalCtrlBatch(BotContext ctx, InventorySnapshot inventory, StashSnapshot snapshot)
        {
            if (!this.activeBatchEnabled || this.activeBatchSize < 2)
            {
                this.EndWithdrawalCtrlBatch(ctx);
                return false;
            }

            var currentUsable = inventory.Items.Count(item =>
                item.WaystoneTier is int tier && tier >= this.activeMinTier && tier <= this.activeMaxTier &&
                WaystoneCrafting.GetNextAction(item, ctx.Settings, out _) != WaystoneCraftingAction.Reject &&
                !this.stoppedCraftItems.Contains(item.ItemAddress));
            var needed = Math.Max(0, this.activeBatchSize - currentUsable);
            var samePage = this.withdrawalCtrlBatchActive &&
                string.Equals(snapshot.CurrentTabName, this.withdrawalCtrlBatchTab, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(snapshot.CurrentTierName, this.withdrawalCtrlBatchTier, StringComparison.Ordinal) &&
                string.Equals(snapshot.CurrentPageName, this.withdrawalCtrlBatchPage, StringComparison.Ordinal);

            if (this.withdrawalCtrlBatchActive && !samePage)
            {
                this.EndWithdrawalCtrlBatch(ctx);
            }

            if (!this.withdrawalCtrlBatchActive)
            {
                if (needed < 2)
                {
                    return false;
                }

                this.withdrawalCtrlBatchActive = true;
                this.withdrawalCtrlBatchTab = snapshot.CurrentTabName;
                this.withdrawalCtrlBatchTier = snapshot.CurrentTierName;
                this.withdrawalCtrlBatchPage = snapshot.CurrentPageName;
            }

            this.withdrawalCtrlBatchRemaining = needed;
            if (this.withdrawalCtrlBatchRemaining <= 0)
            {
                this.EndWithdrawalCtrlBatch(ctx);
                return false;
            }

            return true;
        }

        private void CompleteConfirmedWithdrawal(BotContext ctx)
        {
            var preserveCtrl = this.withdrawalCtrlBatchActive && this.withdrawalCtrlBatchRemaining > 1;
            ctx.Interaction.Reset(preserveUiCtrlHold: preserveCtrl);
            this.pendingInteraction = PendingInteraction.None;
            if (this.withdrawalCtrlBatchActive)
            {
                this.withdrawalCtrlBatchRemaining = Math.Max(0, this.withdrawalCtrlBatchRemaining - 1);
                if (this.withdrawalCtrlBatchRemaining == 0)
                {
                    this.EndWithdrawalCtrlBatch(ctx);
                }
            }

            this.ClearWithdrawalGuard();
            this.waystoneStockExhausted = false;
            this.nextWaystoneStockRecheckUtc = DateTime.MinValue;
            this.inventorySnapshot = null;
            this.lastInventoryReadUtc = DateTime.MinValue;
        }

        private void EndWithdrawalCtrlBatch(BotContext? ctx = null)
        {
            var interaction = ctx?.Interaction ?? this.interaction;
            interaction?.ReleaseUiCtrlBatch(this.withdrawalCtrlHoldOwner);
            this.withdrawalCtrlBatchActive = false;
            this.withdrawalCtrlBatchRemaining = 0;
            this.withdrawalCtrlBatchTab = string.Empty;
            this.withdrawalCtrlBatchTier = string.Empty;
            this.withdrawalCtrlBatchPage = string.Empty;
        }

        private void TickFinalWithdrawalVerification(BotContext ctx)
        {
            if (DateTime.UtcNow < this.withdrawalFinalVerificationNotBeforeUtc)
            {
                this.Status = "Waiting 200ms before the final fresh Waystone inventory check";
                this.Decision = "VerifyWithdrawal";
                return;
            }

            var inventory = ctx.Area.ServerDataObject.ReadInventorySnapshot(
                InventoryName.MainInventory1,
                InventorySnapshotDetailLevel.Full,
                forceRefresh: true);
            this.inventorySnapshot = inventory;
            this.lastInventoryReadUtc = DateTime.UtcNow;
            var stash = ctx.GameUi.Stash.ReadSnapshot(
                InventorySnapshotDetailLevel.Full,
                forceInventoryRefresh: true);
            if (this.withdrawalBaseline is { } baseline && IsStashWithdrawalChanged(stash, baseline))
            {
                this.withdrawalStashChanged = true;
            }

            if (inventory.State == InventorySnapshotState.Ready &&
                this.withdrawalBaseline is { } confirmedBaseline &&
                IsWithdrawalConfirmed(inventory, confirmedBaseline))
            {
                this.ClearWithdrawalGuard();
                this.Status = "Waystone withdrawal confirmed by a fresh inventory snapshot";
                this.Decision = "WithdrawalConfirmed";
                return;
            }

            if (inventory.State != InventorySnapshotState.Ready && this.withdrawalVerificationRetries++ < 3)
            {
                this.withdrawalFinalVerificationNotBeforeUtc = DateTime.UtcNow + ActiveSnapshotRefreshInterval;
                this.Status = $"Main inventory {inventory.State}: retrying final withdrawal check";
                this.Decision = "VerifyWithdrawal";
                return;
            }

            this.withdrawalFinalVerificationPending = false;
            this.Status = this.withdrawalStashChanged
                ? "Stash changed but no Waystone appeared in a fresh inventory read — stopped to avoid a second withdrawal"
                : "Waystone withdrawal was not confirmed by a fresh inventory read — stopped to avoid a second withdrawal";
            this.Decision = "WithdrawalUnconfirmed";
        }

        private void ClearWithdrawalGuard()
        {
            this.withdrawalAttempted = false;
            this.withdrawalNoClickRetryUsed = false;
            this.withdrawalBaseline = null;
            this.withdrawalStashChanged = false;
            this.withdrawalFinalVerificationPending = false;
            this.withdrawalFinalVerificationNotBeforeUtc = DateTime.MinValue;
            this.withdrawalVerificationRetries = 0;
            this.nextActiveSnapshotRefreshUtc = DateTime.MinValue;
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
            if (completed == PendingInteraction.Withdraw)
            {
                this.CompleteConfirmedWithdrawal(ctx);
                this.nextRetryUtc = DateTime.MinValue;
                return;
            }

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
                {
                    var selectedPageName = this.pendingPageNumber.ToString();
                    this.nextSpecializedPage = Math.Max(this.nextSpecializedPage, this.pendingPageNumber + 1);
                    this.pendingPageNumber = 0;
                    this.stashPhase = this.phaseAfterPageSelection;
                    this.BeginPageScanSettle(selectedPageName);
                    break;
                }
                case PendingInteraction.SelectTier:
                    this.stashPhase = StashPhase.SelectPage;
                    break;
                case PendingInteraction.SelectCurrencyTab:
                    this.tabActionNotBeforeUtc = DateTime.MinValue;
                    this.delayedTabTarget = string.Empty;
                    break;
                case PendingInteraction.UseWisdom:
                case PendingInteraction.UseAlchemy:
                case PendingInteraction.UseExalted:
                    this.activeCraftActions.Clear();
                    this.inventorySnapshot = null;
                    this.lastInventoryReadUtc = DateTime.MinValue;
                    break;
                case PendingInteraction.CursorCleanup:
                    break;
            }

            this.nextActiveSnapshotRefreshUtc = DateTime.MinValue;
        }

        private void HandleInteractionFailure(BotContext ctx)
        {
            var failed = this.pendingInteraction;
            var failure = ctx.Interaction.LastFailure;
            var failedCurrencyTargetIndex = ctx.Interaction.CurrencyTargetIndex;
            var withdrawalClickIssued = ctx.Interaction.LastInputClickIssued;
            var withdrawalClickCompleted = ctx.Interaction.LastInputClickCompleted;
            ctx.Interaction.Reset();
            this.pendingInteraction = PendingInteraction.None;
            if (failed is PendingInteraction.UseWisdom or
                PendingInteraction.UseAlchemy or
                PendingInteraction.UseExalted)
            {
                if (this.activeCraftActions.Count > 0)
                {
                    var index = Math.Clamp(failedCurrencyTargetIndex, 0, this.activeCraftActions.Count - 1);
                    var baseline = this.activeCraftActions[index];
                    var retryKey = new CraftRetryKey(
                        baseline.WaystoneItemAddress,
                        baseline.WaystoneSlotX,
                        baseline.WaystoneSlotY,
                        baseline.Action,
                        baseline.ModCountBeforeUse);
                    if (this.craftRetryCounts.GetValueOrDefault(retryKey) < 1)
                    {
                        this.pendingCraftRetry = new CraftRetryRequest(
                            baseline,
                            retryKey,
                            DateTime.UtcNow + TimeSpan.FromMilliseconds(500));
                        this.Status = $"{failure} — checking both exact slots before one safe retry";
                        this.Decision = "VerifyCraftRetry";
                    }
                    else
                    {
                        this.stoppedCraftItems.Add(baseline.WaystoneItemAddress);
                        this.activeCraftActions.Clear();
                        this.Status = $"{failure} — retry limit reached; item stopped";
                        this.Decision = "CraftingUnconfirmed";
                    }
                }
                else
                {
                    this.Status = string.IsNullOrWhiteSpace(failure)
                        ? "Waystone crafting was not confirmed — item stopped"
                        : $"{failure} — item stopped";
                    this.Decision = "CraftingUnconfirmed";
                }

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
                if (!withdrawalClickIssued && withdrawalClickCompleted &&
                    !this.withdrawalNoClickRetryUsed && this.withdrawalBaseline is { } retryBaseline &&
                    this.TryGetUnchangedWithdrawalRetryTarget(ctx, retryBaseline, out var unchangedBaseline))
                {
                    this.withdrawalNoClickRetryUsed = true;
                    this.withdrawalBaseline = unchangedBaseline;
                    if (ctx.Interaction.BeginUiCtrlClick(
                            unchangedBaseline.StashUiAddress,
                            $"Tier {unchangedBaseline.Tier} Waystone",
                            current => IsWithdrawalConfirmed(this.ReadInventory(current), unchangedBaseline),
                            ctrlHoldOwner: this.withdrawalCtrlBatchActive ? this.withdrawalCtrlHoldOwner : null,
                            releaseCtrlHoldAfterClick: this.withdrawalCtrlBatchActive && this.withdrawalCtrlBatchRemaining <= 1))
                    {
                        this.withdrawalFinalVerificationPending = false;
                        this.pendingInteraction = PendingInteraction.Withdraw;
                        this.nextActiveSnapshotRefreshUtc = DateTime.UtcNow + ActiveSnapshotRefreshInterval;
                        this.Status = "Waystone input was not sent and both inventories are unchanged — retrying this exact stash item once";
                        this.Decision = "RetryUnissuedWithdrawal";
                        this.TickStartedInteraction(ctx);
                        return;
                    }
                }

                this.EndWithdrawalCtrlBatch(ctx);

                this.withdrawalFinalVerificationPending = true;
                this.withdrawalFinalVerificationNotBeforeUtc = DateTime.UtcNow + ActiveSnapshotRefreshInterval;
                this.withdrawalVerificationRetries = 0;
                this.Status = "Waystone click did not confirm — waiting for one fresh inventory check before stopping";
                this.Decision = "VerifyWithdrawal";
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
                this.ResetPageClickDelay();
                this.stashPhase = this.pendingPageNumber == 1
                    ? StashPhase.SelectPage
                    : this.phaseAfterPageSelection;
            }

            this.nextRetryUtc = DateTime.UtcNow + RetryDelay;
            this.Status = failure;
            this.Decision = "RetryStashAction";
        }

        private bool TryGetUnchangedWithdrawalRetryTarget(
            BotContext ctx,
            WithdrawalBaseline baseline,
            out WithdrawalBaseline refreshedBaseline)
        {
            refreshedBaseline = baseline;
            var inventory = this.ReadInventory(ctx, forceRefresh: true);
            var stash = ctx.GameUi.Stash.ReadSnapshot(
                InventorySnapshotDetailLevel.Full,
                forceInventoryRefresh: true);
            if (inventory.State != InventorySnapshotState.Ready || stash.State != StashSnapshotState.Ready ||
                inventory.Items.Count != baseline.MainInventoryItemAddresses.Count ||
                !baseline.MainInventoryItemAddresses.SetEquals(inventory.Items.Select(item => item.ItemAddress)) ||
                CountWaystonesInTier(inventory, baseline.Tier) != baseline.MainInventoryTierCount ||
                !string.Equals(stash.CurrentTabName, baseline.StashTabName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(stash.CurrentTierName, baseline.StashTierName, StringComparison.Ordinal) ||
                !string.Equals(stash.CurrentPageName, baseline.StashPageName, StringComparison.Ordinal) ||
                (baseline.StashTierCount >= 0 &&
                 (stash.Tiers.Count < baseline.Tier || stash.Tiers[baseline.Tier - 1].Count != baseline.StashTierCount)))
            {
                return false;
            }

            var unchangedTargets = stash.VisibleItems.Where(item => item.ItemAddress == baseline.ItemAddress).ToArray();
            if (unchangedTargets.Length != 1 || unchangedTargets[0].UiAddress == IntPtr.Zero ||
                !TryGetWaystoneTier(unchangedTargets[0].ItemPath, out var visibleTier) || visibleTier != baseline.Tier)
            {
                return false;
            }

            refreshedBaseline = baseline with { StashUiAddress = unchangedTargets[0].UiAddress };
            return true;
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
            this.EndWithdrawalCtrlBatch();
            this.tabActionNotBeforeUtc = DateTime.MinValue;
            this.delayedTabTarget = string.Empty;
            this.useTabScrollFallback = false;
            this.targetWaystoneTier = 0;
            this.nextSpecializedPage = 1;
            this.exhaustedWaystoneTiers.Clear();
            this.pendingPageNumber = 0;
            this.ResetPageClickDelay();
            this.settlingPageName = string.Empty;
            this.pageScanNotBeforeUtc = DateTime.MinValue;
            this.stashPhase = StashPhase.SelectTab;
            this.pendingInteraction = PendingInteraction.None;
            this.phaseAfterPageSelection = StashPhase.FindItem;
            this.waystoneStockExhausted = false;
            this.nextWaystoneStockRecheckUtc = DateTime.MinValue;
            this.nextActiveSnapshotRefreshUtc = DateTime.MinValue;
            this.forcePageStashRefresh = false;
            if (!preserveWithdrawalGuard)
            {
                this.ClearWithdrawalGuard();
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

        private static int CountWaystonesInTier(InventorySnapshot inventory, int tier) =>
            inventory.Items.Count(entry => entry.WaystoneTier == tier);

        private static bool IsWithdrawalConfirmed(InventorySnapshot inventory, WithdrawalBaseline baseline)
        {
            if (inventory.State != InventorySnapshotState.Ready)
            {
                return false;
            }

            return inventory.Items.Any(item =>
                       item.ItemAddress == baseline.ItemAddress && item.WaystoneTier == baseline.Tier) ||
                   inventory.Items.Any(item =>
                       item.WaystoneTier == baseline.Tier &&
                       !baseline.MainInventoryItemAddresses.Contains(item.ItemAddress)) ||
                   CountWaystonesInTier(inventory, baseline.Tier) > baseline.MainInventoryTierCount;
        }

        private static bool IsStashWithdrawalChanged(StashSnapshot snapshot, WithdrawalBaseline baseline)
        {
            if (snapshot.State != StashSnapshotState.Ready || baseline.StashTierCount < 0 ||
                !string.Equals(snapshot.CurrentTabName, baseline.StashTabName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(snapshot.CurrentTierName, baseline.StashTierName, StringComparison.Ordinal) ||
                !string.Equals(snapshot.CurrentPageName, baseline.StashPageName, StringComparison.Ordinal) ||
                snapshot.Tiers.Count < baseline.Tier)
            {
                return false;
            }

            return snapshot.Tiers[baseline.Tier - 1].Count < baseline.StashTierCount ||
                   (baseline.WasVisibleOnStashPage &&
                    snapshot.VisibleItems.All(item => item.ItemAddress != baseline.ItemAddress));
        }

        private static bool CraftingActionChangedSlot(
            BotContext ctx,
            WaystoneCraftingAction action,
            IntPtr expectedWaystoneItemAddress,
            int slotX,
            int slotY,
            int? expectedWaystoneTier,
            Rarity? oldRarity,
            int oldModCount,
            Action<string>? onVerificationDiagnostic = null,
            Action<IntPtr>? onItemAddressObserved = null)
        {
            var result = ctx.Area.ServerDataObject.ReadInventoryItemAt(
                InventoryName.MainInventory1,
                slotX,
                slotY,
                InventorySnapshotDetailLevel.Full);
            var item = result.Item;
            if (result.State != InventorySnapshotState.Ready)
            {
                onVerificationDiagnostic?.Invoke(
                    $"slot ({slotX},{slotY})={result.State}; expected Waystone tier {expectedWaystoneTier}, " +
                    $"explicit mods unavailable; {result.Diagnostic}; " +
                    $"request={result.ServerRequestCounter}, revision=0x{result.SourceRevision:X}");
                return false;
            }

            if (item == null)
            {
                onVerificationDiagnostic?.Invoke(
                    $"slot ({slotX},{slotY}) Ready but empty; expected Waystone 0x{expectedWaystoneItemAddress.ToInt64():X}, " +
                    $"tier {expectedWaystoneTier}, explicit mods unavailable; " +
                    $"request={result.ServerRequestCounter}, revision=0x{result.SourceRevision:X}");
                return false;
            }

            if (!item.IsWaystone || item.WaystoneTier != expectedWaystoneTier)
            {
                onVerificationDiagnostic?.Invoke(
                    $"slot ({slotX},{slotY}) Ready; item=0x{item.ItemAddress.ToInt64():X} " +
                    $"(baseline=0x{expectedWaystoneItemAddress.ToInt64():X}), " +
                    $"path={item.Path}, tier={item.WaystoneTier?.ToString() ?? "unknown"} " +
                    $"(expected {expectedWaystoneTier?.ToString() ?? "unknown"}), " +
                    $"explicit mods={item.ExplicitMods.Count}; " +
                    $"request={result.ServerRequestCounter}, revision=0x{result.SourceRevision:X}");
                return false;
            }

            onItemAddressObserved?.Invoke(item.ItemAddress);

            var explicitModCount = item.ExplicitMods.Count;
            var success = action switch
            {
                WaystoneCraftingAction.Identify => explicitModCount > 0,
                WaystoneCraftingAction.Alchemy =>
                    item.Rarity == Rarity.Rare &&
                    (oldRarity != Rarity.Rare || explicitModCount > oldModCount),
                WaystoneCraftingAction.Exalted => explicitModCount > oldModCount,
                _ => false,
            };
            var expected = action switch
            {
                WaystoneCraftingAction.Identify => "explicit mods > 0",
                WaystoneCraftingAction.Alchemy when oldRarity != Rarity.Rare => "Rare rarity",
                WaystoneCraftingAction.Alchemy => $"Rare and explicit mods > {oldModCount}",
                WaystoneCraftingAction.Exalted => $"explicit mods > {oldModCount}",
                _ => "a supported crafting action",
            };
            var modsDiagnostic = string.IsNullOrWhiteSpace(item.DetailDiagnostic)
                ? item.Rarity.HasValue ? "Full Mods details readable" : "Mods component unavailable"
                : item.DetailDiagnostic;
            onVerificationDiagnostic?.Invoke(
                $"slot ({slotX},{slotY}) Ready; item=0x{item.ItemAddress.ToInt64():X} " +
                $"(baseline=0x{expectedWaystoneItemAddress.ToInt64():X}), tier={item.WaystoneTier}; " +
                $"rarity={item.Rarity?.ToString() ?? "unavailable"}, explicit mods={explicitModCount} " +
                $"(expected {expected}); details={modsDiagnostic}; " +
                $"request={result.ServerRequestCounter}, revision=0x{result.SourceRevision:X}");
            return success;
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

        private sealed record CraftActionBaseline(
            IntPtr InitialWaystoneItemAddress,
            int WaystoneSlotX,
            int WaystoneSlotY,
            int? WaystoneTier,
            Rarity? Rarity,
            int ModCountBeforeUse,
            WaystoneCraftingAction Action,
            string CurrencyTab,
            string CurrencyPath,
            IntPtr CurrencyItemAddress,
            int CurrencySlotX,
            int CurrencySlotY,
            int CurrencyStackCountBeforeUse)
        {
            public IntPtr WaystoneItemAddress { get; set; } = InitialWaystoneItemAddress;

            public int? ObservedCurrencyStackCountBeforeUse { get; set; } = CurrencyStackCountBeforeUse;

            public string VerificationDiagnostic { get; set; } = "No target-slot read yet";
        }

        private sealed record WithdrawalBaseline(
            IntPtr ItemAddress,
            IntPtr StashUiAddress,
            int Tier,
            int MainInventoryTierCount,
            HashSet<IntPtr> MainInventoryItemAddresses,
            int StashTierCount,
            string StashTabName,
            string StashTierName,
            string StashPageName,
            bool WasVisibleOnStashPage);

        private readonly record struct CraftRetryKey(
            IntPtr WaystoneItemAddress,
            int WaystoneSlotX,
            int WaystoneSlotY,
            WaystoneCraftingAction Action,
            int ModCountBeforeUse);

        private sealed record CraftRetryRequest(
            CraftActionBaseline Baseline,
            CraftRetryKey Key,
            DateTime NotBeforeUtc);

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
