// <copyright file="Buffs.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace TEHhub.RemoteObjects.Components
{
    using System;
    using System.Buffers;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using Coroutine;
    using TEHhub.CoroutineEvents;
    using TEHhub.Offsets.Objects.Components;
    using TEHhub.Offsets.Objects.FilesStructures;
    using ImGuiNET;
    using Utils;

    /// <summary>
    ///     The <see cref="Buffs" /> component in the entity.
    /// </summary>
    public class Buffs : ComponentBase
    {
        private static readonly ConcurrentDictionary<IntPtr, (string Name, byte BuffType)> BuffDefinitionCache = new();
        private static readonly ConcurrentDictionary<(string BaseName, uint SkillGemId), string> SkillGemBuffNameCache = new();

        [ThreadStatic]
        private static IntPtr[]? threadLocalPtrArray;

        static Buffs()
        {
            CoroutineHandler.Start(OnAreaChange());
            CoroutineHandler.Start(OnGameClose());
        }

        /// <summary>
        ///     Initializes a new instance of the <see cref="Buffs" /> class.
        /// </summary>
        /// <param name="address">address of the <see cref="Buffs" /> component.</param>
        public Buffs(IntPtr address)
            : base(address) { }

        /// <summary>
        ///     Gets the Buffs/Debuffs associated with the entity.
        ///     This is not updated anymore once entity dies.
        /// </summary>
        public StatusEffectDictionary StatusEffects { get; } = new();

        public bool[] FlaskActive { get; private set; } = new bool[5];

        internal static void ClearStaticCaches()
        {
            BuffDefinitionCache.Clear();
            SkillGemBuffNameCache.Clear();
        }

        private static IEnumerable<Wait> OnAreaChange()
        {
            while (true)
            {
                yield return new(RemoteEvents.AreaChanged);
                ClearStaticCaches();
            }
        }

        private static IEnumerable<Wait> OnGameClose()
        {
            while (true)
            {
                yield return new(TEHhubEvents.OnClose);
                ClearStaticCaches();
            }
        }

        /// <inheritdoc />
        internal override void ToImGui()
        {
            base.ToImGui();
            if (ImGui.TreeNode("Status Effect (Buffs/Debuffs)"))
            {
                foreach (var kv in this.StatusEffects)
                {
                    if (ImGui.TreeNode($"{kv.Key}"))
                    {
                        ImGuiHelper.DisplayTextAndCopyOnClick($"Name: {kv.Key}", kv.Key);
                        ImGuiHelper.IntPtrToImGui("BuffDefinationPtr", kv.Value.BuffDefinationPtr);
                        ImGuiHelper.DisplayFloatWithInfinitySupport("Total Time:", kv.Value.TotalTime);
                        ImGuiHelper.DisplayFloatWithInfinitySupport("Time Left:", kv.Value.TimeLeft);
                        ImGui.Text($"Source Entity Id: {kv.Value.SourceEntityId}");
                        ImGui.Text($"Raw Stage: {kv.Value.RawStage}");
                        ImGui.Text($"Charges: {kv.Value.Charges}");
                        ImGui.Text($"Source FlaskSlot: {kv.Value.FlaskSlot}");
                        ImGui.Text($"Source Effectiveness: {100 + kv.Value.Effectiveness} (raw value: {kv.Value.Effectiveness})");
                        ImGui.Text($"Source UnknownIdAndEquipmentInfo: {kv.Value.UnknownIdAndEquipmentInfo:X}");
                        ImGui.TreePop();
                    }
                }

                ImGui.TreePop();
            }
        }

        /// <inheritdoc />
        protected override void UpdateData(bool hasAddressChanged)
        {
            var reader = Core.Process.Handle;
            var data = reader.ReadMemory<BuffsOffsets>(this.Address);
            this.OwnerEntityAddress = data.Header.EntityPtr;
            this.StatusEffects.Clear();
            Array.Fill(this.FlaskActive, false);

            var byteLength = data.StatusEffectPtr.Last.ToInt64() - data.StatusEffectPtr.First.ToInt64();
            if (byteLength <= 0 || byteLength % IntPtr.Size != 0 || byteLength > 50_000_000)
            {
                return;
            }

            var statusEffectCount = (int)(byteLength / IntPtr.Size);
            var statusEffects = threadLocalPtrArray;
            if (statusEffects == null || statusEffects.Length < statusEffectCount)
            {
                threadLocalPtrArray = statusEffects = new IntPtr[Math.Max(statusEffectCount, 32)];
            }

            if (!reader.TryReadMemoryArray(data.StatusEffectPtr.First, statusEffects, statusEffectCount, out _))
            {
                return;
            }

            // F-129: snapshot the 4-level Player.Id chain once. Each access goes
            // through RemoteObjectBase.Address.get (a lock); re-traversing per-loop
            // is both costly and racy during state transitions. NRE during the
            // chain -> playerId stays uint.MaxValue, all flask matches fail
            // (statusEffectData.SourceEntityId is uint, max value is unreachable).
            uint playerId;
            try
            {
                playerId = Core.States.InGameStateObject.CurrentAreaInstance.Player.Id;
            }
            catch (NullReferenceException)
            {
                playerId = uint.MaxValue;
            }

            for (var i = 0; i < statusEffectCount; i++)
            {
                var statusEffectData = reader.ReadMemory<StatusEffectStruct>(statusEffects[i]);
                if (statusEffectData.BuffDefinationPtr == IntPtr.Zero)
                {
                    continue;
                }

                if (playerId != statusEffectData.SourceEntityId)
                {
                    statusEffectData.FlaskSlot = -1;
                }

                MiscHelper.ActiveSkillGemDataParser(
                    statusEffectData.UnknownIdAndEquipmentInfo,
                    out _,
                    out _,
                    out _,
                    out _,
                    out _,
                    out var skillGemUnknownId);

                if (!BuffDefinitionCache.TryGetValue(statusEffectData.BuffDefinationPtr, out var buffDef))
                {
                    buffDef = GetNameFromBuffDefination(statusEffectData.BuffDefinationPtr);
                    BuffDefinitionCache.TryAdd(statusEffectData.BuffDefinationPtr, buffDef);
                }

                var effectName = buffDef.Name;
                var effectType = buffDef.BuffType;

                if (effectType != 0x4) // Flask Effect Type is 4.
                {
                    statusEffectData.FlaskSlot = -1;
                }
                else if (statusEffectData.FlaskSlot >= 0 && statusEffectData.FlaskSlot < 5)
                {
                    this.FlaskActive[statusEffectData.FlaskSlot] = true;
                }

                if (skillGemUnknownId != 0)
                {
                    if (!SkillGemBuffNameCache.TryGetValue((effectName, skillGemUnknownId), out var combinedName))
                    {
                        combinedName = $"{effectName}_{skillGemUnknownId:X}";
                        SkillGemBuffNameCache.TryAdd((effectName, skillGemUnknownId), combinedName);
                    }

                    effectName = combinedName;
                }

                ref var entry = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(this.StatusEffects, effectName, out var exists);
                if (exists)
                {
                    var incomingStacks = statusEffectData.Charges > 0 ? statusEffectData.Charges : (short)1;
                    statusEffectData.Charges = (short)(entry.Charges + incomingStacks);
                    statusEffectData.TimeLeft = Math.Max(entry.TimeLeft, statusEffectData.TimeLeft);
                    entry = statusEffectData;
                }
                else
                {
                    entry = statusEffectData;
                }
            }
        }

        /// <summary>
        ///     Adds a status effect read from an entity-backed player skill to the player's
        ///     normal status-effect collection. A real player status effect with the same
        ///     name wins unless the synthetic effect reports more stacks.
        /// </summary>
        /// <param name="effectName">Name used by normal status-effect consumers.</param>
        /// <param name="statusEffectData">Status-effect data with its stage represented as charges.</param>
        internal void AddSyntheticStatusEffect(string effectName, StatusEffectStruct statusEffectData)
        {
            ref var entry = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(this.StatusEffects, effectName, out var exists);
            if (exists)
            {
                if (statusEffectData.Charges > entry.Charges)
                {
                    entry = statusEffectData;
                }
            }
            else
            {
                entry = statusEffectData;
            }
        }

        private static (string Name, byte BuffType) GetNameFromBuffDefination(IntPtr addr)
        {
            var reader = Core.Process.Handle;
            var data = reader.ReadMemory<BuffDefinitionsOffset>(addr);
            return (reader.ReadUnicodeString(data.Name), data.BuffType);
        }
    }

    /// <summary>
    ///     Reusable dictionary for status effects that exposes IsEmpty helper.
    /// </summary>
    public sealed class StatusEffectDictionary : Dictionary<string, StatusEffectStruct>
    {
        public StatusEffectDictionary()
            : base(StringComparer.Ordinal)
        {
        }

        public StatusEffectDictionary(int capacity)
            : base(capacity, StringComparer.Ordinal)
        {
        }

        /// <summary>
        ///     Gets a value indicating whether the collection is empty.
        /// </summary>
        public bool IsEmpty => this.Count == 0;
    }
}
