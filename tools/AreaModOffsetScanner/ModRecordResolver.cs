namespace AreaModOffsetScanner
{
    using System;
    using System.Collections.Generic;
    using TEHhub.Offsets.Objects.Components;

    /// <summary>
    ///     Safely inspects ModArrayStruct entries and resolves Mods.dat row pointers to Unicode strings.
    ///     Keeps all state purely local to the scanner session.
    /// </summary>
    public sealed class ModRecordResolver
    {
        private readonly Dictionary<IntPtr, string> stringCache = new();

        public readonly record struct ResolutionResult(
            int AttemptedEntries,
            int ReadableEntries,
            int PlausibleModsPtrCount,
            int ResolvedModCount,
            int NonEmptyRawNameCount,
            List<string> SampleRawNames);

        /// <summary>
        ///     Inspects up to maxEntries elements from a candidate vector address.
        /// </summary>
        public ResolutionResult ResolveCandidate(NativeMemoryReader reader, IntPtr firstElementAddress, int maxEntries)
        {
            var sampleNames = new List<string>();
            var attempted = 0;
            var readable = 0;
            var plausibleModsPtr = 0;
            var resolvedMods = 0;
            var nonEmptyRawNames = 0;

            if (maxEntries <= 0 || firstElementAddress == IntPtr.Zero || !NativeMemoryReader.IsValidAddress(firstElementAddress))
            {
                return new ResolutionResult(attempted, readable, plausibleModsPtr, resolvedMods, nonEmptyRawNames, sampleNames);
            }

            const int stride = VectorEvaluator.ModArrayStride; // 0x40

            for (var i = 0; i < maxEntries; i++)
            {
                var entryAddress = firstElementAddress + (i * stride);
                if (!NativeMemoryReader.IsValidAddress(entryAddress))
                {
                    break;
                }

                attempted++;
                if (!reader.TryRead<ModArrayStruct>(entryAddress, out var mod))
                {
                    // Memory read failure at element boundary
                    break;
                }

                readable++;

                // ModsPtr is at +0x28 in ModArrayStruct
                if (mod.ModsPtr == IntPtr.Zero || !NativeMemoryReader.IsValidAddress(mod.ModsPtr))
                {
                    continue;
                }

                plausibleModsPtr++;

                var rawName = this.GetModName(reader, mod.ModsPtr);
                if (!string.IsNullOrEmpty(rawName))
                {
                    resolvedMods++;
                    if (!string.IsNullOrWhiteSpace(rawName))
                    {
                        nonEmptyRawNames++;
                        if (sampleNames.Count < 10)
                        {
                            var cleanName = rawName.Length > 70 ? rawName[..67] + "..." : rawName;
                            sampleNames.Add(cleanName);
                        }
                    }
                }
            }

            return new ResolutionResult(attempted, readable, plausibleModsPtr, resolvedMods, nonEmptyRawNames, sampleNames);
        }

        /// <summary>
        ///     Dereferences the Mods.dat row pointer:
        ///     Row Address -> String Pointer at (+0x00) -> UTF-16 Unicode String.
        /// </summary>
        public string GetModName(NativeMemoryReader reader, IntPtr modsDatRowAddress)
        {
            if (modsDatRowAddress == IntPtr.Zero || !NativeMemoryReader.IsValidAddress(modsDatRowAddress))
            {
                return string.Empty;
            }

            if (this.stringCache.TryGetValue(modsDatRowAddress, out var cached))
            {
                return cached;
            }

            if (!reader.TryRead<IntPtr>(modsDatRowAddress, out var stringAddress))
            {
                return string.Empty;
            }

            if (stringAddress == IntPtr.Zero || !NativeMemoryReader.IsValidAddress(stringAddress))
            {
                return string.Empty;
            }

            var name = reader.ReadUnicodeString(stringAddress, maxChars: 256);
            if (!string.IsNullOrEmpty(name))
            {
                this.stringCache[modsDatRowAddress] = name;
            }

            return name;
        }
    }
}
