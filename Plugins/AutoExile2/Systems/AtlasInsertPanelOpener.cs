// <copyright file="AtlasInsertPanelOpener.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Systems
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Numerics;
    using AutoExile2.Modes;
    using ClickableTransparentOverlay.Win32;
    using TEHhub;
    using TEHhub.RemoteObjects.Components;
    using TEHhub.RemoteObjects.States.InGameStateObjects;
    using TEHhub.RemoteObjects.UiElement;
    using TEHhub.Utils;

    /// <summary>
    /// Opens the Map Device and stops when its Waystone insertion panel is visibly open.
    /// This intentionally does not insert an item or activate Traverse.
    /// </summary>
    internal sealed class AtlasInsertPanelOpener
    {
        private const int MaxUiSearchNodes = 512;
        private const int MaxUiSearchDepth = 6;
        private const int MaxChildrenPerSearchNode = 128;
        private const int MaxStashCloseAttempts = 3;
        private static readonly TimeSpan WorkflowTimeout = TimeSpan.FromSeconds(90);
        private static readonly TimeSpan StashCloseRetryDelay = TimeSpan.FromSeconds(2);
        private static readonly string MapDeviceVariantsPath =
            "Metadata/Terrain/Missions/Hideouts/Objects/MapDeviceVariants/";

        private OpenerState state;
        private DateTime startedAtUtc;
        private DateTime stashCloseDeadlineUtc;
        private int stashCloseAttempts;
        private bool mapDeviceClickIssued;
        private bool atlasNodeClickIssued;
        private IntPtr targetMapNodeAddress;
        private int targetMapNodeIndex = -1;
        private string targetMapId = string.Empty;
        private InteractionSystem? interaction;

        private static List<Vector2> EmptyPath { get; } = new();

        public string Status { get; private set; } = "Idle";

        public string Decision { get; private set; } = "Idle";

        public bool IsStarted => this.state != OpenerState.Idle;

        public List<Vector2> CurrentNavPath => this.interaction?.CurrentNavPath ?? EmptyPath;

        public int CurrentWaypointIndex => this.interaction?.CurrentWaypointIndex ?? 0;

        public Vector2? CurrentDestination => this.interaction?.CurrentDestination;

        public void Begin()
        {
            this.state = OpenerState.CloseStash;
            this.startedAtUtc = DateTime.UtcNow;
            this.stashCloseDeadlineUtc = DateTime.MinValue;
            this.stashCloseAttempts = 0;
            this.mapDeviceClickIssued = false;
            this.atlasNodeClickIssued = false;
            this.targetMapNodeAddress = IntPtr.Zero;
            this.targetMapNodeIndex = -1;
            this.targetMapId = string.Empty;
            this.Status = "Waystone ready — preparing Map Device";
            this.Decision = "CloseStashSafely";
        }

        public void Reset()
        {
            this.state = OpenerState.Idle;
            this.startedAtUtc = DateTime.MinValue;
            this.stashCloseDeadlineUtc = DateTime.MinValue;
            this.stashCloseAttempts = 0;
            this.mapDeviceClickIssued = false;
            this.atlasNodeClickIssued = false;
            this.targetMapNodeAddress = IntPtr.Zero;
            this.targetMapNodeIndex = -1;
            this.targetMapId = string.Empty;
            this.Status = "Idle";
            this.Decision = "Idle";
        }

        public void Tick(BotContext ctx)
        {
            this.interaction = ctx.Interaction;
            if (this.state is OpenerState.Idle or OpenerState.Completed or OpenerState.Failed)
            {
                return;
            }

            if (!ctx.World.AreaDetails.IsHideout)
            {
                this.Fail(ctx, "Hideout left before the Waystone panel opened", "WaitForHideout");
                return;
            }

            if (DateTime.UtcNow - this.startedAtUtc > WorkflowTimeout)
            {
                this.Fail(ctx, "Timed out while opening the Waystone insertion panel", "MapDeviceWorkflowTimeout");
                return;
            }

            if (this.TryFinishIfPanelAlreadyOpen(ctx))
            {
                return;
            }

            if (ctx.GameUi.IsVendorOpen ||
                (ctx.GameUi.IsStashOpen &&
                 this.state != OpenerState.CloseStash &&
                 this.state != OpenerState.WaitForStashClose))
            {
                this.Fail(ctx, "A Stash or Vendor panel reopened during Map Device setup", "BlockingPanelReopened");
                return;
            }

            if (this.state == OpenerState.CloseStash || this.state == OpenerState.WaitForStashClose)
            {
                this.TickStashClose(ctx);
                return;
            }

            if (this.state == OpenerState.FindMapDevice || this.state == OpenerState.InteractWithMapDevice)
            {
                this.TickMapDevice(ctx);
                return;
            }

            if (this.state == OpenerState.WaitForAtlas)
            {
                this.TickWaitForAtlas(ctx);
                return;
            }

            if (this.state == OpenerState.SelectAtlasNode || this.state == OpenerState.WaitForInsertionPanel)
            {
                this.TickAtlasNode(ctx);
            }
        }

        private void TickStashClose(BotContext ctx)
        {
            if (!ctx.GameUi.IsStashOpen)
            {
                this.state = OpenerState.FindMapDevice;
                this.Status = "Stash closed — finding personal Map Device";
                this.Decision = "FindPersonalMapDevice";
                return;
            }

            if (ctx.GameUi.IsVendorOpen || ctx.GameUi.WorldMapPanel.IsVisible || ctx.GameUi.Atlas.IsVisible)
            {
                this.Fail(ctx, "Stash and another large panel are open — close the extra panel manually", "StashCloseBlocked");
                return;
            }

            if (ctx.Interaction.CurrencyMayBeActive || ctx.Interaction.CurrencyClickInFlight)
            {
                this.Fail(ctx, "Currency interaction is still active — Stash was left open", "StashCloseBlocked");
                return;
            }

            if (!ctx.GameUi.Stash.TryGetSafeCursorCancelUiAddress(out _))
            {
                this.Fail(ctx, "Stash is reported open but its title control could not be verified", "StashCloseUnverified");
                return;
            }

            if (this.stashCloseAttempts > 0 && DateTime.UtcNow < this.stashCloseDeadlineUtc)
            {
                this.Status = $"Waiting for verified Stash close ({this.stashCloseAttempts}/{MaxStashCloseAttempts})";
                this.Decision = "VerifyStashClosed";
                return;
            }

            if (this.stashCloseAttempts >= MaxStashCloseAttempts)
            {
                this.Fail(ctx, $"Stash did not close after {MaxStashCloseAttempts} Close All UI (Spacebar) attempts", "StashCloseUnconfirmed");
                return;
            }

            if (!CanIssueInput(ctx))
            {
                this.Status = "Waiting for foreground game and inactive chat before closing Stash";
                this.Decision = "WaitForSafeInput";
                return;
            }

            // The observed PoE 2 "Close All User Interface" binding is Spacebar.
            // Hold it long enough to register, then verify before any bounded retry.
            BotInput.ReleaseAllMovementKeys(ctx.Settings);
            BotInput.TapKey(VK.SPACE, baseHoldMs: 80);
            this.stashCloseAttempts++;
            this.stashCloseDeadlineUtc = DateTime.UtcNow + StashCloseRetryDelay;
            this.state = OpenerState.WaitForStashClose;
            this.Status = $"Sent Close All UI (Spacebar) to Stash ({this.stashCloseAttempts}/{MaxStashCloseAttempts})";
            this.Decision = "VerifyStashClosed";
        }

        private void TickMapDevice(BotContext ctx)
        {
            if (!this.mapDeviceClickIssued && ctx.GameUi.WorldMapPanel.IsVisible && !ctx.GameUi.Atlas.IsVisible)
            {
                this.Fail(ctx, "World map panel is already open outside the Map Device Atlas view — stopped safely", "WorldMapAlreadyOpen");
                return;
            }

            if (!this.mapDeviceClickIssued && ctx.GameUi.Atlas.IsVisible)
            {
                this.Fail(ctx, "Atlas is already open without the Map Device insertion panel — stopped safely", "AtlasAlreadyOpen");
                return;
            }

            if (this.state == OpenerState.FindMapDevice)
            {
                var device = FindNearestPersonalMapDevice(ctx.Area, ctx.PlayerGrid);
                if (device == null)
                {
                    this.Fail(ctx, "No targetable personal Map Device variant was found nearby", "MapDeviceNotFound");
                    return;
                }

                if (!ctx.Interaction.BeginEntity(
                        ctx,
                        device,
                        "personal Map Device",
                        current => this.mapDeviceClickIssued &&
                                   current.GameUi.Atlas.IsVisible &&
                                   current.GameUi.AtlasMaps.Count > 0,
                        interactionRange: 20f,
                        maxClickAttempts: 1,
                        requireMouseOverEntity: true))
                {
                    this.Fail(ctx, "Could not start a single validated Map Device interaction", "MapDeviceInteractionUnavailable");
                    return;
                }

                this.state = OpenerState.InteractWithMapDevice;
                this.Status = "Approaching the personal Map Device";
                this.Decision = "InteractWithPersonalMapDevice";
            }

            var result = ctx.Interaction.Tick(ctx);
            if (ctx.Interaction.Phase == InteractionPhase.WaitingForSuccess)
            {
                this.mapDeviceClickIssued = true;
            }

            if (result == InteractionResult.Succeeded)
            {
                this.state = OpenerState.WaitForAtlas;
                this.Status = "Map Device interaction confirmed — waiting for Atlas map nodes";
                this.Decision = "WaitForAtlasVisible";
                return;
            }

            if (result == InteractionResult.Failed)
            {
                this.Fail(ctx, $"Map Device interaction stopped: {ctx.Interaction.LastFailure}", "MapDeviceInteractionFailed");
                return;
            }

            this.Status = ctx.Interaction.Status;
            this.Decision = "InteractWithPersonalMapDevice";
        }

        private void TickWaitForAtlas(BotContext ctx)
        {
            if (!ctx.GameUi.Atlas.IsVisible)
            {
                this.Status = "Map Device was clicked — waiting for Atlas to become visible";
                this.Decision = "WaitForAtlasVisible";
                return;
            }

            if (ctx.GameUi.AtlasMaps.Count == 0)
            {
                this.Status = "Atlas is visible — waiting for its node data";
                this.Decision = "WaitForAtlasNodes";
                return;
            }

            this.state = OpenerState.SelectAtlasNode;
            this.Status = "Atlas ready — checking visible accessible normal nodes";
            this.Decision = "FindEligibleAtlasNode";
        }

        private void TickAtlasNode(BotContext ctx)
        {
            if (!ctx.GameUi.Atlas.IsVisible)
            {
                this.Fail(ctx, "Atlas closed before the insertion panel was verified", "AtlasClosed");
                return;
            }

            if (TryFindInsertionPanel(ctx, out _, out var panelDiagnostic))
            {
                this.Complete("Waystone insertion panel is visible and verified", "WaystoneInsertPanelOpen");
                return;
            }

            if (panelDiagnostic.StartsWith("Ambiguous", StringComparison.Ordinal) ||
                panelDiagnostic.Contains("safety limit", StringComparison.OrdinalIgnoreCase) ||
                panelDiagnostic == "Atlas or its shared panel container is not visible")
            {
                this.Fail(ctx, panelDiagnostic, "InsertionPanelAmbiguous");
                return;
            }

            if (this.state == OpenerState.SelectAtlasNode)
            {
                if (this.atlasNodeClickIssued)
                {
                    this.Fail(ctx, "Atlas node click was already issued but the insertion panel is not verified", "InsertionPanelUnconfirmed");
                    return;
                }

                var selected = FindFirstVisibleAccessibleNormalNode(ctx.GameUi);
                if (selected == null)
                {
                    this.Fail(ctx, "No visible accessible normal Atlas node could be verified", "AccessibleAtlasNodeNotFound");
                    return;
                }

                this.targetMapNodeAddress = selected.Value.Node.Address;
                this.targetMapNodeIndex = selected.Value.Node.Index;
                this.targetMapId = selected.Value.Node.MapId;
                if (!ctx.Interaction.BeginUiElement(
                        this.targetMapNodeAddress,
                        $"Atlas map node {selected.Value.Node.DisplayName}",
                        current => TryFindInsertionPanel(current, out _, out _),
                        maxClickAttempts: 1,
                        uiSource: "visible Atlas map node"))
                {
                    this.Fail(ctx, "Could not start a single Atlas node click", "AtlasNodeClickUnavailable");
                    return;
                }

                this.state = OpenerState.WaitForInsertionPanel;
                this.Status = $"Selected one visible accessible normal Atlas node ({selected.Value.Node.DisplayName})";
                this.Decision = "VerifyWaystoneInsertPanel";
            }

            if (!this.atlasNodeClickIssued && !IsSameTargetNodeVisible(ctx.GameUi, this.targetMapNodeIndex, this.targetMapNodeAddress, this.targetMapId))
            {
                this.Fail(ctx, "The chosen Atlas node changed before its one click could be verified", "AtlasNodeChanged");
                return;
            }

            var result = ctx.Interaction.Tick(ctx);
            if (ctx.Interaction.Phase == InteractionPhase.WaitingForSuccess)
            {
                this.atlasNodeClickIssued = true;
            }

            if (result == InteractionResult.Succeeded)
            {
                this.Complete("Waystone insertion panel is visible and verified", "WaystoneInsertPanelOpen");
                return;
            }

            if (result == InteractionResult.Failed)
            {
                this.Fail(ctx, $"Atlas node selection stopped: {ctx.Interaction.LastFailure}", "InsertionPanelUnconfirmed");
                return;
            }

            this.Status = this.atlasNodeClickIssued
                ? "One Atlas node click issued — waiting for the verified Waystone insertion panel"
                : ctx.Interaction.Status;
            this.Decision = "VerifyWaystoneInsertPanel";
        }

        private bool TryFinishIfPanelAlreadyOpen(BotContext ctx)
        {
            if (!ctx.GameUi.Atlas.IsVisible)
            {
                return false;
            }

            if (TryFindInsertionPanel(ctx, out _, out _))
            {
                this.Complete("Waystone insertion panel is already visible and verified", "WaystoneInsertPanelOpen");
                return true;
            }

            if ((this.state is OpenerState.CloseStash or OpenerState.WaitForStashClose or OpenerState.FindMapDevice) &&
                !this.mapDeviceClickIssued)
            {
                this.Fail(ctx, "Atlas is open without the verified Map Device insertion panel — stopped safely", "AtlasAlreadyOpen");
                return true;
            }

            return false;
        }

        private static Entity? FindNearestPersonalMapDevice(AreaInstance area, Vector2 playerGrid)
        {
            if (area == null)
            {
                return null;
            }

            Entity? nearest = null;
            var nearestDistance = Pathfinding.NetworkBubbleRadius;
            foreach (var entity in area.AwakeEntities.Values)
            {
                if (!entity.IsValid || entity.Id == 0 || entity.Address == IntPtr.Zero ||
                    !IsPersonalMapDevicePath(entity.Path) ||
                    !entity.TryGetComponent<Targetable>(out var targetable) || !targetable.IsTargetable ||
                    !entity.TryGetComponent<Render>(out var render))
                {
                    continue;
                }

                var distance = Vector2.Distance(
                    playerGrid,
                    new Vector2(render.GridPosition.X, render.GridPosition.Y));
                if (distance < nearestDistance)
                {
                    nearest = entity;
                    nearestDistance = distance;
                }
            }

            return nearest;
        }

        private static bool IsPersonalMapDevicePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) ||
                !path.StartsWith(MapDeviceVariantsPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return path.Length > MapDeviceVariantsPath.Length &&
                   path.IndexOf('/', MapDeviceVariantsPath.Length) < 0;
        }

        private static (AtlasMapNode Node, UiElementBase Ui)? FindFirstVisibleAccessibleNormalNode(ImportantUiElements gameUi)
        {
            foreach (var node in gameUi.AtlasMaps
                         .Where(node => node.State == AtlasMapNodeState.AccessibleNow &&
                                        IsKnownNormalMap(node.MapId) &&
                                        !string.IsNullOrWhiteSpace(node.MapId))
                         .OrderBy(node => node.Index))
            {
                var nodeUi = gameUi.Atlas[node.Index];
                if (nodeUi == null || nodeUi.Address == IntPtr.Zero || nodeUi.Address != node.Address ||
                    !nodeUi.IsVisible || !IsUsableRect(nodeUi.Position, nodeUi.Size))
                {
                    continue;
                }

                return (node, nodeUi);
            }

            return null;
        }

        private static bool IsSameTargetNodeVisible(
            ImportantUiElements gameUi,
            int index,
            IntPtr address,
            string mapId)
        {
            var node = gameUi.AtlasMaps.FirstOrDefault(candidate =>
                candidate.Index == index && candidate.Address == address &&
                string.Equals(candidate.MapId, mapId, StringComparison.Ordinal) &&
                candidate.State == AtlasMapNodeState.AccessibleNow &&
                IsKnownNormalMap(candidate.MapId));
            var ui = index >= 0 ? gameUi.Atlas[index] : null;
            return node != null && ui != null && ui.Address == address && ui.IsVisible && IsUsableRect(ui.Position, ui.Size);
        }

        private static bool IsKnownNormalMap(string mapId) =>
            string.Equals(WorldAreaTags.GetMeta(mapId)?.Type, "normal", StringComparison.OrdinalIgnoreCase);

        private static bool TryFindInsertionPanel(BotContext ctx, out UiElementBase? panel, out string diagnostic)
        {
            panel = null;
            diagnostic = "No visible Map Device insertion panel matched the observed slot structure";

            var atlas = ctx.GameUi.Atlas;
            var worldMap = ctx.GameUi.WorldMapPanel;
            if (atlas.Address == IntPtr.Zero || !atlas.IsVisible ||
                worldMap.Address == IntPtr.Zero || !worldMap.IsVisible ||
                !worldMap.TryGetParent(out var sharedParent) || sharedParent == null || !sharedParent.IsVisible)
            {
                diagnostic = "Atlas or its shared panel container is not visible";
                return false;
            }

            // UiDump shows the insert panel beside WorldMapPanel under their common parent.
            // Resolve that relationship live instead of baking the captured child path/address.
            var matches = new List<UiElementBase>(2);
            var visited = new HashSet<IntPtr>();
            var pending = new Queue<(UiElementBase Element, int Depth)>();
            for (var i = 0; i < sharedParent.TotalChildrens; i++)
            {
                var sibling = sharedParent[i];
                if (sibling == null || sibling.Address == worldMap.Address || sibling.Address == atlas.Address ||
                    !sibling.IsVisible)
                {
                    continue;
                }

                pending.Enqueue((sibling, 0));
            }

            while (pending.Count > 0 && visited.Count < MaxUiSearchNodes)
            {
                var (element, depth) = pending.Dequeue();
                if (element.Address == IntPtr.Zero || !visited.Add(element.Address) || !element.IsVisible)
                {
                    continue;
                }

                if (LooksLikeInsertionPanel(element))
                {
                    matches.Add(element);
                    if (matches.Count > 1)
                    {
                        diagnostic = "Ambiguous: multiple visible panels matched the Waystone and Tablet slot structure";
                        return false;
                    }
                }

                if (depth >= MaxUiSearchDepth || element.TotalChildrens <= 0 ||
                    element.TotalChildrens > MaxChildrenPerSearchNode)
                {
                    continue;
                }

                for (var i = 0; i < element.TotalChildrens; i++)
                {
                    var child = element[i];
                    if (child != null && child.IsVisible)
                    {
                        pending.Enqueue((child, depth + 1));
                    }
                }
            }

            if (visited.Count >= MaxUiSearchNodes)
            {
                diagnostic = "Atlas panel search reached its safety limit before the insertion panel was unique";
                return false;
            }

            if (matches.Count == 1)
            {
                panel = matches[0];
                diagnostic = "Verified visible Waystone and Tablet insertion slots";
                return true;
            }

            return false;
        }

        private static bool LooksLikeInsertionPanel(UiElementBase candidate)
        {
            if (!candidate.IsVisible || candidate.TotalChildrens != 3 ||
                !IsPanelRect(candidate.Position, candidate.Size))
            {
                return false;
            }

            var waystoneSlot = candidate[0];
            var tabletSlot = candidate[1];
            if (waystoneSlot == null || tabletSlot == null ||
                !waystoneSlot.IsVisible || !tabletSlot.IsVisible ||
                waystoneSlot.TotalChildrens > 4 ||
                tabletSlot.TotalChildrens > 8 ||
                !IsUsableRect(waystoneSlot.Position, waystoneSlot.Size) ||
                !IsUsableRect(tabletSlot.Position, tabletSlot.Size))
            {
                return false;
            }

            var waystoneAspect = waystoneSlot.Size.X / waystoneSlot.Size.Y;
            var tabletAspect = tabletSlot.Size.X / tabletSlot.Size.Y;
            var widthRatio = tabletSlot.Size.X / waystoneSlot.Size.X;
            var heightRatio = tabletSlot.Size.Y / waystoneSlot.Size.Y;
            return waystoneAspect is >= 0.75f and <= 1.3f &&
                   tabletAspect >= 2f &&
                   widthRatio >= 1.25f &&
                   heightRatio <= 0.8f &&
                   IsContainedWithMargin(candidate.Position, candidate.Size, waystoneSlot.Position, waystoneSlot.Size) &&
                   IsContainedWithMargin(candidate.Position, candidate.Size, tabletSlot.Position, tabletSlot.Size);
        }

        private static bool IsContainedWithMargin(Vector2 outerPosition, Vector2 outerSize, Vector2 innerPosition, Vector2 innerSize)
        {
            var marginX = outerSize.X * 0.03f;
            var marginY = outerSize.Y * 0.03f;
            return innerPosition.X >= outerPosition.X - marginX &&
                   innerPosition.Y >= outerPosition.Y - marginY &&
                   innerPosition.X + innerSize.X <= outerPosition.X + outerSize.X + marginX &&
                   innerPosition.Y + innerSize.Y <= outerPosition.Y + outerSize.Y + marginY;
        }

        private static bool IsUsableRect(Vector2 position, Vector2 size) =>
            float.IsFinite(position.X) && float.IsFinite(position.Y) &&
            float.IsFinite(size.X) && float.IsFinite(size.Y) &&
            size.X >= 16f && size.Y >= 16f && size.X <= 800f && size.Y <= 300f;

        private static bool IsPanelRect(Vector2 position, Vector2 size) =>
            float.IsFinite(position.X) && float.IsFinite(position.Y) &&
            float.IsFinite(size.X) && float.IsFinite(size.Y) &&
            size.X >= 160f && size.Y >= 160f && size.X <= 1600f && size.Y <= 1000f;

        private static bool CanIssueInput(BotContext ctx) =>
            ctx.Settings.IsRunning &&
            Core.Process.Foreground &&
            ctx.GameUi.ChatParent?.IsChatActive != true;

        private void Complete(string status, string decision)
        {
            this.state = OpenerState.Completed;
            this.Status = status;
            this.Decision = decision;
        }

        private void Fail(BotContext ctx, string status, string decision)
        {
            if (ctx.Interaction.IsBusy)
            {
                ctx.Interaction.Cancel(ctx.Settings);
            }

            this.state = OpenerState.Failed;
            this.Status = status;
            this.Decision = decision;
        }

        private enum OpenerState
        {
            Idle,
            CloseStash,
            WaitForStashClose,
            FindMapDevice,
            InteractWithMapDevice,
            WaitForAtlas,
            SelectAtlasNode,
            WaitForInsertionPanel,
            Completed,
            Failed,
        }
    }
}
