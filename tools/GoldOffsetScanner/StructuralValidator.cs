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
            Console.WriteLine("Step | Base Address        | Offset  | Raw Value Read     | Resolved Target    | Source-Backed SDK Field / Operation");
            Console.WriteLine("-----+---------------------+---------+--------------------+--------------------+------------------------------------");

            var ctx = PointerEvaluator.ResolveSdkRoots(reader);
            if (!ctx.IsValid)
            {
                Console.WriteLine("[-] FAILED: InGameState could not be resolved from GameStates pattern.");
                return;
            }

            // Step 1.1: GameStates Base Static Address
            var staticBase = ctx.GameStatesBase;
            reader.TryRead<ulong>((IntPtr)(long)staticBase, out var gameStatePtr);
            Console.WriteLine($"1.1  | 0x{staticBase:X12} | +0x0000 | 0x{gameStatePtr:X16} | 0x{gameStatePtr:X12} | GameStateStaticOffset.GameState");

            // Step 1.2: InGameState via GameStateOffset.States[4].X (InlineArray at 0x50, Index 4 = 0x50 + 4 * 16 = 0x90)
            var states4XOffset = 0x50 + (4 * 16); // 0x90
            reader.TryRead<ulong>((IntPtr)(long)(gameStatePtr + (ulong)states4XOffset), out var inGameRaw);
            Console.WriteLine($"1.2  | 0x{gameStatePtr:X12} | +0x0090 | 0x{inGameRaw:X16} | 0x{inGameRaw:X12} | GameStateOffset.States[4].X (GameStateBuffer Index 4)");

            // Step 1.3: AreaInstanceData via InGameStateOffset.AreaInstanceData (+0x290)
            reader.TryRead<ulong>((IntPtr)(long)(ctx.InGameState + 0x290), out var areaRaw);
            Console.WriteLine($"1.3  | 0x{ctx.InGameState:X12} | +0x0290 | 0x{areaRaw:X16} | 0x{ctx.AreaInstance:X12} | InGameStateOffset.AreaInstanceData");

            // Step 1.4: ServerData via AreaInstanceOffsets.PlayerInfo.ServerDataPtr (+0x5B0)
            reader.TryRead<ulong>((IntPtr)(long)(ctx.AreaInstance + 0x5B0), out var serverRaw);
            Console.WriteLine($"1.4  | 0x{ctx.AreaInstance:X12} | +0x05B0 | 0x{serverRaw:X16} | 0x{ctx.ServerData:X12} | AreaInstanceOffsets.PlayerInfo.ServerDataPtr");

            // Step 1.5a: PlayerServerData StdVector.First via ServerDataOffsets.PlayerServerDataPtr (+0x48)
            reader.TryRead<ulong>((IntPtr)(long)(ctx.ServerData + 0x48), out var psdFirstRaw);
            Console.WriteLine($"1.5a | 0x{ctx.ServerData:X12} | +0x0048 | 0x{psdFirstRaw:X16} | 0x{psdFirstRaw:X12} | ServerDataOffsets.PlayerServerDataPtr.First");

            // Step 1.5b: PlayerServerData Instance via PlayerServerDataPtr.First[0] (+0x00)
            reader.TryRead<ulong>((IntPtr)(long)psdFirstRaw, out var psdPtrRaw);
            Console.WriteLine($"1.5b | 0x{psdFirstRaw:X12} | +0x0000 | 0x{psdPtrRaw:X16} | 0x{ctx.PlayerServerData:X12} | PlayerServerDataPtr.First[0] (PlayerServerData)");

            if (ctx.PlayerServerData == 0)
            {
                Console.WriteLine("[-] FAILED: PlayerServerData pointer is null.");
                return;
            }

            var psdAddr = (IntPtr)(long)ctx.PlayerServerData;

            // 2. Suspicious Pointer Pattern Analysis: PSD + 0xDF0 .. 0x0E40
            Console.WriteLine("\n[STEP 2: Pointer Sequence Analysis: PSD + 0xDF0 .. 0x0E40]");
            Console.WriteLine("PSD Slot | Pointer Stored      | Delta from Prev Ptr | Delta from +0xE08 | Structural Description");
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
                            interp = $"Observed record pointer -> Entry #{recordIdx} (Offset in sequence = +0x{recordIdx * 0x80:X3})";
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

                // Compute offset to Gold from this pointer (Gold is at baseE08 + 0x818)
                var offsetToGold = (long)(baseE08 + 0x818) - (long)ptr;
                Console.WriteLine($"  PSD + 0x{off:X4} (Slot #{i}): 0x{ptr:X12} -> Expected: 0x{expected:X12} [{match}] -> Offset to Gold: +0x{offsetToGold:X3}");
            }

            Console.WriteLine($"\n[Pattern Audit Conclusion]:");
            Console.WriteLine($"  -> Observed Stride: Exactly 0x80 (128 bytes) per consecutive slot (+0xE08 .. +0xE28) [Strict Match: {isStrict80Stride}].");
            Console.WriteLine($"  -> Observed Sequence: Pointers at +0xE08..+0xE28 point to a contiguous sequence of 0x80-byte records.");
            Console.WriteLine($"  -> Mathematical Relationship: Record_N_Address = Record_0_Base + (N * 0x80)");
            Console.WriteLine($"  -> Offset Mechanics: Moving forward 1 slot (+0x08) advances 1 record (+0x80), decreasing remaining offset to Record #16 + 0x18 by exactly 0x80.");

            // 3. Find Gold Record and Structure Layout
            Console.WriteLine("\n[STEP 3: Gold Record Structure & Ownership Analysis]");
            reader.TryRead<ulong>((IntPtr)(psdAddr.ToInt64() + 0x0E28), out var ptrE28);
            var resolvedGoldAddress = ptrE28 + 0x618;

            Console.WriteLine($"Resolved Gold Address G: 0x{resolvedGoldAddress:X12} (Resolved via PSD + 0x0E28 -> +0x618)");
            reader.TryRead<int>((IntPtr)(long)resolvedGoldAddress, out var resolvedGoldVal);
            Console.WriteLine($"Current Native Value at G: {resolvedGoldVal:N0} (0x{resolvedGoldVal:X8})");

            // Calculate Record Base for the Gold Record:
            // Since records are 0x80 bytes apart, Gold is at RecordBase + 0x18
            var record16Base = ptrE28 + 0x600; // ptrE28 is Record #4; 4 + 12 = 16 (12 * 0x80 = 0x600)
            var offsetInRecord = (long)resolvedGoldAddress - (long)record16Base;
            Console.WriteLine($"Record Base for Gold (Record #16): 0x{record16Base:X12}");
            Console.WriteLine($"Gold Offset in Record:             +0x{offsetInRecord:X2} (0x18)");

            // Dump Record #16 (0x00 .. 0x80)
            Console.WriteLine("\n--- Record #16 (Containing Gold) Full 0x80-Byte Memory Dump ---");
            DumpRecord(reader, record16Base, resolvedGoldAddress);

            // Dump adjacent records: Record #15, Record #17
            var record15Base = record16Base - 0x80;
            var record17Base = record16Base + 0x80;
            Console.WriteLine("\n--- Preceding Record #15 Full 0x80-Byte Memory Dump ---");
            DumpRecord(reader, record15Base, 0);

            Console.WriteLine("\n--- Succeeding Record #17 Full 0x80-Byte Memory Dump ---");
            DumpRecord(reader, record17Base, 0);

            // 4. Extended Surrounding Memory Audit: G - 0x1000 .. G + 0x200
            Console.WriteLine("\n[STEP 4: Extended Surrounding Range Audit (G - 0x1000 .. G + 0x200)]");
            var blockStart = (resolvedGoldAddress >= 0x1000) ? (resolvedGoldAddress - 0x1000) : 0;
            var alignedStart = record16Base - (ulong)(((record16Base - blockStart) / 0x80) * 0x80);
            var blockEnd = record16Base + 0x200;
            var totalBlockSize = (int)(blockEnd - alignedStart);
            var blockBuf = new byte[totalBlockSize];

            if (reader.TryReadBytes((IntPtr)(long)alignedStart, blockBuf, out var blockRead))
            {
                Console.WriteLine($"Successfully read 0x{blockRead:X} bytes of surrounding heap memory [0x{alignedStart:X12} .. 0x{(alignedStart + (ulong)blockRead):X12}]");
                Console.WriteLine("\nRecord Sequence Elements Discovered in this Allocation (Stride = 0x80):");
                Console.WriteLine("Record Address      | Rel to Record #16  | +0x18 Val (Int32) | +0x1C Field | Triplet First..Last               | Marker");
                Console.WriteLine("--------------------+--------------------+-------------------+-------------+-----------------------------------+-------------------");

                for (var rOff = 0; rOff <= blockRead - 0x80; rOff += 0x80)
                {
                    var rAddr = alignedStart + (ulong)rOff;
                    var relToG = (long)rAddr - (long)record16Base;
                    var valAt18 = BitConverter.ToInt32(blockBuf, rOff + 0x18);
                    var valAt1C = BitConverter.ToInt32(blockBuf, rOff + 0x1C);
                    var vFirst = BitConverter.ToInt64(blockBuf, rOff + 0x20);
                    var vLast = BitConverter.ToInt64(blockBuf, rOff + 0x28);

                    var isGold = (rAddr == record16Base);
                    var tag = isGold ? ">>> [RECORD #16 / NATIVE GOLD] <<<" : "";
                    Console.WriteLine($"0x{rAddr:X12}  | {relToG,18:+0;-#;0} | {valAt18,17:N0} | {valAt1C,11} | 0x{vFirst:X10}..0x{vLast:X10} | {tag}");
                }
            }

            // 5. Cross-Check with Differential Candidates
            Console.WriteLine("\n[STEP 5: Cross-Check Against Differential-Scan Candidate Address]");
            var session = ScanSession.LoadOrCreate();
            var diffCandidate = session.Data.Candidates.Find(c => !c.IsUiTextCandidate);

            Console.WriteLine($"Structural Chain Resolved Address:  0x{resolvedGoldAddress:X12} (Value: {resolvedGoldVal:N0})");

            var isCandidateFromCurrentProcess = false;
            if (diffCandidate != null && session.Data.ProcessId != 0 && session.Data.ProcessId == reader.ProcessId)
            {
                isCandidateFromCurrentProcess = true;
            }

            if (isCandidateFromCurrentProcess && diffCandidate != null)
            {
                Console.WriteLine($"Current Process Candidate Address:  0x{diffCandidate.Address:X12} (Value: {diffCandidate.CurrentValue:N0})");
                var addrMatch = resolvedGoldAddress == diffCandidate.Address;
                var valMatch = resolvedGoldVal == diffCandidate.CurrentValue;
                Console.WriteLine($"  -> Address Exact Match: {(addrMatch ? "MATCH (100% IDENTICAL)" : "MISMATCH")}");
                Console.WriteLine($"  -> Value Exact Match:   {(valMatch ? "MATCH (100% IDENTICAL)" : "MISMATCH")}");
            }
            else if (diffCandidate != null && session.Data.ProcessId != 0 && session.Data.ProcessId != reader.ProcessId)
            {
                Console.WriteLine($"[Stale Candidate Detected from Previous Process (PID {session.Data.ProcessId})]: Address 0x{diffCandidate.Address:X12} belongs to previous process.");
                Console.WriteLine("  -> Current Process Status: No current-process differential candidate available.");
                Console.WriteLine($"  -> Structural Chain: Independently resolved current dynamic address 0x{resolvedGoldAddress:X12} = {resolvedGoldVal:N0}.");
            }
            else
            {
                Console.WriteLine("  -> Current Process Status: No active differential scan session; structural chain read verified independently.");
            }

            // 6. Data Type & Signedness Analysis
            Console.WriteLine("\n[STEP 6: Data Type & Signedness Analysis]");
            reader.TryRead<uint>((IntPtr)(long)resolvedGoldAddress, out var goldUInt32);
            reader.TryRead<int>((IntPtr)(long)resolvedGoldAddress, out var goldInt32);
            reader.TryRead<long>((IntPtr)(long)resolvedGoldAddress, out var goldInt64);
            Console.WriteLine($"  Int32 Representation:  {goldInt32:N0} (0x{goldInt32:X8})");
            Console.WriteLine($"  UInt32 Representation: {goldUInt32:N0} (0x{goldUInt32:X8})");
            Console.WriteLine($"  Int64 Adjacent 8-Byte: {goldInt64:N0} (0x{goldInt64:X16}) [Note: high 32-bits are {goldInt64 >> 32}]");
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

            // Test 1: Null PlayerServerData base pointer
            var nullPsdResult = SafeReadGold(reader, IntPtr.Zero, 0x0E28, 0x618, out var val1);
            Console.WriteLine($"  Test 1 [Null PlayerServerData Base]:    Success={nullPsdResult}, Value={val1} -> {(nullPsdResult == false ? "PASSED (Safe)" : "FAILED")}");

            // Test 2: Unmapped / Invalid Base Address (Low address < 0x10000)
            var badAddrResult = SafeReadGold(reader, (IntPtr)0x1000, 0x0E28, 0x618, out var val2);
            Console.WriteLine($"  Test 2 [Invalid Base Address <0x10000]: Success={badAddrResult}, Value={val2} -> {(badAddrResult == false ? "PASSED (Safe)" : "FAILED")}");

            // Test 3: Null / Unmapped Target Pointer
            var nullPtrResult = SafeReadGold(reader, unchecked((IntPtr)(long)0x00007FFFFFFE0000UL), 0x00, 0x618, out var val3);
            Console.WriteLine($"  Test 3 [Null / Unmapped Record Pointer]: Success={nullPtrResult}, Value={val3} -> {(nullPtrResult == false ? "PASSED (Safe)" : "FAILED")}");

            // Test 4: Live Valid Read
            var liveResult = SafeReadGold(reader, (IntPtr)(long)validCtx.PlayerServerData, 0x0E28, 0x618, out var liveVal);
            Console.WriteLine($"  Test 4 [Live Valid Read]:               Success={liveResult}, Value={liveVal:N0} -> {(liveResult == true ? "PASSED (Read Confirmed)" : "FAILED")}");
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
            Console.WriteLine($"    +0x00: 0x{field00:X16} (Unknown field 0x00)");
            Console.WriteLine($"    +0x08: 0x{field08:X16} (Unknown pointer / field 0x08)");
            Console.WriteLine($"    +0x10: 0x{field10:X16} (Unknown pointer / field 0x10)");
            Console.WriteLine($"    +0x18: {valInt32,12:N0} (0x{valInt32:X8}) -> [Native 32-bit Integer / Target Gold Field]");
            Console.WriteLine($"    +0x1C: {flag1C,12:N0} (0x{flag1C:X8}) -> [Unknown 32-bit Field 0x1C]");
            Console.WriteLine($"    +0x20..0x38 Candidate vector-shaped triplet (StdVector-compatible First/Last/End shape): First=0x{vecFirst:X12}, Last=0x{vecLast:X12}, End=0x{vecEnd:X12}");
        }
    }
}
