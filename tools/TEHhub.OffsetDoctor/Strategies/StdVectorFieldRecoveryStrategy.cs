namespace TEHhub.OffsetDoctor.Strategies;

using TEHhub.OffsetDoctor.Evidence;
using TEHhub.OffsetDoctor.Manifest;
using TEHhub.OffsetDoctor.Process;

public sealed class StdVectorFieldRecoveryStrategy : IRecoveryStrategy
{
    public ValueKind SupportedKind => ValueKind.StdVectorField;

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

        for (var off = startOffset; off <= endOffset; off += step)
        {
            if (!reader.TryRead<IntPtr>(parentAddress + off, out var begin) ||
                !reader.TryRead<IntPtr>(parentAddress + off + 8, out var end) ||
                !reader.TryRead<IntPtr>(parentAddress + off + 16, out var cap))
            {
                continue;
            }

            var bVal = (ulong)begin.ToInt64();
            var eVal = (ulong)end.ToInt64();
            var cVal = (ulong)cap.ToInt64();

            if (bVal == 0 || eVal == 0 || bVal > eVal)
            {
                continue;
            }

            if (cVal != 0 && eVal > cVal)
            {
                continue;
            }

            if (!reader.IsValidAddress(begin) || (bVal != eVal && !reader.IsValidAddress(end)))
            {
                continue;
            }

            var byteSize = (long)(eVal - bVal);
            if (byteSize % 8 != 0 || byteSize > 100_000)
            {
                continue;
            }

            var elemCount = byteSize / 8;
            IntPtr targetAddress = begin;
            if (elemCount > 0 && reader.TryRead<IntPtr>(begin, out var firstElem) && reader.IsValidAddress(firstElem))
            {
                targetAddress = firstElem;
            }

            var candidate = new CandidateResult
            {
                Offset = off,
                TargetAddress = targetAddress,
                ExtractedValue = $"StdVector [Length={elemCount}, Begin=0x{bVal:X}, End=0x{eVal:X}]"
            };

            int score = 0;

            // Validator 1: Vector layout integrity
            candidate.Evidence.Add(new EvidenceRecord
            {
                RuleName = "StdVectorLayoutIntegrity",
                Description = $"Begin (0x{bVal:X}) <= End (0x{eVal:X}) <= Capacity (0x{cVal:X}), elementCount={elemCount}",
                ScoreDelta = 35,
                Passed = true,
                IsIndependentValidator = true
            });
            score += 35;

            // Validator 2: Element bounds & address validity
            candidate.Evidence.Add(new EvidenceRecord
            {
                RuleName = "VectorMemoryValid",
                Description = "Vector begin address is in committed user memory",
                ScoreDelta = 25,
                Passed = true,
                IsIndependentValidator = false
            });
            score += 25;

            // Validator 3: Downstream child probe
            if (childNode != null && targetAddress != IntPtr.Zero && reader.IsValidAddress(targetAddress))
            {
                var childOffset = context.ProvisionalOffsets.GetValueOrDefault(childNode.Id, childNode.DefaultOffset);
                if (reader.TryRead<IntPtr>(targetAddress + childOffset, out var childPtr) && reader.IsValidAddress(childPtr))
                {
                    candidate.Evidence.Add(new EvidenceRecord
                    {
                        RuleName = "DownstreamChildValid",
                        Description = $"Downstream node '{childNode.Id}' at +0x{childOffset:X} points to valid address 0x{childPtr.ToInt64():X}",
                        ScoreDelta = 40,
                        Passed = true,
                        IsIndependentValidator = true
                    });
                    score += 40;
                    candidate.DownstreamEvidenceSummary = $"Downstream '{childNode.Id}' verified from vector target";
                }
            }

            candidate.Score = score;
            candidate.Confidence = ConfidenceCalculator.Calculate(score, candidate.IndependentValidatorsCount);
            candidates.Add(candidate);
        }

        return candidates.OrderByDescending(c => c.Score).ThenBy(c => Math.Abs(c.Offset - node.DefaultOffset)).ToList();
    }
}
