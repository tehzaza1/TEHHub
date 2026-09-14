using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.RegularExpressions;
using TEHhub;
using TEHhub.Offsets.Natives;
using TEHhub.Offsets.Objects.States.InGameState;
using TEHhub.Offsets.Objects.UiElement;
using TEHhub.RemoteEnums;
using TEHhub.RemoteObjects;
using TEHhub.RemoteObjects.Components;
using TEHhub.RemoteObjects.States.InGameStateObjects;
using ImGuiNET;

namespace NinjaPricer
{
    public sealed class MonolithOffer
    {
        public string RecipeId { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Reward { get; set; } = string.Empty;
        public int RewardCount { get; set; } = 1;
        public List<string> Runes { get; set; } = new();
        public float PriceChaos { get; set; } = 0f;
        public float DisplayValue { get; set; } = 0f;
        public int ComboWeight { get; set; } = 0;
        public bool HasRareRune { get; set; } = false;
    }

    public sealed class MonolithData
    {
        public IntPtr EntityAddress { get; set; }
        public int HoleCount { get; set; }
        public int AnchorPos { get; set; }
        public int AnchorIdx { get; set; } = -1;
        public bool IsUnique { get; set; }
        public bool IsCompleted { get; set; }
        public List<int> GoldenSlots { get; set; } = new();
        public Vector3 WorldPos { get; set; }
        public uint Color { get; set; } = 0xFFFFFFFF;
        public List<MonolithOffer> Offers { get; set; } = new();
        public MonolithOffer? BestOffer { get; set; }
    }

    public static class NinjaRuneshapeHelper
    {
        private const int ListenerVectorOffset = 0x20;
        private const int StationOwnerOffset = 0x10;
        private const int StationAnchorRefOffset = 0x28;
        private const int StationAnchorHolderOffset = 0x30;
        private const int StationHoleCountOffset = 0x38;
        private const int StationAnchorPosOffset = 0x3C;
        private const int StationGoldenSlotsOffset = 0x40;
        private const int MinimapCompletedOffset = 0x10;

        public const uint UiVisibleMask = 0x800;

        public static readonly string[] RuneNames = new string[]
        {
            "Fire", "Cold", "Lightning", "Tempest", "Momentum", "Bloodletting",
            "Stone", "Adaptive", "Arcane", "Toxic", "Electrocuting", "Protective",
            "Cyclonic", "Vision", "Tidal", "Rebirth", "Prismatic", "Gasp",
            "Moon", "Celestial", "Opulent", "Rage", "Wisdom", "Sky",
            "Earth", "Life", "Bond", "Ward", "Soul", "Death",
            "Oath", "Time", "Power", "Bait"
        };

        private static readonly uint[] PanelFlagFingerprints =
            { 0x00462EF1, 0x00502EF3, 0x00502EF7, 0x00542EF1, 0x00502EF1 };

        private static IntPtr cachedRecipesContainer = IntPtr.Zero;
        private static DateTime nextPanelResolveUtc = DateTime.MinValue;

