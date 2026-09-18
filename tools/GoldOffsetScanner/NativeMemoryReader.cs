namespace GoldOffsetScanner
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Runtime.InteropServices;
    using System.Text;

    /// <summary>
    ///     Safe, read-only memory reader for inspecting external PoE2 process memory.
    ///     Explicitly contains NO write APIs, memory allocations in target, hooks, or injection.
    /// </summary>
    public sealed unsafe partial class NativeMemoryReader : IDisposable
    {
        private const uint PROCESS_VM_READ = 0x0010;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;

        private const long MinValidAddress = 0x10000;
        private const long MaxUserModeAddress = 0x7FFFFFFFFFFF;

        private const uint MEM_COMMIT = 0x1000;
        private const uint PAGE_NOACCESS = 0x01;
        private const uint PAGE_READONLY = 0x02;
        private const uint PAGE_READWRITE = 0x04;
        private const uint PAGE_WRITECOPY = 0x08;
        private const uint PAGE_EXECUTE_READ = 0x20;
        private const uint PAGE_EXECUTE_READWRITE = 0x40;
        private const uint PAGE_GUARD = 0x100;

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORY_BASIC_INFORMATION64
        {
            public ulong BaseAddress;
            public ulong AllocationBase;
            public uint AllocationProtect;
            public uint __alignment1;
            public ulong RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
            public uint __alignment2;
        }

        public readonly struct MemoryRegionInfo
        {
            public ulong BaseAddress { get; init; }
            public ulong RegionSize { get; init; }
            public uint State { get; init; }
            public uint Protect { get; init; }
            public uint Type { get; init; }

            public bool IsReadable =>
                State == MEM_COMMIT &&
                (Protect & PAGE_GUARD) == 0 &&
                (Protect & (PAGE_READONLY | PAGE_READWRITE | PAGE_WRITECOPY | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE)) != 0;

            public string ProtectDescription => Protect switch
            {
                PAGE_READONLY => "PAGE_READONLY",
                PAGE_READWRITE => "PAGE_READWRITE",
                PAGE_WRITECOPY => "PAGE_WRITECOPY",
                PAGE_EXECUTE_READ => "PAGE_EXECUTE_READ",
                PAGE_EXECUTE_READWRITE => "PAGE_EXECUTE_READWRITE",
                _ => $"0x{Protect:X}"
            };

            public string TypeDescription => Type switch
            {
                0x20000 => "MEM_PRIVATE",
                0x40000 => "MEM_MAPPED",
                0x1000000 => "MEM_IMAGE",
                _ => $"0x{Type:X}"
            };
        }

        private readonly IntPtr processHandle;
        private bool disposed;

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial IntPtr OpenProcess(
            uint dwDesiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
            int dwProcessId);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(IntPtr hObject);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool ReadProcessMemory(
            IntPtr hProcess,
            IntPtr lpBaseAddress,
            void* lpBuffer,
            nuint nSize,
            out nuint lpNumberOfBytesRead);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial nuint VirtualQueryEx(
            IntPtr hProcess,
            IntPtr lpAddress,
            out MEMORY_BASIC_INFORMATION64 lpBuffer,
            nuint dwLength);

        private NativeMemoryReader(int processId, IntPtr handle, Process process)
        {
            this.ProcessId = processId;
            this.processHandle = handle;
            this.ProcessName = process.ProcessName;
            this.MainModuleBase = process.MainModule?.BaseAddress ?? IntPtr.Zero;
            this.MainModuleSize = (ulong)(process.MainModule?.ModuleMemorySize ?? 0);
        }

        public int ProcessId { get; }
        public string ProcessName { get; }
        public IntPtr MainModuleBase { get; }
        public ulong MainModuleSize { get; }

        public bool IsOpen => !this.disposed && this.processHandle != IntPtr.Zero;

        public static NativeMemoryReader Open(int processId)
        {
            var process = Process.GetProcessById(processId);
            var handle = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, processId);
            if (handle == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"Failed to open process {processId} for read-only access (Win32 Error: {error}). Ensure administrator privileges if target is elevated.");
            }

            return new NativeMemoryReader(processId, handle, process);
        }

        public static bool IsValidAddress(IntPtr address)
        {
            var addr = address.ToInt64();
            return addr >= MinValidAddress && addr <= MaxUserModeAddress;
        }

        public static bool IsValidAddress(ulong address)
        {
            return address >= (ulong)MinValidAddress && address <= (ulong)MaxUserModeAddress;
        }

        public bool TryRead<T>(IntPtr address, out T result)
            where T : unmanaged
        {
            result = default;
            if (this.disposed || !IsValidAddress(address))
            {
                return false;
            }

            var size = (nuint)sizeof(T);
            fixed (T* ptr = &result)
            {
                var ok = ReadProcessMemory(this.processHandle, address, ptr, size, out var bytesRead);
                return ok && bytesRead == size;
            }
        }

        public bool TryReadBytes(IntPtr address, Span<byte> destination, out int bytesReadCount)
        {
            bytesReadCount = 0;
            if (this.disposed || !IsValidAddress(address) || destination.IsEmpty)
            {
                return false;
            }

            fixed (byte* ptr = destination)
            {
                var ok = ReadProcessMemory(this.processHandle, address, ptr, (nuint)destination.Length, out var bytesRead);
                bytesReadCount = (int)bytesRead;
                return ok;
            }
        }

        public bool QueryMemory(ulong address, out MemoryRegionInfo regionInfo)
        {
            regionInfo = default;
            if (this.disposed) return false;

            var size = (nuint)sizeof(MEMORY_BASIC_INFORMATION64);
            var res = VirtualQueryEx(this.processHandle, (IntPtr)(long)address, out var mbi, size);
            if (res == size)
            {
                regionInfo = new MemoryRegionInfo
                {
                    BaseAddress = mbi.BaseAddress,
                    RegionSize = mbi.RegionSize,
                    State = mbi.State,
                    Protect = mbi.Protect,
                    Type = mbi.Type
                };
                return true;
            }

            return false;
        }

        public List<MemoryRegionInfo> EnumerateReadableRegions(ulong minAddr = (ulong)MinValidAddress, ulong maxAddr = (ulong)MaxUserModeAddress)
        {
            var regions = new List<MemoryRegionInfo>();
            ulong currentAddr = minAddr;

            while (currentAddr < maxAddr)
            {
                if (!this.QueryMemory(currentAddr, out var info))
                {
                    currentAddr += 0x1000;
                    continue;
                }

                if (info.RegionSize == 0)
                {
                    currentAddr += 0x1000;
                    continue;
                }

                if (info.IsReadable)
                {
                    regions.Add(info);
                }

                currentAddr = info.BaseAddress + info.RegionSize;
            }

            return regions;
        }

        public string FormatHexDump(ulong centerAddress, int radius = 0x80)
        {
            var startAddr = centerAddress >= (ulong)radius ? (centerAddress - (ulong)radius) : 0;
            startAddr &= ~0x0Fu; // Align to 16-byte boundary
            var endAddr = centerAddress + (ulong)radius;
            var totalBytes = (int)(endAddr - startAddr);

            var buffer = new byte[totalBytes];
            if (!this.TryReadBytes((IntPtr)(long)startAddr, buffer, out var readCount) || readCount == 0)
            {
                return "  [Unable to read surrounding memory]";
            }

            var sb = new StringBuilder();
            for (var offset = 0; offset < readCount; offset += 16)
            {
                var rowAddr = startAddr + (ulong)offset;
                var marker = (rowAddr <= centerAddress && centerAddress < rowAddr + 16) ? ">>" : "  ";
                sb.Append($"{marker} 0x{rowAddr:X12} | ");

                // Hex bytes
                for (var b = 0; b < 16; b++)
                {
                    if (offset + b < readCount)
                    {
                        var byteVal = buffer[offset + b];
                        var isTarget = (rowAddr + (ulong)b == centerAddress);
                        sb.Append(isTarget ? $"[{byteVal:X2}]" : $" {byteVal:X2} ");
                    }
                    else
                    {
                        sb.Append("    ");
                    }
                }

                sb.Append(" | ");

                // ASCII
                for (var b = 0; b < 16; b++)
                {
                    if (offset + b < readCount)
                    {
                        var byteVal = buffer[offset + b];
                        char c = (byteVal >= 32 && byteVal <= 126) ? (char)byteVal : '.';
                        sb.Append(c);
                    }
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }

        public void Dispose()
        {
            if (!this.disposed)
            {
                this.disposed = true;
                if (this.processHandle != IntPtr.Zero)
                {
                    CloseHandle(this.processHandle);
                }
            }
        }
    }
}
