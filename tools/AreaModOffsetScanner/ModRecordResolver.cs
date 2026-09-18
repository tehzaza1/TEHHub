namespace AreaModOffsetScanner
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json.Serialization;
    using TEHhub.Offsets.Objects.Components;

    /// <summary>
    ///     Safely inspects candidate vector entries across multiple element layout hypotheses:
    ///     - Hypothesis A: vector&lt;ModArrayStruct&gt; (stride 0x40, ModsPtr at +0x28 -> StringPtr at +0x00 -> UTF-16)
    ///     - Hypothesis B: vector&lt;IntPtr&gt; direct Mods.dat rows (stride 0x08, rowPtr -> StringPtr at +0x00 -> UTF-16)
    ///     - Hypothesis C: vector&lt;IntPtr&gt; ptrs to ModArrayStruct (stride 0x08, structPtr -> ModsPtr at +0x28 -> StringPtr at +0x00 -> UTF-16)
    /// </summary>
    public sealed class ModRecordResolver
    {
        private readonly Dictionary<IntPtr, string> stringCache = new();

        public sealed class HypothesisEvaluation
        {
            public string HypothesisCode { get; set; } = string.Empty; // "A", "B", "C"
            public string Description { get; set; } = string.Empty;
            public int Stride { get; set; }
            public bool StrideDivisible { get; set; }
            public long TotalElements { get; set; }
            public int AttemptedEntries { get; set; }
            public int ReadableEntries { get; set; }
            public int PlausibleModsPtrCount { get; set; }
            public int ReadableNameChainCount { get; set; }
            public int AsciiIdentifierCount { get; set; }
            public List<string> SampleNames { get; set; } = new();
        }

        /// <summary>
        ///     Hypothesis A: vector&lt;ModArrayStruct&gt; (stride 0x40 = 64 bytes).
        ///     ModsPtr is located at +0x28 in ModArrayStruct.
        /// </summary>
        public HypothesisEvaluation ResolveHypothesisA(NativeMemoryReader reader, IntPtr firstElementAddress, long usedBytes, int maxInspect = 100)
        {
            const int stride = 0x40;
            var isDivisible = usedBytes > 0 && usedBytes % stride == 0;
            var totalElements = isDivisible ? usedBytes / stride : 0;

            var result = new HypothesisEvaluation
            {
                HypothesisCode = "A",
                Description = "vector<ModArrayStruct> (stride 0x40, ModsPtr at +0x28)",
                Stride = stride,
                StrideDivisible = isDivisible,
                TotalElements = totalElements
            };

            if (!isDivisible || firstElementAddress == IntPtr.Zero || !NativeMemoryReader.IsValidAddress(firstElementAddress))
            {
                return result;
            }

            var inspectCount = (int)Math.Min(totalElements, maxInspect);
            for (var i = 0; i < inspectCount; i++)
            {
                var entryAddress = firstElementAddress + (i * stride);
                if (!NativeMemoryReader.IsValidAddress(entryAddress))
                {
                    break;
                }

                result.AttemptedEntries++;
                if (!reader.TryRead<ModArrayStruct>(entryAddress, out var mod))
                {
                    break;
                }

                result.ReadableEntries++;

                if (mod.ModsPtr == IntPtr.Zero || !NativeMemoryReader.IsValidAddress(mod.ModsPtr))
                {
                    continue;
                }

                result.PlausibleModsPtrCount++;

                var rawName = this.GetModName(reader, mod.ModsPtr);
                if (!string.IsNullOrEmpty(rawName))
                {
                    result.ReadableNameChainCount++;
                    if (IsAsciiIdentifierLikeName(rawName))
                    {
                        result.AsciiIdentifierCount++;
                        if (result.SampleNames.Count < 10)
                        {
                            result.SampleNames.Add(FormatSampleName(rawName));
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        ///     Hypothesis B: vector&lt;IntPtr&gt; direct Mods.dat row pointers (stride 0x08 = 8 bytes).
        ///     Element is directly a pointer to a Mods.dat record row.
        /// </summary>
        public HypothesisEvaluation ResolveHypothesisB(NativeMemoryReader reader, IntPtr firstElementAddress, long usedBytes, int maxInspect = 100)
        {
            const int stride = 0x08;
            var isDivisible = usedBytes > 0 && usedBytes % stride == 0;
            var totalElements = isDivisible ? usedBytes / stride : 0;

            var result = new HypothesisEvaluation
            {
                HypothesisCode = "B",
                Description = "vector<IntPtr> direct Mods.dat rows (stride 0x08)",
                Stride = stride,
                StrideDivisible = isDivisible,
                TotalElements = totalElements
            };

            if (!isDivisible || firstElementAddress == IntPtr.Zero || !NativeMemoryReader.IsValidAddress(firstElementAddress))
            {
                return result;
            }

            var inspectCount = (int)Math.Min(totalElements, maxInspect);
            for (var i = 0; i < inspectCount; i++)
            {
                var entryAddress = firstElementAddress + (i * stride);
                if (!NativeMemoryReader.IsValidAddress(entryAddress))
                {
                    break;
                }

                result.AttemptedEntries++;
                if (!reader.TryRead<IntPtr>(entryAddress, out var rowPtr))
                {
                    break;
                }

                result.ReadableEntries++;

                if (rowPtr == IntPtr.Zero || !NativeMemoryReader.IsValidAddress(rowPtr))
                {
                    continue;
                }

                result.PlausibleModsPtrCount++;

                var rawName = this.GetModName(reader, rowPtr);
                if (!string.IsNullOrEmpty(rawName))
                {
                    result.ReadableNameChainCount++;
                    if (IsAsciiIdentifierLikeName(rawName))
                    {
                        result.AsciiIdentifierCount++;
                        if (result.SampleNames.Count < 10)
                        {
                            result.SampleNames.Add(FormatSampleName(rawName));
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        ///     Hypothesis C: vector&lt;IntPtr&gt; pointers to ModArrayStruct / wrapper (stride 0x08 = 8 bytes).
        ///     Element is a pointer to a heap struct; ModsPtr is at structPtr + 0x28.
        /// </summary>
        public HypothesisEvaluation ResolveHypothesisC(NativeMemoryReader reader, IntPtr firstElementAddress, long usedBytes, int maxInspect = 100)
        {
            const int stride = 0x08;
            var isDivisible = usedBytes > 0 && usedBytes % stride == 0;
            var totalElements = isDivisible ? usedBytes / stride : 0;

            var result = new HypothesisEvaluation
            {
                HypothesisCode = "C",
                Description = "vector<IntPtr> ptrs to ModArrayStruct (stride 0x08, struct+0x28)",
                Stride = stride,
                StrideDivisible = isDivisible,
                TotalElements = totalElements
            };

            if (!isDivisible || firstElementAddress == IntPtr.Zero || !NativeMemoryReader.IsValidAddress(firstElementAddress))
            {
                return result;
            }

            var inspectCount = (int)Math.Min(totalElements, maxInspect);
            for (var i = 0; i < inspectCount; i++)
            {
                var entryAddress = firstElementAddress + (i * stride);
                if (!NativeMemoryReader.IsValidAddress(entryAddress))
                {
                    break;
                }

                result.AttemptedEntries++;
                if (!reader.TryRead<IntPtr>(entryAddress, out var structPtr))
                {
                    break;
                }

                result.ReadableEntries++;

                if (structPtr == IntPtr.Zero || !NativeMemoryReader.IsValidAddress(structPtr))
                {
                    continue;
                }

                // Read ModsPtr at structPtr + 0x28
                if (!reader.TryRead<IntPtr>(structPtr + 0x28, out var modsPtr) ||
                    modsPtr == IntPtr.Zero ||
                    !NativeMemoryReader.IsValidAddress(modsPtr))
                {
                    continue;
                }

                result.PlausibleModsPtrCount++;

                var rawName = this.GetModName(reader, modsPtr);
                if (!string.IsNullOrEmpty(rawName))
                {
                    result.ReadableNameChainCount++;
                    if (IsAsciiIdentifierLikeName(rawName))
                    {
                        result.AsciiIdentifierCount++;
                        if (result.SampleNames.Count < 10)
                        {
                            result.SampleNames.Add(FormatSampleName(rawName));
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        ///     Strict check for genuine game modifier identifiers (e.g. MapMonsterSpeed, AreaMonsterLevel, etc.).
        ///     Criteria:
        ///     1. Length between 3 and 100 characters.
        ///     2. Pure printable ASCII range (0x20 - 0x7E). Rejects CJK and Unicode control artifacts.
        ///     3. Allowed characters: ASCII alphanumeric, underscores, hyphens, slashes, periods, spaces, +, %.
        ///     4. Must contain at least one ASCII letter (A-Z, a-z).
        /// </summary>
        public static bool IsAsciiIdentifierLikeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length < 3 || name.Length > 100)
            {
                return false;
            }

            var hasLetter = false;
            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];

                // Strict ASCII check
                if (c < 32 || c > 126)
                {
                    return false;
                }

                if (char.IsAsciiLetter(c))
                {
                    hasLetter = true;
                    continue;
                }

                if (char.IsAsciiDigit(c) || c is '_' or '-' or '/' or '.' or ' ' or '+' or '%')
                {
                    continue;
                }

                // Any other unusual symbols are rejected
                return false;
            }

            return hasLetter;
        }

        /// <summary>
        ///     Dereferences candidate mod record row pointer:
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

            var name = reader.ReadUnicodeString(stringAddress, maxChars: 128);
            if (!string.IsNullOrEmpty(name))
            {
                this.stringCache[modsDatRowAddress] = name;
            }

            return name;
        }

        private static string FormatSampleName(string rawName)
        {
            return rawName.Length > 70 ? rawName[..67] + "..." : rawName;
        }
    }
}
