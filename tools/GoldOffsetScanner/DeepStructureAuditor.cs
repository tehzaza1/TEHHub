namespace GoldOffsetScanner
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.InteropServices;
    using TEHhub.Offsets.Natives;

    public static class DeepStructureAuditor
    {
        public static void Audit(NativeMemoryReader reader, ulong goldAddress)
        {
            Console.WriteLine("================================================================================");
            Console.WriteLine("                    DEEP STRUCTURE & POINTER CHAIN AUDIT                        ");
            Console.WriteLine("================================================================================");

            var ctx = PointerEvaluator.ResolveSdkRoots(reader);
            if (!ctx.IsValid || ctx.PlayerServerData == 0)
            {
                Console.WriteLine("Error: Unable to resolve PlayerServerData root.");
                return;
            }

            Console.WriteLine($"InGameState:      0x{ctx.InGameState:X12}");
            Console.WriteLine($"AreaInstance:     0x{ctx.AreaInstance:X12}");
            Console.WriteLine($"ServerData:       0x{ctx.ServerData:X12}");
            Console.WriteLine($"PlayerServerData: 0x{ctx.PlayerServerData:X12}");
            Console.WriteLine($"Target Gold Addr: 0x{goldAddress:X12}");

            // 1. Inspect Candidate Struct (+/- 0x100 bytes around goldAddress)
            Console.WriteLine("\n[1. Target Object Memory Layout]");
            var startAddr = goldAddress - 0x80;
            var objBuf = new byte[0x100];
            if (reader.TryReadBytes((IntPtr)(long)startAddr, objBuf, out var objRead))
            {
                for (var i = 0; i < objRead; i += 16)
                {
                    var addr = startAddr + (ulong)i;
                    var marker = (addr <= goldAddress && goldAddress < addr + 16) ? ">>" : "  ";
                    Console.Write($"{marker} 0x{addr:X12} | ");
                    for (var b = 0; b < 16; b++)
                    {
                        var byteVal = objBuf[i + b];
                        var isTarget = (addr + (ulong)b == goldAddress);
                        Console.Write(isTarget ? $"[{byteVal:X2}]" : $" {byteVal:X2} ");
                    }
                    Console.Write(" | ");
                    for (var b = 0; b < 16; b++)
                    {
                        var byteVal = objBuf[i + b];
                        char c = (byteVal >= 32 && byteVal <= 126) ? (char)byteVal : '.';
                        Console.Write(c);
                    }
                    Console.WriteLine();
                }
            }

            // 2. Scan PlayerServerData offsets from 0x000 to 0x1800
            Console.WriteLine("\n[2. PlayerServerData Pointers & StdVectors pointing near Gold]");
            var psdBuf = new byte[0x2000];
            if (reader.TryReadBytes((IntPtr)(long)ctx.PlayerServerData, psdBuf, out var psdRead))
            {
                for (var off = 0x0; off <= psdRead - 8; off += 8)
                {
                    var val64 = (ulong)BitConverter.ToInt64(psdBuf, off);

                    // Check if direct pointer is near gold
                    if (val64 >= (goldAddress - 0x1000) && val64 <= (goldAddress + 0x1000))
                    {
                        var diff = (long)goldAddress - (long)val64;
                        Console.WriteLine($"  PSD + 0x{off:X4}: Direct Ptr 0x{val64:X12} -> Gold at Object + 0x{diff:X4} (diff={diff})");
                    }

                    // Check if StdVector
                    if (off <= psdRead - 24)
                    {
                        var vFirst = val64;
                        var vLast = (ulong)BitConverter.ToInt64(psdBuf, off + 8);
                        var vEnd = (ulong)BitConverter.ToInt64(psdBuf, off + 16);

                        if (vFirst >= 0x10000 && vLast >= vFirst && vEnd >= vLast && (vEnd - vFirst) < 0x200000 && (vEnd - vFirst) > 0)
                        {
                            var totalBytes = (int)(vLast - vFirst);
                            if (goldAddress >= vFirst && goldAddress < vLast)
                            {
                                Console.ForegroundColor = ConsoleColor.Green;
                                Console.WriteLine($"  *** PSD + 0x{off:X4}: StdVector [0x{vFirst:X12}..0x{vLast:X12}] CONTAINS GOLD! Offset in vector = +0x{goldAddress - vFirst:X} ***");
                                Console.ResetColor();
                            }

                            // Check elements if pointer array
                            if (totalBytes <= 0x10000)
                            {
                                var elemBuf = new byte[totalBytes];
                                if (reader.TryReadBytes((IntPtr)(long)vFirst, elemBuf, out var eRead))
                                {
                                    for (var ei = 0; ei <= eRead - 8; ei += 8)
                                    {
                                        var elemPtr = (ulong)BitConverter.ToInt64(elemBuf, ei);
                                        if (elemPtr >= (goldAddress - 0x1000) && elemPtr <= (goldAddress + 0x1000))
                                        {
                                            var diff = (long)goldAddress - (long)elemPtr;
                                            Console.WriteLine($"  PSD + 0x{off:X4}: StdVector[{ei / 8}] Ptr 0x{elemPtr:X12} -> Gold at +0x{diff:X4}");
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            Console.WriteLine("================================================================================");
        }
    }
}
