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
    using GameHelper.RemoteObjects.Components;
    using GameHelper.RemoteObjects.States.InGameStateObjects;
    using GameHelper.Utils;
    using GameOffsets.Natives;
    using ImGuiNET;
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

        public List<Vector2> CurrentNavPath { get; private set; } = new();

        public int CurrentWaypointIndex { get; private set; } = 0;

        public Vector2? CurrentDestination { get; private set; }

        // PoE 2 Movement Animation IDs discovered from Memory:
        public const int ANIM_IDLE = 0x0;
        public const int ANIM_RUN = 0x4;
        public const int ANIM_ROLL = 0x10C;   // 268 decimal: Dodge Roll (Tap B)
        public const int ANIM_SPRINT = 0x368; // 872 decimal: Sprint (Hold B)

        private Vector2? lastKnownLeaderGrid;
        private Vector2? lastKnownFollowerGrid;
        private bool isHoldingFormation = false;
        private bool isFollowerSprinting = false;
        private int currentFollowerAnimId = 0;
        private int nearbyEnemyCount = 0;
        private DateTime lastFollowerAttackTime = DateTime.MinValue;
        private DateTime lastSprintTapTime = DateTime.MinValue;
        private DateTime lastFollowerLifeFlaskTime = DateTime.MinValue;
        private DateTime lastFollowerManaFlaskTime = DateTime.MinValue;
        private DateTime lastRepathTime = DateTime.MinValue;
        private Vector2 lastRepathLeaderPos = Vector2.Zero;
        private Vector2 leaderHeading = new Vector2(0, -1);

        public bool IsFollowerSprinting => this.isFollowerSprinting;
        public Vector2? LastKnownLeaderGrid => this.lastKnownLeaderGrid;
        public Vector2? LastKnownFollowerGrid => this.lastKnownFollowerGrid;
        public Vector2 LeaderHeading => this.leaderHeading;

        public void OnEnter(BotContext ctx)
        {
            ctx.Log("Entering Co-op FollowerMode");
            this.CurrentNavPath.Clear();
            this.CurrentWaypointIndex = 0;
            this.CurrentDestination = null;
            this.isHoldingFormation = false;
            this.lastKnownLeaderGrid = null;
            this.lastKnownFollowerGrid = null;
            this.lastRepathTime = DateTime.MinValue;
            this.lastRepathLeaderPos = Vector2.Zero;
            this.CurrentState = "Connecting Controls";
            this.CurrentAction = "Connecting Controls & Gamepads...";

            ctx.CoopGamepad.EnsureConnected(true, ctx.Settings.CoopPhysicalPadIndex, true);
        }

        public void OnExit(BotContext ctx)
        {
            ctx.Log("Exiting Co-op FollowerMode - Releasing inputs (keeping gamepads connected)");
            this.CurrentNavPath.Clear();
            this.CurrentWaypointIndex = 0;
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
                string target = !string.IsNullOrWhiteSpace(s.FollowerCharacterName) ? s.FollowerCharacterName : "Player 2";
                this.CurrentState = "Searching Follower";
                this.CurrentAction = $"Waiting for [{target}] to enter area...";
                this.CurrentNavPath.Clear();
                this.CurrentWaypointIndex = 0;
                pad.SetFollowerMovement(Vector2.Zero);
                pad.SetFollowerAim(Vector2.Zero);
                pad.SetFollowerSprint(false);
                return;
            }

            var followerGrid = new Vector2(fRender.GridPosition.X, fRender.GridPosition.Y);
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

            this.lastKnownFollowerGrid = followerGrid;

            // Read Follower Actor.Animation in real-time
            if (followerEntity.TryGetComponent<Actor>(out var fActor))
            {
                this.currentFollowerAnimId = (int)fActor.Animation;
            }
            else
            {
                this.currentFollowerAnimId = 0;
            }

            // 4. Handle Player 2 (Follower) automated flasks (only in combat zones)
            if (!isPeacefulZone)
            {
                this.TickFollowerFlasks(followerEntity, pad, s);
            }

            // 5. Follower Autonomous Combat: Target hostiles near P1 / P2 (disabled in town/hideout)
            bool didCombat = false;
            if (s.P2EnableCombat && !isPeacefulZone)
            {
                didCombat = this.TickFollowerCombat(ctx, area, followerEntity, followerGrid, leaderGrid, pad, s);
            }
            else
            {
                pad.SetFollowerAim(Vector2.Zero);
            }

            // 6. Follower Movement / Formation towards Leader (A* Pathfinding around obstacles)
            this.TickFollowerMovement(ctx, followerEntity, followerGrid, leaderGrid, didCombat, pad, s);
        }

        private void TickLeaderAssists(BotContext ctx, Entity leader, CoopVirtualGamepad pad, AutoExile2Settings s)
        {
            if (leader.TryGetComponent<Life>(out var life) && life.Health.Total > 0)
            {
                float hpPct = (float)life.Health.Current / life.Health.Total * 100f;
                float esPct = life.EnergyShield.Total > 0 ? (float)life.EnergyShield.Current / life.EnergyShield.Total * 100f : 0f;
                int maxPool = life.Health.Total + life.EnergyShield.Total;
                int curPool = life.Health.Current + life.EnergyShield.Current;
                float combinedPct = maxPool > 0 ? (float)curPool / maxPool * 100f : 100f;

                float manaPct = life.Mana.Total > 0 ? (float)life.Mana.Current / life.Mana.Total * 100f : 100f;

                // Leader Auto Life Flask
                if (s.P1AutoLifeFlask && hpPct <= s.P1LifeFlaskThresholdPercent)
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
                if (s.P1AutoManaFlask && manaPct <= s.P1ManaFlaskThresholdPercent)
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
                    var statusEffects = leader.TryGetComponent<Buffs>(out var pBuffs) ? pBuffs.StatusEffects : null;

                    foreach (var slot in s.P1Skills.Where(x => x.Enabled).OrderByDescending(x => x.Priority))
                    {
                        if ((now - slot.LastCastAt).TotalMilliseconds < slot.MinCastIntervalMs) continue;

                        if (slot.OnlyOnLowHp)
                        {
                            float evalPct = slot.VitalCondition switch
                            {
                                VitalConditionType.HpOnly => hpPct,
                                VitalConditionType.EsOnly => esPct,
                                _ => combinedPct
                            };
                            if (evalPct > slot.LowHpThresholdPercent) continue;
                        }

                        if (slot.MinManaPercent > 0 && manaPct < slot.MinManaPercent) continue;

                        if (slot.OnlyWhenBuffMissing)
                        {
                            string buffToMatch = !string.IsNullOrWhiteSpace(slot.BuffDebuffName)
                                ? slot.BuffDebuffName
                                : (!string.IsNullOrWhiteSpace(slot.AssignedSkillName) ? slot.AssignedSkillName : slot.Name);

                            if (!string.IsNullOrWhiteSpace(buffToMatch) && statusEffects != null)
                            {
                                string clean = buffToMatch.Trim().ToLowerInvariant().Replace(" ", "").Replace("_", "").Replace("-", "");
                                bool hasBuff = statusEffects.Any(kv => kv.Key.ToLowerInvariant().Replace(" ", "").Replace("_", "").Replace("-", "").Contains(clean));
                                if (hasBuff) continue;
                            }
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
                float hpPct = (float)fLife.Health.Current / fLife.Health.Total * 100f;
                float manaPct = fLife.Mana.Total > 0 ? (float)fLife.Mana.Current / fLife.Mana.Total * 100f : 100f;

                const int debounceMs = CombatSystem.FlaskDebounceMs;

                if (s.P2AutoLifeFlask && hpPct <= s.P2LifeFlaskThresholdPercent)
                {
                    bool active = s.CheckFlaskActiveEffect && CombatSystem.IsFlaskActive(follower, 0, isLife: true);
                    if (!active)
                    {
                        pad.PressFollowerFlask(true, debounceMs);
                    }
                }

                if (s.P2AutoManaFlask && manaPct <= s.P2ManaFlaskThresholdPercent)
                {
                    bool active = s.CheckFlaskActiveEffect && CombatSystem.IsFlaskActive(follower, 1, isLife: false);
                    if (!active)
                    {
                        pad.PressFollowerFlask(false, debounceMs);
                    }
                }
            }
        }

        private bool TickFollowerCombat(BotContext ctx, AreaInstance area, Entity follower, Vector2 followerGrid, Vector2 leaderGrid, CoopVirtualGamepad pad, AutoExile2Settings s)
        {
            if (s.P2Skills == null || s.P2Skills.Count == 0) return false;

            // Find best target hostile near Follower or Leader
            Entity? bestTarget = null;
            float closestDist = float.MaxValue;
            int nearbyEnemies = 0;

            foreach (var kvp in area.AwakeEntities)
            {
                var ent = kvp.Value;
                if (ent.Address == follower.Address) continue;
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

            var now = DateTime.Now;
            var buffs = follower.TryGetComponent<Buffs>(out var fBuffs) ? fBuffs.StatusEffects : null;
            bool hasLife = follower.TryGetComponent<Life>(out var fl) && fl != null;
            float fHpPct = hasLife && fl!.Health.Total > 0
                ? (float)fl.Health.Current / fl.Health.Total * 100f
                : 100f;
            float fEsPct = hasLife && fl!.EnergyShield.Total > 0
                ? (float)fl.EnergyShield.Current / fl.EnergyShield.Total * 100f
                : 0f;
            int fMaxPool = hasLife ? fl!.Health.Total + fl.EnergyShield.Total : 0;
            int fCurPool = hasLife ? fl!.Health.Current + fl.EnergyShield.Current : 0;
            float fCombinedPct = fMaxPool > 0 ? (float)fCurPool / fMaxPool * 100f : 100f;

            float fManaPct = hasLife && fl!.Mana.Total > 0
                ? (float)fl.Mana.Current / fl.Mana.Total * 100f
                : 100f;

            // Helper lambda for evaluating vital condition
            bool IsLowVital(SkillSlotConfig slotCfg)
            {
                if (!slotCfg.OnlyOnLowHp) return false;
                float evalPct = slotCfg.VitalCondition switch
                {
                    VitalConditionType.HpOnly => fHpPct,
                    VitalConditionType.EsOnly => fEsPct,
                    _ => fCombinedPct
                };
                return evalPct <= slotCfg.LowHpThresholdPercent;
            }

            // 1. Tick SelfBuffGuard skills on Follower (independent of hostiles)
            foreach (var slot in s.P2Skills.Where(x => x.Enabled && x.Role == SkillRole.SelfBuffGuard).OrderByDescending(x => x.Priority))
            {
                if ((now - slot.LastCastAt).TotalMilliseconds < slot.MinCastIntervalMs) continue;
                if (slot.OnlyOnLowHp && !IsLowVital(slot)) continue;
                if (slot.MinManaPercent > 0 && fManaPct < slot.MinManaPercent) continue;

                if (slot.OnlyWhenBuffMissing)
                {
                    string buffToMatch = !string.IsNullOrWhiteSpace(slot.BuffDebuffName)
                        ? slot.BuffDebuffName
                        : (!string.IsNullOrWhiteSpace(slot.AssignedSkillName) ? slot.AssignedSkillName : slot.Name);

                    if (!string.IsNullOrWhiteSpace(buffToMatch) && buffs != null)
                    {
                        string clean = buffToMatch.Trim().ToLowerInvariant().Replace(" ", "").Replace("_", "").Replace("-", "");
                        bool hasBuff = buffs.Any(kv => kv.Key.ToLowerInvariant().Replace(" ", "").Replace("_", "").Replace("-", "").Contains(clean));
                        if (hasBuff) continue;
                    }
                }

                slot.LastCastAt = now;
                pad.PressFollowerButton(slot.GamepadButton, Math.Max(120, slot.HoldDurationMs));

                this.CurrentState = "Follower Buff";
                this.CurrentAction = $"Casting self buff/guard: {slot.Name}";
                return true;
            }

            // 2. Culler Skills on Follower (Focused Fire ahead of Host / Cull low HP hostiles)
            float gridToWorld = area.WorldToGridConvertor > 0 ? area.WorldToGridConvertor : 10.87f;
            float distToLeader = Vector2.Distance(followerGrid, leaderGrid);
            float distToLeaderWorld = distToLeader * gridToWorld;

            // Priority System: Combat vs Following Leader
            // - If follower is actively Sprinting (0x368) or Rolling (0x10C), don't stop animation to cast spells.
            // - If follower is too far away (> 28g / s.CoopFollowDistance + 10g), prioritize catchup over attacking.
            // - Within combat zone (<= 28g), follower can attack and move simultaneously!
            bool isActuallySprinting = this.currentFollowerAnimId == ANIM_SPRINT || this.currentFollowerAnimId == ANIM_ROLL;
            bool isFarBehind = distToLeader > Math.Max(28f, s.CoopFollowDistance + 10f);
            if (isActuallySprinting || isFarBehind)
            {
                return false;
            }

            foreach (var cullerSkill in s.P2Skills.Where(x => x.Enabled && (x.Category == "Culler" || x.Role == SkillRole.Culler)).OrderByDescending(x => x.Priority))
            {
                // Enforce minimum 300ms between Culler casts to prevent input spam
                int effectiveCullerInterval = Math.Max(300, cullerSkill.MinCastIntervalMs);
                if ((now - cullerSkill.LastCastAt).TotalMilliseconds < effectiveCullerInterval) continue;
                if (cullerSkill.MinManaPercent > 0 && fManaPct < cullerSkill.MinManaPercent) continue;

                // "ให้ตีเมื่ออยู่ใกล้ๆหัว" - อิงตาม Distance from host to start attacking (หน่วย g) ที่ผู้ใช้ปรับไว้
                float rawStartDist = cullerSkill.CullerStartAttackDistance > 0 ? cullerSkill.CullerStartAttackDistance : 35f;
                // Auto-convert legacy world values (> 150) to grid
                float startDistGrid = rawStartDist > 150f ? (rawStartDist / gridToWorld) : rawStartDist;
                if (distToLeader > startDistGrid) continue; // Too far from host, let follower catch up first!

                // "ให้ตั้งได้ว่าจะ ให้ต้องมีมอนก่อนถึงจะตี หรือ ไม่ต้องมี"
                if (cullerSkill.CullerRequireMonsters && nearbyEnemies == 0) continue;

                // Let PoE 2 Controller Native Auto-Aim handle targeting automatically!
                // Do not force right-stick angles which fight or disrupt PoE 2's built-in target lock
                pad.SetFollowerAim(Vector2.Zero);

                cullerSkill.LastCastAt = now;
                this.lastFollowerAttackTime = now;
                pad.PressFollowerButton(cullerSkill.GamepadButton, Math.Max(120, cullerSkill.HoldDurationMs));

                this.CurrentState = "Culler Attack";
                this.CurrentAction = $"Culling ahead of Host ({distToLeader:F0}/{startDistGrid:F0}g, {nearbyEnemies} hostiles)";
                return true;
            }

            // 3. Regular Targeted Skills on Follower
            if (bestTarget != null && bestTarget.TryGetComponent<Render>(out var targetRender))
            {
                // Rely 100% on PoE 2 native Gamepad Auto-Targeting
                pad.SetFollowerAim(Vector2.Zero);

                Rarity targetRarity = Rarity.Normal;
                if (bestTarget.TryGetComponent<ObjectMagicProperties>(out var omp))
                {
                    targetRarity = omp.Rarity;
                }

                foreach (var skill in s.P2Skills.Where(x => x.Enabled && x.Role != SkillRole.SelfBuffGuard && x.Role != SkillRole.Culler && x.Category != "Culler" && x.Role != SkillRole.Disabled).OrderByDescending(x => x.Priority))
                {
                    if ((now - skill.LastCastAt).TotalMilliseconds < skill.MinCastIntervalMs) continue;
                    if (skill.OnlyOnLowHp && !IsLowVital(skill)) continue;
                    if (skill.MinManaPercent > 0 && fManaPct < skill.MinManaPercent) continue;

                    // Filter by target rarity
                    if (skill.TargetFilter == SkillTargetFilter.NormalOnly && targetRarity != Rarity.Normal) continue;
                    if (skill.TargetFilter == SkillTargetFilter.MagicOrAbove && targetRarity < Rarity.Magic) continue;
                    if (skill.TargetFilter == SkillTargetFilter.RareOrAbove && targetRarity < Rarity.Rare) continue;
                    if (skill.TargetFilter == SkillTargetFilter.UniqueOnly && targetRarity != Rarity.Unique) continue;

                    if (skill.MinNearbyEnemies > 0 && nearbyEnemies < skill.MinNearbyEnemies) continue;
                    float effectiveRange = skill.MaxTargetRange > 0 ? skill.MaxTargetRange : 75f;
                    if (closestDist > effectiveRange) continue;

                    if (skill.OnlyWhenBuffMissing && !string.IsNullOrWhiteSpace(skill.BuffDebuffName))
                    {
                        bool hasBuff = false;
                        if (buffs != null)
                        {
                            string clean = skill.BuffDebuffName.Trim().ToLowerInvariant().Replace(" ", "").Replace("_", "").Replace("-", "");
                            hasBuff = buffs.Any(kv => kv.Key.ToLowerInvariant().Replace(" ", "").Replace("_", "").Replace("-", "").Contains(clean));
                        }
                        if (hasBuff) continue;
                    }

                    // Execute skill on Follower (Virtual Gamepad #2)
                    skill.LastCastAt = now;
                    this.lastFollowerAttackTime = now;
                    pad.PressFollowerButton(skill.GamepadButton, Math.Max(120, skill.HoldDurationMs));

                    this.CurrentState = "Follower Combat";
                    this.CurrentAction = $"Attacking hostile with {skill.Name} ({nearbyEnemies} hostiles)";
                    return true;
                }
            }
            else
            {
                // No hostiles: center aim stick
                pad.SetFollowerAim(Vector2.Zero);
            }

            return false;
        }

        private void TickFollowerMovement(BotContext ctx, Entity follower, Vector2 followerGrid, Vector2 leaderGrid, bool didCombat, CoopVirtualGamepad pad, AutoExile2Settings s)
        {
            // 0. Manual Player Keyboard Override (Arrow Keys)
            if (pad.IsFollowerManualMoving)
            {
                this.CurrentState = "Manual Control (P2)";
                this.CurrentAction = "Player is steering Character 2 via Keyboard Arrow Keys";
                this.CurrentNavPath.Clear();
                this.CurrentWaypointIndex = 0;
                this.CurrentDestination = null;
                pad.SetFollowerSprint(false);
                return;
            }

            float distToLeader = Vector2.Distance(followerGrid, leaderGrid);
            float followDist = s.CoopFollowDistance;
            float stopDist = s.CoopStopDistance;
            float sprintDist = s.CoopSprintDistance;

            var walkableData = ctx.Area.GridWalkableData;
            int bytesPerRow = ctx.Area.TerrainMetadata.BytesPerRow;
            int rows = walkableData != null && bytesPerRow > 0 ? walkableData.Length / bytesPerRow : 0;
            int cols = bytesPerRow * 2;

            // Safe Distance vs Follow Distance Hysteresis System:
            // 1. safeDist (CoopStopDistance): The close target distance where the follower stops moving.
            // 2. followTriggerDist (CoopFollowDistance): The outer boundary where follower starts moving to catch up.
            // When in between (safeDist < distToLeader <= followTriggerDist), the follower stays peacefully in place,
            // free to attack hostiles, cast culler/auras, without jerky micro-movements!
            float safeDist = Math.Max(2f, s.CoopStopDistance);
            float followTriggerDist = Math.Max(safeDist + 1f, s.CoopFollowDistance);

            // Fixed formation target positioned at safeDist from leader
            Vector2 fixedHeading = new Vector2(0, -1);
            Vector2 formationTarget = GetFormationTarget(leaderGrid, fixedHeading, s.FollowerPosition, safeDist);
            if (walkableData != null && bytesPerRow > 0 && !Pathfinding.IsWalkable(walkableData, bytesPerRow, (int)formationTarget.X, (int)formationTarget.Y))
            {
                formationTarget = leaderGrid;
            }

            float distToTarget = Vector2.Distance(followerGrid, formationTarget);

            // Condition A: Follower has reached or is inside the Safe Distance
            if (distToLeader <= safeDist)
            {
                this.isHoldingFormation = true;
                this.isFollowerSprinting = false;
                this.CurrentNavPath.Clear();
                this.CurrentWaypointIndex = 0;
                this.CurrentDestination = null;
                pad.SetFollowerMovement(Vector2.Zero);
                pad.SetFollowerSprint(false);

                if (!didCombat)
                {
                    this.CurrentState = "In Safe Zone";
                    this.CurrentAction = $"Safe Distance held ({distToLeader:F0}g from Leader, safe <= {safeDist:F0}g)";
                }
                return;
            }

            // Condition B: Follower was stopped, and leader is still within the Safe Zone buffer (<= followTriggerDist)
            if (this.isHoldingFormation && distToLeader <= followTriggerDist)
            {
                this.isFollowerSprinting = false;
                pad.SetFollowerMovement(Vector2.Zero);
                pad.SetFollowerSprint(false);

                if (!didCombat)
                {
                    this.CurrentState = "In Safe Zone";
                    this.CurrentAction = $"Safe Zone ({distToLeader:F0}g from Leader, triggers follow at > {followTriggerDist:F0}g)";
                }
                return;
            }

            // Condition C: Outside Safe Zone (distToLeader > followTriggerDist) -> start moving until inside safeDist!
            this.isHoldingFormation = false;

            // 2. Needs to move towards Formation Target
            var now = DateTime.Now;

            // Check direct Line-of-Sight between Follower and Formation Target
            bool hasLos = Pathfinding.HasLineOfSight(walkableData, bytesPerRow, followerGrid, formationTarget, rows, cols, 3);

            Vector2 targetGridPos;

            if (hasLos && distToTarget <= followDist + 15f)
            {
                // Open terrain with Line-of-Sight: Direct steering towards formation target
                this.CurrentNavPath.Clear();
                this.CurrentWaypointIndex = 0;
                this.CurrentDestination = formationTarget;
                targetGridPos = formationTarget;
            }
            else
            {
                // Wall/corner/pillar or distance: Use A* Pathfinding around obstacles!
                bool needRepath = false;
                double timeSinceRepath = (now - this.lastRepathTime).TotalMilliseconds;

                if (this.CurrentNavPath.Count == 0 || this.CurrentWaypointIndex >= this.CurrentNavPath.Count)
                {
                    needRepath = timeSinceRepath > 250;
                }
                else if (Vector2.Distance(this.lastRepathLeaderPos, formationTarget) > 15f)
                {
                    needRepath = timeSinceRepath > 350;
                }

                if (needRepath && walkableData != null && bytesPerRow > 0)
                {
                    var path = Pathfinding.FindPath(walkableData, bytesPerRow, followerGrid, formationTarget);
                    this.lastRepathTime = now;
                    this.lastRepathLeaderPos = formationTarget;
                    this.CurrentNavPath.Clear();
                    this.CurrentWaypointIndex = 0;

                    if (path != null && path.Count > 0)
                    {
                        this.CurrentNavPath.AddRange(path);
                        this.CurrentDestination = formationTarget;
                    }
                }

                // Follow next waypoint along the A* path
                if (this.CurrentNavPath.Count > 0 && this.CurrentWaypointIndex < this.CurrentNavPath.Count)
                {
                    var wp = this.CurrentNavPath[this.CurrentWaypointIndex];
                    float distToWp = Vector2.Distance(followerGrid, wp);

                    // Advance to next waypoint once within 10g
                    if (distToWp <= 10f)
                    {
                        this.CurrentWaypointIndex++;
                        if (this.CurrentWaypointIndex < this.CurrentNavPath.Count)
                        {
                            wp = this.CurrentNavPath[this.CurrentWaypointIndex];
                        }
                        else
                        {
                            wp = formationTarget;
                        }
                    }

                    targetGridPos = wp;
                }
                else
                {
                    // Fallback if pathfinding returned empty
                    targetGridPos = formationTarget;
                }
            }

            // Sprint System with Hardware Memory Animation Verification (0x368 = Sprint, 0x10C = Roll):
            bool isActuallySprintingAnim = this.currentFollowerAnimId == ANIM_SPRINT;
            bool isRollingAnim = this.currentFollowerAnimId == ANIM_ROLL;

            // "ห้าม Sprint เวลามีมอน แต่ให้กลิ้งเข้าไปได้":
            // When monsters are nearby, DO NOT sprint (hold B).
            // Instead, if follower has fallen behind (> followDist + 8f), execute Dodge Roll (tap B) towards leader!
            bool hasNearbyMonsters = this.nearbyEnemyCount > 0;

            bool shouldSprint;
            if (hasNearbyMonsters)
            {
                shouldSprint = false;

                // Dodge Roll (Tap B) catchup when falling behind amidst monsters:
                double msSinceRoll = (now - this.lastSprintTapTime).TotalMilliseconds;
                if (distToLeader > (followDist + 8f) && msSinceRoll > 1200 && !isRollingAnim)
                {
                    this.lastSprintTapTime = now;
                    pad.PressFollowerButton(CoopPadButton.B, 200); // Tap B for Dodge Roll (200ms)
                }
            }
            else if (this.isFollowerSprinting || isActuallySprintingAnim)
            {
                // Once sprinting, maintain sprint continuously until follower reaches the safe stop distance
                shouldSprint = distToLeader > safeDist;
            }
            else
            {
                // Start sprint when far behind or leader sprints
                shouldSprint = (distToTarget >= sprintDist || pad.IsLeaderSprinting) && distToLeader > safeDist;
            }

            // If actively casting/attacking, halt sprint only when close to leader
            double msSinceAttack = (now - this.lastFollowerAttackTime).TotalMilliseconds;
            bool isCastingNow = didCombat || msSinceAttack < 250;
            if (isCastingNow && distToLeader <= (followDist + 4f))
            {
                shouldSprint = false;
            }

            this.isFollowerSprinting = shouldSprint;

            {
                // Execute movement steering towards targetGridPos (converted to screen space for gamepad)
                var moveDir = BotInput.GridToScreenDirection(ctx.World, follower, targetGridPos, followerGrid, ctx.Area.WorldToGridConvertor);

                // PoE characters move at full fixed movement speed; always push stick at 100% (1.0f)
                pad.SetFollowerMovement(moveDir);
                pad.SetFollowerSprint(shouldSprint);
            }

            if (!didCombat)
            {
                string sprintBadge = isActuallySprintingAnim ? " [SPRINTING (0x368)]" : (shouldSprint ? " [ENGAGING SPRINT]" : "");
                this.CurrentState = isActuallySprintingAnim || shouldSprint ? "Sprinting (Catchup)" : "Following";
                string navType = hasLos ? "Direct" : $"A* (wp {this.CurrentWaypointIndex}/{this.CurrentNavPath.Count})";
                this.CurrentAction = $"Moving to {s.FollowerPosition} ({distToLeader:F0}g away){sprintBadge} [{navType}]";
            }
            else
            {
                this.CurrentAction += $" | Moving to {s.FollowerPosition}";
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
            // In couch co-op on a single PC or party:
            // Area.Player is Player 1 (Leader).
            // AwakeEntities contains Player 2 (Follower) and any other players in the instance.
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
