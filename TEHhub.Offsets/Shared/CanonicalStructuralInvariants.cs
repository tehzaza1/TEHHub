namespace TEHhub.Offsets.Shared;

using System;
using TEHhub.Offsets.Natives;

/// <summary>
/// Provides read-only structural validation predicates for low-level game engine memory primitives.
/// Used for verifying pointer validity, container layout integrity (StdVector, StdMap),
/// UI self-reference invariants, and component ownership chains.
/// 
/// ARCHITECTURAL RULES:
/// 1. Strictly read-only: No memory writes, no mutation interfaces, no offset application.
/// 2. Evidence only: Returns <see cref="SharedValidationResult"/> describing structural integrity;
///    never decides Recovery terminal outcomes or candidate rankings.
/// 3. Versioned semantic heuristics are separated from compiler/ABI structural layout rules.
/// 4. Historical offsets are never accepted as ground truth.
/// </summary>
public static class CanonicalStructuralInvariants
{
    // =========================================================================
    // Pointer / Address Structural Invariants
    // =========================================================================

    /// <summary>
    /// Checks whether an address falls within the valid x64 user-mode address range.
    /// </summary>
    public static bool IsCanonicalPointer(
        long address,
        long min = CanonicalStructuralAbi.MinValidUserModeAddress,
        long max = CanonicalStructuralAbi.MaxValidUserModeAddress)
    {
        return address >= min && address <= max;
    }

    /// <summary>
    /// Checks whether an address falls within the valid x64 user-mode address range.
    /// </summary>
    public static bool IsCanonicalPointer(
        IntPtr address,
        long min = CanonicalStructuralAbi.MinValidUserModeAddress,
        long max = CanonicalStructuralAbi.MaxValidUserModeAddress)
    {
        return IsCanonicalPointer(address.ToInt64(), min, max);
    }

    /// <summary>
    /// Validates that an address is a plausible, non-null user-mode pointer.
    /// </summary>
    public static SharedValidationResult ValidatePointer(
        long address,
        string predicateId = "pointer-canonical",
        long min = CanonicalStructuralAbi.MinValidUserModeAddress,
        long max = CanonicalStructuralAbi.MaxValidUserModeAddress)
    {
        if (address == 0)
        {
            return SharedValidationResult.Fail(predicateId, "Null pointer", 0);
        }

        if (address < min)
        {
            return SharedValidationResult.Fail(predicateId, $"Address 0x{address:X} below user-mode floor 0x{min:X}", address);
        }

        if (address > max)
        {
            return SharedValidationResult.Fail(predicateId, $"Address 0x{address:X} above user-mode ceiling 0x{max:X}", address);
        }

        return SharedValidationResult.Pass(predicateId, $"Valid canonical pointer 0x{address:X}", address);
    }

    /// <summary>
    /// Validates that an address is a plausible, non-null user-mode pointer.
    /// </summary>
    public static SharedValidationResult ValidatePointer(
        IntPtr address,
        string predicateId = "pointer-canonical",
        long min = CanonicalStructuralAbi.MinValidUserModeAddress,
        long max = CanonicalStructuralAbi.MaxValidUserModeAddress)
    {
        return ValidatePointer(address.ToInt64(), predicateId, min, max);
    }

    /// <summary>
    /// Validates that an address satisfies pointer alignment constraints.
    /// </summary>
    public static SharedValidationResult ValidatePointerAlignment(
        long address,
        int alignment = CanonicalStructuralAbi.PointerAlignment,
        string predicateId = "pointer-aligned")
    {
        if (address == 0)
        {
            return SharedValidationResult.Fail(predicateId, "Null pointer", 0);
        }

        if (alignment <= 0 || (address % alignment) != 0)
        {
            return SharedValidationResult.Fail(predicateId, $"Address 0x{address:X} is not aligned to {alignment} bytes", address);
        }

        return SharedValidationResult.Pass(predicateId, $"Address 0x{address:X} aligned to {alignment} bytes", address);
    }

    // =========================================================================
    // StdVector Structural Invariants
    // =========================================================================

