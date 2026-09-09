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
    using GameHelper;
    using GameHelper.RemoteEnums;
    using GameHelper.RemoteEnums.Entity;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.RemoteObjects.States.InGameStateObjects;
    using GameOffsets.Natives;
    using AutoExile2.WebServer;

    /// <summary>
    /// Handles hostile monster scanning, smart target scoring, Auto Flasks,
    /// and multi-skill execution based on individual skill slot configurations.
    /// Ported and adapted from AutoExile 1's combat engine.
    /// </summary>
    public class CombatSystem
    {
        private DateTime nextAttackAllowed = DateTime.MinValue;
        private DateTime lastLifeFlaskAt = DateTime.MinValue;
        private DateTime lastManaFlaskAt = DateTime.MinValue;

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
        /// Stops any currently active channeled skill and releases attack inputs.
        /// </summary>
        public void StopAllChannels()
        {
            if (this.activeChannelSlot != null)
            {
                BotInput.StopChannel(this.activeChannelSlot.InputType, this.activeChannelSlot.Key);
                this.activeChannelSlot = null;
            }

            BotInput.ReleaseAllAttackInputs();
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
                this.CurrentTargetId = 0;
                this.NearbyHostileCount = 0;
                this.WeightedDensity = 0;
                this.ClosestHostileDistance = float.MaxValue;
                return false;
            }

            if (!player.TryGetComponent<Render>(out var pRender))
            {
                this.ClosestHostileDistance = float.MaxValue;
                return false;
            }

            // 1. Player Vitals (HP and Mana percentages)
            float hpPercent = 100f;
            float manaPercent = 100f;
            if (player.TryGetComponent<Life>(out var pLife))
            {
                if (pLife.Health.Total > 0)
                {
                    hpPercent = ((float)pLife.Health.Current / pLife.Health.Total) * 100f;
                }

                if (pLife.Mana.Total > 0)
                {
                    manaPercent = ((float)pLife.Mana.Current / pLife.Mana.Total) * 100f;
                }
            }

            // 2. Auto Flasks (AutoExile 1 automated recovery)
            this.TickAutoFlasks(settings, hpPercent, manaPercent);

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

            // 4. Tick Self-Cast Skills (Buffs, Guards, Cries) independently of target
            bool executedSelfSkill = this.TickSelfSkills(player, settings, hpPercent, manaPercent);

            if (bestTarget == null)
            {
                this.CurrentTargetId = 0;
                this.CurrentTargetName = string.Empty;
                this.CurrentTargetRarity = string.Empty;
                this.StopAllChannels();
                return executedSelfSkill;
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

            // Live scan debuffs on the targeted monster
            if (bestTarget.TryGetComponent<Buffs>(out var tBuffs) && tBuffs.StatusEffects != null)
            {
                this.ObservedTargetDebuffs.Clear();
                foreach (var (debuffName, eff) in tBuffs.StatusEffects)
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
                hpPercent,
                manaPercent,
                settings);
        }

        private void TickAutoFlasks(AutoExile2Settings settings, float hpPercent, float manaPercent)
        {
            if (settings.AutoLifeFlask && hpPercent <= settings.LifeFlaskThresholdPercent)
            {
                if ((DateTime.Now - this.lastLifeFlaskAt).TotalMilliseconds >= settings.LifeFlaskCooldownMs)
                {
                    BotInput.TapKey(settings.LifeFlaskKey, 40);
                    this.lastLifeFlaskAt = DateTime.Now;
                }
            }

            if (settings.AutoManaFlask && manaPercent <= settings.ManaFlaskThresholdPercent)
            {
                if ((DateTime.Now - this.lastManaFlaskAt).TotalMilliseconds >= settings.ManaFlaskCooldownMs)
                {
                    BotInput.TapKey(settings.ManaFlaskKey, 40);
                    this.lastManaFlaskAt = DateTime.Now;
                }
            }
        }

        private bool TickSelfSkills(Entity player, AutoExile2Settings settings, float hpPercent, float manaPercent)
        {
            if (settings.Skills == null || settings.Skills.Count == 0)
            {
                return false;
            }

            foreach (var slot in settings.Skills.Where(s => s.Enabled && s.Role == SkillRole.SelfBuffGuard).OrderByDescending(s => s.Priority))
            {
                if (slot.OnlyOnLowHp && hpPercent > slot.LowHpThresholdPercent)
                {
                    continue;
                }

                if (slot.MinManaPercent > 0 && manaPercent < slot.MinManaPercent)
                {
                    continue;
                }

                if (slot.MinCastIntervalMs > 0 && (DateTime.Now - slot.LastCastAt).TotalMilliseconds < slot.MinCastIntervalMs)
                {
                    continue;
                }

                // Check if buff is already active on the player
                if (slot.OnlyWhenBuffMissing)
                {
                    string buffToMatch = !string.IsNullOrWhiteSpace(slot.BuffDebuffName)
                        ? slot.BuffDebuffName
                        : (!string.IsNullOrWhiteSpace(slot.AssignedSkillName) ? slot.AssignedSkillName : slot.Name);

                    if (!string.IsNullOrWhiteSpace(buffToMatch) && player.TryGetComponent<Buffs>(out var pBuffs) && pBuffs.StatusEffects != null)
                    {
                        string clean = buffToMatch.Trim().ToLowerInvariant().Replace(" ", "").Replace("_", "").Replace("-", "");
                        bool hasBuff = pBuffs.StatusEffects.Any(kv =>
                        {
                            string k = kv.Key.ToLowerInvariant().Replace(" ", "").Replace("_", "").Replace("-", "");
                            return k.Contains(clean);
                        });

                        if (hasBuff)
                        {
                            continue; // Buff already present on player! Do not recast!
                        }
                    }
                }

                BotInput.ExecuteAttack(slot.InputType, slot.Key, slot.HoldDurationMs);
                slot.LastCastAt = DateTime.Now;
                this.LastSkillAction = $"Buff: {slot.Name}";
                return true;
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
            float hpPercent,
            float manaPercent,
            AutoExile2Settings settings)
        {
            // Calculate screen position of target
            if (!bestTarget.TryGetComponent<Render>(out var targetRender))
            {
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

            if (targetScreenPos == Vector2.Zero || float.IsNaN(targetScreenPos.X))
            {
                return true;
            }

            float distToTarget = Vector2.Distance(
                playerGrid,
                new Vector2(targetRender.GridPosition.X, targetRender.GridPosition.Y));

            // If channeling an active skill, update cursor towards target
            if (this.activeChannelSlot != null)
            {
                BotInput.MoveCursor(targetScreenPos);

                // Check if channeling conditions are still met
                if (distToTarget > (this.activeChannelSlot.MaxTargetRange > 0 ? this.activeChannelSlot.MaxTargetRange : settings.CombatRange))
                {
                    this.StopAllChannels();
                }
            }

            // Attack pacing gate
            if (DateTime.Now < this.nextAttackAllowed)
            {
                return true;
            }

            var configuredSkills = settings.Skills ?? SkillSlotConfig.GetDefaultSlots();
            var candidateSkills = configuredSkills
                .Where(s => s.Enabled && s.Role != SkillRole.Disabled && s.Role != SkillRole.SelfBuffGuard)
                .OrderByDescending(s => s.Priority)
                .ToList();

            // Fallback to legacy settings if no active skills found
            if (candidateSkills.Count == 0)
            {
                return this.ExecuteLegacyAttack(world, bestTarget, bestTargetRarity, targetScreenPos, settings);
            }

            foreach (var slot in candidateSkills)
            {
                // Cooldown / Cast Interval check
                if (slot.MinCastIntervalMs > 0 && (DateTime.Now - slot.LastCastAt).TotalMilliseconds < slot.MinCastIntervalMs)
                {
                    continue;
                }

                // 1. Totem / Minion Max Count check
                if (slot.Category == SkillClassifier.CategoryTotem || slot.Role == SkillRole.TotemOrMinion)
                {
                    int currentTotems = this.CountActiveTotems(area, player);
                    int maxTotems = slot.MaxTotemCount > 0 ? slot.MaxTotemCount : 1;
                    if (currentTotems >= maxTotems)
                    {
                        continue; // Max totem limit reached! Do not recast!
                    }
                }

                // 2. Debuff / Curse presence check on target
                if (slot.OnlyWhenBuffMissing && (slot.Category == SkillClassifier.CategoryCurse || slot.Role == SkillRole.PackTargeted))
                {
                    if (bestTarget.TryGetComponent<Buffs>(out var tBuffs) && tBuffs.StatusEffects != null)
                    {
                        string curseToMatch = !string.IsNullOrWhiteSpace(slot.BuffDebuffName)
                            ? slot.BuffDebuffName
                            : (!string.IsNullOrWhiteSpace(slot.AssignedSkillName) ? slot.AssignedSkillName : slot.Name);
                        if (!string.IsNullOrWhiteSpace(curseToMatch))
                        {
                            string cleanCurse = curseToMatch.Trim().ToLowerInvariant().Replace(" ", "").Replace("_", "").Replace("-", "");
                            bool hasDebuff = tBuffs.StatusEffects.Any(kv => kv.Key.ToLowerInvariant().Replace(" ", "").Replace("_", "").Replace("-", "").Contains(cleanCurse));
                            if (hasDebuff)
                            {
                                continue; // Monster already has this curse/debuff!
                            }
                        }
                    }
                }

                // Range check
                float maxRange = slot.MaxTargetRange > 0 ? slot.MaxTargetRange : settings.CombatRange;
                if (distToTarget > maxRange)
                {
                    continue;
                }

                // Min nearby enemies check
                if (slot.MinNearbyEnemies > 0 && hostileCount < slot.MinNearbyEnemies)
                {
                    continue;
                }

                // Low HP condition check
                if (slot.OnlyOnLowHp && hpPercent > slot.LowHpThresholdPercent)
                {
                    continue;
                }

                // Min Mana check
                if (slot.MinManaPercent > 0 && manaPercent < slot.MinManaPercent)
                {
                    continue;
                }

                // Target Filter check
                if (!this.MatchesTargetFilter(slot.TargetFilter, bestTargetRarity))
                {
                    continue;
                }

                // Aim cursor based on SkillRole
                Vector2 aimPos = targetScreenPos;
                if (slot.Role == SkillRole.PackTargeted)
                {
                    aimPos = this.GetGridScreenPos(world, this.PackCenter, playerRender.TerrainHeight, targetScreenPos);
                }
                else if (slot.Role == SkillRole.TotemOrMinion)
                {
                    // Deploy totem slightly between player and target pack
                    var totemGrid = Vector2.Lerp(playerGrid, new Vector2(targetRender.GridPosition.X, targetRender.GridPosition.Y), 0.65f);
                    aimPos = this.GetGridScreenPos(world, totemGrid, playerRender.TerrainHeight, targetScreenPos);
                }

                BotInput.MoveCursor(aimPos);

                // Execute input
                if (slot.IsChannel)
                {
                    if (this.activeChannelSlot != slot)
                    {
                        this.StopAllChannels();
                        BotInput.StartChannel(slot.InputType, slot.Key);
                        this.activeChannelSlot = slot;
                    }
                }
                else
                {
                    if (this.activeChannelSlot != null)
                    {
                        this.StopAllChannels();
                    }

                    BotInput.ExecuteAttack(slot.InputType, slot.Key, slot.HoldDurationMs);
                    this.nextAttackAllowed = DateTime.Now.AddMilliseconds(slot.HoldDurationMs + 20);
                }

                slot.LastCastAt = DateTime.Now;
                this.LastSkillAction = $"{slot.Name} ({slot.Role})";
                return true;
            }

            return true;
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
            if (pos == Vector2.Zero || float.IsNaN(pos.X))
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
                    if (entity.Path.Contains("Totem", StringComparison.OrdinalIgnoreCase) ||
                        entity.Path.Contains("Ballista", StringComparison.OrdinalIgnoreCase) ||
                        entity.Path.Contains("Ancestor", StringComparison.OrdinalIgnoreCase))
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

                // 1. Check Ignore Monsters list configured in GameHelper Settings
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
