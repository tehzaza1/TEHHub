// <copyright file="PooledNativeVector.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace GameHelper.Utils
{
    using System;
    using System.Buffers;
    using System.Runtime.CompilerServices;
    using GameOffsets.Natives;

    /// <summary>
    ///     Reads short-lived native vectors into reusable buffers. Call <see cref="Return{T}"/>
    ///     after consuming the first <c>count</c> entries.
    /// </summary>
    internal static class PooledNativeVector
    {
        private const long MaxVectorByteLength = 50_000_000;

        /// <summary>
        ///     Reads a native vector. Invalid or failed vectors are exposed as an empty buffer,
        ///     matching <see cref="SafeMemoryHandle.ReadStdVector{T}(StdVector)"/> semantics.
        /// </summary>
        internal static void Read<T>(SafeMemoryHandle reader, StdVector vector, out T[] buffer, out int count)
            where T : unmanaged
        {
            buffer = Array.Empty<T>();
            count = 0;

            var elementSize = Unsafe.SizeOf<T>();
            var byteLength = vector.Last.ToInt64() - vector.First.ToInt64();
            if (byteLength <= 0 || byteLength % elementSize != 0 || byteLength > MaxVectorByteLength)
            {
                return;
            }

            count = (int)(byteLength / elementSize);
            var rented = ArrayPool<T>.Shared.Rent(count);
            if (reader.TryReadMemoryArray(vector.First, rented, count, out _))
            {
                buffer = rented;
                return;
            }

            ArrayPool<T>.Shared.Return(rented);
            count = 0;
        }

        /// <summary>
        ///     Returns a buffer obtained from <see cref="Read{T}"/>.
        /// </summary>
        internal static void Return<T>(T[] buffer)
            where T : unmanaged
        {
            if (buffer.Length > 0)
            {
                ArrayPool<T>.Shared.Return(buffer);
            }
        }
    }
}
