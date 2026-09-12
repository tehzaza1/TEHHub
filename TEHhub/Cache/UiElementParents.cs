// <copyright file="UiElementParents.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>


namespace TEHhub.Cache
{
    using System;
    using System.Buffers;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;
    using Coroutine;
    using TEHhub.RemoteObjects.UiElement;
    using TEHhub.CoroutineEvents;
    using TEHhub.Offsets.Objects.UiElement;
    using ImGuiNET;
    using TEHhub.RemoteEnums;
    using System.Threading.Tasks;

    internal class UiElementParents
    {
        private const int MaxBatchSpanBytes = 64 * 1024;
        private const int MaxGapAfterUiElementBytes = 0x200;
        private const int MinBatchElements = 4;
        private readonly string name;
        private readonly UiElementParents? grandparent;
        private readonly GameStateTypes ownerState1;
        private readonly GameStateTypes ownerState2;
        private readonly Dictionary<IntPtr, UiElementBase> cache;

        /// <summary>
        ///     Initializes a new instance of the <see cref="UiElementParents" /> class.
        /// </summary>
        /// <param name="grandparent">other Ui Element cache to check</param>
        /// <param name="ownerStateA"><see cref="GameStateTypes"/> on which cache shouldn't be cleaned</param>
        /// <param name="ownerStateB"><see cref="GameStateTypes"/> on which cache shouldn't be cleaned</param>
        /// <param name="name">human friendly name to give to this cache</param>
        public UiElementParents(UiElementParents? grandparent, GameStateTypes ownerStateA, GameStateTypes ownerStateB, string name)
        {
            this.name = name;
            this.ownerState1 = ownerStateA;
            this.ownerState2 = ownerStateB;
            this.cache = new();
            this.grandparent = grandparent;
            CoroutineHandler.Start(this.OnGameClose());
            CoroutineHandler.Start(this.OnStateChange());
        }

        /// <summary>
        ///     Adds a Parent UiElement to the cache if the key doesn't already exist.
        /// </summary>
        /// <param name="address">address pointing to the parent UiElement.</param>
        public void AddIfNotExists(IntPtr address)
        {
            if (address == IntPtr.Zero)
            {
                return;
            }

            if (this.grandparent != null)
            {
                bool inGrandparent;
                lock (this.grandparent.cache)
                {
                    inGrandparent = this.grandparent.cache.ContainsKey(address);
                }

                if (inGrandparent)
                {
                    return;
                }
            }

            lock (this.cache)
            {
                if (!this.cache.ContainsKey(address))
                {
                    try
                    {
                        this.cache.Add(address, new(address, this));
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"Failed to add the UiElement Parent in the cache. 0x{address.ToInt64():X} due to {e}");
                    }
                }
            }
        }

        public bool TryGetParent(IntPtr address, [NotNullWhen(true)] out UiElementBase? parent)
        {
            if (address == IntPtr.Zero)
            {
                parent = null;
                return false;
            }

            lock (this.cache)
            {
                if (this.cache.TryGetValue(address, out parent))
                {
                    return true;
                }
            }

            if (this.grandparent != null)
            {
                lock (this.grandparent.cache)
                {
                    if (this.grandparent.cache.TryGetValue(address, out parent))
                    {
                        return true;
                    }
                }
            }

            parent = null;
            return false;
        }

        public void UpdateAllParentsParallel(bool refreshChildren = true)
        {
            KeyValuePair<IntPtr, UiElementBase>[] snapshot;
            lock (this.cache)
            {
                snapshot = new KeyValuePair<IntPtr, UiElementBase>[this.cache.Count];
                ((ICollection<KeyValuePair<IntPtr, UiElementBase>>)this.cache).CopyTo(snapshot, 0);
            }

            // A cached parent can be freed/reused by the game after we cached it. For ordinary
            // tree navigation preserve the historic full refresh. ImportantUiElements only uses
            // this cache for parent-chain position/visibility, so it can reuse the validation
            // snapshot and skip child-vector reads without making node positions stale.
            if (refreshChildren || snapshot.Length < MinBatchElements)
            {
                this.UpdateParentsIndividually(snapshot, refreshChildren);
                return;
            }

            // Game UI elements are often allocated as nearby fixed-size objects. Combine only
            // adjacent addresses and cap each span; a failed span falls back to the original
            // scalar read path, preserving behaviour across heap/page boundaries.
            Array.Sort(snapshot, static (left, right) => left.Key.CompareTo(right.Key));
            var batches = BuildReadBatches(snapshot);
            var stale = new ConcurrentBag<IntPtr>();
            Parallel.ForEach(batches, batch => RefreshBatch(snapshot, batch, stale));

            if (!stale.IsEmpty)
            {
                lock (this.cache)
                {
                    foreach (var key in stale)
                    {
                        this.cache.Remove(key);
                    }
                }
            }
        }

        private static List<ReadBatch> BuildReadBatches(KeyValuePair<IntPtr, UiElementBase>[] sorted)
        {
            var batches = new List<ReadBatch>();
            var elementSize = Unsafe.SizeOf<UiElementBaseOffset>();
            var start = 0;
            while (start < sorted.Length)
            {
                var firstAddress = sorted[start].Key.ToInt64();
                var end = start + 1;
                var previousAddress = firstAddress;
                while (end < sorted.Length)
                {
                    var nextAddress = sorted[end].Key.ToInt64();
                    var gapAfterPrevious = nextAddress - previousAddress - elementSize;
                    var span = nextAddress - firstAddress + elementSize;
                    if (gapAfterPrevious < 0 || gapAfterPrevious > MaxGapAfterUiElementBytes || span > MaxBatchSpanBytes)
                    {
                        break;
                    }

                    previousAddress = nextAddress;
                    end++;
                }

                batches.Add(new ReadBatch(start, end, checked((int)(previousAddress - firstAddress + elementSize))));
                start = end;
            }

            return batches;
        }

