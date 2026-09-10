// <copyright file="CoopCombatWorker.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Brain.Workers
{
    using System;
    using System.Linq;
    using System.Numerics;
    using GameHelper.RemoteEnums;
    using GameHelper.RemoteObjects.Components;
    using GameOffsets.Natives;
    using AutoExile2.Brain;
    using AutoExile2.Modes;
    using AutoExile2.Systems;

    /// <summary>
    /// Specialized Worker Subsystem for Player 2 (Follower) combat:
    /// executes Culler skills, SelfBuffs, Guard skills, and targeted attacks.
    /// </summary>
    public class CoopCombatWorker
    {
        public const int ANIM_SPRINT = 0x368; // 872 decimal: Sprint (Hold B)

        public DateTime LastAttackTime { get; private set; } = DateTime.MinValue;

        public string ActiveSkillName { get; private set; } = string.Empty;

        public void Reset()
        {
            this.LastAttackTime = DateTime.MinValue;
            this.ActiveSkillName = string.Empty;
        }

        /// <summary>
        /// Executes combat actions based on the active BotGoal from the Central Brain.
        /// Returns true if a skill was fired this tick.
        /// </summary>
        public bool Execute(
            BotGoal goal,
            WorldPerception p,
            CoopVirtualGamepad pad,
            AutoExile2Settings s,
            BotContext ctx)
        {
            if (p.FollowerEntity == null || !s.P2EnableCombat || p.IsPeacefulZone)
            {
                pad.SetFollowerAim(Vector2.Zero);
                return false;
            }

            // Sprinting stance sheathes weapons -> no attacks allowed during HardCatchup or active sprint
            if (goal.Type == BotGoalType.HardCatchup || p.FollowerAnimId == ANIM_SPRINT)
            {
                return false;
            }

            var follower = p.FollowerEntity;
            if (!follower.TryGetComponent<Life>(out var fLife) || fLife.Health.Current <= 0)
            {
                return false;
            }

            var fVitals = new PlayerVitals(fLife);
            var now = DateTime.Now;

            // 1. Self Buff & Guard Skills (Highest combat priority)
            if (s.P2Skills != null)
            {
                foreach (var slot in s.P2Skills.Where(x => x.Enabled && (x.Role == SkillRole.SelfBuffGuard || x.Category == "Buff" || x.Category == "Guard" || x.Category == "Warcry")).OrderByDescending(x => x.Priority))
                {
                    int effectiveInterval = CombatSystem.HasAvailableCharges(follower, slot) ? Math.Min(slot.MinCastIntervalMs, 300) : slot.MinCastIntervalMs;
                    int totalInterval = effectiveInterval + Math.Max(30, slot.HoldDurationMs);
                    if ((now - slot.LastCastAt).TotalMilliseconds < totalInterval) continue;
                    if (!CombatSystem.IsSkillReadyInGame(follower, slot)) continue;
                    if (slot.OnlyOnLowHp && !fVitals.IsLowVital(slot)) continue;
                    if (slot.MinManaPercent > 0 && fVitals.ManaPercent < slot.MinManaPercent) continue;
                    if (slot.MinNearbyEnemies > 0 && p.NearbyEnemyCount < slot.MinNearbyEnemies) continue;

                    if (slot.OnlyWhenBuffMissing && CombatSystem.HasBuff(follower, slot)) continue;

                    slot.LastCastAt = now;
                    this.LastAttackTime = now;
                    this.ActiveSkillName = slot.Name;
                    pad.PressFollowerButton(slot.GamepadButton, Math.Max(30, slot.HoldDurationMs));
                    return true;
                }
            }

            // 2. Only perform offensive attacks if Brain determined Goal is Combat
            if (goal.Type != BotGoalType.Combat || s.P2Skills == null)
            {
                return false;
            }

            // 2.1 Culler Skills (Focused fire ahead of Leader)
            foreach (var cullerSkill in s.P2Skills.Where(x => x.Enabled && (x.Category == "Culler" || x.Role == SkillRole.Culler)).OrderByDescending(x => x.Priority))
            {
                int effectiveCullerInterval = Math.Max(100, cullerSkill.MinCastIntervalMs);
                int totalCullerInterval = effectiveCullerInterval + Math.Max(30, cullerSkill.HoldDurationMs);
                if ((now - cullerSkill.LastCastAt).TotalMilliseconds < totalCullerInterval) continue;
                if (cullerSkill.MinManaPercent > 0 && fVitals.ManaPercent < cullerSkill.MinManaPercent) continue;

                float rawStartDist = cullerSkill.CullerStartAttackDistance > 0 ? cullerSkill.CullerStartAttackDistance : 35f;
                float startDistGrid = rawStartDist > 150f ? (rawStartDist / p.GridToWorld) : rawStartDist;
                if (p.DistanceToLeader > startDistGrid) continue;

                if (cullerSkill.CullerRequireMonsters && p.NearbyEnemyCount == 0) continue;

                // Native PoE 2 Gamepad Auto-Aim handles targeting
                pad.SetFollowerAim(Vector2.Zero);

                cullerSkill.LastCastAt = now;
                this.LastAttackTime = now;
                this.ActiveSkillName = $"Culler: {cullerSkill.Name}";
                pad.PressFollowerButton(cullerSkill.GamepadButton, Math.Max(30, cullerSkill.HoldDurationMs));
                return true;
            }

            // 2.2 Regular Targeted Skills
            if (p.BestCombatTarget != null && p.BestCombatTarget.TryGetComponent<Render>(out var targetRender))
            {
                pad.SetFollowerAim(Vector2.Zero);

                Rarity targetRarity = Rarity.Normal;
                if (p.BestCombatTarget.TryGetComponent<ObjectMagicProperties>(out var omp))
                {
                    targetRarity = omp.Rarity;
                }

                foreach (var skill in s.P2Skills.Where(x => x.Enabled && x.Role != SkillRole.SelfBuffGuard && x.Role != SkillRole.Culler && x.Category != "Buff" && x.Category != "Guard" && x.Category != "Warcry" && x.Category != "Culler" && x.Role != SkillRole.Disabled).OrderByDescending(x => x.Priority))
                {
                    int effectiveInterval = CombatSystem.HasAvailableCharges(follower, skill) ? Math.Min(skill.MinCastIntervalMs, 300) : skill.MinCastIntervalMs;
                    int totalInterval = effectiveInterval + Math.Max(30, skill.HoldDurationMs);
                    if ((now - skill.LastCastAt).TotalMilliseconds < totalInterval) continue;
                    if (!CombatSystem.IsSkillReadyInGame(follower, skill)) continue;
                    if (skill.OnlyOnLowHp && !fVitals.IsLowVital(skill)) continue;
                    if (skill.MinManaPercent > 0 && fVitals.ManaPercent < skill.MinManaPercent) continue;

                    if (skill.TargetFilter == SkillTargetFilter.NormalOnly && targetRarity != Rarity.Normal) continue;
                    if (skill.TargetFilter == SkillTargetFilter.MagicOrAbove && targetRarity < Rarity.Magic) continue;
                    if (skill.TargetFilter == SkillTargetFilter.RareOrAbove && targetRarity < Rarity.Rare) continue;
                    if (skill.TargetFilter == SkillTargetFilter.UniqueOnly && targetRarity != Rarity.Unique) continue;

                    if (skill.MinNearbyEnemies > 0 && p.NearbyEnemyCount < skill.MinNearbyEnemies) continue;
                    float effectiveRange = skill.MaxTargetRange > 0 ? skill.MaxTargetRange : 75f;
                    if (p.ClosestEnemyDistance > effectiveRange) continue;

                    if (skill.OnlyWhenBuffMissing && CombatSystem.HasBuff(follower, skill)) continue;

                    skill.LastCastAt = now;
                    this.LastAttackTime = now;
                    this.ActiveSkillName = skill.Name;
                    pad.PressFollowerButton(skill.GamepadButton, Math.Max(30, skill.HoldDurationMs));
                    return true;
                }
            }

            return false;
        }
    }
}
