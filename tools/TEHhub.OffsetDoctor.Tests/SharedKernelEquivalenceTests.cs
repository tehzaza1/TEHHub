namespace TEHhub.OffsetDoctor.Tests;

using System;
using System.Collections.Generic;
using TEHhub.Offsets.Natives;
using TEHhub.Offsets.Objects.Components;
using TEHhub.Offsets.Objects.UiElement;
using TEHhub.Offsets.Shared;
using TEHhub.OffsetDoctor.RecoveryV1;

/// <summary>
/// Phase A convergence test suite verifying:
/// 1. Shared Kernel primitives (CanonicalLayout, CanonicalStructuralInvariants, CanonicalStructuralAbi, CanonicalVersionedSemantics).
/// 2. Structural ABI vs Versioned Semantic separation.
/// 3. Old OH-style logic vs New Shared Kernel predicate equivalence harness.
/// 4. Zero memory write / read-only safety guarantees.
/// </summary>
public static class SharedKernelEquivalenceTests
{
    public static void RunAll(Action<bool, string> check)
    {
        Console.WriteLine("[Shared Kernel] Running Phase A Shared Kernel & Equivalence Tests...");

        TestCanonicalLayout(check);
        TestAbiVsSemanticSeparation(check);
        TestStdVectorInvariants(check);
        TestStdMapInvariants(check);
        TestUiElementInvariants(check);
        TestComponentOwnerInvariants(check);
        TestPointerInvariants(check);
        TestOldVsNewEquivalenceHarness(check);

        Console.WriteLine("[Shared Kernel] All Phase A tests completed successfully.");
    }

    private static void TestCanonicalLayout(Action<bool, string> check)
    {
        // UiElementBaseOffset
        check(CanonicalLayout<UiElementBaseOffset>.OffsetOf("Self") == 0x008,
            "CanonicalLayout<UiElementBaseOffset> Self offset must equal 0x008.");
        check(CanonicalLayout<UiElementBaseOffset>.OffsetOf("Flags") == 0x168,
            "CanonicalLayout<UiElementBaseOffset> Flags offset must equal 0x168.");
        check(CanonicalLayout<UiElementBaseOffset>.OffsetOf("ChildrensPtr") == 0x010,
            "CanonicalLayout<UiElementBaseOffset> ChildrensPtr offset must equal 0x010.");
        check(CanonicalLayout<UiElementBaseOffset>.OffsetOf("ParentPtr") == 0x0B8,
            "CanonicalLayout<UiElementBaseOffset> ParentPtr offset must equal 0x0B8.");

        // ComponentHeader
        check(CanonicalLayout<ComponentHeader>.OffsetOf("StaticPtr") == 0x000,
            "CanonicalLayout<ComponentHeader> StaticPtr offset must equal 0x000.");
        check(CanonicalLayout<ComponentHeader>.OffsetOf("EntityPtr") == 0x008,
            "CanonicalLayout<ComponentHeader> EntityPtr offset must equal 0x008.");

        // StdVector
        check(CanonicalLayout<StdVector>.Size == 24,
            "CanonicalLayout<StdVector> Size must equal 24 bytes.");
        check(CanonicalLayout<StdVector>.OffsetOf("First") == 0x000,
            "CanonicalLayout<StdVector> First offset must equal 0x000.");
        check(CanonicalLayout<StdVector>.OffsetOf("Last") == 0x008,
            "CanonicalLayout<StdVector> Last offset must equal 0x008.");
        check(CanonicalLayout<StdVector>.OffsetOf("End") == 0x010,
            "CanonicalLayout<StdVector> End offset must equal 0x010.");

        // AtlasConnectionEdge
        check(CanonicalLayout<AtlasConnectionEdge>.Size == 20,
            "CanonicalLayout<AtlasConnectionEdge> Size must equal 20 bytes (Pack=1).");
        check(CanonicalLayout<AtlasConnectionEdge>.OffsetOf("Unknown") == 0x00,
            "CanonicalLayout<AtlasConnectionEdge> Unknown offset must equal 0x00.");
        check(CanonicalLayout<AtlasConnectionEdge>.OffsetOf("SourceX") == 0x04,
            "CanonicalLayout<AtlasConnectionEdge> SourceX offset must equal 0x04.");
        check(CanonicalLayout<AtlasConnectionEdge>.OffsetOf("SourceY") == 0x08,
            "CanonicalLayout<AtlasConnectionEdge> SourceY offset must equal 0x08.");
        check(CanonicalLayout<AtlasConnectionEdge>.OffsetOf("TargetX") == 0x0C,
            "CanonicalLayout<AtlasConnectionEdge> TargetX offset must equal 0x0C.");
        check(CanonicalLayout<AtlasConnectionEdge>.OffsetOf("TargetY") == 0x10,
            "CanonicalLayout<AtlasConnectionEdge> TargetY offset must equal 0x10.");

        // TryGetOffsetOf and caching
        check(CanonicalLayout<UiElementBaseOffset>.TryGetOffsetOf("Self", out var cachedSelf) && cachedSelf == 0x008,
            "TryGetOffsetOf must return true with valid cached offset.");
        check(!CanonicalLayout<UiElementBaseOffset>.TryGetOffsetOf("NonExistentField", out var nonExistent) && nonExistent == -1,
            "TryGetOffsetOf for invalid field must return false with -1.");

        // FieldOffsets dictionary
        var uiFieldOffsets = CanonicalLayout<UiElementBaseOffset>.FieldOffsets;
        check(uiFieldOffsets.ContainsKey("Self") && uiFieldOffsets["Self"] == 0x008,
            "FieldOffsets map must contain Self at 0x008.");
        check(uiFieldOffsets.ContainsKey("Flags") && uiFieldOffsets["Flags"] == 0x168,
            "FieldOffsets map must contain Flags at 0x168.");
    }

