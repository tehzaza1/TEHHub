namespace TEHhub.OffsetDoctor.Validation;

using System.Runtime.CompilerServices;
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
using TEHhub.Offsets.Objects.States;
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
        var resolvedResults = new Dictionary<string, ValidationResult>();
        var resolvedTraversalAddresses = new Dictionary<string, IntPtr>();

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
                if (result.TraversalAddress != IntPtr.Zero)
                {
                    resolvedTraversalAddresses[node.Id] = result.TraversalAddress;
                }
                resolvedResults[node.Id] = result;
                results.Add(result);
                continue;
            }

            // 2. Parent Dependency Check
            if (!resolvedResults.TryGetValue(node.ParentId, out var parentRes))
            {
                result.Status = ValidationStatus.BLOCKED;
                result.ErrorMessage = $"Blocked: Parent node '{node.ParentId}' not found.";
                result.Evidence.Add(new EvidenceRecord
                {
                    RuleName = "ParentDependencyCheck",
                    Description = $"Required parent '{node.ParentId}' was not evaluated.",
                    Passed = false,
                    ScoreDelta = 0
                });
                resolvedResults[node.Id] = result;
                results.Add(result);
                continue;
            }

            if (parentRes.Status == ValidationStatus.BROKEN || parentRes.Status == ValidationStatus.BLOCKED)
            {
                result.Status = ValidationStatus.BLOCKED;
                result.ErrorMessage = $"Blocked: Parent node '{node.ParentId}' is {parentRes.Status}.";
                result.Evidence.Add(new EvidenceRecord
                {
                    RuleName = "ParentDependencyCheck",
                    Description = $"Required parent '{node.ParentId}' is {parentRes.Status}",
                    Passed = false,
                    ScoreDelta = 0
                });
                resolvedResults[node.Id] = result;
                results.Add(result);
                continue;
            }

            if (!resolvedTraversalAddresses.TryGetValue(node.ParentId, out var parentTraversalAddr) ||
                parentTraversalAddr == IntPtr.Zero ||
                !reader.IsValidAddress(parentTraversalAddr) ||
                !reader.TryRead<byte>(parentTraversalAddr, out _))
            {
                result.Status = ValidationStatus.BLOCKED;
                result.ErrorMessage = $"Blocked: Parent node '{node.ParentId}' has no valid downstream traversal address.";
                result.Evidence.Add(new EvidenceRecord
                {
                    RuleName = "ParentTraversalAddressCheck",
                    Description = $"Required parent '{node.ParentId}' has no safe traversal address (status: {parentRes.Status})",
                    Passed = false,
                    ScoreDelta = 0
                });
                resolvedResults[node.Id] = result;
                results.Add(result);
                continue;
            }

            if (parentRes.Status == ValidationStatus.VALID)
            {
                result.Evidence.Add(new EvidenceRecord
                {
                    RuleName = "ParentValid",
                    Description = $"Parent '{node.ParentId}' is VALID at 0x{parentTraversalAddr.ToInt64():X}",
                    Passed = true,
                    ScoreDelta = 10,
                    IsIndependentValidator = true
                });
            }
            else
            {
                result.Evidence.Add(new EvidenceRecord
                {
                    RuleName = "ParentStructurallyResolved",
                    Description = $"Parent '{node.ParentId}' is structurally resolved at 0x{parentTraversalAddr.ToInt64():X} (status: {parentRes.Status})",
                    Passed = true,
                    ScoreDelta = 5,
                    IsIndependentValidator = true
                });
            }

            // 3. Evaluate node at configured offset
            EvaluateNode(reader, parentTraversalAddr, node, result, context);

            if (result.TraversalAddress != IntPtr.Zero)
            {
                resolvedTraversalAddresses[node.Id] = result.TraversalAddress;
            }

            resolvedResults[node.Id] = result;
            results.Add(result);
        }

        return results;
    }

    private static void ValidateStaticPatternNode(IProcessMemoryReader reader, OffsetNode node, ValidationResult result)
    {
        if (reader.MainModuleBase == IntPtr.Zero || reader.MainModuleSize <= 0)
        {
            result.Status = ValidationStatus.BROKEN;
            result.TraversalAddress = IntPtr.Zero;
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
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"Static pattern '{patternName}' is not defined in StaticOffsetsPatterns.";
            return;
        }

        int scanSize = (int)(reader.MainModuleSize > 0 ? reader.MainModuleSize : 100_000_000);
        var buf = reader.ReadBytes(reader.MainModuleBase, scanSize);

        if (buf == null || buf.Length < patternDef.Data.Length)
        {
            result.Status = ValidationStatus.BROKEN;
            result.TraversalAddress = IntPtr.Zero;
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
            result.TraversalAddress = IntPtr.Zero;
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
            // CRITICAL: Ambiguous static patterns MUST NOT provide any traversal address!
            result.Status = ValidationStatus.UNVERIFIED;
            result.TraversalAddress = IntPtr.Zero;
            result.ObservedAddress = reader.MainModuleBase + matchOffset;
            result.ErrorMessage = $"Pattern '{patternName}' matched multiple locations ({matchCount}). Uniqueness invariant violated; traversal blocked.";
            result.Evidence.Add(new EvidenceRecord
            {
                RuleName = "PatternUnique",
                Description = $"Pattern '{patternName}' matched {matchCount} locations (expected 1)",
                Passed = false
            });
            return;
        }

        result.Evidence.Add(new EvidenceRecord
        {
            RuleName = "PatternUniqueMatch",
            Description = $"Pattern '{patternName}' matched uniquely at RVA +0x{matchOffset:X}",
            Passed = true,
            IsIndependentValidator = true
        });

        if (node.StaticPatternResolution == StaticPatternResolutionKind.RipRelativeDisp32)
        {
            int dispOffset = matchOffset + patternDef.BytesToSkip;
            if (dispOffset + 4 <= buf.Length)
            {
                int disp32 = BitConverter.ToInt32(buf, dispOffset);
                long rip = reader.MainModuleBase.ToInt64() + dispOffset + 4;
                long targetStaticAddr = rip + disp32;
                var targetPtr = new IntPtr(targetStaticAddr);

                if (reader.IsValidAddress(targetPtr) && reader.TryRead<byte>(targetPtr, out _))
                {
                    result.Status = ValidationStatus.VALID;
                    result.ObservedAddress = reader.MainModuleBase + matchOffset;
                    result.ResolvedAddress = targetPtr;
                    result.TraversalAddress = targetPtr;
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
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"Pattern '{patternName}' matched but RIP displacement target is unmapped or unreadable.";
        }
        else if (node.StaticPatternResolution == StaticPatternResolutionKind.DirectMatchPlusSkip)
        {
            long targetDirectAddr = reader.MainModuleBase.ToInt64() + matchOffset + patternDef.BytesToSkip;
            var targetPtr = new IntPtr(targetDirectAddr);

            if (reader.IsValidAddress(targetPtr) && reader.TryRead<byte>(targetPtr, out _))
            {
                result.Status = ValidationStatus.VALID;
                result.ObservedAddress = reader.MainModuleBase + matchOffset;
                result.ResolvedAddress = targetPtr;
                result.TraversalAddress = targetPtr;
                result.ExtractedValue = $"DirectPtr (0x{targetPtr.ToInt64():X})";
                result.Evidence.Add(new EvidenceRecord
                {
                    RuleName = "DirectMatchReadable",
                    Description = $"Direct pattern address 0x{targetPtr.ToInt64():X} points to valid readable memory",
                    Passed = true,
                    IsIndependentValidator = true
                });
                return;
            }

            result.Status = ValidationStatus.BROKEN;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"Pattern '{patternName}' matched but direct target 0x{targetPtr.ToInt64():X} is unmapped or unreadable.";
        }
    }

    private static void EvaluateNode(
        IProcessMemoryReader reader,
        IntPtr parentAddr,
        OffsetNode node,
        ValidationResult result,
        RecoveryContext context)
    {
        var targetAddr = parentAddr + node.DefaultOffset;
        result.ObservedAddress = targetAddr;

        if (node.ConservativeUnverifiedOnly)
        {
            result.Status = ValidationStatus.UNVERIFIED;
            result.ResolvedAddress = targetAddr;
            result.TraversalAddress = IntPtr.Zero;
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

            case ValueKind.RecordSlotField:
            {
                ValidateGoldRecordSlot(reader, parentAddr, targetAddr, node, result);
                break;
            }

            case ValueKind.PointerField:
            {
                ValidatePointerField(reader, targetAddr, node, result);
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
                result.TraversalAddress = IntPtr.Zero;
                result.ErrorMessage = $"Unsupported ValueKind: {node.Kind}";
                break;
        }
    }

    private static void ValidateGoldRecordSlot(
        IProcessMemoryReader reader,
        IntPtr parentAddr,
        IntPtr targetAddr,
        OffsetNode node,
        ValidationResult result)
    {
        result.ResolvedAddress = targetAddr;

        // Verify the sequence of neighboring record pointer slots in PlayerServerData.
        // Known structure: 5 consecutive 8-byte pointer slots at PSD + 0x0E08, 0x0E10, 0x0E18, 0x0E20, 0x0E28
        // pointing to records with 0x80 byte stride (PlayerServerDataOffsets.RecordStride).
        // Configured GoldRecordPtrSlot (0x0E28) is the 5th slot (index 4).
        const int slotCount = 5;
        const int configuredSlotIndex = 4;
        const int slotPointerSize = 8;

        var recordPointers = new IntPtr[slotCount];
        for (int i = 0; i < slotCount; i++)
        {
            int slotOffset = node.DefaultOffset - (configuredSlotIndex - i) * slotPointerSize;
            IntPtr slotAddr = parentAddr + slotOffset;

            if (!reader.TryRead<IntPtr>(slotAddr, out var recPtr))
            {
                result.Status = ValidationStatus.BROKEN;
                result.TraversalAddress = IntPtr.Zero;
                result.ErrorMessage = $"Failed to read record slot #{i} pointer at PSD+0x{slotOffset:X}";
                result.Evidence.Add(new EvidenceRecord
                {
                    RuleName = "NeighboringRecordSlotReadable",
                    Description = $"Record slot #{i} at +0x{slotOffset:X} could not be read",
                    Passed = false
                });
                return;
            }

            if (recPtr == IntPtr.Zero || !reader.IsValidAddress(recPtr) || !reader.TryRead<byte>(recPtr, out _))
            {
                result.Status = ValidationStatus.BROKEN;
                result.TraversalAddress = IntPtr.Zero;
                result.ErrorMessage = $"Record slot #{i} pointer at PSD+0x{slotOffset:X} (0x{recPtr.ToInt64():X}) is null, invalid, or unreadable.";
                result.Evidence.Add(new EvidenceRecord
                {
                    RuleName = "NeighboringRecordSlotTargetValid",
                    Description = $"Record slot #{i} target pointer 0x{recPtr.ToInt64():X} is null, invalid, or unreadable",
                    Passed = false
                });
                return;
            }

            if (i > 0)
            {
                long prevAddr = recordPointers[i - 1].ToInt64();
                long currAddr = recPtr.ToInt64();
                long stride = currAddr - prevAddr;
                if (stride != PlayerServerDataOffsets.RecordStride)
                {
                    result.Status = ValidationStatus.BROKEN;
                    result.TraversalAddress = IntPtr.Zero;
                    result.ErrorMessage = $"Record slot stride mismatch between slot #{i - 1} and #{i}: expected 0x{PlayerServerDataOffsets.RecordStride:X} (128 bytes), observed 0x{stride:X} ({stride} bytes).";
                    result.Evidence.Add(new EvidenceRecord
                    {
                        RuleName = "RecordSlotStrideRelationship",
                        Description = $"Stride between slot #{i - 1} (0x{prevAddr:X}) and slot #{i} (0x{currAddr:X}) is 0x{stride:X} (expected 0x{PlayerServerDataOffsets.RecordStride:X})",
                        Passed = false
                    });
                    return;
                }
            }

            recordPointers[i] = recPtr;
        }

        IntPtr configuredRecordMem = recordPointers[configuredSlotIndex];

        // Verify final gold address is readable as Int32
        IntPtr finalGoldAddr = configuredRecordMem + PlayerServerDataOffsets.GoldFieldOffset;
        if (!reader.TryRead<int>(finalGoldAddr, out _))
        {
            result.Status = ValidationStatus.BROKEN;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"Final gold address at 0x{finalGoldAddr.ToInt64():X} (+0x{PlayerServerDataOffsets.GoldFieldOffset:X} from record) is unreadable as Int32.";
            result.Evidence.Add(new EvidenceRecord
            {
                RuleName = "FinalGoldAddressReadable",
                Description = $"Memory at 0x{finalGoldAddr.ToInt64():X} could not be read as 32-bit integer",
                Passed = false
            });
            return;
        }

        result.Status = ValidationStatus.VALID;
        result.ResolvedAddress = targetAddr;
        result.TraversalAddress = configuredRecordMem;
        result.ExtractedValue = $"RecordSlot #4 -> 0x{configuredRecordMem.ToInt64():X} (5 PSD record slots verified with 0x{PlayerServerDataOffsets.RecordStride:X} stride)";
        result.Evidence.Add(new EvidenceRecord
        {
            RuleName = "RecordSlotSequenceStrideVerified",
            Description = $"Verified {slotCount} neighboring PSD record slots with exact 0x{PlayerServerDataOffsets.RecordStride:X} stride sequence up to slot +0x{node.DefaultOffset:X} (0x{configuredRecordMem.ToInt64():X})",
            Passed = true,
            IsIndependentValidator = true
        });
    }

    private static void ValidatePointerField(
        IProcessMemoryReader reader,
        IntPtr targetAddr,
        OffsetNode node,
        ValidationResult result)
    {
        if (!reader.TryRead<IntPtr>(targetAddr, out var ptr))
        {
            result.Status = ValidationStatus.BROKEN;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"Failed to read pointer memory at +0x{node.DefaultOffset:X}";
            result.Evidence.Add(new EvidenceRecord
            {
                RuleName = "PointerReadable",
                Description = $"Memory at +0x{node.DefaultOffset:X} could not be read",
                Passed = false
            });
            return;
        }

        if (ptr == IntPtr.Zero)
        {
            if (node.IsOptionalStateDependent)
            {
                result.Status = ValidationStatus.UNVERIFIED;
                result.ResolvedAddress = IntPtr.Zero;
                result.TraversalAddress = IntPtr.Zero;
                result.ErrorMessage = "Optional / state-dependent pointer is null in current runtime state.";
                result.Evidence.Add(new EvidenceRecord
                {
                    RuleName = "OptionalPointerNullState",
                    Description = "Pointer is null (legitimately inactive / closed in current runtime state)",
                    Passed = true
                });
                return;
            }
            else
            {
                result.Status = ValidationStatus.BROKEN;
                result.ResolvedAddress = IntPtr.Zero;
                result.TraversalAddress = IntPtr.Zero;
                result.ErrorMessage = $"Required pointer at +0x{node.DefaultOffset:X} is null.";
                result.Evidence.Add(new EvidenceRecord
                {
                    RuleName = "PointerNotNull",
                    Description = $"Required pointer at +0x{node.DefaultOffset:X} is null",
                    Passed = false
                });
                return;
            }
        }

        if (!reader.IsValidAddress(ptr) || !reader.TryRead<byte>(ptr, out _))
        {
            result.Status = ValidationStatus.BROKEN;
            result.ResolvedAddress = ptr;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"Pointer at +0x{node.DefaultOffset:X} (0x{ptr.ToInt64():X}) points to unmapped or unreadable memory.";
            result.Evidence.Add(new EvidenceRecord
            {
                RuleName = "PointerTargetReadable",
                Description = $"Pointer target 0x{ptr.ToInt64():X} is unreadable or unmapped",
                Passed = false
            });
            return;
        }

        // Pointer is valid and target memory is readable.
        result.ResolvedAddress = ptr;
        result.TraversalAddress = ptr;
        result.ExtractedValue = $"0x{ptr.ToInt64():X}";

        if (node.Id == "area_local_player_entity")
        {
            if (reader.TryRead<ItemStruct>(ptr, out var item) &&
                item.EntityDetailsPtr != IntPtr.Zero &&
                reader.IsValidAddress(item.EntityDetailsPtr) &&
                reader.TryRead<EntityDetails>(item.EntityDetailsPtr, out var details))
            {
                var pathStr = DecodeStdWString(reader, details.name);
                if (!string.IsNullOrEmpty(pathStr) && pathStr.StartsWith("Metadata/Characters/", StringComparison.Ordinal))
                {
                    result.Status = ValidationStatus.VALID;
                    result.Evidence.Add(new EvidenceRecord
                    {
                        RuleName = "PlayerEntityMetadataVerified",
                        Description = $"Local player entity verified with character path \"{pathStr}\"",
                        Passed = true,
                        IsIndependentValidator = true
                    });
                    return;
                }
            }
        }

        // Generic pointer readability without dedicated semantic proof -> UNVERIFIED
        result.Status = ValidationStatus.UNVERIFIED;
        result.ErrorMessage = "Pointer target is readable and structurally valid; unverified without semantic object proof.";
        result.Evidence.Add(new EvidenceRecord
        {
            RuleName = "PointerTargetReadableStructuralOnly",
            Description = $"Pointer at +0x{node.DefaultOffset:X} (0x{ptr.ToInt64():X}) is readable (structural evidence only)",
            Passed = true
        });
    }

    private static void ValidateComponentLookup(
        IProcessMemoryReader reader,
        IntPtr entityAddr,
        OffsetNode node,
        ValidationResult result)
    {
        var compName = node.ComponentName ?? "Component";

        if (!reader.TryRead<ItemStruct>(entityAddr, out var itemStruct) ||
            itemStruct.EntityDetailsPtr == IntPtr.Zero ||
            !reader.IsValidAddress(itemStruct.EntityDetailsPtr))
        {
            result.Status = ValidationStatus.BROKEN;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"Unable to read valid ItemStruct / EntityDetailsPtr on Entity at 0x{entityAddr.ToInt64():X}";
            return;
        }

        if (!reader.TryRead<EntityDetails>(itemStruct.EntityDetailsPtr, out var entityDetails) ||
            entityDetails.ComponentLookUpPtr == IntPtr.Zero ||
            !reader.IsValidAddress(entityDetails.ComponentLookUpPtr))
        {
            result.Status = ValidationStatus.BROKEN;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"Unable to read EntityDetails or ComponentLookUpPtr at 0x{itemStruct.EntityDetailsPtr.ToInt64():X}";
            return;
        }

        if (!reader.TryRead<ComponentLookUpStruct>(entityDetails.ComponentLookUpPtr, out var lookupStruct))
        {
            result.Status = ValidationStatus.BROKEN;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"Unable to read ComponentLookUpStruct at 0x{entityDetails.ComponentLookUpPtr.ToInt64():X}";
            return;
        }

        var bucketVector = lookupStruct.ComponentsNameAndIndex.Data;
        int elemSize = Unsafe.SizeOf<ComponentNameAndIndexStruct>();
        long bVal = bucketVector.First.ToInt64();
        long eVal = bucketVector.Last.ToInt64();
        long cVal = bucketVector.End.ToInt64();

        if (bVal == 0 || eVal == 0 || cVal == 0 || bVal > eVal || eVal > cVal)
        {
            result.Status = ValidationStatus.BROKEN;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = "Corrupt ComponentLookUp bucket vector boundaries (begin <= end <= capacity violated).";
            return;
        }

        long bucketByteLen = eVal - bVal;
        long bucketCapLen = cVal - bVal;

        if (bucketByteLen % elemSize != 0 || bucketCapLen % elemSize != 0 || bucketByteLen > 100_000)
        {
            result.Status = ValidationStatus.BROKEN;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"Invalid ComponentLookUp bucket vector alignment ({bucketByteLen} bytes, element size {elemSize}).";
            return;
        }

        int entryCount = (int)(bucketByteLen / elemSize);
        if (entryCount == 0)
        {
            result.Status = ValidationStatus.UNVERIFIED;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = "Entity component lookup table is empty (0 entries).";
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
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"Component '{compName}' not found in entity component lookup dictionary ({entryCount} entries scanned).";
            return;
        }

        long clB = itemStruct.ComponentListPtr.First.ToInt64();
        long clE = itemStruct.ComponentListPtr.Last.ToInt64();
        long clC = itemStruct.ComponentListPtr.End.ToInt64();

        if (clB == 0 || clE == 0 || clC == 0 || clB > clE || clE > clC || (clE - clB) % 8 != 0 || (clC - clB) % 8 != 0)
        {
            result.Status = ValidationStatus.BROKEN;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = "Corrupt ComponentList vector boundaries or alignment.";
            return;
        }

        int compListCount = (int)((clE - clB) / 8);
        if (targetIndex >= compListCount || targetIndex < 0)
        {
            result.Status = ValidationStatus.BROKEN;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"Component '{compName}' lookup index {targetIndex} is out of ComponentList bounds (count {compListCount}).";
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
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"Component '{compName}' resolved to invalid address or invalid ComponentHeader.";
            return;
        }

        result.Status = ValidationStatus.VALID;
        result.ResolvedAddress = compAddr;
        result.TraversalAddress = compAddr;
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
        result.ResolvedAddress = targetAddr;

        if (!reader.TryRead<StdVector>(targetAddr, out var vec))
        {
            result.Status = ValidationStatus.BROKEN;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"Failed to read StdVector struct at +0x{node.DefaultOffset:X}";
            return;
        }

        var bVal = (ulong)vec.First.ToInt64();
        var eVal = (ulong)vec.Last.ToInt64();
        var cVal = (ulong)vec.End.ToInt64();

        // Check empty vector: {0,0,0} or First == Last
        if ((bVal == 0 && eVal == 0) || (bVal != 0 && bVal == eVal))
        {
            result.Status = ValidationStatus.UNVERIFIED;
            result.TraversalAddress = IntPtr.Zero; // CRITICAL: EMPTY VECTOR HAS ZERO TRAVERSAL ADDRESS!
            result.ExtractedValue = "StdVector [Empty, Count=0]";
            result.ErrorMessage = "StdVector has 0 elements; unproven without active elements and cannot be traversed.";
            result.Evidence.Add(new EvidenceRecord
            {
                RuleName = "StdVectorEmptyUnproven",
                Description = "StdVector has 0 elements; requires active elements for semantic proof and downstream traversal",
                Passed = true
            });
            return;
        }

        // Non-empty vector requires all pointers non-zero, valid range, valid alignment
        if (bVal == 0 || eVal == 0 || cVal == 0 || bVal > eVal || eVal > cVal ||
            !reader.IsValidAddress(vec.First) || !reader.IsValidAddress(vec.Last))
        {
            result.Status = ValidationStatus.BROKEN;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"Invalid StdVector boundaries at +0x{node.DefaultOffset:X} (First <= Last <= End violated or End==0)";
            result.Evidence.Add(new EvidenceRecord
            {
                RuleName = "StdVectorStructureValid",
                Description = "StdVector pointers violate begin <= end <= capacity invariant",
                Passed = false
            });
            return;
        }

        int elemSize = node.VectorElementSize > 0 ? node.VectorElementSize : 8;
        var byteSize = (long)(eVal - bVal);
        var capByteSize = (long)(cVal - bVal);

        if (byteSize <= 0 || byteSize % elemSize != 0 || capByteSize % elemSize != 0 || byteSize > 50_000_000)
        {
            result.Status = ValidationStatus.BROKEN;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"Invalid StdVector alignment at +0x{node.DefaultOffset:X} (Size {byteSize} bytes not multiple of {elemSize})";
            return;
        }

        var count = byteSize / elemSize;

        if (node.VectorIsPointerElements)
        {
            if (!reader.TryRead<IntPtr>(vec.First, out var firstPtr) ||
                firstPtr == IntPtr.Zero ||
                !reader.IsValidAddress(firstPtr) ||
                !reader.TryRead<byte>(firstPtr, out _))
            {
                result.Status = ValidationStatus.BROKEN;
                result.TraversalAddress = IntPtr.Zero;
                result.ErrorMessage = "Pointer vector first element is null, unmapped, or unreadable.";
                return;
            }

            result.Status = ValidationStatus.UNVERIFIED;
            result.TraversalAddress = firstPtr;
            result.ExtractedValue = $"StdVector [Count={count}, Target=0x{firstPtr.ToInt64():X}]";
            result.Evidence.Add(new EvidenceRecord
            {
                RuleName = "StdVectorPointersValid",
                Description = $"StdVector at +0x{node.DefaultOffset:X} contains {count} pointer elements; first target 0x{firstPtr.ToInt64():X} is readable",
                Passed = true,
                IsIndependentValidator = true
            });
            return;
        }

        // Struct elements vector
        var payloadBytes = reader.ReadBytes(vec.First, elemSize);
        if (payloadBytes == null || payloadBytes.Length < elemSize)
        {
            result.Status = ValidationStatus.BROKEN;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"Failed to read first struct element payload ({elemSize} bytes) at 0x{vec.First.ToInt64():X}";
            return;
        }

        result.Status = ValidationStatus.UNVERIFIED; // Structural plausibility only for generic struct vectors
        result.TraversalAddress = vec.First;
        result.ExtractedValue = $"StdVector [Count={count}, ElemSize={elemSize}]";
        result.Evidence.Add(new EvidenceRecord
        {
            RuleName = "StdVectorStructElementsValid",
            Description = $"StdVector at +0x{node.DefaultOffset:X} contains {count} struct elements (element size {elemSize})",
            Passed = true,
            IsIndependentValidator = true
        });
    }

    private static void ValidateStdMap(
        IProcessMemoryReader reader,
        IntPtr targetAddr,
        OffsetNode node,
        ValidationResult result)
    {
        result.ResolvedAddress = targetAddr;

        if (!reader.TryRead<IntPtr>(targetAddr, out var head) ||
            !reader.TryRead<int>(targetAddr + 8, out var size))
        {
            result.Status = ValidationStatus.BROKEN;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"Failed to read StdMap at +0x{node.DefaultOffset:X}";
            return;
        }

        if (size < 0 || size >= 100_000)
        {
            result.Status = ValidationStatus.BROKEN;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"Invalid StdMap size ({size}) at +0x{node.DefaultOffset:X}";
            return;
        }

        if (size == 0)
        {
            result.Status = ValidationStatus.UNVERIFIED;
            result.TraversalAddress = IntPtr.Zero;
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

        if (head == IntPtr.Zero || !reader.IsValidAddress(head) || !reader.TryRead<byte>(head, out _))
        {
            result.Status = ValidationStatus.BROKEN;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"StdMap head pointer 0x{head.ToInt64():X} is null or unreadable.";
            return;
        }

        result.Status = ValidationStatus.UNVERIFIED; // Structural plausibility only
        result.TraversalAddress = targetAddr;
        result.ExtractedValue = $"StdMap [Size={size}, Head=0x{head.ToInt64():X}]";
        result.Evidence.Add(new EvidenceRecord
        {
            RuleName = "StdMapValid",
            Description = $"StdMap at +0x{node.DefaultOffset:X} is structurally valid with size {size}",
            Passed = true,
            IsIndependentValidator = true
        });
    }

    private static void ValidateStdWString(
        IProcessMemoryReader reader,
        IntPtr targetAddr,
        OffsetNode node,
        ValidationResult result)
    {
        result.ResolvedAddress = targetAddr;
        result.TraversalAddress = targetAddr;

        if (!reader.TryRead<StdWString>(targetAddr, out var wstr))
        {
            result.Status = ValidationStatus.BROKEN;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"Failed to read StdWString struct at +0x{node.DefaultOffset:X}";
            return;
        }

        if (wstr.Length < 0 || wstr.Length > 1000 || wstr.Capacity < wstr.Length || wstr.Capacity > 10_000)
        {
            result.Status = ValidationStatus.BROKEN;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"Malformed StdWString dimensions (Length={wstr.Length}, Capacity={wstr.Capacity})";
            return;
        }

        if (wstr.Length == 0)
        {
            result.Status = ValidationStatus.UNVERIFIED;
            result.TraversalAddress = IntPtr.Zero;
            result.ExtractedValue = "\"\" (Length=0)";
            result.ErrorMessage = "StdWString is empty (Length=0).";
            return;
        }

        string strVal = string.Empty;
        if (wstr.Capacity <= 8)
        {
            var buf = new byte[16];
            BitConverter.GetBytes(wstr.Buffer.ToInt64()).CopyTo(buf, 0);
            BitConverter.GetBytes(wstr.ReservedBytes.ToInt64()).CopyTo(buf, 8);
            int byteLen = Math.Min(wstr.Length * 2, 16);
            if (!IsValidUtf16Bytes(buf, wstr.Length))
            {
                result.Status = ValidationStatus.BROKEN;
                result.TraversalAddress = IntPtr.Zero;
                result.ErrorMessage = "Malformed UTF-16 string: unpaired surrogate character detected in SSO buffer.";
                return;
            }
            strVal = Encoding.Unicode.GetString(buf, 0, byteLen);
        }
        else
        {
            if (wstr.Buffer == IntPtr.Zero || !reader.IsValidAddress(wstr.Buffer))
            {
                result.Status = ValidationStatus.BROKEN;
                result.TraversalAddress = IntPtr.Zero;
                result.ErrorMessage = $"Heap StdWString has unmapped or null buffer pointer 0x{wstr.Buffer.ToInt64():X}";
                return;
            }

            var heapBytes = reader.ReadBytes(wstr.Buffer, wstr.Length * 2);
            if (heapBytes == null || heapBytes.Length < wstr.Length * 2)
            {
                result.Status = ValidationStatus.BROKEN;
                result.TraversalAddress = IntPtr.Zero;
                result.ErrorMessage = "Unable to read full heap buffer contents for StdWString.";
                return;
            }

            if (!IsValidUtf16Bytes(heapBytes, wstr.Length))
            {
                result.Status = ValidationStatus.BROKEN;
                result.TraversalAddress = IntPtr.Zero;
                result.ErrorMessage = "Malformed UTF-16 string: unpaired surrogate character detected in heap buffer.";
                return;
            }

            strVal = Encoding.Unicode.GetString(heapBytes);
        }

        result.ExtractedValue = $"\"{strVal}\"";

        if (node.Id == "player_entity_name")
        {
            if (strVal.StartsWith("Metadata/Characters/", StringComparison.Ordinal))
            {
                result.Status = ValidationStatus.VALID;
                result.Evidence.Add(new EvidenceRecord
                {
                    RuleName = "EntityPathSemanticVerified",
                    Description = $"Entity path \"{strVal}\" verified with character metadata prefix",
                    Passed = true,
                    IsIndependentValidator = true
                });
                return;
            }
            else
            {
                result.Status = ValidationStatus.UNVERIFIED;
                result.ErrorMessage = $"Entity path \"{strVal}\" does not start with expected 'Metadata/Characters/' prefix.";
                return;
            }
        }

        // Generic player name / arbitrary string -> UNVERIFIED
        result.Status = ValidationStatus.UNVERIFIED;
        result.ErrorMessage = "String layout is structurally valid; content unverified without ground truth.";
        result.Evidence.Add(new EvidenceRecord
        {
            RuleName = "StdWStringStructuralOnly",
            Description = $"StdWString \"{strVal}\" layout is valid (Length={wstr.Length}, Cap={wstr.Capacity})",
            Passed = true
        });
    }

    private static void ValidateVitalStruct(
        IProcessMemoryReader reader,
        IntPtr targetAddr,
        OffsetNode node,
        ValidationResult result,
        RecoveryContext context)
    {
        result.ResolvedAddress = targetAddr;
        result.TraversalAddress = targetAddr;

        if (!reader.TryRead<VitalStruct>(targetAddr, out var vital))
        {
            result.Status = ValidationStatus.BROKEN;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"Failed to read VitalStruct at +0x{node.DefaultOffset:X}";
            return;
        }

        bool vtableValid = vital.VtablePtr != IntPtr.Zero && reader.IsValidAddress(vital.VtablePtr) && reader.TryRead<byte>(vital.VtablePtr, out _);
        bool totalSane = vital.Total >= 0 && vital.Total <= 500_000;
        bool currentSane = vital.Current >= 0 && vital.Current <= vital.Total;

        if (!vtableValid || !totalSane || !currentSane)
        {
            result.Status = ValidationStatus.BROKEN;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"Invalid VitalStruct (vtableValid={vtableValid}, Total={vital.Total}, Current={vital.Current})";
            return;
        }

        result.ExtractedValue = $"VitalStruct [Total={vital.Total}, Current={vital.Current}]";

        // Check ground truth if provided; mismatch is UNVERIFIED (never BROKEN) due to dynamic asynchronous game state
        if (node.Id == "comp_life_health")
        {
            if (context.ExpectedHpCurrent.HasValue && vital.Current != context.ExpectedHpCurrent.Value)
            {
                result.Status = ValidationStatus.UNVERIFIED;
                result.ErrorMessage = $"Supplied HP Current ground truth ({context.ExpectedHpCurrent.Value}) did not match live snapshot ({vital.Current}); value may have changed dynamically.";
                return;
            }
            if (context.ExpectedHpTotal.HasValue && vital.Total != context.ExpectedHpTotal.Value)
            {
                result.Status = ValidationStatus.UNVERIFIED;
                result.ErrorMessage = $"Supplied HP Total ground truth ({context.ExpectedHpTotal.Value}) did not match live snapshot ({vital.Total}).";
                return;
            }
        }
        else if (node.Id == "comp_life_mana")
        {
            if (context.ExpectedMpCurrent.HasValue && vital.Current != context.ExpectedMpCurrent.Value)
            {
                result.Status = ValidationStatus.UNVERIFIED;
                result.ErrorMessage = $"Supplied Mana Current ground truth ({context.ExpectedMpCurrent.Value}) did not match live snapshot ({vital.Current}); value may have changed dynamically.";
                return;
            }
            if (context.ExpectedMpTotal.HasValue && vital.Total != context.ExpectedMpTotal.Value)
            {
                result.Status = ValidationStatus.UNVERIFIED;
                result.ErrorMessage = $"Supplied Mana Total ground truth ({context.ExpectedMpTotal.Value}) did not match live snapshot ({vital.Total}).";
                return;
            }
        }
        else if (node.Id == "comp_life_es")
        {
            if (context.ExpectedEsCurrent.HasValue && vital.Current != context.ExpectedEsCurrent.Value)
            {
                result.Status = ValidationStatus.UNVERIFIED;
                result.ErrorMessage = $"Supplied ES Current ground truth ({context.ExpectedEsCurrent.Value}) did not match live snapshot ({vital.Current}); value may have changed dynamically.";
                return;
            }
            if (context.ExpectedEsTotal.HasValue && vital.Total != context.ExpectedEsTotal.Value)
            {
                result.Status = ValidationStatus.UNVERIFIED;
                result.ErrorMessage = $"Supplied ES Total ground truth ({context.ExpectedEsTotal.Value}) did not match live snapshot ({vital.Total}).";
                return;
            }
        }

        result.Status = ValidationStatus.VALID;
        result.Evidence.Add(new EvidenceRecord
        {
            RuleName = "VitalStructValid",
            Description = $"VitalStruct at +0x{node.DefaultOffset:X} verified (Total={vital.Total}, Current={vital.Current})",
            Passed = true,
            IsIndependentValidator = true
        });
    }

    private static void ValidateNumericField(
        IProcessMemoryReader reader,
        IntPtr targetAddr,
        OffsetNode node,
        ValidationResult result,
        RecoveryContext context)
    {
        result.ResolvedAddress = targetAddr;
        result.TraversalAddress = targetAddr;

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
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"Unable to read {node.ScalarType} at +0x{node.DefaultOffset:X}";
            return;
        }

        result.ExtractedValue = numVal;

        if (node.Id == "psd_gold_field")
        {
            int goldVal = (int)numVal;
            if (context.ExpectedGoldAmount.HasValue)
            {
                if (goldVal == context.ExpectedGoldAmount.Value)
                {
                    result.Status = ValidationStatus.VALID;
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
                    result.Status = ValidationStatus.UNVERIFIED;
                    result.ErrorMessage = $"Supplied gold ground truth ({context.ExpectedGoldAmount.Value:N0}) did not match snapshot ({goldVal:N0}); value may have changed dynamically.";
                    return;
                }
            }
            else
            {
                result.Status = ValidationStatus.UNVERIFIED;
                result.ErrorMessage = $"Plausible numeric value {goldVal:N0} requires external ground truth (--gold <amount>).";
                return;
            }
        }

        // Tightly bounded domain checks (plausibility only -> UNVERIFIED; out-of-range -> BROKEN):
        if (node.Id == "area_current_level" || node.Id == "comp_player_level")
        {
            if (numVal >= 1 && numVal <= 100)
            {
                result.Status = ValidationStatus.UNVERIFIED;
                result.Evidence.Add(new EvidenceRecord
                {
                    RuleName = "LevelDomainPlausible",
                    Description = $"Level value {numVal} is within valid PoE level domain [1..100] (plausibility only)",
                    Passed = true
                });
                return;
            }
            result.Status = ValidationStatus.BROKEN;
            result.ErrorMessage = $"Level value {numVal} is outside valid domain [1..100].";
            return;
        }

        if (node.Id == "comp_stats_weapon_index")
        {
            if (numVal == 0 || numVal == 1)
            {
                result.Status = ValidationStatus.UNVERIFIED;
                result.Evidence.Add(new EvidenceRecord
                {
                    RuleName = "WeaponIndexPlausible",
                    Description = $"Weapon index {numVal} is plausible (0 or 1)",
                    Passed = true
                });
                return;
            }
            result.Status = ValidationStatus.BROKEN;
            result.ErrorMessage = $"Weapon index {numVal} is invalid (must be 0 or 1).";
            return;
        }

        if (node.Id == "comp_positioned_reaction")
        {
            if (numVal >= 0 && numVal <= 2)
            {
                result.Status = ValidationStatus.UNVERIFIED;
                result.Evidence.Add(new EvidenceRecord
                {
                    RuleName = "ReactionPlausible",
                    Description = $"Reaction byte {numVal} is plausible (0, 1, or 2)",
                    Passed = true
                });
                return;
            }
            result.Status = ValidationStatus.BROKEN;
            result.ErrorMessage = $"Reaction byte {numVal} is invalid.";
            return;
        }

        if (node.Id == "loading_state_is_loading")
        {
            if (numVal == 0 || numVal == 1)
            {
                result.Status = ValidationStatus.UNVERIFIED;
                result.Evidence.Add(new EvidenceRecord
                {
                    RuleName = "IsLoadingPlausible",
                    Description = $"IsLoading integer {numVal} is plausible (0 or 1)",
                    Passed = true
                });
                return;
            }
            result.Status = ValidationStatus.BROKEN;
            result.ErrorMessage = $"IsLoading integer {numVal} is invalid.";
            return;
        }

        // Broad plausibility scalars (CurrentAreaHash, TotalLoadingScreenTimeMs, AnimationId, TerrainHeight, etc.) -> UNVERIFIED
        result.Status = ValidationStatus.UNVERIFIED;
        result.ErrorMessage = $"Scalar {node.ScalarType} value {numVal} is readable; unverified without dedicated catalog or ground truth.";
        result.Evidence.Add(new EvidenceRecord
        {
            RuleName = "ScalarReadablePlausibilityOnly",
            Description = $"Scalar {node.ScalarType} value {numVal} at +0x{node.DefaultOffset:X} is readable (plausibility only)",
            Passed = true
        });
    }

    private static void ValidateStructField(
        IProcessMemoryReader reader,
        IntPtr targetAddr,
        OffsetNode node,
        ValidationResult result)
    {
        result.ResolvedAddress = targetAddr;
        result.TraversalAddress = targetAddr;

        if (node.Id == "comp_render_world_pos")
        {
            if (reader.TryRead<StdTuple3D<float>>(targetAddr, out var pos))
            {
                bool validFloats = !float.IsNaN(pos.X) && !float.IsInfinity(pos.X) &&
                                  !float.IsNaN(pos.Y) && !float.IsInfinity(pos.Y) &&
                                  !float.IsNaN(pos.Z) && !float.IsInfinity(pos.Z);
                if (validFloats)
                {
                    result.Status = ValidationStatus.UNVERIFIED; // Plausibility only without spatial cross-validation
                    result.ExtractedValue = $"Pos({pos.X:F1}, {pos.Y:F1}, {pos.Z:F1})";
                    result.ErrorMessage = "World position coordinates are finite floats; unverified without spatial ground truth.";
                    result.Evidence.Add(new EvidenceRecord
                    {
                        RuleName = "WorldPositionFiniteFloats",
                        Description = $"World position contains finite floats ({pos.X:F1}, {pos.Y:F1}, {pos.Z:F1})",
                        Passed = true
                    });
                    return;
                }
            }

            result.Status = ValidationStatus.BROKEN;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"Invalid WorldPosition floats at +0x{node.DefaultOffset:X}";
            return;
        }

        if (node.Id == "area_terrain_metadata")
        {
            if (reader.TryRead<TerrainStruct>(targetAddr, out var terrain))
            {
                result.Status = ValidationStatus.UNVERIFIED; // Plausibility only
                result.ExtractedValue = $"TerrainStruct [BytesPerRow={terrain.BytesPerRow}]";
                result.ErrorMessage = "TerrainStruct layout is readable; unverified without area terrain catalog cross-reference.";
                result.Evidence.Add(new EvidenceRecord
                {
                    RuleName = "TerrainStructReadable",
                    Description = $"Terrain metadata struct memory is readable at +0x{node.DefaultOffset:X}",
                    Passed = true
                });
                return;
            }

            result.Status = ValidationStatus.BROKEN;
            result.TraversalAddress = IntPtr.Zero;
            result.ErrorMessage = $"Invalid TerrainStruct at +0x{node.DefaultOffset:X}";
            return;
        }

        if (reader.TryRead<byte>(targetAddr, out _))
        {
            result.Status = ValidationStatus.UNVERIFIED;
            result.ExtractedValue = $"Struct at +0x{node.DefaultOffset:X}";
            result.ErrorMessage = "Struct memory is readable; unverified without dedicated semantic validator.";
            return;
        }

        result.Status = ValidationStatus.BROKEN;
        result.TraversalAddress = IntPtr.Zero;
        result.ErrorMessage = $"Unreadable struct memory at +0x{node.DefaultOffset:X}";
    }

    private static bool IsValidUtf16Bytes(byte[] bytes, int charCount)
    {
        if (bytes == null || bytes.Length < charCount * 2) return false;
        for (int i = 0; i < charCount; i++)
        {
            ushort u = BitConverter.ToUInt16(bytes, i * 2);
            if (u >= 0xD800 && u <= 0xDBFF)
            {
                // High surrogate: must be followed by Low surrogate (0xDC00..0xDFFF)
                if (i + 1 >= charCount)
                    return false;
                ushort next = BitConverter.ToUInt16(bytes, (i + 1) * 2);
                if (next < 0xDC00 || next > 0xDFFF)
                    return false;
                i++; // Skip paired low surrogate
            }
            else if (u >= 0xDC00 && u <= 0xDFFF)
            {
                // Unpaired low surrogate
                return false;
            }
        }
        return true;
    }

    private static string DecodeStdWString(IProcessMemoryReader reader, StdWString wstr)
    {
        if (wstr.Length <= 0 || wstr.Length > 1000 || wstr.Capacity < wstr.Length)
        {
            return string.Empty;
        }

        if (wstr.Capacity <= 8)
        {
            var buf = new byte[16];
            BitConverter.GetBytes(wstr.Buffer.ToInt64()).CopyTo(buf, 0);
            BitConverter.GetBytes(wstr.ReservedBytes.ToInt64()).CopyTo(buf, 8);
            int byteLen = Math.Min(wstr.Length * 2, 16);
            if (!IsValidUtf16Bytes(buf, wstr.Length)) return string.Empty;
            return Encoding.Unicode.GetString(buf, 0, byteLen);
        }
        else if (wstr.Buffer != IntPtr.Zero && reader.IsValidAddress(wstr.Buffer))
        {
            var heapBytes = reader.ReadBytes(wstr.Buffer, wstr.Length * 2);
            if (heapBytes != null)
            {
                if (!IsValidUtf16Bytes(heapBytes, wstr.Length)) return string.Empty;
                return Encoding.Unicode.GetString(heapBytes);
            }
        }

        return string.Empty;
    }
}
