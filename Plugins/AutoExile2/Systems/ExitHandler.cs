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
    /// Creates at most one town portal, waits for that new portal, and exits through it.
    /// The entity click is routed through InteractionSystem and confirmed by an area change.
    /// </summary>
    public sealed class ExitHandler
    {
        private static readonly TimeSpan PortalAppearanceTimeout = TimeSpan.FromSeconds(20);
        private const float MaxPortalDistance = 80f;
        private static readonly List<Vector2> EmptyPath = new();

        private readonly HashSet<uint> existingPortalIds = new();
        private ExitPhase phase;
        private DateTime portalKeyTimeUtc = DateTime.MinValue;
        private string sourceAreaHash = string.Empty;
        private InteractionSystem? interaction;
        private bool portalKeyPressed;

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
            this.sourceAreaHash = string.Empty;
            this.interaction = null;
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

            if (!ctx.Settings.IsRunning || !Core.Process.Foreground ||
                ctx.GameUi.ChatParent?.IsChatActive == true)
            {
                this.phase = ExitPhase.WaitingForSafeInput;
                this.Status = "Waiting for foreground game, active bot, and inactive chat";
                return;
            }

            if (ctx.GameUi.IsAnyLargePanelOpen)
            {
                this.phase = ExitPhase.WaitingForSafeInput;
                this.Status = "Waiting for large panels to close before creating an exit portal";
                return;
            }

            if (ctx.Interaction.IsBusy)
            {
                this.phase = ExitPhase.WaitingForSafeInput;
                this.Status = "Waiting for the current interaction to finish";
                return;
            }

            this.sourceAreaHash = ctx.Area.AreaHash;
            this.existingPortalIds.Clear();
            foreach (var existingId in PortalEntitySelector.Snapshot(ctx.Area))
            {
                this.existingPortalIds.Add(existingId);
            }

            // This is deliberately a one-shot key press. If no new portal appears, the terminal
            // timeout below will not send another key press or enter an older portal.
            BotInput.ReleaseAllMovementKeys(ctx.Settings);
            BotInput.FastPressKey(ctx.Settings.PortalKey);
            this.portalKeyPressed = true;
            this.portalKeyTimeUtc = DateTime.UtcNow;
            this.phase = ExitPhase.WaitingForPortal;
            this.Status = $"Opening exit portal [{ctx.Settings.PortalKey}]...";
            ctx.Log($"[ExitHandler] Pressed portal key {ctx.Settings.PortalKey} once");
        }

        private void PollForExitPortal(BotContext ctx)
        {
            var elapsed = DateTime.UtcNow - this.portalKeyTimeUtc;
            if (elapsed >= PortalAppearanceTimeout)
            {
                this.phase = ExitPhase.TimedOut;
                this.Status = "Timed out waiting for a new exit portal; portal key will not be repeated";
                ctx.Log("[ExitHandler] Exit portal timed out; stopping without another key press");
                return;
            }

            var candidates = PortalEntitySelector.FindFreshTargetable(
                ctx.Area,
                ctx.PlayerGrid,
                this.existingPortalIds,
                MaxPortalDistance);

            if (candidates.Count == 0)
            {
                this.Status = $"Waiting for exit portal ({elapsed.TotalSeconds:F0}/{PortalAppearanceTimeout.TotalSeconds:F0}s)";
                return;
            }

            if (!PortalEntitySelector.TrySelectNearest(candidates, out var selected))
            {
                this.phase = ExitPhase.Failed;
                this.Status = "No targetable exit portal candidate was available";
                ctx.Log($"[ExitHandler] {this.Status}");
                return;
            }

            if (ctx.GameUi.IsAnyLargePanelOpen || ctx.Interaction.IsBusy)
            {
                this.Status = ctx.GameUi.IsAnyLargePanelOpen
                    ? "Exit portal found; waiting for large panels to close"
                    : "Exit portal found; waiting for the current interaction to finish";
                return;
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
                return;
            }

            this.interaction = ctx.Interaction;
            this.phase = ExitPhase.Interacting;
            this.Status = $"Approaching nearest exit portal ({selected.Distance:F0}g)";
            ctx.Log($"[ExitHandler] Navigating to nearest new exit portal ({selected.Entity.Path})");
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
