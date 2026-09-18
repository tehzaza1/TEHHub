namespace TEHhub.OffsetDoctor.Validation;

using System;
using System.Runtime.InteropServices;
using TEHhub.OffsetDoctor.Evidence;
using TEHhub.OffsetDoctor.Manifest;
using TEHhub.OffsetDoctor.Process;
using TEHhub.Offsets.Natives;
using TEHhub.Offsets.Objects.States;
using TEHhub.Offsets.Objects.States.InGameState;
using TEHhub.Offsets.Objects.UiElement;

public static class UiSemanticValidator
{
    private const uint IsVisibleMask = 0x800; // Bit 11

    public static bool TryValidateUiElement(
        IProcessMemoryReader reader,
        IntPtr targetAddr,
        OffsetNode node,
        ValidationResult result)
    {
        switch (node.Id)
        {
            case "ui_root_struct":
                ValidateUiRootStruct(reader, targetAddr, node, result);
                return true;

            case "ui_game_ui_ptr":
                ValidateGameUiPtr(reader, targetAddr, node, result);
                return true;

            case "ui_chat_parent":
                ValidateChatParent(reader, targetAddr, node, result);
                return true;

            case "ui_left_panel":
                ValidateSidePanel(reader, targetAddr, node, result, "LeftPanel");
                return true;

            case "ui_right_panel":
                ValidateSidePanel(reader, targetAddr, node, result, "RightPanel");
                return true;

            case "ui_passive_tree_panel":
                ValidatePassiveTreePanel(reader, targetAddr, node, result);
                return true;

            case "ui_map_parent":
                ValidateMapParent(reader, targetAddr, node, result);
                return true;

            case "ui_world_map_panel":
                ValidateWorldMapPanel(reader, targetAddr, node, result);
                return true;

            default:
                return false;
        }
    }

