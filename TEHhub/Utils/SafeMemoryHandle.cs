// <copyright file="SafeMemoryHandle.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace TEHhub.Utils
{
    using System;
    using System.Buffers;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using TEHhub.Offsets.Natives;
    using Microsoft.Win32.SafeHandles;

    /// <summary>
    ///     Handle to a process.
    /// </summary>
    public class SafeMemoryHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        /// <summary>
        ///     Max valid user-mode address on 64-bit Windows (48-bit addressing).
        /// </summary>
        private const long MaxUserModeAddress = 0x7FFFFFFFFFFF;

        /// <summary>
        ///     Lowest address that can ever back a real allocation. The first 64 KiB is the
        ///     reserved null-pointer partition on Windows x64, and allocation granularity is
        ///     64 KiB, so anything below this is never a valid pointer. Game pointers live far
        ///     above this (module base 0x140000000+, heap in the TiB range), so this floor never
        ///     rejects a legitimate read — it only catches data values (floats, small ints,
        ///     string fragments) that a torn read fed in where a pointer was expected.
        /// </summary>
        private const long MinValidAddress = 0x10000;

        // A per-worker read-through window used by hot object graphs that are known to be
        // allocated close together. It is deliberately thread-local: Entity updates run in
        // parallel and no worker may observe another worker's in-flight snapshot.
        [ThreadStatic]
        private static ReadCacheWindow? currentReadCacheWindow;

        // Optional frame-wide read plan. Like the existing component window this is
        // thread-local because entity refreshes may run on different workers in other
        // call paths. The frame snapshot path itself executes on one worker, so its
        // buffers never need synchronization.
        [ThreadStatic]
        private static ReadCachePlan? currentReadCachePlan;

        /// <summary>
        ///     Required by SafeHandle infrastructure for finalizer / marshaling support.
        ///     Private to prevent callers accidentally constructing a zombie handle
        ///     without a PID — see audit F-034. Real construction must go through
        ///     the <see cref="SafeMemoryHandle(int)"/> ctor.
        /// </summary>
        private SafeMemoryHandle()
            : base(true)
        {
        }

        /// <summary>
        ///     Initializes a new instance of the <see cref="SafeMemoryHandle" /> class.
        /// </summary>
        /// <param name="processId">processId you want to access.</param>
        internal SafeMemoryHandle(int processId)
            : base(true)
        {
            var handle = NativeProcessMemory.OpenForRead(processId);
            if (handle == IntPtr.Zero)
            {
                Console.WriteLine($"Failed to open a new handle 0x{handle:X}" +
                                  $" due to ErrorNo: {NativeProcessMemory.LastError}");
            }
            else
            {
                Console.WriteLine($"Opened a new handle using IntPtr 0x{handle:X}");
            }

            this.SetHandle(handle);
        }

        /// <summary>
        ///     Reads the process memory as type T.
        /// </summary>
        /// <typeparam name="T">type of data structure to read.</typeparam>
        /// <param name="address">address to read the data from.</param>
        /// <returns>data from the process in T format.</returns>
        public T ReadMemory<T>(IntPtr address)
            where T : unmanaged
        {
            if (this.TryReadMemory<T>(address, out var result))
            {
                return result;
            }

            // Only a genuine read failure on a plausible address warrants a console error;
            // an out-of-range address is silently skipped (historical behaviour). Both cases
            // are still captured by the diagnostics window via TryReadMemory.
            if (!this.IsInvalid && IsValidAddress(address))
            {
                Console.WriteLine("ERROR: Failed To Read the Memory (T)" +
                                  $" due to Error Number: 0x{NativeProcessMemory.LastError:X} on " +
                                  $"adress 0x{address.ToInt64():X} for type {typeof(T).Name}" +
                                  $" [caller: {DescribeCaller()}]");
            }

            return default;
        }

        /// <summary>
        ///     Reads the process memory as type T. Returns false
        ///     (and <paramref name="result"/> = default) when the handle is invalid, the
        ///     address fails the <see cref="IsValidAddress"/> sanity check, or the underlying
        ///     read fails. Use this on hot, inherently-racy paths (e.g. walking a live,
        ///     concurrently-mutated container) where torn reads are expected and recoverable,
        ///     so they don't flood the log. Use <see cref="ReadMemory{T}"/> elsewhere so a
        ///     genuine offset/layout breakage still surfaces.
        /// </summary>
        /// <typeparam name="T">type of data structure to read.</typeparam>
        /// <param name="address">address to read the data from.</param>
        /// <param name="result">data read from the process, or default on failure.</param>
        /// <param name="recordFailure">whether a failed read should enter diagnostics.</param>
        /// <returns>true if the read succeeded; otherwise false.</returns>
        public bool TryReadMemory<T>(IntPtr address, out T result, bool recordFailure = true)
            where T : unmanaged
        {
            result = default;
            if (this.IsInvalid || !IsValidAddress(address))
            {
                if (recordFailure)
                {
                    RecordDiagnosticFailure(typeof(T).Name, address);
                }
                return false;
            }

            if (TryReadFromCurrentCache(address, out result))
            {
                return true;
            }

            // When EnableNewMemoryRead is active, lazily materialize an exact read for a
            // scalar address that was not known while the plan was built, caching it for the
            // remainder of the frame. This remains bounded; a failed read falls through to
            // the exact legacy scalar read below.
            if (Core.GHSettings.EnableNewMemoryRead &&
                currentReadCachePlan?.TryReadDynamic(this, address.ToInt64(), out result) == true)
            {
                return true;
            }

            try
            {
                var measureRead = Ui.MemoryReadDiagnostics.IsRecording;
                var startedAt = measureRead ? Stopwatch.GetTimestamp() : 0;
                var succeeded = NativeProcessMemory.TryRead(this.handle, address, out result, out var bytesRead);
                var expectedBytes = (nuint)Unsafe.SizeOf<T>();
                if (measureRead)
                {
                    Ui.MemoryReadDiagnostics.RecordRead(
                        Ui.MemoryReadKind.Scalar,
                        (long)expectedBytes,
                        Stopwatch.GetTimestamp() - startedAt,
                        succeeded && bytesRead == expectedBytes);
                }

                if (!succeeded || bytesRead != expectedBytes)
                {
                    result = default;
                    if (recordFailure)
                    {
                        RecordDiagnosticFailure(typeof(T).Name, address);
                    }
                    return false;
                }

                return true;
            }
            catch
            {
                result = default;
                if (recordFailure)
                {
                    RecordDiagnosticFailure(typeof(T).Name, address);
                }
                return false;
            }
        }

        /// <summary>
        ///     Records a failed read into the diagnostics window when it is enabled. Gated up
        ///     front so the (stack-walking) caller lookup is never paid in normal operation.
        /// </summary>
        /// <param name="typeName">name of the type that failed to read.</param>
        /// <param name="address">the address that failed.</param>
        private static void RecordDiagnosticFailure(string typeName, IntPtr address)
        {
            if (!Ui.MemoryReadDiagnostics.IsRecording)
            {
                return;
            }

            Ui.MemoryReadDiagnostics.RecordFailure(typeName, DescribeCaller(), address.ToInt64());
        }

        /// <summary>
        ///     Cheap sanity check that an address could plausibly back a real allocation in
        ///     the target process. Rejects the reserved low memory range and addresses beyond
        ///     the 48-bit user-mode limit. Does not guarantee the address is currently mapped.
        /// </summary>
        /// <param name="address">address to check.</param>
        /// <returns>true if the address is within the plausible user-mode range.</returns>
        internal static bool IsValidAddress(IntPtr address)
        {
            var addr = address.ToInt64();
            return addr >= MinValidAddress && addr <= MaxUserModeAddress;
        }

        /// <summary>
        ///     Reads the std::vector into an array.
        /// </summary>
        /// <typeparam name="T">Object type to read.</typeparam>
        /// <param name="nativeContainer">StdVector address to read from.</param>
        /// <returns>An array of elements of type T.</returns>
        internal T[] ReadStdVector<T>(StdVector nativeContainer)
            where T : unmanaged
        {
            var typeSize = Unsafe.SizeOf<T>();
            var length = nativeContainer.Last.ToInt64() - nativeContainer.First.ToInt64();
            if (length <= 0 || length % typeSize != 0 || length > 50_000_000)
            {
                return Array.Empty<T>();
            }

            return this.ReadMemoryArray<T>(nativeContainer.First, (int)length / typeSize);
        }

        /// <summary>
        ///     Reads the process memory as an array.
        /// </summary>
        /// <typeparam name="T">Array type to read.</typeparam>
        /// <param name="address">memory address to read from.</param>
        /// <param name="nsize">total array elements to read.</param>
        /// <returns>
        ///     An array of type T and of size nsize. In case or any error it returns empty array.
        /// </returns>
        internal T[] ReadMemoryArray<T>(IntPtr address, int nsize)
            where T : unmanaged
        {
            if (this.IsInvalid || !IsValidAddress(address) || nsize <= 0)
            {
                if (nsize > 0)
                {
                    RecordDiagnosticFailure($"{typeof(T).Name}[]", address);
                }

                return Array.Empty<T>();
            }

            var buffer = new T[nsize];
            return this.TryReadMemoryArray(address, buffer, out _)
                ? buffer
                : Array.Empty<T>();
        }

        /// <summary>
        ///     Reads into a caller-owned array without allocating a second buffer.
        ///     Exposed for plugins that need bulk reads while sharing the process handle,
        ///     validation and diagnostics owned by TEHhub.
        /// </summary>
        /// <typeparam name="T">unmanaged array element type.</typeparam>
        /// <param name="address">source address in the target process.</param>
        /// <param name="buffer">destination array.</param>
        /// <param name="bytesRead">number of bytes copied by Windows.</param>
        /// <returns>true only when the complete requested buffer was read.</returns>
        public bool TryReadMemoryArray<T>(IntPtr address, T[] buffer, out nuint bytesRead)
            where T : unmanaged
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return this.TryReadMemoryArray(address, buffer, buffer.Length, out bytesRead);
        }

        /// <summary>
        ///     Reads a prefix of a caller-owned buffer. This permits pooled buffers to be used
        ///     for an exact native read without exposing unused trailing capacity to the target.
        /// </summary>
        internal bool TryReadMemoryArray<T>(IntPtr address, T[] buffer, int elementCount, out nuint bytesRead)
            where T : unmanaged
        {
            ArgumentNullException.ThrowIfNull(buffer);
            bytesRead = 0;
            ArgumentOutOfRangeException.ThrowIfNegative(elementCount);
            if (elementCount > buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(elementCount));
            }

            if (elementCount == 0)
            {
                return true;
            }

            if (this.IsInvalid || !IsValidAddress(address))
            {
                RecordDiagnosticFailure($"{typeof(T).Name}[]", address);
                return false;
            }

            try
            {
                var expectedBytes = checked((nuint)elementCount * (nuint)Unsafe.SizeOf<T>());
                var measureRead = Ui.MemoryReadDiagnostics.IsRecording;
                var startedAt = measureRead ? Stopwatch.GetTimestamp() : 0;
                var succeeded = NativeProcessMemory.TryRead(this.handle, address, buffer, elementCount, out bytesRead);
                var complete = succeeded && bytesRead == expectedBytes;
                if (measureRead)
                {
                    Ui.MemoryReadDiagnostics.RecordRead(
                        Ui.MemoryReadKind.Buffer,
                        (long)expectedBytes,
                        Stopwatch.GetTimestamp() - startedAt,
                        complete);
                }

                if (!complete)
                {
                    RecordDiagnosticFailure($"{typeof(T).Name}[]", address);
                    return false;
                }

                return true;
            }
            catch
            {
                RecordDiagnosticFailure($"{typeof(T).Name}[]", address);
                return false;
            }
        }

        /// <summary>
        ///     Reads the std::string. String read is in ASCII format.
        /// </summary>
        /// <param name="nativecontainer">native object of std::string.</param>
        /// <returns>string.</returns>
        internal string ReadStdString(StdString nativecontainer)
        {
            const int MaxAllowed = 1000;
            if (nativecontainer.Length <= 0 ||
                nativecontainer.Length > MaxAllowed ||
                nativecontainer.Capacity <= 0 ||
                nativecontainer.Capacity > MaxAllowed)
            {
                return string.Empty;
            }

            if (nativecontainer.Capacity <= 15)
            {
                var buffer = BitConverter.GetBytes(nativecontainer.Buffer.ToInt64());
                var ret = Encoding.ASCII.GetString(buffer);
                buffer = BitConverter.GetBytes(nativecontainer.ReservedBytes.ToInt64());
                ret += Encoding.ASCII.GetString(buffer);
                if (nativecontainer.Length < ret.Length)
                {
                    return ret[..nativecontainer.Length];
                }
                else
                {
                    return string.Empty;
                }
            }
            else
            {
                var buffer = this.ReadMemoryArray<byte>(nativecontainer.Buffer, nativecontainer.Length);
                return Encoding.ASCII.GetString(buffer);
            }
        }

        /// <summary>
        ///     Reads the std::wstring. String read is in unicode format.
        /// </summary>
        /// <param name="nativecontainer">native object of std::wstring.</param>
        /// <returns>string.</returns>
        internal string ReadStdWString(StdWString nativecontainer)
        {
            const int MaxAllowed = 1000;
            if (nativecontainer.Length <= 0 ||
                nativecontainer.Length > MaxAllowed ||
                nativecontainer.Capacity <= 0 ||
                nativecontainer.Capacity > MaxAllowed)
            {
                return string.Empty;
            }

            if (nativecontainer.Capacity <= 8)
            {
                var buffer = BitConverter.GetBytes(nativecontainer.Buffer.ToInt64());
                var ret = Encoding.Unicode.GetString(buffer);
                buffer = BitConverter.GetBytes(nativecontainer.ReservedBytes.ToInt64());
                ret += Encoding.Unicode.GetString(buffer);
                if (nativecontainer.Length <= ret.Length)
                {
                    return ret[..nativecontainer.Length];
                }
                else
                {
                    return string.Empty;
                }
            }
            else
            {
                var byteLength = nativecontainer.Length * 2;
                var buffer = ArrayPool<byte>.Shared.Rent(byteLength);
                try
                {
                    if (!this.TryReadMemoryArray(nativecontainer.Buffer, buffer, byteLength, out _))
                    {
                        return string.Empty;
                    }

                    return Encoding.Unicode.GetString(buffer, 0, byteLength);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }

        /// <summary>
        ///     Reads one bounded memory range and makes complete scalar reads within that range
        ///     available to the current worker without another kernel transition. A failed batch
        ///     intentionally returns an inactive scope; callers then retain the usual scalar
        ///     read behaviour.
        /// </summary>
        /// <remarks>
        ///     The scope is meant for a short, coherent refresh operation only. It is not a
        ///     general cache: disposing it restores the previous worker-local window and returns
        ///     its pooled buffer immediately.
        /// </remarks>
        internal ReadCacheScope BeginReadCache(IntPtr startAddress, int byteCount)
        {
            if (byteCount <= 0 || this.IsInvalid || !IsValidAddress(startAddress))
            {
                return default;
            }

            // A frame-wide plan already contains this entity-local span. Do not issue the
            // same kernel read again; TryReadMemory will resolve each scalar directly from
            // the plan while the caller keeps its normal loop and validation behaviour.
            if (currentReadCachePlan?.Contains(startAddress.ToInt64(), byteCount) == true)
            {
                return default;
            }

            var buffer = ArrayPool<byte>.Shared.Rent(byteCount);
            if (!this.TryReadMemoryArray(startAddress, buffer, byteCount, out _))
            {
                ArrayPool<byte>.Shared.Return(buffer);
                return default;
            }

            var previous = currentReadCacheWindow;
            currentReadCacheWindow = new ReadCacheWindow(startAddress.ToInt64(), byteCount, buffer);
            return new ReadCacheScope(buffer, previous);
        }

        /// <summary>
        ///     Starts an optional frame-wide read plan. Ranges must be sorted and should be
        ///     coalesced by the caller. Failed ranges are simply omitted, so the normal scalar
        ///     fallback remains available for torn or unmapped spans.
        /// </summary>
        internal ReadCachePlanScope BeginReadCachePlan(
            IReadOnlyList<ReadCacheRange> ranges,
            bool enableDynamicCache = false)
        {
            if (ranges is null || this.IsInvalid || (ranges.Count == 0 && !enableDynamicCache))
            {
                return default;
            }

            var windows = new List<ReadCacheWindow>(ranges.Count);
            foreach (var range in ranges)
            {
                if (range.ByteCount <= 0 || !IsValidAddress(range.StartAddress))
                {
                    continue;
                }

                var buffer = ArrayPool<byte>.Shared.Rent(range.ByteCount);
                if (!this.TryReadMemoryArray(range.StartAddress, buffer, range.ByteCount, out _))
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    continue;
                }

                windows.Add(new ReadCacheWindow(range.StartAddress.ToInt64(), range.ByteCount, buffer));
            }

            if (windows.Count == 0 && !enableDynamicCache)
            {
                return default;
            }

            var plan = new ReadCachePlan(windows.ToArray(), enableDynamicCache);
            var previous = currentReadCachePlan;
            currentReadCachePlan = plan;
            return new ReadCachePlanScope(plan, previous);
        }

        private static bool TryReadFromCurrentCache<T>(IntPtr address, out T result)
            where T : unmanaged
        {
            result = default;
            var cache = currentReadCacheWindow;
            if (cache is not null)
            {
                var offset = address.ToInt64() - cache.StartAddress;
                var size = Unsafe.SizeOf<T>();
                if (offset >= 0 && offset <= cache.ByteCount - size)
                {
                    result = MemoryMarshal.Read<T>(cache.Buffer.AsSpan((int)offset, size));
                    return true;
                }
            }

            return currentReadCachePlan?.TryRead(address.ToInt64(), out result) == true;
        }

        /// <summary>
        ///     Owns one worker-local read-through window.
        /// </summary>
        internal readonly struct ReadCacheScope : IDisposable
        {
            private readonly byte[]? buffer;
            private readonly ReadCacheWindow? previous;

            internal ReadCacheScope(byte[] buffer, ReadCacheWindow? previous)
            {
                this.buffer = buffer;
                this.previous = previous;
            }

            public void Dispose()
            {
                if (this.buffer is null)
                {
                    return;
                }

                currentReadCacheWindow = this.previous;
                ArrayPool<byte>.Shared.Return(this.buffer);
            }
        }

        internal sealed class ReadCacheWindow
        {
            internal ReadCacheWindow(long startAddress, int byteCount, byte[] buffer)
            {
                this.StartAddress = startAddress;
                this.ByteCount = byteCount;
                this.Buffer = buffer;
            }

            internal long StartAddress { get; }

            internal int ByteCount { get; }

            internal byte[] Buffer { get; }
        }

        /// <summary>
        ///     One range in a frame-wide read plan.
        /// </summary>
        internal readonly struct ReadCacheRange
        {
            internal ReadCacheRange(IntPtr startAddress, int byteCount)
            {
                this.StartAddress = startAddress;
                this.ByteCount = byteCount;
            }

            internal IntPtr StartAddress { get; }

            internal int ByteCount { get; }
        }

        internal sealed class ReadCachePlan : IDisposable
        {
            private const int CompactBlockSize = 128;
            private const int CompactPromotionThreshold = 2;
            private const int MaxDynamicWindows = 2048;

            private readonly ReadCacheWindow[] windows;
            private readonly Dictionary<long, ReadCacheWindow>? exactWindows;
            private readonly Dictionary<long, ReadCacheWindow>? compactWindows;
            private readonly Dictionary<long, int>? blockAccessCounts;

            internal ReadCachePlan(ReadCacheWindow[] windows, bool enableDynamicCache)
            {
                this.windows = windows;
                if (enableDynamicCache)
                {
                    this.exactWindows = new();
                    this.compactWindows = new();
                    this.blockAccessCounts = new();
                }
            }

            internal bool Contains(long startAddress, int byteCount)
            {
                if (byteCount <= 0)
                {
                    return false;
                }

                var window = this.FindWindow(startAddress);
                return window is not null &&
                    startAddress >= window.StartAddress &&
                    startAddress <= window.StartAddress + window.ByteCount - byteCount;
            }

            internal bool TryRead<T>(long address, out T result)
                where T : unmanaged
            {
                result = default;
                var size = Unsafe.SizeOf<T>();
                var window = this.FindWindow(address);
                if (window is null)
                {
                    return false;
                }

                var offset = address - window.StartAddress;
                if (offset < 0 || offset > window.ByteCount - size)
                {
                    return false;
                }

                result = MemoryMarshal.Read<T>(window.Buffer.AsSpan((int)offset, size));
                return true;
            }

            internal bool TryReadDynamic<T>(SafeMemoryHandle reader, long address, out T result)
                where T : unmanaged
            {
                result = default;
                if (this.exactWindows is null || this.compactWindows is null || this.blockAccessCounts is null)
                {
                    return false;
                }

                var size = Unsafe.SizeOf<T>();
                if (size <= 0)
                {
                    return false;
                }

                var blockStart = address & ~(CompactBlockSize - 1L);
                var offsetInBlock = address - blockStart;
                var fitsInBlock = size <= CompactBlockSize && offsetInBlock + size <= CompactBlockSize;

                // 1. Check compact 128B window cache (O(1) dictionary lookup by aligned block start)
                if (fitsInBlock && this.compactWindows.TryGetValue(blockStart, out var compactWindow))
                {
                    if (offsetInBlock + size <= compactWindow.ByteCount)
                    {
                        result = MemoryMarshal.Read<T>(compactWindow.Buffer.AsSpan((int)offsetInBlock, size));
                        return true;
                    }
                }

                // 2. Check exact window cache (O(1) dictionary lookup by exact address)
                if (this.exactWindows.TryGetValue(address, out var existingSameAddressExact))
                {
                    if (existingSameAddressExact.ByteCount >= size)
                    {
                        result = MemoryMarshal.Read<T>(existingSameAddressExact.Buffer.AsSpan(0, size));
                        return true;
                    }
                }
                else
                {
                    // Secondary containment check across exact entries (e.g. sub-range inside larger exact read)
                    foreach (var w in this.exactWindows.Values)
                    {
                        var offset = address - w.StartAddress;
                        if (offset >= 0 && offset <= w.ByteCount - size)
                        {
                            result = MemoryMarshal.Read<T>(w.Buffer.AsSpan((int)offset, size));
                            return true;
                        }
                    }
                }

                // 3. Adaptive 128B Promotion on locality evidence (miss in 128B block with access count >= threshold)
                if (fitsInBlock && IsValidAddress(new IntPtr(blockStart)))
                {
                    var count = this.blockAccessCounts.GetValueOrDefault(blockStart, 0) + 1;
                    this.blockAccessCounts[blockStart] = count;

                    if (count >= CompactPromotionThreshold &&
                        (this.compactWindows.Count + this.exactWindows.Count) < MaxDynamicWindows)
                    {
                        var compactBuffer = ArrayPool<byte>.Shared.Rent(CompactBlockSize);
                        if (reader.TryReadMemoryArray(new IntPtr(blockStart), compactBuffer, CompactBlockSize, out _))
                        {
                            var newCompactWindow = new ReadCacheWindow(blockStart, CompactBlockSize, compactBuffer);
                            this.compactWindows[blockStart] = newCompactWindow;

                            // Safely remove and return any exact entries superseded by this compact window
                            if (this.exactWindows.Count > 0)
                            {
                                List<long>? toRemove = null;
                                foreach (var (addr, w) in this.exactWindows)
                                {
                                    if (addr >= blockStart && addr + w.ByteCount <= blockStart + CompactBlockSize)
                                    {
                                        toRemove ??= new();
                                        toRemove.Add(addr);
                                    }
                                }

                                if (toRemove is not null)
                                {
                                    for (int i = 0; i < toRemove.Count; i++)
                                    {
                                        var addr = toRemove[i];
                                        if (this.exactWindows.Remove(addr, out var oldExact))
                                        {
                                            ArrayPool<byte>.Shared.Return(oldExact.Buffer);
                                        }
                                    }
                                }
                            }

                            result = MemoryMarshal.Read<T>(compactBuffer.AsSpan((int)offsetInBlock, size));
                            return true;
                        }

                        // Promotion read failed; safely return the rented buffer and preserve prior exact cache
                        ArrayPool<byte>.Shared.Return(compactBuffer);
                    }
                }

                // 4. Exact-First Read (first access, non-promoted, promotion failure, or cross-boundary requests)
                if (existingSameAddressExact is null && (this.compactWindows.Count + this.exactWindows.Count) >= MaxDynamicWindows)
                {
                    return false;
                }

                if (!IsValidAddress(new IntPtr(address)))
                {
                    return false;
                }

                var exactBuffer = ArrayPool<byte>.Shared.Rent(size);
                if (!reader.TryReadMemoryArray(new IntPtr(address), exactBuffer, size, out _))
                {
                    ArrayPool<byte>.Shared.Return(exactBuffer);
                    return false;
                }

                var newExactWindow = new ReadCacheWindow(address, size, exactBuffer);
                this.exactWindows[address] = newExactWindow;

                if (existingSameAddressExact is not null)
                {
                    ArrayPool<byte>.Shared.Return(existingSameAddressExact.Buffer);
                }

                result = MemoryMarshal.Read<T>(exactBuffer.AsSpan(0, size));
                return true;
            }

            public void Dispose()
            {
                foreach (var window in this.windows)
                {
                    ArrayPool<byte>.Shared.Return(window.Buffer);
                }

                if (this.compactWindows is not null)
                {
                    foreach (var window in this.compactWindows.Values)
                    {
                        ArrayPool<byte>.Shared.Return(window.Buffer);
                    }

                    this.compactWindows.Clear();
                }

                if (this.exactWindows is not null)
                {
                    foreach (var window in this.exactWindows.Values)
                    {
                        ArrayPool<byte>.Shared.Return(window.Buffer);
                    }

                    this.exactWindows.Clear();
                }

                this.blockAccessCounts?.Clear();
            }

            private ReadCacheWindow? FindWindow(long address)
            {
                var low = 0;
                var high = this.windows.Length - 1;
                while (low <= high)
                {
                    var middle = low + ((high - low) >> 1);
                    var window = this.windows[middle];
                    if (address < window.StartAddress)
                    {
                        high = middle - 1;
                    }
                    else if (address >= window.StartAddress + window.ByteCount)
                    {
                        low = middle + 1;
                    }
                    else
                    {
                        return window;
                    }
                }

                return null;
            }
        }

        internal readonly struct ReadCachePlanScope : IDisposable
        {
            private readonly ReadCachePlan? plan;
            private readonly ReadCachePlan? previous;

            internal ReadCachePlanScope(ReadCachePlan plan, ReadCachePlan? previous)
            {
                this.plan = plan;
                this.previous = previous;
            }

            public void Dispose()
            {
                if (this.plan is null)
                {
                    return;
                }

                currentReadCachePlan = this.previous;
                this.plan.Dispose();
            }
        }

        /// <summary>
        ///     Reads the string.
        /// </summary>
        /// <param name="address">pointer to the string.</param>
        /// <returns>string read.</returns>
        internal string ReadString(IntPtr address)
        {
            const int bufferLength = 128;
            var buffer = ArrayPool<byte>.Shared.Rent(bufferLength);
            try
            {
                if (!this.TryReadMemoryArray(address, buffer, bufferLength, out _))
                {
                    return string.Empty;
                }

                var count = Array.IndexOf(buffer, (byte)0, 0, bufferLength);
                return count > 0 ? Encoding.ASCII.GetString(buffer, 0, count) : string.Empty;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        /// <summary>
        ///     Reads Unicode string when string length isn't know.
        ///     Use  <see cref="ReadStdWString" /> if string length is known.
        /// </summary>
        /// <param name="address">points to the Unicode string pointer.</param>
        /// <returns>string read from the memory.</returns>
        internal string ReadUnicodeString(IntPtr address)
        {
            const int bufferLength = 256;
            var buffer = ArrayPool<byte>.Shared.Rent(bufferLength);
            try
            {
                if (!this.TryReadMemoryArray(address, buffer, bufferLength, out _))
                {
                    return string.Empty;
                }

                var count = 0;
                for (var i = 0; i < bufferLength - 1; i += 2)
                {
                    if (buffer[i] == 0x00 && buffer[i + 1] == 0x00)
                    {
                        count = i;
                        break;
                    }
                }

                // Let's not return a string if a terminator isn't found.
                return count == 0 ? string.Empty : Encoding.Unicode.GetString(buffer, 0, count);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        /// <summary>
        ///     Reads the StdMap in parallel and execute onValue function on each node that isn't null
        /// </summary>
        /// <typeparam name="TKey">stdmap key type</typeparam>
        /// <typeparam name="TValue">stdmap value type</typeparam>
        /// <param name="nativeContainer">native object pointing to the std::map</param>
        /// <param name="maxSizeAllowed">to remove infinite loops, function will return upon reaching this number</param>
        /// <param name="enableCounting">extract more juice from the cpu</param>
        /// <param name="onEachNotNullNode">function to execute on each std map node that isn't null.</param>
        /// <returns>total nodes/childrens in the stdmap</returns>
        internal int ReadStdMap<TKey, TValue>(StdMap nativeContainer, int maxSizeAllowed, bool enableCounting, Func<TKey, TValue, bool> onEachNotNullNode)
            where TKey : unmanaged
            where TValue : unmanaged
        {
            if (nativeContainer.Size <= 0 || nativeContainer.Size > maxSizeAllowed)
            {
                return 0;
            }

            var head = this.ReadMemory<StdMapNode<TKey, TValue>>(nativeContainer.Head);
            var parent = this.ReadMemory<StdMapNode<TKey, TValue>>(head.Parent);
            var first64Childrens = new Queue<StdMapNode<TKey, TValue>>(64);

            // processing first 63 nodes will gives us 64 childrens
            // in the first64Childrens list (assuming there is no node with just 1 child).
            // TODO: Benchmark 64 childrens
            var totalChildrenProcessed = processSubTree(first64Childrens, parent, 64);

            // then Parallel.ForEach loop will process those 32 childrens in parallel
            Parallel.ForEach(first64Childrens,
                new ParallelOptions() { MaxDegreeOfParallelism = Core.GHSettings.EntityReaderMaxDegreeOfParallelism },
                // executed once per task/thread
                () => { return (new Queue<StdMapNode<TKey, TValue>>(2000), new int()); },
                // executed once per iteration
                (first32Child, _, _, localState) =>
                {
                    localState.Item2 += processSubTree(localState.Item1, first32Child, maxSizeAllowed / first64Childrens.Count);
                    return localState;
                },
                // executed once per task/thread
                localFinal =>
                {
                    if(enableCounting)
                    {
                        Interlocked.Add(ref totalChildrenProcessed, localFinal.Item2);
                    }
                });

            return totalChildrenProcessed;

            void processNode(Queue<StdMapNode<TKey, TValue>> childrens, StdMapNode<TKey, TValue> current)
            {
                if (!current.IsNil)
                {
                    onEachNotNullNode(current.Data.Key, current.Data.Value);
                }

                // Child pointers are read from a live tree the game mutates concurrently, so a
                // torn read can yield a non-pointer value. Use the non-logging read + validity
                // check and simply stop descending that branch on failure (audit: torn-read noise).
                // Color is the red/black flag and is always 0 or 1 in a real node; if a torn/bad
                // pointer lands us on string or float data, Color is almost never 0/1, so this
                // rejects garbage before we descend into it and propagate the failure further.
                if (this.TryReadMemory<StdMapNode<TKey, TValue>>(current.Left, out var leftChild) &&
                    !leftChild.IsNil && leftChild.Color <= 1)
                {
                    childrens.Enqueue(leftChild);
                }

                if (this.TryReadMemory<StdMapNode<TKey, TValue>>(current.Right, out var rightChild) &&
                    !rightChild.IsNil && rightChild.Color <= 1)
                {
                    childrens.Enqueue(rightChild);
                }
            }

            int processSubTree(Queue<StdMapNode<TKey, TValue>> childrens, StdMapNode<TKey, TValue> subTreeRoot, int forceBreakOnIteration)
            {
                childrens.Enqueue(subTreeRoot);
                var counter = 0;
                while (++counter < forceBreakOnIteration && childrens.TryDequeue(out var current))
                {
                    processNode(childrens, current);
                }

                return counter;
            }
        }

        /// <summary>
        ///     Reads a std::map in breadth-first waves and coalesces nearby tree nodes. A failed
        ///     span falls back to scalar node reads, preserving the original map traversal.
        /// </summary>
        internal int ReadStdMapBatched<TKey, TValue>(
            StdMap nativeContainer,
            int maxSizeAllowed,
            bool enableCounting,
            Func<TKey, TValue, bool> onEachNotNullNode)
            where TKey : unmanaged
            where TValue : unmanaged
        {
            var maxGapAfterMapNodeBytes = Core.GHSettings.EnableNewMemoryRead ? 0x10000 : 0x200;
            var maxBatchSpanBytes = Core.GHSettings.EnableNewMemoryRead ? 1024 * 1024 : 64 * 1024;
            const int minBatchNodes = 4;

            if (nativeContainer.Size <= 0 || nativeContainer.Size > maxSizeAllowed)
            {
                return 0;
            }

            var head = this.ReadMemory<StdMapNode<TKey, TValue>>(nativeContainer.Head);
            var pending = new List<IntPtr>(64) { head.Parent };
            var visited = new HashSet<IntPtr> { nativeContainer.Head };
            var totalChildrenProcessed = 0;
            var nodeSize = Unsafe.SizeOf<StdMapNode<TKey, TValue>>();

            while (pending.Count > 0 && totalChildrenProcessed < maxSizeAllowed)
            {
                var addresses = pending.ToArray();
                pending.Clear();
                Array.Sort(addresses);
                var nodes = new Dictionary<IntPtr, StdMapNode<TKey, TValue>>(addresses.Length);
                var batchStart = 0;

                while (batchStart < addresses.Length)
                {
                    var firstAddress = addresses[batchStart].ToInt64();
                    var previousAddress = firstAddress;
                    var batchEnd = batchStart + 1;
                    while (batchEnd < addresses.Length)
                    {
                        var nextAddress = addresses[batchEnd].ToInt64();
                        var gapAfterPrevious = nextAddress - previousAddress - nodeSize;
                        var span = nextAddress - firstAddress + nodeSize;
                        if (gapAfterPrevious < 0 ||
                            gapAfterPrevious > maxGapAfterMapNodeBytes ||
                            span > maxBatchSpanBytes)
                        {
                            break;
                        }

                        previousAddress = nextAddress;
                        batchEnd++;
                    }

                    var count = batchEnd - batchStart;
                    var batchSucceeded = false;
                    if (count >= minBatchNodes)
                    {
                        var byteCount = checked((int)(previousAddress - firstAddress + nodeSize));
                        var buffer = ArrayPool<byte>.Shared.Rent(byteCount);
                        try
                        {
                            batchSucceeded = this.TryReadMemoryArray(
                                new IntPtr(firstAddress),
                                buffer,
                                byteCount,
                                out _);
                            if (batchSucceeded)
                            {
                                for (var i = batchStart; i < batchEnd; i++)
                                {
                                    var offset = checked((int)(addresses[i].ToInt64() - firstAddress));
                                    nodes[addresses[i]] = MemoryMarshal.Read<StdMapNode<TKey, TValue>>(
                                        buffer.AsSpan(offset, nodeSize));
                                }
                            }
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(buffer);
                        }
                    }

                    if (!batchSucceeded)
                    {
                        for (var i = batchStart; i < batchEnd; i++)
                        {
                            if (this.TryReadMemory(addresses[i], out StdMapNode<TKey, TValue> node))
                            {
                                nodes[addresses[i]] = node;
                            }
                        }
                    }

                    batchStart = batchEnd;
                }

                foreach (var address in addresses)
                {
                    if (!nodes.TryGetValue(address, out var current) || current.Color > 1)
                    {
                        continue;
                    }

                    totalChildrenProcessed++;
                    if (!current.IsNil)
                    {
                        onEachNotNullNode(current.Data.Key, current.Data.Value);
                    }

                    if (!current.IsNil)
                    {
                        if (current.Left != nativeContainer.Head &&
                            IsValidAddress(current.Left) &&
                            visited.Add(current.Left))
                        {
                            pending.Add(current.Left);
                        }

                        if (current.Right != nativeContainer.Head &&
                            IsValidAddress(current.Right) &&
                            visited.Add(current.Right))
                        {
                            pending.Add(current.Right);
                        }
                    }

                    if (totalChildrenProcessed >= maxSizeAllowed)
                    {
                        break;
                    }
                }
            }

            return totalChildrenProcessed;
        }

        /// <summary>
        ///     Reads the StdList into a List.
        /// </summary>
        /// <typeparam name="TValue">StdList element structure.</typeparam>
        /// <param name="nativeContainer">native object of the std::list.</param>
        /// <returns>List containing TValue elements.</returns>
        internal List<TValue> ReadStdList<TValue>(StdList nativeContainer)
            where TValue : unmanaged
        {
            const int MaxIterations = 100_000;
            var retList = new List<TValue>();
            var currNodeAddress = this.ReadMemory<StdListNode>(nativeContainer.Head).Next;
            var iterations = 0;
            while (currNodeAddress != nativeContainer.Head)
            {
                if (++iterations > MaxIterations)
                {
                    Console.WriteLine($"[SafeMemoryHandle.ReadStdList] iteration cap {MaxIterations} hit; possible cycle in torn list. Returning partial result.");
                    break;
                }

                var currNode = this.ReadMemory<StdListNode<TValue>>(currNodeAddress);
                if (currNodeAddress == IntPtr.Zero)
                {
                    Console.WriteLine("Terminating reading of list next nodes because of" +
                                      "unexpected 0x00 found. This is normal if it happens " +
                                      "after closing the game, otherwise report it.");
                    break;
                }

                retList.Add(currNode.Data);
                currNodeAddress = currNode.Next;
            }

            return retList;
        }

        /// <summary>
        ///     Reads the std::bucket into a array.
        /// </summary>
        /// <typeparam name="TValue">value type that the std bucket contains.</typeparam>
        /// <param name="nativeContainer">native object of the std::bucket.</param>
        /// <returns>a array containing all the valid values found in std::bucket.</returns>
        internal TValue[] ReadStdBucket<TValue>(StdBucket nativeContainer)
            where TValue : unmanaged
        {
            if (nativeContainer.Data.First == IntPtr.Zero ||
                nativeContainer.Capacity <= 0x00)
            {
                return Array.Empty<TValue>();
            }

            return this.ReadStdVector<TValue>(nativeContainer.Data);
        }

        /// <summary>
        ///     Walks the current call stack to attribute a bad read to core vs. a specific
        ///     plugin assembly (each plugin is loaded under its own ALC/name). Skips this
        ///     class's own frames, then returns the first application frame as
        ///     "AssemblyName!Type.Method". Because objects are frequently constructed via
        ///     reflection (Activator.CreateInstance), the real caller can sit above a
        ///     runtime/reflection trampoline; when the first frame we hit is such infrastructure
        ///     we keep walking (recording the chain) until we reach real application code, so the
        ///     trampoline doesn't mask the true origin. Stack capture skips line info to stay cheap.
        /// </summary>
        /// <returns>caller chain, or a fallback string if it can't be determined.</returns>
        private static string DescribeCaller()
        {
            try
            {
                var stack = new System.Diagnostics.StackTrace(1, false);
                var parts = new List<string>();
                foreach (var frame in stack.GetFrames())
                {
                    var method = frame?.GetMethod();
                    var declaringType = method?.DeclaringType;
                    if (declaringType == null || declaringType == typeof(SafeMemoryHandle))
                    {
                        continue;
                    }

                    var asm = declaringType.Assembly.GetName().Name ?? "?";
                    parts.Add($"{asm}!{declaringType.Name}.{method!.Name}");

                    // Stop at the first real application frame. Keep collecting through
                    // runtime/reflection frames (with a hard cap) so a reflection invoke
                    // shows "trampoline <- realCaller" instead of just the trampoline.
                    if (!IsInfrastructureAssembly(asm) || parts.Count >= 4)
                    {
                        break;
                    }
                }

                return parts.Count > 0 ? string.Join(" <- ", parts) : "<unknown>";
            }
            catch
            {
                return "<unavailable>";
            }
        }

        /// <summary>
        ///     Whether an assembly is .NET runtime / reflection infrastructure rather than
        ///     application (TEHhub or plugin) code. Used by <see cref="DescribeCaller"/> to
        ///     walk past reflection trampolines.
        /// </summary>
        /// <param name="assemblyName">the assembly's simple name.</param>
        /// <returns>true if it is a framework/runtime assembly.</returns>
        private static bool IsInfrastructureAssembly(string assemblyName)
        {
            return assemblyName.StartsWith("System", StringComparison.Ordinal) ||
                   assemblyName == "mscorlib" ||
                   assemblyName == "netstandard" ||
                   assemblyName == "?";
        }

        /// <summary>
        ///     When overridden in a derived class, executes the code required to free the handle.
        /// </summary>
        /// <returns>
        ///     true if the handle is released successfully; otherwise, in the event of a catastrophic failure, false.
        ///     In this case, it generates a releaseHandleFailed MDA Managed Debugging Assistant.
        /// </returns>
        protected override bool ReleaseHandle()
        {
            Console.WriteLine($"Releasing handle on 0x{this.handle:X}\n");
            return NativeProcessMemory.Close(this.handle);
        }
    }
}
