// <copyright file="NativeProcessMemory.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace GameHelper.Utils;

using System;
using System.Runtime.InteropServices;

/// <summary>
///     Minimal source-generated Windows interop used by the process-memory hot path.
///     The legacy package remains available to plugins while they migrate.
/// </summary>
internal static partial class NativeProcessMemory
{
    private const uint ProcessVmRead = 0x0010;

    internal static int LastError => Marshal.GetLastPInvokeError();

    internal static nint OpenForRead(int processId) => OpenProcess(ProcessVmRead, false, processId);

    internal static unsafe bool TryRead<T>(
        nint processHandle,
        nint address,
        out T result,
        out nuint bytesRead)
        where T : unmanaged
    {
        result = default;
        fixed (T* resultPointer = &result)
        {
            return ReadProcessMemory(
                processHandle,
                address,
                resultPointer,
                (nuint)sizeof(T),
                out bytesRead);
        }
    }

    internal static unsafe bool TryRead<T>(
        nint processHandle,
        nint address,
        T[] buffer,
        int elementCount,
        out nuint bytesRead)
        where T : unmanaged
    {
        if (elementCount <= 0)
        {
            bytesRead = 0;
            return true;
        }

        if (elementCount > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(elementCount));
        }

        fixed (T* bufferPointer = buffer)
        {
            return ReadProcessMemory(
                processHandle,
                address,
                bufferPointer,
                checked((nuint)elementCount * (nuint)sizeof(T)),
                out bytesRead);
        }
    }

    internal static bool Close(nint processHandle) => CloseHandle(processHandle);

    [LibraryImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)]
    private static partial nint OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [LibraryImport("kernel32.dll", EntryPoint = "ReadProcessMemory", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool ReadProcessMemory(
        nint processHandle,
        nint baseAddress,
        void* buffer,
        nuint size,
        out nuint numberOfBytesRead);

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
