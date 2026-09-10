// <copyright file="Wait.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>


namespace AutoHotKeyTrigger.ProfileManager.Component
{
    using AutoHotKeyTrigger;
    using ImGuiNET;
    using System.Text.Json.Serialization;
    using System.Diagnostics;
    using System.Numerics;

    /// <summary>
    ///     Adds wait to the condition.
    /// </summary>
    public class Wait : IComponent
    {
        [JsonInclude] private float duration;
        private Stopwatch sw;

        /// <summary>
        ///     Initializes a new instance of the <see cref="Wait" /> class.
        /// </summary>
        /// <param name="duration">duration in seconds to wait for.</param>
        [JsonConstructor]
        public Wait(float duration)
        {
            this.duration = duration;
            this.sw = new();
        }

        /// <inheritdoc/>
        public IComponent Clone()
        {
            return new Wait(this.duration);
        }

        /// <inheritdoc/>
        public void Display(bool expand)
        {
            if (expand)
            {
                ImGui.Text(AhkText.T("component.wait.prefix", "Condition WAITS for"));
                ImGui.SameLine();
                ImGui.DragFloat(AhkText.Label("component.wait.seconds", "(seconds)", "WAITCOMPONENT"), ref this.duration, 0.05f, 0.0f, 5f);
            }
            else
            {
                ImGui.SameLine();
                ImGui.Text(AhkText.T("component.wait.for", "for"));
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(255, 255, 0, 255), $"{this.duration}");
                ImGui.SameLine();
                ImGui.Text(AhkText.T("component.wait.seconds_suffix", "seconds."));
            }
        }

        /// <inheritdoc/>
        public bool execute(bool isConditionValid)
        {
            if (isConditionValid)
            {
                if (!this.sw.IsRunning)
                {
                    this.sw.Start();
                }

                if (this.sw.ElapsedMilliseconds >= (this.duration * 1000f))
                {
                    return true;
                }
            }
            else
            {
                this.sw.Reset();
            }

            return false;
        }
    }
}