    private static void TestAbiVsSemanticSeparation(Action<bool, string> check)
    {
        // ABI constants derived from layouts
        check(CanonicalStructuralAbi.StdVectorHeaderSize == 24,
            "CanonicalStructuralAbi.StdVectorHeaderSize must derive 24 from CanonicalLayout<StdVector>.");
        check(CanonicalStructuralAbi.AtlasConnectionEdgeSize == 20,
            "CanonicalStructuralAbi.AtlasConnectionEdgeSize must derive 20 from CanonicalLayout<AtlasConnectionEdge>.");
        check(CanonicalStructuralAbi.StdVectorHeaderSize != CanonicalStructuralAbi.AtlasConnectionEdgeSize,
            "StdVector header size (24) and Atlas edge stride (20) must remain distinct concepts.");

        check(CanonicalStructuralAbi.UiElementBaseSelfOffset == 0x008,
            "CanonicalStructuralAbi.UiElementBaseSelfOffset must derive 0x008.");
        check(CanonicalStructuralAbi.UiElementBaseFlagsOffset == 0x168,
            "CanonicalStructuralAbi.UiElementBaseFlagsOffset must derive 0x168.");
        check(CanonicalStructuralAbi.ComponentHeaderEntityPtrOffset == 0x008,
            "CanonicalStructuralAbi.ComponentHeaderEntityPtrOffset must derive 0x008.");

        // Versioned Semantics - Provenance Verification
        check(CanonicalVersionedSemantics.RuneshapeRecipeCountV1 == 321,
            "CanonicalVersionedSemantics.RuneshapeRecipeCountV1 must be 321.");
        check(CanonicalVersionedSemantics.RuneshapeRecipeCountV1 == Od144RuneshapePanelRecovery.RecipeCountV1,
            "CanonicalVersionedSemantics.RuneshapeRecipeCountV1 must match Od144RuneshapePanelRecovery.RecipeCountV1.");

        check(CanonicalVersionedSemantics.UiVisibleMask == 0x800,
            "CanonicalVersionedSemantics.UiVisibleMask must be 0x800 (bit 11).");
        check(CanonicalVersionedSemantics.UiVisibleMask == Od144RuneshapePanelRecovery.VisibleMask,
            "CanonicalVersionedSemantics.UiVisibleMask must match Od144RuneshapePanelRecovery.VisibleMask.");

        // Runeshape Fingerprint Provenance: Raw == 0x00462EF1, Masked == 0x004626F1
        check(CanonicalVersionedSemantics.RawRuneshapeFingerprintV1 == 0x00462EF1,
            "CanonicalVersionedSemantics.RawRuneshapeFingerprintV1 must be 0x00462EF1.");
        check(CanonicalVersionedSemantics.RawRuneshapeFingerprintV1 == Od144RuneshapePanelRecovery.RawFingerprintV1,
            "CanonicalVersionedSemantics.RawRuneshapeFingerprintV1 must match Od144RuneshapePanelRecovery.RawFingerprintV1.");

        check(CanonicalVersionedSemantics.MaskedRuneshapeFingerprint == 0x004626F1,
            "CanonicalVersionedSemantics.MaskedRuneshapeFingerprint must be 0x004626F1.");
        check(CanonicalVersionedSemantics.MaskedRuneshapeFingerprint == Od144RuneshapePanelRecovery.MaskedFingerprint,
            "CanonicalVersionedSemantics.MaskedRuneshapeFingerprint must match Od144RuneshapePanelRecovery.MaskedFingerprint.");

        // Atlas Fingerprint Provenance: Raw == 0x542EF3, Masked == 0x5426F3
        check(CanonicalVersionedSemantics.AtlasMapNodeFp == 0x542EF3,
            "CanonicalVersionedSemantics.AtlasMapNodeFp must be 0x542EF3.");
        check(CanonicalVersionedSemantics.AtlasMapNodeFp == Od145AtlasLayoutRecovery.AtlasMapNodeFp,
            "CanonicalVersionedSemantics.AtlasMapNodeFp must match Od145AtlasLayoutRecovery.AtlasMapNodeFp.");

        check(CanonicalVersionedSemantics.AtlasMistNodeFp == 0x442EF3,
            "CanonicalVersionedSemantics.AtlasMistNodeFp must be 0x442EF3.");
        check(CanonicalVersionedSemantics.AtlasMistNodeFp == Od145AtlasLayoutRecovery.AtlasMistNodeFp,
            "CanonicalVersionedSemantics.AtlasMistNodeFp must match Od145AtlasLayoutRecovery.AtlasMistNodeFp.");

        check(CanonicalVersionedSemantics.MaskedAtlasMapNodeFp == 0x5426F3,
            "CanonicalVersionedSemantics.MaskedAtlasMapNodeFp must be 0x5426F3.");
        check(CanonicalVersionedSemantics.MaskedAtlasMapNodeFp == Od145AtlasLayoutRecovery.MaskedAtlasMapNodeFp,
            "CanonicalVersionedSemantics.MaskedAtlasMapNodeFp must match Od145AtlasLayoutRecovery.MaskedAtlasMapNodeFp.");

        check(CanonicalVersionedSemantics.MaskedAtlasMistNodeFp == 0x4426F3,
            "CanonicalVersionedSemantics.MaskedAtlasMistNodeFp must be 0x4426F3.");
        check(CanonicalVersionedSemantics.MaskedAtlasMistNodeFp == Od145AtlasLayoutRecovery.MaskedAtlasMistNodeFp,
            "CanonicalVersionedSemantics.MaskedAtlasMistNodeFp must match Od145AtlasLayoutRecovery.MaskedAtlasMistNodeFp.");

        // Derivation checks
        check(CanonicalVersionedSemantics.MaskedRuneshapeFingerprint ==
              (CanonicalVersionedSemantics.RawRuneshapeFingerprintV1 & ~CanonicalVersionedSemantics.UiVisibleMask),
            "MaskedRuneshapeFingerprint must derive from RawRuneshapeFingerprintV1 & ~UiVisibleMask.");
        check(CanonicalVersionedSemantics.MaskedAtlasMapNodeFp ==
              (CanonicalVersionedSemantics.AtlasMapNodeFp & ~CanonicalVersionedSemantics.UiVisibleMask),
            "MaskedAtlasMapNodeFp must derive from AtlasMapNodeFp & ~UiVisibleMask.");
        check(CanonicalVersionedSemantics.MaskedAtlasMistNodeFp ==
              (CanonicalVersionedSemantics.AtlasMistNodeFp & ~CanonicalVersionedSemantics.UiVisibleMask),
            "MaskedAtlasMistNodeFp must derive from AtlasMistNodeFp & ~UiVisibleMask.");

        // MaxMapSize and Safety Budget Classification
        check(CanonicalVersionedSemantics.MaxMapSize == 1_000_000,
            "CanonicalVersionedSemantics.MaxMapSize must be 1,000,000.");
        check(CanonicalVersionedSemantics.MaxMapSizeMetadata.Classification == StructuralInvariantClassification.SAFETY_BUDGET,
            "MaxMapSize must be classified as SAFETY_BUDGET.");

        // UI Visibility Meaning vs Flags Layout Classification
        check(CanonicalVersionedSemantics.UiVisibleMaskMetadata.Classification == StructuralInvariantClassification.VERSIONED_SEMANTIC_INVARIANT,
            "UiVisibleMask bit meaning must be classified as VERSIONED_SEMANTIC_INVARIANT.");
        check(CanonicalStructuralAbi.UiElementBaseFlagsOffset == 0x168,
            "UiElementBaseFlagsOffset must be 0x168 (structural field offset).");

        // Atlas edge representation equivalence (Pack=1, Size=20, field-compatible)
        check(CanonicalLayout<TEHhub.Offsets.Shared.AtlasConnectionEdge>.Size == 20,
            "TEHhub.Offsets.Shared.AtlasConnectionEdge size must be 20.");
        check(System.Runtime.InteropServices.Marshal.SizeOf<Od145AtlasLayoutRecovery.AtlasConnectionEdge>() == 20,
            "Od145AtlasLayoutRecovery.AtlasConnectionEdge size must be 20.");
        check(CanonicalLayout<TEHhub.Offsets.Shared.AtlasConnectionEdge>.Size ==
              System.Runtime.InteropServices.Marshal.SizeOf<Od145AtlasLayoutRecovery.AtlasConnectionEdge>(),
            "Shared and Recovery AtlasConnectionEdge layouts must have identical 20-byte size.");
        check(typeof(TEHhub.Offsets.Shared.AtlasConnectionEdge).StructLayoutAttribute?.Pack == 1,
            "Shared AtlasConnectionEdge must have Pack = 1.");
        check(typeof(Od145AtlasLayoutRecovery.AtlasConnectionEdge).StructLayoutAttribute?.Pack == 1,
            "Recovery AtlasConnectionEdge must have Pack = 1.");

        // Metadata classification record
        var abiMeta = new CanonicalInvariantMetadata(
            "AtlasConnectionEdgeSize",
            CanonicalStructuralAbi.AtlasConnectionEdgeSize,
            StructuralInvariantClassification.STRUCTURAL_ABI_INVARIANT,
            "Packed native edge stride");
        check(abiMeta.Classification == StructuralInvariantClassification.STRUCTURAL_ABI_INVARIANT,
            "Classification must preserve STRUCTURAL_ABI_INVARIANT.");

        var semMeta = new CanonicalInvariantMetadata(
            "RuneshapeRecipeCountV1",
            CanonicalVersionedSemantics.RuneshapeRecipeCountV1,
            StructuralInvariantClassification.VERSIONED_SEMANTIC_INVARIANT,
            "V1 recipes count");
        check(semMeta.Classification == StructuralInvariantClassification.VERSIONED_SEMANTIC_INVARIANT,
            "Classification must preserve VERSIONED_SEMANTIC_INVARIANT.");
    }

