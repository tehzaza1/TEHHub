namespace TEHhub.OffsetDoctor.Process;

public interface IProcessMemoryReader : IDisposable
{
    IntPtr MainModuleBase { get; }
    long MainModuleSize { get; }
    ProcessMetadata Metadata { get; }

    bool TryRead<T>(IntPtr address, out T value) where T : unmanaged;
    bool TryReadBytes(IntPtr address, Span<byte> destination);
    byte[]? ReadBytes(IntPtr address, int count);
    bool IsValidAddress(IntPtr address);
}