        public static bool TryReadMonolith(Entity entity, out MonolithData info)
        {
            info = new MonolithData();
            if (entity.Address == IntPtr.Zero) return false;

            if (!entity.TryGetComponent<StateMachine>(out var sm) || sm.Address == IntPtr.Zero)
                return false;

            var reader = Core.Process.Handle;
            var listeners = reader.ReadMemory<StdVector>(sm.Address + ListenerVectorOffset);
            var nodes = reader.ReadStdVector<long>(listeners);
            if (nodes == null || nodes.Length == 0 || nodes.Length > 256) return false;

            IntPtr station = IntPtr.Zero;
            foreach (var nodeValue in nodes)
            {
                if (nodeValue == 0) continue;
                var sub = reader.ReadMemory<IntPtr>(new IntPtr(nodeValue));
                if (sub == IntPtr.Zero) continue;

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

            if (station == IntPtr.Zero) return false;

            var holeCount = reader.ReadMemory<int>(station + StationHoleCountOffset);
            if (holeCount is <= 0 or > 16) return false;

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

                            if (anchorIdx < 0 || anchorIdx >= RuneNames.Length) anchorIdx = -1;
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

            var goldenSlots = new List<int>();
            var goldenVec = reader.ReadMemory<StdVector>(station + StationGoldenSlotsOffset);
            var goldenCount = goldenVec.TotalElements(sizeof(int));
            if (goldenCount > 0 && goldenCount <= 16)
            {
                var slots = reader.ReadMemoryArray<int>(goldenVec.First, (int)goldenCount);
                if (slots != null)
                {
                    foreach (var s in slots)
                    {
                        if (s >= 0 && s < holeCount && !goldenSlots.Contains(s))
                        {
                            goldenSlots.Add(s);
                        }
                    }
                }
            }
            goldenSlots.Sort();

            if (entity.TryGetComponent<Render>(out var render))
            {
                info.WorldPos = new Vector3(render.WorldPosition.X, render.WorldPosition.Y, render.TerrainHeight);
            }

            info.EntityAddress = entity.Address;
            info.HoleCount = holeCount;
            info.AnchorPos = anchorPos;
            info.AnchorIdx = anchorIdx;
            info.IsUnique = isUnique;
            info.IsCompleted = isCompleted;
            info.GoldenSlots = goldenSlots;

            // Assign color based on anchor or category
            uint[] colors = { 0xFF3333E5, 0xFFE56633, 0xFF33E533, 0xFF33D5E5, 0xFFD533D5 };
            info.Color = isUnique ? 0xFFD533D5 : (anchorIdx >= 0 ? colors[anchorIdx % colors.Length] : 0xFF33D5E5);

            return true;
        }

        public static IntPtr ResolveRuneforgeContainer(IntPtr gameUiAddress)
        {
            var now = DateTime.UtcNow;
            if (now < nextPanelResolveUtc && cachedRecipesContainer != IntPtr.Zero)
            {
                return cachedRecipesContainer;
            }

            nextPanelResolveUtc = now.AddMilliseconds(200);
            if (gameUiAddress == IntPtr.Zero)
            {
                cachedRecipesContainer = IntPtr.Zero;
                return IntPtr.Zero;
            }

            var handle = Core.Process?.Handle;
            if (handle == null) return IntPtr.Zero;

            var importantUi = Core.States.InGameStateObject?.GameUi;
            if (importantUi != null && importantUi.RuneshapeCombinationsPanel.Address != IntPtr.Zero)
            {
                var panelAddr = importantUi.RuneshapeCombinationsPanel.Address;
                if (handle.TryReadMemory<UiElementBaseOffset>(panelAddr, out var pOff))
                {
                    if ((pOff.Flags & UiVisibleMask) != 0)
                    {
                        var found = WalkRuneforgeUi(panelAddr, 1);
                        if (found != IntPtr.Zero)
                        {
                            cachedRecipesContainer = found;
                            return cachedRecipesContainer;
                        }
                    }
                    else if (!Core.GHSettings.EnableControllerMode)
                    {
                        cachedRecipesContainer = IntPtr.Zero;
                        return IntPtr.Zero;
                    }
                }
            }

            cachedRecipesContainer = WalkRuneforgeUi(gameUiAddress, 0);
            return cachedRecipesContainer;
        }

        private static IntPtr WalkRuneforgeUi(IntPtr root, int depth)
        {
            if (root == IntPtr.Zero || depth > 10) return IntPtr.Zero;
            var handle = Core.Process?.Handle;
            if (handle == null) return IntPtr.Zero;

            if (!handle.TryReadMemory<UiElementBaseOffset>(root, out var off)) return IntPtr.Zero;
            if ((off.Flags & UiVisibleMask) == 0) return IntPtr.Zero;

            var children = handle.ReadStdVector<IntPtr>(off.ChildrensPtr);
            if (children == null || children.Length == 0) return IntPtr.Zero;

            if (children.Length > 20 && off.UnscaledSize.X > 100f && off.UnscaledSize.Y > 100f)
            {
                return root;
            }

            foreach (var child in children)
            {
                if (child == IntPtr.Zero) continue;
                var res = WalkRuneforgeUi(child, depth + 1);
                if (res != IntPtr.Zero) return res;
            }

            return IntPtr.Zero;
        }

        public static void ParseRuneforgeRowText(string raw, out int count, out string name)
        {
            count = 1;
            name = (raw ?? string.Empty).Trim();

            var match = Regex.Match(name, @"^(\d+)[xX]\s*(.+)$");
            if (match.Success)
            {
                if (int.TryParse(match.Groups[1].Value, out var c)) count = Math.Max(1, c);
                name = match.Groups[2].Value.Trim();
            }
        }
    }
}