    private static void TestStdVectorInvariants(Action<bool, string> check)
    {
        // 1. Null empty vector
        var nullResult = CanonicalStructuralInvariants.ValidateStdVector(0, 0, 0);
        check(nullResult.IsValid && nullResult.Verdict == SharedValidationVerdict.Pass,
            "StdVector with null pointers (0,0,0) must pass as valid empty vector.");

        // 2. Valid empty vector where First == Last != 0
        var emptyResult = CanonicalStructuralInvariants.ValidateStdVector(0x20000, 0x20000, 0x20100);
        check(emptyResult.IsValid && emptyResult.ObservedValue == 0,
            "StdVector where First == Last != 0 must pass as valid empty vector.");

        // 3. Valid non-empty vector without element size
        var validResult = CanonicalStructuralInvariants.ValidateStdVector(0x20000, 0x20050, 0x20100);
        check(validResult.IsValid && validResult.ObservedValue == 0x50,
            "Valid StdVector without element size must pass with observed byte length.");

        // 4. Disordered First > Last
        var disorderedFirst = CanonicalStructuralInvariants.ValidateStdVector(0x20060, 0x20050, 0x20100);
        check(!disorderedFirst.IsValid && disorderedFirst.Verdict == SharedValidationVerdict.Fail,
            "StdVector with First > Last must fail.");

        // 5. Disordered Last > End
        var disorderedLast = CanonicalStructuralInvariants.ValidateStdVector(0x20000, 0x20200, 0x20100);
        check(!disorderedLast.IsValid && disorderedLast.Verdict == SharedValidationVerdict.Fail,
            "StdVector with Last > End must fail.");

        // 6. Non-canonical address floor violation
        var belowFloor = CanonicalStructuralInvariants.ValidateStdVector(0x50, 0x60, 0x100);
        check(!belowFloor.IsValid && belowFloor.Verdict == SharedValidationVerdict.Fail,
            "StdVector with pointer below 0x10000 floor must fail.");

        // 7. Non-canonical address ceiling violation
        var aboveCeiling = CanonicalStructuralInvariants.ValidateStdVector(0x800000000000, 0x800000000010, 0x800000000020);
        check(!aboveCeiling.IsValid && aboveCeiling.Verdict == SharedValidationVerdict.Fail,
            "StdVector with pointer above user ceiling must fail.");

        // 8. Element size divisibility: valid
        var divOk = CanonicalStructuralInvariants.ValidateStdVector(0x20000, 0x20050, 0x20100, elementSize: 8);
        check(divOk.IsValid && divOk.ObservedValue == 10,
            "StdVector with 80 bytes and element size 8 must pass with count 10.");

        // 9. Element size divisibility: unaligned remainder
        var divFail = CanonicalStructuralInvariants.ValidateStdVector(0x20000, 0x20055, 0x20100, elementSize: 8);
        check(!divFail.IsValid && divFail.Verdict == SharedValidationVerdict.Fail,
            "StdVector with non-divisible byte span must fail.");

        // 10. Native struct overload
        var nativeVec = new StdVector
        {
            First = new IntPtr(0x30000),
            Last = new IntPtr(0x30040),
            End = new IntPtr(0x30080)
        };
        var nativeResult = CanonicalStructuralInvariants.ValidateStdVector(nativeVec, elementSize: 16);
        check(nativeResult.IsValid && nativeResult.ObservedValue == 4,
            "ValidateStdVector(StdVector, elemSize) overload must pass with count 4.");
    }