        private void UpdateParentsIndividually(KeyValuePair<IntPtr, UiElementBase>[] snapshot, bool refreshChildren)
        {
            var stale = new ConcurrentBag<IntPtr>();
            Parallel.ForEach(snapshot, data => RefreshIndividually(data, refreshChildren, stale));
            this.RemoveStale(stale);
        }

        private static void RefreshBatch(
            KeyValuePair<IntPtr, UiElementBase>[] sorted,
            ReadBatch batch,
            ConcurrentBag<IntPtr> stale)
        {
            if (batch.Count < MinBatchElements)
            {
                for (var i = batch.StartIndex; i < batch.EndIndex; i++)
                {
                    RefreshIndividually(sorted[i], false, stale);
                }

                return;
            }

            var buffer = ArrayPool<byte>.Shared.Rent(batch.ByteCount);
            try
            {
                var startAddress = sorted[batch.StartIndex].Key;
                if (!Core.Process.Handle.TryReadMemoryArray(startAddress, buffer, batch.ByteCount, out _))
                {
                    for (var i = batch.StartIndex; i < batch.EndIndex; i++)
                    {
                        RefreshIndividually(sorted[i], false, stale);
                    }

                    return;
                }

                var startAddressValue = startAddress.ToInt64();
                var elementSize = Unsafe.SizeOf<UiElementBaseOffset>();
                for (var i = batch.StartIndex; i < batch.EndIndex; i++)
                {
                    var data = sorted[i];
                    var offset = checked((int)(data.Key.ToInt64() - startAddressValue));
                    var uiOffset = MemoryMarshal.Read<UiElementBaseOffset>(buffer.AsSpan(offset, elementSize));
                    if (!data.Value.TryRefreshParentData(uiOffset))
                    {
                        stale.Add(data.Key);
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"Failed to batch-update UiElement Parents at 0x{sorted[batch.StartIndex].Key.ToInt64():X} due to {e}");
                for (var i = batch.StartIndex; i < batch.EndIndex; i++)
                {
                    RefreshIndividually(sorted[i], false, stale);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private static void RefreshIndividually(
            KeyValuePair<IntPtr, UiElementBase> data,
            bool refreshChildren,
            ConcurrentBag<IntPtr> stale)
        {
            try
            {
                var offsets = Core.Process.Handle.ReadMemory<UiElementBaseOffset>(data.Key);
                if (offsets.Self != IntPtr.Zero && offsets.Self != data.Key)
                {
                    stale.Add(data.Key);
                    return;
                }

                if (refreshChildren)
                {
                    data.Value.Address = data.Key;
                }
                else if (!data.Value.TryRefreshParentData(offsets))
                {
                    stale.Add(data.Key);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"Failed to update the UiElement Parent in the cache. 0x{data.Key.ToInt64():X} due to {e}");
            }
        }

        private void RemoveStale(ConcurrentBag<IntPtr> stale)
        {
            if (stale.IsEmpty)
            {
                return;
            }

            lock (this.cache)
            {
                foreach (var key in stale)
                {
                    this.cache.Remove(key);
                }
            }
        }

        private readonly record struct ReadBatch(int StartIndex, int EndIndex, int ByteCount)
        {
            internal int Count => this.EndIndex - this.StartIndex;
        }

        public void Clear()
        {
            lock (this.cache)
            {
                this.cache.Clear();
            }
        }

        public void ToImGui()
        {
            KeyValuePair<IntPtr, UiElementBase>[] snapshot;
            lock (this.cache)
            {
                snapshot = new KeyValuePair<IntPtr, UiElementBase>[this.cache.Count];
                ((ICollection<KeyValuePair<IntPtr, UiElementBase>>)this.cache).CopyTo(snapshot, 0);
            }

            ImGui.Text($"Total Size: {snapshot.Length}");
            if (ImGui.TreeNode($"{this.name} Parent UiElements"))
            {
                foreach (var (key, value) in snapshot)
                {
                    if (ImGui.TreeNode($"0x{key.ToInt64():X}"))
                    {
                        value.ToImGui();
                        ImGui.TreePop();
                    }
                }

                ImGui.TreePop();
            }
        }

        private IEnumerable<Wait> OnGameClose()
        {
            while (true)
            {
                yield return new(TEHhubEvents.OnClose);
                try
                {
                    lock (this.cache)
                    {
                        this.cache.Clear();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[UiElementParents.OnGameClose] {ex}");
                }
            }
        }

        private IEnumerable<Wait> OnStateChange()
        {
            while (true)
            {
                yield return new(RemoteEvents.StateChanged);
                try
                {
                    if (Core.States.GameCurrentState != this.ownerState1 &&
                        Core.States.GameCurrentState != this.ownerState2)
                    {
                        lock (this.cache)
                        {
                            this.cache.Clear();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[UiElementParents.OnStateChange] {ex}");
                }
            }
        }
    }
}