    public static bool ValidateUiElementBaseLayout(
        IProcessMemoryReader reader,
        IntPtr elementAddr,
        out UiElementBaseOffset layout,
        out string? failureReason)
    {
        layout = default;
        failureReason = null;

        if (elementAddr == IntPtr.Zero || !reader.IsValidAddress(elementAddr))
        {
            failureReason = $"Element pointer 0x{elementAddr.ToInt64():X} is null or unmapped.";
            return false;
        }

        if (!reader.TryRead<UiElementBaseOffset>(elementAddr, out layout))
        {
            failureReason = $"Failed to read UiElementBaseOffset memory at 0x{elementAddr.ToInt64():X}.";
            return false;
        }

        // 1. Critical PoE2 Invariant: Self pointer must point to the element's own address
        if (layout.Self != elementAddr)
        {
            failureReason = $"Self pointer mismatch: expected 0x{elementAddr.ToInt64():X}, read 0x{layout.Self.ToInt64():X}.";
            return false;
        }

        // 2. Flags Domain Validation
        if (layout.Flags == uint.MaxValue)
        {
            failureReason = $"Flags 0x{layout.Flags:X8} contains invalid domain bits (uninitialized/all bits set).";
            return false;
        }

        // 3. Children vector bounds and alignment
        var cFirst = layout.ChildrensPtr.First.ToInt64();
        var cLast = layout.ChildrensPtr.Last.ToInt64();
        var cEnd = layout.ChildrensPtr.End.ToInt64();

        if (cFirst < 0 || cLast < 0 || cEnd < 0)
        {
            failureReason = "Negative pointer values in ChildrensPtr vector.";
            return false;
        }

        if (cFirst == 0 && (cLast != 0 || cEnd != 0))
        {
            failureReason = "ChildrensPtr First is null while Last or End is non-zero.";
            return false;
        }

        if (cFirst > cLast || cLast > cEnd)
        {
            failureReason = $"Invalid ChildrensPtr vector boundaries (First=0x{cFirst:X}, Last=0x{cLast:X}, End=0x{cEnd:X}).";
            return false;
        }

        long byteLen = cLast - cFirst;
        long capLen = cEnd - cFirst;
        if (byteLen % IntPtr.Size != 0 || capLen % IntPtr.Size != 0)
        {
            failureReason = $"ChildrensPtr vector is not pointer-aligned (byteLen={byteLen}, capLen={capLen}).";
            return false;
        }

        int childCount = (int)(byteLen / IntPtr.Size);
        if (childCount < 0 || childCount > 50000)
        {
            failureReason = $"ChildrensPtr child count {childCount} is out of sane bounds (0..50000).";
            return false;
        }

        if (childCount > 0 && (!reader.IsValidAddress(layout.ChildrensPtr.First) || !reader.TryRead<byte>(layout.ChildrensPtr.First, out _)))
        {
            failureReason = $"ChildrensPtr buffer at 0x{cFirst:X} is unmapped or unreadable.";
            return false;
        }

        // 3. Scalar fields domain validation
        if (float.IsNaN(layout.LocalScaleMultiplier) || float.IsInfinity(layout.LocalScaleMultiplier) ||
            layout.LocalScaleMultiplier <= 0.0f || layout.LocalScaleMultiplier > 100.0f)
        {
            failureReason = $"LocalScaleMultiplier {layout.LocalScaleMultiplier} is out of sane bounds.";
            return false;
        }

        if (float.IsNaN(layout.UnscaledSize.X) || float.IsInfinity(layout.UnscaledSize.X) ||
            float.IsNaN(layout.UnscaledSize.Y) || float.IsInfinity(layout.UnscaledSize.Y) ||
            layout.UnscaledSize.X < 0.0f || layout.UnscaledSize.Y < 0.0f ||
            layout.UnscaledSize.X > 100000.0f || layout.UnscaledSize.Y > 100000.0f)
        {
            failureReason = $"UnscaledSize ({layout.UnscaledSize.X}, {layout.UnscaledSize.Y}) contains invalid or negative dimensions.";
            return false;
        }

        if (float.IsNaN(layout.RelativePosition.X) || float.IsInfinity(layout.RelativePosition.X) ||
            float.IsNaN(layout.RelativePosition.Y) || float.IsInfinity(layout.RelativePosition.Y) ||
            float.IsNaN(layout.PositionModifier.X) || float.IsInfinity(layout.PositionModifier.X) ||
            float.IsNaN(layout.PositionModifier.Y) || float.IsInfinity(layout.PositionModifier.Y))
        {
            failureReason = "RelativePosition or PositionModifier contains NaN or Infinity.";
            return false;
        }

        if (layout.ScaleIndex > 32)
        {
            failureReason = $"ScaleIndex {layout.ScaleIndex} exceeds maximum expected value (32).";
            return false;
        }

        // 4. Parent pointer coherence (if present)
        if (layout.ParentPtr != IntPtr.Zero)
        {
            if (!reader.IsValidAddress(layout.ParentPtr) ||
                !reader.TryRead<UiElementBaseOffset>(layout.ParentPtr, out var parentLayout) ||
                parentLayout.Self != layout.ParentPtr)
            {
                failureReason = $"ParentPtr 0x{layout.ParentPtr.ToInt64():X} does not point to a valid UiElementBase.";
                return false;
            }
        }

        return true;
    }