    private static void TestStdMapInvariants(Action<bool, string> check)
    {
        // 1. Valid empty map (size = 0, sentinel IsNil=1, Color=0)
        var emptyMap = CanonicalStructuralInvariants.ValidateStdMap(
            head: 0x20000, size: 0, sentinelIsNil: 1, sentinelColor: 0);
        check(emptyMap.IsValid && emptyMap.Verdict == SharedValidationVerdict.Pass,
            "StdMap with size 0 and valid sentinel must pass.");

        // 2. Valid populated map
        var validMap = CanonicalStructuralInvariants.ValidateStdMap(
            head: 0x20000, size: 15, sentinelIsNil: 1, sentinelColor: 0,
            root: 0x20100, rootIsNil: 0, rootColor: 0);
        check(validMap.IsValid && validMap.ObservedValue == 15,
            "Populated StdMap with valid sentinel and root node must pass.");

        // 3. Invalid sentinel: IsNil is 0
        var badSentinelNil = CanonicalStructuralInvariants.ValidateStdMap(
            head: 0x20000, size: 5, sentinelIsNil: 0, sentinelColor: 0);
        check(!badSentinelNil.IsValid && badSentinelNil.Verdict == SharedValidationVerdict.Fail,
            "StdMap with sentinel IsNil == 0 must fail.");

        // 4. Invalid sentinel: Color > 1
        var badSentinelColor = CanonicalStructuralInvariants.ValidateStdMap(
            head: 0x20000, size: 5, sentinelIsNil: 1, sentinelColor: 2);
        check(!badSentinelColor.IsValid && badSentinelColor.Verdict == SharedValidationVerdict.Fail,
            "StdMap with sentinel Color > 1 must fail.");

        // 5. Negative size
        var negSize = CanonicalStructuralInvariants.ValidateStdMap(
            head: 0x20000, size: -1, sentinelIsNil: 1, sentinelColor: 0);
        check(!negSize.IsValid && negSize.Verdict == SharedValidationVerdict.Fail,
            "StdMap with negative size must fail.");

        // 6. Excessive size (above safety budget)
        var hugeSize = CanonicalStructuralInvariants.ValidateStdMap(
            head: 0x20000, size: 2_000_000, sentinelIsNil: 1, sentinelColor: 0);
        check(!hugeSize.IsValid && hugeSize.Verdict == SharedValidationVerdict.Fail,
            "StdMap with size > MaxMapSize must fail.");

        // 7. Non-canonical head pointer
        var badHead = CanonicalStructuralInvariants.ValidateStdMap(
            head: 0x50, size: 0, sentinelIsNil: 1, sentinelColor: 0);
        check(!badHead.IsValid && badHead.Verdict == SharedValidationVerdict.Fail,
            "StdMap with non-canonical head pointer must fail.");

        // 8. Populated map with invalid root node (root IsNil != 0)
        var badRootNil = CanonicalStructuralInvariants.ValidateStdMap(
            head: 0x20000, size: 5, sentinelIsNil: 1, sentinelColor: 0,
            root: 0x20100, rootIsNil: 1, rootColor: 0);
        check(!badRootNil.IsValid && badRootNil.Verdict == SharedValidationVerdict.Fail,
            "Populated StdMap with root node IsNil != 0 must fail.");
    }

