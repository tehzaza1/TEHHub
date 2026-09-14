namespace TEHhub.Offsets.Objects.Components
{
    using System.Runtime.InteropServices;

    [StructLayout(LayoutKind.Explicit, Pack = 1)]
    public struct TargetableOffsets
    {
        // found this function by checking whats accessing 0x52.
        // 0: First check is on Entity -> IsValid offset (i.e greater than zero).
        // Shifted by +0x18 in PoE 2 (0x51 -> 0x69)
        [FieldOffset(0x00)] public ComponentHeader Header;
        [FieldOffset(0x69)] public bool IsTargetable; // 1 -> True
        [FieldOffset(0x6A)] public bool IsHighlightable; // Non-Highlightable things can be targetted.
        [FieldOffset(0x6B)] public bool IsTargettedByPlayer;
    }
}
