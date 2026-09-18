namespace AreaModOffsetScanner
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using TEHhub.Offsets.Natives;

    /// <summary>
    ///     Executes a candidate memory scan across a configurable range.
    /// </summary>
    public sealed class ScanSession
    {
        public sealed class ScanMetadata
        {
            public int ProcessId { get; init; }
            public string ProcessName { get; init; } = string.Empty;
            public string MainModuleFileName { get; init; } = string.Empty;
            public string ServerDataObjectAddressHex { get; init; } = "0x0";
            public long ServerDataObjectAddress { get; init; }
            public string PlayerServerDataAddressHex { get; init; } = "0x0";
            public long PlayerServerDataAddress { get; init; }
            public int StartOffset { get; init; }
            public int EndOffset { get; init; }
            public int Step { get; init; }
            public int? ExpectedUiModCount { get; init; }
            public DateTime Timestamp { get; init; }
            public int TotalOffsetsInspected { get; init; }
            public int StructurallyValidCount { get; init; }
            public int StructurallyInvalidPlausibleCount { get; init; }
            public int DeepInspectedCandidatesCount { get; init; }
            public int CandidatesWithReadableNameChainsCount { get; init; }
            public int CandidatesWithPlausibleNamesCount { get; init; }
            public long ElapsedMilliseconds { get; init; }
        }

        public sealed class ScanResult
        {
            public ScanMetadata Metadata { get; init; } = new();
            public List<AreaModCandidate> AllEvaluatedCandidates { get; init; } = new();
            public List<AreaModCandidate> RankedCandidates { get; init; } = new();
        }

        public static ScanResult Execute(
            NativeMemoryReader reader,
            IntPtr playerServerDataAddress,
            IntPtr serverDataObjectAddress,
            int startOffset = 0x0000,
            int endOffset = 0x4000,
            int step = 8,
            int? expectedUiModCount = null)
        {
            var sw = Stopwatch.StartNew();
            var resolver = new ModRecordResolver();
            var allEvaluated = new List<AreaModCandidate>();

            var totalInspected = 0;
            var structurallyValidCount = 0;
            var structurallyInvalidPlausibleCount = 0;
            var deepInspectedCount = 0;
            var candidatesWithReadableNameChainsCount = 0;
            var candidatesWithPlausibleNamesCount = 0;

            for (var offset = startOffset; offset <= endOffset; offset += step)
            {
                totalInspected++;
                var candidateAddr = playerServerDataAddress + offset;

                if (!reader.TryRead<StdVector>(candidateAddr, out var vec))
                {
                    continue;
                }

                var eval = VectorEvaluator.Evaluate(vec, VectorEvaluator.ModArrayStride);
                var isPlausible = eval.ElementCount is > 0 and < 150;
                var isSpecial = offset is 0x8A8 or 0xD8 or 0x120;

                if (eval.IsStructurallyValid && eval.ElementCount > 0)
                {
                    structurallyValidCount++;
                }
                else if (!eval.IsStructurallyValid && isPlausible)
                {
                    structurallyInvalidPlausibleCount++;
                }

                var specialTag = offset switch
                {
                    0x8A8 => "[UNVERIFIED HARD-CODED 0x8A8]",
                    0xD8 => "[LIVE FALSE-POSITIVE 0xD8]",
                    0x120 => "[LIVE FALSE-POSITIVE 0x120]",
                    _ => string.Empty
                };

                var resolution = default(ModRecordResolver.ResolutionResult);

                // Deep inspect ModArrayStruct ONLY if the vector is structurally valid.
                // Structurally invalid vectors (e.g. 0xD8 with End < Last) are recorded
                // for diagnostics but never parsed as legitimate containers.
                var shouldInspect = eval.IsStructurallyValid &&
                                    eval.ElementCount is > 0 and < 150 &&
                                    vec.First != IntPtr.Zero &&
                                    NativeMemoryReader.IsValidAddress(vec.First);

                if (shouldInspect)
                {
                    deepInspectedCount++;
                    var maxEntries = (int)Math.Clamp(eval.ElementCount, 1, 150);
                    resolution = resolver.ResolveCandidate(reader, vec.First, maxEntries);

                    if (resolution.ReadableNameChainCount > 0)
                    {
                        candidatesWithReadableNameChainsCount++;
                    }

                    if (resolution.PlausibleRawNameCount > 0)
                    {
                        candidatesWithPlausibleNamesCount++;
                    }
                }

                if (isPlausible || isSpecial || resolution.ReadableNameChainCount > 0 || (eval.IsStructurallyValid && eval.ElementCount > 0))
                {
                    allEvaluated.Add(new AreaModCandidate
                    {
                        Offset = offset,
                        First = $"0x{vec.First.ToInt64():X11}",
                        Last = $"0x{vec.Last.ToInt64():X11}",
                        End = $"0x{vec.End.ToInt64():X11}",
                        IsStructurallyValid = eval.IsStructurallyValid,
                        StructuralFailureReason = eval.FailureReasons,
                        ElementCount = eval.ElementCount,
                        CapacityCount = eval.CapacityCount,
                        AttemptedEntries = resolution.AttemptedEntries,
                        ReadableEntries = resolution.ReadableEntries,
                        PlausibleModsPtrCount = resolution.PlausibleModsPtrCount,
                        ReadableNameChainCount = resolution.ReadableNameChainCount,
                        PlausibleRawNameCount = resolution.PlausibleRawNameCount,
                        SampleRawNames = resolution.SampleRawNames ?? new List<string>(),
                        SpecialTag = specialTag
                    });
                }
            }

            sw.Stop();

            // Ranking for diagnostic presentation only:
            // 1. Structurally valid vectors first
            // 2. More plausible RawName identifiers
            // 3. More readable string chains
            // 4. Smaller mismatch between vector element count and plausible name count
            // 5. Offset ascending
            var ranked = allEvaluated
                .OrderByDescending(c => c.IsStructurallyValid)
                .ThenByDescending(c => c.PlausibleRawNameCount)
                .ThenByDescending(c => c.ReadableNameChainCount)
                .ThenBy(c => Math.Abs(c.ElementCount - c.PlausibleRawNameCount))
                .ThenBy(c => c.Offset)
                .ToList();

            var metadata = new ScanMetadata
            {
                ProcessId = reader.ProcessId,
                ProcessName = reader.ProcessName,
                MainModuleFileName = reader.MainModuleFileName,
                ServerDataObjectAddressHex = $"0x{serverDataObjectAddress.ToInt64():X11}",
                ServerDataObjectAddress = serverDataObjectAddress.ToInt64(),
                PlayerServerDataAddressHex = $"0x{playerServerDataAddress.ToInt64():X11}",
                PlayerServerDataAddress = playerServerDataAddress.ToInt64(),
                StartOffset = startOffset,
                EndOffset = endOffset,
                Step = step,
                ExpectedUiModCount = expectedUiModCount,
                Timestamp = DateTime.UtcNow,
                TotalOffsetsInspected = totalInspected,
                StructurallyValidCount = structurallyValidCount,
                StructurallyInvalidPlausibleCount = structurallyInvalidPlausibleCount,
                DeepInspectedCandidatesCount = deepInspectedCount,
                CandidatesWithReadableNameChainsCount = candidatesWithReadableNameChainsCount,
                CandidatesWithPlausibleNamesCount = candidatesWithPlausibleNamesCount,
                ElapsedMilliseconds = sw.ElapsedMilliseconds
            };

            return new ScanResult
            {
                Metadata = metadata,
                AllEvaluatedCandidates = allEvaluated,
                RankedCandidates = ranked
            };
        }
    }
}