    private static void TestUiElementInvariants(Action<bool, string> check)
    {
        // 1. Self == Address match
        var match = CanonicalStructuralInvariants.ValidateUiElementSelf(0x50000, 0x50000);
        check(match.IsValid && match.ObservedValue == 0x50000,
            "UiElement Self == Address must pass with observed address.");

        // 2. Self mismatch
        var mismatch = CanonicalStructuralInvariants.ValidateUiElementSelf(0x50008, 0x50000);
        check(!mismatch.IsValid && mismatch.Verdict == SharedValidationVerdict.Fail,
            "UiElement Self != Address must fail.");

        // 3. Null address
        var nullAddr = CanonicalStructuralInvariants.ValidateUiElementSelf(0, 0);
        check(!nullAddr.IsValid && nullAddr.Verdict == SharedValidationVerdict.Fail,
            "UiElement null address must fail.");

        // 4. Below floor address
        var belowFloor = CanonicalStructuralInvariants.ValidateUiElementSelf(0x100, 0x100);
        check(!belowFloor.IsValid && belowFloor.Verdict == SharedValidationVerdict.Fail,
            "UiElement address below 0x10000 floor must fail.");

        // 5. Visibility and masking with proven fingerprints
        check(CanonicalStructuralInvariants.IsUiVisible(CanonicalVersionedSemantics.RawRuneshapeFingerprintV1),
            "Flags RawRuneshapeFingerprintV1 (0x00462EF1 with bit 11 set) must be visible.");
        check(!CanonicalStructuralInvariants.IsUiVisible(CanonicalVersionedSemantics.MaskedRuneshapeFingerprint),
            "Flags MaskedRuneshapeFingerprint (0x004626F1 with bit 11 cleared) must not be visible.");
        check(CanonicalStructuralInvariants.GetMaskedUiFlags(CanonicalVersionedSemantics.RawRuneshapeFingerprintV1) == CanonicalVersionedSemantics.MaskedRuneshapeFingerprint,
            "GetMaskedUiFlags for RawRuneshapeFingerprintV1 must yield MaskedRuneshapeFingerprint.");

        check(CanonicalStructuralInvariants.IsUiVisible(CanonicalVersionedSemantics.AtlasMapNodeFp),
            "Flags AtlasMapNodeFp (0x542EF3 with bit 11 set) must be visible.");
        check(!CanonicalStructuralInvariants.IsUiVisible(CanonicalVersionedSemantics.MaskedAtlasMapNodeFp),
            "Flags MaskedAtlasMapNodeFp (0x5426F3 with bit 11 cleared) must not be visible.");
        check(CanonicalStructuralInvariants.GetMaskedUiFlags(CanonicalVersionedSemantics.AtlasMapNodeFp) == CanonicalVersionedSemantics.MaskedAtlasMapNodeFp,
            "GetMaskedUiFlags for AtlasMapNodeFp must yield MaskedAtlasMapNodeFp.");
    }

