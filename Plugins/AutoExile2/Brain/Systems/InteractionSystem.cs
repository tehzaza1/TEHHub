// <copyright file="InteractionSystem.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Systems
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Numerics;
    using System.Threading;
    using AutoExile2.Modes;
    using AutoExile2.Modes.Shared;
    using TEHhub;
    using TEHhub.Plugin;
    using TEHhub.RemoteEnums;
    using TEHhub.RemoteObjects.Components;
    using TEHhub.RemoteObjects.States.InGameStateObjects;

    /// <summary>
    /// Current outcome of one interaction request.
    /// </summary>
    public enum InteractionResult
    {
        Idle,
        InProgress,
        Succeeded,
        Failed,
    }

    /// <summary>
    /// Observable phase of the shared PoE 2 interaction state machine.
    /// </summary>
    public enum InteractionPhase
    {
        Idle,
        Navigating,
        Settling,
        Clicking,
        Scrolling,
        WaitingForSuccess,
        Succeeded,
        Failed,
    }

    /// <summary>A Waystone inventory target and the state predicate that verifies its currency use.</summary>
    public sealed record CurrencyUseTarget(
        IntPtr UiAddress,
        Func<BotContext, bool> SuccessPredicate,
        Action<BotContext>? BeforeClick = null);

    /// <summary>
    /// Serializes world-entity and UI interactions. A request navigates when necessary,
    /// clicks with a bounded retry budget, and succeeds only when its game-state predicate agrees.
    /// </summary>
    public sealed class InteractionSystem
    {
        private const int DefaultMaxClickAttempts = 5;
        private const double SettleMilliseconds = 200d;
        private const double ClickRetryMilliseconds = 900d;
        private const double CtrlClickVerificationMilliseconds = 2000d;
        private const double CurrencyActivationMilliseconds = 650d;
        private const double CurrencyCleanupMilliseconds = 350d;
        private const double RepathMilliseconds = 300d;
        private const float WaypointReachedDistance = 12f;

        private readonly List<Vector2> currentNavPath = new();
        private InteractionKind kind;
        private uint entityId;
        private string entityPath = string.Empty;
        private IntPtr uiAddress;
        private IntPtr fallbackUiAddress;
        private IntPtr targetUiAddress;
        private string uiSource = "UI control";
        private string fallbackUiSource = "fallback UI control";
        private int wheelDirection;
        private int currencyUseStep;
        private IReadOnlyList<CurrencyUseTarget>? currencyTargets;
        private int currencyTargetIndex;
        private volatile bool currencyActivationIssued;
        private volatile bool currencyActivationCompleted;
        private volatile bool currencyTargetClickIssued;
        private volatile bool currencyTargetClickCompleted;
        private volatile bool currencyClickInFlight;
        private Func<BotContext, bool>? successPredicate;
        private DateTime startedAtUtc;
        private DateTime settleStartedAtUtc;
        private DateTime lastClickAtUtc;
        private DateTime lastRepathAtUtc;
        private Vector2? lastPathTarget;
        private Vector2 lastPlayerGrid;
        private float stuckSeconds;
        private int pathFailures;
        private int stuckRecoveries;
        private int clickAttempts;
        private int maxClickAttempts;
        private float interactionRange;
        private TimeSpan timeout;
        private long requestGeneration;
        private volatile bool currencyMayBeActive;

        public InteractionPhase Phase { get; private set; } = InteractionPhase.Idle;

        public string Description { get; private set; } = string.Empty;

        public string Status { get; private set; } = "Idle";

        public string LastFailure { get; private set; } = string.Empty;

        public List<Vector2> CurrentNavPath => this.currentNavPath;

        public int CurrentWaypointIndex { get; private set; }

        public Vector2? CurrentDestination { get; private set; }

        public bool IsBusy => this.Phase is InteractionPhase.Navigating or
            InteractionPhase.Settling or InteractionPhase.Clicking or InteractionPhase.Scrolling or
            InteractionPhase.WaitingForSuccess;

        public InteractionResult Result => this.Phase switch
        {
            InteractionPhase.Succeeded => InteractionResult.Succeeded,
            InteractionPhase.Failed => InteractionResult.Failed,
            InteractionPhase.Idle => InteractionResult.Idle,
            _ => InteractionResult.InProgress,
        };

        /// <summary>
        /// Gets a value indicating that this system activated a currency but has not yet proved
        /// that the target slot changed or completed a safe cursor cleanup.
        /// </summary>
        public bool CurrencyMayBeActive => this.currencyMayBeActive;

        public bool CurrencyClickInFlight => this.currencyClickInFlight;

        public int CurrencyTargetIndex => this.currencyTargetIndex;

        /// <summary>
        /// Starts one entity interaction. The entity is refreshed by id on every tick.
        /// </summary>
        public bool BeginEntity(
            BotContext ctx,
            Entity entity,
            string description,
            Func<BotContext, bool> successPredicate,
            float interactionRange = 20f,
            int maxClickAttempts = DefaultMaxClickAttempts)
        {
            if (entity == null || !entity.IsValid || entity.Id == 0 || this.IsBusy)
            {
                return false;
            }

            this.ResetRequest();
            this.kind = InteractionKind.Entity;
            this.LastFailure = string.Empty;
            this.entityId = entity.Id;
            this.entityPath = entity.Path ?? string.Empty;
            this.Description = description;
            this.successPredicate = successPredicate;
            this.interactionRange = Math.Clamp(interactionRange, 8f, 45f);
            this.maxClickAttempts = Math.Clamp(maxClickAttempts, 1, 10);
            this.startedAtUtc = DateTime.UtcNow;
            this.timeout = ComputeEntityTimeout(ctx.PlayerGrid, entity, this.interactionRange);
            this.Phase = InteractionPhase.Navigating;
            this.Status = $"Finding route to {description}";
            return true;
        }

        /// <summary>
        /// Starts one visible UI-element interaction. The rectangle is resolved again before every click.
        /// </summary>
        public bool BeginUiElement(
            IntPtr uiAddress,
            string description,
            Func<BotContext, bool> successPredicate,
            IntPtr fallbackUiAddress = default,
            int maxClickAttempts = DefaultMaxClickAttempts,
            string uiSource = "top tab",
            string fallbackUiSource = "all-tabs list")
        {
            if (uiAddress == IntPtr.Zero || this.IsBusy)
            {
                return false;
            }

            this.ResetRequest();
            this.kind = InteractionKind.UiElement;
            this.LastFailure = string.Empty;
            this.uiAddress = uiAddress;
            this.fallbackUiAddress = fallbackUiAddress;
            this.uiSource = uiSource;
            this.fallbackUiSource = fallbackUiSource;
            this.Description = description;
            this.successPredicate = successPredicate;
            this.maxClickAttempts = Math.Clamp(maxClickAttempts, 1, 10);
            this.startedAtUtc = DateTime.UtcNow;
            this.timeout = TimeSpan.FromSeconds(10);
            this.Phase = InteractionPhase.Settling;
            this.settleStartedAtUtc = this.startedAtUtc;
            this.Status = $"Preparing {description}";
            return true;
        }

        /// <summary>
        /// Starts a bounded Ctrl+mouse-wheel interaction at the center of a UI container. Each
        /// attempt sends one notch and verifies the selected tab before another is allowed.
        /// </summary>
        public bool BeginUiScroll(
            IntPtr hoverAddress,
            string description,
            int wheelDirection,
            Func<BotContext, bool> successPredicate,
            int maxScrollAttempts = DefaultMaxClickAttempts)
        {
            if (hoverAddress == IntPtr.Zero || wheelDirection == 0 || this.IsBusy)
            {
                return false;
            }

            this.ResetRequest();
            this.kind = InteractionKind.UiScroll;
            this.LastFailure = string.Empty;
            this.uiAddress = hoverAddress;
            this.wheelDirection = Math.Sign(wheelDirection);
            this.Description = description;
            this.successPredicate = successPredicate;
            this.maxClickAttempts = Math.Clamp(maxScrollAttempts, 1, 32);
            this.startedAtUtc = DateTime.UtcNow;
            this.timeout = TimeSpan.FromSeconds(Math.Clamp((this.maxClickAttempts * 1.2d) + 3d, 6d, 45d));
            this.Phase = InteractionPhase.Settling;
            this.settleStartedAtUtc = this.startedAtUtc;
            this.Status = $"Preparing {description}";
            return true;
        }

        /// <summary>
        /// Starts one Ctrl+left-click on a visible UI item. The default is deliberately one attempt:
        /// a delayed inventory refresh must never cause a second item to be withdrawn accidentally.
        /// </summary>
        public bool BeginUiCtrlClick(
            IntPtr uiAddress,
            string description,
            Func<BotContext, bool> successPredicate)
        {
            if (uiAddress == IntPtr.Zero || this.IsBusy)
            {
                return false;
            }

            this.ResetRequest();
            this.kind = InteractionKind.UiCtrlClick;
            this.LastFailure = string.Empty;
            this.uiAddress = uiAddress;
            this.Description = description;
            this.successPredicate = successPredicate;
            this.maxClickAttempts = 1;
            this.startedAtUtc = DateTime.UtcNow;
            this.timeout = TimeSpan.FromSeconds(6);
            this.Phase = InteractionPhase.Settling;
            this.settleStartedAtUtc = this.startedAtUtc;
            this.Status = $"Preparing {description}";
            return true;
        }

        /// <summary>
        /// Starts one bounded PoE2 currency action: right-click the currency control, then
        /// right-click the exact target item control. Right-click currency activation is not
        /// represented by Cursor1, so callers verify the changed target slot before success.
        /// </summary>
        public bool BeginUiCurrencyUse(
            IntPtr currencyUiAddress,
            IntPtr targetItemUiAddress,
            string description,
            Func<BotContext, bool> successPredicate)
        {
            if (currencyUiAddress == IntPtr.Zero || targetItemUiAddress == IntPtr.Zero ||
                this.IsBusy || this.currencyMayBeActive || this.currencyClickInFlight)
            {
                return false;
            }

            this.ResetRequest();
            this.kind = InteractionKind.UiCurrencyUse;
            this.LastFailure = string.Empty;
            this.uiAddress = currencyUiAddress;
            this.targetUiAddress = targetItemUiAddress;
            this.currencyTargets = new[] { new CurrencyUseTarget(targetItemUiAddress, successPredicate) };
            this.currencyTargetIndex = 0;
            this.Description = description;
            this.successPredicate = successPredicate;
            this.maxClickAttempts = 1;
            this.startedAtUtc = DateTime.UtcNow;
            this.timeout = TimeSpan.FromSeconds(8);
            this.Phase = InteractionPhase.Settling;
            this.settleStartedAtUtc = this.startedAtUtc;
            this.Status = $"Preparing {description}";
            return true;
        }

        /// <summary>Starts one currency activation followed by verified target clicks.</summary>
        public bool BeginUiCurrencyUseBatch(
            IntPtr currencyUiAddress,
            IReadOnlyList<CurrencyUseTarget> targets,
            string description)
        {
            if (currencyUiAddress == IntPtr.Zero || targets == null || targets.Count == 0 ||
                targets.Any(target => target.UiAddress == IntPtr.Zero || target.SuccessPredicate == null) ||
                this.IsBusy || this.currencyMayBeActive || this.currencyClickInFlight)
            {
                return false;
            }

            this.ResetRequest();
            this.kind = InteractionKind.UiCurrencyUse;
            this.LastFailure = string.Empty;
            this.uiAddress = currencyUiAddress;
            this.currencyTargets = targets;
            this.currencyTargetIndex = 0;
            this.Description = description;
            this.maxClickAttempts = 1;
            this.startedAtUtc = DateTime.UtcNow;
            this.timeout = TimeSpan.FromSeconds(Math.Clamp(12 + (targets.Count * 3), 15, 210));
            this.Phase = InteractionPhase.Settling;
            this.settleStartedAtUtc = this.startedAtUtc;
            this.Status = $"Preparing {description}";
            return true;
        }

        /// <summary>
        /// Starts one recovery right-click on a validated non-item UI control. This is allowed only
        /// when a previous currency action may have left a currency attached to the cursor.
        /// </summary>
        public bool BeginUiCurrencyCursorCleanup(IntPtr safeUiAddress)
        {
            if (safeUiAddress == IntPtr.Zero || this.IsBusy || !this.currencyMayBeActive)
            {
                return false;
            }

            this.ResetRequest();
            this.kind = InteractionKind.UiCurrencyCursorCleanup;
            this.uiAddress = safeUiAddress;
            this.Description = "currency cursor cleanup";
            this.startedAtUtc = DateTime.UtcNow;
            this.timeout = TimeSpan.FromSeconds(4);
            this.Phase = InteractionPhase.Settling;
            this.settleStartedAtUtc = this.startedAtUtc;
            this.Status = "Preparing currency cursor cleanup";
            return true;
        }

        /// <summary>
        /// Advances the active request by one bot tick.
        /// </summary>
        public InteractionResult Tick(BotContext ctx)
        {
            if (this.Phase == InteractionPhase.Idle ||
                this.Phase == InteractionPhase.Succeeded ||
                this.Phase == InteractionPhase.Failed)
            {
                return this.Result;
            }

            if (!CanIssueInput(ctx))
            {
                BotInput.ReleaseAllMovementKeys(ctx.Settings);
                BotInput.ReleaseSprint(ctx.Settings.SprintKey);
                this.startedAtUtc += TimeSpan.FromSeconds(ctx.DeltaTime);
                this.Status = "Waiting for foreground game and inactive chat";
                return InteractionResult.InProgress;
            }

            if (this.kind != InteractionKind.UiCurrencyUse && this.IsSuccessful(ctx))
            {
                this.Complete(ctx);
                return this.Result;
            }

            var now = DateTime.UtcNow;
            if (now - this.startedAtUtc > this.timeout)
            {
                this.Fail(ctx, $"Timed out after {this.timeout.TotalSeconds:F0}s");
                return this.Result;
            }

            return this.kind switch
            {
                InteractionKind.Entity => this.TickEntity(ctx, now),
                InteractionKind.UiElement => this.TickUiElement(ctx, now),
                InteractionKind.UiCtrlClick => this.TickUiElement(ctx, now),
                InteractionKind.UiCurrencyUse => this.TickUiCurrencyUse(ctx, now),
                InteractionKind.UiCurrencyCursorCleanup => this.TickUiCurrencyCursorCleanup(ctx, now),
                InteractionKind.UiScroll => this.TickUiScroll(ctx, now),
                _ => this.Fail(ctx, "No interaction target"),
            };
        }

        /// <summary>
        /// Cancels all interaction state and releases movement owned by this system.
        /// </summary>
        public void Cancel(AutoExile2Settings settings)
        {
            BotInput.ReleaseAllMovementKeys(settings);
            BotInput.ReleaseSprint(settings.SprintKey);
            this.Reset();
        }

        public void Reset()
        {
            this.ResetRequest();
            this.Phase = InteractionPhase.Idle;
            this.Status = "Idle";
            this.Description = string.Empty;
            this.LastFailure = string.Empty;
        }

        private static TimeSpan ComputeEntityTimeout(Vector2 playerGrid, Entity entity, float interactionRange)
        {
            var distance = 0f;
            if (entity.TryGetComponent<Render>(out var render))
            {
                distance = Vector2.Distance(
                    playerGrid,
                    new Vector2(render.GridPosition.X, render.GridPosition.Y));
            }

            var estimatedTravelSeconds = Math.Max(0f, distance - interactionRange) / 18f;
            return TimeSpan.FromSeconds(Math.Clamp(estimatedTravelSeconds + 12f, 12f, 60f));
        }

        private InteractionResult TickEntity(BotContext ctx, DateTime now)
        {
            var entity = ctx.Area.AwakeEntities.Values.FirstOrDefault(candidate =>
                candidate.IsValid && candidate.Id == this.entityId);
            if (entity == null)
            {
                return this.Fail(ctx, $"{this.Description} entity #{this.entityId} is no longer awake");
            }

            if (!string.Equals(entity.Path, this.entityPath, StringComparison.OrdinalIgnoreCase))
            {
                return this.Fail(ctx, $"{this.Description} entity id was reused");
            }

            if (!entity.TryGetComponent<Render>(out var render))
            {
                return this.Fail(ctx, $"{this.Description} has no Render component");
            }

            var targetGrid = new Vector2(render.GridPosition.X, render.GridPosition.Y);
            var distance = Vector2.Distance(ctx.PlayerGrid, targetGrid);
            this.CurrentDestination = targetGrid;

            if (distance > this.interactionRange)
            {
                this.Phase = InteractionPhase.Navigating;
                this.Status = $"Moving to {this.Description} ({distance:F0}g)";
                this.Navigate(ctx, targetGrid, now);
                return this.Result;
            }

            BotInput.ReleaseAllMovementKeys(ctx.Settings);
            BotInput.ReleaseSprint(ctx.Settings.SprintKey);
            this.currentNavPath.Clear();
            this.CurrentWaypointIndex = 0;

            if (this.Phase == InteractionPhase.Navigating)
            {
                this.Phase = InteractionPhase.Settling;
                this.settleStartedAtUtc = now;
                this.Status = $"Settling near {this.Description}";
                return this.Result;
            }

            if (this.Phase == InteractionPhase.Settling &&
                (now - this.settleStartedAtUtc).TotalMilliseconds < SettleMilliseconds)
            {
                return this.Result;
            }

            if (this.Phase == InteractionPhase.WaitingForSuccess &&
                (now - this.lastClickAtUtc).TotalMilliseconds < ClickRetryMilliseconds)
            {
                this.Status = $"Waiting for {this.Description} ({this.clickAttempts}/{this.maxClickAttempts})";
                return this.Result;
            }

            if (this.clickAttempts >= this.maxClickAttempts)
            {
                return this.Fail(ctx, $"{this.Description} did not confirm after {this.clickAttempts} clicks");
            }

            if (!entity.TryGetComponent<Targetable>(out var targetable) || !targetable.IsTargetable)
            {
                return this.Fail(ctx, $"{this.Description} is not targetable");
            }

            this.Phase = InteractionPhase.Clicking;
            var generation = Volatile.Read(ref this.requestGeneration);
            if (!ModeHelpers.ClickEntity(
                    ctx.World,
                    entity,
                    () => generation == Volatile.Read(ref this.requestGeneration) && CanIssueInput(ctx)))
            {
                this.Status = $"Waiting for {this.Description} to be on screen";
                this.Phase = InteractionPhase.Settling;
                this.settleStartedAtUtc = now;
                return this.Result;
            }

            this.clickAttempts++;
            this.lastClickAtUtc = now;
            this.Phase = InteractionPhase.WaitingForSuccess;
            this.Status = $"Clicked {this.Description} ({this.clickAttempts}/{this.maxClickAttempts})";
            return this.Result;
        }

        private InteractionResult TickUiElement(BotContext ctx, DateTime now)
        {
            BotInput.ReleaseAllMovementKeys(ctx.Settings);
            BotInput.ReleaseSprint(ctx.Settings.SprintKey);

            if (this.Phase == InteractionPhase.Settling &&
                (now - this.settleStartedAtUtc).TotalMilliseconds < SettleMilliseconds)
            {
                return this.Result;
            }

            var waitMilliseconds = this.kind == InteractionKind.UiCtrlClick
                ? CtrlClickVerificationMilliseconds
                : ClickRetryMilliseconds;
            if (this.Phase == InteractionPhase.WaitingForSuccess &&
                (now - this.lastClickAtUtc).TotalMilliseconds < waitMilliseconds)
            {
                this.Status = $"Waiting for {this.Description} ({this.clickAttempts}/{this.maxClickAttempts})";
                return this.Result;
            }

            if (this.clickAttempts >= this.maxClickAttempts)
            {
                return this.Fail(ctx, $"{this.Description} did not confirm after {this.clickAttempts} clicks");
            }

            var usedFallback = this.kind == InteractionKind.UiElement &&
                               this.fallbackUiAddress != IntPtr.Zero && this.clickAttempts >= 2;
            var clickAddress = usedFallback ? this.fallbackUiAddress : this.uiAddress;
            if (!TryGetUiClickPoint(clickAddress, out var clickPoint))
            {
                var alternateAddress = usedFallback ? this.uiAddress : this.fallbackUiAddress;
                if (alternateAddress == IntPtr.Zero || !TryGetUiClickPoint(alternateAddress, out clickPoint))
                {
                    return this.Fail(ctx, $"{this.Description} UI controls are hidden or invalid");
                }

                usedFallback = !usedFallback;
            }

            this.Phase = InteractionPhase.Clicking;
            var generation = Volatile.Read(ref this.requestGeneration);
            if (this.kind == InteractionKind.UiCtrlClick)
            {
                BotInput.HumanCtrlClick(
                    clickPoint,
                    () => generation == Volatile.Read(ref this.requestGeneration) && CanIssueInput(ctx));
            }
            else
            {
                BotInput.HumanClick(
                    clickPoint,
                    canClick: () => generation == Volatile.Read(ref this.requestGeneration) && CanIssueInput(ctx));
            }
            this.clickAttempts++;
            this.lastClickAtUtc = now;
            this.Phase = InteractionPhase.WaitingForSuccess;
            var source = usedFallback ? this.fallbackUiSource : this.uiSource;
            var action = this.kind == InteractionKind.UiCtrlClick ? "Ctrl+clicked" : "Clicked";
            this.Status = $"{action} {this.Description} via {source} ({this.clickAttempts}/{this.maxClickAttempts})";
            return this.Result;
        }

        private InteractionResult TickUiScroll(BotContext ctx, DateTime now)
        {
            BotInput.ReleaseAllMovementKeys(ctx.Settings);
            BotInput.ReleaseSprint(ctx.Settings.SprintKey);

            if (this.Phase == InteractionPhase.Settling &&
                (now - this.settleStartedAtUtc).TotalMilliseconds < SettleMilliseconds)
            {
                return this.Result;
            }

            if (this.Phase == InteractionPhase.WaitingForSuccess &&
                (now - this.lastClickAtUtc).TotalMilliseconds < ClickRetryMilliseconds)
            {
                this.Status = $"Waiting for {this.Description} ({this.clickAttempts}/{this.maxClickAttempts})";
                return this.Result;
            }

            if (this.clickAttempts >= this.maxClickAttempts)
            {
                return this.Fail(ctx, $"{this.Description} did not confirm after {this.clickAttempts} scroll notches");
            }

            if (!TryGetUiClickPoint(this.uiAddress, out var hoverPoint))
            {
                return this.Fail(ctx, $"{this.Description} top tab bar is hidden or invalid");
            }

            this.Phase = InteractionPhase.Scrolling;
            var generation = Volatile.Read(ref this.requestGeneration);
            BotInput.HumanCtrlScroll(
                hoverPoint,
                this.wheelDirection,
                () => generation == Volatile.Read(ref this.requestGeneration) && CanIssueInput(ctx));
            this.clickAttempts++;
            this.lastClickAtUtc = now;
            this.Phase = InteractionPhase.WaitingForSuccess;
            var direction = this.wheelDirection > 0 ? "up" : "down";
            this.Status = $"Ctrl+scrolled {direction} for {this.Description} ({this.clickAttempts}/{this.maxClickAttempts})";
            return this.Result;
        }

        private InteractionResult TickUiCurrencyUse(BotContext ctx, DateTime now)
        {
            BotInput.ReleaseAllMovementKeys(ctx.Settings);
            BotInput.ReleaseSprint(ctx.Settings.SprintKey);

            if (this.Phase == InteractionPhase.Settling &&
                (now - this.settleStartedAtUtc).TotalMilliseconds < SettleMilliseconds)
            {
                return this.Result;
            }

            var generation = Volatile.Read(ref this.requestGeneration);
            if (this.currencyUseStep == 0)
            {
                var cursor = ReadCursor(ctx);
                if (cursor.State == InventorySnapshotState.Loading)
                {
                    this.Status = "Waiting for Cursor1 to stabilize before currency use";
                    return this.Result;
                }

                if (cursor.State != InventorySnapshotState.Ready)
                {
                    return this.Fail(ctx, $"Cursor1 is {cursor.State}");
                }

                if (cursor.Item != null)
                {
                    return this.Fail(ctx, $"Cursor1 is not empty ({cursor.Item.Path})");
                }

                if (!TryGetUiClickPoint(this.uiAddress, out var currencyPoint))
                {
                    return this.Fail(ctx, $"{this.Description} currency UI is hidden or invalid");
                }

                this.Phase = InteractionPhase.Clicking;
                this.currencyActivationIssued = false;
                this.currencyActivationCompleted = false;
                this.currencyClickInFlight = true;
                BotInput.HumanClick(
                    currencyPoint,
                    rightClick: true,
                    canClick: () => generation == Volatile.Read(ref this.requestGeneration) && CanIssueInput(ctx),
                    onClickIssued: () =>
                    {
                        this.currencyMayBeActive = true;
                        if (generation == Volatile.Read(ref this.requestGeneration))
                        {
                            this.currencyActivationIssued = true;
                        }
                    },
                    onClickFinished: clickIssued =>
                    {
                        this.currencyClickInFlight = false;
                        if (clickIssued && generation == Volatile.Read(ref this.requestGeneration))
                        {
                            this.currencyActivationCompleted = true;
                        }
                    });
                this.currencyUseStep = 1;
                this.lastClickAtUtc = now;
                this.Phase = InteractionPhase.WaitingForSuccess;
                this.Status = $"Right-clicked currency for {this.Description}";
                return this.Result;
            }

            if (this.currencyUseStep == 1)
            {
                if (!this.currencyActivationIssued)
                {
                    if ((now - this.lastClickAtUtc).TotalMilliseconds >= 2500d)
                    {
                        return this.Fail(ctx, $"{this.Description} currency click was not issued");
                    }

                    this.Status = $"Waiting for currency click: {this.Description}";
                    return this.Result;
                }

                if (!this.currencyActivationCompleted)
                {
                    this.Status = $"Waiting for currency click release: {this.Description}";
                    return this.Result;
                }

                if ((now - this.lastClickAtUtc).TotalMilliseconds < CurrencyActivationMilliseconds)
                {
                    this.Status = $"Waiting for currency activation: {this.Description}";
                    return this.Result;
                }

                var target = this.currencyTargets?[this.currencyTargetIndex];
                if (target == null || !TryGetUiClickPoint(target.UiAddress, out var targetPoint))
                {
                    return this.Fail(ctx, $"{this.Description} target item UI is hidden or invalid");
                }

                try
                {
                    target.BeforeClick?.Invoke(ctx);
                }
                catch
                {
                    return this.Fail(ctx, $"{this.Description} target baseline could not be read");
                }

                this.Phase = InteractionPhase.Clicking;
                var shiftClick = this.currencyTargetIndex < this.currencyTargets.Count - 1;
                this.currencyTargetClickIssued = false;
                this.currencyTargetClickCompleted = false;
                this.currencyClickInFlight = true;
                BotInput.HumanClick(
                    targetPoint,
                    rightClick: true,
                    canClick: () => generation == Volatile.Read(ref this.requestGeneration) &&
                                    CanIssueInput(ctx) &&
                                    (shiftClick ||
                                     (!BotInput.IsKeyDown(VK.LSHIFT) &&
                                      !BotInput.IsKeyDown(VK.RSHIFT) &&
                                      !BotInput.IsKeyDown(VK.SHIFT))),
                    shiftClick: shiftClick,
                    onClickIssued: () =>
                    {
                        this.currencyMayBeActive = shiftClick;
                        if (generation == Volatile.Read(ref this.requestGeneration))
                        {
                            this.currencyTargetClickIssued = true;
                        }
                    },
                    onClickFinished: clickIssued =>
                    {
                        this.currencyClickInFlight = false;
                        if (clickIssued && generation == Volatile.Read(ref this.requestGeneration))
                        {
                            this.currencyTargetClickCompleted = true;
                        }
                    });
                this.currencyUseStep = 2;
                this.lastClickAtUtc = now;
                this.Phase = InteractionPhase.WaitingForSuccess;
                this.Status = $"Right-clicked target for {this.Description}";
                return this.Result;
            }

            if (this.currencyUseStep == 2)
            {
                if (!this.currencyTargetClickIssued)
                {
                    if ((now - this.lastClickAtUtc).TotalMilliseconds >= 2500d)
                    {
                        return this.Fail(ctx, $"{this.Description} target click was not issued");
                    }

                    this.Status = $"Waiting for target click: {this.Description}";
                    return this.Result;
                }

                if (!this.currencyTargetClickCompleted)
                {
                    this.Status = $"Waiting for target click release: {this.Description}";
                    return this.Result;
                }

                var target = this.currencyTargets?[this.currencyTargetIndex];
                bool targetSucceeded;
                try
                {
                    targetSucceeded = target?.SuccessPredicate(ctx) == true;
                }
                catch
                {
                    targetSucceeded = false;
                }

                if (!targetSucceeded)
                {
                    this.Status = $"Verifying currency count and target slot for {this.Description}";
                    return this.Result;
                }

                this.currencyTargetIndex++;
                if (this.currencyTargets != null && this.currencyTargetIndex < this.currencyTargets.Count)
                {
                    this.currencyUseStep = 1;
                    this.lastClickAtUtc = now;
                    this.currencyMayBeActive = true;
                    this.Status = $"Verified target {this.currencyTargetIndex}/{this.currencyTargets.Count} for {this.Description}";
                    return this.Result;
                }

                this.Complete(ctx);
                return this.Result;
            }

            return this.Fail(ctx, "Invalid currency-use state");
        }

        private InteractionResult TickUiCurrencyCursorCleanup(BotContext ctx, DateTime now)
        {
            BotInput.ReleaseAllMovementKeys(ctx.Settings);
            BotInput.ReleaseSprint(ctx.Settings.SprintKey);

            if (this.currencyUseStep == 0)
            {
                var cursor = ReadCursor(ctx);
                if (cursor.State == InventorySnapshotState.Loading)
                {
                    return this.Result;
                }

                if (cursor.State != InventorySnapshotState.Ready)
                {
                    return this.Fail(ctx, $"Cursor1 is {cursor.State} during cleanup");
                }

                if (cursor.Item != null)
                {
                    return this.Fail(ctx, $"Cannot cancel right-click currency while Cursor1 holds {cursor.Item.Path}");
                }

                if (this.Phase == InteractionPhase.Settling &&
                    (now - this.settleStartedAtUtc).TotalMilliseconds < SettleMilliseconds)
                {
                    return this.Result;
                }

                if (!TryGetUiClickPoint(this.uiAddress, out var safePoint))
                {
                    return this.Fail(ctx, "Safe currency cursor cleanup UI is hidden or invalid");
                }

                var generation = Volatile.Read(ref this.requestGeneration);
                BotInput.HumanClick(
                    safePoint,
                    rightClick: true,
                    canClick: () => generation == Volatile.Read(ref this.requestGeneration) && CanIssueInput(ctx));
                this.currencyUseStep = 1;
                this.lastClickAtUtc = now;
                this.Phase = InteractionPhase.WaitingForSuccess;
                this.Status = "Right-clicked safe Stash title to cancel active currency";
                return this.Result;
            }

            if ((now - this.lastClickAtUtc).TotalMilliseconds >= CurrencyCleanupMilliseconds)
            {
                this.currencyMayBeActive = false;
                this.Complete(ctx);
                return this.Result;
            }

            this.Status = "Waiting for right-click currency cancellation";
            return this.Result;
        }

        private static InventorySlotItemSnapshot ReadCursor(BotContext ctx) =>
            ctx.Area.ServerDataObject.ReadInventoryItemAt(
                InventoryName.Cursor1,
                0,
                0,
                InventorySnapshotDetailLevel.Basic);

        private void Navigate(BotContext ctx, Vector2 targetGrid, DateTime now)
        {
            var needRepath = this.currentNavPath.Count == 0 ||
                             this.CurrentWaypointIndex >= this.currentNavPath.Count ||
                             !this.lastPathTarget.HasValue ||
                             Vector2.Distance(this.lastPathTarget.Value, targetGrid) > 8f;
            if (needRepath && (now - this.lastRepathAtUtc).TotalMilliseconds >= RepathMilliseconds)
            {
                this.lastRepathAtUtc = now;
                this.lastPathTarget = targetGrid;
                var path = Pathfinding.FindPath(
                    ctx.Area.GridWalkableData,
                    ctx.Area.TerrainMetadata.BytesPerRow,
                    ctx.PlayerGrid,
                    targetGrid);
                this.currentNavPath.Clear();
                this.CurrentWaypointIndex = 0;
                if (path.Count == 0)
                {
                    this.pathFailures++;
                    BotInput.ReleaseAllMovementKeys(ctx.Settings);
                    BotInput.ReleaseSprint(ctx.Settings.SprintKey);
                    this.Status = $"No route to {this.Description} ({this.pathFailures}/3)";
                    if (this.pathFailures >= 3)
                    {
                        this.Fail(ctx, $"No walkable route to {this.Description}");
                    }
                    return;
                }

                this.pathFailures = 0;
                this.currentNavPath.AddRange(path);
            }

            while (this.CurrentWaypointIndex < this.currentNavPath.Count &&
                   Vector2.Distance(ctx.PlayerGrid, this.currentNavPath[this.CurrentWaypointIndex]) <= WaypointReachedDistance)
            {
                this.CurrentWaypointIndex++;
            }

            if (this.CurrentWaypointIndex >= this.currentNavPath.Count)
            {
                BotInput.ReleaseAllMovementKeys(ctx.Settings);
                BotInput.ReleaseSprint(ctx.Settings.SprintKey);
                return;
            }

            var moved = Vector2.Distance(ctx.PlayerGrid, this.lastPlayerGrid);
            this.lastPlayerGrid = ctx.PlayerGrid;
            this.stuckSeconds = moved < 0.75f ? this.stuckSeconds + ctx.DeltaTime : 0f;
            if (this.stuckSeconds >= 1.5f)
            {
                this.stuckSeconds = 0f;
                this.currentNavPath.Clear();
                this.lastRepathAtUtc = DateTime.MinValue;
                this.stuckRecoveries++;
                BotInput.ReleaseAllMovementKeys(ctx.Settings);
                BotInput.ReleaseSprint(ctx.Settings.SprintKey);
                this.Status = $"Repathing to {this.Description} after no movement";
                if (this.stuckRecoveries >= 3)
                {
                    this.Fail(ctx, $"Movement to {this.Description} is stuck");
                }
                return;
            }

            var waypoint = this.currentNavPath[this.CurrentWaypointIndex];
            var direction = BotInput.GridToScreenDirection(
                ctx.World,
                ctx.Player,
                waypoint,
                ctx.PlayerGrid,
                ctx.Area.WorldToGridConvertor);
            BotInput.SetSprint(ctx.Settings.SprintKey, false);
            BotInput.WasdMove(direction, ctx.Settings);
        }

        private static bool TryGetUiClickPoint(IntPtr address, out Vector2 clickPoint)
        {
            clickPoint = Vector2.Zero;
            if (!PluginUiElementReflection.TryGetAbsoluteRect(address, out var position, out var size) ||
                size.X < 8f || size.Y < 8f || size.X > 800f || size.Y > 300f)
            {
                return false;
            }

            var localPoint = position + (size * 0.5f);
            var window = Core.Process.WindowArea;
            if (!float.IsFinite(localPoint.X) || !float.IsFinite(localPoint.Y) ||
                localPoint.X < 0f || localPoint.Y < 0f ||
                localPoint.X >= window.Width || localPoint.Y >= window.Height)
            {
                return false;
            }

            clickPoint = localPoint + new Vector2(window.Left, window.Top);
            return true;
        }

        private static bool CanIssueInput(BotContext ctx) =>
            ctx.Settings.IsRunning &&
            Core.Process.Foreground &&
            ctx.GameUi.ChatParent?.IsChatActive != true;

        private bool IsSuccessful(BotContext ctx)
        {
            try
            {
                return this.successPredicate?.Invoke(ctx) == true;
            }
            catch (Exception ex)
            {
                this.LastFailure = $"Success check failed: {ex.GetType().Name}";
                return false;
            }
        }

        private void Complete(BotContext ctx)
        {
            if (this.kind is InteractionKind.UiCurrencyUse or InteractionKind.UiCurrencyCursorCleanup)
            {
                this.currencyMayBeActive = false;
            }

            BotInput.ReleaseAllMovementKeys(ctx.Settings);
            BotInput.ReleaseSprint(ctx.Settings.SprintKey);
            this.currentNavPath.Clear();
            this.CurrentWaypointIndex = 0;
            this.Phase = InteractionPhase.Succeeded;
            this.Status = $"{this.Description} confirmed";
            this.LastFailure = string.Empty;
        }

        private InteractionResult Fail(BotContext ctx, string reason)
        {
            BotInput.ReleaseAllMovementKeys(ctx.Settings);
            BotInput.ReleaseSprint(ctx.Settings.SprintKey);
            this.currentNavPath.Clear();
            this.CurrentWaypointIndex = 0;
            this.Phase = InteractionPhase.Failed;
            this.Status = reason;
            this.LastFailure = reason;
            ctx.Log($"[Interaction] {reason}");
            return InteractionResult.Failed;
        }

        private void ResetRequest()
        {
            Interlocked.Increment(ref this.requestGeneration);
            this.kind = InteractionKind.None;
            this.entityId = 0;
            this.entityPath = string.Empty;
            this.uiAddress = IntPtr.Zero;
            this.fallbackUiAddress = IntPtr.Zero;
            this.targetUiAddress = IntPtr.Zero;
            this.currencyTargets = null;
            this.currencyTargetIndex = 0;
            this.currencyActivationIssued = false;
            this.currencyActivationCompleted = false;
            this.currencyTargetClickIssued = false;
            this.currencyTargetClickCompleted = false;
            this.uiSource = "UI control";
            this.fallbackUiSource = "fallback UI control";
            this.wheelDirection = 0;
            this.currencyUseStep = 0;
            this.successPredicate = null;
            this.startedAtUtc = DateTime.MinValue;
            this.settleStartedAtUtc = DateTime.MinValue;
            this.lastClickAtUtc = DateTime.MinValue;
            this.lastRepathAtUtc = DateTime.MinValue;
            this.lastPathTarget = null;
            this.lastPlayerGrid = Vector2.Zero;
            this.stuckSeconds = 0f;
            this.pathFailures = 0;
            this.stuckRecoveries = 0;
            this.clickAttempts = 0;
            this.maxClickAttempts = DefaultMaxClickAttempts;
            this.interactionRange = 20f;
            this.timeout = TimeSpan.Zero;
            this.currentNavPath.Clear();
            this.CurrentWaypointIndex = 0;
            this.CurrentDestination = null;
        }

        private enum InteractionKind
        {
            None,
            Entity,
            UiElement,
            UiCtrlClick,
            UiCurrencyUse,
            UiCurrencyCursorCleanup,
            UiScroll,
        }
    }
}
