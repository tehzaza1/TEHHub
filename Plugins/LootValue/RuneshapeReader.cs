namespace LootValue
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;
    using TEHhub;
    using TEHhub.RemoteObjects.Components;
    using TEHhub.RemoteObjects.States.InGameStateObjects;
    using TEHhub.Offsets.Natives;
    using TEHhub.Offsets.Objects.UiElement;

    public sealed class MonolithInfo
    {
        public IntPtr EntityAddress { get; set; }
        public int HoleCount { get; set; }
        public int AnchorIdx { get; set; } = -1;
        public int AnchorPos { get; set; }
        public bool IsUnique { get; set; }
        public bool IsCompleted { get; set; }
        public Vector3 WorldPosition { get; set; }
        public float TerrainHeight { get; set; }
        public RecipeOffer? BestOffer { get; set; }
        public List<RecipeOffer> AllOffers { get; set; } = new();
    }

    public sealed class RuneforgeRowInfo
    {
        public IntPtr RowAddress { get; set; }
        public int Count { get; set; } = 1;
        public string ItemName { get; set; } = string.Empty;
        public Vector2 ScreenPos { get; set; }
        public Vector2 ScreenSize { get; set; }
        public double DisplayPrice { get; set; }
        public string CurrencySymbol { get; set; } = "c";
    }

    public static class RuneshapeReader
    {
        // Offsets in StateMachine and RuneStation
        private const int ListenerVectorOffset = 0x20;
        private const int StationOwnerOffset = 0x10;
        private const int StationAnchorRefOffset = 0x28;
        private const int StationAnchorHolderOffset = 0x30;
        private const int StationHoleCountOffset = 0x38;
        private const int StationAnchorPosOffset = 0x3C;
        private const int MinimapCompletedOffset = 0x10;

        // Runeforge UI Fingerprints
        public static readonly uint[] PanelFlagFingerprints =
            { 0x00462EF1, 0x00502EF3, 0x00502EF7, 0x00542EF1, 0x00502EF1 };
        public const uint UiVisibleMask = 0x800;

        private static DateTime nextPanelResolveUtc = DateTime.MinValue;
        private static IntPtr cachedRecipesContainer = IntPtr.Zero;

        public static bool TryReadMonolith(Entity entity, int areaLevel, int displayCurrency, out MonolithInfo info)
        {
            info = new MonolithInfo();
            if (entity.Address == IntPtr.Zero)
            {
                return false;
            }

            if (!entity.TryGetComponent<StateMachine>(out var sm) || sm.Address == IntPtr.Zero)
            {
                return false;
            }

            var reader = Core.Process.Handle;
            var listeners = reader.ReadMemory<StdVector>(sm.Address + ListenerVectorOffset);
            var nodes = reader.ReadStdVector<long>(listeners);
            if (nodes == null || nodes.Length == 0 || nodes.Length > 256)
            {
                return false;
            }

            IntPtr station = IntPtr.Zero;
            foreach (var nodeValue in nodes)
            {
                if (nodeValue == 0) continue;
                var sub = reader.ReadMemory<IntPtr>(new IntPtr(nodeValue));
                if (sub == IntPtr.Zero) continue;

                // Try both 0xA0 and 0x98 for version compatibility
                var cand1 = sub - 0xA0;
                if (reader.ReadMemory<IntPtr>(cand1 + StationOwnerOffset) == entity.Address)
                {
                    station = cand1;
                    break;
                }

                var cand2 = sub - 0x98;
                if (reader.ReadMemory<IntPtr>(cand2 + StationOwnerOffset) == entity.Address)
                {
                    station = cand2;
                    break;
                }
            }

            if (station == IntPtr.Zero)
            {
                return false;
            }

            var holeCount = reader.ReadMemory<int>(station + StationHoleCountOffset);
            if (holeCount is <= 0 or > 16)
            {
                return false;
            }

            var anchorPos = reader.ReadMemory<int>(station + StationAnchorPosOffset);
            var rowPtr = reader.ReadMemory<IntPtr>(station + StationAnchorRefOffset);
            var isUnique = false;
            var anchorIdx = -1;

            if (rowPtr == IntPtr.Zero)
            {
                isUnique = true;
            }
            else
            {
                var holder = reader.ReadMemory<IntPtr>(station + StationAnchorHolderOffset);
                if (holder != IntPtr.Zero)
                {
                    var p1 = reader.ReadMemory<IntPtr>(holder + 0x28);
                    if (p1 != IntPtr.Zero)
                    {
                        var tableBase = reader.ReadMemory<long>(p1);
                        if (tableBase != 0)
                        {
                            var delta = rowPtr.ToInt64() - tableBase;
                            if (delta >= 0)
                            {
                                if (delta % 0x68 == 0) anchorIdx = (int)(delta / 0x68);
                                else if (delta % 0x6C == 0) anchorIdx = (int)(delta / 0x6C);
                            }

                            if (anchorIdx < 0 || anchorIdx >= 34) anchorIdx = -1;
                        }
                    }
                }
            }

            var isCompleted = false;
            if (entity.TryGetComponent<MinimapIcon>(out var mmIcon) && mmIcon.Address != IntPtr.Zero)
            {
                var state = reader.ReadMemory<int>(mmIcon.Address + MinimapCompletedOffset);
                isCompleted = state != 0;
            }

            if (entity.TryGetComponent<Render>(out var render))
            {
                info.WorldPosition = new Vector3(render.WorldPosition.X, render.WorldPosition.Y, render.WorldPosition.Z);
                info.TerrainHeight = render.TerrainHeight;
            }

            info.EntityAddress = entity.Address;
            info.HoleCount = holeCount;
            info.AnchorIdx = anchorIdx;
            info.AnchorPos = anchorPos;
            info.IsUnique = isUnique;
            info.IsCompleted = isCompleted;

            info.BestOffer = RuneshapeCatalog.Instance.GetBestOffer(anchorIdx, anchorPos, holeCount, isUnique, areaLevel, displayCurrency);
            info.AllOffers = RuneshapeCatalog.Instance.GetOffers(anchorIdx, anchorPos, holeCount, isUnique, areaLevel, displayCurrency);

            return true;
        }

        public static IntPtr ResolveRuneforgeContainer(
            IntPtr gameUiAddress,
            Func<IntPtr, UiElementBaseOffset?> readUiOffset,
            Func<StdVector, IntPtr[]?> readStdVec)
        {
            var now = DateTime.UtcNow;
            if (cachedRecipesContainer != IntPtr.Zero && now < nextPanelResolveUtc)
            {
                return cachedRecipesContainer;
            }

            nextPanelResolveUtc = now.AddMilliseconds(150);
            if (gameUiAddress == IntPtr.Zero)
            {
                cachedRecipesContainer = IntPtr.Zero;
                return IntPtr.Zero;
            }

            // Fast path: Check the registered RuneshapeCombinationsPanel from ImportantUiElements
            var importantUi = Core.States.InGameStateObject?.GameUi;
            if (importantUi != null && importantUi.RuneshapeCombinationsPanel.Address != IntPtr.Zero)
            {
                var panelAddr = importantUi.RuneshapeCombinationsPanel.Address;
                var pOff = readUiOffset(panelAddr);
                if (pOff != null)
                {
                    if ((pOff.Value.Flags & UiVisibleMask) != 0)
                    {
                        var found = WalkRuneforgeUi(panelAddr, 1, readUiOffset, readStdVec);
                        if (found != IntPtr.Zero)
                        {
                            cachedRecipesContainer = found;
                            return cachedRecipesContainer;
                        }
                    }
                    else if (!Core.GHSettings.EnableControllerMode)
                    {
                        // In KBM mode, if the primary Runeshape panel is invisible, it is closed.
                        cachedRecipesContainer = IntPtr.Zero;
                        return IntPtr.Zero;
                    }
                }
            }

            if (Core.GHSettings.EnableControllerMode)
            {
                cachedRecipesContainer = ResolveControllerRuneforgeContainer(gameUiAddress, readUiOffset, readStdVec);
                return cachedRecipesContainer;
            }

            cachedRecipesContainer = WalkRuneforgeUi(gameUiAddress, 0, readUiOffset, readStdVec);
            return cachedRecipesContainer;
        }

        private static IntPtr ResolveControllerRuneforgeContainer(
            IntPtr gameUiAddress,
            Func<IntPtr, UiElementBaseOffset?> readUiOffset,
            Func<StdVector, IntPtr[]?> readStdVec)
        {
            if (readUiOffset == null || readStdVec == null)
            {
                return IntPtr.Zero;
            }

            // Fast path: Check RightPanel and LeftPanel from ImportantUiElements
            var importantUi = Core.States.InGameStateObject?.GameUi;
            if (importantUi != null)
            {
                var win = FindRuneforgeInPanel(importantUi.RightPanel.Address, readUiOffset, readStdVec);
                if (win == IntPtr.Zero)
                {
                    win = FindRuneforgeInPanel(importantUi.LeftPanel.Address, readUiOffset, readStdVec);
                }

                if (win != IntPtr.Zero)
                {
                    return WalkRuneforgeUi(win, 1, readUiOffset, readStdVec);
                }
            }

            // Co-op mode split-screen roots: GamepadUiRoot child [22] (Player 1) and [23] (Player 2)
            var rootOff = readUiOffset(gameUiAddress);
            if (rootOff != null)
            {
                var rootKids = readStdVec(rootOff.Value.ChildrensPtr);
                if (rootKids != null)
                {
                    if (rootKids.Length > 22 && rootKids[22] != IntPtr.Zero)
                    {
                        var win = FindRuneforgeInPanel(rootKids[22], readUiOffset, readStdVec);
                        if (win != IntPtr.Zero)
                        {
                            return WalkRuneforgeUi(win, 1, readUiOffset, readStdVec);
                        }
                    }

                    if (rootKids.Length > 23 && rootKids[23] != IntPtr.Zero)
                    {
                        var win = FindRuneforgeInPanel(rootKids[23], readUiOffset, readStdVec);
                        if (win != IntPtr.Zero)
                        {
                            return WalkRuneforgeUi(win, 1, readUiOffset, readStdVec);
                        }
                    }
                }
            }

            // Fallback: search controller container
            var container = ImportantUiElements.GetControllerContainerAddress(gameUiAddress);
            if (container != IntPtr.Zero)
            {
                var win = FindRuneforgeInContainer(container, readUiOffset, readStdVec);
                if (win != IntPtr.Zero)
                {
                    return WalkRuneforgeUi(win, 1, readUiOffset, readStdVec);
                }
            }

            return IntPtr.Zero;
        }

        private static IntPtr FindRuneforgeInPanel(
            IntPtr panel,
            Func<IntPtr, UiElementBaseOffset?> readUiOffset,
            Func<StdVector, IntPtr[]?> readStdVec,
            int depth = 0,
            int maxDepth = 6)
        {
            if (panel == IntPtr.Zero || readUiOffset == null || readStdVec == null || depth > maxDepth)
            {
                return IntPtr.Zero;
            }

            var off = readUiOffset(panel);
            if (off == null || (off.Value.Flags & UiVisibleMask) == 0)
            {
                return IntPtr.Zero;
            }

            var targetFp = PanelFlagFingerprints[0] & ~UiVisibleMask;
            if ((off.Value.Flags & ~UiVisibleMask) == targetFp)
            {
                return panel;
            }

            var kids = readStdVec(off.Value.ChildrensPtr);
            if (kids == null || kids.Length == 0 || kids.Length > 100)
            {
                return IntPtr.Zero;
            }

            foreach (var child in kids)
            {
                if (child == IntPtr.Zero)
                {
                    continue;
                }

                var found = FindRuneforgeInPanel(child, readUiOffset, readStdVec, depth + 1, maxDepth);
                if (found != IntPtr.Zero)
                {
                    return found;
                }
            }

            return IntPtr.Zero;
        }

        private static IntPtr FindRuneforgeInContainer(
            IntPtr container,
            Func<IntPtr, UiElementBaseOffset?> readUiOffset,
            Func<StdVector, IntPtr[]?> readStdVec)
        {
            if (container == IntPtr.Zero || readUiOffset == null || readStdVec == null)
            {
                return IntPtr.Zero;
            }

            var cOff = readUiOffset(container);
            if (cOff == null)
            {
                return IntPtr.Zero;
            }

            var cKids = readStdVec(cOff.Value.ChildrensPtr);
            if (cKids == null || cKids.Length <= 2)
            {
                return IntPtr.Zero;
            }

            var c2 = cKids[2];
            if (c2 == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            var c2Off = readUiOffset(c2);
            if (c2Off == null)
            {
                return IntPtr.Zero;
            }

            var c2Kids = readStdVec(c2Off.Value.ChildrensPtr);
            if (c2Kids == null || c2Kids.Length == 0)
            {
                return IntPtr.Zero;
            }

            var targetFp = PanelFlagFingerprints[0] & ~UiVisibleMask;
            int searchLimit = Math.Min(c2Kids.Length, 4);
            for (int i = 0; i < searchLimit; i++)
            {
                var panelMgr = c2Kids[i];
                if (panelMgr == IntPtr.Zero)
                {
                    continue;
                }

                var pmOff = readUiOffset(panelMgr);
                if (pmOff == null)
                {
                    continue;
                }

                var panels = readStdVec(pmOff.Value.ChildrensPtr);
                if (panels == null || panels.Length == 0 || panels.Length > 100)
                {
                    continue;
                }

                foreach (var cand in panels)
                {
                    if (cand == IntPtr.Zero)
                    {
                        continue;
                    }

                    var candOff = readUiOffset(cand);
                    if (candOff == null || (candOff.Value.Flags & UiVisibleMask) == 0)
                    {
                        continue;
                    }

                    if ((candOff.Value.Flags & ~UiVisibleMask) == targetFp)
                    {
                        return cand;
                    }

                    var candKids = readStdVec(candOff.Value.ChildrensPtr);
                    if (candKids != null && candKids.Length > 0 && candKids.Length <= 4)
                    {
                        foreach (var inner in candKids)
                        {
                            if (inner == IntPtr.Zero)
                            {
                                continue;
                            }

                            var inOff = readUiOffset(inner);
                            if (inOff == null || (inOff.Value.Flags & UiVisibleMask) == 0)
                            {
                                continue;
                            }

                            if ((inOff.Value.Flags & ~UiVisibleMask) == targetFp)
                            {
                                return inner;
                            }
                        }
                    }
                }
            }

            return IntPtr.Zero;
        }

        private static IntPtr WalkRuneforgeUi(
            IntPtr parent,
            int step,
            Func<IntPtr, UiElementBaseOffset?> readUiOffset,
            Func<StdVector, IntPtr[]?> readStdVec)
        {
            if (parent == IntPtr.Zero || readUiOffset == null || readStdVec == null)
            {
                return IntPtr.Zero;
            }

            if (step >= PanelFlagFingerprints.Length)
            {
                return parent;
            }

            var off = readUiOffset(parent);
            if (off == null) return IntPtr.Zero;

            var kids = readStdVec(off.Value.ChildrensPtr);
            if (kids == null || kids.Length == 0) return IntPtr.Zero;

            var targetFp = PanelFlagFingerprints[step] & ~UiVisibleMask;

            // Step 0 is the window container — must be VISIBLE, otherwise panel is closed!
            if (step == 0)
            {
                foreach (var child in kids)
                {
                    if (child == IntPtr.Zero) continue;
                    var coff = readUiOffset(child);
                    if (coff == null) continue;
                    var visible = (coff.Value.Flags & UiVisibleMask) != 0;
                    if (!visible) continue;
                    if ((coff.Value.Flags & ~UiVisibleMask) == targetFp)
                    {
                        var res = WalkRuneforgeUi(child, step + 1, readUiOffset, readStdVec);
                        if (res != IntPtr.Zero) return res;
                    }
                }
                return IntPtr.Zero;
            }

            for (var pass = 0; pass < 2; pass++)
            {
                var wantVisible = pass == 0;
                foreach (var child in kids)
                {
                    if (child == IntPtr.Zero) continue;
                    var coff = readUiOffset(child);
                    if (coff == null) continue;
                    var visible = (coff.Value.Flags & UiVisibleMask) != 0;
                    if (visible != wantVisible) continue;
                    if ((coff.Value.Flags & ~UiVisibleMask) == targetFp)
                    {
                        var res = WalkRuneforgeUi(child, step + 1, readUiOffset, readStdVec);
                        if (res != IntPtr.Zero) return res;
                    }
                }
            }

            return IntPtr.Zero;
        }
    }
}
