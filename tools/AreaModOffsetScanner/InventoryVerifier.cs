namespace AreaModOffsetScanner
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using TEHhub.Offsets.Natives;
    using TEHhub.Offsets.Objects.States.InGameState;

    /// <summary>
    ///     Live-verifies the PlayerServerData base address using the independently known
    ///     PlayerInventories vector at PlayerServerData + 0x320 (InventoryArrayStruct stride 0x18).
    /// </summary>
    public static class InventoryVerifier
    {
        public const int PlayerInventoriesOffset = 0x320;
        public const int InventoryArrayStructStride = 0x18; // 24 bytes

        public sealed class InventoryEntryInfo
        {
            public int Index { get; init; }
            public int InventoryId { get; init; }
            public string InventoryNameString { get; init; } = string.Empty;
            public IntPtr InventoryPtr0 { get; init; }
            public IntPtr InventoryPtr1 { get; init; }
            public bool Ptr0Valid { get; init; }
            public bool Ptr1Valid { get; init; }
            public bool SatisfiesFingerprint { get; init; } // InventoryPtr1 + 0x10 == InventoryPtr0
        }

        public sealed class VerificationResult
        {
            public bool IsVerified { get; init; }
            public IntPtr PlayerServerDataAddress { get; init; }
            public IntPtr VectorAddress { get; init; }
            public StdVector Vector { get; init; }
            public bool IsStructurallyValid { get; init; }
            public string StructuralFailureReason { get; init; } = string.Empty;
            public long UsedBytes { get; init; }
            public long ElementCount { get; init; }
            public int ValidPointerPairsCount { get; init; }
            public int FingerprintMatchesCount { get; init; }
            public List<InventoryEntryInfo> SampleEntries { get; init; } = new();
            public string Log { get; init; } = string.Empty;
        }

        public static VerificationResult Verify(NativeMemoryReader reader, IntPtr playerServerDataAddress)
        {
            var sb = new StringBuilder();
            sb.AppendLine("================================================================================");
            sb.AppendLine("              PLAYER SERVER DATA BASE VERIFICATION (0x320 INVENTORIES)          ");
            sb.AppendLine("================================================================================");
            sb.AppendLine($"Target PlayerServerData: 0x{playerServerDataAddress.ToInt64():X11}");

            var vectorAddr = playerServerDataAddress + PlayerInventoriesOffset;
            sb.AppendLine($"PlayerInventories Vector Address: 0x{vectorAddr.ToInt64():X11} (Offset +0x{PlayerInventoriesOffset:X3})");

            if (!reader.TryRead<StdVector>(vectorAddr, out var vec))
            {
                var failLog = $"[FAIL] Could not read StdVector at PlayerServerData + 0x320.";
                sb.AppendLine(failLog);
                sb.AppendLine("PLAYER SERVER DATA BASE: NOT VERIFIED");
                sb.AppendLine("================================================================================");
                return new VerificationResult
                {
                    IsVerified = false,
                    PlayerServerDataAddress = playerServerDataAddress,
                    VectorAddress = vectorAddr,
                    Log = sb.ToString()
                };
            }

            var eval = VectorEvaluator.EvaluateBasic(vec);
            sb.AppendLine($"Vector First: 0x{vec.First.ToInt64():X11}");
            sb.AppendLine($"Vector Last:  0x{vec.Last.ToInt64():X11}");
            sb.AppendLine($"Vector End:   0x{vec.End.ToInt64():X11}");
            sb.AppendLine($"Used Bytes:   {eval.UsedBytes} bytes (Capacity: {eval.CapacityBytes} bytes)");
            sb.AppendLine($"Structure:    {(eval.IsBasicValid ? "VALID" : $"INVALID ({eval.FailureReasons})")}");

            if (!eval.IsBasicValid || eval.UsedBytes <= 0)
            {
                sb.AppendLine("[FAIL] PlayerInventories vector is structurally invalid or empty.");
                sb.AppendLine("PLAYER SERVER DATA BASE: NOT VERIFIED");
                sb.AppendLine("================================================================================");
                return new VerificationResult
                {
                    IsVerified = false,
                    PlayerServerDataAddress = playerServerDataAddress,
                    VectorAddress = vectorAddr,
                    Vector = vec,
                    IsStructurallyValid = eval.IsBasicValid,
                    StructuralFailureReason = eval.FailureReasons,
                    UsedBytes = eval.UsedBytes,
                    Log = sb.ToString()
                };
            }

            if (eval.UsedBytes % InventoryArrayStructStride != 0)
            {
                sb.AppendLine($"[FAIL] Vector length ({eval.UsedBytes} bytes) is not divisible by InventoryArrayStruct stride 0x{InventoryArrayStructStride:X2}.");
                sb.AppendLine("PLAYER SERVER DATA BASE: NOT VERIFIED");
                sb.AppendLine("================================================================================");
                return new VerificationResult
                {
                    IsVerified = false,
                    PlayerServerDataAddress = playerServerDataAddress,
                    VectorAddress = vectorAddr,
                    Vector = vec,
                    IsStructurallyValid = false,
                    StructuralFailureReason = $"UsedBytes not divisible by 0x{InventoryArrayStructStride:X2}",
                    UsedBytes = eval.UsedBytes,
                    Log = sb.ToString()
                };
            }

            var elementCount = eval.UsedBytes / InventoryArrayStructStride;
            sb.AppendLine($"Inventory Elements: {elementCount}");

            var sampleCount = (int)Math.Min(elementCount, 25);
            var samples = new List<InventoryEntryInfo>();
            var validPairs = 0;
            var fingerprintMatches = 0;

            sb.AppendLine($"\nInspecting first {sampleCount} inventory entries:");
            sb.AppendLine("--------------------------------------------------------------------------------");
            sb.AppendLine("Idx | Id  | Name                 | Ptr0            | Ptr1            | Ptr1+0x10==Ptr0");
            sb.AppendLine("--------------------------------------------------------------------------------");

            for (var i = 0; i < sampleCount; i++)
            {
                var entryAddr = vec.First + (i * InventoryArrayStructStride);
                if (!reader.TryRead<InventoryArrayStruct>(entryAddr, out var entry))
                {
                    break;
                }

                var p0Valid = entry.InventoryPtr0 != IntPtr.Zero && NativeMemoryReader.IsValidAddress(entry.InventoryPtr0);
                var p1Valid = entry.InventoryPtr1 != IntPtr.Zero && NativeMemoryReader.IsValidAddress(entry.InventoryPtr1);
                var fpMatch = p0Valid && p1Valid && (entry.InventoryPtr1 + 0x10 == entry.InventoryPtr0);

                if (p0Valid && p1Valid)
                {
                    validPairs++;
                }

                if (fpMatch)
                {
                    fingerprintMatches++;
                }

                var nameStr = GetInventoryNameString(entry.InventoryId);
                var fpStr = fpMatch ? "MATCH (VALID)" : (p0Valid && p1Valid ? "DIFF" : "INVALID_PTR");

                sb.AppendLine(string.Format(
                    "{0,3} | {1,3} | {2,-20} | 0x{3:X11} | 0x{4:X11} | {5}",
                    i,
                    entry.InventoryId,
                    nameStr.Length > 20 ? nameStr[..20] : nameStr,
                    entry.InventoryPtr0.ToInt64(),
                    entry.InventoryPtr1.ToInt64(),
                    fpStr));

                samples.Add(new InventoryEntryInfo
                {
                    Index = i,
                    InventoryId = entry.InventoryId,
                    InventoryNameString = nameStr,
                    InventoryPtr0 = entry.InventoryPtr0,
                    InventoryPtr1 = entry.InventoryPtr1,
                    Ptr0Valid = p0Valid,
                    Ptr1Valid = p1Valid,
                    SatisfiesFingerprint = fpMatch
                });
            }

            sb.AppendLine("--------------------------------------------------------------------------------");
            sb.AppendLine($"Summary: Valid Pointer Pairs: {validPairs}/{sampleCount}, Fingerprint Matches (Ptr1+0x10==Ptr0): {fingerprintMatches}/{sampleCount}");

            // A valid PlayerServerData must have a reasonable inventory count (e.g. >= 5) and at least some valid pointer pairs/matches
            var isVerified = elementCount >= 5 && validPairs >= 3;
            if (isVerified)
            {
                sb.AppendLine("[SUCCESS] Known inventory structure at +0x320 confirmed live in target process.");
                sb.AppendLine("PLAYER SERVER DATA BASE: VERIFIED");
            }
            else
            {
                sb.AppendLine("[WARNING] Inventory structure at +0x320 did not match expected characteristics.");
                sb.AppendLine("PLAYER SERVER DATA BASE: NOT VERIFIED");
            }
            sb.AppendLine("================================================================================");

            return new VerificationResult
            {
                IsVerified = isVerified,
                PlayerServerDataAddress = playerServerDataAddress,
                VectorAddress = vectorAddr,
                Vector = vec,
                IsStructurallyValid = eval.IsBasicValid,
                StructuralFailureReason = eval.FailureReasons,
                UsedBytes = eval.UsedBytes,
                ElementCount = elementCount,
                ValidPointerPairsCount = validPairs,
                FingerprintMatchesCount = fingerprintMatches,
                SampleEntries = samples,
                Log = sb.ToString()
            };
        }

        private static string GetInventoryNameString(int id)
        {
            return id switch
            {
                1 => "MainInventory1",
                2 => "BodyArmour1",
                3 => "Weapon1",
                4 => "Offhand1",
                5 => "Helm1",
                6 => "Amulet1",
                7 => "Ring1",
                8 => "Ring2",
                9 => "Gloves1",
                10 => "Boots1",
                11 => "Belt1",
                12 => "Flask1",
                13 => "Cursor1",
                14 => "Map1",
                15 => "Weapon2",
                16 => "Offhand2",
                17 => "Expedition2Crafting",
                24 => "PassiveJewels1",
                27 => "StashInventoryId",
                _ => $"Inv_{id}"
            };
        }
    }
}
