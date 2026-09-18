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
        if (!reader.IsValidAddress(parentAddress))
        {
            return candidates;
        }

        var startOffset = Math.Max(0, node.DefaultOffset - node.SearchRadius);
        var endOffset = node.DefaultOffset + node.SearchRadius;
        var step = node.Alignment > 0 ? node.Alignment : 4;

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

            var candidate = new CandidateResult
            {
                Offset = off,
                TargetAddress = parentAddress + off,
                ExtractedValue = val
            };

            int score = 0;

            // Validator 1: Plausible non-negative numeric range
            candidate.Evidence.Add(new EvidenceRecord
            {
                RuleName = "PlausibleNumericRange",
                Description = $"Value {val:N0} is non-negative and <= 2,000,000,000",
                ScoreDelta = 35,
                Passed = true,
                IsIndependentValidator = true
            });
            score += 35;

            // Validator 2: Exact expected value match (if user provided --gold <amount>)
            if (context.ExpectedGoldAmount.HasValue && val == context.ExpectedGoldAmount.Value)
            {
                candidate.Evidence.Add(new EvidenceRecord
                {
                    RuleName = "ExactExpectedValueMatch",
                    Description = $"Value {val:N0} matches user-supplied expected gold amount {context.ExpectedGoldAmount.Value:N0}",
                    ScoreDelta = 65,
                    Passed = true,
                    IsIndependentValidator = true
                });
                score += 65;
            }
            else if (off == node.DefaultOffset)
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

            candidate.Score = score;
            candidate.Confidence = ConfidenceCalculator.Calculate(score, candidate.IndependentValidatorsCount);
            candidates.Add(candidate);
        }

        return candidates.OrderByDescending(c => c.Score).ThenBy(c => Math.Abs(c.Offset - node.DefaultOffset)).ToList();
    }
}
