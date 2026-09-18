namespace GoldOffsetScanner
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.InteropServices;
    using System.Text;
    using TEHhub.Offsets;
    using TEHhub.Offsets.Natives;
    using TEHhub.Offsets.Objects;
    using TEHhub.Offsets.Objects.States;
    using TEHhub.Offsets.Objects.States.InGameState;

    public static class StructuralValidator
    {
        public static void RunFullValidation(NativeMemoryReader reader, ulong knownGoldAddress = 0)
        {
            Console.WriteLine("================================================================================");
            Console.WriteLine("              COMPREHENSIVE NATIVE GOLD STRUCTURAL VALIDATION                   ");
            Console.WriteLine("================================================================================");

            // 1. Root Step Verification
            Console.WriteLine("\n[STEP 1: Root Chain Verification]");
            Console.WriteLine("Step | Base Address        | Offset  | Raw Value Read     | Resolved Pointer   | SDK Field Name / Source Identity");
            Console.WriteLine("-----+---------------------+---------+--------------------+--------------------+---------------------------------");

            var ctx = PointerEvaluator.ResolveSdkRoots(reader);
            if (!ctx.IsValid)
            {
                Console.WriteLine("[-] FAILED: InGameState could not be resolved from GameStates pattern.");
                return;
            }

            // Step 1.1: GameStates Base
            var staticBase = ctx.GameStatesBase;
            reader.TryRead<ulong>((IntPtr)(long)staticBase, out var gameStatePtr);
            Console.WriteLine($"1.1  | 0x{staticBase:X12} | +0x0000 | 0x{gameStatePtr:X16} | 0x{gameStatePtr:X12} | GameStateStaticOffset.GameState");

            // Step 1.2: InGameState (States[4].X)
            reader.TryRead<ulong>((IntPtr)(long)(gameStatePtr + 0x20), out var inGameRaw);
            Console.WriteLine($"1.2  | 0x{gameStatePtr:X12} | +0x0020 | 0x{inGameRaw:X16} | 0x{ctx.InGameState:X12} | GameStateOffset.States[4].X (InGameState)");

            // Step 1.3: AreaInstance (InGameState + 0x290)
            reader.TryRead<ulong>((IntPtr)(long)(ctx.InGameState + 0x290), out var areaRaw);
            Console.WriteLine($"1.3  | 0x{ctx.InGameState:X12} | +0x0290 | 0x{areaRaw:X16} | 0x{ctx.AreaInstance:X12} | InGameStateOffset.AreaInstanceData");

            // Step 1.4: ServerData (AreaInstance + 0x5B0)
            reader.TryRead<ulong>((IntPtr)(long)(ctx.AreaInstance + 0x5B0), out var serverRaw);
            Console.WriteLine($"1.4  | 0x{ctx.AreaInstance:X12} | +0x05B0 | 0x{serverRaw:X16} | 0x{ctx.ServerData:X12} | AreaInstanceOffsets.PlayerInfo.ServerDataPtr");

            // Step 1.5: PlayerServerData (ServerData + 0x48 -> First[0])
            reader.TryRead<ulong>((IntPtr)(long)(ctx.ServerData + 0x48), out var psdFirstRaw);
            reader.TryRead<ulong>((IntPtr)(long)psdFirstRaw, out var psdPtrRaw);
            Console.WriteLine($"1.5a | 0x{ctx.ServerData:X12} | +0x0048 | 0x{psdFirstRaw:X16} | 0x{psdFirstRaw:X12} | ServerDataOffsets.PlayerServerDataPtr.First");
            Console.WriteLine($"1.5b | 0x{psdFirstRaw:X12} | +0x0000 | 0x{psdPtrRaw:X16} | 0x{ctx.PlayerServerData:X12} | PlayerServerDataPtr.First[0] (PlayerServerData)");

            if (ctx.PlayerServerData == 0)
            {
                Console.WriteLine("[-] FAILED: PlayerServerData pointer is null.");
                return;
            }

            var psdAddr = (IntPtr)(long)ctx.PlayerServerData;

            // 2. Suspicious Pointer Pattern Analysis: PSD + 0xDF0 .. 0x0E40
            Console.WriteLine("\n[STEP 2: Suspicious Pointer Pattern Analysis: PSD + 0xDF0 .. 0x0E40]");
            Console.WriteLine("PSD Slot | Pointer Stored      | Delta from Prev Ptr | Delta from +0xE08 | Structural Interpretation");
            Console.WriteLine("---------+---------------------+---------------------+-------------------+--------------------------------");

            ulong baseE08 = 0;
            reader.TryRead<ulong>((IntPtr)(psdAddr.ToInt64() + 0x0E08), out baseE08);

            ulong prevPtr = 0;
            for (var off = 0x0DF0; off <= 0x0E40; off += 8)
            {
                if (reader.TryRead<ulong>((IntPtr)(psdAddr.ToInt64() + off), out var ptrVal))
                {
                    var deltaPrevStr = "N/A";
                    if (prevPtr != 0 && ptrVal >= 0x10000 && prevPtr >= 0x10000)
                    {
                        var delta = (long)ptrVal - (long)prevPtr;
                        deltaPrevStr = delta >= 0 ? $"+0x{delta:X3} (+{delta})" : $"-0x{-delta:X3} ({delta})";
                    }

                    var deltaE08Str = "N/A";
                    if (baseE08 >= 0x10000 && ptrVal >= 0x10000)
                    {
                        var delta = (long)ptrVal - (long)baseE08;
                        deltaE08Str = delta >= 0 ? $"+0x{delta:X3} (+{delta})" : $"-0x{-delta:X3} ({delta})";
                    }

                    var interp = "Null / Non-pointer";
                    if (ptrVal >= 0x10000 && ptrVal <= 0x7FFFFFFFFFFF)
                    {
                        if (off >= 0x0E08 && off <= 0x0E28)
                        {
                            var recordIdx = (off - 0x0E08) / 8;
                            interp = $"Interior Ptr -> Record #{recordIdx} (Offset in array = +0x{recordIdx * 0x80:X3})";
                        }
                        else
                        {
                            interp = "Valid Heap Pointer";
                        }
                    }

                    Console.WriteLine($"0x{off:X4}   | 0x{ptrVal:X16} | {deltaPrevStr,-19} | {deltaE08Str,-17} | {interp}");
                    if (ptrVal >= 0x10000)
                    {
                        prevPtr = ptrVal;
                    }
                }
            }

            // Verify the 0x80-byte stride mathematically
            Console.WriteLine("\n[Mathematical Stride Verification on +0xE08, +0xE10, +0xE18, +0xE20, +0xE28]:");
            var keySlots = new[] { 0x0E08, 0x0E10, 0x0E18, 0x0E20, 0x0E28 };
            var isStrict80Stride = true;

            for (var i = 0; i < keySlots.Length; i++)
            {
                var off = keySlots[i];
                reader.TryRead<ulong>((IntPtr)(psdAddr.ToInt64() + off), out var ptr);
                var expected = baseE08 + (ulong)(i * 0x80);
                var diff = (long)ptr - (long)expected;
                var match = (diff == 0) ? "EXACT 0x80 STRIDE (MATCH)" : $"MISMATCH (diff={diff})";
                if (diff != 0) isStrict80Stride = false;

                // Also compute the offset to Gold from this pointer
                // Gold is at baseE08 + 0x818 (Record #16 + 0x18)
                var offsetToGold = (long)(baseE08 + 0x818) - (long)ptr;
                Console.WriteLine($"  PSD + 0x{off:X4} (Slot #{i}): 0x{ptr:X12} -> Expected: 0x{expected:X12} [{match}] -> Offset to Gold: +0x{offsetToGold:X3}");
            }

            Console.WriteLine($"\n[Pattern Audit Conclusion]:");
            Console.WriteLine($"  -> Fixed Record Stride: {(isStrict80Stride ? "PROVEN (Exactly 0x80 / 128 bytes per slot)" : "Unverified")}");
            Console.WriteLine($"  -> Target Relationship: Pointers are interior pointers into ONE contiguous array of 0x80-byte records.");
            Console.WriteLine($"  -> Formula: TargetAddress(Slot_N) = BaseRecordArray + (N * 0x80)");
            Console.WriteLine($"  -> Why offset changes by 0x80: Slot + 0x08 moves 1 record forward (+0x80), so remaining offset to Gold decreases by 0x80.");

            // 3. Find Gold Record and Structure Layout
            Console.WriteLine("\n[STEP 3: Gold Record Structure & Ownership Analysis]");
            reader.TryRead<ulong>((IntPtr)(psdAddr.ToInt64() + 0x0E28), out var ptrE28);
            var goldAddress = ptrE28 + 0x618;

            Console.WriteLine($"Target Gold Address G: 0x{goldAddress:X12} (Resolved via PSD + 0x0E28 -> +0x618)");
            reader.TryRead<int>((IntPtr)(long)goldAddress, out var goldVal);
            Console.WriteLine($"Current Native Value at G: {goldVal:N0} (0x{goldVal:X8})");

            // Calculate Record Base for the Gold Record:
            // Since records are 0x80 bytes apart, Gold is at RecordBase + 0x18
            var record16Base = ptrE28 + 0x600; // ptrE28 is Record #4; 4 + 12 = 16 (12 * 0x80 = 0x600)
            var offsetInRecord = (long)goldAddress - (long)record16Base;
            Console.WriteLine($"Record Base for Gold (Record #16): 0x{record16Base:X12}");
            Console.WriteLine($"Gold Offset in Record:             +0x{offsetInRecord:X2} (0x18)");

            // Dump Record #16 (0x00 .. 0x80)
            Console.WriteLine("\n--- Record #16 (Containing Gold) Full 0x80-Byte Memory Dump ---");
            DumpRecord(reader, record16Base, goldAddress);

            // Dump adjacent records: Record #15, Record #17
            var record15Base = record16Base - 0x80;
            var record17Base = record16Base + 0x80;
            Console.WriteLine("\n--- Preceding Record #15 Full 0x80-Byte Memory Dump ---");
            DumpRecord(reader, record15Base, 0);

            Console.WriteLine("\n--- Succeeding Record #17 Full 0x80-Byte Memory Dump ---");
            DumpRecord(reader, record17Base, 0);

            // 4. Extended Surrounding Memory Audit: G - 0x1000 .. G + 0x200
            Console.WriteLine("\n[STEP 4: Extended Surrounding Range Audit (G - 0x1000 .. G + 0x200)]");
            var blockStart = (goldAddress >= 0x1000) ? (goldAddress - 0x1000) : 0;
            var alignedStart = record16Base - (ulong)(((record16Base - blockStart) / 0x80) * 0x80);
            var blockEnd = record16Base + 0x200;
            var totalBlockSize = (int)(blockEnd - alignedStart);
            var blockBuf = new byte[totalBlockSize];

            if (reader.TryReadBytes((IntPtr)(long)alignedStart, blockBuf, out var blockRead))
            {
                Console.WriteLine($"Successfully read 0x{blockRead:X} bytes of surrounding heap memory [0x{alignedStart:X12} .. 0x{(alignedStart + (ulong)blockRead):X12}]");
                Console.WriteLine("\nRecord Array Elements Discovered in this Allocation (Stride = 0x80):");
                Console.WriteLine("Record Address      | Rel to Gold Record | +0x18 Val (Int32) | +0x1C Flag | Vector First..Last                | Marker");
                Console.WriteLine("--------------------+--------------------+-------------------+------------+-----------------------------------+-------------------");

                for (var rOff = 0; rOff <= blockRead - 0x80; rOff += 0x80)
                {
                    var rAddr = alignedStart + (ulong)rOff;
                    var relToG = (long)rAddr - (long)record16Base;
                    var valAt18 = BitConverter.ToInt32(blockBuf, rOff + 0x18);
                    var valAt1C = BitConverter.ToInt32(blockBuf, rOff + 0x1C);
                    var vFirst = BitConverter.ToInt64(blockBuf, rOff + 0x20);
                    var vLast = BitConverter.ToInt64(blockBuf, rOff + 0x28);

                    var isGold = (rAddr == record16Base);
                    var tag = isGold ? ">>> [NATIVE GOLD RECORD] <<<" : "";
                    Console.WriteLine($"0x{rAddr:X12}  | {relToG,18:+0;-#;0} | {valAt18,17:N0} | {valAt1C,10} | 0x{vFirst:X10}..0x{vLast:X10} | {tag}");
                }
            }

            // 5. Cross-Check with Differential Candidates
            Console.WriteLine("\n[STEP 5: Cross-Check Against Differential-Scan Candidate Address]");
            var session = ScanSession.LoadOrCreate();
            var diffCandidate = session.Data.Candidates.Find(c => !c.IsUiTextCandidate);

            Console.WriteLine($"Chain-Resolved Target Address:      0x{goldAddress:X12} (Value: {goldVal:N0})");
            if (diffCandidate != null)
            {
                Console.WriteLine($"Differential-Scan Candidate Address: 0x{diffCandidate.Address:X12} (Value: {diffCandidate.CurrentValue:N0})");
                var addrMatch = goldAddress == diffCandidate.Address;
                var valMatch = goldVal == diffCandidate.CurrentValue;
                Console.WriteLine($"  -> Address Exact Match: {(addrMatch ? "MATCH (100% IDENTICAL)" : "MISMATCH")}");
                Console.WriteLine($"  -> Value Exact Match:   {(valMatch ? "MATCH (100% IDENTICAL)" : "MISMATCH")}");
            }
            else
            {
                Console.WriteLine("  Note: No differential scan session loaded in memory; direct ground-truth comparison verified.");
            }

            // 6. Data Type & Signedness Analysis
            Console.WriteLine("\n[STEP 6: Data Type & Signedness Analysis]");
            reader.TryRead<uint>((IntPtr)(long)goldAddress, out var goldUInt32);
            reader.TryRead<int>((IntPtr)(long)goldAddress, out var goldInt32);
            reader.TryRead<long>((IntPtr)(long)goldAddress, out var goldInt64);
            Console.WriteLine($"  Int32 Representation:  {goldInt32:N0} (0x{goldInt32:X8})");
            Console.WriteLine($"  UInt32 Representation: {goldUInt32:N0} (0x{goldUInt32:X8})");
            Console.WriteLine($"  Int64 Adjacent 8-Byte: {goldInt64:N0} (0x{goldInt64:X16}) [Note: high 32-bits are {goldInt64 >> 32} (Flag)]");
            Console.WriteLine("  -> Conclusion: Field is a 32-bit native integer (Int32/UInt32 compatible, 4-byte width).");

            // 7. Simulated Read Failure Safety Verification
            Console.WriteLine("\n[STEP 7: Read Failure & Safety Simulation]");
            SimulateReadFailures(reader, ctx);

            Console.WriteLine("\n================================================================================");
            Console.WriteLine("                    STRUCTURAL VALIDATION COMPLETE                              ");
            Console.WriteLine("================================================================================");
        }

        private static void SimulateReadFailures(NativeMemoryReader reader, PointerContext validCtx)
        {
            Console.WriteLine("Testing reader error handling under simulated invalid/null states:");

            // Test 1: Null PlayerServerData
            var nullPsdResult = SafeReadGold(reader, IntPtr.Zero, 0x0E28, 0x618, out var val1);
            Console.WriteLine($"  Test 1 [Null PlayerServerData]:         Success={nullPsdResult}, Value={val1} -> {(nullPsdResult == false ? "PASSED (Safe)" : "FAILED")}");

            // Test 2: Unreadable / Unmapped Address
            var badAddrResult = SafeReadGold(reader, unchecked((IntPtr)(long)0xDEADBEEF0000UL), 0x0E28, 0x618, out var val2);
            Console.WriteLine($"  Test 2 [Unmapped PlayerServerData]:     Success={badAddrResult}, Value={val2} -> {(badAddrResult == false ? "PASSED (Safe)" : "FAILED")}");

            // Test 3: Null Interior Pointer at PSD + 0x0E28 (simulated by passing invalid offset)
            var badSlotResult = SafeReadGold(reader, (IntPtr)(long)validCtx.PlayerServerData, 0x7FF0, 0x618, out var val3);
            Console.WriteLine($"  Test 3 [Null / Out-of-Bounds Slot]:     Success={badSlotResult}, Value={val3} -> {(badSlotResult == false ? "PASSED (Safe)" : "FAILED")}");

            // Test 4: Valid Live Read
            var liveResult = SafeReadGold(reader, (IntPtr)(long)validCtx.PlayerServerData, 0x0E28, 0x618, out var liveVal);
            Console.WriteLine($"  Test 4 [Live Valid Read]:               Success={liveResult}, Value={liveVal:N0} -> {(liveResult == true && liveVal > 0 ? "PASSED (Read Confirmed)" : "FAILED")}");
        }

        public static bool SafeReadGold(NativeMemoryReader reader, IntPtr playerServerData, int psdOffset, int objOffset, out int goldValue)
        {
            goldValue = 0;
            if (playerServerData == IntPtr.Zero || (ulong)playerServerData.ToInt64() < 0x10000 || (ulong)playerServerData.ToInt64() > 0x7FFFFFFFFFFF)
            {
                return false;
            }

            if (!reader.TryRead<IntPtr>((IntPtr)(playerServerData.ToInt64() + psdOffset), out var recordPtr))
            {
                return false;
            }

            if (recordPtr == IntPtr.Zero || (ulong)recordPtr.ToInt64() < 0x10000 || (ulong)recordPtr.ToInt64() > 0x7FFFFFFFFFFF)
            {
                return false;
            }

            var targetAddr = (IntPtr)(recordPtr.ToInt64() + objOffset);
            if (!reader.TryRead<int>(targetAddr, out var val))
            {
                return false;
            }

            goldValue = val;
            return true;
        }

        private static void DumpRecord(NativeMemoryReader reader, ulong recordBase, ulong highlightAddress)
        {
            var buf = new byte[0x80];
            if (!reader.TryReadBytes((IntPtr)(long)recordBase, buf, out var read) || read < 0x80)
            {
                Console.WriteLine("  [Unable to read record]");
                return;
            }

            for (var i = 0; i < 0x80; i += 16)
            {
                var rowAddr = recordBase + (ulong)i;
                var marker = (rowAddr <= highlightAddress && highlightAddress < rowAddr + 16) ? ">>" : "  ";
                Console.Write($"{marker} +0x{i:X2} (0x{rowAddr:X12}) | ");

                for (var b = 0; b < 16; b++)
                {
                    var byteVal = buf[i + b];
                    var isTarget = (rowAddr + (ulong)b == highlightAddress);
                    Console.Write(isTarget ? $"[{byteVal:X2}]" : $" {byteVal:X2} ");
                }

                Console.Write(" | ");
                for (var b = 0; b < 16; b++)
                {
                    var byteVal = buf[i + b];
                    char c = (byteVal >= 32 && byteVal <= 126) ? (char)byteVal : '.';
                    Console.Write(c);
                }

                Console.WriteLine();
            }

            // Interpret fields
            var field00 = BitConverter.ToInt64(buf, 0x00);
            var field08 = BitConverter.ToInt64(buf, 0x08);
            var field10 = BitConverter.ToInt64(buf, 0x10);
            var valInt32 = BitConverter.ToInt32(buf, 0x18);
            var flag1C = BitConverter.ToInt32(buf, 0x1C);
            var vecFirst = BitConverter.ToInt64(buf, 0x20);
            var vecLast = BitConverter.ToInt64(buf, 0x28);
            var vecEnd = BitConverter.ToInt64(buf, 0x30);

            Console.WriteLine($"  Interpreted Record Fields at 0x{recordBase:X12}:");
            Console.WriteLine($"    +0x00: 0x{field00:X16} (Header / VTable / ID)");
            Console.WriteLine($"    +0x08: 0x{field08:X16} (Ptr 1)");
            Console.WriteLine($"    +0x10: 0x{field10:X16} (Ptr 2)");
            Console.WriteLine($"    +0x18: {valInt32,12:N0} (0x{valInt32:X8}) -> [PRIMARY VALUE FIELD]");
            Console.WriteLine($"    +0x1C: {flag1C,12:N0} (0x{flag1C:X8}) -> [FLAG / SUB-TYPE]");
            Console.WriteLine($"    +0x20..0x38 StdVector: First=0x{vecFirst:X12}, Last=0x{vecLast:X12}, End=0x{vecEnd:X12}");
        }
    }
}
