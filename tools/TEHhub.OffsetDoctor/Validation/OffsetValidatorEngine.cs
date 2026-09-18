namespace TEHhub.OffsetDoctor.Validation;

using TEHhub.OffsetDoctor.Evidence;
using TEHhub.OffsetDoctor.Manifest;
using TEHhub.OffsetDoctor.Process;
using TEHhub.OffsetDoctor.Strategies;
using TEHhub.Offsets;
using TEHhub.Offsets.Objects;

public sealed class OffsetValidatorEngine
{
    private readonly Dictionary<ValueKind, IRecoveryStrategy> _strategies = new();

    public OffsetValidatorEngine()
    {
        RegisterStrategy(new PointerFieldRecoveryStrategy());
        RegisterStrategy(new StdVectorFieldRecoveryStrategy());
        RegisterStrategy(new GoldRecordSlotRecoveryStrategy());
        RegisterStrategy(new NumericFieldRecoveryStrategy());
    }

    public void RegisterStrategy(IRecoveryStrategy strategy)
    {
        _strategies[strategy.SupportedKind] = strategy;
    }

    public List<ValidationResult> ValidateChain(
        IProcessMemoryReader reader,
        List<OffsetNode> manifestNodes,
        RecoveryContext context,
        bool allowRecovery = true)
    {
        var results = new List<ValidationResult>();
        var resolvedAddresses = new Dictionary<string, IntPtr>();
        var provisionalAddresses = new Dictionary<string, IntPtr>();

        foreach (var node in manifestNodes)
        {
            var result = new ValidationResult
            {
                NodeId = node.Id,
                NodeDisplayName = node.DisplayName,
                ConfiguredOffset = context.ProvisionalOffsets.GetValueOrDefault(node.Id, node.DefaultOffset)
            };

            // 1. Root node validation (Game States static pattern)
            if (node.ParentId == null)
            {
                ValidateRootNode(reader, node, result);
                if (result.Status == ValidationStatus.VALID)
                {
                    resolvedAddresses[node.Id] = result.ResolvedAddress;
                }
                results.Add(result);
                continue;
            }

            // 2. Determine parent address (authoritative or provisional)
            IntPtr parentAddr = IntPtr.Zero;
            bool isParentProvisional = false;

            if (resolvedAddresses.TryGetValue(node.ParentId, out var authAddr) && authAddr != IntPtr.Zero)
            {
                parentAddr = authAddr;
            }
            else if (allowRecovery && provisionalAddresses.TryGetValue(node.ParentId, out var provAddr) && provAddr != IntPtr.Zero)
            {
                parentAddr = provAddr;
                isParentProvisional = true;
            }

            if (parentAddr == IntPtr.Zero || !reader.IsValidAddress(parentAddr) || !reader.TryRead<byte>(parentAddr, out _))
            {
                result.Status = ValidationStatus.BLOCKED;
                result.ErrorMessage = $"Blocked: Parent node '{node.ParentId}' is not valid or recovered.";
                results.Add(result);
                continue;
            }

            result.IsProvisional = isParentProvisional;

            // 3. Evaluate configured offset
            EvaluateNodeAtOffset(reader, parentAddr, node, result.ConfiguredOffset, result, context);

            if (result.Status == ValidationStatus.VALID)
            {
                if (isParentProvisional)
                {
                    provisionalAddresses[node.Id] = result.ResolvedAddress;
                }
                else
                {
                    resolvedAddresses[node.Id] = result.ResolvedAddress;
                }
            }
            else if (allowRecovery)
            {
                // Node is BROKEN / NEEDS_MANUAL_PROOF at configured offset. Attempt bounded recovery only in recover mode.
                if (_strategies.TryGetValue(node.Kind, out var strategy))
                {
                    var candidates = strategy.SearchCandidates(reader, parentAddr, node, context);
                    result.Candidates = candidates;

                    if (candidates.Count == 1)
                    {
                        var single = candidates[0];
                        result.BestCandidate = single;

                        if (single.Confidence == Confidence.HIGH)
                        {
                            result.Status = ValidationStatus.CANDIDATE_FOUND;
                            provisionalAddresses[node.Id] = single.TargetAddress;
                        }
                        else
                        {
                            result.Status = ValidationStatus.NEEDS_MANUAL_PROOF;
                            // Do NOT apply weak candidate provisionally
                        }
                    }
                    else if (candidates.Count > 1)
                    {
                        var topCandidate = candidates[0];
                        var secondCandidate = candidates[1];

                        // A candidate may be used automatically as a provisional offset ONLY when:
                        // - BestCandidate.Confidence == HIGH
                        // - Meaningful score separation (>= 20)
                        // - Not ambiguous
                        if (topCandidate.Confidence == Confidence.HIGH && (topCandidate.Score - secondCandidate.Score >= 20))
                        {
                            result.BestCandidate = topCandidate;
                            result.Status = ValidationStatus.CANDIDATE_FOUND;
                            provisionalAddresses[node.Id] = topCandidate.TargetAddress;
                        }
                        else
                        {
                            result.BestCandidate = null;
                            result.Status = ValidationStatus.AMBIGUOUS;
                            result.ErrorMessage = $"Multiple competing candidates found ({candidates.Count}) with insufficient score separation. Manual proof required.";
                            // Do NOT apply ambiguous candidate provisionally
                        }
                    }
                    else
                    {
                        result.Status = ValidationStatus.BROKEN;
                        result.ErrorMessage = $"No recovery candidates discovered within radius ±0x{node.SearchRadius:X}.";
                    }
                }
                else
                {
                    result.Status = ValidationStatus.UNSUPPORTED_RECOVERY;
                }
            }

            results.Add(result);
        }

        return results;
    }

