// <copyright file="ExitHandler.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Systems
{
    using System;
    using System.Numerics;
    using GameHelper.RemoteObjects.Components;
    using AutoExile2.Modes;
    using AutoExile2.Modes.Shared;

    /// <summary>
    /// Handles map exit flow: presses portal hotkey, searches for spawned portal entity,
    /// and clicks portal to return to town/hideout.
    /// Ported directly from AutoExile 1 exit architecture, adapted for PoE 2.
    /// </summary>
    public class ExitHandler
    {
        private bool portalKeyPressed;
        private DateTime portalKeyTime = DateTime.MinValue;
        private DateTime lastPortalClickTime = DateTime.MinValue;
        private DateTime lastCandidateLogTime = DateTime.MinValue;

        public string Status { get; private set; } = string.Empty;

        public bool IsExitInitiated => this.portalKeyPressed;

        public void Reset()
        {
            this.portalKeyPressed = false;
            this.portalKeyTime = DateTime.MinValue;
            this.lastPortalClickTime = DateTime.MinValue;
            this.lastCandidateLogTime = DateTime.MinValue;
            this.Status = string.Empty;
        }

        private const float PortalCastDelaySeconds = 2.5f;
        private const float PortalTimeoutSeconds = 6.0f;
        private const int ClickIntervalMs = 1500;

        public void Tick(BotContext ctx)
        {
            BotInput.ReleaseAllMovementKeys(ctx.Settings);

            if (!this.portalKeyPressed)
            {
                // Press portal key from settings (default B in PoE 2)
                BotInput.TapKey(ctx.Settings.PortalKey);
                this.portalKeyPressed = true;
                this.portalKeyTime = DateTime.Now;
                this.lastPortalClickTime = DateTime.MinValue;
                this.lastCandidateLogTime = DateTime.MinValue;
                this.Status = $"Opening portal [{ctx.Settings.PortalKey}]...";
                ctx.Log($"[ExitHandler] Pressed portal key {ctx.Settings.PortalKey}");
                return;
            }

            // Wait for portal cast/channeling animation to complete (PoE 2 takes ~2-3s)
            double elapsed = (DateTime.Now - this.portalKeyTime).TotalSeconds;
            if (elapsed < PortalCastDelaySeconds)
            {
                this.Status = $"Casting portal... ({elapsed:F1}s/{PortalCastDelaySeconds:F1}s)";
                return;
            }

            // Find nearest targetable portal entity via ModeHelpers
            var portal = ModeHelpers.FindNearestPortal(ctx.Area, ctx.PlayerGrid, 60f);
            if (portal != null)
            {
                // Rate limit clicks (every 1.5 seconds)
                if ((DateTime.Now - this.lastPortalClickTime).TotalMilliseconds > ClickIntervalMs)
                {
                    if (ModeHelpers.ClickEntity(ctx.World, portal))
                    {
                        this.lastPortalClickTime = DateTime.Now;
                        this.Status = "Clicking portal to exit...";
                        ctx.Log($"[ExitHandler] Clicked exit portal ({portal.Path})");
                    }
                }
            }
            else
            {
                // Auto-discovery logging: print all nearby targetable entities to console
                if ((DateTime.Now - this.lastCandidateLogTime).TotalSeconds > 2.0)
                {
                    this.lastCandidateLogTime = DateTime.Now;
                    ctx.Log("[ExitHandler] Portal not found yet. Scanning nearby targetables in PoE 2:");
                    int count = 0;
                    foreach (var e in ctx.Area.AwakeEntities.Values)
                    {
                        if (e.IsValid && e.TryGetComponent<Targetable>(out var t) && t.IsTargetable && e.TryGetComponent<Render>(out var r))
                        {
                            var pos = new Vector2(r.GridPosition.X, r.GridPosition.Y);
                            float d = Vector2.Distance(ctx.PlayerGrid, pos);
                            if (d <= 70f)
                            {
                                ctx.Log($"  -> Nearby Entity: Path='{e.Path}', Type={e.EntityType}, Subtype={e.EntitySubtype}, Dist={d:F1}");
                                count++;
                            }
                        }
                    }

                    if (count == 0)
                    {
                        ctx.Log("  -> (No targetable entities within 70 grid units)");
                    }
                }

                if (elapsed > PortalTimeoutSeconds)
                {
                    // Timeout: portal didn't spawn or player got interrupted, retry pressing key
                    ctx.Log($"[ExitHandler] Portal entity not found after {PortalTimeoutSeconds:F1}s, retrying portal key...");
                    this.portalKeyPressed = false;
                }
            }
        }
    }
}
