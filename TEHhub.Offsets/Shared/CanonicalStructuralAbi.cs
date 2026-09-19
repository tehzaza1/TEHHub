namespace TEHhub.Offsets.Shared;

using TEHhub.Offsets.Natives;
using TEHhub.Offsets.Objects.Components;
using TEHhub.Offsets.Objects.UiElement;

/// <summary>
/// Canonical structural ABI constants derived from native struct layouts and x64 execution environment rules.
/// These represent immutable properties of the compiled binary representation.
/// No values here are arbitrary magic numbers; all offsets and sizes are derived via <see cref="CanonicalLayout{T}"/>.
/// </summary>
public static class CanonicalStructuralAbi
{
    /// <summary>
    /// Lowest valid user-mode virtual memory address on 64-bit Windows (64 KiB allocation granularity floor).
    /// Prevents treating small integers, floats, or null page offsets as pointers.
    /// </summary>
    public const long MinValidUserModeAddress = 0x10000;

    /// <summary>
    /// Highest valid user-mode virtual memory address on 64-bit Windows (48-bit address space).
    /// </summary>
    public const long MaxValidUserModeAddress = 0x7FFFFFFFFFFF;

    /// <summary>
    /// Standard x64 pointer alignment in bytes.
    /// </summary>
    public const int PointerAlignment = 8;

    /// <summary>
    /// Standard x64 pointer size in bytes.
    /// </summary>
    public const int PointerSize = 8;

    /// <summary>
    /// MSVC std::vector header size (24 bytes: First, Last, End pointers).
    /// Derived directly from the canonical native struct layout <see cref="StdVector"/>.
    /// </summary>
    public static int StdVectorHeaderSize => CanonicalLayout<StdVector>.Size;

    /// <summary>
    /// Atlas connection edge stride (20 bytes: 5 32-bit integers, Pack = 1).
    /// Derived directly from the canonical native struct layout <see cref="AtlasConnectionEdge"/>.
    /// Distinct from <see cref="StdVectorHeaderSize"/> (24 bytes).
    /// </summary>
    public static int AtlasConnectionEdgeSize => CanonicalLayout<AtlasConnectionEdge>.Size;

    /// <summary>
    /// Offset of Self pointer in <see cref="UiElementBaseOffset"/> (0x008).
    /// Derived from the canonical layout definition.
    /// </summary>
    public static int UiElementBaseSelfOffset => CanonicalLayout<UiElementBaseOffset>.OffsetOf(nameof(UiElementBaseOffset.Self));

    /// <summary>
    /// Offset of Flags in <see cref="UiElementBaseOffset"/> (0x168).
    /// Derived from the canonical layout definition.
    /// </summary>
    public static int UiElementBaseFlagsOffset => CanonicalLayout<UiElementBaseOffset>.OffsetOf(nameof(UiElementBaseOffset.Flags));

    /// <summary>
    /// Offset of ChildrensPtr in <see cref="UiElementBaseOffset"/> (0x010).
    /// Derived from the canonical layout definition.
    /// </summary>
    public static int UiElementBaseChildrensPtrOffset => CanonicalLayout<UiElementBaseOffset>.OffsetOf(nameof(UiElementBaseOffset.ChildrensPtr));

    /// <summary>
    /// Offset of ParentPtr in <see cref="UiElementBaseOffset"/> (0x0B8).
    /// Derived from the canonical layout definition.
    /// </summary>
    public static int UiElementBaseParentPtrOffset => CanonicalLayout<UiElementBaseOffset>.OffsetOf(nameof(UiElementBaseOffset.ParentPtr));

    /// <summary>
    /// Offset of EntityPtr in <see cref="ComponentHeader"/> (0x008).
    /// Derived from the canonical layout definition.
    /// </summary>
    public static int ComponentHeaderEntityPtrOffset => CanonicalLayout<ComponentHeader>.OffsetOf(nameof(ComponentHeader.EntityPtr));

    /// <summary>
    /// Offset of StaticPtr in <see cref="ComponentHeader"/> (0x000).
    /// Derived from the canonical layout definition.
    /// </summary>
    public static int ComponentHeaderStaticPtrOffset => CanonicalLayout<ComponentHeader>.OffsetOf(nameof(ComponentHeader.StaticPtr));
}