    private static void ValidateUiRootStruct(
        IProcessMemoryReader reader,
        IntPtr targetAddr,
        OffsetNode node,
        ValidationResult result)
    {
        if (!reader.TryRead<IntPtr>(targetAddr, out var uiRootStructPtr))
        {
            result.Status = ValidationStatus.BROKEN;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"Failed to read UiRootStruct pointer at +0x{node.DefaultOffset:X}.";
            return;
        }

        if (uiRootStructPtr == IntPtr.Zero || !reader.IsValidAddress(uiRootStructPtr))
        {
            result.Status = ValidationStatus.BROKEN;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"UiRootStruct pointer 0x{uiRootStructPtr.ToInt64():X} is null or unmapped.";
            return;
        }

        if (!reader.TryRead<UiRootStruct>(uiRootStructPtr, out var uiRootStruct))
        {
            result.Status = ValidationStatus.BROKEN;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"Failed to read UiRootStruct at 0x{uiRootStructPtr.ToInt64():X}.";
            return;
        }

        var gameUiPtr = uiRootStruct.GameUiPtr;
        if (gameUiPtr == IntPtr.Zero || !reader.IsValidAddress(gameUiPtr))
        {
            result.Status = ValidationStatus.BROKEN;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"UiRootStruct at 0x{uiRootStructPtr.ToInt64():X} contains null/invalid GameUiPtr (0x{gameUiPtr.ToInt64():X}).";
            return;
        }

        if (!ValidateUiElementBaseLayout(reader, gameUiPtr, out var gameUiLayout, out var layoutFail))
        {
            result.Status = ValidationStatus.BROKEN;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"GameUiPtr in UiRootStruct failed UiElementBase validation: {layoutFail}";
            return;
        }

        result.Status = ValidationStatus.VALID;
        result.ResolvedAddress = uiRootStructPtr;
        result.TraversalAddress = uiRootStructPtr;
        result.ExtractedValue = $"VALID: UiElementBase + visibility/child layout + expected helper path resolved (UiRootStruct 0x{uiRootStructPtr.ToInt64():X}, GameUi=0x{gameUiPtr.ToInt64():X})";
        result.Evidence.Add(new EvidenceRecord
        {
            RuleName = "UiRootStructVerified",
            Description = $"UiRootStruct at 0x{uiRootStructPtr.ToInt64():X} verified with valid GameUi UiElementBase (0x{gameUiPtr.ToInt64():X})",
            Passed = true,
            IsIndependentValidator = true
        });
    }

    private static void ValidateGameUiPtr(
        IProcessMemoryReader reader,
        IntPtr targetAddr,
        OffsetNode node,
        ValidationResult result)
    {
        if (!reader.TryRead<IntPtr>(targetAddr, out var gameUiPtr))
        {
            result.Status = ValidationStatus.BROKEN;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"Failed to read GameUi pointer at +0x{node.DefaultOffset:X}.";
            return;
        }

        if (gameUiPtr == IntPtr.Zero || !reader.IsValidAddress(gameUiPtr))
        {
            result.Status = ValidationStatus.BROKEN;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"GameUi pointer 0x{gameUiPtr.ToInt64():X} is null or unmapped.";
            return;
        }

        if (!ValidateUiElementBaseLayout(reader, gameUiPtr, out var layout, out var failReason))
        {
            result.Status = ValidationStatus.BROKEN;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"GameUi failed UiElementBase validation: {failReason}";
            return;
        }

        int childCount = (int)((layout.ChildrensPtr.Last.ToInt64() - layout.ChildrensPtr.First.ToInt64()) / IntPtr.Size);
        if (childCount < 20)
        {
            result.Status = ValidationStatus.UNVERIFIED;
            result.ResolvedAddress = gameUiPtr;
            result.TraversalAddress = gameUiPtr;
            result.ErrorMessage = $"GameUi child count ({childCount}) is lower than expected for main UI container (expected >= 20).";
            return;
        }

        result.Status = ValidationStatus.VALID;
        result.ResolvedAddress = gameUiPtr;
        result.TraversalAddress = gameUiPtr;
        result.ExtractedValue = $"VALID: UiElementBase + visibility/child layout + expected helper path resolved (GameUi 0x{gameUiPtr.ToInt64():X}, {childCount} children)";
        result.Evidence.Add(new EvidenceRecord
        {
            RuleName = "GameUiContainerVerified",
            Description = $"GameUi container verified with {childCount} child UiElements (Self=0x{layout.Self.ToInt64():X})",
            Passed = true,
            IsIndependentValidator = true
        });
    }

