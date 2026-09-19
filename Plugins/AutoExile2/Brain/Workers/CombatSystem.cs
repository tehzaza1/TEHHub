// <copyright file="CombatSystem.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Systems
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Numerics;
    using TEHhub;
    using TEHhub.RemoteEnums;
    using TEHhub.RemoteEnums.Entity;
    using TEHhub.RemoteObjects.Components;
    using TEHhub.RemoteObjects.States.InGameStateObjects;
    using TEHhub.Offsets.Natives;
    using TEHhub.Offsets.Objects.Components;
    using ClickableTransparentOverlay.Win32;
    using AutoExile2.WebServer;

    /// <summary>
    /// Handles hostile monster scanning, smart target scoring, Auto Flasks,
    /// and multi-skill execution based on individual skill slot configurations.
    /// Ported and adapted from AutoExile 1's combat engine.
    /// </summary>
    public class CombatSystem
    {
        /// <summary>
        /// Hardcoded debounce cooldown for life and mana flasks in milliseconds (200ms).
        /// Prevents duplicate key triggers within the same server tick while effect/charges are evaluated.
        /// </summary>
        public const int FlaskDebounceMs = 200;

        /// <summary>
        /// Sanitizes user-configured flask cooldowns while preserving a small anti-spam floor.
        /// </summary>
        public static int NormalizeFlaskCooldownMs(int configuredMs) => Math.Clamp(configuredMs, 50, 10000);

        private const int InputReleaseMarginMs = 20;

        /// <summary>
        /// Minimum time between starts of the same configured skill.
        /// User MinCastIntervalMs is always honored; charge availability never shortens it.
        /// The hold duration only becomes the floor when it is longer than the configured interval.
        /// </summary>
        internal static int GetSkillCastSpacingMs(SkillSlotConfig slot)
        {
            int configuredInterval = Math.Max(0, slot.MinCastIntervalMs);
            int inputHoldFloor = Math.Max(0, slot.HoldDurationMs) + InputReleaseMarginMs;
            return Math.Max(configuredInterval, inputHoldFloor);
        }

        /// <summary>
        /// A short keyboard tap completes synchronously in BotInput, so offense can resume in
        /// the same combat tick without overlapping a held defensive input.
        /// </summary>
        private static bool CanSelfSkillOverlapOffense(SkillSlotConfig slot)
        {
            return !slot.IsChannel &&
                   slot.InputType == AttackInputType.KeyboardKey &&
                   slot.HoldDurationMs <= 30;
        }

        private DateTime nextAttackAllowed = DateTime.MinValue;
        private readonly object defensiveInputGate = new();
        private DateTime defensiveInputBusyUntil = DateTime.MinValue;
        private int defensiveInputPriority = int.MinValue;
        private bool defensiveInputIsEmergency;
        public DateTime LastLifeFlaskAt { get; set; } = DateTime.MinValue;
        public DateTime LastManaFlaskAt { get; set; } = DateTime.MinValue;

        // Target focus tracking (AutoExile 1 target timeout prevention)
        private uint lastTargetId = 0;
        private DateTime targetFocusStart = DateTime.MinValue;
        private readonly HashSet<uint> deprioritizedTargets = new();
        private const float TargetFocusTimeoutSec = 5.0f;

        // Active channeling slot
        private SkillSlotConfig? activeChannelSlot = null;

        /// <summary>Currently targeted monster entity ID (0 if none).</summary>
        public uint CurrentTargetId { get; private set; }

        /// <summary>Currently targeted monster name or clean path.</summary>
        public string CurrentTargetName { get; private set; } = string.Empty;

        /// <summary>Currently targeted monster rarity.</summary>
        public string CurrentTargetRarity { get; private set; } = string.Empty;

        /// <summary>Number of hostile monsters in combat range.</summary>
        public int NearbyHostileCount { get; private set; }

        /// <summary>Weighted density of hostile monsters in combat range (Normal=1, Magic=2, Rare/Unique=3).</summary>
        public int WeightedDensity { get; private set; }

        /// <summary>Center of mass of nearby monster pack in grid coordinates.</summary>
        public Vector2 PackCenter { get; private set; } = Vector2.Zero;

        /// <summary>Distance to closest alive hostile monster in awareness range (or float.MaxValue if none).</summary>
        public float ClosestHostileDistance { get; private set; } = float.MaxValue;

        /// <summary>Current active skill action description for overlay/telemetry.</summary>
        public string LastSkillAction { get; private set; } = "Idle";

        /// <summary>Live debuffs observed on current or nearby hostile targets.</summary>
        public List<ActiveBuffInfo> ObservedTargetDebuffs { get; } = new();

        /// <summary>
        /// Stops all combat/skill inputs, including an in-flight defensive hold.
        /// Intended for lifecycle STOP/pause and hard invalid-state cleanup.
        /// </summary>
        public void StopAllChannels()
        {
            lock (this.defensiveInputGate)
            {
                if (this.activeChannelSlot != null)
                {
                    BotInput.StopChannel(this.activeChannelSlot.InputType, this.activeChannelSlot.Key);
                    this.activeChannelSlot = null;
                }

                BotInput.ReleaseAllAttackInputs();
                this.defensiveInputBusyUntil = DateTime.MinValue;
                this.defensiveInputPriority = int.MinValue;
                this.defensiveInputIsEmergency = false;
                this.nextAttackAllowed = DateTime.MinValue;
            }
        }

        private void StopOffensiveInputs()
        {
            if (this.activeChannelSlot != null)
            {
                BotInput.StopChannel(this.activeChannelSlot.InputType, this.activeChannelSlot.Key);
                this.activeChannelSlot = null;
            }

            BotInput.ReleaseOffensiveAttackInputs();
        }

        private void ClearCombatSnapshot()
        {
            this.CurrentTargetId = 0;
            this.CurrentTargetName = string.Empty;
            this.CurrentTargetRarity = string.Empty;
            this.NearbyHostileCount = 0;
            this.WeightedDensity = 0;
            this.PackCenter = Vector2.Zero;
            this.ClosestHostileDistance = float.MaxValue;
            this.ObservedTargetDebuffs.Clear();
            this.LastSkillAction = "Idle";
            this.lastTargetId = 0;
            this.targetFocusStart = DateTime.MinValue;
            this.deprioritizedTargets.Clear();
        }

        /// <summary>
        /// Updates WASD movement during combat based on CombatStyle (Melee vs Ranged) and FightRange.
        /// Melee: Closes in to FightRange, then holds ground to attack.
        /// Ranged: Kites/orbits around the monster pack at FightRange, avoiding swarms.
        /// </summary>
        public void UpdateCombatMovement(
            WorldData world,
            Entity player,
            AreaInstance area,
            AutoExile2Settings settings)
        {
            if (player == null || area == null || world == null || this.NearbyHostileCount == 0 || this.PackCenter == Vector2.Zero)
            {
                BotInput.ReleaseAllMovementKeys(settings);
                return;
            }

            if (!player.TryGetComponent<Render>(out var pRender))
            {
                BotInput.ReleaseAllMovementKeys(settings);
                return;
            }

            var playerGrid = new Vector2(pRender.GridPosition.X, pRender.GridPosition.Y);
            float dist = Vector2.Distance(playerGrid, this.PackCenter);
            float fightRange = Math.Max(10f, settings.FightRange);

            Vector2 desiredMoveDir = Vector2.Zero;

            if (settings.CombatStyle == CombatStyle.Melee)
            {
                // Melee: Close in until within fightRange
                if (dist > fightRange)
                {
                    desiredMoveDir = this.PackCenter - playerGrid;
                }
                else
                {
                    // In melee range: stop moving and focus on attacking
                    BotInput.ReleaseAllMovementKeys(settings);
                    return;
                }
            }
            else // Ranged
            {
                if (dist > fightRange * 1.35f)
                {
                    // Too far from combat: close in towards pack
                    desiredMoveDir = this.PackCenter - playerGrid;
                }
                else if (dist < fightRange * 0.75f)
                {
                    // Too close to monsters: back up / kite away from pack
                    desiredMoveDir = playerGrid - this.PackCenter;
                }
                else
                {
                    // In the ideal fightRange zone: orbit perpendicular to pack
                    var toPack = this.PackCenter - playerGrid;
                    var perp = new Vector2(-toPack.Y, toPack.X); // 90 degree tangent
                    desiredMoveDir = perp;
                }
            }

            if (desiredMoveDir.LengthSquared() > 0.001f)
            {
                desiredMoveDir = Vector2.Normalize(desiredMoveDir);
                var targetGridPos = playerGrid + (desiredMoveDir * 15f);
                var screenDir = BotInput.GridToScreenDirection(
                    world,
                    player,
                    targetGridPos,
                    playerGrid,
                    area.WorldToGridConvertor);

                BotInput.WasdMove(screenDir, settings);
            }
            else
            {
                BotInput.ReleaseAllMovementKeys(settings);
            }
        }

        /// <summary>
        /// Scans for hostiles, runs Auto Flasks, evaluates skills by priority, and executes attacks.
        /// Returns true if combat is active (monsters engaged or attacks executed).
        /// </summary>
        public bool TickCombat(
            AreaInstance area,
            WorldData world,
            Entity player,
            AutoExile2Settings settings)
        {
            if (player == null || area == null || world == null)
            {
                this.StopAllChannels();
                this.ClearCombatSnapshot();
                return false;
            }

            if (!player.TryGetComponent<Render>(out var pRender))
            {
                this.StopAllChannels();
                this.ClearCombatSnapshot();
                return false;
            }

            // 1. Player Vitals (HP, ES, Combined, and Mana percentages)
            // A transient Life-component miss must be treated as unknown/healthy, not 0% HP/Mana.
            PlayerVitals vitals = player.TryGetComponent<Life>(out var pLife)
                ? new PlayerVitals(pLife)
                : new PlayerVitals(null);

            // 2. Auto Flasks (AutoExile 1 automated recovery)
            this.TickAutoFlasks(settings, vitals.HpPercent, vitals.ManaPercent);

            // 3. Scan Threats & Select Best Target
            var playerGrid = new Vector2(pRender.GridPosition.X, pRender.GridPosition.Y);
            Entity? bestTarget = null;
            float bestScore = float.MinValue;
            float closestDist = float.MaxValue;
            int hostileCount = 0;
            int weightedDensity = 0;
            Vector2 hostileGridSum = Vector2.Zero;
            Rarity bestTargetRarity = Rarity.Normal;

            foreach (var entity in area.AwakeEntities.Values)
            {
                if (!IsHostileMonster(entity, player.Address))
                {
                    continue;
                }

                if (!entity.TryGetComponent<Render>(out var render))
                {
                    continue;
                }

                var entityGrid = new Vector2(render.GridPosition.X, render.GridPosition.Y);
                float dist = Vector2.Distance(playerGrid, entityGrid);

                if (dist < closestDist)
                {
                    closestDist = dist;
                }

                if (dist <= settings.CombatRange)
                {
                    hostileCount++;
                    hostileGridSum += entityGrid;

                    Rarity rarity = Rarity.Normal;
                    if (entity.TryGetComponent<ObjectMagicProperties>(out var omp))
                    {
                        rarity = omp.Rarity;
                    }

                    // User Rule: normal = 1, magic = 2, rare / unique = 3
                    int packWeight = rarity switch
                    {
                        Rarity.Magic => 2,
                        >= Rarity.Rare => 3,
                        _ => 1,
                    };
                    weightedDensity += packWeight;

                    // AutoExile 1 Rarity Weighting for Targeting: Normal=1, Magic=3, Rare=10, Unique=25
                    float rarityWeight = rarity switch
                    {
                        Rarity.Magic => 3.0f,
                        Rarity.Rare => 10.0f,
                        Rarity.Unique => 25.0f,
                        _ => 1.0f,
                    };

                    float score = rarityWeight - (dist * 0.1f);

                    // Boss / Unique Monster Priority: Always prioritize bosses over trash mobs
                    string mPath = entity.Path ?? string.Empty;
                    if (rarity == Rarity.Unique || mPath.Contains("Boss", StringComparison.OrdinalIgnoreCase))
                    {
                        score += 500f;
                    }

                    // Target Focus Timeout Penalty (-200 if stuck on this target > 5s)
                    if (this.deprioritizedTargets.Contains(entity.Id))
                    {
                        score -= 200f;
                    }

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestTarget = entity;
                        bestTargetRarity = rarity;
                    }
                }
            }

            this.NearbyHostileCount = hostileCount;
            this.WeightedDensity = weightedDensity;
            this.ClosestHostileDistance = closestDist;
            this.PackCenter = hostileCount > 0 ? (hostileGridSum / hostileCount) : playerGrid;

            // Target focus duration tracker
            if (bestTarget != null)
            {
                if (bestTarget.Id == this.lastTargetId)
                {
                    if ((DateTime.Now - this.targetFocusStart).TotalSeconds > TargetFocusTimeoutSec)
                    {
                        this.deprioritizedTargets.Add(bestTarget.Id);
                    }
                }
                else
                {
                    this.lastTargetId = bestTarget.Id;
                    this.targetFocusStart = DateTime.Now;
                }
            }
            else
            {
                this.lastTargetId = 0;
                this.deprioritizedTargets.Clear();
            }

            // 4. Defensive/self lane always evaluates before offense.
            // Short keyboard taps may share the tick; held/channel inputs reserve the offensive lane.
            SkillSlotConfig? executedSelfSkill = this.TryTickSelfSkills(player, settings, vitals, hostileCount);
            bool selfSkillBlocksOffense =
                executedSelfSkill != null && !CanSelfSkillOverlapOffense(executedSelfSkill);

            if (bestTarget == null)
            {
                this.CurrentTargetId = 0;
                this.CurrentTargetName = string.Empty;
                this.CurrentTargetRarity = string.Empty;
                this.PackCenter = Vector2.Zero;
                this.ObservedTargetDebuffs.Clear();

                // The defensive scheduler already stopped offense before a self skill fired.
                // Do not immediately cancel that new defensive hold just because no monster target exists.
                if (executedSelfSkill == null)
                {
                    this.StopOffensiveInputs();
                    this.LastSkillAction = "Idle";
                }

                return executedSelfSkill != null;
            }

            this.CurrentTargetId = bestTarget.Id;
            this.CurrentTargetRarity = bestTargetRarity.ToString();

            // Extract readable target monster name
            string targetName = string.Empty;
            if (bestTarget.Path != null)
            {
                int lastSlash = bestTarget.Path.LastIndexOf('/');
                targetName = lastSlash >= 0 ? bestTarget.Path.Substring(lastSlash + 1) : bestTarget.Path;
                int atSign = targetName.IndexOf('@');
                if (atSign >= 0) targetName = targetName.Substring(0, atSign);
            }
            this.CurrentTargetName = targetName;

            // Live scan debuffs on the targeted monster.
            // Clear first so a target without Buffs cannot leave stale telemetry from the previous target.
            this.ObservedTargetDebuffs.Clear();
            if (bestTarget.TryGetComponent<Buffs>(out var tBuffs) && tBuffs.FastStatusEffects != null)
            {
                foreach (var (debuffName, eff) in tBuffs.FastStatusEffects)
                {
                    if (string.IsNullOrWhiteSpace(debuffName)) continue;
                    this.ObservedTargetDebuffs.Add(new ActiveBuffInfo
                    {
                        Name = debuffName,
                        TimeLeft = float.IsInfinity(eff.TimeLeft) ? 0f : (float)Math.Round(eff.TimeLeft, 1),
                        Charges = eff.Charges,
                    });
                }
            }

            if (selfSkillBlocksOffense)
            {
                return true;
            }

            // 5. Evaluate and Execute Targeted Skills by Priority
            return this.TickTargetedSkills(
                area,
                world,
                player,
                pRender,
                playerGrid,
                bestTarget,
                bestTargetRarity,
                hostileCount,
                vitals,
                settings);
        }

        /// <summary>
        /// Determines 0-indexed flask slot (0 to 4) from virtual key.
        /// </summary>
        public static int GetFlaskSlotFromKey(VK key, int defaultSlot)
        {
            return key switch
            {
                VK.KEY_1 => 0,
                VK.KEY_2 => 1,
                VK.KEY_3 => 2,
                VK.KEY_4 => 3,
                VK.KEY_5 => 4,
                _ => defaultSlot,
            };
        }

        /// <summary>
        /// Checks whether the flask in the given slot is currently active on the player.
        /// Uses TEHhub's parsed Buffs.FlaskActive array (identical to AutoHotKeyTrigger's FlaskInfo.Active)
        /// and status effects scan to prevent drinking while the effect is running.
        /// </summary>
        public static bool IsFlaskActive(Entity? player, int slot, bool isLife)
        {
            if (player == null || !player.IsValid)
            {
                return false;
            }

            if (player.TryGetComponent<Buffs>(out var buffs))
            {
                // 1. Direct slot check from TEHhub's parsed FlaskActive array
                if (slot >= 0 && slot < buffs.FlaskActive.Length && buffs.FlaskActive[slot])
                {
                    return true;
                }

                // 2. Fallback scan on active status effects
                if (buffs.FastStatusEffects != null && buffs.FastStatusEffects.Count > 0)
                {
                    string target = isLife ? "life" : "mana";
                    foreach (var kvp in buffs.FastStatusEffects)
                    {
                        if (kvp.Value.FlaskSlot == slot)
                        {
                            return true;
                        }

                        string name = kvp.Key.ToLowerInvariant();
                        if (name.Contains("flask") && name.Contains(target))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Checks whether the flask item in the specified inventory slot has enough charges to use.
        /// Directly mirrors AutoHotKeyTrigger's FlaskInfo.IsUsable implementation.
        /// </summary>
        public static bool HasFlaskCharges(ServerData? serverData, int slot)
        {
            if (serverData == null || slot < 0 || slot >= 5)
            {
                return true;
            }

            try
            {
                var flaskItem = serverData.FlaskInventory[0, slot];
                if (flaskItem == null || flaskItem.Address == IntPtr.Zero)
                {
                    return true; // Empty slot or not loaded yet, allow trigger
                }

                if (flaskItem.TryGetComponent<Charges>(out var chargesComp))
                {
                    if (chargesComp.PerUseCharge > 0)
                    {
                        return chargesComp.Current >= chargesComp.PerUseCharge;
                    }

                    return chargesComp.Current > 0;
                }
            }
            catch
            {
                // Fallback to true if read fails
            }

            return true;
        }

        public void TickAutoFlasks(AutoExile2Settings settings, float hpPercent, float manaPercent, CoopVirtualGamepad? pad = null)
        {
            var inGameState = TEHhub.Core.States.InGameStateObject;
            var area = inGameState?.CurrentAreaInstance;
            this.TickAutoFlasks(area?.Player, area?.ServerDataObject, settings, hpPercent, manaPercent, pad);
        }

        public void TickAutoFlasks(
            Entity? player,
            ServerData? serverData,
            AutoExile2Settings settings,
            float hpPercent,
            float manaPercent,
            CoopVirtualGamepad? pad = null)
        {
            var now = DateTime.Now;

            // 1. Auto Life Flask
            if (settings.AutoLifeFlask && hpPercent <= settings.LifeFlaskThresholdPercent)
            {
                int lifeSlot = GetFlaskSlotFromKey(settings.LifeFlaskKey, 0);

                // Check active effect: do NOT drink if flask effect is already active!
                bool active = settings.CheckFlaskActiveEffect && IsFlaskActive(player, lifeSlot, isLife: true);

                // Check charges: do NOT drink if not enough charges!
                bool hasCharges = !settings.CheckFlaskCharges || HasFlaskCharges(serverData, lifeSlot);

                if (!active && hasCharges)
                {
                    int debounceMs = NormalizeFlaskCooldownMs(settings.LifeFlaskCooldownMs);

                    if ((now - this.LastLifeFlaskAt).TotalMilliseconds >= debounceMs)
                    {
                        this.LastLifeFlaskAt = now;
                        if (pad != null && pad.IsLeaderConnected)
                        {
                            pad.PressLeaderFlask(true, debounceMs);
                        }
                        else
                        {
                            BotInput.FastPressKey(settings.LifeFlaskKey);
                        }
                    }
                }
            }

            // 2. Auto Mana Flask
            if (settings.AutoManaFlask && manaPercent <= settings.ManaFlaskThresholdPercent)
            {
                int manaSlot = GetFlaskSlotFromKey(settings.ManaFlaskKey, 1);

                // Check active effect: do NOT drink if flask effect is already active!
                bool active = settings.CheckFlaskActiveEffect && IsFlaskActive(player, manaSlot, isLife: false);

                // Check charges: do NOT drink if not enough charges!
                bool hasCharges = !settings.CheckFlaskCharges || HasFlaskCharges(serverData, manaSlot);

                if (!active && hasCharges)
                {
                    int debounceMs = NormalizeFlaskCooldownMs(settings.ManaFlaskCooldownMs);

                    if ((now - this.LastManaFlaskAt).TotalMilliseconds >= debounceMs)
                    {
                        this.LastManaFlaskAt = now;
                        if (pad != null && pad.IsLeaderConnected)
                        {
                            pad.PressLeaderFlask(false, debounceMs);
                        }
                        else
                        {
                            BotInput.FastPressKey(settings.ManaFlaskKey);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Instantly checks and fires emergency low-HP guard / panic defense skills (e.g. Steelskin, Molten Shell)
        /// with zero delay, directly mirroring AutoHotKeyTrigger's emergency rule triggers.
        /// </summary>
        public bool TickEmergencyLowHpSkills(
            Entity player,
            AutoExile2Settings settings,
            PlayerVitals vitals,
            CoopVirtualGamepad? pad = null,
            AreaInstance? area = null)
        {
            if (settings.Skills == null || settings.Skills.Count == 0)
            {
                return false;
            }

            var now = DateTime.Now;
            foreach (var slot in settings.Skills.Where(s => s.Enabled && s.Role == SkillRole.SelfBuffGuard && s.OnlyOnLowHp).OrderByDescending(s => s.Priority))
            {
                if (!vitals.IsLowVital(slot))
                {
                    continue;
                }

                if (slot.MinNearbyEnemies > 0)
                {
                    int hostiles = this.NearbyHostileCount;
                    if (hostiles < slot.MinNearbyEnemies && area != null && player.TryGetComponent<Render>(out var pRend))
                    {
                        hostiles = CountHostilesInRange(area, new Vector2(pRend.GridPosition.X, pRend.GridPosition.Y), settings.CombatRange);
                    }

                    if (hostiles < slot.MinNearbyEnemies)
                    {
                        continue;
                    }
                }

                if (slot.MinManaPercent > 0 && vitals.ManaPercent < slot.MinManaPercent)
                {
                    continue;
                }

                int castSpacingMs = GetSkillCastSpacingMs(slot);
                if (castSpacingMs > 0 && (now - slot.LastCastAt).TotalMilliseconds < castSpacingMs)
                {
                    continue;
                }

                // In-game dynamic cooldown check (directly from PoE 2 engine memory)
                bool allowUntracked = pad?.IsLeaderConnected == true;
                if (!IsSkillReadyInGame(player, slot, allowUntracked))
                {
                    continue;
                }

                // Check if buff is already active on the player
                if (slot.OnlyWhenBuffMissing && HasBuff(player, slot))
                {
                    continue;
                }

                bool canOverlapOffense =
                    pad?.IsLeaderConnected != true && CanSelfSkillOverlapOffense(slot);
                bool executed = this.TryExecuteDefensiveCast(
                    slot,
                    now,
                    canOverlapOffense,
                    emergency: true,
                    executeInput: () =>
                    {
                        if (pad != null && pad.IsLeaderConnected && slot.GamepadButton != CoopPadButton.None)
                        {
                            pad.PressLeaderBuff(slot.GamepadButton, Math.Max(30, slot.HoldDurationMs));
                        }
                        else
                        {
                            BotInput.ExecuteAttack(
                                slot.InputType,
                                slot.Key,
                                Math.Max(30, slot.HoldDurationMs),
                                defensive: true);
                        }
                    });

                if (!executed)
                {
                    continue;
                }

                slot.LastCastAt = now;
                this.LastSkillAction = $"Panic Guard: {slot.Name}";
                return true;
            }

            return false;
        }

        public static bool IsLowVital(SkillSlotConfig slot, float hpPercent, float esPercent, float combinedPercent, bool hasEs = true)
        {
            if (!slot.OnlyOnLowHp) return false;
            float eval = slot.VitalCondition switch
            {
                VitalConditionType.HpOnly => hpPercent,
                VitalConditionType.EsOnly => hasEs ? esPercent : 100f,
                _ => combinedPercent,
            };
            return eval <= slot.LowHpThresholdPercent;
        }

        public bool TickSelfSkills(Entity player, AutoExile2Settings settings, PlayerVitals vitals, int nearbyHostiles)
        {
            return this.TryTickSelfSkills(player, settings, vitals, nearbyHostiles) != null;
        }

        private SkillSlotConfig? TryTickSelfSkills(
            Entity player,
            AutoExile2Settings settings,
            PlayerVitals vitals,
            int nearbyHostiles)
        {
            if (settings.Skills == null || settings.Skills.Count == 0)
            {
                return null;
            }

            var now = DateTime.Now;
            // Low-HP defensive slots are owned exclusively by the emergency lane so the
            // render-thread panic path and normal combat loop cannot double-dispatch them.
            foreach (var slot in settings.Skills
                .Where(s => s.Enabled && s.Role == SkillRole.SelfBuffGuard && !s.OnlyOnLowHp)
                .OrderByDescending(s => s.Priority))
            {
                if (slot.MinNearbyEnemies > 0 && nearbyHostiles < slot.MinNearbyEnemies)
                {
                    continue;
                }

                if (slot.MinManaPercent > 0 && vitals.ManaPercent < slot.MinManaPercent)
                {
                    continue;
                }

                int castSpacingMs = GetSkillCastSpacingMs(slot);
                if (castSpacingMs > 0 && (now - slot.LastCastAt).TotalMilliseconds < castSpacingMs)
                {
                    continue;
                }

                // In-game dynamic cooldown check (directly from PoE 2 engine memory)
                if (!IsSkillReadyInGame(player, slot, allowUntracked: false))
                {
                    continue;
                }

                // Check if buff is already active on the player
                if (slot.OnlyWhenBuffMissing && HasBuff(player, slot))
                {
                    continue; // Buff already present on player! Do not recast!
                }

                bool canOverlapOffense = CanSelfSkillOverlapOffense(slot);
                bool executed = this.TryExecuteDefensiveCast(
                    slot,
                    now,
                    canOverlapOffense,
                    emergency: false,
                    executeInput: () => BotInput.ExecuteAttack(
                        slot.InputType,
                        slot.Key,
                        Math.Max(30, slot.HoldDurationMs),
                        defensive: true));

                if (!executed)
                {
                    continue;
                }

                slot.LastCastAt = now;
                this.LastSkillAction = $"Buff: {slot.Name}";
                return slot;
            }

            return null;
        }

        private bool TryExecuteDefensiveCast(
            SkillSlotConfig slot,
            DateTime now,
            bool allowOffensiveOverlap,
            bool emergency,
            Action executeInput)
        {
            lock (this.defensiveInputGate)
            {
                bool defensiveBusy = now < this.defensiveInputBusyUntil;
                if (defensiveBusy)
                {
                    // Normal Buff/Guard work never interrupts another held defensive input.
                    if (!emergency)
                    {
                        return false;
                    }

                    // Emergency work may preempt a normal defensive hold, but not an equal/higher
                    // priority emergency that is already in flight.
                    if (this.defensiveInputIsEmergency &&
                        slot.Priority <= this.defensiveInputPriority)
                    {
                        return false;
                    }

                    if (this.activeChannelSlot != null)
                    {
                        BotInput.StopChannel(this.activeChannelSlot.InputType, this.activeChannelSlot.Key);
                        this.activeChannelSlot = null;
                    }

                    BotInput.ReleaseAllAttackInputs();
                }
                else
                {
                    this.StopOffensiveInputs();
                }

                if (allowOffensiveOverlap)
                {
                    // This is a synchronous short keyboard tap. It has already preempted offense,
                    // but does not need to reserve the lane after ExecuteAttack returns.
                    this.defensiveInputBusyUntil = DateTime.MinValue;
                    this.defensiveInputPriority = int.MinValue;
                    this.defensiveInputIsEmergency = false;
                    executeInput();
                    return true;
                }

                int holdMs = Math.Max(30, slot.HoldDurationMs) + InputReleaseMarginMs;
                DateTime blockedUntil = now.AddMilliseconds(holdMs);
                this.defensiveInputBusyUntil = blockedUntil;
                this.defensiveInputPriority = slot.Priority;
                this.defensiveInputIsEmergency = emergency;

                if (blockedUntil > this.nextAttackAllowed)
                {
                    this.nextAttackAllowed = blockedUntil;
                }

                executeInput();
                return true;
            }
        }

        public static bool HasBuff(Entity entity, SkillSlotConfig slot)
        {
            if (slot == null) return false;
            string rawName = !string.IsNullOrWhiteSpace(slot.BuffDebuffName)
                ? slot.BuffDebuffName
                : (!string.IsNullOrWhiteSpace(slot.AssignedSkillName) ? slot.AssignedSkillName : slot.Name);
            return HasBuff(entity, rawName);
        }

        public static bool HasBuff(Entity entity, string buffName)
        {
            if (entity == null || string.IsNullOrWhiteSpace(buffName) || !entity.TryGetComponent<Buffs>(out var pBuffs) || pBuffs.FastStatusEffects == null || pBuffs.FastStatusEffects.IsEmpty)
            {
                return false;
            }

            string clean = CleanBuffString(buffName);
            if (string.IsNullOrWhiteSpace(clean))
            {
                return false;
            }

            var aliases = GetBuffAliases(clean);

            foreach (var kv in pBuffs.FastStatusEffects)
            {
                string keyClean = CleanBuffString(kv.Key);
                string keyCleanNoDigits = System.Text.RegularExpressions.Regex.Replace(keyClean, @"\d+$", "");

                for (int i = 0; i < aliases.Count; i++)
                {
                    string alias = aliases[i];
                    string aliasNoDigits = System.Text.RegularExpressions.Regex.Replace(alias, @"\d+$", "");

                    if (keyClean.Contains(alias, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrEmpty(keyCleanNoDigits) && !string.IsNullOrEmpty(aliasNoDigits) && (keyCleanNoDigits.Contains(aliasNoDigits, StringComparison.OrdinalIgnoreCase) || aliasNoDigits.Contains(keyCleanNoDigits, StringComparison.OrdinalIgnoreCase))))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public static int CountHostilesInRange(AreaInstance area, Vector2 playerGrid, float maxRange)
        {
            if (area == null || area.AwakeEntities == null) return 0;
            int count = 0;
            float maxDistSq = maxRange > 0 ? maxRange * maxRange : 50f * 50f;
            foreach (var entity in area.AwakeEntities.Values)
            {
                if (!IsHostileMonster(entity, IntPtr.Zero)) continue;
                if (!entity.TryGetComponent<Render>(out var render)) continue;
                var eg = new Vector2(render.GridPosition.X, render.GridPosition.Y);
                if (Vector2.DistanceSquared(playerGrid, eg) <= maxDistSq)
                {
                    count++;
                }
            }
            return count;
        }

        public static bool IsSkillReadyInGame(
            Entity? player,
            SkillSlotConfig slot,
            bool allowUntracked = true)
        {
            if (player == null || slot == null)
            {
                return allowUntracked;
            }

            if (!player.TryGetComponent<Actor>(out var actor))
            {
                return allowUntracked;
            }

            var (matchedKey, matchedDetails) = FindMatchingSkill(actor, slot);
            if (matchedKey == null)
            {
                // PoE2 minion command skills live on summoned minion actors, not the player's ActiveSkills.
                // TEHHub aggregates those commands onto the player Actor with a direct usability verdict.
                if (TryGetMinionCommandUsability(actor, slot, out bool commandUsable))
                {
                    return commandUsable;
                }

                // Solo configured slots fail closed so typos / removed skills do not spam arbitrary inputs.
                // Existing co-op callers retain the historical fail-open behavior via the default parameter.
                return allowUntracked;
            }

            // 1. Check in-game cooldown charges directly from Actor.ActiveSkillCooldowns
            if (actor.ActiveSkillCooldowns != null &&
                actor.ActiveSkillCooldowns.TryGetValue(matchedDetails.UnknownIdAndEquipmentInfo, out var cdInfo))
            {
                if (cdInfo.CannotBeUsed())
                {
                    return false;
                }
            }

            // 2. Check game engine usability flag (IsSkillUsable / "Can use skills" list).
            if (actor.IsSkillUsable != null && !actor.IsSkillUsable.Contains(matchedKey))
            {
                bool anyUsable = false;
                string normalizedMatched = NormalizeSkillMatchName(matchedKey);
                foreach (var usableName in actor.IsSkillUsable)
                {
                    if (NormalizeSkillMatchName(usableName) == normalizedMatched)
                    {
                        anyUsable = true;
                        break;
                    }
                }

                if (!anyUsable)
                {
                    return false;
                }
            }

            return true;
        }

        public static bool HasAvailableCharges(Entity? player, SkillSlotConfig slot)
        {
            if (player == null || slot == null) return false;
            if (!player.TryGetComponent<Actor>(out var actor) || actor.ActiveSkills == null) return false;

            var (matchedKey, matchedDetails) = FindMatchingSkill(actor, slot);
            if (matchedKey == null) return false;

            if (actor.ActiveSkillCooldowns != null &&
                actor.ActiveSkillCooldowns.TryGetValue(matchedDetails.UnknownIdAndEquipmentInfo, out var cdInfo))
            {
                return cdInfo.MaxUses > 1 && cdInfo.TotalActiveCooldowns() < cdInfo.MaxUses;
            }

            return false;
        }

        private static (string? key, ActiveSkillDetails details) FindMatchingSkill(Actor actor, SkillSlotConfig slot)
        {
            if (actor.ActiveSkills == null || actor.ActiveSkills.Count == 0)
                return (null, default);

            string name1 = slot.AssignedSkillName ?? string.Empty;
            string name2 = slot.Name ?? string.Empty;
            string match1 = NormalizeSkillMatchName(name1);
            string match2 = NormalizeSkillMatchName(name2);

            var candidates = new List<(string key, ActiveSkillDetails details)>();
            foreach (var (k, details) in actor.ActiveSkills)
            {
                if (k.Equals(name1, StringComparison.OrdinalIgnoreCase) ||
                    k.Equals(name2, StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add((k, details));
                    continue;
                }

                string matchK = NormalizeSkillMatchName(k);
                if ((!string.IsNullOrEmpty(match1) && matchK == match1) ||
                    (!string.IsNullOrEmpty(match2) && matchK == match2))
                {
                    candidates.Add((k, details));
                }
            }

            if (candidates.Count == 0)
                return (null, default);

            // 1. Prefer candidate that has an entry in ActiveSkillCooldowns (e.g. ConvalescenceActive over Convalescence)
            if (actor.ActiveSkillCooldowns != null && actor.ActiveSkillCooldowns.Count > 0)
            {
                var withCooldown = candidates.FirstOrDefault(c =>
                    actor.ActiveSkillCooldowns.ContainsKey(c.details.UnknownIdAndEquipmentInfo));
                if (withCooldown.key != null)
                    return withCooldown;
            }

            // 2. Prefer candidate that ends with "Active" if search term was base
            var activeCandidate = candidates.FirstOrDefault(c =>
                c.key.EndsWith("Active", StringComparison.OrdinalIgnoreCase));
            if (activeCandidate.key != null)
                return activeCandidate;

            // 3. Prefer exact match
            var exact = candidates.FirstOrDefault(c =>
                c.key.Equals(name1, StringComparison.OrdinalIgnoreCase) ||
                c.key.Equals(name2, StringComparison.OrdinalIgnoreCase));
            if (exact.key != null)
                return exact;

            return candidates[0];
        }

        private static bool TryGetMinionCommandUsability(
            Actor actor,
            SkillSlotConfig slot,
            out bool usable)
        {
            usable = false;
            if (actor.MinionCommandSkills == null || actor.MinionCommandSkills.Count == 0)
            {
                return false;
            }

            string name1 = slot.AssignedSkillName ?? string.Empty;
            string name2 = slot.Name ?? string.Empty;
            string clean1 = CleanBuffString(name1);
            string clean2 = CleanBuffString(name2);

            foreach (var (commandName, commandUsable) in actor.MinionCommandSkills)
            {
                if (commandName.Equals(name1, StringComparison.OrdinalIgnoreCase) ||
                    commandName.Equals(name2, StringComparison.OrdinalIgnoreCase))
                {
                    usable = commandUsable;
                    return true;
                }

                string cleanCommand = CleanBuffString(commandName);
                if ((!string.IsNullOrEmpty(clean1) && cleanCommand == clean1) ||
                    (!string.IsNullOrEmpty(clean2) && cleanCommand == clean2))
                {
                    usable = commandUsable;
                    return true;
                }
            }

            return false;
        }

        private static string CleanBuffString(string s)
        {
            return (s ?? string.Empty).Trim().ToLowerInvariant().Replace(" ", "").Replace("_", "").Replace("-", "");
        }

        private static string NormalizeSkillMatchName(string s)
        {
            string clean = CleanBuffString(s);
            if (clean.EndsWith("active", StringComparison.OrdinalIgnoreCase) && clean.Length > 6)
            {
                clean = clean.Substring(0, clean.Length - 6);
            }

            if (clean.EndsWith("triggered", StringComparison.OrdinalIgnoreCase) && clean.Length > 9)
            {
                clean = clean.Substring(0, clean.Length - 9);
            }

            return clean;
        }

        private static List<string> GetBuffAliases(string clean)
        {
            var list = new List<string> { clean };

            // Strip trailing "active" or "triggered" from skill gem names (e.g. ConvalescenceActive -> convalescence)
            if (clean.EndsWith("active", StringComparison.OrdinalIgnoreCase) && clean.Length > 6)
            {
                string baseName = clean.Substring(0, clean.Length - 6);
                if (!list.Contains(baseName)) list.Add(baseName);
            }
            if (clean.EndsWith("triggered", StringComparison.OrdinalIgnoreCase) && clean.Length > 9)
            {
                string baseName = clean.Substring(0, clean.Length - 9);
                if (!list.Contains(baseName)) list.Add(baseName);
            }

            if (clean.Contains("convalescence"))
            {
                list.Add("convalescence");
                list.Add("convalescenceenergyshield");
            }
            else if (clean.Contains("encaseinjade") || clean.Contains("jade"))
            {
                list.Add("encaseinjade");
                list.Add("jade");
            }
            else if (clean.Contains("fortifyingcry") || clean.Contains("fortify"))
            {
                list.Add("fortifyingcry");
                list.Add("fortify");
            }
            else if (clean.Contains("magmabarrier"))
            {
                list.Add("magma");
            }
            else if (clean.Contains("virtuousbarrier"))
            {
                list.Add("virtuous");
            }
            else if (clean.Contains("glacialbarrier"))
            {
                list.Add("glacial");
            }
            else if (clean.Contains("arcticarmour"))
            {
                list.Add("arctic");
            }
            else if (clean.Contains("exposure"))
            {
                list.Add("exposure");
                list.Add("exposureelemental");
                list.Add("exposurefire");
                list.Add("exposurecold");
                list.Add("exposurelightning");
            }
            return list;
        }

        private static bool ShouldCheckTargetDebuff(SkillSlotConfig slot)
        {
            return slot.OnlyWhenBuffMissing &&
                   (string.Equals(slot.Category, SkillClassifier.CategoryCurse, StringComparison.OrdinalIgnoreCase) ||
                    slot.Role == SkillRole.PackTargeted);
        }

        internal static bool TargetHasConfiguredDebuff(Entity target, SkillSlotConfig slot)
        {
            if (!ShouldCheckTargetDebuff(slot) ||
                !target.TryGetComponent<Buffs>(out var buffs) ||
                buffs.FastStatusEffects == null)
            {
                return false;
            }

            string curseToMatch = !string.IsNullOrWhiteSpace(slot.BuffDebuffName)
                ? slot.BuffDebuffName
                : (!string.IsNullOrWhiteSpace(slot.AssignedSkillName) ? slot.AssignedSkillName : slot.Name);
            string cleanCurse = CleanBuffString(curseToMatch);
            if (string.IsNullOrEmpty(cleanCurse))
            {
                return false;
            }

            var aliases = GetBuffAliases(cleanCurse);
            foreach (var kv in buffs.FastStatusEffects)
            {
                string targetBuffClean = CleanBuffString(kv.Key);
                string targetBuffCleanNoDigits =
                    System.Text.RegularExpressions.Regex.Replace(targetBuffClean, @"\d+$", "");

                for (int i = 0; i < aliases.Count; i++)
                {
                    string alias = aliases[i];
                    string aliasNoDigits =
                        System.Text.RegularExpressions.Regex.Replace(alias, @"\d+$", "");

                    if (targetBuffClean.Contains(alias, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrEmpty(targetBuffCleanNoDigits) &&
                         !string.IsNullOrEmpty(aliasNoDigits) &&
                         (targetBuffCleanNoDigits.Contains(aliasNoDigits, StringComparison.OrdinalIgnoreCase) ||
                          aliasNoDigits.Contains(targetBuffCleanNoDigits, StringComparison.OrdinalIgnoreCase))))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private bool TickTargetedSkills(
            AreaInstance? area,
            WorldData world,
            Entity player,
            Render playerRender,
            Vector2 playerGrid,
            Entity bestTarget,
            Rarity bestTargetRarity,
            int hostileCount,
            PlayerVitals vitals,
            AutoExile2Settings settings)
        {
            // Calculate screen position of target
            if (!bestTarget.TryGetComponent<Render>(out var targetRender))
            {
                this.StopOffensiveInputs();
                return true;
            }

            var targetPos = targetRender.WorldPosition;
            float targetZ = targetPos.Z + (targetRender.ModelBounds.Z * 0.5f);
            var targetScreenPos = world.WorldToScreen(new StdTuple3D<float>
            {
                X = targetPos.X,
                Y = targetPos.Y,
                Z = targetZ,
            }, targetZ);

            if (targetScreenPos == Vector2.Zero ||
                !float.IsFinite(targetScreenPos.X) ||
                !float.IsFinite(targetScreenPos.Y))
            {
                this.StopOffensiveInputs();
                return true;
            }

            var targetGrid = new Vector2(targetRender.GridPosition.X, targetRender.GridPosition.Y);

            bool hasExplicitSkillConfiguration = settings.Skills != null;
            var configuredSkills = settings.Skills ?? SkillSlotConfig.GetDefaultSlots();

            if (this.activeChannelSlot != null &&
                (!configuredSkills.Contains(this.activeChannelSlot) ||
                 !this.activeChannelSlot.Enabled ||
                 !this.activeChannelSlot.IsChannel ||
                 this.activeChannelSlot.Role == SkillRole.Disabled ||
                 this.activeChannelSlot.Role == SkillRole.SelfBuffGuard ||
                 this.activeChannelSlot.Role == SkillRole.CorpseTargeted ||
                 this.activeChannelSlot.Role == SkillRole.Culler))
            {
                this.StopOffensiveInputs();
            }

            // Revalidate user-configured channel conditions every tick. Do not keep a held key
            // alive after range/vitals/mana/density/rarity/debuff conditions have stopped matching.
            if (this.activeChannelSlot != null)
            {
                var activeSlot = this.activeChannelSlot;
                Vector2 activeAimGrid = this.GetSkillAimGrid(activeSlot, playerGrid, targetGrid);
                float activeAimDistance = Vector2.Distance(playerGrid, activeAimGrid);
                float activeMaxRange = activeSlot.MaxTargetRange > 0
                    ? activeSlot.MaxTargetRange
                    : settings.CombatRange;

                bool canContinue =
                    activeAimDistance <= activeMaxRange &&
                    (activeSlot.MinNearbyEnemies <= 0 || hostileCount >= activeSlot.MinNearbyEnemies) &&
                    (!activeSlot.OnlyOnLowHp || vitals.IsLowVital(activeSlot)) &&
                    (activeSlot.MinManaPercent <= 0 || vitals.ManaPercent >= activeSlot.MinManaPercent) &&
                    this.MatchesTargetFilter(activeSlot.TargetFilter, bestTargetRarity) &&
                    !TargetHasConfiguredDebuff(bestTarget, activeSlot);

                if (!canContinue)
                {
                    this.StopOffensiveInputs();
                }
                else
                {
                    Vector2 activeAimPos = this.GetSkillAimScreen(
                        activeSlot,
                        world,
                        playerRender,
                        playerGrid,
                        targetGrid,
                        targetScreenPos);
                    BotInput.MoveCursor(activeAimPos);
                }
            }

            // Attack pacing gate
            if (DateTime.Now < this.nextAttackAllowed)
            {
                return true;
            }

            var candidateSkills = configuredSkills
                .Where(s => s.Enabled &&
                            s.Role != SkillRole.Disabled &&
                            s.Role != SkillRole.SelfBuffGuard &&
                            s.Role != SkillRole.CorpseTargeted &&
                            s.Role != SkillRole.Culler)
                .OrderByDescending(s => s.Priority)
                .ToList();

            if (candidateSkills.Count == 0)
            {
                // Preserve legacy attack behavior only for old/unconfigured profiles.
                // Once the user has an explicit skill list, Disabled/Self/Corpse/Culler-only
                // configurations must not silently fall back to an unrelated legacy attack.
                if (!hasExplicitSkillConfiguration)
                {
                    return this.ExecuteLegacyAttack(world, bestTarget, bestTargetRarity, targetScreenPos, settings);
                }

                this.StopOffensiveInputs();
                this.LastSkillAction = "No offensive skill configured";
                return true;
            }

            foreach (var slot in candidateSkills)
            {
                // Per-skill cast spacing. Charges affect game readiness, not the user's minimum interval.
                int castSpacingMs = GetSkillCastSpacingMs(slot);
                if (castSpacingMs > 0 && (DateTime.Now - slot.LastCastAt).TotalMilliseconds < castSpacingMs)
                {
                    continue;
                }

                // In-game dynamic cooldown check (directly from PoE 2 engine memory)
                if (!IsSkillReadyInGame(player, slot, allowUntracked: false))
                {
                    continue;
                }

                // 1. Totem Max Count check. PoE2 minions are auto-managed and never enter this cast path.
                if (string.Equals(slot.Category, SkillClassifier.CategoryTotem, StringComparison.OrdinalIgnoreCase))
                {
                    int currentTotems = this.CountActiveTotems(area, player);
                    int maxTotems = slot.MaxTotemCount > 0 ? slot.MaxTotemCount : 1;
                    if (currentTotems >= maxTotems)
                    {
                        continue; // Max totem limit reached! Do not recast!
                    }
                }

                // 2. Debuff / Curse presence check on target
                if (TargetHasConfiguredDebuff(bestTarget, slot))
                {
                    continue;
                }

                // Range check must use the actual role-aware cast point, not always the selected monster.
                Vector2 aimGrid = this.GetSkillAimGrid(slot, playerGrid, targetGrid);
                float aimDistance = Vector2.Distance(playerGrid, aimGrid);
                float maxRange = slot.MaxTargetRange > 0 ? slot.MaxTargetRange : settings.CombatRange;
                if (aimDistance > maxRange)
                {
                    continue;
                }

                // Min nearby enemies check
                if (slot.MinNearbyEnemies > 0 && hostileCount < slot.MinNearbyEnemies)
                {
                    continue;
                }

                // Low HP / Vital condition check
                if (slot.OnlyOnLowHp && !vitals.IsLowVital(slot))
                {
                    continue;
                }

                // Min Mana check
                if (slot.MinManaPercent > 0 && vitals.ManaPercent < slot.MinManaPercent)
                {
                    continue;
                }

                // Target Filter check
                if (!this.MatchesTargetFilter(slot.TargetFilter, bestTargetRarity))
                {
                    continue;
                }

                // Aim at the same role-aware point that passed the range check.
                Vector2 aimPos = this.GetSkillAimScreen(
                    slot,
                    world,
                    playerRender,
                    playerGrid,
                    targetGrid,
                    targetScreenPos);

                BotInput.MoveCursor(aimPos);

                // Execute input
                if (slot.IsChannel)
                {
                    if (this.activeChannelSlot != slot)
                    {
                        this.StopOffensiveInputs();
                        BotInput.StartChannel(slot.InputType, slot.Key);
                        this.activeChannelSlot = slot;
                    }
                }
                else
                {
                    if (this.activeChannelSlot != null)
                    {
                        this.StopOffensiveInputs();
                    }

                    BotInput.ExecuteAttack(slot.InputType, slot.Key, slot.HoldDurationMs);
                    this.nextAttackAllowed = DateTime.Now.AddMilliseconds(
                        Math.Max(20, slot.HoldDurationMs + InputReleaseMarginMs));
                }

                slot.LastCastAt = DateTime.Now;
                this.LastSkillAction = $"{slot.Name} ({slot.Role})";
                return true;
            }

            return true;
        }

        private Vector2 GetSkillAimGrid(
            SkillSlotConfig slot,
            Vector2 playerGrid,
            Vector2 targetGrid)
        {
            return slot.Role switch
            {
                SkillRole.PackTargeted => this.PackCenter,
                SkillRole.TotemOrMinion => Vector2.Lerp(playerGrid, targetGrid, 0.65f),
                _ => targetGrid,
            };
        }

        private Vector2 GetSkillAimScreen(
            SkillSlotConfig slot,
            WorldData world,
            Render playerRender,
            Vector2 playerGrid,
            Vector2 targetGrid,
            Vector2 targetScreenPos)
        {
            if (slot.Role == SkillRole.EnemyTargeted || slot.Role == SkillRole.Culler)
            {
                return targetScreenPos;
            }

            Vector2 aimGrid = this.GetSkillAimGrid(slot, playerGrid, targetGrid);
            return this.GetGridScreenPos(
                world,
                aimGrid,
                playerRender.TerrainHeight,
                targetScreenPos);
        }

        private bool MatchesTargetFilter(SkillTargetFilter filter, Rarity rarity)
        {
            return filter switch
            {
                SkillTargetFilter.Any => true,
                SkillTargetFilter.NormalOnly => rarity == Rarity.Normal,
                SkillTargetFilter.MagicOrAbove => rarity >= Rarity.Magic,
                SkillTargetFilter.RareOrAbove => rarity >= Rarity.Rare,
                SkillTargetFilter.UniqueOnly => rarity >= Rarity.Unique,
                _ => true,
            };
        }

        private Vector2 GetGridScreenPos(WorldData world, Vector2 gridPos, float terrainZ, Vector2 fallbackScreenPos)
        {
            float wx = gridPos.X * 10.87f;
            float wy = gridPos.Y * 10.87f;
            var pos = world.WorldToScreen(new StdTuple3D<float> { X = wx, Y = wy, Z = terrainZ }, terrainZ);
            if (pos == Vector2.Zero ||
                !float.IsFinite(pos.X) ||
                !float.IsFinite(pos.Y))
            {
                return fallbackScreenPos;
            }

            return pos;
        }

        private bool ExecuteLegacyAttack(
            WorldData world,
            Entity bestTarget,
            Rarity bestTargetRarity,
            Vector2 targetScreenPos,
            AutoExile2Settings settings)
        {
            BotInput.MoveCursor(targetScreenPos);

            AttackInputType attackType = settings.PrimaryAttackType;
            var attackKey = settings.PrimaryAttackKey;

            if (bestTargetRarity >= Rarity.Rare && settings.UseSecondaryAttack)
            {
                attackType = settings.SecondaryAttackType;
                attackKey = settings.SecondaryAttackKey;
            }

            BotInput.ExecuteAttack(attackType, attackKey, settings.AttackHoldDurationMs);
            this.nextAttackAllowed = DateTime.Now.AddMilliseconds(settings.AttackHoldDurationMs + settings.AttackCooldownMs);
            this.LastSkillAction = $"Legacy: {attackType}";
            return true;
        }

        /// <summary>
        /// Robustly counts active friendly totems using AwakeEntities, Actor.DeployedEntities, and totem reservation buffs.
        /// </summary>
        /// <summary>
        /// Robustly counts active friendly totems within radius of the player (default 40g).
        /// If a totem is farther than 40g, it is considered gone so the player will recast near them!
        /// </summary>
        public int CountActiveTotems(AreaInstance? area, Entity player, float maxDistanceGrid = 40f)
        {
            int awakeTotemCount = 0;
            Vector2 playerGrid = Vector2.Zero;
            bool hasPlayerGrid = false;
            if (player != null && player.TryGetComponent<Render>(out var pR))
            {
                hasPlayerGrid = true;
                playerGrid = new Vector2(pR.GridPosition.X, pR.GridPosition.Y);
            }

            if (area != null && area.AwakeEntities != null)
            {
                foreach (var entity in area.AwakeEntities.Values)
                {
                    if (!entity.IsValid) continue;
                    string path = entity.Path ?? string.Empty;
                    if (path.Contains("Totem", StringComparison.OrdinalIgnoreCase) ||
                        path.Contains("Ballista", StringComparison.OrdinalIgnoreCase) ||
                        path.Contains("Ancestor", StringComparison.OrdinalIgnoreCase))
                    {
                        if (IsFriendlyTotem(entity) && IsTotemAlive(entity))
                        {
                            if (hasPlayerGrid && entity.TryGetComponent<Render>(out var tR) && tR != null)
                            {
                                var tGrid = new Vector2(tR.GridPosition.X, tR.GridPosition.Y);
                                if (Vector2.Distance(playerGrid, tGrid) > maxDistanceGrid)
                                {
                                    continue; // Too far (> 40g), consider non-existent so bot recasts near player!
                                }
                            }
                            awakeTotemCount++;
                        }
                    }
                }
            }

            return awakeTotemCount;
        }

        public static bool IsFriendlyTotem(Entity entity)
        {
            if (entity.TryGetComponent<Positioned>(out var pos) && pos.IsFriendly)
            {
                return true;
            }
            if (entity.TryGetComponent<Stats>(out var stats))
            {
                if (stats.StatsChangedByBuffAndActions != null &&
                    stats.StatsChangedByBuffAndActions.TryGetValue(GameStats.is_player_minion, out var mVal) && mVal > 0)
                {
                    return true;
                }
                if (stats.StatsChangedByItems != null &&
                    stats.StatsChangedByItems.TryGetValue(GameStats.is_player_minion, out var mVal2) && mVal2 > 0)
                {
                    return true;
                }
            }
            return false;
        }

        public static bool IsTotemAlive(Entity entity)
        {
            if (entity.TryGetComponent<Life>(out var l) && (!l.IsAlive || l.Health.Current <= 0))
            {
                return false;
            }
            if (entity.TryGetComponent<Stats>(out var stats))
            {
                if (stats.StatsChangedByBuffAndActions != null &&
                    stats.StatsChangedByBuffAndActions.TryGetValue(GameStats.is_dead, out var dVal) && dVal > 0)
                {
                    return false;
                }
                if (stats.StatsChangedByItems != null &&
                    stats.StatsChangedByItems.TryGetValue(GameStats.is_dead, out var dVal2) && dVal2 > 0)
                {
                    return false;
                }
            }
            return true;
        }

        public static bool IsHostileMonster(Entity entity, IntPtr playerAddress)
        {
            if (!entity.IsValid || entity.Address == playerAddress) return false;
            if (entity.EntityType == EntityTypes.Player || entity.EntityType == EntityTypes.NPC) return false;
            if (entity.Path != null && (entity.Path.StartsWith("Metadata/Characters/") || entity.Path.StartsWith("Metadata/NPC/") || entity.Path.StartsWith("Metadata/Terrain/"))) return false;
            if (entity.EntityState == EntityStates.MonsterFriendly) return false;
            if (entity.TryGetComponent<Positioned>(out var posComp) && posComp.IsFriendly) return false;
            if (IsFriendlyTotem(entity)) return false;

            // Block Daemons (Map Mod aura generators like MapModEnfeebleDaemon), hazards, effigies, dummies, markers
            if (entity.Path != null)
            {
                string p = entity.Path;

                // 1. Check Ignore Monsters list configured in TEHhub Settings
                if (Core.GHSettings?.MonstersPathsToIgnore != null &&
                    Core.GHSettings.MonstersPathsToIgnore.Any(ignored => !string.IsNullOrEmpty(ignored) && p.StartsWith(ignored, StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }

                // 2. Block keywords for non-combat daemons, environment hazards, ambient critters
                if (p.Contains("Daemon", StringComparison.OrdinalIgnoreCase) ||
                    p.Contains("Hazard", StringComparison.OrdinalIgnoreCase) ||
                    p.Contains("Triggerable", StringComparison.OrdinalIgnoreCase) ||
                    p.Contains("Effigy", StringComparison.OrdinalIgnoreCase) ||
                    p.Contains("Dummy", StringComparison.OrdinalIgnoreCase) ||
                    p.Contains("Marker", StringComparison.OrdinalIgnoreCase) ||
                    p.Contains("Firefly", StringComparison.OrdinalIgnoreCase) ||
                    p.Contains("FireFlies", StringComparison.OrdinalIgnoreCase) ||
                    p.Contains("Wisp", StringComparison.OrdinalIgnoreCase) ||
                    p.Contains("Beetle", StringComparison.OrdinalIgnoreCase) ||
                    p.Contains("Ambient", StringComparison.OrdinalIgnoreCase) ||
                    p.Contains("Critter", StringComparison.OrdinalIgnoreCase) ||
                    p.Contains("Pet", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            if (!entity.TryGetComponent<Life>(out var mLife) || mLife.Health.Current <= 0) return false;
            if (!entity.TryGetComponent<Render>(out _)) return false;

            // Check if monster entity (via component, path, or magic properties)
            bool isMonster = entity.EntityType == EntityTypes.Monster
                || (entity.Path != null && (entity.Path.StartsWith("Metadata/Monsters/") || entity.Path.Contains("Monster")))
                || entity.TryGetComponent<ObjectMagicProperties>(out _);

            return isMonster;
        }
    }
}
