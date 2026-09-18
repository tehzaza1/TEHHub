namespace GoldOffsetScanner
{
    using System;

    public static class PsdOffsetAnalyzer
    {
        public static void Analyze(NativeMemoryReader reader, ulong goldAddress)
        {
            Console.WriteLine("================================================================================");
            Console.WriteLine("                PLAYER SERVER DATA ALL POINTERS DUMP                            ");
            Console.WriteLine("================================================================================");

            var ctx = PointerEvaluator.ResolveSdkRoots(reader);
            if (!ctx.IsValid || ctx.PlayerServerData == 0) return;

            var psdAddr = (IntPtr)(long)ctx.PlayerServerData;
            var psdBuf = new byte[0x2000];
            if (!reader.TryReadBytes(psdAddr, psdBuf, out var psdRead)) return;

            for (var off = 0x0; off <= psdRead - 8; off += 8)
            {
                var val = (ulong)BitConverter.ToInt64(psdBuf, off);
                if (val >= (goldAddress - 0x2000) && val <= (goldAddress + 0x2000))
                {
                    var diff = (long)goldAddress - (long)val;
                    Console.WriteLine($"  PSD + 0x{off:X4}: 0x{val:X12} (Gold at Ptr + 0x{diff:X4}, diff = {diff})");
                }
            }

            Console.WriteLine("================================================================================");
        }
    }
}