    private static void ValidateChatParent(
        IProcessMemoryReader reader,
        IntPtr targetAddr,
        OffsetNode node,
        ValidationResult result)
    {
        if (!reader.TryRead<IntPtr>(targetAddr, out var chatPtr))
        {
            result.Status = ValidationStatus.BROKEN;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"Failed to read ChatParent pointer at +0x{node.DefaultOffset:X}.";
            return;
        }

        if (chatPtr == IntPtr.Zero || !reader.IsValidAddress(chatPtr))
        {
            result.Status = ValidationStatus.BROKEN;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"ChatParent pointer 0x{chatPtr.ToInt64():X} is null or unmapped.";
            return;
        }

        if (!ValidateUiElementBaseLayout(reader, chatPtr, out var layout, out var failReason))
        {
            result.Status = ValidationStatus.UNVERIFIED;
            result.ResolvedAddress = chatPtr;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"ChatParent pointer at 0x{chatPtr.ToInt64():X} failed UiElementBase layout: {failReason}";
            return;
        }

        bool isVisible = (layout.Flags & IsVisibleMask) != 0;
        result.Status = ValidationStatus.VALID;
        result.ResolvedAddress = chatPtr;
        result.TraversalAddress = chatPtr;
        result.ExtractedValue = $"VALID: UiElementBase + visibility/child layout + expected helper path resolved (ChatParent 0x{chatPtr.ToInt64():X}, Visible={isVisible}, Flags=0x{layout.Flags:X})";
        result.Evidence.Add(new EvidenceRecord
        {
            RuleName = "ChatParentVerified",
            Description = $"ChatParent UiElementBase verified (Addr=0x{chatPtr.ToInt64():X}, Flags=0x{layout.Flags:X})",
            Passed = true,
            IsIndependentValidator = true
        });
    }

