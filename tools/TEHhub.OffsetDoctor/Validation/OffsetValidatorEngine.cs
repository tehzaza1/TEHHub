namespace TEHhub.OffsetDoctor.Validation;

using System.Text;
using TEHhub.OffsetDoctor.Evidence;
using TEHhub.OffsetDoctor.Manifest;
using TEHhub.OffsetDoctor.Process;
using TEHhub.OffsetDoctor.Strategies;
using TEHhub.Offsets;
using TEHhub.Offsets.Natives;
using TEHhub.Offsets.Objects;
using TEHhub.Offsets.Objects.Components;

public sealed class OffsetValidatorEngine
{
    public List<ValidationResult> ValidateChain(
        IProcessMemoryReader reader,
        List<OffsetNode> manifestNodes,
        RecoveryContext context,
        bool allowRecovery = false)
    {
        var results = new List<ValidationResult>();
        var resolvedAddresses = new Dictionary<string, IntPtr>();

        foreach (var node in manifestNodes)
        {
            var result = new ValidationResult
            {
                NodeId = node.Id,
                NodeDisplayName = node.DisplayName,
                Category = node.Category,
                ParentId = node.ParentId,
                ConfiguredOffset = node.DefaultOffset
            };

            // 1. Root / Pattern Node Validation
            if (node.ParentId == null)
            {
                ValidateStaticPatternNode(reader, node, result);
                if (result.Status == ValidationStatus.VALID)
                {
                    resolvedAddresses[node.Id] = result.ResolvedAddress;
                }
                results.Add(result);
                continue;
            }

            // 2. Parent Dependency Check
            if (!resolvedAddresses.TryGetValue(node.ParentId, out var parentAddr) ||
                parentAddr == IntPtr.Zero ||
                !reader.IsValidAddress(parentAddr) ||
                !reader.TryRead<byte>(parentAddr, out _))
            {
                var parentRes = results.FirstOrDefault(r => r.NodeId == node.ParentId);
                var parentStatusStr = parentRes?.Status.ToString() ?? "NOT FOUND";
                result.Status = ValidationStatus.BLOCKED;
                result.ErrorMessage = $"Blocked: Parent node '{node.ParentId}' is {parentStatusStr}.";
                result.Evidence.Add(new EvidenceRecord
                {
                    RuleName = "ParentDependencyCheck",
                    Description = $"Required parent '{node.ParentId}' is {parentStatusStr}",
                    Passed = false,
                    ScoreDelta = 0
                });
                results.Add(result);
                continue;
            }

            result.Evidence.Add(new EvidenceRecord
            {
                RuleName = "ParentValid",
                Description = $"Parent '{node.ParentId}' is VALID at 0x{parentAddr.ToInt64():X}",
                Passed = true,
                ScoreDelta = 10,
                IsIndependentValidator = true
            });

            // 3. Evaluate node at configured offset
            EvaluateNode(reader, parentAddr, node, result, context);

            if (result.Status == ValidationStatus.VALID || result.Status == ValidationStatus.UNVERIFIED)
            {
                resolvedAddresses[node.Id] = result.ResolvedAddress;
            }

            results.Add(result);
        }

        return results;
    }

