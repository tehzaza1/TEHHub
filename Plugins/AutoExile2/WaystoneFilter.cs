// <copyright file="WaystoneFilter.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using TEHhub.RemoteObjects.States.InGameStateObjects;

    /// <summary>One blockable Waystone modifier family shared by runtime and Web UI metadata.</summary>
    public sealed record WaystoneModifierDefinition(string Key, string Label, string ModIdPrefix);

    /// <summary>Evaluates AutoExile2 Waystone thresholds and blocked modifier families.</summary>
    public static class WaystoneFilter
    {
        /// <summary>All known PoE2 Waystone modifier families. Unchecked means allowed.</summary>
        public static IReadOnlyList<WaystoneModifierDefinition> ModifierDefinitions { get; } =
            new WaystoneModifierDefinition[]
            {
                new("MapMonsterDamage", "Increased Monster Damage", "MapMonsterDamageIncrease"),
                new("MapMonsterLife", "More Monster Life", "MapMonsterLifeIncrease"),
                new("MapMonsterFast", "Monster Attack, Cast and Movement Speed", "MapMonsterSpeedIncrease"),
                new("MapMonstersElementalPenetration", "Monster Elemental Penetration", "MapMonstersElementalPenetration"),
                new("MapMonstersArmourBreak", "Monster Armour Break", "MapMonsterArmourBreak"),
                new("MapMonsterChaosDamage", "Extra Chaos Damage", "MapMonsterDamageAsChaos"),
                new("MapMonsterColdDamage", "Extra Cold Damage", "MapMonsterDamageAsCold"),
                new("MapMonsterFireDamage", "Extra Fire Damage", "MapMonsterDamageAsFire"),
                new("MapMonsterLightningDamage", "Extra Lightning Damage", "MapMonsterDamageAsLightning"),
                new("MapBleeding", "Monster Bleeding Chance", "MapMonsterBleeding"),
                new("MapPoisoning", "Monster Poison Chance", "MapMonsterPoisoning"),
                new("MapMonstersAccuracy", "Monster Accuracy", "MapMonsterAccuracy"),
                new("MapMonsterCriticalStrikesAndDamage", "Monster Critical Chance and Damage", "MapMonsterCritIncrease"),
                new("MapMonstersStunBuildup", "Monster Stun Buildup", "MapMonsterStunBuildup"),
                new("MapMonstersCurseEffectOnSelfFinal", "Reduced Curse Effect on Monsters", "MapMonstersCurseEffectOnSelf"),
                new("MapPlayerMaxResists", "Reduced Player Maximum Resistances", "MapPlayerMaximumResists"),
                new("MapBurningGround", "Burning Ground", "MapSpreadBurningGround"),
                new("MapChilledGround", "Chilled Ground", "MapSpreadChilledGround"),
                new("MapShockedGround", "Shocked Ground", "MapSpreadShockedGround"),
                new("MapMonstersAilmentChance", "Monster Elemental Ailment Chance", "MapMonsterElementAilmentChance"),
                new("MapMonstersAllResistances", "Monster Elemental Resistances", "MapMonsterElementalResistances"),
                new("MapMonstersArmoured", "Armoured Monsters", "MapMonsterArmoured"),
                new("MapMonstersEvasive", "Evasive Monsters", "MapMonsterEvasive"),
                new("MapMonstersEnergyShield", "Monster Extra Energy Shield", "MapMonsterEnergyShield"),
                new("MapMonstersStunAndAilmentThreshold", "Monster Stun and Ailment Threshold", "MapMonsterStunAilmentThreshold"),
                new("MapMonstersBaseSelfCriticalMultiplier", "Reduced Extra Damage from Critical Hits", "MapMonstersBaseSelfCriticalMultiplier"),
                new("MapPlayerElementalWeakness", "Periodic Elemental Weakness", "MapPlayerElementalWeakness"),
                new("MapPlayerEnfeeblement", "Periodic Enfeeble", "MapPlayerEnfeeble"),
                new("MapPlayerTemporalChains", "Periodic Temporal Chains", "MapPlayerTemporalChains"),
                new("MapPlayersGainReducedFlaskCharges", "Reduced Flask Charges Gained", "MapPlayerFlaskChargeGain"),
                new("MapPlayerCooldownRecovery", "Less Player Cooldown Recovery", "MapPlayerCooldownRecovery"),
                new("MapPlayerReducedRegen", "Less Life and Energy Shield Recovery", "MapPlayerRecoveryRate"),
            };

        /// <summary>Returns true when one Waystone satisfies every active filter.</summary>
        public static bool IsEligible(
            InventorySnapshotItem item,
            AutoExile2Settings settings,
            out string rejection)
        {
            if (!item.IsWaystone)
            {
                rejection = "not a Waystone";
                return false;
            }

            if (!PassStrict(settings.MinWaystoneItemRarity, item.WaystoneItemRarity))
            {
                rejection = "Item Rarity threshold";
                return false;
            }

            if (!PassStrict(settings.MinWaystonePackSize, item.WaystonePackSize))
            {
                rejection = "Pack Size threshold";
                return false;
            }

            if (!PassStrict(settings.MinWaystoneMonsterRarity, item.WaystoneMonsterRarity))
            {
                rejection = "Monster Rarity threshold";
                return false;
            }

            if (!PassStrict(settings.MinWaystoneMonsterEffectiveness, item.WaystoneMonsterEffectiveness))
            {
                rejection = "Monster Effectiveness threshold";
                return false;
            }

            if (!PassStrict(settings.MinWaystoneDropChance, item.WaystoneDropChance))
            {
                rejection = "Waystone Drop Chance threshold";
                return false;
            }

            var maxMods = Math.Clamp(settings.MaxWaystoneMods, 4, 6);
            if (item.ExplicitMods.Count > maxMods)
            {
                rejection = $"{item.ExplicitMods.Count} mods exceeds maximum {maxMods}";
                return false;
            }

            var blocked = settings.BlockedWaystoneMods ?? new List<string>();
            foreach (var mod in item.ExplicitMods)
            {
                var definition = ModifierDefinitions.FirstOrDefault(candidate =>
                    mod.Name.StartsWith(candidate.ModIdPrefix, StringComparison.OrdinalIgnoreCase));
                if (definition != null && blocked.Contains(definition.Key, StringComparer.OrdinalIgnoreCase))
                {
                    rejection = $"blocked mod: {definition.Label}";
                    return false;
                }

                if (blocked.Contains(mod.Name, StringComparer.OrdinalIgnoreCase))
                {
                    rejection = $"blocked mod: {mod.Name}";
                    return false;
                }
            }

            rejection = string.Empty;
            return true;
        }

        /// <summary>Removes unknown and duplicate modifier keys before settings are persisted.</summary>
        public static List<string> NormalizeBlockedMods(IEnumerable<string>? values)
        {
            var known = ModifierDefinitions.Select(entry => entry.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return (values ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value) && known.Contains(value.Trim()))
                .Select(value => ModifierDefinitions.First(entry =>
                    entry.Key.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase)).Key)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>Creates a stable signature so runtime restarts selection when filters change.</summary>
        public static string GetSettingsSignature(AutoExile2Settings settings) => string.Join(
            "|",
            new[]
            {
                settings.MinWaystoneItemRarity?.ToString() ?? string.Empty,
                settings.MinWaystonePackSize?.ToString() ?? string.Empty,
                settings.MinWaystoneMonsterRarity?.ToString() ?? string.Empty,
                settings.MinWaystoneMonsterEffectiveness?.ToString() ?? string.Empty,
                settings.MinWaystoneDropChance?.ToString() ?? string.Empty,
                Math.Clamp(settings.MaxWaystoneMods, 4, 6).ToString(),
                string.Join(',', NormalizeBlockedMods(settings.BlockedWaystoneMods).OrderBy(value => value)),
            });

        private static bool PassStrict(int? configuredMinimum, int? actual) =>
            !configuredMinimum.HasValue || (actual.HasValue && actual.Value > configuredMinimum.Value);
    }
}