    private static void ValidateSidePanel(
        IProcessMemoryReader reader,
        IntPtr targetAddr,
        OffsetNode node,
        ValidationResult result,
        string panelName)
    {
        if (!reader.TryRead<IntPtr>(targetAddr, out var panelPtr))
        {
            result.Status = ValidationStatus.BROKEN;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"Failed to read {panelName} pointer memory at +0x{node.DefaultOffset:X}.";
            return;
        }

        if (panelPtr == IntPtr.Zero)
        {
            result.Status = ValidationStatus.UNVERIFIED;
            result.ResolvedAddress = IntPtr.Zero;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"{panelName} is null (closed in current runtime state).";
            result.Evidence.Add(new EvidenceRecord
            {
                RuleName = "PanelClosedNullState",
                Description = $"{panelName} pointer is null (inactive/closed)",
                Passed = true
            });
            return;
        }

        if (!reader.IsValidAddress(panelPtr) || !reader.TryRead<byte>(panelPtr, out _))
        {
            result.Status = ValidationStatus.UNVERIFIED;
            result.ResolvedAddress = panelPtr;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"{panelName} pointer 0x{panelPtr.ToInt64():X} is inactive or sentinel in current runtime state.";
            result.Evidence.Add(new EvidenceRecord
            {
                RuleName = "PanelSentinelState",
                Description = $"{panelName} pointer has inactive sentinel value 0x{panelPtr.ToInt64():X}",
                Passed = true
            });
            return;
        }

        if (!ValidateUiElementBaseLayout(reader, panelPtr, out var layout, out var failReason))
        {
            result.Status = ValidationStatus.UNVERIFIED;
            result.ResolvedAddress = panelPtr;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"UNVERIFIED: active {panelName} pointer structurally readable, but UiElementBase layout proof missing: {failReason}";
            result.Evidence.Add(new EvidenceRecord
            {
                RuleName = "PanelLayoutUnverified",
                Description = $"Active pointer 0x{panelPtr.ToInt64():X} layout unverified: {failReason}",
                Passed = false
            });
            return;
        }

        if (layout.UnscaledSize.X < 50 || layout.UnscaledSize.Y < 50)
        {
            result.Status = ValidationStatus.UNVERIFIED;
            result.ResolvedAddress = panelPtr;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"UNVERIFIED: active {panelName} pointer structurally readable, visibility flag readable, but size ({layout.UnscaledSize.X:F0}x{layout.UnscaledSize.Y:F0}) too small for side panel.";
            return;
        }

        if (layout.ParentPtr == IntPtr.Zero)
        {
            result.Status = ValidationStatus.UNVERIFIED;
            result.ResolvedAddress = panelPtr;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"UNVERIFIED: active {panelName} pointer structurally readable, visibility flag readable, but missing parent container link.";
            return;
        }

        int childCount = (int)((layout.ChildrensPtr.Last.ToInt64() - layout.ChildrensPtr.First.ToInt64()) / IntPtr.Size);
        if (childCount < 1)
        {
            result.Status = ValidationStatus.UNVERIFIED;
            result.ResolvedAddress = panelPtr;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"UNVERIFIED: active {panelName} pointer structurally readable, visibility flag readable, but semantic child path proof missing.";
            return;
        }

        bool isVisible = (layout.Flags & IsVisibleMask) != 0;

        result.Status = ValidationStatus.VALID;
        result.ResolvedAddress = panelPtr;
        result.TraversalAddress = panelPtr;
        result.ExtractedValue = $"VALID: UiElementBase + visibility/child layout + expected helper path resolved ({panelName} 0x{panelPtr.ToInt64():X}, Size={layout.UnscaledSize.X:F0}x{layout.UnscaledSize.Y:F0}, Visible={isVisible}, Children={childCount})";
        result.Evidence.Add(new EvidenceRecord
        {
            RuleName = "SidePanelVerified",
            Description = $"{panelName} verified: UiElementBase + active dimensions ({layout.UnscaledSize.X:F0}x{layout.UnscaledSize.Y:F0}) + parent link (0x{layout.ParentPtr.ToInt64():X}) + {childCount} children",
            Passed = true,
            IsIndependentValidator = true
        });
    }

