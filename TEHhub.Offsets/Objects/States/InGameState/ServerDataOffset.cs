namespace TEHhub.Offsets.Objects.States.InGameState
{
    using System;
    using System.Runtime.InteropServices;
    using Natives;

    [StructLayout(LayoutKind.Explicit, Pack = 1)]
    public struct ServerDataOffsets
    {
        [FieldOffset(0x48)] public StdVector PlayerServerDataPtr;
    }
    
    [StructLayout(LayoutKind.Explicit, Pack = 1)]
    public struct ServerDataStructure
    {
        [FieldOffset(0x320)] public StdVector PlayerInventories; // InventoryArrayStruct
        [FieldOffset(0x8A8)] public StdVector WorldAreaMods; // ModArrayStruct or pointers to Mods.dat
    }

    [StructLayout(LayoutKind.Explicit, Pack = 1)]
    public struct InventoryArrayStruct
    {
        [FieldOffset(0x00)] public int InventoryId;
        [FieldOffset(0x04)] public int PAD_0;
        [FieldOffset(0x08)] public IntPtr InventoryPtr0; // InventoryStruct
        [FieldOffset(0x10)] public IntPtr InventoryPtr1; // this points to 0x10 bytes before InventoryPtr0
    }

    /// <summary>
    ///     Offsets and structural layout constants for PlayerServerData.
    /// </summary>
    public static class PlayerServerDataOffsets
    {
        /// <summary>
        ///     Offset in PlayerServerData pointing to the 4th record pointer slot (observed Record #4)
        ///     within the 0x80-stride record sequence.
        /// </summary>
        public const int GoldRecordPtrSlot = 0x0E28;

        /// <summary>
        ///     Offset to the 32-bit native Gold value relative to the pointer at <see cref="GoldRecordPtrSlot"/>.
        ///     Equivalent to (16 - 4) * RecordStride (0x80) + RecordGoldOffset (0x18) = 0x618.
        /// </summary>
        public const int GoldFieldOffset = 0x0618;

        /// <summary>
        ///     Observed stride between sequential record blocks (128 bytes).
        /// </summary>
        public const int RecordStride = 0x80;

        /// <summary>
        ///     Observed 0-based record index containing the player's current gold balance.
        /// </summary>
        public const int GoldRecordIndex = 16;

        /// <summary>
        ///     Observed offset within the gold record to the 32-bit native gold integer.
        /// </summary>
        public const int RecordGoldOffset = 0x18;
    }
}
