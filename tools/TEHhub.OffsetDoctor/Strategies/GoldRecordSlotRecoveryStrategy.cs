namespace TEHhub.OffsetDoctor.Strategies;

using TEHhub.OffsetDoctor.Evidence;
using TEHhub.OffsetDoctor.Manifest;
using TEHhub.OffsetDoctor.Process;

public sealed class GoldRecordSlotRecoveryStrategy : IRecoveryStrategy
{
    public ValueKind SupportedKind => ValueKind.RecordSlotField;

    public List<CandidateResult> SearchCandidates(
        IProcessMemoryReader reader,
        IntPtr parentAddress,
        OffsetNode node,
        RecoveryContext context)
    {
        var candidates = new List<CandidateResult>();
        if (!reader.IsValidAddress(parentAddress))
        {
            return candidates;
        }

        var startOffset = Math.Max(0, node.DefaultOffset - node.SearchRadius);
        var endOffset = node.DefaultOffset + node.SearchRadius;
        var step = node.Alignment > 0 ? node.Alignment : 8;

        var childNode = context.FindChild(node.Id);
        var goldFieldOffset = childNode != null
            ? context.ProvisionalOffsets.GetValueOrDefault(childNode.Id, childNode.DefaultOffset)
            : 0x618;

        for (var off = startOffset; off <= endOffset; off += step)
        {
            if (!reader.TryRead<IntPtr>(parentAddress + off, out var recordPtr))
            {
                continue;
            }

            if (recordPtr == IntPtr.Zero || !reader.IsValidAddress(recordPtr) || !reader.TryRead<byte>(recordPtr, out _))
            {
                continue;
            }

            var candidate = new CandidateResult
            {
                Offset = off,
                TargetAddress = recordPtr
            };

            int score = 0;

            // Validator 1: Valid user-mode memory address and readable target
            candidate.Evidence.Add(new EvidenceRecord
            {
                RuleName = "ValidAddress",
                Description = $"Record slot pointer 0x{recordPtr.ToInt64():X} points to readable memory",
                ScoreDelta = 25,
                Passed = true,
                IsIndependentValidator = true
            });
            score += 25;

            candidate.Evidence.Add(new EvidenceRecord
            {
                RuleName = "TargetMemoryReadable",
                Description = "Record memory page is readable",
                ScoreDelta = 20,
                Passed = true,
                IsIndependentValidator = false
            });
            score += 20;

            // Validator 2: Structural stride check on neighboring PSD record slots (0x80 stride)
            if (CheckNeighboringStride(reader, parentAddress, off, recordPtr))
            {
                candidate.Evidence.Add(new EvidenceRecord
                {
                    RuleName = "NeighboringSlotStridePattern",
                    Description = "Observed consistent 0x80 address stride across neighboring PSD slots",
                    ScoreDelta = 30,
                    Passed = true,
                    IsIndependentValidator = true
                });
                score += 30;
            }

            // Validator 3: Downstream Gold field check
            if (reader.TryRead<int>(recordPtr + goldFieldOffset, out var goldVal))
            {
                if (context.ExpectedGoldAmount.HasValue)
                {
                    if (goldVal == context.ExpectedGoldAmount.Value)
                    {
                        candidate.Evidence.Add(new EvidenceRecord
                        {
                            RuleName = "ExactGoldMatch",
                            Description = $"Exact expected gold match: {goldVal:N0} at +0x{goldFieldOffset:X}",
                            ScoreDelta = 50,
                            Passed = true,
                            IsIndependentValidator = true
                        });
                        score += 50;
                        candidate.DownstreamEvidenceSummary = $"Gold field matches exact {goldVal:N0}";
                        candidate.ExtractedValue = goldVal;
                    }
                }
                else if (goldVal >= 0 && goldVal <= 2_000_000_000)
                {
                    candidate.Evidence.Add(new EvidenceRecord
                    {
                        RuleName = "PlausibleGoldValue",
                        Description = $"Plausible non-negative gold value: {goldVal:N0} at +0x{goldFieldOffset:X} (unverified without --gold)",
                        ScoreDelta = 25,
                        Passed = true,
                        IsIndependentValidator = false
                    });
                    score += 25;
                    candidate.DownstreamEvidenceSummary = $"Gold field reads {goldVal:N0}";
                    candidate.ExtractedValue = goldVal;
                }
            }

            candidate.Score = score;
            candidate.Confidence = ConfidenceCalculator.Calculate(score, candidate.IndependentValidatorsCount);
            candidates.Add(candidate);
        }

        return candidates.OrderByDescending(c => c.Score).ThenBy(c => Math.Abs(c.Offset - node.DefaultOffset)).ToList();
    }

    private static bool CheckNeighboringStride(IProcessMemoryReader reader, IntPtr parentAddress, int currentOffset, IntPtr currentTarget)
    {
        // Check slot immediately preceding (at currentOffset - 8)
        if (currentOffset >= 8 && reader.TryRead<IntPtr>(parentAddress + currentOffset - 8, out var prevTarget))
        {
            if (prevTarget != IntPtr.Zero && reader.IsValidAddress(prevTarget))
            {
                var diff = currentTarget.ToInt64() - prevTarget.ToInt64();
                if (diff == 0x80)
                {
                    return true;
                }
            }
        }

        // Check slot immediately following (at currentOffset + 8)
        if (reader.TryRead<IntPtr>(parentAddress + currentOffset + 8, out var nextTarget))
        {
            if (nextTarget != IntPtr.Zero && reader.IsValidAddress(nextTarget))
            {
                var diff = nextTarget.ToInt64() - currentTarget.ToInt64();
                if (diff == 0x80)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
