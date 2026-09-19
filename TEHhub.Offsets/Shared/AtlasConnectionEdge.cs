namespace TEHhub.Offsets.Shared;

using System.Runtime.InteropServices;

/// <summary>
/// Represents the packed 20-byte connection edge entry within the Atlas graph canvas.
/// This is an immutable native layout struct (Pack = 1, 5 32-bit integers).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct AtlasConnectionEdge
{
    public int Unknown;
    public int SourceX;
    public int SourceY;
    public int TargetX;
    public int TargetY;
}
