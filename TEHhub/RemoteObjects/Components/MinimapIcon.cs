// <copyright file="MinimapIcon.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace TEHhub.RemoteObjects.Components
{
    using System;
    using System.Buffers;
    using TEHhub.Offsets.Objects.Components;
    using ImGuiNET;

    /// <summary>
    ///     The <see cref="MinimapIcon" /> component in the entity.
    /// </summary>
    public class MinimapIcon : ComponentBase
    {
        /// <summary>
        ///     Initializes a new instance of the <see cref="MinimapIcon" /> class.
        /// </summary>
        /// <param name="address">address of the <see cref="MinimapIcon" /> component.</param>
        public MinimapIcon(IntPtr address)
            : base(address) { }

        /// <summary>
        ///     The icon name from MinimapIcons.dat (e.g. "RewardChestExpedition").
        /// </summary>
        public string? IconName { get; private set; }

        /// <summary>
        ///     Gets the state/flag of the minimap icon (0 = active/visible, != 0 = hidden/completed).
        /// </summary>
        public int State { get; private set; }

        /// <summary>
        ///     Gets a value indicating whether the minimap icon is hidden or completed.
        /// </summary>
        public bool IsHide => this.State != 0;

        // MinimapIcon requires per-frame refresh to track dynamic changes to State (e.g. icon hidden upon completion).
        internal override bool RequiresPerFrameRefresh => true;

        /// <inheritdoc/>
        internal override void ToImGui()
        {
            base.ToImGui();
            ImGui.Text($"Icon Name: {this.IconName ?? "(none)"}");
            ImGui.Text($"State: {this.State} (IsHide: {this.IsHide})");
        }

        private string? TryReadUtf16String(IntPtr address)
        {
            const int bufferLength = 512;
            var bytes = ArrayPool<byte>.Shared.Rent(bufferLength);
            try
            {
                var reader = Core.Process.Handle;
                if (!reader.TryReadMemoryArray(address, bytes, bufferLength, out _))
                {
                    return null;
                }

                for (var i = 0; i < bufferLength - 1; i += 2)
                {
                    if (bytes[i] == 0 && bytes[i + 1] == 0)
                    {
                        if (i == 0) return null;
                        return System.Text.Encoding.Unicode.GetString(bytes, 0, i);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MinimapIcon.TryReadUtf16String] {address.ToInt64():X}: {ex.Message}");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(bytes);
            }

            return null;
        }

        /// <inheritdoc/>
        protected override void UpdateData(bool hasAddressChanged)
        {
            var reader = Core.Process.Handle;
            var data = reader.ReadMemory<MinimapIconOffsets>(this.Address);
            this.OwnerEntityAddress = data.Header.EntityPtr;
            this.State = data.State;

            // Read icon name once or on component address change: offset 0x20 -> dat row pointer -> +0x00 -> UTF-16 string
            if (this.IconName == null || hasAddressChanged)
            {
                try
                {
                    var datRowPtr = data.MinimapIconDatRowPtr;
                    if (datRowPtr != IntPtr.Zero && (long)datRowPtr > 0x10000)
                    {
                        var namePtr = reader.ReadMemory<IntPtr>(datRowPtr);
                        if (namePtr != IntPtr.Zero && (long)namePtr > 0x10000)
                        {
                            this.IconName = this.TryReadUtf16String(namePtr);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MinimapIcon.UpdateData] {this.Address.ToInt64():X}: {ex.Message}");
                }
            }
        }
    }
}