    /// <summary>
    /// Validates MSVC std::vector structural invariants:
    /// 1. Null/empty vector: First == Last == End == 0 (Valid).
    /// 2. Pointer ordering: First &lt;= Last &lt;= End.
    /// 3. Pointer ranges: First, Last, and End (if non-zero) must be canonical pointers.
    /// 4. Divisibility: If elementSize &gt; 0, both byteSpan and capSpan must be divisible by elementSize.
    /// </summary>
    public static SharedValidationResult ValidateStdVector(
        long first,
        long last,
        long end,
        int elementSize = 0,
        string predicateId = "stdvector-shape",
        long minAddress = CanonicalStructuralAbi.MinValidUserModeAddress,
        long maxAddress = CanonicalStructuralAbi.MaxValidUserModeAddress)
    {
        // Case 1: Null vector (all zero pointers) is structurally valid empty
        if (first == 0 && last == 0 && end == 0)
        {
            return SharedValidationResult.Pass(predicateId, "Empty StdVector (null pointers)", 0);
        }

        // Case 2: Invariant ordering First <= Last <= End
        if (first > last)
        {
            return SharedValidationResult.Fail(predicateId, $"StdVector First (0x{first:X}) exceeds Last (0x{last:X})", last - first);
        }

        if (last > end)
        {
            return SharedValidationResult.Fail(predicateId, $"StdVector Last (0x{last:X}) exceeds End (0x{end:X})", end - last);
        }

        // Case 3: Pointer range sanity
        if (!IsCanonicalPointer(first, minAddress, maxAddress))
        {
            return SharedValidationResult.Fail(predicateId, $"StdVector First (0x{first:X}) is outside canonical user-mode range", first);
        }

        if (!IsCanonicalPointer(last, minAddress, maxAddress))
        {
            return SharedValidationResult.Fail(predicateId, $"StdVector Last (0x{last:X}) is outside canonical user-mode range", last);
        }

        if (end != 0 && !IsCanonicalPointer(end, minAddress, maxAddress))
        {
            return SharedValidationResult.Fail(predicateId, $"StdVector End (0x{end:X}) is outside canonical user-mode range", end);
        }

        // Case 4: Element size arithmetic and divisibility
        var byteSpan = last - first;
        var capSpan = end - first;

        if (elementSize > 0)
        {
            if (byteSpan % elementSize != 0)
            {
                return SharedValidationResult.Fail(predicateId, $"StdVector byte span {byteSpan} not divisible by element size {elementSize}", byteSpan);
            }

            if (end != 0 && capSpan % elementSize != 0)
            {
                return SharedValidationResult.Fail(predicateId, $"StdVector capacity span {capSpan} not divisible by element size {elementSize}", capSpan);
            }

            var count = byteSpan / elementSize;
            return SharedValidationResult.Pass(predicateId, $"Valid StdVector (count={count}, elemSize={elementSize})", count);
        }

        return SharedValidationResult.Pass(predicateId, $"Valid StdVector (bytes={byteSpan})", byteSpan);
    }

    /// <summary>
    /// Validates MSVC std::vector structural invariants from a <see cref="StdVector"/> native struct.
    /// </summary>
    public static SharedValidationResult ValidateStdVector(
        StdVector vector,
        int elementSize = 0,
        string predicateId = "stdvector-shape")
    {
        return ValidateStdVector(
            vector.First.ToInt64(),
            vector.Last.ToInt64(),
            vector.End.ToInt64(),
            elementSize,
            predicateId);
    }

    // =========================================================================
    // StdMap Structural Invariants
    // =========================================================================

    /// <summary>
    /// Validates MSVC std::map / std::_Tree structural invariants:
    /// 1. Bounded size [0, maxMapSize].
    /// 2. Head pointer must be canonical.
    /// 3. Sentinel node must have IsNil != 0 and Color &lt;= 1.
    /// 4. If size &gt; 0, root pointer (if checked) must be canonical and root node must have IsNil == 0 and Color &lt;= 1.
    /// </summary>
    public static SharedValidationResult ValidateStdMap(
        long head,
        int size,
        byte sentinelIsNil,
        byte sentinelColor,
        long root = 0,
        byte? rootIsNil = null,
        byte? rootColor = null,
        int maxMapSize = CanonicalVersionedSemantics.MaxMapSize,
        string predicateId = "stdmap-shape")
    {
        if (size < 0 || size > maxMapSize)
        {
            return SharedValidationResult.Fail(predicateId, $"StdMap size {size} is out of bounds [0, {maxMapSize}]", size);
        }

        if (!IsCanonicalPointer(head))
        {
            return SharedValidationResult.Fail(predicateId, $"StdMap head pointer 0x{head:X} is not canonical", head);
        }

        if (sentinelIsNil == 0)
        {
            return SharedValidationResult.Fail(predicateId, "StdMap sentinel IsNil byte is 0 (expected != 0)", sentinelIsNil);
        }

        if (sentinelColor > 1)
        {
            return SharedValidationResult.Fail(predicateId, $"StdMap sentinel Color byte {sentinelColor} is invalid (expected 0 or 1)", sentinelColor);
        }

        if (size == 0)
        {
            return SharedValidationResult.Pass(predicateId, "Empty StdMap (valid sentinel)", 0);
        }

        // Non-empty map checks
        if (root != 0 && !IsCanonicalPointer(root))
        {
            return SharedValidationResult.Fail(predicateId, $"StdMap root pointer 0x{root:X} is not canonical", root);
        }

        if (rootIsNil.HasValue && rootIsNil.Value != 0)
        {
            return SharedValidationResult.Fail(predicateId, $"StdMap root node IsNil byte is {rootIsNil.Value} (expected 0)", rootIsNil.Value);
        }

        if (rootColor.HasValue && rootColor.Value > 1)
        {
            return SharedValidationResult.Fail(predicateId, $"StdMap root node Color byte {rootColor.Value} is invalid (expected 0 or 1)", rootColor.Value);
        }

        return SharedValidationResult.Pass(predicateId, $"Valid StdMap (size={size}, head=0x{head:X})", size);
    }

