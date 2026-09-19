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
    /// Traceable to Od144RuneshapePanelRecovery.RecipeCountV1.
    /// </summary>
    public const int RuneshapeRecipeCountV1 = 321;

    /// <summary>
    /// Metadata classification for RuneshapeRecipeCountV1 as a versioned semantic invariant.
    /// </summary>
    public static readonly CanonicalInvariantMetadata RuneshapeRecipeCountMetadata = new(
        nameof(RuneshapeRecipeCountV1),
        RuneshapeRecipeCountV1,
        StructuralInvariantClassification.VERSIONED_SEMANTIC_INVARIANT,
        "PoE2 V1 Runesmithing combinations recipe count");

    /// <summary>
    /// UI element visibility bit mask in Flags (bit 11 = 0x800).
    /// Traceable to UiElementBaseOffset.IS_VISIBLE_BINARY_POS (0x0B) and Od144RuneshapePanelRecovery.VisibleMask.
    ///
    /// CLASSIFICATION NOTE:
    /// - FIELD LAYOUT: The byte offset of UiElementBaseOffset.Flags (0x168) is a STRUCTURAL_ABI_INVARIANT.
    /// - BIT MEANING: The semantic interpretation that bit 11 (0x800) means 'Visible' is an engine/versioned
    ///   semantic invariant (VERSIONED_SEMANTIC_INVARIANT).
    /// </summary>
    public const uint UiVisibleMask = 0x800;

    /// <summary>
    /// Metadata classification for UiVisibleMask as a versioned engine semantic invariant.
    /// </summary>
    public static readonly CanonicalInvariantMetadata UiVisibleMaskMetadata = new(
        nameof(UiVisibleMask),
        UiVisibleMask,
        StructuralInvariantClassification.VERSIONED_SEMANTIC_INVARIANT,
        "Engine semantic interpretation of bit 11 in UiElementBaseOffset.Flags as Visible");

    /// <summary>
    /// Raw UI element flags fingerprint for Runeshape panel including visible bit (0x00462EF1).
    /// Traceable to Od144RuneshapePanelRecovery.RawFingerprintV1.
    /// </summary>
    public const uint RawRuneshapeFingerprintV1 = 0x00462EF1;

    /// <summary>
    /// Metadata classification for RawRuneshapeFingerprintV1 as a versioned semantic invariant.
    /// </summary>
    public static readonly CanonicalInvariantMetadata RawRuneshapeFingerprintMetadata = new(
        nameof(RawRuneshapeFingerprintV1),
        RawRuneshapeFingerprintV1,
        StructuralInvariantClassification.VERSIONED_SEMANTIC_INVARIANT,
        "Raw Runeshape combinations panel UI flags fingerprint including visibility bit");

    /// <summary>
    /// Masked UI element structural fingerprint for Runeshape panel (0x004626F1).
    /// Derived from RawRuneshapeFingerprintV1 with UiVisibleMask masked out.
    /// Traceable to Od144RuneshapePanelRecovery.MaskedFingerprint.
    /// </summary>
    public static uint MaskedRuneshapeFingerprint => RawRuneshapeFingerprintV1 & ~UiVisibleMask;

    /// <summary>
    /// Atlas Map node raw fingerprint (0x542EF3).
    /// Traceable to ImportantUiElements.AtlasMapNodeFp and Od145AtlasLayoutRecovery.AtlasMapNodeFp.
    /// </summary>
    public const uint AtlasMapNodeFp = 0x542EF3;

    /// <summary>
    /// Metadata classification for AtlasMapNodeFp as a versioned semantic invariant.
    /// </summary>
    public static readonly CanonicalInvariantMetadata AtlasMapNodeFpMetadata = new(
        nameof(AtlasMapNodeFp),
        AtlasMapNodeFp,
        StructuralInvariantClassification.VERSIONED_SEMANTIC_INVARIANT,
        "Atlas Map node raw UI flags fingerprint");

    /// <summary>
    /// Atlas Mist node raw fingerprint (0x442EF3).
    /// Traceable to ImportantUiElements.AtlasMistNodeFp and Od145AtlasLayoutRecovery.AtlasMistNodeFp.
    /// </summary>
    public const uint AtlasMistNodeFp = 0x442EF3;

    /// <summary>
    /// Metadata classification for AtlasMistNodeFp as a versioned semantic invariant.
    /// </summary>
    public static readonly CanonicalInvariantMetadata AtlasMistNodeFpMetadata = new(
        nameof(AtlasMistNodeFp),
        AtlasMistNodeFp,
        StructuralInvariantClassification.VERSIONED_SEMANTIC_INVARIANT,
        "Atlas Mist node raw UI flags fingerprint");

    /// <summary>
    /// Atlas Map node masked fingerprint (0x5426F3).
    /// Derived from AtlasMapNodeFp with UiVisibleMask masked out.
    /// Traceable to Od145AtlasLayoutRecovery.MaskedAtlasMapNodeFp.
    /// </summary>
    public static uint MaskedAtlasMapNodeFp => AtlasMapNodeFp & ~UiVisibleMask;

    /// <summary>
    /// Atlas Mist node masked fingerprint (0x4426F3).
    /// Derived from AtlasMistNodeFp with UiVisibleMask masked out.
    /// Traceable to Od145AtlasLayoutRecovery.MaskedAtlasMistNodeFp.
    /// </summary>
    public static uint MaskedAtlasMistNodeFp => AtlasMistNodeFp & ~UiVisibleMask;

    /// <summary>
    /// Default maximum plausible size for an in-memory StdMap tree traversal.
    /// Traceable to OffsetHelperEngine.MaxMapSize.
    /// Classified as a SAFETY_BUDGET traversal bound to prevent unbounded loops in memory reading.
    /// </summary>
    public const int MaxMapSize = 1_000_000;

    /// <summary>
    /// Metadata classification for MaxMapSize as a safety budget traversal bound.
    /// </summary>
    public static readonly CanonicalInvariantMetadata MaxMapSizeMetadata = new(
        nameof(MaxMapSize),
        MaxMapSize,
        StructuralInvariantClassification.SAFETY_BUDGET,
        "Defensive safety budget limit for std::map tree traversal");
}
