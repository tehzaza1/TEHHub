// <copyright file="BuffDictionary.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace AutoHotKeyTrigger.ProfileManager.DynamicConditions
{
    using System.Collections.Generic;
    using GameOffsets.Objects.Components;
    using AutoHotKeyTrigger.ProfileManager.DynamicConditions.Interface;

    /// <summary>
    ///     Describes a set of buffs applied to the player
    /// </summary>
    public class BuffDictionary : IBuffDictionary
    {
        private readonly IReadOnlyDictionary<string, StatusEffectStruct> source;

        /// <summary>
        ///     Creates a new instance
        /// </summary>
        /// <param name="source">Source data for the buffs</param>
        public BuffDictionary(IReadOnlyDictionary<string, StatusEffectStruct> source)
        {
            this.source = source;
        }

        /// <summary>
        ///     Returns a buff description
        /// </summary>
        /// <param name="id">The buff id</param>
        public IStatusEffect this[string id]
        {
            get
            {
                if (this.source.TryGetValue(id, out var value))
                {
                    return new StatusEffect(true, value.TimeLeft, value.TotalTime, value.Charges, value.Effectiveness);
                }

                foreach (var kv in this.source)
                {
                    if (kv.Key.StartsWith(id + "_", System.StringComparison.OrdinalIgnoreCase))
                    {
                        var val = kv.Value;
                        return new StatusEffect(true, val.TimeLeft, val.TotalTime, val.Charges, val.Effectiveness);
                    }
                }

                return new StatusEffect(false, 0, 0, 0, 0);
            }
        }

        /// <summary>
        ///     Checks whether the buff is present
        /// </summary>
        /// <param name="id">The buff id</param>
        public bool Has(string id)
        {
            if (this.source.ContainsKey(id))
            {
                return true;
            }

            foreach (var k in this.source.Keys)
            {
                if (k.StartsWith(id + "_", System.StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