    private static void TestComponentOwnerInvariants(Action<bool, string> check)
    {
        // 1. Exact match
        var match = CanonicalStructuralInvariants.ValidateComponentOwner(0x60000, 0x60000);
        check(match.IsValid && match.ObservedValue == 0x60000,
            "Component EntityPtr == Owner must pass with observed owner.");

        // 2. Mismatch
        var mismatch = CanonicalStructuralInvariants.ValidateComponentOwner(0x60010, 0x60000);
        check(!mismatch.IsValid && mismatch.Verdict == SharedValidationVerdict.Fail,
            "Component EntityPtr != Owner must fail.");

        // 3. Null owner
        var nullOwner = CanonicalStructuralInvariants.ValidateComponentOwner(0, 0);
        check(!nullOwner.IsValid && nullOwner.Verdict == SharedValidationVerdict.Fail,
            "Component null owner must fail.");

        // 4. Non-canonical owner
        var badOwner = CanonicalStructuralInvariants.ValidateComponentOwner(0x50, 0x50);
        check(!badOwner.IsValid && badOwner.Verdict == SharedValidationVerdict.Fail,
            "Component owner below user floor must fail.");
    }

    private static void TestPointerInvariants(Action<bool, string> check)
    {
        check(CanonicalStructuralInvariants.IsCanonicalPointer(0x10000),
            "0x10000 must be a valid canonical pointer.");
        check(CanonicalStructuralInvariants.IsCanonicalPointer(0x7FFFFFFFFFFF),
            "0x7FFFFFFFFFFF must be a valid canonical pointer.");
        check(!CanonicalStructuralInvariants.IsCanonicalPointer(0),
            "0 must not be a valid canonical pointer.");
        check(!CanonicalStructuralInvariants.IsCanonicalPointer(0xFFFF),
            "0xFFFF must not be a valid canonical pointer.");
        check(!CanonicalStructuralInvariants.IsCanonicalPointer(0x800000000000),
            "0x800000000000 must not be a valid canonical pointer.");

        var alignPass = CanonicalStructuralInvariants.ValidatePointerAlignment(0x20008, 8);
        check(alignPass.IsValid, "0x20008 must pass 8-byte alignment.");

        var alignFail = CanonicalStructuralInvariants.ValidatePointerAlignment(0x20005, 8);
        check(!alignFail.IsValid, "0x20005 must fail 8-byte alignment.");
    }

