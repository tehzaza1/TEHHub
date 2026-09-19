namespace TEHhub.Offsets.Shared;

/// <summary>
/// Versioned semantic invariants and game-specific heuristics.
/// Note: These values reflect specific game versions or patches and are NOT eternal structural ABI truths.
/// They must be kept strictly separated from binary layout invariants.
/// Historical offsets are never accepted as ground truth.
/// </summary>
public static class CanonicalVersionedSemantics
{
    /// <summary>
    /// Runesmithing combinations count for PoE2 V1 (321 recipes).
    /// </summary>
    public const int RuneshapeRecipeCountV1 = 321;

    /// <summary>
    /// UI element visibility bit mask in Flags (bit 11 = 0x800).
    /// </summary>
    public const uint UiVisibleMask = 0x800;

    /// <summary>
    /// Raw UI element flags fingerprint for Runeshape panel including visible bit.
    /// </summary>
    public const uint RawRuneshapeFingerprintV1 = 0x22800;

    /// <summary>
    /// Masked UI element structural fingerprint for Runeshape panel (0x22000).
    /// </summary>
    public static uint MaskedRuneshapeFingerprint => RawRuneshapeFingerprintV1 & ~UiVisibleMask;

    /// <summary>
    /// Atlas Map node raw fingerprint (0x22800).
    /// </summary>
    public const uint AtlasMapNodeFp = 0x22800;

    /// <summary>
    /// Atlas Mist node raw fingerprint (0x22800).
    /// </summary>
    public const uint AtlasMistNodeFp = 0x22800;

    /// <summary>
    /// Atlas Map node masked fingerprint (0x22000).
    /// </summary>
    public static uint MaskedAtlasMapNodeFp => AtlasMapNodeFp & ~UiVisibleMask;

    /// <summary>
    /// Atlas Mist node masked fingerprint (0x22000).
    /// </summary>
    public static uint MaskedAtlasMistNodeFp => AtlasMistNodeFp & ~UiVisibleMask;

    /// <summary>
    /// Default maximum plausible size for an in-memory StdMap tree traversal.
    /// </summary>
    public const int MaxMapSize = 1_000_000;
}