    private void ValidateRootNode(IProcessMemoryReader reader, OffsetNode node, ValidationResult result)
    {
        if (reader.MainModuleBase == IntPtr.Zero || reader.MainModuleSize <= 0)
        {
            result.Status = ValidationStatus.BROKEN;
            result.ErrorMessage = "Main module base address or size is invalid.";
            return;
        }

        // 1. Try static pattern scan for "Game States"
        var baseAddr = reader.MainModuleBase;
        var size = reader.MainModuleSize;

        var patterns = FindGameStatesPattern(reader, baseAddr, size);
        if (patterns != null && patterns.TryGetValue(node.StaticPatternName ?? "Game States", out var gsOffset))
        {
            if (reader.TryRead<int>(baseAddr + gsOffset, out var offsetDataValue))
            {
                var gameStatesAddr = baseAddr + gsOffset + offsetDataValue + 0x04;
                if (reader.TryRead<GameStateStaticOffset>(gameStatesAddr, out var staticObj) &&
                    reader.IsValidAddress(staticObj.GameState) &&
                    reader.TryRead<byte>(staticObj.GameState, out _))
                {
                    result.Status = ValidationStatus.VALID;
                    result.ResolvedAddress = staticObj.GameState;
                    result.ExtractedValue = $"GameStateStaticObj (0x{staticObj.GameState.ToInt64():X})";
                    result.Evidence.Add(new EvidenceRecord
                    {
                        RuleName = "GameStatePatternMatch",
                        Description = $"Pattern '{node.StaticPatternName}' resolved to 0x{staticObj.GameState.ToInt64():X}",
                        ScoreDelta = 50,
                        Passed = true,
                        IsIndependentValidator = true
                    });
                    return;
                }
            }
        }

        // 2. Direct GameState pointer check if synthetic or pre-resolved at base
        if (reader.TryRead<GameStateStaticOffset>(baseAddr, out var directStatic) &&
            reader.IsValidAddress(directStatic.GameState) &&
            reader.TryRead<byte>(directStatic.GameState, out _))
        {
            result.Status = ValidationStatus.VALID;
            result.ResolvedAddress = directStatic.GameState;
            result.ExtractedValue = $"DirectGameStateStaticObj (0x{directStatic.GameState.ToInt64():X})";
            result.Evidence.Add(new EvidenceRecord
            {
                RuleName = "DirectGameStateStaticMatch",
                Description = $"Static GameState pointer resolved at base: 0x{directStatic.GameState.ToInt64():X}",
                ScoreDelta = 50,
                Passed = true,
                IsIndependentValidator = true
            });
            return;
        }

        result.Status = ValidationStatus.BROKEN;
        result.ErrorMessage = $"Failed to locate or resolve static root pattern '{node.StaticPatternName}'.";
    }

