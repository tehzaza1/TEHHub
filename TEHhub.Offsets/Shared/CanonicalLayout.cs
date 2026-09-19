namespace TEHhub.Offsets.Shared;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

/// <summary>
/// Provides high-performance, thread-safe, cached canonical metadata for native layout structs.
/// Derives byte offsets and unmanaged sizes directly from the compiled struct definitions,
/// eliminating duplicate coordinator-local offset literals across the codebase.
/// This kernel is strictly read-only and memory-safe.
/// </summary>
/// <typeparam name="T">Struct layout type to inspect.</typeparam>
public static class CanonicalLayout<T> where T : struct
{
    private static readonly Lazy<int> _size = new(ComputeSize);
    private static readonly Lazy<int> _unmanagedSize = new(ComputeUnmanagedSize);
    private static readonly ConcurrentDictionary<string, int> _fieldOffsets = new(StringComparer.Ordinal);
    private static readonly Lazy<IReadOnlyDictionary<string, int>> _allFieldOffsets = new(DiscoverAllFieldOffsets);

    /// <summary>
    /// Gets the managed memory size of the struct in bytes via Unsafe.SizeOf.
    /// Matches the actual in-memory byte stride during memory reads.
    /// </summary>
    public static int Size => _size.Value;

    /// <summary>
    /// Gets the marshaled unmanaged size of the struct in bytes via Marshal.SizeOf.
    /// </summary>
    public static int UnmanagedSize => _unmanagedSize.Value;

    /// <summary>
    /// Gets a dictionary of all mapped instance field names to their byte offsets.
    /// </summary>
    public static IReadOnlyDictionary<string, int> FieldOffsets => _allFieldOffsets.Value;

    /// <summary>
    /// Retrieves the byte offset of a named field within the struct using Marshal.OffsetOf.
    /// The result is statically cached to avoid per-call reflection overhead.
    /// </summary>
    /// <param name="fieldName">Name of the field.</param>
    /// <returns>Byte offset of the field.</returns>
    /// <exception cref="ArgumentException">Thrown if the field is not found on the struct.</exception>
    public static int OffsetOf(string fieldName)
    {
        return _fieldOffsets.GetOrAdd(fieldName, static name =>
        {
            var field = typeof(T).GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field == null)
            {
                throw new ArgumentException($"Field '{name}' not found on struct '{typeof(T).FullName}'.", nameof(fieldName));
            }

            return Marshal.OffsetOf<T>(name).ToInt32();
        });
    }

    /// <summary>
    /// Safely attempts to retrieve the byte offset of a named field without throwing an exception.
    /// </summary>
    /// <param name="fieldName">Name of the field.</param>
    /// <param name="offset">When this method returns true, contains the byte offset; otherwise -1.</param>
    /// <returns>True if the field exists on the struct; otherwise false.</returns>
    public static bool TryGetOffsetOf(string fieldName, out int offset)
    {
        try
        {
            offset = OffsetOf(fieldName);
            return true;
        }
        catch
        {
            offset = -1;
            return false;
        }
    }

    private static int ComputeSize() => Unsafe.SizeOf<T>();

    private static int ComputeUnmanagedSize()
    {
        try
        {
            return Marshal.SizeOf<T>();
        }
        catch
        {
            return Unsafe.SizeOf<T>();
        }
    }

    private static IReadOnlyDictionary<string, int> DiscoverAllFieldOffsets()
    {
        var dict = new Dictionary<string, int>(StringComparer.Ordinal);
        var fields = typeof(T).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        foreach (var field in fields)
        {
            try
            {
                dict[field.Name] = Marshal.OffsetOf<T>(field.Name).ToInt32();
            }
            catch
            {
                // Skip fields that cannot be marshaled
            }
        }

        return new ReadOnlyDictionary<string, int>(dict);
    }
}
