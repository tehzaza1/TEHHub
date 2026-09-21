// <copyright file="Mods.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace TEHhub.RemoteObjects.Components
{
    using System;
    using System.Collections.Generic;
    using TEHhub.RemoteEnums;
    using TEHhub.Offsets.Objects.Components;
    using ImGuiNET;
    using TEHhub.Utils;

    /// <summary>
    ///     The <see cref="Mods" /> component in the entity.
    /// </summary>
    public class Mods : ComponentBase
    {
        /// <summary>
        ///     Initializes a new instance of the <see cref="Mods" /> class.
        /// </summary>
        /// <param name="address">address of the <see cref="Mods" /> component.</param>
        public Mods(IntPtr address)
            : base(address) { }

        /// <summary>
        ///     Gets a value indicating item rarity information.
        /// </summary>
        public Rarity Rarity { get; private set; } = Rarity.Normal;

        /// <summary>
        ///     Gets a diagnostic hex capture around the item-state bytes between rarity and the
        ///     modifier vectors. This is research-only until the PoE2 identification flag is
        ///     validated from a before/after capture; automation must not interpret these bytes.
        /// </summary>
        public string ItemStateProbe { get; private set; } = string.Empty;

        /// <summary>
        ///     Gets the mods and their values of the entity.
        ///     If a mod doesn't have a value, it will be represented by
        ///     <see cref="float.NaN"/>.
        /// </summary>
        public List<(string name, (float value0, float value1) values)>
            ImplicitMods = new(),
            ExplicitMods = new(),
            EnchantMods = new(),
            HellscapeMods = new();

        /// <summary>
        ///     Gets human-readable display text for each entry in <see cref="ExplicitMods"/>.
        ///     For Waystone items, each string is the translated mod text (e.g. "[P] Monsters deal 17% of Damage as Extra Chaos").
        ///     For non-Waystone items, each string is the raw mod name.
        ///     Index-aligned 1:1 with <see cref="ExplicitMods"/>.
        /// </summary>
        public List<string> ExplicitModsDisplay = new();

        /// <summary>
        ///     Gets the aggregate stats contributed by this item's modifiers. This is intentionally
        ///     kept separate from the modifier rows so Waystone summaries can use Stats.dat ids
        ///     without repeatedly decoding every modifier.
        /// </summary>
        public Dictionary<GameStats, int> ModStats = new();

        /// <summary>Gets a value indicating whether this component represents a PoE2 Waystone.</summary>
        public bool IsWaystone =>
            this.ModStats.ContainsKey(GameStats.map_unique_item_drop_chance_positive_percentage) ||
            this.ModStats.ContainsKey(GameStats.map_pack_size_positive_percentage_final_from_map);

        /// <summary>Gets the Waystone Revives Available count (6 - explicit mods count), or null if not a waystone.</summary>
        public int? WaystoneRevives =>
            this.IsWaystone ? Math.Max(0, 6 - this.ExplicitMods.Count) : null;

        /// <summary>Gets the Waystone Item Rarity percentage from aggregate ModStats, or null if absent.</summary>
        public int? WaystoneItemRarity =>
            this.ModStats.TryGetValue(GameStats.map_pack_size_positive_percentage_final_from_map, out var v) ? v : null;

        /// <summary>Gets the Waystone Pack Size percentage from aggregate ModStats, or null if absent.</summary>
        public int? WaystonePackSize =>
            this.ModStats.TryGetValue(GameStats.map_number_of_magic_and_rare_packs_positive_percentage_final_and_rare_monster_modifiers_chance_positive_percentage_final_from_map, out var v) ? v : null;

        /// <summary>Gets the Waystone Monster Rarity percentage from aggregate ModStats, or null if absent.</summary>
        public int? WaystoneMonsterRarity =>
            this.ModStats.TryGetValue(GameStats.map_monster_potency_positive_percentage_final_from_map, out var v) ? v : null;

        /// <summary>Gets the Waystone Monster Effectiveness percentage from aggregate ModStats, or null if absent.</summary>
        public int? WaystoneMonsterEffectiveness =>
            this.ModStats.TryGetValue(GameStats.map_map_item_drop_chance_positive_percentage_final_from_map, out var v) ? v : null;

        /// <summary>Gets the Waystone Drop Chance percentage from aggregate ModStats, or null if absent.</summary>
        public int? WaystoneDropChance =>
            this.ModStats.TryGetValue(GameStats.map_unique_item_drop_chance_positive_percentage, out var v) ? v : null;

        /// <summary>
        ///     Converts the <see cref="Mods" /> class data to ImGui.
        /// </summary>
        internal override void ToImGui()
        {
            base.ToImGui();
            ImGui.Text($"Rarity: {this.Rarity}");
            ImGuiHelper.DisplayTextAndCopyOnClick(
                $"Item-state probe [0x90..0x9F]: {this.ItemStateProbe}",
                this.ItemStateProbe);
            ObjectMagicProperties.ModsToImGui("ImplicitMods", this.ImplicitMods);
            ObjectMagicProperties.ModsToImGui("ExplicitMods", this.ExplicitMods);
            ObjectMagicProperties.ModsToImGui("EnchantMods", this.EnchantMods);
            ObjectMagicProperties.ModsToImGui("HellscapeMods", this.HellscapeMods);
            if (ImGui.TreeNode($"Explicit Mod Display ({this.ExplicitModsDisplay.Count})"))
            {
                foreach (var displayText in this.ExplicitModsDisplay)
                {
                    ImGuiHelper.DisplayTextAndCopyOnClick(displayText, displayText);
                }

                ImGui.TreePop();
            }

            ImGuiHelper.StatsWidget(this.ModStats, "Stats from Mods");
        }

        /// <inheritdoc />
        protected override void UpdateData(bool hasAddressChanged)
        {
            var reader = Core.Process.Handle;
            var data = reader.ReadMemory<ModsOffsets>(this.Address);
            this.OwnerEntityAddress = data.Header.EntityPtr;
            this.Rarity = (Rarity)data.Details0.Rarity;
            this.ItemStateProbe = Convert.ToHexString(
                reader.ReadMemoryArray<byte>(this.Address + 0x90, 0x10));

            if (hasAddressChanged)
            {
                this.ImplicitMods.Clear();
                this.ExplicitMods.Clear();
                this.EnchantMods.Clear();
                this.HellscapeMods.Clear();
                ObjectMagicProperties.AddToMods(this.ImplicitMods, reader.ReadStdVector<ModArrayStruct>(data.Details0.Mods.ImplicitMods));
                ObjectMagicProperties.AddToMods(this.ExplicitMods, reader.ReadStdVector<ModArrayStruct>(data.Details0.Mods.ExplicitMods));
                ObjectMagicProperties.AddToMods(this.EnchantMods, reader.ReadStdVector<ModArrayStruct>(data.Details0.Mods.EnchantMods));
                ObjectMagicProperties.AddToMods(this.HellscapeMods, reader.ReadStdVector<ModArrayStruct>(data.Details0.Mods.HellscapeMods));
                ObjectMagicProperties.AddToMods(this.HellscapeMods, reader.ReadStdVector<ModArrayStruct>(data.Details0.Mods.CrucibleMods));
                base.StatUpdator(this.ModStats, data.Details0.StatsFromMods);

                // Populate ExplicitModsDisplay after ModStats is known (IsWaystone depends on ModStats)
                this.ExplicitModsDisplay.Clear();
                var isWaystone = this.IsWaystone;
                foreach (var (name, (v0, v1)) in this.ExplicitMods)
                {
                    this.ExplicitModsDisplay.Add(
                        isWaystone
                            ? WaystoneModTranslator.Translate(name, v0, v1)
                            : name);
                }
            }
        }
    }
}
