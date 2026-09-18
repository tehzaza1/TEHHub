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

            if (recordPtr == IntPtr.Zero || !reader.IsValidAddress(recordPtr))
            {
                continue;
            }

            var candidate = new CandidateResult
            {
                Offset = off,
                TargetAddress = recordPtr
            };

            int score = 0;

            // Validator 1: Valid user-mode memory address
            candidate.Evidence.Add(new EvidenceRecord
            {
                RuleName = "ValidAddress",
                Description = $"Record slot pointer 0x{recordPtr.ToInt64():X} is valid memory",
                ScoreDelta = 25,
                Passed = true,
                IsIndependentValidator = true
            });
            score += 25;

            // Validator 2: Target memory page is readable
            if (reader.TryRead<long>(recordPtr, out _))
            {
                candidate.Evidence.Add(new EvidenceRecord
                {
                    RuleName = "TargetMemoryReadable",
                    Description = "Record memory page is readable",
                    ScoreDelta = 25,
                    Passed = true,
                    IsIndependentValidator = false
                });
                score += 25;
            }

            // Validator 3: Downstream Gold field check
            if (reader.TryRead<int>(recordPtr + goldFieldOffset, out var goldVal))
            {
                if (goldVal >= 0 && goldVal <= 2_000_000_000)
                {
                    bool isExpectedMatch = context.ExpectedGoldAmount.HasValue && goldVal == context.ExpectedGoldAmount.Value;
                    int delta = isExpectedMatch ? 50 : 35;

                    candidate.Evidence.Add(new EvidenceRecord
                    {
                        RuleName = isExpectedMatch ? "ExactGoldMatch" : "PlausibleGoldValue",
                        Description = isExpectedMatch
                            ? $"Exact expected gold match: {goldVal:N0} at +0x{goldFieldOffset:X}"
                            : $"Plausible non-negative gold value: {goldVal:N0} at +0x{goldFieldOffset:X}",
                        ScoreDelta = delta,
                        Passed = true,
                        IsIndependentValidator = true
                    });
                    score += delta;
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
}
