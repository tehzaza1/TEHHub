namespace TEHhub.OffsetDoctor.Validation;

using System.Runtime.InteropServices;
using System.Text;
using TEHhub.OffsetDoctor.Evidence;
using TEHhub.OffsetDoctor.Manifest;
using TEHhub.OffsetDoctor.Process;
using TEHhub.OffsetDoctor.Strategies;
using TEHhub.Offsets;
using TEHhub.Offsets.Natives;
using TEHhub.Offsets.Objects;
using TEHhub.Offsets.Objects.Components;
using TEHhub.Offsets.Objects.States.InGameState;

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
                if (result.Status == ValidationStatus.VALID || result.Status == ValidationStatus.UNVERIFIED)
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
                Description = $"Parent '{node.ParentId}' is resolved at 0x{parentAddr.ToInt64():X}",
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

        // Calculate RIP displacement target: baseAddress + matchOffset + BytesToSkip + disp32 + 4
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
            case ValueKind.ComponentLookup:
            {
                ValidateComponentLookup(reader, parentAddr, node, result);
                break;
            }

            case ValueKind.PointerField:
            case ValueKind.RecordSlotField:
            {
                if (reader.TryRead<IntPtr>(targetAddr, out var ptr))
                {
                    if (ptr == IntPtr.Zero)
                    {
                        if (node.IsOptionalStateDependent)
                        {
                            result.Status = ValidationStatus.UNVERIFIED;
                            result.ResolvedAddress = IntPtr.Zero;
                            result.ErrorMessage = "Optional / state-dependent pointer is null in current runtime state.";
                            result.Evidence.Add(new EvidenceRecord
                            {
                                RuleName = "OptionalPointerNullState",
                                Description = "Pointer is null (legitimately inactive in current state)",
                                Passed = true
                            });
                            break;
                        }
                        else
                        {
                            result.Status = ValidationStatus.BROKEN;
                            result.ErrorMessage = $"Null pointer at +0x{node.DefaultOffset:X}";
                            result.Evidence.Add(new EvidenceRecord
                            {
                                RuleName = "PointerNotNull",
                                Description = $"Pointer at +0x{node.DefaultOffset:X} is null",
                                Passed = false
                            });
                            break;
                        }
                    }

                    if (reader.IsValidAddress(ptr) && reader.TryRead<byte>(ptr, out _))
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
                        break;
                    }
                }

                result.Status = ValidationStatus.BROKEN;
                result.ErrorMessage = $"Invalid, unmapped, or unreadable pointer at +0x{node.DefaultOffset:X}";
                result.Evidence.Add(new EvidenceRecord
                {
                    RuleName = "PointerReadable",
                    Description = $"Pointer at +0x{node.DefaultOffset:X} is unreadable or unmapped",
                    Passed = false
                });
                break;
            }

            case ValueKind.StdVectorField:
            {
                ValidateStdVector(reader, targetAddr, node, result);
                break;
            }

            case ValueKind.StdMapField:
            {
                ValidateStdMap(reader, targetAddr, node, result);
                break;
            }

            case ValueKind.StdWStringField:
            {
                ValidateStdWString(reader, targetAddr, node, result);
                break;
            }

            case ValueKind.VitalStructField:
            {
                ValidateVitalStruct(reader, targetAddr, node, result, context);
                break;
            }

            case ValueKind.NumericField:
            {
                ValidateNumericField(reader, targetAddr, node, result, context);
                break;
            }

            case ValueKind.StructField:
            {
                ValidateStructField(reader, targetAddr, node, result);
                break;
            }

            default:
                result.Status = ValidationStatus.UNVERIFIED;
                result.ErrorMessage = $"Unsupported ValueKind: {node.Kind}";
                break;
        }
    }

    private static void ValidateComponentLookup(
        IProcessMemoryReader reader,
        IntPtr entityAddr,
        OffsetNode node,
        ValidationResult result)
    {
        var compName = node.ComponentName ?? "Component";

        // Read ItemStruct at entityAddr (+0x00)
        if (!reader.TryRead<ItemStruct>(entityAddr, out var itemStruct) ||
            itemStruct.EntityDetailsPtr == IntPtr.Zero ||
            !reader.IsValidAddress(itemStruct.EntityDetailsPtr))
        {
            result.Status = ValidationStatus.BROKEN;
            result.ErrorMessage = $"Unable to read valid ItemStruct / EntityDetailsPtr on Entity at 0x{entityAddr.ToInt64():X}";
            return;
        }

        // Read EntityDetails
        if (!reader.TryRead<EntityDetails>(itemStruct.EntityDetailsPtr, out var entityDetails) ||
            entityDetails.ComponentLookUpPtr == IntPtr.Zero ||
            !reader.IsValidAddress(entityDetails.ComponentLookUpPtr))
        {
            result.Status = ValidationStatus.BROKEN;
            result.ErrorMessage = $"Unable to read EntityDetails or ComponentLookUpPtr at 0x{itemStruct.EntityDetailsPtr.ToInt64():X}";
            return;
        }

        // Read ComponentLookUpStruct
        if (!reader.TryRead<ComponentLookUpStruct>(entityDetails.ComponentLookUpPtr, out var lookupStruct))
        {
            result.Status = ValidationStatus.BROKEN;
            result.ErrorMessage = $"Unable to read ComponentLookUpStruct at 0x{entityDetails.ComponentLookUpPtr.ToInt64():X}";
            return;
        }

        var bucketVector = lookupStruct.ComponentsNameAndIndex.Data;
        int elemSize = System.Runtime.CompilerServices.Unsafe.SizeOf<ComponentNameAndIndexStruct>();
        long bucketByteLen = bucketVector.Last.ToInt64() - bucketVector.First.ToInt64();

        if (bucketByteLen < 0 || bucketByteLen % elemSize != 0 || bucketByteLen > 100_000)
        {
            result.Status = ValidationStatus.BROKEN;
            result.ErrorMessage = $"Invalid ComponentLookUp bucket vector dimensions ({bucketByteLen} bytes)";
            return;
        }

        int entryCount = (int)(bucketByteLen / elemSize);
        if (entryCount == 0)
        {
            result.Status = ValidationStatus.UNVERIFIED;
            result.ErrorMessage = "Entity component lookup table is empty.";
            return;
        }

        int targetIndex = -1;
        for (int i = 0; i < entryCount; i++)
        {
            var entryAddr = bucketVector.First + (i * elemSize);
            if (reader.TryRead<ComponentNameAndIndexStruct>(entryAddr, out var entry) &&
                entry.NamePtr != IntPtr.Zero &&
                reader.IsValidAddress(entry.NamePtr))
            {
                var nameBytes = reader.ReadBytes(entry.NamePtr, 64);
                if (nameBytes != null)
                {
                    int nullIdx = Array.IndexOf(nameBytes, (byte)0);
                    var entryName = Encoding.ASCII.GetString(nameBytes, 0, nullIdx >= 0 ? nullIdx : nameBytes.Length);
                    if (string.Equals(entryName, compName, StringComparison.OrdinalIgnoreCase))
                    {
                        targetIndex = entry.Index;
                        break;
                    }
                }
            }
        }

        if (targetIndex < 0)
        {
            result.Status = ValidationStatus.BROKEN;
            result.ErrorMessage = $"Component '{compName}' not found in entity component lookup dictionary ({entryCount} entries scanned).";
            return;
        }

        // Read Component from ItemStruct.ComponentListPtr
        long compListByteLen = itemStruct.ComponentListPtr.Last.ToInt64() - itemStruct.ComponentListPtr.First.ToInt64();
        int compListCount = (int)(compListByteLen / 8);

        if (targetIndex >= compListCount || targetIndex < 0)
        {
            result.Status = ValidationStatus.BROKEN;
            result.ErrorMessage = $"Component '{compName}' index {targetIndex} out of range for ComponentList (count {compListCount}).";
            return;
        }

        var compSlotAddr = itemStruct.ComponentListPtr.First + (targetIndex * 8);
        if (!reader.TryRead<IntPtr>(compSlotAddr, out var compAddr) ||
            compAddr == IntPtr.Zero ||
            !reader.IsValidAddress(compAddr) ||
            !reader.TryRead<ComponentHeader>(compAddr, out var header) ||
            header.StaticPtr == IntPtr.Zero ||
            !reader.IsValidAddress(header.StaticPtr) ||
            !reader.TryRead<byte>(header.StaticPtr, out _))
        {
            result.Status = ValidationStatus.BROKEN;
            result.ErrorMessage = $"Component '{compName}' resolved to invalid address or invalid ComponentHeader.";
            return;
        }

        result.Status = ValidationStatus.VALID;
        result.ResolvedAddress = compAddr;
        result.ExtractedValue = $"{compName}Component (0x{compAddr.ToInt64():X})";
        result.Evidence.Add(new EvidenceRecord
        {
            RuleName = "ComponentResolvedFromLookup",
            Description = $"Component '{compName}' resolved via lookup table (Index={targetIndex}, Addr=0x{compAddr.ToInt64():X}, Header=0x{header.StaticPtr.ToInt64():X})",
            Passed = true,
            IsIndependentValidator = true
        });
    }

    private static void ValidateStdVector(
        IProcessMemoryReader reader,
        IntPtr targetAddr,
        OffsetNode node,
        ValidationResult result)
    {
        if (reader.TryRead<StdVector>(targetAddr, out var vec))
        {
            var bVal = (ulong)vec.First.ToInt64();
            var eVal = (ulong)vec.Last.ToInt64();
            var cVal = (ulong)vec.End.ToInt64();

            bool validNull = bVal == 0 && eVal == 0;
            bool validRange = bVal != 0 && eVal != 0 && bVal <= eVal && (cVal == 0 || eVal <= cVal);

            if (validNull)
            {
                result.Status = ValidationStatus.UNVERIFIED;
                result.ResolvedAddress = targetAddr;
                result.ExtractedValue = "StdVector [Empty, Count=0]";
                result.ErrorMessage = "StdVector is cleanly initialized but empty (0 elements); unproven without active items.";
                result.Evidence.Add(new EvidenceRecord
                {
                    RuleName = "StdVectorEmptyUnproven",
                    Description = "StdVector is empty (0 elements); requires active elements for full semantic proof",
                    Passed = true
                });
                return;
            }

            if (validRange && reader.IsValidAddress(vec.First) && reader.TryRead<byte>(vec.First, out _))
            {
                int elemSize = node.VectorElementSize > 0 ? node.VectorElementSize : 8;
                var byteSize = (long)(eVal - bVal);

                if (byteSize % elemSize == 0 && byteSize <= 50_000_000)
                {
                    var count = byteSize / elemSize;
                    IntPtr resolvedTarget = vec.First;

                    if (count > 0 && node.VectorIsPointerElements)
                    {
                        if (reader.TryRead<IntPtr>(vec.First, out var firstPtr) &&
                            firstPtr != IntPtr.Zero &&
                            reader.IsValidAddress(firstPtr) &&
                            reader.TryRead<byte>(firstPtr, out _))
                        {
                            resolvedTarget = firstPtr;
                        }
                    }

                    result.Status = ValidationStatus.VALID;
                    result.ResolvedAddress = resolvedTarget;
                    result.ExtractedValue = $"StdVector [Count={count}, Target=0x{resolvedTarget.ToInt64():X}]";
                    result.Evidence.Add(new EvidenceRecord
                    {
                        RuleName = "StdVectorStructureValid",
                        Description = $"StdVector at +0x{node.DefaultOffset:X} is valid (Count={count}, ElemSize={elemSize})",
                        Passed = true,
                        IsIndependentValidator = true
                    });
                    return;
                }
            }
        }

        result.Status = ValidationStatus.BROKEN;
        result.ErrorMessage = $"Invalid StdVector structure at +0x{node.DefaultOffset:X}";
        result.Evidence.Add(new EvidenceRecord
        {
            RuleName = "StdVectorStructureValid",
            Description = "StdVector pointers violate begin <= end <= capacity invariant or unaligned element size",
            Passed = false
        });
    }

    private static void ValidateStdMap(
        IProcessMemoryReader reader,
        IntPtr targetAddr,
        OffsetNode node,
        ValidationResult result)
    {
        if (reader.TryRead<IntPtr>(targetAddr, out var head) &&
            reader.TryRead<int>(targetAddr + 8, out var size))
        {
            if (size >= 0 && size < 100_000)
            {
                if (size == 0)
                {
                    result.Status = ValidationStatus.UNVERIFIED;
                    result.ResolvedAddress = targetAddr;
                    result.ExtractedValue = "StdMap [Size=0]";
                    result.ErrorMessage = "StdMap size is 0; unproven without active map elements.";
                    result.Evidence.Add(new EvidenceRecord
                    {
                        RuleName = "StdMapEmptyUnproven",
                        Description = "StdMap is empty (Size=0); requires active elements for full semantic proof",
                        Passed = true
                    });
                    return;
                }

                if (head != IntPtr.Zero && reader.IsValidAddress(head) && reader.TryRead<byte>(head, out _))
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
                    return;
                }
            }
        }

        result.Status = ValidationStatus.BROKEN;
        result.ErrorMessage = $"Invalid StdMap structure at +0x{node.DefaultOffset:X}";
    }

    private static void ValidateStdWString(
        IProcessMemoryReader reader,
        IntPtr targetAddr,
        OffsetNode node,
        ValidationResult result)
    {
        if (reader.TryRead<StdWString>(targetAddr, out var wstr))
        {
            if (wstr.Length >= 0 && wstr.Length <= 1000 && wstr.Capacity >= wstr.Length)
            {
                if (wstr.Length == 0)
                {
                    result.Status = ValidationStatus.UNVERIFIED;
                    result.ResolvedAddress = targetAddr;
                    result.ExtractedValue = "\"\" (Length=0)";
                    result.ErrorMessage = "StdWString is empty (Length=0).";
                    return;
                }

                string strVal = string.Empty;
                if (wstr.Capacity <= 8)
                {
                    // Inline SSO buffer (16 bytes in Buffer and ReservedBytes)
                    var buf = new byte[16];
                    BitConverter.GetBytes(wstr.Buffer.ToInt64()).CopyTo(buf, 0);
                    BitConverter.GetBytes(wstr.ReservedBytes.ToInt64()).CopyTo(buf, 8);
                    int byteLen = Math.Min(wstr.Length * 2, 16);
                    strVal = Encoding.Unicode.GetString(buf, 0, byteLen);
                }
                else if (wstr.Buffer != IntPtr.Zero && reader.IsValidAddress(wstr.Buffer))
                {
                    // Heap buffer
                    int byteLen = wstr.Length * 2;
                    var heapBytes = reader.ReadBytes(wstr.Buffer, byteLen);
                    if (heapBytes != null)
                    {
                        strVal = Encoding.Unicode.GetString(heapBytes);
                    }
                }

                if (!string.IsNullOrEmpty(strVal))
                {
                    result.Status = ValidationStatus.VALID;
                    result.ResolvedAddress = targetAddr;
                    result.ExtractedValue = $"\"{strVal}\"";
                    result.Evidence.Add(new EvidenceRecord
                    {
                        RuleName = "StdWStringValid",
                        Description = $"StdWString at +0x{node.DefaultOffset:X} verified (Length={wstr.Length}, Cap={wstr.Capacity})",
                        Passed = true,
                        IsIndependentValidator = true
                    });
                    return;
                }
            }
        }

        result.Status = ValidationStatus.BROKEN;
        result.ErrorMessage = $"Invalid StdWString structure at +0x{node.DefaultOffset:X}";
    }

    private static void ValidateVitalStruct(
        IProcessMemoryReader reader,
        IntPtr targetAddr,
        OffsetNode node,
        ValidationResult result,
        RecoveryContext context)
    {
        if (reader.TryRead<VitalStruct>(targetAddr, out var vital))
        {
            bool vtableValid = vital.VtablePtr != IntPtr.Zero && reader.IsValidAddress(vital.VtablePtr) && reader.TryRead<byte>(vital.VtablePtr, out _);
            bool totalSane = vital.Total >= 0 && vital.Total <= 500_000;
            bool currentSane = vital.Current >= 0 && vital.Current <= vital.Total + 50_000;

            if (vtableValid && totalSane && currentSane)
            {
                // Verify ground truth if provided
                if (node.Id == "comp_life_health")
                {
                    if (context.ExpectedHpCurrent.HasValue && vital.Current != context.ExpectedHpCurrent.Value)
                    {
                        result.Status = ValidationStatus.BROKEN;
                        result.ErrorMessage = $"Health Current ({vital.Current}) does not match expected ({context.ExpectedHpCurrent.Value}).";
                        return;
                    }
                    if (context.ExpectedHpTotal.HasValue && vital.Total != context.ExpectedHpTotal.Value)
                    {
                        result.Status = ValidationStatus.BROKEN;
                        result.ErrorMessage = $"Health Total ({vital.Total}) does not match expected ({context.ExpectedHpTotal.Value}).";
                        return;
                    }
                }
                else if (node.Id == "comp_life_mana")
                {
                    if (context.ExpectedMpCurrent.HasValue && vital.Current != context.ExpectedMpCurrent.Value)
                    {
                        result.Status = ValidationStatus.BROKEN;
                        result.ErrorMessage = $"Mana Current ({vital.Current}) does not match expected ({context.ExpectedMpCurrent.Value}).";
                        return;
                    }
                    if (context.ExpectedMpTotal.HasValue && vital.Total != context.ExpectedMpTotal.Value)
                    {
                        result.Status = ValidationStatus.BROKEN;
                        result.ErrorMessage = $"Mana Total ({vital.Total}) does not match expected ({context.ExpectedMpTotal.Value}).";
                        return;
                    }
                }
                else if (node.Id == "comp_life_es")
                {
                    if (context.ExpectedEsCurrent.HasValue && vital.Current != context.ExpectedEsCurrent.Value)
                    {
                        result.Status = ValidationStatus.BROKEN;
                        result.ErrorMessage = $"ES Current ({vital.Current}) does not match expected ({context.ExpectedEsCurrent.Value}).";
                        return;
                    }
                    if (context.ExpectedEsTotal.HasValue && vital.Total != context.ExpectedEsTotal.Value)
                    {
                        result.Status = ValidationStatus.BROKEN;
                        result.ErrorMessage = $"ES Total ({vital.Total}) does not match expected ({context.ExpectedEsTotal.Value}).";
                        return;
                    }
                }

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
                return;
            }
        }

        result.Status = ValidationStatus.BROKEN;
        result.ErrorMessage = $"Invalid VitalStruct at +0x{node.DefaultOffset:X}";
    }

    private static void ValidateNumericField(
        IProcessMemoryReader reader,
        IntPtr targetAddr,
        OffsetNode node,
        ValidationResult result,
        RecoveryContext context)
    {
        double numVal = 0;
        bool readSuccess = false;

        switch (node.ScalarType)
        {
            case ScalarType.Byte:
                if (reader.TryRead<byte>(targetAddr, out var bVal)) { numVal = bVal; readSuccess = true; }
                break;
            case ScalarType.UShort:
                if (reader.TryRead<ushort>(targetAddr, out var usVal)) { numVal = usVal; readSuccess = true; }
                break;
            case ScalarType.UInt:
                if (reader.TryRead<uint>(targetAddr, out var uiVal)) { numVal = uiVal; readSuccess = true; }
                break;
            case ScalarType.Float:
                if (reader.TryRead<float>(targetAddr, out var fVal) && !float.IsNaN(fVal) && !float.IsInfinity(fVal)) { numVal = fVal; readSuccess = true; }
                break;
            case ScalarType.Double:
                if (reader.TryRead<double>(targetAddr, out var dVal) && !double.IsNaN(dVal) && !double.IsInfinity(dVal)) { numVal = dVal; readSuccess = true; }
                break;
            case ScalarType.Int:
            default:
                if (reader.TryRead<int>(targetAddr, out var iVal)) { numVal = iVal; readSuccess = true; }
                break;
        }

        if (!readSuccess)
        {
            result.Status = ValidationStatus.BROKEN;
            result.ErrorMessage = $"Unable to read {node.ScalarType} at +0x{node.DefaultOffset:X}";
            return;
        }

        if (node.Id == "psd_gold_field")
        {
            int goldVal = (int)numVal;
            if (context.ExpectedGoldAmount.HasValue)
            {
                if (goldVal == context.ExpectedGoldAmount.Value)
                {
                    result.Status = ValidationStatus.VALID;
                    result.ResolvedAddress = targetAddr;
                    result.ExtractedValue = goldVal;
                    result.Evidence.Add(new EvidenceRecord
                    {
                        RuleName = "ExactExpectedValueMatch",
                        Description = $"Numeric value {goldVal:N0} matches expected amount {context.ExpectedGoldAmount.Value:N0}",
                        Passed = true,
                        IsIndependentValidator = true
                    });
                    return;
                }
                else
                {
                    result.Status = ValidationStatus.BROKEN;
                    result.ErrorMessage = $"Value at +0x{node.DefaultOffset:X} ({goldVal:N0}) does not match expected amount ({context.ExpectedGoldAmount.Value:N0})";
                    return;
                }
            }
            else if (goldVal >= 0 && goldVal <= 2_000_000_000)
            {
                result.Status = ValidationStatus.UNVERIFIED;
                result.ResolvedAddress = targetAddr;
                result.ExtractedValue = goldVal;
                result.ErrorMessage = $"Plausible numeric value {goldVal:N0} requires external ground truth (--gold <amount>).";
                result.Evidence.Add(new EvidenceRecord
                {
                    RuleName = "PlausibleNumericValue",
                    Description = $"Numeric value {goldVal:N0} is within valid bounds [0..2B] (unverified without --gold)",
                    Passed = true
                });
                return;
            }
        }

        if (node.ExpectedMinNumeric.HasValue && numVal < node.ExpectedMinNumeric.Value)
        {
            result.Status = ValidationStatus.BROKEN;
            result.ErrorMessage = $"Value {numVal} is below expected minimum ({node.ExpectedMinNumeric.Value})";
            return;
        }

        if (node.ExpectedMaxNumeric.HasValue && numVal > node.ExpectedMaxNumeric.Value)
        {
            result.Status = ValidationStatus.BROKEN;
            result.ErrorMessage = $"Value {numVal} is above expected maximum ({node.ExpectedMaxNumeric.Value})";
            return;
        }

        result.Status = ValidationStatus.VALID;
        result.ResolvedAddress = targetAddr;
        result.ExtractedValue = numVal;
        result.Evidence.Add(new EvidenceRecord
        {
            RuleName = "NumericFieldDomainValid",
            Description = $"Numeric {node.ScalarType} value {numVal} at +0x{node.DefaultOffset:X} satisfies domain bounds",
            Passed = true,
            IsIndependentValidator = true
        });
    }

    private static void ValidateStructField(
        IProcessMemoryReader reader,
        IntPtr targetAddr,
        OffsetNode node,
        ValidationResult result)
    {
        if (node.Id == "comp_render_world_pos")
        {
            if (reader.TryRead<StdTuple3D<float>>(targetAddr, out var pos))
            {
                bool validFloats = !float.IsNaN(pos.X) && !float.IsInfinity(pos.X) &&
                                  !float.IsNaN(pos.Y) && !float.IsInfinity(pos.Y) &&
                                  !float.IsNaN(pos.Z) && !float.IsInfinity(pos.Z);
                bool validBounds = Math.Abs(pos.X) < 100_000 && Math.Abs(pos.Y) < 100_000 && Math.Abs(pos.Z) < 100_000;

                if (validFloats && validBounds)
                {
                    result.Status = ValidationStatus.VALID;
                    result.ResolvedAddress = targetAddr;
                    result.ExtractedValue = $"Pos({pos.X:F1}, {pos.Y:F1}, {pos.Z:F1})";
                    result.Evidence.Add(new EvidenceRecord
                    {
                        RuleName = "WorldPositionValid",
                        Description = $"Render world position finite floats within bounds ({pos.X:F1}, {pos.Y:F1}, {pos.Z:F1})",
                        Passed = true,
                        IsIndependentValidator = true
                    });
                    return;
                }
            }

            result.Status = ValidationStatus.BROKEN;
            result.ErrorMessage = $"Invalid WorldPosition floats at +0x{node.DefaultOffset:X}";
            return;
        }

        if (node.Id == "area_terrain_metadata")
        {
            if (reader.TryRead<TerrainStruct>(targetAddr, out var terrain))
            {
                if (terrain.BytesPerRow >= 0)
                {
                    result.Status = ValidationStatus.VALID;
                    result.ResolvedAddress = targetAddr;
                    result.ExtractedValue = $"TerrainStruct [BytesPerRow={terrain.BytesPerRow}]";
                    result.Evidence.Add(new EvidenceRecord
                    {
                        RuleName = "TerrainStructValid",
                        Description = $"Terrain metadata valid at +0x{node.DefaultOffset:X}",
                        Passed = true,
                        IsIndependentValidator = true
                    });
                    return;
                }
            }

            result.Status = ValidationStatus.BROKEN;
            result.ErrorMessage = $"Invalid TerrainStruct at +0x{node.DefaultOffset:X}";
            return;
        }

        if (reader.TryRead<byte>(targetAddr, out _))
        {
            result.Status = ValidationStatus.VALID;
            result.ResolvedAddress = targetAddr;
            result.ExtractedValue = $"Struct at +0x{node.DefaultOffset:X}";
            return;
        }

        result.Status = ValidationStatus.BROKEN;
        result.ErrorMessage = $"Unreadable struct memory at +0x{node.DefaultOffset:X}";
    }
}
