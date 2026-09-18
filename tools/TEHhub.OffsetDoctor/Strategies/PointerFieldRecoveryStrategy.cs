namespace TEHhub.OffsetDoctor.Strategies;

using TEHhub.OffsetDoctor.Evidence;
using TEHhub.OffsetDoctor.Manifest;
using TEHhub.OffsetDoctor.Process;

public sealed class PointerFieldRecoveryStrategy : IRecoveryStrategy
{
    public ValueKind SupportedKind => ValueKind.PointerField;

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
            if (!reader.TryRead<IntPtr>(parentAddress + off, out var ptr))
            {
                continue;
            }

            if (ptr == IntPtr.Zero || !reader.IsValidAddress(ptr) || !reader.TryRead<byte>(ptr, out _))
            {
                continue;
            }

            var candidate = new CandidateResult
            {
                Offset = off,
                TargetAddress = ptr
            };

            int score = 0;

            // Validator 1: Valid user-mode memory address and readable target
            candidate.Evidence.Add(new EvidenceRecord
            {
                RuleName = "ValidAddress",
                Description = $"Pointer 0x{ptr.ToInt64():X} points to valid and readable memory",
                ScoreDelta = 25,
                Passed = true,
                IsIndependentValidator = true
            });
            score += 25;

            // Validator 2: Target memory page is readable
            if (reader.TryRead<long>(ptr, out _))
            {
                candidate.Evidence.Add(new EvidenceRecord
                {
                    RuleName = "TargetMemoryReadable",
                    Description = "Target address memory page is committed and readable",
                    ScoreDelta = 25,
                    Passed = true,
                    IsIndependentValidator = false
                });
                score += 25;
            }

            // Downstream validation check if child exists
            if (childNode != null)
            {
                var childOffset = context.ProvisionalOffsets.GetValueOrDefault(childNode.Id, childNode.DefaultOffset);
                if (childNode.Kind == ValueKind.PointerField || childNode.Kind == ValueKind.RecordSlotField)
                {
                    if (reader.TryRead<IntPtr>(ptr + childOffset, out var childPtr) &&
                        childPtr != IntPtr.Zero &&
                        reader.IsValidAddress(childPtr) &&
                        reader.TryRead<byte>(childPtr, out _))
                    {
                        candidate.Evidence.Add(new EvidenceRecord
                        {
                            RuleName = "DownstreamPointerValid",
                            Description = $"Downstream node '{childNode.Id}' at +0x{childOffset:X} points to valid readable address 0x{childPtr.ToInt64():X}",
                            ScoreDelta = 50,
                            Passed = true,
                            IsIndependentValidator = true
                        });
                        score += 50;
                        candidate.DownstreamEvidenceSummary = $"Downstream '{childNode.Id}' validated at +0x{childOffset:X}";
                    }
                }
                else if (childNode.Kind == ValueKind.StdVectorField)
                {
                    if (reader.TryRead<IntPtr>(ptr + childOffset, out var b) &&
                        reader.TryRead<IntPtr>(ptr + childOffset + 8, out var e) &&
                        reader.TryRead<IntPtr>(ptr + childOffset + 16, out var c))
                    {
                        if (reader.IsValidAddress(b) && reader.IsValidAddress(e) &&
                            reader.TryRead<byte>(b, out _) &&
                            b.ToInt64() <= e.ToInt64() && (c == IntPtr.Zero || e.ToInt64() <= c.ToInt64()))
                        {
                            candidate.Evidence.Add(new EvidenceRecord
                            {
                                RuleName = "DownstreamVectorValid",
                                Description = $"Downstream StdVector '{childNode.Id}' at +0x{childOffset:X} is structurally valid",
                                ScoreDelta = 50,
                                Passed = true,
                                IsIndependentValidator = true
                            });
                            score += 50;
                            candidate.DownstreamEvidenceSummary = $"Downstream vector '{childNode.Id}' validated at +0x{childOffset:X}";
                        }
                    }
                }
            }

            candidate.Score = score;
            candidate.Confidence = ConfidenceCalculator.Calculate(score, candidate.IndependentValidatorsCount);
            candidates.Add(candidate);
        }

        return candidates.OrderByDescending(c => c.Score).ThenBy(c => Math.Abs(c.Offset - node.DefaultOffset)).ToList();
    }
}
