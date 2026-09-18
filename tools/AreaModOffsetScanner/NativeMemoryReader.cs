namespace AreaModOffsetScanner
{
    using System;
    using System.Diagnostics;
    using System.Runtime.InteropServices;
    using System.Text;

    /// <summary>
    ///     Safe, read-only memory reader for inspecting external process memory.
    ///     Explicitly contains NO write APIs, memory allocations, hooks, or injection.
    /// </summary>
    public sealed unsafe partial class NativeMemoryReader : IDisposable
    {
        private const uint PROCESS_VM_READ = 0x0010;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;

        private const long MinValidAddress = 0x10000;
        private const long MaxUserModeAddress = 0x7FFFFFFFFFFF;

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

        private NativeMemoryReader(int processId, IntPtr handle, Process process)
        {
            this.ProcessId = processId;
            this.processHandle = handle;
            this.ProcessName = process.ProcessName;
            this.MainModuleFileName = GetProcessPath(process);
        }

        public int ProcessId { get; }

        public string ProcessName { get; }

        public string MainModuleFileName { get; }

        public bool IsOpen => !this.disposed && this.processHandle != IntPtr.Zero;

        /// <summary>
        ///     Opens a process for read-only access (PROCESS_VM_READ | PROCESS_QUERY_INFORMATION).
        /// </summary>
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

        /// <summary>
        ///     Checks if an address is within valid 64-bit user-mode memory bounds.
        /// </summary>
        public static bool IsValidAddress(IntPtr address)
        {
            var addr = address.ToInt64();
            return addr >= MinValidAddress && addr <= MaxUserModeAddress;
        }

        /// <summary>
        ///     Safely reads a single unmanaged struct from process memory.
        /// </summary>
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

        /// <summary>
        ///     Safely reads an array of unmanaged structs from process memory.
        /// </summary>
        public bool TryReadArray<T>(IntPtr address, int count, out T[] result)
            where T : unmanaged
        {
            result = Array.Empty<T>();
            if (this.disposed || !IsValidAddress(address) || count <= 0 || count > 50_000)
            {
                return false;
            }

            var size = (nuint)(count * sizeof(T));
            var arr = new T[count];
            fixed (T* ptr = arr)
            {
                var ok = ReadProcessMemory(this.processHandle, address, ptr, size, out var bytesRead);
                if (ok && bytesRead == size)
                {
                    result = arr;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        ///     Reads raw bytes from process memory.
        /// </summary>
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

        /// <summary>
        ///     Reads a null-terminated UTF-16 Unicode string from external memory.
        /// </summary>
        public string ReadUnicodeString(IntPtr address, int maxChars = 256)
        {
            if (this.disposed || !IsValidAddress(address) || maxChars <= 0)
            {
                return string.Empty;
            }

            var byteLength = maxChars * 2;
            Span<byte> buffer = stackalloc byte[byteLength];
            if (!this.TryReadBytes(address, buffer, out var bytesRead) || bytesRead < 2)
            {
                return string.Empty;
            }

            var charCount = 0;
            for (var i = 0; i < bytesRead - 1; i += 2)
            {
                if (buffer[i] == 0x00 && buffer[i + 1] == 0x00)
                {
                    charCount = i;
                    break;
                }
            }

            return charCount == 0 ? string.Empty : Encoding.Unicode.GetString(buffer[..charCount]);
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

        private static string GetProcessPath(Process p)
        {
            try
            {
                return p.MainModule?.FileName ?? p.ProcessName;
            }
            catch
            {
                return p.ProcessName;
            }
        }
    }
}
