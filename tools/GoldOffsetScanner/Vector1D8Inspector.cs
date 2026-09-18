namespace GoldOffsetScanner
{
    using System;
    using System.Runtime.InteropServices;
    using TEHhub.Offsets.Natives;

    public static class Vector1D8Inspector
    {
        public static void Inspect(NativeMemoryReader reader)
        {
            Console.WriteLine("================================================================================");
            Console.WriteLine("               INSPECTING PLAYER SERVER DATA + 0x01D8 VECTOR                    ");
            Console.WriteLine("================================================================================");

            var ctx = PointerEvaluator.ResolveSdkRoots(reader);
            if (!ctx.IsValid || ctx.PlayerServerData == 0)
            {
                Console.WriteLine("Error: Unable to resolve PlayerServerData root.");
                return;
            }

            var psdAddr = (IntPtr)(long)ctx.PlayerServerData;
            var vecAddr = (IntPtr)(long)(ctx.PlayerServerData + 0x01D8);

            if (!reader.TryRead<StdVector>(vecAddr, out var stdVec))
            {
                Console.WriteLine("Error: Failed to read StdVector at PSD + 0x01D8");
                return;
            }

            var totalPtrs = stdVec.TotalElements(8);
            Console.WriteLine($"StdVector at PSD + 0x01D8:");
            Console.WriteLine($"  First: 0x{stdVec.First.ToInt64():X12}");
            Console.WriteLine($"  Last:  0x{stdVec.Last.ToInt64():X12}");
            Console.WriteLine($"  End:   0x{stdVec.End.ToInt64():X12}");
            Console.WriteLine($"  Total Elements (8-byte pointers): {totalPtrs:N0}");

            if (totalPtrs > 1059)
            {
                var ptr1059Addr = (IntPtr)(stdVec.First.ToInt64() + (1059 * 8));
                if (reader.TryRead<IntPtr>(ptr1059Addr, out var ptr1059))
                {
                    Console.WriteLine($"\nElement [1059] pointer: 0x{ptr1059.ToInt64():X12}");
                    var goldFieldAddr = (IntPtr)(ptr1059.ToInt64() + 0x98);

                    if (reader.TryRead<int>(goldFieldAddr, out var goldVal))
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"  >>> Gold Value at (Element[1059] + 0x98): {goldVal:N0} <<<");
                        Console.ResetColor();
                    }

                    ObjectMetadataInspector.InspectObject(reader, (ulong)ptr1059.ToInt64());

                    // Let's also inspect the structure at ptr1059 from 0x00 to 0xC0
                    Console.WriteLine("\nStructure Hex Dump at Element [1059] (0x00 .. 0xC0):");
                    var buf = new byte[0xC0];
                    if (reader.TryReadBytes(ptr1059, buf, out var readCount))
                    {
                        for (var i = 0; i < readCount; i += 16)
                        {
                            var rowAddr = (ulong)ptr1059.ToInt64() + (ulong)i;
                            var marker = (i <= 0x98 && 0x98 < i + 16) ? ">>" : "  ";
                            Console.Write($"{marker} +0x{i:X2} (0x{rowAddr:X12}) | ");
                            for (var b = 0; b < 16; b++)
                            {
                                if (i + b < readCount)
                                {
                                    var byteVal = buf[i + b];
                                    var isTarget = (i + b == 0x98);
                                    Console.Write(isTarget ? $"[{byteVal:X2}]" : $" {byteVal:X2} ");
                                }
                                else Console.Write("    ");
                            }
                            Console.Write(" | ");
                            for (var b = 0; b < 16; b++)
                            {
                                if (i + b < readCount)
                                {
                                    var byteVal = buf[i + b];
                                    char c = (byteVal >= 32 && byteVal <= 126) ? (char)byteVal : '.';
                                    Console.Write(c);
                                }
                            }
                            Console.WriteLine();
                        }
                    }
                }
            }

            // Let's also scan all elements in the vector to see what IDs / structures they have
            Console.WriteLine("\nScanning all elements in vector to understand its index/id scheme:");
            var totalBytes = (int)Math.Min((long)totalPtrs * 8, 10000 * 8);
            var ptrBuf = new byte[totalBytes];
            if (reader.TryReadBytes(stdVec.First, ptrBuf, out var ptrRead))
            {
                var ptrSpan = MemoryMarshal.Cast<byte, IntPtr>(ptrBuf.AsSpan(0, ptrRead));
                var nonNullCount = 0;
                for (var idx = 0; idx < ptrSpan.Length; idx++)
                {
                    var p = ptrSpan[idx];
                    if (p != IntPtr.Zero)
                    {
                        nonNullCount++;
                        // Let's check what's at p + 0x00 and p + 0x08
                        if (reader.TryRead<ulong>(p, out var vtableOrId))
                        {
                            if (idx == 1059 || idx < 10 || (idx >= 1055 && idx <= 1065))
                            {
                                reader.TryRead<int>((IntPtr)(p.ToInt64() + 0x98), out var intAt98);
                                Console.WriteLine($"  Index [{idx,4}]: Ptr=0x{p.ToInt64():X12} | Header=0x{vtableOrId:X16} | +0x98 Val={intAt98:N0}");
                            }
                        }
                    }
                }
                Console.WriteLine($"Total Non-Null Pointers in Vector: {nonNullCount}/{ptrSpan.Length}");
            }

            Console.WriteLine("================================================================================");
        }
    }
}
