namespace TEHhub.Offsets.Shared;

/// <summary>
/// Verdict of a shared structural validation predicate.
/// Decoupled from Recovery decision logic (contains no PROPOSED, candidate ranking, or terminal status).
/// </summary>
public enum SharedValidationVerdict
{
    /// <summary>
    /// The structural invariant holds.
    /// </summary>
    Pass,

    /// <summary>
    /// The structural invariant is broken.
    /// </summary>
    Fail,

    /// <summary>
    /// The state cannot be determined (e.g. unreadable, uninitialized, or insufficient evidence).
    /// </summary>
    Unknown
}

/// <summary>
/// Small, lightweight, allocation-free readonly struct capturing the outcome of a structural invariant check.
/// Provides predicates and evidence only — does not decide Recovery terminals or accept historical offsets as truth.
/// Strictly read-only; has no memory write or state mutation capability.
/// </summary>
public readonly record struct SharedValidationResult(
    SharedValidationVerdict Verdict,
    string PredicateId,
    string Detail,
    long ObservedValue = 0)
{
    /// <summary>
    /// Returns true if the verdict is Pass.
    /// </summary>
    public bool IsValid => this.Verdict == SharedValidationVerdict.Pass;

    /// <summary>
    /// Creates a passing validation result.
    /// </summary>
    public static SharedValidationResult Pass(string predicateId, string detail, long observed = 0) =>
        new(SharedValidationVerdict.Pass, predicateId, detail, observed);

    /// <summary>
    /// Creates a failing validation result.
    /// </summary>
    public static SharedValidationResult Fail(string predicateId, string detail, long observed = 0) =>
        new(SharedValidationVerdict.Fail, predicateId, detail, observed);

    /// <summary>
    /// Creates an unknown/indeterminate validation result.
    /// </summary>
    public static SharedValidationResult Unknown(string predicateId, string detail, long observed = 0) =>
        new(SharedValidationVerdict.Unknown, predicateId, detail, observed);
}
