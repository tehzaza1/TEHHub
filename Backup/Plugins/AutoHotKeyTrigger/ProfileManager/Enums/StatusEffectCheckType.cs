// <copyright file="StatusEffectCheckType.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace AutoHotKeyTrigger.ProfileManager.Enums
{

    /// <summary>
    ///     Check type for the condition
    /// </summary>
    public enum StatusEffectCheckType
    {
        /// <summary>
        ///     Check remaining buff duration
        /// </summary>
        TimeLeft,

        /// <summary>
        ///     Check remaning buff duration in percent
        /// </summary>
        PercentTimeLeft,

        /// <summary>
        ///     Check buff charges
        /// </summary>
        Charges
    }
}