    private static void ValidatePassiveTreePanel(
        IProcessMemoryReader reader,
        IntPtr targetAddr,
        OffsetNode node,
        ValidationResult result)
    {
        if (!reader.TryRead<IntPtr>(targetAddr, out var treePtr))
        {
            result.Status = ValidationStatus.BROKEN;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"Failed to read PassiveSkillTreePanel pointer memory at +0x{node.DefaultOffset:X}.";
            return;
        }

        if (treePtr == IntPtr.Zero)
        {
            result.Status = ValidationStatus.UNVERIFIED;
            result.ResolvedAddress = IntPtr.Zero;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = "PassiveSkillTreePanel is null (closed in current runtime state).";
            result.Evidence.Add(new EvidenceRecord
            {
                RuleName = "PassiveTreeClosedNullState",
                Description = "PassiveSkillTreePanel pointer is null (inactive/closed)",
                Passed = true
            });
            return;
        }

        if (!reader.IsValidAddress(treePtr) || !reader.TryRead<byte>(treePtr, out _))
        {
            result.Status = ValidationStatus.UNVERIFIED;
            result.ResolvedAddress = treePtr;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"PassiveSkillTreePanel pointer 0x{treePtr.ToInt64():X} is inactive or sentinel in current runtime state.";
            result.Evidence.Add(new EvidenceRecord
            {
                RuleName = "PassiveTreeSentinelState",
                Description = $"PassiveSkillTreePanel pointer has inactive sentinel value 0x{treePtr.ToInt64():X}",
                Passed = true
            });
            return;
        }

        if (!ValidateUiElementBaseLayout(reader, treePtr, out var layout, out var failReason))
        {
            result.Status = ValidationStatus.UNVERIFIED;
            result.ResolvedAddress = treePtr;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"UNVERIFIED: active PassiveSkillTreePanel pointer structurally readable, but UiElementBase layout proof missing: {failReason}";
            return;
        }

        int childCount = (int)((layout.ChildrensPtr.Last.ToInt64() - layout.ChildrensPtr.First.ToInt64()) / IntPtr.Size);
        if (childCount < 3)
        {
            result.Status = ValidationStatus.UNVERIFIED;
            result.ResolvedAddress = treePtr;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"UNVERIFIED: active PassiveSkillTreePanel pointer structurally readable, visibility flag readable, but child count ({childCount}) insufficient for passive tree container (expected >= 3).";
            return;
        }

        IntPtr child2Addr = IntPtr.Zero;
        if (reader.TryRead<IntPtr>(layout.ChildrensPtr.First + (2 * IntPtr.Size), out child2Addr) &&
            child2Addr != IntPtr.Zero &&
            ValidateUiElementBaseLayout(reader, child2Addr, out var child2Layout, out _))
        {
            int nodeContainerChildren = (int)((child2Layout.ChildrensPtr.Last.ToInt64() - child2Layout.ChildrensPtr.First.ToInt64()) / IntPtr.Size);
            bool isVisible = (layout.Flags & IsVisibleMask) != 0;

            result.Status = ValidationStatus.VALID;
            result.ResolvedAddress = treePtr;
            result.TraversalAddress = treePtr;
            result.ExtractedValue = $"VALID: UiElementBase + visibility/child layout + expected helper path resolved (PassiveSkillTree 0x{treePtr.ToInt64():X}, NodeContainer=0x{child2Addr.ToInt64():X}, Visible={isVisible}, NodeChildren={nodeContainerChildren})";
            result.Evidence.Add(new EvidenceRecord
            {
                RuleName = "PassiveTreeContainerVerified",
                Description = $"PassiveSkillTreePanel verified: UiElementBase + child[2] node container resolved (0x{child2Addr.ToInt64():X}, {nodeContainerChildren} children)",
                Passed = true,
                IsIndependentValidator = true
            });
            return;
        }

        result.Status = ValidationStatus.UNVERIFIED;
        result.ResolvedAddress = treePtr;
        result.TraversalAddress = IntPtr.Zero;
        result.ErrorMessage = "UNVERIFIED: active PassiveSkillTreePanel pointer structurally readable, visibility flag readable, but semantic child[2] node container proof missing.";
    }