    // =========================================================================
    // UiElementBase Structural Invariants
    // =========================================================================

    /// <summary>
    /// Validates that a UiElement's Self pointer exactly equals its own virtual memory address.
    /// This is the primary anchor proving an address is a genuine UiElementBase instance.
    /// </summary>
    public static SharedValidationResult ValidateUiElementSelf(
        long selfPtr,
        long elementAddress,
        string predicateId = "uielement-self")
    {
        if (elementAddress == 0)
        {
            return SharedValidationResult.Fail(predicateId, "UiElement address is null", 0);
        }

        if (!IsCanonicalPointer(elementAddress))
        {
            return SharedValidationResult.Fail(predicateId, $"UiElement address 0x{elementAddress:X} is not canonical", elementAddress);
        }

        if (selfPtr != elementAddress)
        {
            return SharedValidationResult.Fail(predicateId, $"UiElement Self (0x{selfPtr:X}) != Address (0x{elementAddress:X})", selfPtr);
        }

        return SharedValidationResult.Pass(predicateId, $"UiElement Self matches address 0x{elementAddress:X}", elementAddress);
    }

    /// <summary>
    /// Validates that a UiElement's Self pointer exactly equals its own virtual memory address.
    /// </summary>
    public static SharedValidationResult ValidateUiElementSelf(
        IntPtr selfPtr,
        IntPtr elementAddress,
        string predicateId = "uielement-self")
    {
        return ValidateUiElementSelf(selfPtr.ToInt64(), elementAddress.ToInt64(), predicateId);
    }

    /// <summary>
    /// Checks if a UI element is visible based on the canonical visibility bit mask.
    /// </summary>
    public static bool IsUiVisible(uint flags, uint mask = CanonicalVersionedSemantics.UiVisibleMask)
    {
        return (flags & mask) != 0;
    }

    /// <summary>
    /// Returns the UI element flags with the visibility bit masked out.
    /// </summary>
    public static uint GetMaskedUiFlags(uint flags, uint mask = CanonicalVersionedSemantics.UiVisibleMask)
    {
        return flags & ~mask;
    }

    // =========================================================================
    // Component Header Owner Invariants
    // =========================================================================

    /// <summary>
    /// Validates that a component's EntityPtr (back-pointer) matches the expected owning entity's address.
    /// This proves the component belongs to the designated entity.
    /// </summary>
    public static SharedValidationResult ValidateComponentOwner(
        long entityPtr,
        long expectedOwnerAddress,
        string predicateId = "component-owner")
    {
        if (expectedOwnerAddress == 0)
        {
            return SharedValidationResult.Fail(predicateId, "Expected owner address is null", 0);
        }

        if (!IsCanonicalPointer(expectedOwnerAddress))
        {
            return SharedValidationResult.Fail(predicateId, $"Expected owner address 0x{expectedOwnerAddress:X} is not canonical", expectedOwnerAddress);
        }

        if (entityPtr != expectedOwnerAddress)
        {
            return SharedValidationResult.Fail(predicateId, $"Component EntityPtr (0x{entityPtr:X}) != Owner (0x{expectedOwnerAddress:X})", entityPtr);
        }

        return SharedValidationResult.Pass(predicateId, $"Component EntityPtr matches owner 0x{expectedOwnerAddress:X}", expectedOwnerAddress);
    }

    /// <summary>
    /// Validates that a component's EntityPtr (back-pointer) matches the expected owning entity's address.
    /// </summary>
    public static SharedValidationResult ValidateComponentOwner(
        IntPtr entityPtr,
        IntPtr expectedOwnerAddress,
        string predicateId = "component-owner")
    {
        return ValidateComponentOwner(entityPtr.ToInt64(), expectedOwnerAddress.ToInt64(), predicateId);
    }
}
