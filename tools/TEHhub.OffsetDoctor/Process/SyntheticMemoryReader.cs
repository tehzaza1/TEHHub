namespace TEHhub.OffsetDoctor.Process;

using System.Runtime.InteropServices;

public sealed class SyntheticMemoryReader : IProcessMemoryReader
{
    private readonly Dictionary<ulong, byte[]> _blocks = new();
    private ulong _nextAutoAddress = 0x10000000;
    private bool _disposed;

    public IntPtr MainModuleBase { get; set; } = new IntPtr(0x140000000);
    public long MainModuleSize { get; set; } = 0x5000000; // 80MB
    public ProcessMetadata Metadata { get; set; }

    public int TotalReadsCount { get; private set; }
    public ulong MinReadAddress { get; private set; } = ulong.MaxValue;
    public ulong MaxReadAddress { get; private set; } = 0;

    public SyntheticMemoryReader()
    {
        Metadata = new ProcessMetadata
        {
            ProcessId = 99999,
            ProcessName = "SyntheticPoE2.exe",
            FileVersion = "0.2.0.0",
            ModuleBase = MainModuleBase,
            ModuleMemorySize = MainModuleSize,
            IsElevated = true,
            AttachedTimeUtc = DateTime.UtcNow
        };
    }

    public IntPtr AllocateBlock(int size)
    {
        var addr = _nextAutoAddress;
        _nextAutoAddress += (ulong)((size + 0xFFF) & ~0xFFF); // 4KB aligned
        var buffer = new byte[size];
        _blocks[addr] = buffer;
        return new IntPtr((long)addr);
    }

    public IntPtr AllocateBlockAt(ulong address, int size)
    {
        var buffer = new byte[size];
        _blocks[address] = buffer;
        return new IntPtr((long)address);
    }

    public void FreeBlock(IntPtr address)
    {
        _blocks.Remove((ulong)address.ToInt64());
    }

    public unsafe void Write<T>(IntPtr address, T value) where T : unmanaged
    {
        var addr = (ulong)address.ToInt64();
        var block = FindBlock(addr, sizeof(T), out var offset);
        if (block == null)
        {
            throw new InvalidOperationException($"Attempted to write to unallocated synthetic memory at 0x{addr:X}");
        }

        fixed (byte* dest = &block[offset])
        {
            *(T*)dest = value;
        }
    }

    public void WritePointer(IntPtr address, IntPtr target)
    {
        Write(address, target);
    }

    public void WriteBytes(IntPtr address, byte[] data)
    {
        var addr = (ulong)address.ToInt64();
        var block = FindBlock(addr, data.Length, out var offset);
        if (block == null)
        {
            throw new InvalidOperationException($"Attempted to write {data.Length} bytes to unallocated synthetic memory at 0x{addr:X}");
        }

        Buffer.BlockCopy(data, 0, block, offset, data.Length);
    }

    public void WriteStdVector(IntPtr address, IntPtr begin, IntPtr end, IntPtr capacity)
    {
        Write(address, begin);
        Write(address + 8, end);
        Write(address + 16, capacity);
    }

    public bool IsValidAddress(IntPtr address)
    {
        var addr = (ulong)address.ToInt64();
        if (addr < 0x10000UL || addr >= 0x7FFFFFFF0000UL)
        {
            return false;
        }

        return FindBlock(addr, 1, out _) != null;
    }

    public unsafe bool TryRead<T>(IntPtr address, out T value) where T : unmanaged
    {
        value = default;
        var addr = (ulong)address.ToInt64();
        var size = sizeof(T);

        TrackReadAccess(addr, size);

        var block = FindBlock(addr, size, out var offset);
        if (block == null)
        {
            return false;
        }

        fixed (byte* src = &block[offset])
        {
            value = *(T*)src;
            return true;
        }
    }

    public unsafe bool TryReadBytes(IntPtr address, Span<byte> destination)
    {
        if (destination.IsEmpty) return false;

        var addr = (ulong)address.ToInt64();
        var size = destination.Length;

        TrackReadAccess(addr, size);

        var block = FindBlock(addr, size, out var offset);
        if (block == null)
        {
            return false;
        }

        fixed (byte* src = &block[offset])
        fixed (byte* dest = destination)
        {
            Buffer.MemoryCopy(src, dest, size, size);
            return true;
        }
    }

    public byte[]? ReadBytes(IntPtr address, int count)
    {
        if (count <= 0) return null;
        var buf = new byte[count];
        return TryReadBytes(address, buf) ? buf : null;
    }

    private void TrackReadAccess(ulong addr, int size)
    {
        TotalReadsCount++;
        if (addr < MinReadAddress) MinReadAddress = addr;
        if (addr + (ulong)size > MaxReadAddress) MaxReadAddress = addr + (ulong)size;
    }

    private byte[]? FindBlock(ulong address, int size, out int offsetInBlock)
    {
        foreach (var (baseAddr, buffer) in _blocks)
        {
            if (address >= baseAddr && (address + (ulong)size) <= (baseAddr + (ulong)buffer.Length))
            {
                offsetInBlock = (int)(address - baseAddr);
                return buffer;
            }
        }

        offsetInBlock = 0;
        return null;
    }

    public void ResetMetrics()
    {
        TotalReadsCount = 0;
        MinReadAddress = ulong.MaxValue;
        MaxReadAddress = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _blocks.Clear();
    }
}
