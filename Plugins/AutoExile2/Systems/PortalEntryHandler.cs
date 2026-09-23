// <copyright file="PortalEntryHandler.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Systems
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;
    using AutoExile2.Modes;
    using TEHhub;

    /// <summary>Resulting phase of the explicit post-Traverse portal entry flow.</summary>
    public enum PortalEntryPhase
    {
        Idle,
        WaitingForPortal,
        Interacting,
        InMapReady,
        TimedOut,
        Failed,
    }

    /// <summary>
    /// Handles the portal interaction after a future Atlas opener has already issued Traverse.
    /// It never opens Atlas or repeats Traverse: it only polls for a new targetable town portal,
    /// then asks the shared InteractionSystem to enter it and verifies the area transition.
    /// </summary>
    public sealed class PortalEntryHandler
    {
        private static readonly TimeSpan PortalAppearanceTimeout = TimeSpan.FromSeconds(30);
        private const float MaxPortalDistance = 80f;
        private static readonly List<Vector2> EmptyPath = new();

        private readonly HashSet<uint> existingPortalIds = new();
        private string sourceAreaHash = string.Empty;
        private DateTime waitStartedAtUtc;
        private InteractionSystem? interaction;

        public PortalEntryPhase Phase { get; private set; } = PortalEntryPhase.Idle;

        public string Status { get; private set; } = "Idle";

        public bool IsTerminal => this.Phase is PortalEntryPhase.InMapReady or
            PortalEntryPhase.TimedOut or PortalEntryPhase.Failed;

        public List<Vector2> CurrentNavPath => this.interaction?.CurrentNavPath ?? EmptyPath;

        public int CurrentWaypointIndex => this.interaction?.CurrentWaypointIndex ?? 0;

        public Vector2? CurrentDestination => this.interaction?.CurrentDestination;

        /// <summary>
        /// Captures town portal entity ids before the Atlas opener clicks Traverse. Pass this
        /// snapshot to BeginAfterTraverse so a portal that appears immediately after the click
        /// is not mistaken for an older portal.
        /// </summary>
        public static IReadOnlyCollection<uint> CapturePortalSnapshot(BotContext ctx) =>
            PortalEntitySelector.Snapshot(ctx.Area);

        /// <summary>
        /// Starts polling after the caller has issued its one-shot Traverse click and stopped any
        /// retrying UI request. Pass a snapshot captured before the click; this method neither
        /// cancels nor issues input.
        /// </summary>
        public bool BeginAfterTraverse(
            BotContext ctx,
            IReadOnlyCollection<uint> portalIdsBeforeTraverse)
        {
            if (this.Phase == PortalEntryPhase.Interacting && ctx.Interaction.IsBusy)
            {
                this.Status = "Cannot restart while the previous portal interaction is active";
                return false;
            }

            this.ResetState();
            if (ctx.Interaction.IsBusy)
            {
                this.Phase = PortalEntryPhase.Failed;
                this.Status = "Traverse interaction is still active; stop it after the one-shot click before polling";
                return false;
            }

            if (portalIdsBeforeTraverse == null)
            {
                this.Phase = PortalEntryPhase.Failed;
                this.Status = "A pre-Traverse portal snapshot is required";
                return false;
            }

            this.sourceAreaHash = ctx.Area.AreaHash ?? string.Empty;
            if (string.IsNullOrWhiteSpace(this.sourceAreaHash) ||
                (!ctx.World.AreaDetails.IsHideout && !ctx.World.AreaDetails.IsTown))
            {
                this.Phase = PortalEntryPhase.Failed;
                this.Status = "Portal entry can only start from a town or hideout area";
                return false;
            }

            this.interaction = ctx.Interaction;
            foreach (var entityId in portalIdsBeforeTraverse)
            {
                this.existingPortalIds.Add(entityId);
            }

            this.waitStartedAtUtc = DateTime.UtcNow;
            this.Phase = PortalEntryPhase.WaitingForPortal;
            this.Status = "Waiting for the Traverse portal";
            return true;
        }

        /// <summary>Advances the portal poll or the bounded entity interaction by one tick.</summary>
        public PortalEntryPhase Tick(BotContext ctx)
        {
            if (this.IsTerminal || this.Phase == PortalEntryPhase.Idle)
            {
                return this.Phase;
            }

            if (this.AreaChangedToMap(ctx))
            {
                if (this.Phase == PortalEntryPhase.Interacting)
                {
                    ctx.Interaction.Cancel(ctx.Settings);
                }
                this.Phase = PortalEntryPhase.InMapReady;
                this.Status = "Entered map — ready";
                return this.Phase;
            }

            if (this.Phase == PortalEntryPhase.WaitingForPortal)
            {
                var elapsed = DateTime.UtcNow - this.waitStartedAtUtc;
                if (elapsed >= PortalAppearanceTimeout)
                {
                    this.Phase = PortalEntryPhase.TimedOut;
                    this.Status = "Timed out waiting for the Traverse portal; no retry was issued";
                    return this.Phase;
                }

                var candidates = PortalEntitySelector.FindFreshTargetable(
                    ctx.Area,
                    ctx.PlayerGrid,
                    this.existingPortalIds,
                    MaxPortalDistance);

                if (candidates.Count == 0)
                {
                    this.Status = $"Waiting for the Traverse portal ({elapsed.TotalSeconds:F0}/{PortalAppearanceTimeout.TotalSeconds:F0}s)";
                    return this.Phase;
                }

                if (!PortalEntitySelector.TrySelectNearest(candidates, out var selected))
                {
                    this.Phase = PortalEntryPhase.Failed;
                    this.Status = "No targetable portal candidate was available";
                    return this.Phase;
                }

                if (ctx.GameUi.IsAnyLargePanelOpen)
                {
                    this.Status = "Traverse portal found; waiting for large panels to close";
                    return this.Phase;
                }

                if (ctx.Interaction.IsBusy)
                {
                    this.Phase = PortalEntryPhase.Failed;
                    this.Status = "Cannot enter portal because another interaction is still active";
                    return this.Phase;
                }

                bool started = ctx.Interaction.BeginEntity(
                    ctx,
                    selected.Entity,
                    "Traverse portal",
                    current => !string.Equals(current.Area.AreaHash, this.sourceAreaHash, StringComparison.Ordinal) &&
                               !current.World.AreaDetails.IsHideout &&
                               !current.World.AreaDetails.IsTown,
                    interactionRange: 20f,
                    maxClickAttempts: 3,
                    requireMouseOverEntity: true);

                if (!started)
                {
                    this.Phase = PortalEntryPhase.Failed;
                    this.Status = "Could not start the portal interaction";
                    return this.Phase;
                }

                this.interaction = ctx.Interaction;
                this.Phase = PortalEntryPhase.Interacting;
                this.Status = $"Approaching nearest Traverse portal ({selected.Distance:F0}g)";
                return this.Phase;
            }

            if (this.Phase == PortalEntryPhase.Interacting)
            {
                if (!ctx.Settings.IsRunning || !Core.Process.Foreground ||
                    ctx.GameUi.ChatParent?.IsChatActive == true)
                {
                    ctx.Interaction.Cancel(ctx.Settings);
                    this.Phase = PortalEntryPhase.Failed;
                    this.Status = "Portal entry stopped because the bot, foreground game, or chat state changed";
                    return this.Phase;
                }

                if (ctx.GameUi.IsAnyLargePanelOpen)
                {
                    ctx.Interaction.Cancel(ctx.Settings);
                    this.Phase = PortalEntryPhase.Failed;
                    this.Status = "Portal entry cancelled because a large panel opened";
                    return this.Phase;
                }

                if (ctx.Interaction.Phase == InteractionPhase.Idle)
                {
                    this.Phase = PortalEntryPhase.Failed;
                    this.Status = "Portal entry stopped because its interaction was reset";
                    return this.Phase;
                }

                var result = ctx.Interaction.Tick(ctx);
                this.Status = ctx.Interaction.Status;
                if (result == InteractionResult.Succeeded)
                {
                    this.Phase = this.AreaChangedToMap(ctx)
                        ? PortalEntryPhase.InMapReady
                        : PortalEntryPhase.Failed;
                    this.Status = this.Phase == PortalEntryPhase.InMapReady
                        ? "Entered map — ready"
                        : "Portal interaction completed without a map area transition";
                }
                else if (result == InteractionResult.Failed)
                {
                    this.Phase = PortalEntryPhase.Failed;
                }
            }

            return this.Phase;
        }

        /// <summary>Resets this flow and cancels its active portal interaction, if any.</summary>
        public void Reset(BotContext ctx)
        {
            if (this.Phase == PortalEntryPhase.Interacting)
            {
                ctx.Interaction.Cancel(ctx.Settings);
            }

            this.ResetState();
        }

        private bool AreaChangedToMap(BotContext ctx) =>
            !string.Equals(ctx.Area.AreaHash, this.sourceAreaHash, StringComparison.Ordinal) &&
            !ctx.World.AreaDetails.IsHideout &&
            !ctx.World.AreaDetails.IsTown;

        private void ResetState()
        {
            this.existingPortalIds.Clear();
            this.sourceAreaHash = string.Empty;
            this.waitStartedAtUtc = DateTime.MinValue;
            this.interaction = null;
            this.Phase = PortalEntryPhase.Idle;
            this.Status = "Idle";
        }
    }
}
