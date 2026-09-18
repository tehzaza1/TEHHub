namespace OffsetRecovery.Tests
{
    using System;
    using System.Runtime.InteropServices;
    using TEHhub;
    using TEHhub.Offsets.Natives;
    using TEHhub.Offsets.Objects.States.InGameState;
    using TEHhub.RemoteObjects.States.InGameStateObjects;
    using TEHhub.Utils;

    internal static class GoldTests
    {
        internal static void Run(Action<bool, string> check)
        {
            using var proc = System.Diagnostics.Process.GetCurrentProcess();
            using var reader = new SafeMemoryHandle(proc.Id);

            // =========================================================================
            // 1. Pure Pointer Traversal Tests (ServerData.TryReadGold)
            // =========================================================================

            // Allocate mock buffers
            const int PsdSize = 0x1000;
            const int RecordSize = 0x800;
            var psdMem = Marshal.AllocHGlobal(PsdSize);
            var recordMem = Marshal.AllocHGlobal(RecordSize);

            try
            {
                // Clear buffers
                Marshal.Copy(new byte[PsdSize], 0, psdMem, PsdSize);
                Marshal.Copy(new byte[RecordSize], 0, recordMem, RecordSize);

                // Set up valid pointer chain: psdMem + 0x0E28 -> recordMem, recordMem + 0x0618 -> 32_679_874
                Marshal.WriteIntPtr(psdMem + PlayerServerDataOffsets.GoldRecordPtrSlot, recordMem);
                const int NonZeroGold = 32_679_874;
                Marshal.WriteInt32(recordMem + PlayerServerDataOffsets.GoldFieldOffset, NonZeroGold);

                // Test 1: Valid non-zero Gold
                bool okNonZero = ServerData.TryReadGold(reader, psdMem, out int goldNonZero);
                check(okNonZero, "TryReadGold must succeed for valid non-zero gold pointer chain.");
                check(goldNonZero == NonZeroGold, $"TryReadGold must return exact gold value {NonZeroGold} (got {goldNonZero}).");

                // Test 2: Valid zero Gold (must succeed with gold = 0, NOT fail)
                Marshal.WriteInt32(recordMem + PlayerServerDataOffsets.GoldFieldOffset, 0);
                bool okZero = ServerData.TryReadGold(reader, psdMem, out int goldZero);
                check(okZero, "TryReadGold must succeed for a legitimate zero-gold balance (valid zero != failure).");
                check(goldZero == 0, $"TryReadGold must return 0 for zero gold balance (got {goldZero}).");

                // Test 3: Null / invalid PlayerServerData address
                bool okNullPsd = ServerData.TryReadGold(reader, IntPtr.Zero, out int goldNullPsd);
                check(!okNullPsd, "TryReadGold must fail when PlayerServerData address is IntPtr.Zero.");
                check(goldNullPsd == 0, "Failed read on null PlayerServerData must output 0.");

                bool okLowPsd = ServerData.TryReadGold(reader, new IntPtr(0x1234), out int goldLowPsd);
                check(!okLowPsd, "TryReadGold must fail when PlayerServerData address is below valid user memory threshold.");
                check(goldLowPsd == 0, "Failed read on invalid low pointer must output 0.");

                // Test 4: Null record pointer at 0x0E28
                Marshal.WriteIntPtr(psdMem + PlayerServerDataOffsets.GoldRecordPtrSlot, IntPtr.Zero);
                bool okNullRec = ServerData.TryReadGold(reader, psdMem, out int goldNullRec);
                check(!okNullRec, "TryReadGold must fail when record pointer at 0x0E28 is IntPtr.Zero.");
                check(goldNullRec == 0, "Failed read on null record pointer must output 0.");

                // Test 5: Invalid low record pointer at 0x0E28
                Marshal.WriteIntPtr(psdMem + PlayerServerDataOffsets.GoldRecordPtrSlot, new IntPtr(0x5678));
                bool okLowRec = ServerData.TryReadGold(reader, psdMem, out int goldLowRec);
                check(!okLowRec, "TryReadGold must fail when record pointer at 0x0E28 is below valid user memory threshold.");
                check(goldLowRec == 0, "Failed read on invalid low record pointer must output 0.");

                // Test 6: Failed pointer slot read (unmapped memory address)
                var unmappedAddr = new IntPtr(0x7FFF_0000_0000L);
                bool okUnmappedSlot = ServerData.TryReadGold(reader, unmappedAddr, out int goldUnmappedSlot);
                check(!okUnmappedSlot, "TryReadGold must fail gracefully when reading slot from unmapped address.");
                check(goldUnmappedSlot == 0, "Failed read on unmapped slot must output 0.");

                // Test 7: Failed final gold read (valid slot pointing to unmapped address)
                Marshal.WriteIntPtr(psdMem + PlayerServerDataOffsets.GoldRecordPtrSlot, unmappedAddr);
                bool okUnmappedGold = ServerData.TryReadGold(reader, psdMem, out int goldUnmappedGold);
                check(!okUnmappedGold, "TryReadGold must fail gracefully when record pointer points to unmapped address.");
                check(goldUnmappedGold == 0, "Failed read on unmapped gold address must output 0.");

                // Test 8: Null memory reader handle
                bool okNullReader = ServerData.TryReadGold(null!, psdMem, out int goldNullReader);
                check(!okNullReader, "TryReadGold must fail gracefully when reader handle is null.");
                check(goldNullReader == 0, "Failed read on null reader must output 0.");

                // =========================================================================
                // 2. Full ServerData Remote Object Traversal Tests
                // =========================================================================
                const int ServerDataObjSize = 0x100;
                var serverDataMem = Marshal.AllocHGlobal(ServerDataObjSize);
                var vecMem = Marshal.AllocHGlobal(0x20);

                try
                {
                    Marshal.Copy(new byte[ServerDataObjSize], 0, serverDataMem, ServerDataObjSize);
                    Marshal.Copy(new byte[0x20], 0, vecMem, 0x20);

                    // Restore valid non-zero gold in recordMem
                    Marshal.WriteIntPtr(psdMem + PlayerServerDataOffsets.GoldRecordPtrSlot, recordMem);
                    Marshal.WriteInt32(recordMem + PlayerServerDataOffsets.GoldFieldOffset, 42_000);

                    // Set up PlayerServerDataPtr vector in serverDataMem + 0x48
                    Marshal.WriteIntPtr(vecMem, psdMem); // First element
                    var vector = new StdVector
                    {
                        First = vecMem,
                        Last = vecMem + IntPtr.Size,
                        End = vecMem + IntPtr.Size,
                    };
                    Marshal.StructureToPtr(vector, serverDataMem + 0x48, false);

                    var serverDataObj = new ServerData(serverDataMem);

                    // Signed TryGetGold
                    bool objOkSigned = serverDataObj.TryGetGold(out int objGoldSigned);
                    check(objOkSigned, "ServerData.TryGetGold(out int) must succeed through complete object hierarchy.");
                    check(objGoldSigned == 42_000, $"ServerData.TryGetGold(out int) returned {objGoldSigned}, expected 42000.");

                    // Unsigned TryGetGold
                    bool objOkUnsigned = serverDataObj.TryGetGold(out uint objGoldUnsigned);
                    check(objOkUnsigned, "ServerData.TryGetGold(out uint) must succeed through complete object hierarchy.");
                    check(objGoldUnsigned == 42_000u, $"ServerData.TryGetGold(out uint) returned {objGoldUnsigned}, expected 42000.");

                    // Address = IntPtr.Zero
                    var nullServerDataObj = new ServerData(IntPtr.Zero);
                    bool nullObjOk = nullServerDataObj.TryGetGold(out int nullObjGold);
                    check(!nullObjOk, "ServerData.TryGetGold must fail when ServerData.Address is IntPtr.Zero.");
                    check(nullObjGold == 0, "ServerData.TryGetGold on IntPtr.Zero must output 0.");
                }
                finally
                {
                    Marshal.FreeHGlobal(vecMem);
                    Marshal.FreeHGlobal(serverDataMem);
                }

                // =========================================================================
                // 3. Offset Constants Structure Verification
                // =========================================================================
                check(PlayerServerDataOffsets.GoldRecordPtrSlot == 0x0E28, "GoldRecordPtrSlot must be 0x0E28.");
                check(PlayerServerDataOffsets.GoldFieldOffset == 0x0618, "GoldFieldOffset must be 0x0618.");
                check(PlayerServerDataOffsets.RecordStride == 0x80, "RecordStride must be 0x80 (128 bytes).");
                check(PlayerServerDataOffsets.GoldRecordIndex == 16, "GoldRecordIndex must be 16.");
                check(PlayerServerDataOffsets.RecordGoldOffset == 0x18, "RecordGoldOffset must be 0x18.");
                check(
                    (PlayerServerDataOffsets.GoldRecordIndex - 4) * PlayerServerDataOffsets.RecordStride + PlayerServerDataOffsets.RecordGoldOffset == PlayerServerDataOffsets.GoldFieldOffset,
                    "Mathematical identity: (16 - 4) * 0x80 + 0x18 == 0x618.");
            }
            finally
            {
                Marshal.FreeHGlobal(recordMem);
                Marshal.FreeHGlobal(psdMem);
            }
        }
    }
}
