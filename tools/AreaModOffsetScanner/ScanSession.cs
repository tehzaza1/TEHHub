namespace AreaModOffsetScanner
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using TEHhub.Offsets.Natives;

    /// <summary>
    ///     Executes a candidate memory scan across a configurable range with multi-hypothesis inspection.
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
            public string ScanTargetBaseName { get; init; } = "PlayerServerData";
            public int StartOffset { get; init; }
            public int EndOffset { get; init; }
            public int Step { get; init; }
            public int? ExpectedUiModCount { get; init; }
            public DateTime Timestamp { get; init; }
            public int TotalOffsetsInspected { get; init; }
            public int BasicStructurallyValidCount { get; init; }
            public int HypothesisAPlausibleCount { get; init; }
            public int HypothesisBPlausibleCount { get; init; }
            public int HypothesisCPlausibleCount { get; init; }
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
            IntPtr baseAddress,
            IntPtr playerServerDataAddress,
            IntPtr serverDataObjectAddress,
            string targetBaseName = "PlayerServerData",
            int startOffset = 0x0000,
            int endOffset = 0x4000,
            int step = 8,
            int? expectedUiModCount = null)
        {
            var sw = Stopwatch.StartNew();
            var resolver = new ModRecordResolver();
            var allEvaluated = new List<AreaModCandidate>();

            var totalInspected = 0;
            var basicValidCount = 0;
            var hypACount = 0;
            var hypBCount = 0;
            var hypCCount = 0;

            for (var offset = startOffset; offset <= endOffset; offset += step)
            {
                totalInspected++;
                var candidateAddr = baseAddress + offset;

                if (!reader.TryRead<StdVector>(candidateAddr, out var vec))
                {
                    continue;
                }

                var basicEval = VectorEvaluator.EvaluateBasic(vec);
                var isSpecial = offset is 0x8A8 or 0xD8 or 0x120;

                if (basicEval.IsBasicValid && basicEval.UsedBytes > 0)
                {
                    basicValidCount++;
                }

                var specialTag = offset switch
                {
                    0x8A8 => "[UNVERIFIED HARD-CODED 0x8A8]",
                    0xD8 => "[LIVE FALSE-POSITIVE 0xD8]",
                    0x120 => "[LIVE FALSE-POSITIVE 0x120]",
                    _ => string.Empty
                };

                var hypA = new ModRecordResolver.HypothesisEvaluation();
                var hypB = new ModRecordResolver.HypothesisEvaluation();
                var hypC = new ModRecordResolver.HypothesisEvaluation();

                // Only deep-inspect memory if the vector satisfies basic structural bounds and ordering
                // (First <= Last <= End and non-null pointers are valid user-mode addresses)
                if (basicEval.IsBasicValid &&
                    basicEval.UsedBytes > 0 &&
                    basicEval.UsedBytes <= (150 * 0x40) && // Reasonable mod vector size upper bound (9.6 KB)
                    vec.First != IntPtr.Zero &&
                    NativeMemoryReader.IsValidAddress(vec.First))
                {
                    // Hypothesis A: vector<ModArrayStruct> (stride 0x40)
                    if (basicEval.UsedBytes % 0x40 == 0)
                    {
                        hypA = resolver.ResolveHypothesisA(reader, vec.First, basicEval.UsedBytes);
                        if (hypA.AsciiIdentifierCount > 0)
                        {
                            hypACount++;
                        }
                    }

                    // Hypothesis B: vector<IntPtr> direct Mods.dat rows (stride 0x08)
                    if (basicEval.UsedBytes % 0x08 == 0)
                    {
                        hypB = resolver.ResolveHypothesisB(reader, vec.First, basicEval.UsedBytes);
                        if (hypB.AsciiIdentifierCount > 0)
                        {
                            hypBCount++;
                        }
                    }

                    // Hypothesis C: vector<IntPtr> pointers to ModArrayStruct (stride 0x08)
                    if (basicEval.UsedBytes % 0x08 == 0)
                    {
                        hypC = resolver.ResolveHypothesisC(reader, vec.First, basicEval.UsedBytes);
                        if (hypC.AsciiIdentifierCount > 0)
                        {
                            hypCCount++;
                        }
                    }
                }

                var maxAscii = Math.Max(hypA.AsciiIdentifierCount, Math.Max(hypB.AsciiIdentifierCount, hypC.AsciiIdentifierCount));
                var bestHypCode = "None";
                if (maxAscii > 0)
                {
                    if (maxAscii == hypA.AsciiIdentifierCount)
                    {
                        bestHypCode = "A";
                    }
                    else if (maxAscii == hypB.AsciiIdentifierCount)
                    {
                        bestHypCode = "B";
                    }
                    else if (maxAscii == hypC.AsciiIdentifierCount)
                    {
                        bestHypCode = "C";
                    }
                }

                var hasReadableChains = hypA.ReadableNameChainCount > 0 || hypB.ReadableNameChainCount > 0 || hypC.ReadableNameChainCount > 0;
                var hasReasonableElements = (basicEval.UsedBytes > 0 && basicEval.UsedBytes <= (100 * 0x40));

                if (isSpecial || maxAscii > 0 || hasReadableChains || (basicEval.IsBasicValid && hasReasonableElements))
                {
                    allEvaluated.Add(new AreaModCandidate
                    {
                        Offset = offset,
                        First = $"0x{vec.First.ToInt64():X11}",
                        Last = $"0x{vec.Last.ToInt64():X11}",
                        End = $"0x{vec.End.ToInt64():X11}",
                        IsBasicValid = basicEval.IsBasicValid,
                        StructuralFailureReason = basicEval.FailureReasons,
                        UsedBytes = basicEval.UsedBytes,
                        CapacityBytes = basicEval.CapacityBytes,
                        HypothesisA = hypA,
                        HypothesisB = hypB,
                        HypothesisC = hypC,
                        BestHypothesisCode = bestHypCode,
                        BestHypothesisAsciiCount = maxAscii,
                        SpecialTag = specialTag
                    });
                }
            }

            sw.Stop();

            // Ranking for diagnostic presentation:
            // 1. Highest number of strict ASCII identifiers
            // 2. Highest number of readable name chains across any hypothesis
            // 3. Structurally valid vectors
            // 4. Smaller distance between element count and expected UI mods (if expected provided)
            // 5. Offset ascending
            var ranked = allEvaluated
                .OrderByDescending(c => c.BestHypothesisAsciiCount)
                .ThenByDescending(c => Math.Max(c.HypothesisA.ReadableNameChainCount, Math.Max(c.HypothesisB.ReadableNameChainCount, c.HypothesisC.ReadableNameChainCount)))
                .ThenByDescending(c => c.IsBasicValid)
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
                ScanTargetBaseName = targetBaseName,
                StartOffset = startOffset,
                EndOffset = endOffset,
                Step = step,
                ExpectedUiModCount = expectedUiModCount,
                Timestamp = DateTime.UtcNow,
                TotalOffsetsInspected = totalInspected,
                BasicStructurallyValidCount = basicValidCount,
                HypothesisAPlausibleCount = hypACount,
                HypothesisBPlausibleCount = hypBCount,
                HypothesisCPlausibleCount = hypCCount,
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
