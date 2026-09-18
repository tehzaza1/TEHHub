namespace GoldOffsetScanner
{
    using System;
    using System.Threading;

    public static class LiveGoldWatcher
    {
        public static void Watch(NativeMemoryReader reader, int durationSeconds = 30)
        {
            Console.WriteLine("================================================================================");
            Console.WriteLine("                 LIVE GOLD VALUE WATCHER (REAL-TIME TEST)                       ");
            Console.WriteLine("================================================================================");

            var ctx = PointerEvaluator.ResolveSdkRoots(reader);
            if (!ctx.IsValid || ctx.PlayerServerData == 0)
            {
                Console.WriteLine("Error: Unable to resolve SDK roots.");
                return;
            }

            var psdAddr = (IntPtr)(long)ctx.PlayerServerData;
            Console.WriteLine($"PlayerServerData: 0x{ctx.PlayerServerData:X12}");

            if (!reader.TryRead<IntPtr>(psdAddr + 0x0E28, out var ptrE28) || ptrE28 == IntPtr.Zero)
            {
                Console.WriteLine("Error: PSD + 0x0E28 is null.");
                return;
            }

            var targetAddr = (IntPtr)(ptrE28.ToInt64() + 0x618);
            Console.WriteLine($"Gold Target Address: 0x{targetAddr.ToInt64():X12} (PSD + 0x0E28 -> +0x618)");
            Console.WriteLine($"Watching live for {durationSeconds} seconds. Please change Gold in-game to see live updates!\n");

            var lastVal = -1;
            var start = DateTime.UtcNow;

            while ((DateTime.UtcNow - start).TotalSeconds < durationSeconds)
            {
                if (reader.TryRead<int>(targetAddr, out var currentVal))
                {
                    if (currentVal != lastVal)
                    {
                        var delta = lastVal == -1 ? 0 : currentVal - lastVal;
                        var deltaStr = lastVal == -1 ? "(Initial)" : (delta >= 0 ? $"(+{delta:N0})" : $"({delta:N0})");
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] LIVE GOLD UPDATE: {currentVal:N0} Gold {deltaStr}");
                        Console.ResetColor();
                        lastVal = currentVal;
                    }
                }

                Thread.Sleep(100);
            }

            Console.WriteLine("\nLive watching finished.");
            Console.WriteLine("================================================================================");
        }
    }
}
