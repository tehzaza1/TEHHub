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

        // 1. PoE2 Invariant: Self pointer must either be zero or point to the element's own address (see UiElementBase.UpdateData)
        if (layout.Self != IntPtr.Zero && layout.Self != elementAddr)
        {
            failureReason = $"Self pointer mismatch: expected 0x{elementAddr.ToInt64():X} or 0x0, read 0x{layout.Self.ToInt64():X}.";
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

        // 4. Scalar fields domain validation
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

        // 5. Parent pointer coherence (if present)
        if (layout.ParentPtr != IntPtr.Zero)
        {
            if (!reader.IsValidAddress(layout.ParentPtr) ||
                !reader.TryRead<UiElementBaseOffset>(layout.ParentPtr, out var parentLayout) ||
                (parentLayout.Self != IntPtr.Zero && parentLayout.Self != layout.ParentPtr))
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

        result.Status = ValidationStatus.UNVERIFIED;
        result.ResolvedAddress = uiRootStructPtr;
        result.TraversalAddress = uiRootStructPtr;
        result.ExtractedValue = $"UiRootStruct (0x{uiRootStructPtr.ToInt64():X}, GameUi=0x{gameUiPtr.ToInt64():X})";
        result.ErrorMessage = "UNVERIFIED: UiRootStruct is structurally valid with readable GameUi container; unverified without unique root identity proof.";
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

        result.Status = ValidationStatus.UNVERIFIED;
        result.ResolvedAddress = gameUiPtr;
        result.TraversalAddress = gameUiPtr;
        result.ExtractedValue = $"GameUi [Children={childCount}, Self=0x{layout.Self.ToInt64():X}]";
        result.ErrorMessage = "UNVERIFIED: GameUi container is structurally readable with valid child layout; unverified without unique container identity proof.";
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
            result.TraversalAddress = chatPtr;
            result.ErrorMessage = $"ChatParent pointer at 0x{chatPtr.ToInt64():X} failed UiElementBase layout: {failReason}";
            return;
        }

        bool isVisible = (layout.Flags & IsVisibleMask) != 0;
        result.Status = ValidationStatus.UNVERIFIED;
        result.ResolvedAddress = chatPtr;
        result.TraversalAddress = chatPtr;
        result.ExtractedValue = $"ChatParent [Visible={isVisible}, Flags=0x{layout.Flags:X}]";
        result.ErrorMessage = "UNVERIFIED: active ChatParent pointer has valid UiElementBase layout and readable visibility/flags, but no source-backed chat identity proof.";
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
            result.TraversalAddress = panelPtr;
            result.ErrorMessage = $"UNVERIFIED: active {panelName} pointer structurally readable, but UiElementBase layout proof missing: {failReason}";
            result.Evidence.Add(new EvidenceRecord
            {
                RuleName = "PanelLayoutUnverified",
                Description = $"Active pointer 0x{panelPtr.ToInt64():X} layout unverified: {failReason}",
                Passed = false
            });
            return;
        }

        int childCount = (int)((layout.ChildrensPtr.Last.ToInt64() - layout.ChildrensPtr.First.ToInt64()) / IntPtr.Size);
        bool isVisible = (layout.Flags & IsVisibleMask) != 0;

        // Active side panel with valid UiElementBase layout remains UNVERIFIED without dedicated source-backed panel identity proof
        result.Status = ValidationStatus.UNVERIFIED;
        result.ResolvedAddress = panelPtr;
        result.TraversalAddress = panelPtr;
        result.ExtractedValue = $"{panelName} [Size={layout.UnscaledSize.X:F0}x{layout.UnscaledSize.Y:F0}, Visible={isVisible}, Children={childCount}]";
        result.ErrorMessage = "UNVERIFIED: active side panel pointer has valid UiElementBase layout and readable visibility/child data, but no source-backed panel identity proof.";
        result.Evidence.Add(new EvidenceRecord
        {
            RuleName = "SidePanelLayoutPlausible",
            Description = $"{panelName} layout plausible: UiElementBase + dimensions ({layout.UnscaledSize.X:F0}x{layout.UnscaledSize.Y:F0}) + {childCount} children (structural only)",
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
            result.TraversalAddress = treePtr;
            result.ErrorMessage = $"UNVERIFIED: active PassiveSkillTreePanel pointer structurally readable, but UiElementBase layout proof missing: {failReason}";
            return;
        }

        int childCount = (int)((layout.ChildrensPtr.Last.ToInt64() - layout.ChildrensPtr.First.ToInt64()) / IntPtr.Size);
        bool isVisible = (layout.Flags & IsVisibleMask) != 0;

        result.Status = ValidationStatus.UNVERIFIED;
        result.ResolvedAddress = treePtr;
        result.TraversalAddress = treePtr;
        result.ExtractedValue = $"PassiveSkillTree [Children={childCount}, Visible={isVisible}]";
        result.ErrorMessage = "UNVERIFIED: active PassiveSkillTreePanel pointer has valid UiElementBase layout and child container, but no source-backed passive tree node identity proof.";
        result.Evidence.Add(new EvidenceRecord
        {
            RuleName = "PassiveTreeLayoutPlausible",
            Description = $"PassiveSkillTreePanel layout plausible: UiElementBase + {childCount} children (structural only)",
            Passed = true,
            IsIndependentValidator = true
        });
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
            result.TraversalAddress = mapParentPtr;
            result.ErrorMessage = $"UNVERIFIED: active MapParent pointer structurally readable, but UiElementBase layout proof missing: {failReason}";
            return;
        }

        int childCount = (int)((layout.ChildrensPtr.Last.ToInt64() - layout.ChildrensPtr.First.ToInt64()) / IntPtr.Size);
        bool isVisible = (layout.Flags & IsVisibleMask) != 0;

        result.Status = ValidationStatus.UNVERIFIED;
        result.ResolvedAddress = mapParentPtr;
        result.TraversalAddress = mapParentPtr;
        result.ExtractedValue = $"MapParent [Children={childCount}, Visible={isVisible}]";
        result.ErrorMessage = "UNVERIFIED: active MapParent pointer has valid UiElementBase layout and MapUiElement structure, but no source-backed map identity proof.";
        result.Evidence.Add(new EvidenceRecord
        {
            RuleName = "MapParentLayoutPlausible",
            Description = $"MapParent layout plausible: UiElementBase + {childCount} children (structural only)",
            Passed = true,
            IsIndependentValidator = true
        });
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
            result.TraversalAddress = worldMapPtr;
            result.ErrorMessage = $"UNVERIFIED: active WorldMapPanel pointer structurally readable, but UiElementBase layout proof missing: {failReason}";
            return;
        }

        int childCount = (int)((layout.ChildrensPtr.Last.ToInt64() - layout.ChildrensPtr.First.ToInt64()) / IntPtr.Size);
        bool isVisible = (layout.Flags & IsVisibleMask) != 0;

        result.Status = ValidationStatus.UNVERIFIED;
        result.ResolvedAddress = worldMapPtr;
        result.TraversalAddress = worldMapPtr;
        result.ExtractedValue = $"WorldMapPanel [Children={childCount}, Visible={isVisible}]";
        result.ErrorMessage = "UNVERIFIED: active WorldMapPanel pointer has valid UiElementBase layout and readable child tabs, but no source-backed unique panel identity proof.";
        result.Evidence.Add(new EvidenceRecord
        {
            RuleName = "WorldMapPanelLayoutPlausible",
            Description = $"WorldMapPanel layout plausible: UiElementBase + {childCount} child tabs (structural only)",
            Passed = true,
            IsIndependentValidator = true
        });
    }
}