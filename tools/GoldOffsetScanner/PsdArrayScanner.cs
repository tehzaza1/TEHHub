namespace GoldOffsetScanner
{
    using System;

    public static class PsdArrayScanner
    {
        public static void ScanRange(NativeMemoryReader reader, ulong goldAddress)
        {
            Console.WriteLine("================================================================================");
            Console.WriteLine("          PSD 0x0E00..0x0F00 RANGE INSPECTION                                   ");
            Console.WriteLine("================================================================================");

            var ctx = PointerEvaluator.ResolveSdkRoots(reader);
            if (!ctx.IsValid || ctx.PlayerServerData == 0) return;

            var psdAddr = (IntPtr)(long)ctx.PlayerServerData;

            for (var off = 0x0E00; off <= 0x0F50; off += 8)
            {
                if (reader.TryRead<IntPtr>(psdAddr + off, out var ptr))
                {
                    var diff = (long)goldAddress - (long)ptr.ToInt64();
                    reader.TryRead<int>(ptr, out var valAtPtr0);
                    reader.TryRead<int>((IntPtr)(ptr.ToInt64() + 0x18), out var valAtPtr18);
                    reader.TryRead<int>((IntPtr)(ptr.ToInt64() + 0x98), out var valAtPtr98);
                    Console.WriteLine($"  PSD + 0x{off:X4}: Ptr=0x{ptr.ToInt64():X12} | Diff={diff,6} (0x{diff:X4}) | AtPtr[+0]={valAtPtr0,10:N0} | AtPtr[+18]={valAtPtr18,10:N0} | AtPtr[+98]={valAtPtr98,10:N0}");
                }
            }

            Console.WriteLine("================================================================================");
        }
    }
}
