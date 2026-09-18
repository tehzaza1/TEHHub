namespace TEHhub.OffsetDoctor.Strategies;

using TEHhub.OffsetDoctor.Evidence;
using TEHhub.OffsetDoctor.Manifest;
using TEHhub.OffsetDoctor.Process;

public sealed class NumericFieldRecoveryStrategy : IRecoveryStrategy
{
    public ValueKind SupportedKind => ValueKind.NumericField;

    public List<CandidateResult> SearchCandidates(
        IProcessMemoryReader reader,
        IntPtr parentAddress,
        OffsetNode node,
        RecoveryContext context)
    {
        var candidates = new List<CandidateResult>();
        if (!reader.IsValidAddress(parentAddress) || !reader.TryRead<byte>(parentAddress, out _))
        {
            return candidates;
        }

        var startOffset = Math.Max(0, node.DefaultOffset - node.SearchRadius);
        var endOffset = node.DefaultOffset + node.SearchRadius;
        var step = node.Alignment > 0 ? node.Alignment : 4;

        var rawMatches = new List<(int offset, int val)>();

        for (var off = startOffset; off <= endOffset; off += step)
        {
            if (!reader.TryRead<int>(parentAddress + off, out var val))
            {
                continue;
            }

            if (val < 0 || val > 2_000_000_000)
            {
                continue;
            }

            if (context.ExpectedGoldAmount.HasValue)
            {
                if (val == context.ExpectedGoldAmount.Value)
                {
                    rawMatches.Add((off, val));
                }
            }
            else
            {
                rawMatches.Add((off, val));
            }
        }

        bool isSingleExactMatch = context.ExpectedGoldAmount.HasValue && rawMatches.Count == 1;

        foreach (var (off, val) in rawMatches)
        {
            var candidate = new CandidateResult
            {
                Offset = off,
                TargetAddress = parentAddress + off,
                ExtractedValue = val
            };

            int score = 0;

            if (context.ExpectedGoldAmount.HasValue)
            {
                // Validator 1 (Semantic): Single exact expected value match (strictly 1 external validator)
                candidate.Evidence.Add(new EvidenceRecord
                {
                    RuleName = "ExactExpectedValueMatch",
                    Description = $"Value {val:N0} matches user-supplied expected gold amount {context.ExpectedGoldAmount.Value:N0}",
                    ScoreDelta = 65,
                    Passed = true,
                    IsIndependentValidator = true
                });
                score += 65;

                // Validator 2 (Structural/Uniqueness Discriminator): Genuinely distinct proof (unique exact match in record)
                if (isSingleExactMatch)
                {
                    candidate.Evidence.Add(new EvidenceRecord
                    {
                        RuleName = "UniqueRecordValueDiscriminator",
                        Description = "Single unique match for target value within validated record boundary",
                        ScoreDelta = 25,
                        Passed = true,
                        IsIndependentValidator = true
                    });
                    score += 25;
                }
                else if (off == node.DefaultOffset)
                {
                    candidate.Evidence.Add(new EvidenceRecord
                    {
                        RuleName = "DefaultOffsetPreserved",
                        Description = $"Offset aligns with compiled default offset +0x{node.DefaultOffset:X}",
                        ScoreDelta = 15,
                        Passed = true,
                        IsIndependentValidator = false
                    });
                    score += 15;
                }
            }
            else
            {
                // Plausible range alone without ground truth is NOT an independent validator
                candidate.Evidence.Add(new EvidenceRecord
                {
                    RuleName = "PlausibleNumericRange",
                    Description = $"Value {val:N0} is non-negative and <= 2,000,000,000 (unverified without --gold)",
                    ScoreDelta = 35,
                    Passed = true,
                    IsIndependentValidator = false
                });
                score += 35;

                if (off == node.DefaultOffset)
                {
                    candidate.Evidence.Add(new EvidenceRecord
                    {
                        RuleName = "DefaultOffsetPreserved",
                        Description = $"Offset aligns with compiled default offset +0x{node.DefaultOffset:X}",
                        ScoreDelta = 25,
                        Passed = true,
                        IsIndependentValidator = false
                    });
                    score += 25;
                }
            }

            candidate.Score = score;
            candidate.Confidence = ConfidenceCalculator.Calculate(score, candidate.IndependentValidatorsCount);
            candidates.Add(candidate);
        }

        return candidates.OrderByDescending(c => c.Score).ThenBy(c => Math.Abs(c.Offset - node.DefaultOffset)).ToList();
    }
}
