// <copyright file="Render.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace TEHhub.RemoteObjects.Components
{
    using System;
    using TEHhub.Offsets.Natives;
    using TEHhub.Offsets.Objects.Components;
    using TEHhub.Offsets.Objects.States.InGameState;
    using ImGuiNET;

    /// <summary>
    ///     The <see cref="Render" /> component in the entity.
    /// </summary>
    public class Render : ComponentBase
    {
        private static readonly float WorldToGridRatio =
            TileStructure.TileToWorldConversion / TileStructure.TileToGridConversion;

        // Keep X and Y together in one atomic 64-bit value. The old reference-type snapshot
        // avoided torn coordinate pairs, but allocated one object for every entity refresh.
        private long gridSnapBits;

        /// <summary>
        ///     Initializes a new instance of the <see cref="Render" /> class.
        /// </summary>
        /// <param name="address">address of the <see cref="Render" /> component.</param>
        public Render(IntPtr address)
            : base(address) { }

        /// <summary>
        ///     Gets the position where entity is located on the grid (map).
        ///     Returns a per-call snapshot — atomic with respect to UpdateData.
        ///     Z is always 0 (the underlying field is 2D).
        /// </summary>
        public StdTuple3D<float> GridPosition
        {
            get
            {
                var packed = System.Threading.Interlocked.Read(ref this.gridSnapBits);
                return new StdTuple3D<float>
                {
                    X = BitConverter.Int32BitsToSingle((int)(packed >> 32)),
                    Y = BitConverter.Int32BitsToSingle((int)packed),
                    Z = 0f,
                };
            }
        }

        /// <summary>
        ///     Gets the position where entity is located on the grid (map).
        /// </summary>
        public StdTuple3D<float> ModelBounds { get; private set; }

        /// <summary>
        ///     Gets the position where entity is rendered in the game world.
        ///     NOTE: Z-Axis is pointing to the (visible/invisible) healthbar.
        /// </summary>
        public StdTuple3D<float> WorldPosition { get; private set; }

        /// <summary>
        ///     Gets the terrain height on which the Entity is standing.
        /// </summary>
        public float TerrainHeight { get; private set; }

        /// <summary>
        ///     Converts the <see cref="Render" /> class data to ImGui.
        /// </summary>
        internal override void ToImGui()
        {
            base.ToImGui();
            ImGui.Text($"Grid Position: {this.GridPosition}");
            ImGui.Text($"World Position: {this.WorldPosition}");
            ImGui.Text($"Terrain Height (Z-Axis): {this.TerrainHeight}");
            ImGui.Text($"Model Bounds: {this.ModelBounds}");
        }

        /// <inheritdoc />
        protected override void UpdateData(bool hasAddressChanged)
        {
            var reader = Core.Process.Handle;
            var data = reader.ReadMemory<RenderOffsets>(this.Address);
            this.OwnerEntityAddress = data.Header.EntityPtr;
            this.WorldPosition = data.CurrentWorldPosition;
            this.ModelBounds = data.CharactorModelBounds;
            this.TerrainHeight = (float)Math.Round(data.TerrainHeight, 4);

            var newX = data.CurrentWorldPosition.X / WorldToGridRatio;
            var newY = data.CurrentWorldPosition.Y / WorldToGridRatio;
            var packed = ((long)(uint)BitConverter.SingleToInt32Bits(newX) << 32) |
                         (uint)BitConverter.SingleToInt32Bits(newY);
            System.Threading.Interlocked.Exchange(ref this.gridSnapBits, packed);
        }
    }
}