    private static void ValidateStaticPatternNode(IProcessMemoryReader reader, OffsetNode node, ValidationResult result)
    {
        if (reader.MainModuleBase == IntPtr.Zero || reader.MainModuleSize <= 0)
        {
            result.Status = ValidationStatus.BROKEN;
            result.ErrorMessage = "Main module base address or size is invalid.";
            result.Evidence.Add(new EvidenceRecord
            {
                RuleName = "ModuleLoaded",
                Description = "Main module base or size is invalid",
                Passed = false
            });
            return;
        }

        var patternName = node.StaticPatternName ?? "Game States";
        var patternDef = StaticOffsetsPatterns.Patterns.FirstOrDefault(p => p.Name == patternName);

        if (string.IsNullOrEmpty(patternDef.Name))
        {
            result.Status = ValidationStatus.BROKEN;
            result.ErrorMessage = $"Static pattern '{patternName}' is not defined in StaticOffsetsPatterns.";
            return;
        }

        int scanSize = (int)Math.Min(reader.MainModuleSize > 0 ? reader.MainModuleSize : 40_000_000, 40_000_000);
        var buf = reader.ReadBytes(reader.MainModuleBase, scanSize);

        if (buf == null || buf.Length < patternDef.Data.Length)
        {
            result.Status = ValidationStatus.BROKEN;
            result.ErrorMessage = "Unable to read executable module memory for pattern scanning.";
            return;
        }

        var pData = patternDef.Data;
        var pMask = patternDef.Mask;
        var pLen = pData.Length;
        int matchOffset = -1;
        int matchCount = 0;

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
                matchCount++;
                if (matchOffset == -1)
                {
                    matchOffset = i;
                }
            }
        }

        if (matchCount == 0)
        {
            result.Status = ValidationStatus.BROKEN;
            result.ErrorMessage = $"Pattern '{patternName}' not found in executable code.";
            result.Evidence.Add(new EvidenceRecord
            {
                RuleName = "PatternFound",
                Description = $"Pattern '{patternName}' had 0 matches in code section",
                Passed = false
            });
            return;
        }

        if (matchCount > 1)
        {
            result.Status = ValidationStatus.UNVERIFIED;
            result.ErrorMessage = $"Pattern '{patternName}' matched multiple locations ({matchCount}). Uniqueness invariant violated.";
            result.Evidence.Add(new EvidenceRecord
            {
                RuleName = "PatternUnique",
                Description = $"Pattern '{patternName}' matched {matchCount} locations (expected 1)",
                Passed = false
            });
        }
        else
        {
            result.Evidence.Add(new EvidenceRecord
            {
                RuleName = "PatternUniqueMatch",
                Description = $"Pattern '{patternName}' matched uniquely at RVA +0x{matchOffset:X}",
                Passed = true,
                IsIndependentValidator = true
            });
        }

        // Calculate RIP displacement target
        int dispOffset = matchOffset + patternDef.BytesToSkip;
        if (dispOffset + 4 <= buf.Length)
        {
            int disp32 = BitConverter.ToInt32(buf, dispOffset);
            long rip = reader.MainModuleBase.ToInt64() + dispOffset + 4;
            long targetStaticAddr = rip + disp32;
            var targetPtr = new IntPtr(targetStaticAddr);

            if (reader.IsValidAddress(targetPtr) && reader.TryRead<byte>(targetPtr, out _))
            {
                result.Status = matchCount == 1 ? ValidationStatus.VALID : ValidationStatus.UNVERIFIED;
                result.ResolvedAddress = targetPtr;
                result.ExtractedValue = $"StaticPtr (0x{targetPtr.ToInt64():X})";
                result.Evidence.Add(new EvidenceRecord
                {
                    RuleName = "RipDisplacementReadable",
                    Description = $"Resolved static address 0x{targetPtr.ToInt64():X} points to valid readable memory",
                    Passed = true,
                    IsIndependentValidator = true
                });
                return;
            }
        }

        result.Status = ValidationStatus.BROKEN;
        result.ErrorMessage = $"Pattern '{patternName}' matched but displacement resolved to invalid address.";
    }

    private static void EvaluateNode(
        IProcessMemoryReader reader,
        IntPtr parentAddr,
        OffsetNode node,
        ValidationResult result,
        RecoveryContext context)
    {
        var targetAddr = parentAddr + node.DefaultOffset;

        if (node.ConservativeUnverifiedOnly)
        {
            // Specifically conservative nodes (e.g. WorldAreaMods unproven owner)
            result.Status = ValidationStatus.UNVERIFIED;
            result.ResolvedAddress = targetAddr;
            result.ErrorMessage = "Unproven native ownership in current PoE2 version. Reported conservatively as UNVERIFIED.";
            result.Evidence.Add(new EvidenceRecord
            {
                RuleName = "ConservativeUnverifiedPolicy",
                Description = "Marked UNVERIFIED by repository policy to avoid false positive assumptions",
                Passed = true
            });
            return;
        }

        switch (node.Kind)
        {
            case ValueKind.PointerField:
            case ValueKind.RecordSlotField:
            {
                if (reader.TryRead<IntPtr>(targetAddr, out var ptr) &&
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
                        Description = $"Pointer 0x{ptr.ToInt64():X} at +0x{node.DefaultOffset:X} points to valid readable memory",
                        Passed = true,
                        IsIndependentValidator = true
                    });
                }
                else
                {
                    result.Status = ValidationStatus.BROKEN;
                    result.ErrorMessage = $"Invalid, null, or unreadable pointer at +0x{node.DefaultOffset:X}";
                    result.Evidence.Add(new EvidenceRecord
                    {
                        RuleName = "PointerReadable",
                        Description = $"Pointer at +0x{node.DefaultOffset:X} is null or unreadable",
                        Passed = false
                    });
                }
                break;
            }

            case ValueKind.StdVectorField:
            {
                if (reader.TryRead<IntPtr>(targetAddr, out var begin) &&
                    reader.TryRead<IntPtr>(targetAddr + 8, out var end) &&
                    reader.TryRead<IntPtr>(targetAddr + 16, out var cap))
                {
                    var bVal = (ulong)begin.ToInt64();
                    var eVal = (ulong)end.ToInt64();
                    var cVal = (ulong)cap.ToInt64();

                    bool validNull = bVal == 0 && eVal == 0 && cVal == 0;
                    bool validRange = bVal != 0 && eVal != 0 && bVal <= eVal && (cVal == 0 || eVal <= cVal);

                    if (validNull)
                    {
                        result.Status = ValidationStatus.VALID;
                        result.ResolvedAddress = targetAddr;
                        result.ExtractedValue = "StdVector [Empty, Count=0]";
                        result.Evidence.Add(new EvidenceRecord
                        {
                            RuleName = "StdVectorEmptyValid",
                            Description = "StdVector is cleanly initialized and empty (0 elements)",
                            Passed = true,
                            IsIndependentValidator = true
                        });
                        break;
                    }

                    if (validRange && reader.IsValidAddress(begin) && reader.TryRead<byte>(begin, out _))
                    {
                        var byteSize = (long)(eVal - bVal);
                        var count = byteSize / 8;

                        IntPtr firstElem = begin;
                        if (count > 0 && reader.TryRead<IntPtr>(begin, out var elemPtr) && elemPtr != IntPtr.Zero && reader.IsValidAddress(elemPtr))
                        {
                            firstElem = elemPtr;
                        }

                        result.Status = ValidationStatus.VALID;
                        result.ResolvedAddress = firstElem;
                        result.ExtractedValue = $"StdVector [Count={count}, Target=0x{firstElem.ToInt64():X}]";
                        result.Evidence.Add(new EvidenceRecord
                        {
                            RuleName = "StdVectorStructureValid",
                            Description = $"StdVector at +0x{node.DefaultOffset:X} is valid (Count={count})",
                            Passed = true,
                            IsIndependentValidator = true
                        });
                        break;
                    }
                }

                result.Status = ValidationStatus.BROKEN;
                result.ErrorMessage = $"Invalid StdVector structure at +0x{node.DefaultOffset:X}";
                result.Evidence.Add(new EvidenceRecord
                {
                    RuleName = "StdVectorStructureValid",
                    Description = "StdVector pointers violate begin <= end <= capacity invariant or point to unmapped memory",
                    Passed = false
                });
                break;
            }

            case ValueKind.StdMapField:
            {
                if (reader.TryRead<IntPtr>(targetAddr, out var head) &&
                    reader.TryRead<int>(targetAddr + 8, out var size))
                {
                    if (size >= 0 && size < 100_000)
                    {
                        if (size == 0 || (head != IntPtr.Zero && reader.IsValidAddress(head) && reader.TryRead<byte>(head, out _)))
                        {
                            result.Status = ValidationStatus.VALID;
                            result.ResolvedAddress = targetAddr;
                            result.ExtractedValue = $"StdMap [Size={size}, Head=0x{head.ToInt64():X}]";
                            result.Evidence.Add(new EvidenceRecord
                            {
                                RuleName = "StdMapValid",
                                Description = $"StdMap at +0x{node.DefaultOffset:X} is valid with size {size}",
                                Passed = true,
                                IsIndependentValidator = true
                            });
                            break;
                        }
                    }
                }

                result.Status = ValidationStatus.BROKEN;
                result.ErrorMessage = $"Invalid StdMap structure at +0x{node.DefaultOffset:X}";
                break;
            }

            case ValueKind.StdWStringField:
            {
                if (reader.TryRead<IntPtr>(targetAddr, out var bufPtr) &&
                    reader.TryRead<int>(targetAddr + 8, out var len) &&
                    reader.TryRead<int>(targetAddr + 16, out var cap))
                {
                    if (len >= 0 && len <= 2048 && cap >= len)
                    {
                        var readAddr = (cap >= 8 && bufPtr != IntPtr.Zero && reader.IsValidAddress(bufPtr)) ? bufPtr : targetAddr;
                        if (reader.TryReadBytes(readAddr, stackalloc byte[Math.Min(len * 2, 64)]))
                        {
                            result.Status = ValidationStatus.VALID;
                            result.ResolvedAddress = targetAddr;
                            result.ExtractedValue = $"StdWString [Length={len}]";
                            result.Evidence.Add(new EvidenceRecord
                            {
                                RuleName = "StdWStringValid",
                                Description = $"StdWString at +0x{node.DefaultOffset:X} is valid (Length={len}, Cap={cap})",
                                Passed = true,
                                IsIndependentValidator = true
                            });
                            break;
                        }
                    }
                }

                result.Status = ValidationStatus.BROKEN;
                result.ErrorMessage = $"Invalid StdWString structure at +0x{node.DefaultOffset:X}";
                break;
            }

            case ValueKind.VitalStructField:
            {
                if (reader.TryRead<VitalStruct>(targetAddr, out var vital))
                {
                    bool vtableValid = vital.VtablePtr != IntPtr.Zero && reader.IsValidAddress(vital.VtablePtr) && reader.TryRead<byte>(vital.VtablePtr, out _);
                    bool totalSane = vital.Total >= 0 && vital.Total <= 500_000;
                    bool currentSane = vital.Current >= 0 && vital.Current <= vital.Total + 50000;

                    if (vtableValid && totalSane && currentSane)
                    {
                        result.Status = ValidationStatus.VALID;
                        result.ResolvedAddress = targetAddr;
                        result.ExtractedValue = $"VitalStruct [Total={vital.Total}, Current={vital.Current}]";
                        result.Evidence.Add(new EvidenceRecord
                        {
                            RuleName = "VitalStructValid",
                            Description = $"VitalStruct at +0x{node.DefaultOffset:X} verified (Total={vital.Total}, Current={vital.Current})",
                            Passed = true,
                            IsIndependentValidator = true
                        });
                        break;
                    }
                }

                result.Status = ValidationStatus.BROKEN;
                result.ErrorMessage = $"Invalid VitalStruct at +0x{node.DefaultOffset:X}";
                break;
            }

            case ValueKind.ComponentLookup:
            {
                var compName = node.ComponentName ?? "Component";
                IntPtr resolvedComp = IntPtr.Zero;

                if (reader.TryRead<ComponentHeader>(parentAddr, out var directHeader) &&
                    directHeader.StaticPtr != IntPtr.Zero &&
                    reader.IsValidAddress(directHeader.StaticPtr) &&
                    reader.TryRead<byte>(directHeader.StaticPtr, out _))
                {
                    resolvedComp = parentAddr;
                }
                else if (reader.TryRead<IntPtr>(parentAddr, out var compPtr) &&
                         compPtr != IntPtr.Zero &&
                         reader.IsValidAddress(compPtr) &&
                         reader.TryRead<ComponentHeader>(compPtr, out var indHeader) &&
                         indHeader.StaticPtr != IntPtr.Zero &&
                         reader.IsValidAddress(indHeader.StaticPtr) &&
                         reader.TryRead<byte>(indHeader.StaticPtr, out _))
                {
                    resolvedComp = compPtr;
                }

                if (resolvedComp != IntPtr.Zero)
                {
                    result.Status = ValidationStatus.VALID;
                    result.ResolvedAddress = resolvedComp;
                    result.ExtractedValue = $"{compName}Component (0x{resolvedComp.ToInt64():X})";
                    result.Evidence.Add(new EvidenceRecord
                    {
                        RuleName = "ComponentHeaderValid",
                        Description = $"{compName} component at 0x{resolvedComp.ToInt64():X} has valid static header pointer",
                        Passed = true,
                        IsIndependentValidator = true
                    });
                    break;
                }

                result.Status = ValidationStatus.BROKEN;
                result.ErrorMessage = $"Failed to resolve {compName} component.";
                break;
            }

            case ValueKind.NumericField:
            {
                if (reader.TryRead<int>(targetAddr, out var val))
                {
                    if (node.Id == "psd_gold_field")
                    {
                        if (context.ExpectedGoldAmount.HasValue)
                        {
                            if (val == context.ExpectedGoldAmount.Value)
                            {
                                result.Status = ValidationStatus.VALID;
                                result.ResolvedAddress = targetAddr;
                                result.ExtractedValue = val;
                                result.Evidence.Add(new EvidenceRecord
                                {
                                    RuleName = "ExactExpectedValueMatch",
                                    Description = $"Numeric value {val:N0} matches expected amount {context.ExpectedGoldAmount.Value:N0}",
                                    Passed = true,
                                    IsIndependentValidator = true
                                });
                                break;
                            }
                            else
                            {
                                result.Status = ValidationStatus.BROKEN;
                                result.ErrorMessage = $"Value at +0x{node.DefaultOffset:X} ({val:N0}) does not match expected amount ({context.ExpectedGoldAmount.Value:N0})";
                                break;
                            }
                        }
                        else if (val >= 0 && val <= 2_000_000_000)
                        {
                            result.Status = ValidationStatus.UNVERIFIED;
                            result.ResolvedAddress = targetAddr;
                            result.ExtractedValue = val;
                            result.ErrorMessage = $"Plausible numeric value {val:N0} requires external ground truth (--gold <amount>).";
                            result.Evidence.Add(new EvidenceRecord
                            {
                                RuleName = "PlausibleNumericValue",
                                Description = $"Numeric value {val:N0} is within valid bounds [0..2B] (unverified without --gold)",
                                Passed = true
                            });
                            break;
                        }
                    }
                    else
                    {
                        // Standard numeric fields (Level, Hash, Time, AnimationId, etc.)
                        result.Status = ValidationStatus.VALID;
                        result.ResolvedAddress = targetAddr;
                        result.ExtractedValue = val;
                        result.Evidence.Add(new EvidenceRecord
                        {
                            RuleName = "NumericFieldReadable",
                            Description = $"Numeric value {val} at +0x{node.DefaultOffset:X} is valid and readable",
                            Passed = true,
                            IsIndependentValidator = true
                        });
                        break;
                    }
                }

                result.Status = ValidationStatus.BROKEN;
                result.ErrorMessage = $"Invalid numeric value at +0x{node.DefaultOffset:X}";
                break;
            }

            case ValueKind.StructField:
            {
                if (reader.TryRead<byte>(targetAddr, out _))
                {
                    result.Status = ValidationStatus.VALID;
                    result.ResolvedAddress = targetAddr;
                    result.ExtractedValue = $"Struct at +0x{node.DefaultOffset:X}";
                    result.Evidence.Add(new EvidenceRecord
                    {
                        RuleName = "StructMemoryReadable",
                        Description = $"Struct memory at +0x{node.DefaultOffset:X} is valid and readable",
                        Passed = true,
                        IsIndependentValidator = true
                    });
                    break;
                }

                result.Status = ValidationStatus.BROKEN;
                result.ErrorMessage = $"Unreadable struct memory at +0x{node.DefaultOffset:X}";
                break;
            }

            default:
                result.Status = ValidationStatus.UNVERIFIED;
                result.ErrorMessage = $"Unsupported ValueKind: {node.Kind}";
                break;
        }
    }
}
