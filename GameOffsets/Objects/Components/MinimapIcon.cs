namespace GameOffsets.Objects.Components
{
    using System;
    using System.Runtime.InteropServices;

    /// <summary>
    ///     Memory layout for the PoE 2 MinimapIcon entity component.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Pack = 1)]
    public struct MinimapIconOffsets
    {
        [FieldOffset(0x000)] public ComponentHeader Header;

        /// <summary>
        ///     Pointer to the MinimapIcons.dat row. Its first pointer references the UTF-16 name.
        /// </summary>
        [FieldOffset(0x020)] public IntPtr MinimapIconDatRowPtr;
    }
}