    private static void TestOldVsNewEquivalenceHarness(Action<bool, string> check)
    {
        // ---------------------------------------------------------------------
        // 1. StdVector Equivalence Harness
        // ---------------------------------------------------------------------
        // Simulates old OffsetHelperEngine VectorRow logic vs CanonicalStructuralInvariants.ValidateStdVector
        static bool OldOhVectorValid(long first, long last, long end)
        {
            if (first == 0 && last == 0 && end == 0) return true;
            var ordered = first <= last && last <= end;
            var rangeOk = first >= 0x10000 && first <= 0x7FFFFFFFFFFF &&
                          last >= 0x10000 && last <= 0x7FFFFFFFFFFF &&
                          (end == 0 || (end >= 0x10000 && end <= 0x7FFFFFFFFFFF));
            return ordered && rangeOk;
        }

        (long First, long Last, long End)[] vectorTestCases =
        [
            (0, 0, 0),
            (0x20000, 0x20000, 0x20100),
            (0x20000, 0x20040, 0x20080),
            (0x20080, 0x20040, 0x20080), // First > Last
            (0x20000, 0x20090, 0x20080), // Last > End
            (0x10, 0x20, 0x40),          // Below floor
            (0x800000000000, 0x800000000010, 0x800000000020), // Above ceiling
            (0x20000, 0x20040, 0)        // End == 0
        ];

        foreach (var tc in vectorTestCases)
        {
            var oldVal = OldOhVectorValid(tc.First, tc.Last, tc.End);
            var newVal = CanonicalStructuralInvariants.ValidateStdVector(tc.First, tc.Last, tc.End).IsValid;
            check(oldVal == newVal,
                $"StdVector equivalence mismatch on (0x{tc.First:X}, 0x{tc.Last:X}, 0x{tc.End:X}): Old={oldVal}, New={newVal}");
        }

        // ---------------------------------------------------------------------
        // 2. StdMap Equivalence Harness
        // ---------------------------------------------------------------------
        // Simulates old OffsetHelperEngine TryValidateMap logic vs CanonicalStructuralInvariants.ValidateStdMap
        static bool OldOhMapValid(long head, int size, byte sentinelIsNil, byte sentinelColor, long root, byte rootIsNil, byte rootColor)
        {
            if (size < 0 || size > 1_000_000 || head < 0x10000 || head > 0x7FFFFFFFFFFF ||
                sentinelIsNil == 0 || sentinelColor > 1)
            {
                return false;
            }
            if (size == 0) return true;
            if (root < 0x10000 || root > 0x7FFFFFFFFFFF || rootIsNil != 0 || rootColor > 1)
            {
                return false;
            }
            return true;
        }

        (long Head, int Size, byte SentinelIsNil, byte SentinelColor, long Root, byte RootIsNil, byte RootColor)[] mapTestCases =
        [
            (0x20000, 0, 1, 0, 0, 0, 0),
            (0x20000, 10, 1, 0, 0x20100, 0, 0),
            (0x20000, 10, 1, 1, 0x20100, 0, 1),
            (0x20000, -1, 1, 0, 0x20100, 0, 0),
            (0x20000, 2_000_000, 1, 0, 0x20100, 0, 0),
            (0x50, 10, 1, 0, 0x20100, 0, 0),
            (0x20000, 10, 0, 0, 0x20100, 0, 0),
            (0x20000, 10, 1, 2, 0x20100, 0, 0),
            (0x20000, 10, 1, 0, 0x50, 0, 0),
            (0x20000, 10, 1, 0, 0x20100, 1, 0),
            (0x20000, 10, 1, 0, 0x20100, 0, 2)
        ];

        foreach (var tc in mapTestCases)
        {
            var oldVal = OldOhMapValid(tc.Head, tc.Size, tc.SentinelIsNil, tc.SentinelColor, tc.Root, tc.RootIsNil, tc.RootColor);
            var newVal = CanonicalStructuralInvariants.ValidateStdMap(
                tc.Head, tc.Size, tc.SentinelIsNil, tc.SentinelColor, tc.Root, tc.RootIsNil, tc.RootColor).IsValid;
            check(oldVal == newVal,
                $"StdMap equivalence mismatch on Head=0x{tc.Head:X}, Size={tc.Size}: Old={oldVal}, New={newVal}");
        }

        // ---------------------------------------------------------------------
        // 3. UiElement Self Equivalence Harness
        // ---------------------------------------------------------------------
        // Simulates old OffsetHelperEngine IsUiElement logic vs CanonicalStructuralInvariants.ValidateUiElementSelf
        static bool OldOhUiElementValid(long self, long addr)
        {
            return addr >= 0x10000 && addr <= 0x7FFFFFFFFFFF && self == addr;
        }

        (long Self, long Addr)[] uiTestCases =
        [
            (0x30000, 0x30000),
            (0x30008, 0x30000),
            (0, 0),
            (0x50, 0x50),
            (0x800000000000, 0x800000000000)
        ];

        foreach (var tc in uiTestCases)
        {
            var oldVal = OldOhUiElementValid(tc.Self, tc.Addr);
            var newVal = CanonicalStructuralInvariants.ValidateUiElementSelf(tc.Self, tc.Addr).IsValid;
            check(oldVal == newVal,
                $"UiElement Self equivalence mismatch on Self=0x{tc.Self:X}, Addr=0x{tc.Addr:X}: Old={oldVal}, New={newVal}");
        }

        // ---------------------------------------------------------------------
        // 4. Component Owner Equivalence Harness
        // ---------------------------------------------------------------------
        // Simulates old OffsetHelperEngine PointerRow isOwner check vs CanonicalStructuralInvariants.ValidateComponentOwner
        static bool OldOhOwnerValid(long entityPtr, long owner)
        {
            return owner >= 0x10000 && owner <= 0x7FFFFFFFFFFF && entityPtr == owner && entityPtr != 0;
        }

        (long EntityPtr, long Owner)[] ownerTestCases =
        [
            (0x40000, 0x40000),
            (0x40008, 0x40000),
            (0, 0),
            (0x100, 0x100),
            (0x800000000000, 0x800000000000)
        ];

        foreach (var tc in ownerTestCases)
        {
            var oldVal = OldOhOwnerValid(tc.EntityPtr, tc.Owner);
            var newVal = CanonicalStructuralInvariants.ValidateComponentOwner(tc.EntityPtr, tc.Owner).IsValid;
            check(oldVal == newVal,
                $"Component Owner equivalence mismatch on EntityPtr=0x{tc.EntityPtr:X}, Owner=0x{tc.Owner:X}: Old={oldVal}, New={newVal}");
        }
    }
}
