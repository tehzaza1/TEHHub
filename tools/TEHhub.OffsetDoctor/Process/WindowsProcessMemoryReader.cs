namespace TEHhub.OffsetDoctor.Process;

using System.Diagnostics;
using System.Runtime.InteropServices;

public sealed class WindowsProcessMemoryReader : IProcessMemoryReader
{
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    private readonly IntPtr _processHandle;
    private readonly Process _process;
    private bool _disposed;

    public IntPtr MainModuleBase { get; }
    public long MainModuleSize { get; }
    public ProcessMetadata Metadata { get; }

    public WindowsProcessMemoryReader(int processId)
    {
        _process = Process.GetProcessById(processId);
        
        var handle = OpenProcess(ProcessVmRead | ProcessQueryInformation, false, processId);
        if (handle == IntPtr.Zero)
        {
            handle = OpenProcess(ProcessVmRead | ProcessQueryLimitedInformation, false, processId);
        }

        if (handle == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"Unable to open process {processId} for read-only memory access (Win32 Error: {err}). Run with appropriate privileges.");
        }

        _processHandle = handle;

        var mainModule = _process.MainModule;
        MainModuleBase = mainModule?.BaseAddress ?? IntPtr.Zero;
        MainModuleSize = mainModule?.ModuleMemorySize ?? 0;

        Metadata = new ProcessMetadata
        {
            ProcessId = processId,
            ProcessName = _process.ProcessName,
            ProcessPath = mainModule?.FileName,
            FileVersion = mainModule?.FileVersionInfo.FileVersion ?? string.Empty,
            ModuleBase = MainModuleBase,
            ModuleMemorySize = MainModuleSize,
            IsElevated = CheckElevation(),
            AttachedTimeUtc = DateTime.UtcNow
        };
    }

    public bool IsValidAddress(IntPtr address)
    {
        var addr = (ulong)address.ToInt64();
        // Standard user-mode address range check on x64 Windows: 0x10000 to 0x7FFFFFFF0000
        return addr >= 0x10000UL && addr < 0x7FFFFFFF0000UL;
    }

    public unsafe bool TryRead<T>(IntPtr address, out T value) where T : unmanaged
    {
        value = default;
        if (!IsValidAddress(address))
        {
            return false;
        }

        fixed (T* ptr = &value)
        {
            var span = new Span<byte>(ptr, sizeof(T));
            return TryReadBytes(address, span);
        }
    }

    public unsafe bool TryReadBytes(IntPtr address, Span<byte> destination)
    {
        if (!IsValidAddress(address) || destination.IsEmpty)
        {
            return false;
        }

        fixed (byte* destPtr = destination)
        {
            return ReadProcessMemory(_processHandle, address, (IntPtr)destPtr, (nuint)destination.Length, out var bytesRead)
                   && (int)bytesRead == destination.Length;
        }
    }

    public byte[]? ReadBytes(IntPtr address, int count)
    {
        if (count <= 0 || !IsValidAddress(address))
        {
            return null;
        }

        var buffer = new byte[count];
        unsafe
        {
            fixed (byte* destPtr = buffer)
            {
                if (ReadProcessMemory(_processHandle, address, (IntPtr)destPtr, (nuint)count, out var bytesRead) && (int)bytesRead > 0)
                {
                    if ((int)bytesRead < count)
                    {
                        var trimmed = new byte[(int)bytesRead];
                        Buffer.BlockCopy(buffer, 0, trimmed, 0, (int)bytesRead);
                        return trimmed;
                    }
                    return buffer;
                }
            }
        }

        return null;
    }

    private static bool CheckElevation()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_processHandle != IntPtr.Zero)
        {
            CloseHandle(_processHandle);
        }

        _process.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(
        IntPtr hProcess,
        IntPtr lpBaseAddress,
        IntPtr lpBuffer,
        nuint nSize,
        out nuint lpNumberOfBytesRead);
}
