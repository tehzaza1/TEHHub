// <copyright file="SkillClassifier.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Automatic skill classifier ported from AutoExile 1.
    /// Categorizes in-game skills into Attack, Buff, Curse, Totem, Guard, Warcry, Minion, and Movement,
    /// and provides recommended combat parameters.
    /// </summary>
    public static class SkillClassifier
    {
        public const string CategoryAttack = "Attack";
        public const string CategoryBuff = "Buff";
        public const string CategoryCurse = "Curse";
        public const string CategoryTotem = "Totem";
        public const string CategoryGuard = "Guard";
        public const string CategoryWarcry = "Warcry";
        public const string CategoryMinion = "Minion";
        public const string CategoryMovement = "Movement";
        public const string CategoryCustom = "Custom";

        private static readonly string[] TotemKeywords =
        {
            "totem", "ballista", "ancestor", "ancestral", "earthbreaker", "siegeballista", "spelltotem", "holyflame"
        };

        private static readonly string[] CurseKeywords =
        {
            "curse", "hex", "mark", "despair", "flammability", "conductivity", "vulnerability",
            "punishment", "enfeeble", "temporalchains", "elementalweakness", "poachersmark",
            "warlordsmark", "assassinsmark", "snipersmark", "projectileweakness", "frostbite",
            "contagion", "bane", "wither", "frostbomb", "frostbom", "coldexposure", "exposure"
        };

        private static readonly string[] GuardKeywords =
        {
            "steelskin", "moltenshell", "immortalcall", "bonearmour", "arcanecloak",
            "frostshield", "defiancebanner"
        };

        private static readonly string[] WarcryKeywords =
        {
            "warcry", "shout", "enduringcry", "intimidatingcry", "rallyingcry",
            "seismiccry", "battlemagescry", "infernalcry", "generalcry", "ancestralcry"
        };

        private static readonly string[] MinionKeywords =
        {
            "summon", "raise", "animate", "golem", "skeleton", "zombie", "spectre",
            "ragingspirit", "reaper", "absolution", "heraldofpurity", "minion", "dominatingblow"
        };

        private static readonly string[] MovementKeywords =
        {
            "frostblink", "flamedash", "dash", "leapslam", "shieldcharge", "whirlingblades",
            "lightningwarp", "blinkarrow", "flickerstrike", "smokemine", "bodyswap",
            "chargeddash", "blink", "roll"
        };

        private static readonly string[] BuffKeywords =
        {
            "bloodrage", "witheringstep", "berserk", "phaserun", "righteousfire",
            "tempestshield", "grace", "determination", "discipline", "hatred", "anger",
            "wrath", "zealotry", "malevolence", "pride", "haste", "purity", "vitality",
            "clarity", "precision", "aura", "herald", "manatempest", "tempest"
        };

        /// <summary>
        /// Classifies a skill by name into one of the standard categories.
        /// </summary>
        public static string Classify(string skillName)
        {
            if (string.IsNullOrWhiteSpace(skillName))
            {
                return CategoryAttack;
            }

            string clean = skillName.Trim().ToLowerInvariant().Replace(" ", "").Replace("_", "").Replace("-", "");

            if (clean == "move" || clean == "walk") return CategoryMovement;

            foreach (var kw in MovementKeywords)
            {
                if (clean.Contains(kw)) return CategoryMovement;
            }

            foreach (var kw in TotemKeywords)
            {
                if (clean.Contains(kw)) return CategoryTotem;
            }

            foreach (var kw in GuardKeywords)
            {
                if (clean.Contains(kw)) return CategoryGuard;
            }

            foreach (var kw in WarcryKeywords)
            {
                if (clean.Contains(kw)) return CategoryWarcry;
            }

            foreach (var kw in CurseKeywords)
            {
                if (clean.Contains(kw)) return CategoryCurse;
            }

            foreach (var kw in MinionKeywords)
            {
                if (clean.Contains(kw)) return CategoryMinion;
            }

            foreach (var kw in BuffKeywords)
            {
                if (clean.Contains(kw)) return CategoryBuff;
            }

            return CategoryAttack;
        }

        /// <summary>
        /// Applies recommended preset parameters to a skill slot config based on category.
        /// </summary>
        public static void ApplyCategoryDefaults(SkillSlotConfig slot, string category)
        {
            slot.Category = category;

            switch (category)
            {
                case CategoryAttack:
                    slot.Role = SkillRole.EnemyTargeted;
                    slot.Priority = 1;
                    slot.MinCastIntervalMs = 200;
                    slot.HoldDurationMs = 150;
                    slot.TargetFilter = SkillTargetFilter.Any;
                    slot.OnlyOnLowHp = false;
                    slot.MinNearbyEnemies = 0;
                    slot.MaxTargetRange = 0f;
                    slot.OnlyWhenBuffMissing = false;
                    break;

                case CategoryTotem:
                    slot.Role = SkillRole.TotemOrMinion;
                    slot.Priority = 6;
                    slot.MinCastIntervalMs = 4000;
                    slot.HoldDurationMs = 150;
                    slot.TargetFilter = SkillTargetFilter.Any;
                    slot.OnlyOnLowHp = false;
                    slot.MinNearbyEnemies = 1;
                    slot.MaxTargetRange = 65f;
                    slot.MaxTotemCount = 1;
                    slot.OnlyWhenBuffMissing = false;
                    break;

                case CategoryCurse:
                    slot.Role = SkillRole.PackTargeted;
                    slot.Priority = 4;
                    slot.MinCastIntervalMs = 5000;
                    slot.HoldDurationMs = 150;
                    slot.TargetFilter = SkillTargetFilter.MagicOrAbove;
                    slot.OnlyOnLowHp = false;
                    slot.MinNearbyEnemies = 1;
                    slot.MaxTargetRange = 70f;
                    slot.OnlyWhenBuffMissing = true;
                    break;

                case CategoryGuard:
                    slot.Role = SkillRole.SelfBuffGuard;
                    slot.Priority = 9;
                    slot.MinCastIntervalMs = 4000;
                    slot.HoldDurationMs = 100;
                    slot.TargetFilter = SkillTargetFilter.Any;
                    slot.OnlyOnLowHp = true;
                    slot.LowHpThresholdPercent = 60f;
                    slot.MinNearbyEnemies = 0;
                    slot.MaxTargetRange = 0f;
                    slot.OnlyWhenBuffMissing = true;
                    break;

                case CategoryBuff:
                    slot.Role = SkillRole.SelfBuffGuard;
                    slot.Priority = 5;
                    slot.MinCastIntervalMs = 8000;
                    slot.HoldDurationMs = 100;
                    slot.TargetFilter = SkillTargetFilter.Any;
                    slot.OnlyOnLowHp = false;
                    slot.MinNearbyEnemies = 0;
                    slot.MaxTargetRange = 0f;
                    slot.OnlyWhenBuffMissing = true;
                    break;

                case CategoryWarcry:
                    slot.Role = SkillRole.SelfBuffGuard;
                    slot.Priority = 4;
                    slot.MinCastIntervalMs = 4000;
                    slot.HoldDurationMs = 100;
                    slot.TargetFilter = SkillTargetFilter.Any;
                    slot.OnlyOnLowHp = false;
                    slot.MinNearbyEnemies = 2;
                    slot.MaxTargetRange = 0f;
                    slot.OnlyWhenBuffMissing = false;
                    break;

                case CategoryMinion:
                    slot.Role = SkillRole.TotemOrMinion;
                    slot.Priority = 5;
                    slot.MinCastIntervalMs = 6000;
                    slot.HoldDurationMs = 150;
                    slot.TargetFilter = SkillTargetFilter.Any;
                    slot.OnlyOnLowHp = false;
                    slot.MinNearbyEnemies = 1;
                    slot.MaxTargetRange = 60f;
                    slot.MaxMinionCount = 3;
                    slot.OnlyWhenBuffMissing = false;
                    break;

                case CategoryMovement:
                    slot.Role = SkillRole.Disabled;
                    slot.Priority = 0;
                    slot.MinCastIntervalMs = 500;
                    slot.HoldDurationMs = 80;
                    slot.MaxTargetRange = 0f;
                    break;

                case CategoryCustom:
                default:
                    // Keep existing values
                    break;
            }
        }
    }
}
