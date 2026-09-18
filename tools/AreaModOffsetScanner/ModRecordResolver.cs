namespace AreaModOffsetScanner
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using TEHhub.Offsets.Objects.Components;

    /// <summary>
    ///     Safely inspects ModArrayStruct entries and traverses the pointer chain:
    ///     ModArrayStruct.ModsPtr (+0x28) -> String Pointer (+0x00) -> UTF-16 Unicode String.
    ///
    ///     DIAGNOSTIC NOTICE:
    ///     Traversing a readable pointer chain and obtaining a string does NOT constitute proof
    ///     that the memory location is genuinely a Mods.dat record. It is recorded as a
    ///     "Readable Name Chain" and evaluated for "Plausible RawName" characteristics.
    ///     All state is strictly local to the scanner session.
    /// </summary>
    public sealed class ModRecordResolver
    {
        private readonly Dictionary<IntPtr, string> stringCache = new();

        public readonly record struct ResolutionResult(
            int AttemptedEntries,
            int ReadableEntries,
            int PlausibleModsPtrCount,
            int ReadableNameChainCount,
            int PlausibleRawNameCount,
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
            var readableNameChains = 0;
            var plausibleRawNames = 0;

            if (maxEntries <= 0 || firstElementAddress == IntPtr.Zero || !NativeMemoryReader.IsValidAddress(firstElementAddress))
            {
                return new ResolutionResult(attempted, readable, plausibleModsPtr, readableNameChains, plausibleRawNames, sampleNames);
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
                    readableNameChains++;
                    if (IsPlausibleRawName(rawName))
                    {
                        plausibleRawNames++;
                        if (sampleNames.Count < 10)
                        {
                            var cleanName = rawName.Length > 70 ? rawName[..67] + "..." : rawName;
                            sampleNames.Add(cleanName);
                        }
                    }
                }
            }

            return new ResolutionResult(attempted, readable, plausibleModsPtr, readableNameChains, plausibleRawNames, sampleNames);
        }

        /// <summary>
        ///     Conservative transparent check for plausible native modifier raw name identifiers.
        ///     Criteria:
        ///     1. Not null, empty, or whitespace.
        ///     2. Length between 2 and 128 characters.
        ///     3. Contains at least one letter (a-z, A-Z).
        ///     4. All characters are printable (no control characters).
        /// </summary>
        public static bool IsPlausibleRawName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length < 2 || name.Length > 128)
            {
                return false;
            }

            var hasLetter = false;
            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];
                if (char.IsControl(c))
                {
                    return false;
                }

                if (char.IsLetter(c))
                {
                    hasLetter = true;
                }
            }

            return hasLetter;
        }

        /// <summary>
        ///     Dereferences the candidate mod record row pointer:
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