    private static Dictionary<string, int>? FindGameStatesPattern(IProcessMemoryReader reader, IntPtr baseAddress, long size)
    {
        try
        {
            var patternDef = StaticOffsetsPatterns.Patterns.FirstOrDefault(p => p.Name == "Game States");
            if (string.IsNullOrEmpty(patternDef.Name)) return null;

            int scanSize = (int)Math.Min(size, 40_000_000); // 40MB max scan for Game States
            var buf = reader.ReadBytes(baseAddress, scanSize);
            if (buf == null) return null;

            var pData = patternDef.Data;
            var pMask = patternDef.Mask;
            var pLen = pData.Length;

            for (int i = 0; i <= buf.Length - pLen; i++)
            {
                bool match = true;
                for (int j = 0; j < pLen; j++)
                {
                    if (pMask[j] && buf[i + j] != pData[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    return new Dictionary<string, int>
                    {
                        ["Game States"] = i + patternDef.BytesToSkip
                    };
                }
            }
        }
        catch
        {
            // Ignore scan errors and fallback
        }

        return null;
    }

    private static void EvaluateNodeAtOffset(
        IProcessMemoryReader reader,
        IntPtr parentAddr,
        OffsetNode node,
        int offset,
        ValidationResult result,
        RecoveryContext context)
    {
        switch (node.Kind)
        {
            case ValueKind.PointerField:
            case ValueKind.RecordSlotField:
            {
                if (reader.TryRead<IntPtr>(parentAddr + offset, out var ptr) &&
                    ptr != IntPtr.Zero &&
                    reader.IsValidAddress(ptr) &&
                    reader.TryRead<byte>(ptr, out _))
                {
                    result.Status = ValidationStatus.VALID;
                    result.ResolvedAddress = ptr;
                    result.ExtractedValue = $"0x{ptr.ToInt64():X}";
                    result.Evidence.Add(new EvidenceRecord
                    {
                        RuleName = "ValidReadableAddress",
                        Description = $"Pointer 0x{ptr.ToInt64():X} at +0x{offset:X} points to valid readable memory",
                        ScoreDelta = 50,
                        Passed = true,
                        IsIndependentValidator = true
                    });
                }
                else
                {
                    result.Status = ValidationStatus.BROKEN;
                    result.ErrorMessage = $"Invalid, null, or unreadable pointer at +0x{offset:X}";
                }
                break;
            }

            case ValueKind.StdVectorField:
            {
                if (reader.TryRead<IntPtr>(parentAddr + offset, out var begin) &&
                    reader.TryRead<IntPtr>(parentAddr + offset + 8, out var end) &&
                    reader.TryRead<IntPtr>(parentAddr + offset + 16, out var cap))
                {
                    var bVal = (ulong)begin.ToInt64();
                    var eVal = (ulong)end.ToInt64();
                    var cVal = (ulong)cap.ToInt64();

                    if (bVal != 0 && eVal != 0 && bVal <= eVal && (cVal == 0 || eVal <= cVal) &&
                        reader.IsValidAddress(begin) &&
                        reader.TryRead<byte>(begin, out _))
                    {
                        var byteSize = (long)(eVal - bVal);
                        var count = byteSize / 8;

                        IntPtr target = begin;
                        if (count > 0 && reader.TryRead<IntPtr>(begin, out var firstElem) &&
                            firstElem != IntPtr.Zero &&
                            reader.IsValidAddress(firstElem) &&
                            reader.TryRead<byte>(firstElem, out _))
                        {
                            target = firstElem;
                        }

                        result.Status = ValidationStatus.VALID;
                        result.ResolvedAddress = target;
                        result.ExtractedValue = $"StdVector [Count={count}, Target=0x{target.ToInt64():X}]";
                        result.Evidence.Add(new EvidenceRecord
                        {
                            RuleName = "StdVectorValid",
                            Description = $"StdVector at +0x{offset:X} is valid with count {count}",
                            ScoreDelta = 50,
                            Passed = true,
                            IsIndependentValidator = true
                        });
                        break;
                    }
                }

                result.Status = ValidationStatus.BROKEN;
                result.ErrorMessage = $"Invalid StdVector structure at +0x{offset:X}";
                break;
            }

            case ValueKind.NumericField:
            {
                if (reader.TryRead<int>(parentAddr + offset, out var val))
                {
                    if (context.ExpectedGoldAmount.HasValue)
                    {
                        if (val == context.ExpectedGoldAmount.Value)
                        {
                            result.Status = ValidationStatus.VALID;
                            result.ResolvedAddress = parentAddr + offset;
                            result.ExtractedValue = val;
                            result.Evidence.Add(new EvidenceRecord
                            {
                                RuleName = "ExactExpectedValueMatch",
                                Description = $"Numeric value {val:N0} matches expected amount {context.ExpectedGoldAmount.Value:N0}",
                                ScoreDelta = 50,
                                Passed = true,
                                IsIndependentValidator = true
                            });
                            break;
                        }
                        else
                        {
                            result.Status = ValidationStatus.BROKEN;
                            result.ErrorMessage = $"Value at +0x{offset:X} ({val:N0}) does not match expected amount ({context.ExpectedGoldAmount.Value:N0})";
                            break;
                        }
                    }
                    else if (val >= 0 && val <= 2_000_000_000)
                    {
                        // Without explicit ground truth, plausible numeric range is not sufficient to declare VALID
                        result.Status = ValidationStatus.NEEDS_MANUAL_PROOF;
                        result.ResolvedAddress = parentAddr + offset;
                        result.ExtractedValue = val;
                        result.ErrorMessage = $"Plausible numeric value {val:N0} at +0x{offset:X} requires explicit verification (--gold <amount>).";
                        result.Evidence.Add(new EvidenceRecord
                        {
                            RuleName = "PlausibleNumericValue",
                            Description = $"Numeric value {val:N0} at +0x{offset:X} is within valid bounds [0..2,000,000,000] (unverified without --gold)",
                            ScoreDelta = 25,
                            Passed = true,
                            IsIndependentValidator = false
                        });
                        break;
                    }
                }

                result.Status = ValidationStatus.BROKEN;
                result.ErrorMessage = $"Invalid numeric value at +0x{offset:X}";
                break;
            }

            default:
                result.Status = ValidationStatus.UNSUPPORTED_RECOVERY;
                result.ErrorMessage = $"Unsupported ValueKind: {node.Kind}";
                break;
        }
    }
}
