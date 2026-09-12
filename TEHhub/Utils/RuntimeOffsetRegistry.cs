namespace TEHhub.Utils
{
    using System;
    using System.Buffers;
    using System.Collections.Concurrent;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;
    using System.Threading;

    internal sealed record RuntimeOffsetField(string Name, int OriginalOffset, int RecoveredOffset, int Size);

    internal sealed record RuntimeOffsetPatch(Type StructType, RuntimeOffsetField[] Fields, int ReadSize, string[]? ComponentNames = null);

    /// <summary>
    /// Session-only projections of recovered native members into the compiled C# layout.
    /// No target-process memory or FieldOffset attributes are ever written.
    /// </summary>
    internal static class RuntimeOffsetRegistry
    {
        private static readonly ConcurrentDictionary<Type, RuntimeOffsetPatch> Patches = new();
        private static readonly ConcurrentDictionary<string, RuntimeOffsetPatch> ComponentPatches = new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, byte> BlockedComponents = new(StringComparer.Ordinal);
        private static int generation;
        private static int blockedCount;

        internal static int Generation => Volatile.Read(ref generation);

        private static class Slot<T> where T : unmanaged
        {
            internal static RuntimeOffsetPatch? Patch;
            internal static int ArrayObserved;
        }

        internal static bool IsComponentBlocked(string name) =>
            Volatile.Read(ref blockedCount) != 0 && BlockedComponents.ContainsKey(name);

        internal static void SetComponentBlocked(string name, bool blocked)
        {
            var changed = blocked
                ? BlockedComponents.TryAdd(name, 0)
                : BlockedComponents.TryRemove(name, out _);
            if (changed)
            {
                Volatile.Write(ref blockedCount, BlockedComponents.Count);
                Interlocked.Increment(ref generation);
            }
        }

        internal static void Install(RuntimeOffsetPatch patch)
        {
            if (WasReadAsArray(patch.StructType)) throw new InvalidOperationException("Native array stride is unverified.");
            SetSlot(patch.StructType, patch);
            Patches[patch.StructType] = patch;
            foreach (var name in patch.ComponentNames ?? []) ComponentPatches[name] = patch;
            Interlocked.Increment(ref generation);
        }

        internal static void Remove(Type type)
        {
            if (Patches.TryRemove(type, out _))
            {
                SetSlot(type, null);
                foreach (var component in ComponentPatches.Where(c => c.Value.StructType == type))
                    ComponentPatches.TryRemove(component.Key, out _);
                Interlocked.Increment(ref generation);
            }
        }

        internal static void Reset()
        {
            foreach (var type in Patches.Keys)
            {
                Remove(type);
            }

            BlockedComponents.Clear();
            ComponentPatches.Clear();
            Volatile.Write(ref blockedCount, 0);
            Interlocked.Increment(ref generation);
        }

        internal static bool HasPatch<T>() where T : unmanaged =>
            Volatile.Read(ref Slot<T>.Patch) != null;

        /// <summary>
        /// Native arrays have a separate stride contract. A scalar member recovery is not proof
        /// that the compiled element stride survived, so arrays are deliberately not projected.
        /// </summary>
        internal static bool NoteArrayRead<T>() where T : unmanaged
        {
            Volatile.Write(ref Slot<T>.ArrayObserved, 1);
            if (!HasPatch<T>()) return true;
            Remove(typeof(T));
            return false;
        }

        internal static IntPtr ComponentHeaderAddress(string componentName, IntPtr address)
        {
            if (!ComponentPatches.IsEmpty && ComponentPatches.TryGetValue(componentName, out var patch))
            {
                foreach (var field in patch.Fields)
                    if (field.Name == "Header") return address + field.RecoveredOffset;
            }

            return address;
        }

        internal static bool WasReadAsArray(Type type) =>
            (bool)typeof(RuntimeOffsetRegistry)
                .GetMethod(nameof(WasReadAsArrayCore), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(type).Invoke(null, null)!;

        internal static bool TryReadPatched<T>(SafeMemoryHandle reader, IntPtr address, out T result)
            where T : unmanaged
        {
            result = default;
            var patch = Volatile.Read(ref Slot<T>.Patch);
            if (patch == null)
            {
                return false;
            }

            var buffer = ArrayPool<byte>.Shared.Rent(patch.ReadSize);
            try
            {
                // byte[] reads bypass this typed hook and the frame caches. Read one bounded
                // source window so overlapping moves cannot overwrite another field's source.
                if (!reader.TryReadMemoryArray(address, buffer, patch.ReadSize, out _))
                {
                    return false;
                }

                result = MemoryMarshal.Read<T>(buffer.AsSpan(0, Unsafe.SizeOf<T>()));
                var destination = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref result, 1));
                foreach (var field in patch.Fields)
                {
                    buffer.AsSpan(field.RecoveredOffset, field.Size)
                        .CopyTo(destination.Slice(field.OriginalOffset, field.Size));
                }

                // A monitoring sweep may revoke the patch while a worker is reading it.
                if (!ReferenceEquals(patch, Volatile.Read(ref Slot<T>.Patch)))
                {
                    result = default;
                    return false;
                }

                return true;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private static bool WasReadAsArrayCore<T>() where T : unmanaged =>
            Volatile.Read(ref Slot<T>.ArrayObserved) != 0;

        private static void SetSlot(Type type, RuntimeOffsetPatch? patch) =>
            typeof(RuntimeOffsetRegistry)
                .GetMethod(nameof(SetSlotCore), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(type).Invoke(null, new object?[] { patch });

        private static void SetSlotCore<T>(RuntimeOffsetPatch? patch) where T : unmanaged =>
            Volatile.Write(ref Slot<T>.Patch, patch);
    }
}
