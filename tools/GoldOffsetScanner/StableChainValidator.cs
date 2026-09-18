namespace GoldOffsetScanner
{
    using System;

    public static class StableChainValidator
    {
        public static void Validate(NativeMemoryReader reader)
        {
            Console.WriteLine("================================================================================");
            Console.WriteLine("                 STABLE GOLD POINTER CHAIN VERIFICATION                         ");
            Console.WriteLine("================================================================================");

            var ctx = PointerEvaluator.ResolveSdkRoots(reader);
            if (!ctx.IsValid || ctx.PlayerServerData == 0)
            {
                Console.WriteLine("Error: Unable to resolve SDK roots.");
                return;
            }

            var psdAddr = (IntPtr)(long)ctx.PlayerServerData;
            Console.WriteLine($"PlayerServerData Base Address: 0x{ctx.PlayerServerData:X12}");

            // Test candidate pointer chains:
            // 1. PSD + 0x0E28 -> Obj + 0x618
            // 2. PSD + 0x0E20 -> Obj + 0x698
            // 3. PSD + 0x0E18 -> Obj + 0x718
            // 4. PSD + 0x0E10 -> Obj + 0x798
            // 5. PSD + 0x0E08 -> Obj + 0x818
            // 6. PSD + 0x0DA0 -> Obj + 0x8A0
            // 7. PSD + 0x0D70 -> Obj + 0x9A0

            var chains = new (int PsdOffset, int ObjOffset, string Name)[]
            {
                (0x0E28, 0x618, "PlayerServerData + 0x0E28 -> +0x618"),
                (0x0E20, 0x698, "PlayerServerData + 0x0E20 -> +0x698"),
                (0x0E18, 0x718, "PlayerServerData + 0x0E18 -> +0x718"),
                (0x0E10, 0x798, "PlayerServerData + 0x0E10 -> +0x798"),
                (0x0E08, 0x818, "PlayerServerData + 0x0E08 -> +0x818"),
                (0x0DA0, 0x8A0, "PlayerServerData + 0x0DA0 -> +0x8A0"),
                (0x0D70, 0x9A0, "PlayerServerData + 0x0D70 -> +0x9A0"),
                (0x0BD8, 0xC20, "PlayerServerData + 0x0BD8 -> +0xC20"),
            };

            foreach (var (psdOff, objOff, name) in chains)
            {
                if (reader.TryRead<IntPtr>(psdAddr + psdOff, out var ptr) && ptr != IntPtr.Zero)
                {
                    var targetAddr = (IntPtr)(ptr.ToInt64() + objOff);
                    if (reader.TryRead<int>(targetAddr, out var goldVal))
                    {
                        Console.WriteLine($"  [Chain] {name,-42} : Ptr=0x{ptr.ToInt64():X12} -> Target=0x{targetAddr.ToInt64():X12} | Value={goldVal:N0}");
                    }
                }
            }

            Console.WriteLine("================================================================================");
        }
    }
}
