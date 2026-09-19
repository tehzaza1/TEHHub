// <copyright file="CoopCombatWorker.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Brain.Workers
{
    using System;
    using System.Linq;
    using System.Numerics;
    using TEHhub.RemoteEnums;
    using TEHhub.RemoteObjects.Components;
    using TEHhub.Offsets.Natives;
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
        /// Executes combat actions based on the active BotGoal and BrainDirective from the Central Brain.
        /// Returns true if a skill was fired this tick.
        /// Supports concurrent attack-moving (firing while walking to loot or formation).
        /// </summary>
        public bool Execute(
            BotGoal goal,
            WorldPerception p,
            CoopVirtualGamepad pad,
            AutoExile2Settings s,
            BotContext ctx,
            BrainDirective? directive = null)
        {
            if (p.FollowerEntity == null || !s.P2EnableCombat || p.IsPeacefulZone)
            {
                this.NeutralizeAim(pad);
                return false;
            }

            // Sprinting stance sheathes weapons -> no attacks allowed during HardCatchup or active sprint
            if (goal.Type == BotGoalType.HardCatchup || p.FollowerAnimId == ANIM_SPRINT)
            {
                this.NeutralizeAim(pad);
                return false;
            }

            var follower = p.FollowerEntity;
            if (!follower.TryGetComponent<Life>(out var fLife) || fLife.Health.Current <= 0)
            {
                this.NeutralizeAim(pad);
                return false;
            }

            var fVitals = new PlayerVitals(fLife);
            var now = DateTime.Now;

            // 1. Self Buff & Guard Skills (Highest combat priority)
            if (s.P2Skills != null)
            {
                foreach (var slot in s.P2Skills.Where(x => x.Enabled && (x.Role == SkillRole.SelfBuffGuard || x.Category == "Buff" || x.Category == "Guard" || x.Category == "Warcry")).OrderByDescending(x => x.Priority))
                {
                    int castSpacingMs = CombatSystem.GetSkillCastSpacingMs(slot);
                    if ((now - slot.LastCastAt).TotalMilliseconds < castSpacingMs) continue;
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

            // 2. Perform offensive attacks if Brain determined Combat is desired
            // (Enables concurrent attack-moving while walking towards loot or following formation)
            bool shouldAttack = (directive != null && directive.Combat.ShouldAttack) ||
                                goal.Type == BotGoalType.Combat ||
                                (goal.Type == BotGoalType.Loot && p.NearbyEnemyCount > 0);

            if (!shouldAttack || s.P2Skills == null)
            {
                this.NeutralizeAim(pad);
                return false;
            }

            // Aim handling: If directive provides explicit aim, point Right Stick in that direction; otherwise rely on auto-aim
            if (directive?.Combat.AimPosition != null && ctx.World != null)
            {
                var aimDir = BotInput.GridToScreenDirection(ctx.World, follower, directive.Combat.AimPosition.Value, p.FollowerGrid, p.GridToWorld);
                pad.SetFollowerAim(aimDir);
            }
            else
            {
                pad.SetFollowerAim(Vector2.Zero);
            }

            // 2.1 Culler Skills (Focused fire ahead of Leader)
            foreach (var cullerSkill in s.P2Skills.Where(x => x.Enabled && (x.Category == "Culler" || x.Role == SkillRole.Culler)).OrderByDescending(x => x.Priority))
            {
                int cullerSpacingMs = Math.Max(100, CombatSystem.GetSkillCastSpacingMs(cullerSkill));
                if ((now - cullerSkill.LastCastAt).TotalMilliseconds < cullerSpacingMs) continue;
                if (!CombatSystem.IsSkillReadyInGame(follower, cullerSkill)) continue;
                if (cullerSkill.MinManaPercent > 0 && fVitals.ManaPercent < cullerSkill.MinManaPercent) continue;

                float rawStartDist = cullerSkill.CullerStartAttackDistance > 0 ? cullerSkill.CullerStartAttackDistance : 35f;
                float startDistGrid = rawStartDist > 150f ? (rawStartDist / p.GridToWorld) : rawStartDist;
                if (p.DistanceToLeader > startDistGrid) continue;

                if (cullerSkill.CullerRequireMonsters && p.NearbyEnemyCount == 0) continue;

                cullerSkill.LastCastAt = now;
                this.LastAttackTime = now;
                this.ActiveSkillName = $"Culler: {cullerSkill.Name}";
                pad.PressFollowerButton(cullerSkill.GamepadButton, Math.Max(30, cullerSkill.HoldDurationMs));
                return true;
            }

            // 2.2 Regular Targeted Skills
            if (p.BestCombatTarget != null && p.BestCombatTarget.TryGetComponent<Render>(out var targetRender))
            {
                Rarity targetRarity = Rarity.Normal;
                if (p.BestCombatTarget.TryGetComponent<ObjectMagicProperties>(out var omp))
                {
                    targetRarity = omp.Rarity;
                }

                foreach (var skill in s.P2Skills.Where(x => x.Enabled &&
                    x.Role != SkillRole.SelfBuffGuard &&
                    x.Role != SkillRole.Culler &&
                    x.Role != SkillRole.CorpseTargeted &&
                    x.Category != "Buff" &&
                    x.Category != "Guard" &&
                    x.Category != "Warcry" &&
                    x.Category != "Culler" &&
                    x.Role != SkillRole.Disabled).OrderByDescending(x => x.Priority))
                {
                    int castSpacingMs = CombatSystem.GetSkillCastSpacingMs(skill);
                    if ((now - skill.LastCastAt).TotalMilliseconds < castSpacingMs) continue;
                    if (!CombatSystem.IsSkillReadyInGame(follower, skill)) continue;
                    if (skill.OnlyOnLowHp && !fVitals.IsLowVital(skill)) continue;
                    if (skill.MinManaPercent > 0 && fVitals.ManaPercent < skill.MinManaPercent) continue;

                    if (skill.TargetFilter == SkillTargetFilter.NormalOnly && targetRarity != Rarity.Normal) continue;
                    if (skill.TargetFilter == SkillTargetFilter.MagicOrAbove && targetRarity < Rarity.Magic) continue;
                    if (skill.TargetFilter == SkillTargetFilter.RareOrAbove && targetRarity < Rarity.Rare) continue;
                    if (skill.TargetFilter == SkillTargetFilter.UniqueOnly && targetRarity != Rarity.Unique) continue;

                    if (skill.MinNearbyEnemies > 0 && p.NearbyEnemyCount < skill.MinNearbyEnemies) continue;

                    var targetGrid = new Vector2(targetRender.GridPosition.X, targetRender.GridPosition.Y);
                    float targetDistance = Vector2.Distance(p.FollowerGrid, targetGrid);
                    float castDistance = skill.Role == SkillRole.TotemOrMinion
                        ? targetDistance * 0.65f
                        : targetDistance;
                    float effectiveRange = skill.MaxTargetRange > 0 ? skill.MaxTargetRange : 75f;
                    if (castDistance > effectiveRange) continue;

                    if (CombatSystem.TargetHasConfiguredDebuff(p.BestCombatTarget, skill)) continue;

                    skill.LastCastAt = now;
                    this.LastAttackTime = now;
                    this.ActiveSkillName = skill.Name;
                    pad.PressFollowerButton(skill.GamepadButton, Math.Max(30, skill.HoldDurationMs));
                    return true;
                }
            }

            this.NeutralizeAim(pad);
            return false;
        }

        private void NeutralizeAim(CoopVirtualGamepad pad)
        {
            pad.SetFollowerAim(Vector2.Zero);
            this.ActiveSkillName = string.Empty;
        }
    }
}