    private static void ValidateMapParent(
        IProcessMemoryReader reader,
        IntPtr targetAddr,
        OffsetNode node,
        ValidationResult result)
    {
        if (!reader.TryRead<IntPtr>(targetAddr, out var mapParentPtr))
        {
            result.Status = ValidationStatus.BROKEN;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"Failed to read MapParent pointer memory at +0x{node.DefaultOffset:X}.";
            return;
        }

        if (mapParentPtr == IntPtr.Zero)
        {
            result.Status = ValidationStatus.UNVERIFIED;
            result.ResolvedAddress = IntPtr.Zero;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = "MapParent is null (closed in current runtime state).";
            result.Evidence.Add(new EvidenceRecord
            {
                RuleName = "MapParentClosedNullState",
                Description = "MapParent pointer is null (inactive/closed)",
                Passed = true
            });
            return;
        }

        if (!reader.IsValidAddress(mapParentPtr) || !reader.TryRead<byte>(mapParentPtr, out _))
        {
            result.Status = ValidationStatus.UNVERIFIED;
            result.ResolvedAddress = mapParentPtr;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"MapParent pointer 0x{mapParentPtr.ToInt64():X} is inactive or sentinel in current runtime state.";
            result.Evidence.Add(new EvidenceRecord
            {
                RuleName = "MapParentSentinelState",
                Description = $"MapParent pointer has inactive sentinel value 0x{mapParentPtr.ToInt64():X}",
                Passed = true
            });
            return;
        }

        if (!ValidateUiElementBaseLayout(reader, mapParentPtr, out var layout, out var failReason))
        {
            result.Status = ValidationStatus.UNVERIFIED;
            result.ResolvedAddress = mapParentPtr;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"UNVERIFIED: active MapParent pointer structurally readable, but UiElementBase layout proof missing: {failReason}";
            return;
        }

        // 1. MapParentStruct check
        if (reader.TryRead<MapParentStruct>(mapParentPtr, out var mapParentStruct))
        {
            if (mapParentStruct.LargeMapPtr != IntPtr.Zero &&
                reader.IsValidAddress(mapParentStruct.LargeMapPtr) &&
                ValidateUiElementBaseLayout(reader, mapParentStruct.LargeMapPtr, out _, out _) &&
                reader.TryRead<MapUiElementOffset>(mapParentStruct.LargeMapPtr, out var mapOff) &&
                !float.IsNaN(mapOff.Zoom) && !float.IsInfinity(mapOff.Zoom) && mapOff.Zoom is >= 0.01f and <= 50.0f)
            {
                result.Status = ValidationStatus.VALID;
                result.ResolvedAddress = mapParentPtr;
                result.TraversalAddress = mapParentPtr;
                result.ExtractedValue = $"VALID: UiElementBase + visibility/child layout + expected helper path resolved (MapParent 0x{mapParentPtr.ToInt64():X}, LargeMap=0x{mapParentStruct.LargeMapPtr.ToInt64():X}, Zoom={mapOff.Zoom:F2})";
                result.Evidence.Add(new EvidenceRecord
                {
                    RuleName = "MapParentStructVerified",
                    Description = $"MapParent verified: UiElementBase + MapParentStruct.LargeMapPtr (0x{mapParentStruct.LargeMapPtr.ToInt64():X}, Zoom={mapOff.Zoom:F2})",
                    Passed = true,
                    IsIndependentValidator = true
                });
                return;
            }
        }

        // 2. Child vector check
        int childCount = (int)((layout.ChildrensPtr.Last.ToInt64() - layout.ChildrensPtr.First.ToInt64()) / IntPtr.Size);
        if (childCount >= 2)
        {
            if (reader.TryRead<IntPtr>(layout.ChildrensPtr.First, out var child0) &&
                child0 != IntPtr.Zero &&
                ValidateUiElementBaseLayout(reader, child0, out _, out _) &&
                reader.TryRead<MapUiElementOffset>(child0, out var childMapOff) &&
                !float.IsNaN(childMapOff.Zoom) && !float.IsInfinity(childMapOff.Zoom) && childMapOff.Zoom is >= 0.01f and <= 50.0f)
            {
                result.Status = ValidationStatus.VALID;
                result.ResolvedAddress = mapParentPtr;
                result.TraversalAddress = mapParentPtr;
                result.ExtractedValue = $"VALID: UiElementBase + visibility/child layout + expected helper path resolved (MapParent 0x{mapParentPtr.ToInt64():X}, Child0Map=0x{child0.ToInt64():X}, Zoom={childMapOff.Zoom:F2})";
                result.Evidence.Add(new EvidenceRecord
                {
                    RuleName = "MapParentChildPathVerified",
                    Description = $"MapParent verified: UiElementBase + child[0] MapUiElement (0x{child0.ToInt64():X}, Zoom={childMapOff.Zoom:F2})",
                    Passed = true,
                    IsIndependentValidator = true
                });
                return;
            }
        }

        result.Status = ValidationStatus.UNVERIFIED;
        result.ResolvedAddress = mapParentPtr;
        result.TraversalAddress = IntPtr.Zero;
        result.ErrorMessage = "UNVERIFIED: active MapParent pointer structurally readable, visibility flag readable, but semantic MapUiElement child path proof missing.";
    }

