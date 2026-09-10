// <copyright file="Co-op FollowerMode.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Modes
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Numerics;
    using GameHelper.RemoteEnums;
    using GameHelper.RemoteEnums.Entity;
    using GameHelper.RemoteObjects;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.RemoteObjects.States.InGameStateObjects;
    using GameHelper.Utils;
    using GameOffsets.Natives;
    using ImGuiNET;
    using AutoExile2.Brain;
    using AutoExile2.Brain.Workers;
    using AutoExile2.Systems;

    /// <summary>
    /// Couch Co-op Follower Mode for PoE 2 on a single PC.
    /// - Player 1 (Leader / User): 100% manual movement &amp; attacks on Physical Gamepad via Virtual Pad #1 passthrough.
    ///   Bot injects automated Life/Mana flasks (LB/LT) and Auto-Buffs / Guard skills (e.g. Steelskin, Enduring Cry).
    /// - Player 2 (Follower / Bot): 100% autonomous via Virtual Gamepad #2 (ViGEm).
    ///   Navigation: Hybrid A* Pathfinding around obstacles + Direct steering when in Line-of-Sight.
    ///   Controls: Left Stick steering, Right Stick aiming, Buttons A/B/X/Y/RB/RT attacks &amp; flasks on Controller #2.
    ///   Keyboard is left 100% free for the user to manually control / interact with Character 2 without bot interference.
    /// </summary>
    public class CoopFollowerMode : IBotMode
    {
        public string Name => "Co-op FollowerMode";

        public string Description => "Couch Co-op FollowerMode";

        public string Icon => "🎮";

        public AutoExileMode ModeType => AutoExileMode.Follower;

        public string CurrentState { get; private set; } = "Standby";

        public string CurrentAction { get; private set; } = "Initializing Follower Controls...";

        public List<Vector2> CurrentNavPath => this.movementWorker.CurrentNavPath;

        public int CurrentWaypointIndex => this.movementWorker.CurrentWaypointIndex;

        public Vector2? CurrentDestination => this.movementWorker.CurrentDestination;

        // PoE 2 Movement Animation IDs discovered from Memory:
        public const int ANIM_IDLE = 0x0;
        public const int ANIM_RUN = 0x4;
        public const int ANIM_ROLL = 0x10C;   // 268 decimal: Dodge Roll (Tap B)
        public const int ANIM_SPRINT = 0x368; // 872 decimal: Sprint (Hold B)

        private readonly CentralBrain brain = new();
        private readonly CoopMovementWorker movementWorker = new();
        private readonly CoopCombatWorker combatWorker = new();

        private Vector2? lastKnownLeaderGrid;
        private Vector2? lastKnownFollowerGrid;
        private int currentFollowerAnimId = 0;
        private int nearbyEnemyCount = 0;
        private DateTime lastFollowerLifeFlaskTime = DateTime.MinValue;
        private DateTime lastFollowerManaFlaskTime = DateTime.MinValue;
        private Vector2 leaderHeading = new Vector2(0, -1);

        public CentralBrain Brain => this.brain;
        public CoopMovementWorker MovementWorker => this.movementWorker;
        public CoopCombatWorker CombatWorker => this.combatWorker;

        public bool IsFollowerSprinting => this.movementWorker.IsSprinting;
        public Vector2? LastKnownLeaderGrid => this.lastKnownLeaderGrid;
        public Vector2? LastKnownFollowerGrid => this.lastKnownFollowerGrid;
        public Vector2 LeaderHeading => this.leaderHeading;

        public void OnEnter(BotContext ctx)
        {
            ctx.Log("Entering Co-op FollowerMode");
            this.brain.Reset();
            this.movementWorker.Reset();
            this.combatWorker.Reset();
            this.lastKnownLeaderGrid = null;
            this.lastKnownFollowerGrid = null;
            this.CurrentState = "Connecting Controls";
            this.CurrentAction = "Connecting Controls & Gamepads...";

            ctx.CoopGamepad.EnsureConnected(true, ctx.Settings.CoopPhysicalPadIndex, true);
        }

        public void OnExit(BotContext ctx)
        {
            ctx.Log("Exiting Co-op FollowerMode - Releasing inputs (keeping gamepads connected)");
            this.brain.Reset();
            this.movementWorker.Reset();
            this.combatWorker.Reset();
            ctx.CoopGamepad.ResetAllInputs();
            BotInput.ReleaseAllMovementKeys(ctx.Settings);
        }

        public void Tick(BotContext ctx)
        {
            var area = ctx.Area;
            var world = ctx.World;
            var leader = ctx.Player;
            var leaderGrid = ctx.PlayerGrid;
            var pad = ctx.CoopGamepad;
            var s = ctx.Settings;

            // Ensure dual virtual gamepads are connected
            if (!pad.IsLeaderConnected || !pad.IsFollowerConnected)
            {
                pad.EnsureConnected(true, s.CoopPhysicalPadIndex, true);
            }

            // 1. Check Town or Hideout (Peaceful zones)
            bool isPeacefulZone = world.AreaDetails.IsTown || world.AreaDetails.IsHideout;

            // 2. Handle Player 1 (Leader) automated assists (flasks/buffs only outside town/hideout)
            if (!isPeacefulZone)
            {
                this.TickLeaderAssists(ctx, leader, pad, s);
            }

            // 3. Locate Player 2 (Follower Entity in AwakeEntities)
            Entity? followerEntity = this.FindFollowerEntity(area, leader, s.FollowerCharacterName);
            if (followerEntity == null || !followerEntity.TryGetComponent<Render>(out var fRender))
            {
                this.CurrentState = "[Brain: Idle]";
                this.CurrentAction = "Waiting for Player 2...";
                this.movementWorker.Reset();
                pad.SetFollowerMovement(Vector2.Zero);
                pad.SetFollowerAim(Vector2.Zero);
                pad.SetFollowerSprint(false);
                return;
            }

            var followerGrid = new Vector2(fRender.GridPosition.X, fRender.GridPosition.Y);
            this.lastKnownFollowerGrid = followerGrid;

            // Dead Corpse Protection: If Follower HP <= 0, halt all follower actions
            if (followerEntity.TryGetComponent<Life>(out var fLife) && fLife.Health.Total > 0 && fLife.Health.Current <= 0)
            {
                float distToCorpse = Vector2.Distance(leaderGrid, followerGrid);
                this.CurrentState = "[Brain: DeadOrRevive]";
                this.CurrentAction = distToCorpse <= 10f
                    ? $"💀 Follower dead (HP: 0) - Leader nearby ({distToCorpse:F0}g), ready to revive"
                    : $"💀 Follower dead (HP: 0) - {distToCorpse:F0}g from Leader";
                this.movementWorker.Reset();
                pad.SetFollowerMovement(Vector2.Zero);
                pad.SetFollowerAim(Vector2.Zero);
                pad.SetFollowerSprint(false);
                return;
            }

            if (this.lastKnownLeaderGrid.HasValue)
            {
                var lDelta = leaderGrid - this.lastKnownLeaderGrid.Value;
                // Only update heading when leader actually traveled a meaningful distance (> 1.5 grid units)
                if (lDelta.LengthSquared() >= 1.5f)
                {
                    var newHeading = Vector2.Normalize(lDelta);
                    if (this.leaderHeading == Vector2.Zero)
                    {
                        this.leaderHeading = newHeading;
                    }
                    else
                    {
                        this.leaderHeading = Vector2.Normalize(Vector2.Lerp(this.leaderHeading, newHeading, 0.4f));
                    }
                    this.lastKnownLeaderGrid = leaderGrid;
                }
            }
            else
            {
                this.lastKnownLeaderGrid = leaderGrid;
            }

            // Read Follower Actor.Animation in real-time
            if (followerEntity.TryGetComponent<Actor>(out var fActor))
            {
                this.currentFollowerAnimId = (int)fActor.Animation;
            }
            else
            {
                this.currentFollowerAnimId = 0;
            }

            // 4. Reflex Layer (100% Autonomous / Immediate): Follower automated flasks
            if (!isPeacefulZone)
            {
                this.TickFollowerFlasks(followerEntity, pad, s);
            }

            // 5. Environmental Scan: Hostiles
            Entity? bestTarget = null;
            float closestDist = float.MaxValue;
            int nearbyEnemies = 0;

            foreach (var kvp in area.AwakeEntities)
            {
                var ent = kvp.Value;
                if (ent.Address == followerEntity.Address) continue;
                if (!CombatSystem.IsHostileMonster(ent, ctx.Player.Address)) continue;
                if (!ent.TryGetComponent<Render>(out var mRender)) continue;

                var mGrid = new Vector2(mRender.GridPosition.X, mRender.GridPosition.Y);
                float dFollower = Vector2.Distance(followerGrid, mGrid);
                float dLeader = Vector2.Distance(leaderGrid, mGrid);

                if (dFollower <= 75f || dLeader <= 55f)
                {
                    nearbyEnemies++;
                    if (dFollower < closestDist)
                    {
                        closestDist = dFollower;
                        bestTarget = ent;
                    }
                }
            }

            this.nearbyEnemyCount = nearbyEnemies;

            // Formation target
            float safeDist = Math.Max(2f, s.CoopStopDistance);
            Vector2 fixedHeading = new Vector2(0, -1);
            Vector2 formationTarget = GetFormationTarget(leaderGrid, fixedHeading, s.FollowerPosition, safeDist);
            if (area.GridWalkableData != null && area.TerrainMetadata.BytesPerRow > 0 &&
                !Pathfinding.IsWalkable(area.GridWalkableData, area.TerrainMetadata.BytesPerRow, (int)formationTarget.X, (int)formationTarget.Y))
            {
                formationTarget = leaderGrid;
            }

            // 6. World Perception (Central Brain Input)
            var perception = WorldPerception.CollectCoop(
                ctx,
                leader,
                leaderGrid,
                followerEntity,
                followerGrid,
                this.leaderHeading,
                this.currentFollowerAnimId,
                formationTarget,
                nearbyEnemies,
                closestDist,
                bestTarget);

            // 7. Central Decision Brain (Goal Selection & Concurrent Directive Synthesis)
            var activeGoal = this.brain.EvaluateCoopGoal(perception, s, this.movementWorker.IsSprinting);
            var activeDirective = this.brain.CurrentDirective;

            // 8. Specialized Workers Execution (Concurrent Multitasking Execution)
            // 8.1 Combat Worker (Attack-Moving, Aiming & Continuous Culling)
            bool didCombat = this.combatWorker.Execute(activeGoal, perception, pad, s, ctx, activeDirective);

            // 8.2 Movement Worker (Pathing, Fly-by Loot Pickup & Micro-Dodge Slams)
            this.movementWorker.Execute(activeGoal, perception, pad, s, ctx, didCombat, activeDirective);

            // 9. Telemetry & State Reporting
            this.CurrentState = $"[Brain: {activeGoal.Type}]";
            string sprintBadge = this.movementWorker.IsSprinting ? " [SPRINTING]" : "";
            this.CurrentAction = $"{activeGoal.Reason}{sprintBadge}";
            if (didCombat && !string.IsNullOrEmpty(this.combatWorker.ActiveSkillName))
            {
                this.CurrentAction += $" | {this.combatWorker.ActiveSkillName}";
            }
        }

        private void TickLeaderAssists(BotContext ctx, Entity leader, CoopVirtualGamepad pad, AutoExile2Settings s)
        {
            if (leader.TryGetComponent<Life>(out var life) && life.Health.Total > 0)
            {
                if (life.Health.Current <= 0) return; // Leader is dead, do not spam flasks or buffs
                var lVitals = new PlayerVitals(life);

                // Leader Auto Life Flask
                if (s.P1AutoLifeFlask && lVitals.HpPercent <= s.P1LifeFlaskThresholdPercent)
                {
                    int lifeSlot = CombatSystem.GetFlaskSlotFromKey(s.LifeFlaskKey, 0);
                    bool active = s.CheckFlaskActiveEffect && CombatSystem.IsFlaskActive(leader, lifeSlot, isLife: true);
                    bool hasCharges = !s.CheckFlaskCharges || CombatSystem.HasFlaskCharges(ctx.Area.ServerDataObject, lifeSlot);

                    if (!active && hasCharges)
                    {
                        const int debounceMs = CombatSystem.FlaskDebounceMs;
                        if (pad.IsLeaderConnected)
                        {
                            pad.PressLeaderFlask(true, debounceMs);
                        }
                        else
                        {
                            BotInput.FastPressKey(s.LifeFlaskKey);
                        }
                    }
                }

                // Leader Auto Mana Flask
                if (s.P1AutoManaFlask && lVitals.ManaPercent <= s.P1ManaFlaskThresholdPercent)
                {
                    int manaSlot = CombatSystem.GetFlaskSlotFromKey(s.ManaFlaskKey, 1);
                    bool active = s.CheckFlaskActiveEffect && CombatSystem.IsFlaskActive(leader, manaSlot, isLife: false);
                    bool hasCharges = !s.CheckFlaskCharges || CombatSystem.HasFlaskCharges(ctx.Area.ServerDataObject, manaSlot);

                    if (!active && hasCharges)
                    {
                        const int debounceMs = CombatSystem.FlaskDebounceMs;
                        if (pad.IsLeaderConnected)
                        {
                            pad.PressLeaderFlask(false, debounceMs);
                        }
                        else
                        {
                            BotInput.FastPressKey(s.ManaFlaskKey);
                        }
                    }
                }

                // Leader Auto Buffs & Guard skills (Full SkillSlotConfig)
                if (s.P1Skills != null && s.P1Skills.Count > 0)
                {
                    var now = DateTime.Now;
                    var buffs = leader.TryGetComponent<Buffs>(out var pBuffs) ? pBuffs : null;

                    foreach (var slot in s.P1Skills.Where(x => x.Enabled && (x.Role == SkillRole.SelfBuffGuard || x.Category == "Buff" || x.Category == "Guard" || x.Category == "Warcry")).OrderByDescending(x => x.Priority))
                    {
                        int effectiveInterval = CombatSystem.HasAvailableCharges(leader, slot) ? Math.Min(slot.MinCastIntervalMs, 300) : slot.MinCastIntervalMs;
                        if ((now - slot.LastCastAt).TotalMilliseconds < effectiveInterval) continue;
                        if (!CombatSystem.IsSkillReadyInGame(leader, slot)) continue;

                        if (slot.OnlyOnLowHp && !lVitals.IsLowVital(slot)) continue;

                        if (slot.MinManaPercent > 0 && lVitals.ManaPercent < slot.MinManaPercent) continue;

                        if (slot.MinNearbyEnemies > 0)
                        {
                            Vector2 lGrid = leader.TryGetComponent<Render>(out var lRend)
                                ? new Vector2(lRend.GridPosition.X, lRend.GridPosition.Y)
                                : ctx.PlayerGrid;
                            int nearby = CombatSystem.CountHostilesInRange(ctx.Area, lGrid, 65f);
                            if (nearby < slot.MinNearbyEnemies) continue;
                        }

                        if (slot.OnlyWhenBuffMissing && CombatSystem.HasBuff(leader, slot))
                        {
                            continue;
                        }

                        // Cast on Virtual Controller #1 or via Keyboard Hotkey if on Physical Gamepad
                        slot.LastCastAt = now;
                        if (pad.IsLeaderConnected)
                        {
                            pad.PressLeaderBuff(slot.GamepadButton, Math.Max(30, slot.HoldDurationMs));
                        }
                        else
                        {
                            BotInput.FastPressKey(slot.Key);
                        }
                        break;
                    }
                }
            }
        }

        private void TickFollowerFlasks(Entity follower, CoopVirtualGamepad pad, AutoExile2Settings s)
        {
            if (follower.TryGetComponent<Life>(out var fLife) && fLife.Health.Total > 0)
            {
                if (fLife.Health.Current <= 0) return; // Follower is dead, do not waste flasks
                var fVitals = new PlayerVitals(fLife);

                const int debounceMs = CombatSystem.FlaskDebounceMs;

                if (s.P2AutoLifeFlask && fVitals.HpPercent <= s.P2LifeFlaskThresholdPercent)
                {
                    bool active = s.CheckFlaskActiveEffect && CombatSystem.IsFlaskActive(follower, 0, isLife: true);
                    if (!active)
                    {
                        pad.PressFollowerFlask(true, debounceMs);
                    }
                }

                if (s.P2AutoManaFlask && fVitals.ManaPercent <= s.P2ManaFlaskThresholdPercent)
                {
                    bool active = s.CheckFlaskActiveEffect && CombatSystem.IsFlaskActive(follower, 1, isLife: false);
                    if (!active)
                    {
                        pad.PressFollowerFlask(false, debounceMs);
                    }
                }
            }
        }


        public static (float minRight, float maxRight, float minFwd, float maxFwd) GetPositionBounds(CoopFollowerPosition pos, float followDist)
        {
            float d = Math.Max(4f, followDist);
            float halfD = d * 0.5f;

            return pos switch
            {
                CoopFollowerPosition.Back           => (0f, 0f, -d, -d),
                CoopFollowerPosition.Forward        => (0f, 0f, d, d),
                CoopFollowerPosition.BackLeft       => (-d * 0.7f, -d * 0.7f, -d * 0.7f, -d * 0.7f),
                CoopFollowerPosition.BackRight      => (d * 0.7f, d * 0.7f, -d * 0.7f, -d * 0.7f),
                CoopFollowerPosition.ForwardLeft    => (-d * 0.7f, -d * 0.7f, d * 0.7f, d * 0.7f),
                CoopFollowerPosition.ForwardRight   => (d * 0.7f, d * 0.7f, d * 0.7f, d * 0.7f),
                CoopFollowerPosition.BackHalf       => (-d, d, -d * 1.3f, -Math.Min(3f, halfD)),
                CoopFollowerPosition.ForwardHalf    => (-d, d, Math.Min(3f, halfD), d * 1.3f),
                CoopFollowerPosition.LeftWing       => (-d * 1.3f, -Math.Min(3f, halfD), -d, d),
                CoopFollowerPosition.RightWing      => (Math.Min(3f, halfD), d * 1.3f, -d, d),
                CoopFollowerPosition.BackLeftQuad   => (-d * 1.2f, -Math.Min(3f, halfD), -d * 1.2f, -Math.Min(3f, halfD)),
                CoopFollowerPosition.BackRightQuad  => (Math.Min(3f, halfD), d * 1.2f, -d * 1.2f, -Math.Min(3f, halfD)),
                _                                   => (0f, 0f, -d, -d),
            };
        }

        public static bool IsInFormationZone(Vector2 followerGrid, Vector2 leaderGrid, Vector2 heading, CoopFollowerPosition pos, float followDist)
        {
            if (heading == Vector2.Zero) heading = new Vector2(0, -1);
            var fwd = Vector2.Normalize(heading);
            var right = new Vector2(fwd.Y, -fwd.X);

            var toFollower = followerGrid - leaderGrid;
            float currentFwd = Vector2.Dot(toFollower, fwd);
            float currentRight = Vector2.Dot(toFollower, right);

            var (minR, maxR, minF, maxF) = GetPositionBounds(pos, followDist);

            // For pinpoint positions (min == max), allow a deadzone bubble based on follow distance
            if (minR == maxR && minF == maxF)
            {
                float tolerance = Math.Clamp(followDist * 0.35f, 2f, 6f);
                return Math.Abs(currentRight - minR) <= tolerance && Math.Abs(currentFwd - minF) <= tolerance;
            }

            // For zone positions (Half / Wing / Quad), check if follower is inside the bounding box
            return currentRight >= (minR - 2f) && currentRight <= (maxR + 2f) &&
                   currentFwd >= (minF - 2f) && currentFwd <= (maxF + 2f);
        }

        public static Vector2 GetFormationTarget(Vector2 leaderGrid, Vector2 heading, CoopFollowerPosition pos, float dist)
        {
            float d = Math.Max(4f, dist);

            // In PoE Grid:
            // Screen Up: (-d * 0.7f, -d * 0.7f)
            // Screen Down (Back / Underneath): (+d * 0.7f, +d * 0.7f)
            // Screen Left: (-d * 0.7f, +d * 0.7f)
            // Screen Right: (+d * 0.7f, -d * 0.7f)
            float s = d * 0.7071f;

            Vector2 offset = pos switch
            {
                CoopFollowerPosition.Back           => new Vector2(-s, -s),
                CoopFollowerPosition.Forward        => new Vector2(s, s),
                CoopFollowerPosition.BackLeft       => new Vector2(0, -d),
                CoopFollowerPosition.BackRight      => new Vector2(-d, 0),
                CoopFollowerPosition.ForwardLeft    => new Vector2(d, 0),
                CoopFollowerPosition.ForwardRight   => new Vector2(0, d),
                CoopFollowerPosition.BackHalf       => new Vector2(-s, -s),
                CoopFollowerPosition.ForwardHalf    => new Vector2(s, s),
                CoopFollowerPosition.LeftWing       => new Vector2(s, -s),
                CoopFollowerPosition.RightWing      => new Vector2(-s, s),
                CoopFollowerPosition.BackLeftQuad   => new Vector2(0, -d),
                CoopFollowerPosition.BackRightQuad  => new Vector2(-d, 0),
                _                                   => new Vector2(-s, -s),
            };

            return leaderGrid + offset;
        }

        private Entity? FindFollowerEntity(AreaInstance area, Entity leader, string followerNameFilter)
        {
            // 1. Direct Couch Co-op Player 2 from GameHelper Engine (Instant, Zero-Lag, 100% Reliable!)
            if (area.Player2 != null && area.Player2.Address != IntPtr.Zero && area.Player2.IsValid)
            {
                // If a specific name filter is configured and Player component is populated, verify name
                if (!string.IsNullOrWhiteSpace(followerNameFilter) &&
                    area.Player2.TryGetComponent<Player>(out var p2PlayerComp) &&
                    !string.IsNullOrEmpty(p2PlayerComp.Name))
                {
                    if (p2PlayerComp.Name.Equals(followerNameFilter.Trim(), StringComparison.OrdinalIgnoreCase) ||
                        p2PlayerComp.Name.Contains(followerNameFilter.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        return area.Player2;
                    }
                }
                else
                {
                    // Couch Co-op Player 2 instance verified directly
                    return area.Player2;
                }
            }

            // 2. Fallback: Search AwakeEntities (for LAN/Network party or before Player2 pointer links)
            var candidates = new List<(Entity ent, string name)>();

            foreach (var kvp in area.AwakeEntities)
            {
                var ent = kvp.Value;
                if (!ent.IsValid || ent.Address == leader.Address) continue;

                if (ent.EntityType == EntityTypes.Player || (ent.Path != null && ent.Path.StartsWith("Metadata/Characters/")))
                {
                    string pName = ent.TryGetComponent<Player>(out var pComp) ? pComp.Name : string.Empty;
                    candidates.Add((ent, pName));
                }
            }

            if (candidates.Count == 0)
            {
                return null;
            }

            // 1. If a specific follower name is configured, STRICTLY match by name!
            if (!string.IsNullOrWhiteSpace(followerNameFilter))
            {
                string target = followerNameFilter.Trim();

                // Exact match (case-insensitive)
                var exact = candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c.name) && c.name.Equals(target, StringComparison.OrdinalIgnoreCase));
                if (exact.ent != null)
                {
                    return exact.ent;
                }

                // Substring / partial match
                var partial = candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c.name) && c.name.Contains(target, StringComparison.OrdinalIgnoreCase));
                if (partial.ent != null)
                {
                    return partial.ent;
                }

                // Follower name was NOT found in this instance!
                // DO NOT pick a stranger randomly, return null so the bot waits safely!
                return null;
            }

            // 2. If NO name filter is configured:
            // Only latch onto the other player if there is exactly 1 other player in the zone (private room / map).
            if (candidates.Count == 1)
            {
                return candidates[0].ent;
            }

            // If there are multiple other players (e.g. crowded hideout or town) and NO name is configured,
            // DO NOT guess a stranger randomly because it will break the bot!
            return null;
        }

        public void Render(BotContext ctx)
        {
            if (ctx.World == null || !this.lastKnownLeaderGrid.HasValue || !this.lastKnownFollowerGrid.HasValue)
            {
                return;
            }

            var draw = ImGui.GetForegroundDrawList();
            float convertor = ctx.Area.WorldToGridConvertor;

            var lGrid = this.lastKnownLeaderGrid.Value;
            var fGrid = this.lastKnownFollowerGrid.Value;

            float lZ = Pathfinding.GetTerrainHeight(ctx.Area.GridHeightData, (int)lGrid.X, (int)lGrid.Y, 0f);
            float fZ = Pathfinding.GetTerrainHeight(ctx.Area.GridHeightData, (int)fGrid.X, (int)fGrid.Y, 0f);

            var sLeader = ctx.World.WorldToScreen(new Vector2(lGrid.X * convertor, lGrid.Y * convertor), lZ);
            var sFollower = ctx.World.WorldToScreen(new Vector2(fGrid.X * convertor, fGrid.Y * convertor), fZ);

            if (sLeader != Vector2.Zero && sFollower != Vector2.Zero)
            {
                // Tether line between Leader and Follower
                draw.AddLine(sLeader, sFollower, ImGuiHelper.Color(0, 220, 255, 120), 1.5f);

                // Draw active A* path waypoints on the ground
                if (this.CurrentNavPath.Count > 0)
                {
                    var heightData = ctx.Area.GridHeightData;
                    Vector2 prevScreen = sFollower;
                    int maxWp = Math.Min(this.CurrentNavPath.Count, this.CurrentWaypointIndex + 25);
                    for (int i = this.CurrentWaypointIndex; i < maxWp; i++)
                    {
                        var wp = this.CurrentNavPath[i];
                        float wpZ = Pathfinding.GetTerrainHeight(heightData, (int)wp.X, (int)wp.Y, 0f);
                        var curScreen = ctx.World.WorldToScreen(new Vector2(wp.X * convertor, wp.Y * convertor), wpZ);
                        if (curScreen != Vector2.Zero && prevScreen != Vector2.Zero)
                        {
                            draw.AddLine(prevScreen, curScreen, ImGuiHelper.Color(0, 255, 180, 220), 2.2f);
                            draw.AddCircle(curScreen, 4f, ImGuiHelper.Color(0, 255, 180, 240), 8, 1.5f);
                        }
                        prevScreen = curScreen;
                    }
                }

                // Rings around both players
                draw.AddCircle(sLeader, 14f, ImGuiHelper.Color(0, 255, 120, 240), 16, 2.5f);
                draw.AddCircle(sFollower, 14f, ImGuiHelper.Color(0, 180, 255, 240), 16, 2.5f);

                // Labels
                draw.AddText(sLeader + new Vector2(-20, -30), ImGuiHelper.Color(0, 255, 120, 255), "👑 Leader (P1)");
                string followLabel = this.CurrentNavPath.Count > 0
                    ? $"🤖 Follower (P2) [A* wp {this.CurrentWaypointIndex}/{this.CurrentNavPath.Count}]"
                    : "🤖 Follower (P2)";
                draw.AddText(sFollower + new Vector2(-20, -30), ImGuiHelper.Color(0, 180, 255, 255), followLabel);
            }
        }
    }

    /// <summary>
    /// Backward-compatibility alias for CoopFollowerMode.
    /// </summary>
    public sealed class FollowerMode : CoopFollowerMode
    {
    }
}
