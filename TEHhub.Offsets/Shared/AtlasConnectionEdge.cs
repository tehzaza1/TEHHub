namespace TEHhub.Offsets.Shared;

using System.Runtime.InteropServices;

/// <summary>
/// Represents the packed 20-byte connection edge entry within the Atlas graph canvas.
/// This is an immutable native layout struct (Pack = 1, 5 32-bit integers).
///
/// ARCHITECTURAL PROVENANCE NOTE:
/// Equivalent legacy definitions exist in:
/// 1. TEHhub.RemoteObjects.States.InGameStateObjects.ImportantUiElements (private struct AtlasNodeConnectionEdgeOffsets)
/// 2. TEHhub.OffsetDoctor.RecoveryV1.Od145AtlasLayoutRecovery (public struct AtlasConnectionEdge)
///
/// All three definitions share the exact same Pack=1, 20-byte native layout.
/// This type in TEHhub.Offsets.Shared serves as the canonical shared candidate at the lowest architectural layer.
/// Consolidation of consumer call sites is DEFERRED_TO_PHASE_B/C.
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
