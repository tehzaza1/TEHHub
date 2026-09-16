// <copyright file="AreaMod.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace TEHhub.RemoteObjects.States.InGameStateObjects
{
    using System;

    /// <summary>
    ///     Represents an active modifier in the current world area or map instance.
    /// </summary>
    public sealed class AreaMod
    {
        /// <summary>
        ///     Initializes a new instance of the <see cref="AreaMod" /> class.
        /// </summary>
        /// <param name="rawName">Raw internal identifier/path from Mods.dat.</param>
        /// <param name="displayName">Display name of the modifier.</param>
        /// <param name="values">Rolled numeric values (value0, value1).</param>
        /// <param name="modRecordPtr">Pointer to the Mods.dat record row.</param>
        public AreaMod(string rawName, string displayName, (float value0, float value1) values, IntPtr modRecordPtr)
        {
            this.RawName = rawName ?? string.Empty;
            this.DisplayName = string.IsNullOrWhiteSpace(displayName) ? this.RawName : displayName;
            this.Values = values;
            this.ModRecordPtr = modRecordPtr;
        }

        /// <summary>
        ///     Gets the internal raw identifier/path of the mod (from Mods.dat).
        /// </summary>
        public string RawName { get; }

        /// <summary>
        ///     Gets a human-friendly display name of the mod.
        /// </summary>
        public string DisplayName { get; }

        /// <summary>
        ///     Gets the numeric rolled values (min/max or val0/val1) of the modifier.
        /// </summary>
        public (float Value0, float Value1) Values { get; }

        /// <summary>
        ///     Gets the native memory pointer to the Mods.dat record row.
        /// </summary>
        public IntPtr ModRecordPtr { get; }

        /// <inheritdoc />
        public override string ToString()
        {
            if (float.IsNaN(this.Values.Value0))
            {
                return this.DisplayName;
            }

            if (float.IsNaN(this.Values.Value1))
            {
                return $"{this.DisplayName} ({this.Values.Value0})";
            }

            return $"{this.DisplayName} ({this.Values.Value0} - {this.Values.Value1})";
        }
    }
}
