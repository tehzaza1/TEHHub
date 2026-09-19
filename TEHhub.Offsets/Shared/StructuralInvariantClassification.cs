namespace TEHhub.Offsets.Shared;

/// <summary>
/// Categorizes invariants to distinguish structural ABI realities from game-versioned semantic heuristics,
/// safety traversal budgets, and discovery bounds.
/// </summary>
public enum StructuralInvariantClassification
{
    /// <summary>
    /// Fixed compiler/ABI characteristics such as struct sizes, pointer alignment, and native header layouts.
    /// </summary>
    STRUCTURAL_ABI_INVARIANT,

    /// <summary>
    /// Game-versioned heuristics such as recipe counts, UI fingerprints, or league-specific entity counts.
    /// Note: Versioned semantic invariants are not eternal truth.
    /// </summary>
    VERSIONED_SEMANTIC_INVARIANT,

    /// <summary>
    /// Defensive limits on loop iterations, scan bytes, and recursion depth to prevent hangs or runaway traversal.
    /// </summary>
    SAFETY_BUDGET,

    /// <summary>
    /// Spatial search windows, offset radii, and step boundaries used during candidate discovery.
    /// </summary>
    DISCOVERY_BOUND,

    /// <summary>
    /// Heuristic acceptance thresholds for scoring or statistical verification.
    /// </summary>
    VALIDATION_THRESHOLD
}

/// <summary>
/// Read-only metadata container for classifying architectural constants.
/// Historical offsets are never accepted as ground truth.
/// </summary>
public sealed record CanonicalInvariantMetadata(
    string Name,
    object Value,
    StructuralInvariantClassification Classification,
    string Description);
