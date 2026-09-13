namespace TEHhub.Offsets.Objects.UiElement
{
    using System;
    using System.Runtime.InteropServices;
    using Natives;

    /// <summary>
    /// Layout facts used by the Runeshape Combinations UI wrapper.
    /// The panel is discovered by its live UI tree; these are relative field
    /// offsets only and are not an absolute root path.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Pack = 1)]
    public struct RuneshapeCombinationsUiOffsets
    {
        public const int ChildrenVectorOffset = 0x10;
        public const int ParentOffset = 0xB8;
        public const int FlagsOffset = 0x168;
        public const int SizeOffset = 0x270;

        [FieldOffset(ChildrenVectorOffset)] public StdVector Children;
        [FieldOffset(ParentOffset)] public IntPtr Parent;
        [FieldOffset(FlagsOffset)] public uint Flags;
        [FieldOffset(SizeOffset)] public StdTuple2D<float> Size;
    }
}