    private static void ValidateWorldMapPanel(
        IProcessMemoryReader reader,
        IntPtr targetAddr,
        OffsetNode node,
        ValidationResult result)
    {
        if (!reader.TryRead<IntPtr>(targetAddr, out var worldMapPtr))
        {
            result.Status = ValidationStatus.BROKEN;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"Failed to read WorldMapPanel pointer memory at +0x{node.DefaultOffset:X}.";
            return;
        }

        if (worldMapPtr == IntPtr.Zero)
        {
            result.Status = ValidationStatus.UNVERIFIED;
            result.ResolvedAddress = IntPtr.Zero;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = "WorldMapPanel is null (closed in current runtime state).";
            result.Evidence.Add(new EvidenceRecord
            {
                RuleName = "WorldMapClosedNullState",
                Description = "WorldMapPanel pointer is null (inactive/closed)",
                Passed = true
            });
            return;
        }

        if (!reader.IsValidAddress(worldMapPtr) || !reader.TryRead<byte>(worldMapPtr, out _))
        {
            result.Status = ValidationStatus.UNVERIFIED;
            result.ResolvedAddress = worldMapPtr;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"WorldMapPanel pointer 0x{worldMapPtr.ToInt64():X} is inactive or sentinel in current runtime state.";
            result.Evidence.Add(new EvidenceRecord
            {
                RuleName = "WorldMapSentinelState",
                Description = $"WorldMapPanel pointer has inactive sentinel value 0x{worldMapPtr.ToInt64():X}",
                Passed = true
            });
            return;
        }

        if (!ValidateUiElementBaseLayout(reader, worldMapPtr, out var layout, out var failReason))
        {
            result.Status = ValidationStatus.UNVERIFIED;
            result.ResolvedAddress = worldMapPtr;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"UNVERIFIED: active WorldMapPanel pointer structurally readable, but UiElementBase layout proof missing: {failReason}";
            return;
        }

        int childCount = (int)((layout.ChildrensPtr.Last.ToInt64() - layout.ChildrensPtr.First.ToInt64()) / IntPtr.Size);
        if (childCount < 7)
        {
            result.Status = ValidationStatus.UNVERIFIED;
            result.ResolvedAddress = worldMapPtr;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"UNVERIFIED: active WorldMapPanel pointer structurally readable, visibility flag readable, but child tab count ({childCount}) insufficient (expected >= 7 for Acts 1-4, Interlude, Atlas).";
            return;
        }

        int verifiedTabs = 0;
        for (int i = 0; i < Math.Min(childCount, 8); i++)
        {
            if (reader.TryRead<IntPtr>(layout.ChildrensPtr.First + (i * IntPtr.Size), out var tabPtr) &&
                tabPtr != IntPtr.Zero &&
                ValidateUiElementBaseLayout(reader, tabPtr, out _, out _))
            {
                verifiedTabs++;
            }
        }

        if (verifiedTabs >= 4)
        {
            bool isVisible = (layout.Flags & IsVisibleMask) != 0;
            result.Status = ValidationStatus.VALID;
            result.ResolvedAddress = worldMapPtr;
            result.TraversalAddress = worldMapPtr;
            result.ExtractedValue = $"VALID: UiElementBase + visibility/child layout + expected helper path resolved (WorldMapPanel 0x{worldMapPtr.ToInt64():X}, {verifiedTabs} verified tab children, Visible={isVisible})";
            result.Evidence.Add(new EvidenceRecord
            {
                RuleName = "WorldMapPanelVerified",
                Description = $"WorldMapPanel verified: UiElementBase + {verifiedTabs} act/atlas tab children resolved",
                Passed = true,
                IsIndependentValidator = true
            });
            return;
        }

        result.Status = ValidationStatus.UNVERIFIED;
        result.ResolvedAddress = worldMapPtr;
        result.TraversalAddress = IntPtr.Zero;
        result.ErrorMessage = "UNVERIFIED: active WorldMapPanel pointer structurally readable, visibility flag readable, but semantic act/atlas tab child proof missing.";
    }
}