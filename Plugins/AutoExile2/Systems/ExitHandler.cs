// <copyright file="ExitHandler.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Systems
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;
    using AutoExile2.Modes;
    using TEHhub;

    /// <summary>
    /// Creates a bounded number of town portal attempts, waits for a new portal, and exits through it.
    /// The entity click is routed through InteractionSystem and confirmed by an area change.
    /// </summary>
    public sealed class ExitHandler
    {
        private static readonly TimeSpan PortalAttemptTimeout = TimeSpan.FromSeconds(6);
        private static readonly TimeSpan PortalTotalTimeout = TimeSpan.FromSeconds(30);
        private const int MaxPortalKeyAttempts = 5;
        private const float MaxPortalDistance = 80f;
        private static readonly List<Vector2> EmptyPath = new();

        private readonly HashSet<uint> existingPortalIds = new();
        private ExitPhase phase;
        private DateTime portalKeyTimeUtc = DateTime.MinValue;
        private DateTime firstPortalKeyTimeUtc = DateTime.MinValue;
        private string sourceAreaHash = string.Empty;
        private InteractionSystem? interaction;
        private bool portalKeyPressed;
        private bool portalSnapshotCaptured;
        private bool freshPortalObserved;
        private int portalKeyAttemptCount;

        public string Status { get; private set; } = string.Empty;

        public string Decision => this.phase switch
        {
            ExitPhase.WaitingForSafeInput => "WaitForSafePortalKey",
            ExitPhase.WaitingForPortal => "WaitForExitPortal",
            ExitPhase.Interacting => "EnterExitPortal",
            ExitPhase.Returned => "ReturnedToTown",
            ExitPhase.TimedOut => "PortalTimeout",
            ExitPhase.Failed => "ExitFailed",
            _ => "ExitMap",
        };

        public bool IsExitInitiated => this.portalKeyPressed;

        public List<Vector2> CurrentNavPath => this.interaction?.CurrentNavPath ?? EmptyPath;

        public int CurrentWaypointIndex => this.interaction?.CurrentWaypointIndex ?? 0;

        public Vector2? CurrentDestination => this.interaction?.CurrentDestination;

        public void Reset()
        {
            this.existingPortalIds.Clear();
            this.phase = ExitPhase.Idle;
            this.portalKeyPressed = false;
            this.portalKeyTimeUtc = DateTime.MinValue;
            this.firstPortalKeyTimeUtc = DateTime.MinValue;
            this.sourceAreaHash = string.Empty;
            this.interaction = null;
            this.portalSnapshotCaptured = false;
            this.freshPortalObserved = false;
            this.portalKeyAttemptCount = 0;
            this.Status = string.Empty;
        }

        public void Tick(BotContext ctx)
        {
            if (this.IsTerminal)
            {
                return;
            }

            if (this.HasReturnedToTown(ctx))
            {
                ctx.Interaction.Cancel(ctx.Settings);
                this.phase = ExitPhase.Returned;
                this.Status = "Returned to town/hideout";
                return;
            }

            if (this.phase is ExitPhase.Idle or ExitPhase.WaitingForSafeInput)
            {
                if (!ctx.Interaction.IsBusy)
                {
                    BotInput.ReleaseAllMovementKeys(ctx.Settings);
                }

                this.TryStartPortalKey(ctx);
                return;
            }

            if (this.phase == ExitPhase.WaitingForPortal)
            {
                this.PollForExitPortal(ctx);
                return;
            }

            if (this.phase == ExitPhase.Interacting)
            {
                this.TickInteraction(ctx);
            }
        }

        private bool IsTerminal => this.phase is ExitPhase.Returned or ExitPhase.TimedOut or ExitPhase.Failed;

        private void TryStartPortalKey(BotContext ctx)
        {
            if (ctx.World.AreaDetails.IsHideout || ctx.World.AreaDetails.IsTown ||
                string.IsNullOrWhiteSpace(ctx.Area.AreaHash))
            {
                this.phase = ExitPhase.Failed;
                this.Status = "Cannot create an exit portal outside a map area";
                return;
            }

            if (this.portalKeyAttemptCount > 0)
            {
                // Recheck both targetability and fresh entity ids before another key press.
                bool freshPortalSeen = this.TryStartFreshPortalInteraction(ctx);
                if (this.phase == ExitPhase.Interacting || this.IsTerminal)
                {
                    return;
                }

                if (this.HasPortalTotalTimeoutExpired())
                {
                    this.StopAfterPortalWait(ctx);
                    return;
                }

                if (freshPortalSeen)
                {
                    return;
                }

                var elapsed = DateTime.UtcNow - this.portalKeyTimeUtc;
                if (elapsed < PortalAttemptTimeout)
                {
                    this.phase = ExitPhase.WaitingForPortal;
                    this.Status = this.GetPortalWaitStatus(elapsed);
                    return;
                }

                if (this.portalKeyAttemptCount >= MaxPortalKeyAttempts)
                {
                    this.StopAfterPortalWait(ctx);
                    return;
                }
            }

            if (!ctx.Settings.IsRunning || !Core.Process.Foreground ||
                ctx.GameUi.ChatParent?.IsChatActive == true)
            {
                this.phase = ExitPhase.WaitingForSafeInput;
                this.Status = this.portalKeyAttemptCount == 0
                    ? "Waiting for foreground game, active bot, and inactive chat before creating an exit portal"
                    : $"Portal attempt {this.portalKeyAttemptCount}/{MaxPortalKeyAttempts} produced no portal; waiting for foreground game, active bot, and inactive chat before retrying";
                return;
            }

            if (ctx.GameUi.IsAnyLargePanelOpen)
            {
                this.phase = ExitPhase.WaitingForSafeInput;
                this.Status = this.portalKeyAttemptCount == 0
                    ? "Waiting for large panels to close before creating an exit portal"
                    : $"Portal attempt {this.portalKeyAttemptCount}/{MaxPortalKeyAttempts} produced no portal; waiting for large panels to close before retrying";
                return;
            }

            if (ctx.Interaction.IsBusy)
            {
                this.phase = ExitPhase.WaitingForSafeInput;
                this.Status = this.portalKeyAttemptCount == 0
                    ? "Waiting for the current interaction to finish before creating an exit portal"
                    : $"Portal attempt {this.portalKeyAttemptCount}/{MaxPortalKeyAttempts} produced no portal; waiting for the current interaction to finish before retrying";
                return;
            }

            if (this.portalKeyAttemptCount > 0 && this.TryStartFreshPortalInteraction(ctx))
            {
                if (this.phase != ExitPhase.Interacting && !this.IsTerminal && this.HasPortalTotalTimeoutExpired())
                {
                    this.StopAfterPortalWait(ctx);
                }
                return;
            }

            if (this.portalKeyAttemptCount > 0 && this.HasPortalTotalTimeoutExpired())
            {
                this.StopAfterPortalWait(ctx);
                return;
            }

            if (!this.portalSnapshotCaptured)
            {
                this.sourceAreaHash = ctx.Area.AreaHash;
                this.existingPortalIds.Clear();
                foreach (var existingId in PortalEntitySelector.Snapshot(ctx.Area))
                {
                    this.existingPortalIds.Add(existingId);
                }

                this.portalSnapshotCaptured = true;
            }

            // Count only actual key presses. A temporarily unsafe state does not consume an attempt.
            BotInput.ReleaseAllMovementKeys(ctx.Settings);
            BotInput.FastPressKey(ctx.Settings.PortalKey);
            this.portalKeyPressed = true;
            this.portalKeyTimeUtc = DateTime.UtcNow;
            if (this.portalKeyAttemptCount == 0)
            {
                this.firstPortalKeyTimeUtc = this.portalKeyTimeUtc;
            }
            this.portalKeyAttemptCount++;
            this.phase = ExitPhase.WaitingForPortal;
            this.Status = this.portalKeyAttemptCount == 1
                ? $"Opening exit portal [{ctx.Settings.PortalKey}] (attempt 1/{MaxPortalKeyAttempts})..."
                : $"Retrying exit portal [{ctx.Settings.PortalKey}] (attempt {this.portalKeyAttemptCount}/{MaxPortalKeyAttempts})...";
            ctx.Log($"[ExitHandler] Pressed portal key {ctx.Settings.PortalKey} (attempt {this.portalKeyAttemptCount}/{MaxPortalKeyAttempts})");
        }

        private void PollForExitPortal(BotContext ctx)
        {
            bool freshPortalSeen = this.TryStartFreshPortalInteraction(ctx);
            if (this.phase == ExitPhase.Interacting || this.IsTerminal)
            {
                return;
            }

            if (this.HasPortalTotalTimeoutExpired())
            {
                this.StopAfterPortalWait(ctx);
                return;
            }

            if (freshPortalSeen)
            {
                return;
            }

            var elapsed = DateTime.UtcNow - this.portalKeyTimeUtc;
            if (elapsed < PortalAttemptTimeout)
            {
                this.Status = this.GetPortalWaitStatus(elapsed);
                return;
            }

            if (this.portalKeyAttemptCount >= MaxPortalKeyAttempts)
            {
                this.StopAfterPortalWait(ctx);
                return;
            }

            this.phase = ExitPhase.WaitingForSafeInput;
            this.Status = $"Portal attempt {this.portalKeyAttemptCount}/{MaxPortalKeyAttempts} produced no portal; waiting for safe conditions before retrying";
            ctx.Log($"[ExitHandler] Portal attempt {this.portalKeyAttemptCount}/{MaxPortalKeyAttempts} timed out; checking again before a safe retry");
        }

        private bool TryStartFreshPortalInteraction(BotContext ctx)
        {
            // Remember any fresh portal ID, even while it is not targetable or close enough.
            // Retrying after seeing one could create a duplicate portal while the first settles.
            foreach (var portalId in PortalEntitySelector.Snapshot(ctx.Area))
            {
                if (!this.existingPortalIds.Contains(portalId))
                {
                    this.freshPortalObserved = true;
                    break;
                }
            }

            if (!this.freshPortalObserved)
            {
                return false;
            }

            var candidates = PortalEntitySelector.FindFreshTargetable(
                ctx.Area,
                ctx.PlayerGrid,
                this.existingPortalIds,
                MaxPortalDistance);

            if (this.HasPortalTotalTimeoutExpired())
            {
                this.StopAfterPortalWait(ctx);
                return true;
            }

            if (candidates.Count == 0)
            {
                this.phase = ExitPhase.WaitingForPortal;
                this.Status = "A new exit portal appeared; waiting for it to become targetable and enter range";
                return true;
            }

            if (!PortalEntitySelector.TrySelectNearest(candidates, out var selected))
            {
                this.phase = ExitPhase.Failed;
                this.Status = "No targetable exit portal candidate was available";
                ctx.Log($"[ExitHandler] {this.Status}");
                return true;
            }

            if (!ctx.Settings.IsRunning || !Core.Process.Foreground ||
                ctx.GameUi.ChatParent?.IsChatActive == true)
            {
                this.phase = ExitPhase.WaitingForSafeInput;
                this.Status = "Exit portal found; waiting for foreground game, active bot, and inactive chat before entering";
                return true;
            }

            if (ctx.GameUi.IsAnyLargePanelOpen || ctx.Interaction.IsBusy)
            {
                this.phase = ExitPhase.WaitingForSafeInput;
                this.Status = ctx.GameUi.IsAnyLargePanelOpen
                    ? "Exit portal found; waiting for large panels to close"
                    : "Exit portal found; waiting for the current interaction to finish";
                return true;
            }

            bool started = ctx.Interaction.BeginEntity(
                ctx,
                selected.Entity,
                "exit portal",
                current => !string.Equals(current.Area.AreaHash, this.sourceAreaHash, StringComparison.Ordinal) &&
                           (current.World.AreaDetails.IsHideout || current.World.AreaDetails.IsTown),
                interactionRange: 20f,
                maxClickAttempts: 3,
                requireMouseOverEntity: true);

            if (!started)
            {
                this.phase = ExitPhase.Failed;
                this.Status = "Could not start the exit portal interaction";
                ctx.Log($"[ExitHandler] {this.Status}");
                return true;
            }

            this.interaction = ctx.Interaction;
            this.phase = ExitPhase.Interacting;
            this.Status = $"Approaching nearest exit portal ({selected.Distance:F0}g)";
            ctx.Log($"[ExitHandler] Navigating to nearest new exit portal ({selected.Entity.Path})");
            return true;
        }

        private string GetPortalWaitStatus(TimeSpan elapsed) =>
            $"Waiting for exit portal (attempt {this.portalKeyAttemptCount}/{MaxPortalKeyAttempts}, {elapsed.TotalSeconds:F0}/{PortalAttemptTimeout.TotalSeconds:F0}s)";

        private bool HasPortalTotalTimeoutExpired() =>
            this.firstPortalKeyTimeUtc != DateTime.MinValue &&
            DateTime.UtcNow - this.firstPortalKeyTimeUtc >= PortalTotalTimeout;

        private void StopAfterPortalWait(BotContext ctx)
        {
            // Callers inspect fresh portal IDs and targetability before reaching this point.
            this.phase = ExitPhase.TimedOut;
            this.Status = $"Exit portal wait timed out after {PortalTotalTimeout.TotalSeconds:F0}s and {this.portalKeyAttemptCount}/{MaxPortalKeyAttempts} key attempts; stopping without entering an older portal";
            ctx.Log($"[ExitHandler] {this.Status}");
        }

        private void TickInteraction(BotContext ctx)
        {
            if (!ctx.Settings.IsRunning || !Core.Process.Foreground ||
                ctx.GameUi.ChatParent?.IsChatActive == true)
            {
                ctx.Interaction.Cancel(ctx.Settings);
                this.phase = ExitPhase.Failed;
                this.Status = "Exit interaction stopped because the bot, foreground game, or chat state changed";
                ctx.Log($"[ExitHandler] {this.Status}");
                return;
            }

            if (ctx.GameUi.IsAnyLargePanelOpen)
            {
                ctx.Interaction.Cancel(ctx.Settings);
                this.phase = ExitPhase.Failed;
                this.Status = "Exit portal interaction cancelled because a large panel opened";
                ctx.Log($"[ExitHandler] {this.Status}");
                return;
            }

            if (ctx.Interaction.Phase == InteractionPhase.Idle)
            {
                this.phase = ExitPhase.Failed;
                this.Status = "Exit portal interaction stopped because it was reset";
                ctx.Log($"[ExitHandler] {this.Status}");
                return;
            }

            var result = ctx.Interaction.Tick(ctx);
            this.Status = ctx.Interaction.Status;
            if (result == InteractionResult.Succeeded)
            {
                if (this.HasReturnedToTown(ctx))
                {
                    this.phase = ExitPhase.Returned;
                    this.Status = "Returned to town/hideout";
                }
                else
                {
                    this.phase = ExitPhase.Failed;
                    this.Status = "Portal interaction completed without a town/hideout area transition";
                }
            }
            else if (result == InteractionResult.Failed)
            {
                this.phase = ExitPhase.Failed;
            }
        }

        private bool HasReturnedToTown(BotContext ctx) =>
            this.portalKeyPressed &&
            !string.Equals(ctx.Area.AreaHash, this.sourceAreaHash, StringComparison.Ordinal) &&
            (ctx.World.AreaDetails.IsHideout || ctx.World.AreaDetails.IsTown);

        private enum ExitPhase
        {
            Idle,
            WaitingForSafeInput,
            WaitingForPortal,
            Interacting,
            Returned,
            TimedOut,
            Failed,
        }
    }
}
