// <copyright file="Player.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace TEHhub.RemoteObjects.Components
{
    using System;
    using TEHhub.Offsets.Objects.Components;
    using ImGuiNET;

    /// <summary>
    ///     The <see cref="Chest" /> component in the entity.
    /// </summary>
    public class Player : ComponentBase
    {
        // A Player component can be discovered before its name string is populated. Retry until
        // it resolves, but rate-limit empty reads so a delayed buffer does not add a per-frame cost.
        private DateTime nextNameReadUtc = DateTime.MinValue;

        /// <summary>
        ///     Initializes a new instance of the <see cref="Player" /> class.
        /// </summary>
        /// <param name="address">address of the <see cref="Chest" /> component.</param>
        public Player(IntPtr address)
            : base(address) { }

        /// <summary>
        ///     Gets the name of the player.
        /// </summary>
        public string Name { get; private set; } = string.Empty;

        /// <summary>
        ///     Gets the Xp of the player.
        /// </summary>
        public int Xp { get; private set; }

        /// <summary>
        ///     Gets the Level of the player.
        /// </summary>
        public int Level { get; private set; }

        /// <summary>
        ///     Converts the <see cref="Player" /> class data to ImGui.
        /// </summary>
        internal override void ToImGui()
        {
            base.ToImGui();
            ImGui.Text($"Player Name: {this.Name}");
            ImGui.Text($"Xp: {this.Xp}");
            ImGui.Text($"Level: {this.Level}");
        }

        /// <inheritdoc />
        protected override void UpdateData(bool hasAddressChanged)
        {
            var reader = Core.Process.Handle;
            var data = reader.ReadMemory<PlayerOffsets>(this.Address);
            this.OwnerEntityAddress = data.Header.EntityPtr;

            if (hasAddressChanged)
            {
                this.Name = string.Empty;
                this.nextNameReadUtc = DateTime.MinValue;
            }

            var now = DateTime.UtcNow;
            if (string.IsNullOrWhiteSpace(this.Name) && now >= this.nextNameReadUtc)
            {
                this.nextNameReadUtc = now.AddMilliseconds(500);
                try
                {
                    var name = reader.ReadStdWString(data.Name);
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        this.Name = name;
                    }
                }
                catch
                {
                    // The component may arrive before its string buffer is readable. The throttled
                    // retry rate handles that case without preventing XP/level refreshes.
                }
            }

            this.Xp = data.Xp;
            this.Level = data.Level;
        }
    }
}
