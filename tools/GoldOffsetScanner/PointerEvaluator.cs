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

    public sealed class PointerContext
    {
        public ulong GameStatesBase { get; set; }
        public ulong InGameState { get; set; }
        public ulong AreaInstance { get; set; }
        public ulong ServerData { get; set; }
        public ulong PlayerServerData { get; set; }
        public ulong LocalPlayer { get; set; }
        public Dictionary<string, ulong> Inventories { get; } = new();
        public bool IsValid => InGameState != 0;
    }

    public static class PointerEvaluator
    {
        public static PointerContext ResolveSdkRoots(NativeMemoryReader reader)
        {
            var ctx = new PointerContext();
            if (reader.MainModuleBase == IntPtr.Zero || reader.MainModuleSize == 0)
            {
                return ctx;
            }

            try
            {
                var gameStatesPattern = StaticOffsetsPatterns.Patterns[0]; // "Game States"
                var patternOffset = FindPattern(reader, gameStatesPattern);
                if (patternOffset >= 0)
                {
                    var dispAddr = (IntPtr)(reader.MainModuleBase.ToInt64() + patternOffset + gameStatesPattern.BytesToSkip);
                    if (reader.TryRead<int>(dispAddr, out var disp))
                    {
                        var staticAddr = (ulong)(dispAddr.ToInt64() + disp + 4);
                        ctx.GameStatesBase = staticAddr;

                        if (reader.TryRead<GameStateStaticOffset>((IntPtr)(long)staticAddr, out var staticObj) &&
                            staticObj.GameState != IntPtr.Zero)
                        {
                            if (reader.TryRead<GameStateOffset>(staticObj.GameState, out var stateOffset))
                            {
                                // InGameState is at index 4 (States[4].X)
                                ctx.InGameState = (ulong)stateOffset.States[4].X.ToInt64();

                                if (ctx.InGameState != 0)
                                {
                                    ResolveInGameStateRoots(reader, ctx);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PointerEvaluator] Note: Could not resolve full SDK roots: {ex.Message}");
            }

            return ctx;
        }

        private static void ResolveInGameStateRoots(NativeMemoryReader reader, PointerContext ctx)
        {
            var inGameAddr = (IntPtr)(long)ctx.InGameState;
            if (!reader.TryRead<InGameStateOffset>(inGameAddr, out var inGameStateData))
            {
                return;
            }

            ctx.AreaInstance = (ulong)inGameStateData.AreaInstanceData.ToInt64();
            if (ctx.AreaInstance == 0)
            {
                return;
            }

            var areaAddr = (IntPtr)(long)ctx.AreaInstance;
            if (reader.TryRead<AreaInstanceOffsets>(areaAddr, out var areaOffsets))
            {
                ctx.ServerData = (ulong)areaOffsets.PlayerInfo.ServerDataPtr.ToInt64();
                ctx.LocalPlayer = (ulong)areaOffsets.PlayerInfo.LocalPlayerPtr.ToInt64();

                if (ctx.ServerData != 0)
                {
                    var serverDataAddr = (IntPtr)(long)ctx.ServerData;
                    if (reader.TryRead<ServerDataOffsets>(serverDataAddr, out var serverOffsets))
                    {
                        var stdVec = serverOffsets.PlayerServerDataPtr;
                        var elemCount = stdVec.TotalElements(8);
                        if (elemCount > 0 && elemCount < 10)
                        {
                            Span<IntPtr> ptrs = stackalloc IntPtr[(int)elemCount];
                            var byteSize = (int)elemCount * 8;
                            Span<byte> byteBuf = stackalloc byte[byteSize];
                            if (reader.TryReadBytes(stdVec.First, byteBuf, out var bytesRead) && bytesRead == byteSize)
                            {
                                var playerServerDataPtr = MemoryMarshal.Cast<byte, IntPtr>(byteBuf)[0];
                                ctx.PlayerServerData = (ulong)playerServerDataPtr.ToInt64();

                                if (ctx.PlayerServerData != 0)
                                {
                                    ResolveInventories(reader, ctx);
                                }
                            }
                        }
                    }
                }
            }
        }

        private static void ResolveInventories(NativeMemoryReader reader, PointerContext ctx)
        {
            var psdAddr = (IntPtr)(long)ctx.PlayerServerData;
            if (!reader.TryRead<ServerDataStructure>(psdAddr, out var psdData))
            {
                return;
            }

            var invVec = psdData.PlayerInventories;
            var invCount = (int)invVec.TotalElements(0x18); // sizeof(InventoryArrayStruct) is 0x18
            if (invCount is <= 0 or > 100)
            {
                return;
            }

            var totalBytes = invCount * 0x18;
            var buffer = new byte[totalBytes];
            if (reader.TryReadBytes(invVec.First, buffer, out var readBytes) && readBytes == totalBytes)
            {
                var span = MemoryMarshal.Cast<byte, InventoryArrayStruct>(buffer);
                for (var i = 0; i < span.Length; i++)
                {
                    var inv = span[i];
                    var invName = $"Inv_{inv.InventoryId} (0x{inv.InventoryId:X})";
                    if (inv.InventoryPtr0 != IntPtr.Zero)
                    {
                        ctx.Inventories[invName] = (ulong)inv.InventoryPtr0.ToInt64();
                    }
                }
            }
        }

        public static void AnalyzePointerChains(NativeMemoryReader reader, PointerContext ctx, ulong candidateAddress)
        {
            Console.WriteLine($"\n[Pointer Analysis] Tracing pointer chains pointing to candidate 0x{candidateAddress:X12} (and enclosing struct +/- 0x1000)...");

            var candidateMin = candidateAddress >= 0x1000 ? (candidateAddress - 0x1000) : 0;
            var candidateMax = candidateAddress + 0x100;

            // 1. Deep probe PlayerServerData fields
            if (ctx.PlayerServerData != 0)
            {
                Console.WriteLine("\n[PlayerServerData Deep Vector / Struct Probe]:");
                var psdBuf = new byte[0x2000];
                if (reader.TryReadBytes((IntPtr)(long)ctx.PlayerServerData, psdBuf, out var psdRead))
                {
                    for (var off = 0x0; off <= psdRead - 24; off += 8)
                    {
                        var vFirst = (ulong)BitConverter.ToInt64(psdBuf, off);
                        var vLast = (ulong)BitConverter.ToInt64(psdBuf, off + 8);
                        var vEnd = (ulong)BitConverter.ToInt64(psdBuf, off + 16);

                        if (vFirst >= 0x10000 && vLast >= vFirst && vEnd >= vLast && (vEnd - vFirst) < 0x100000 && (vEnd - vFirst) > 0)
                        {
                            var totalBytes = vLast - vFirst;
                            // Check if candidateAddress is within [vFirst, vLast)
                            if (candidateAddress >= vFirst && candidateAddress < vLast)
                            {
                                var elemOffset = candidateAddress - vFirst;
                                Console.ForegroundColor = ConsoleColor.Green;
                                Console.WriteLine($"  *** DIRECT HIT in StdVector at PlayerServerData + 0x{off:X4} ***");
                                Console.WriteLine($"      Vector Range: [0x{vFirst:X12} .. 0x{vLast:X12}] (Total Bytes: 0x{totalBytes:X})");
                                Console.WriteLine($"      Offset from Vector.First: +0x{elemOffset:X}");
                                Console.ResetColor();
                            }
                            // Check if any element in vector points to candidateAddress
                            else if (totalBytes < 0x50000)
                            {
                                var vecData = new byte[(int)totalBytes];
                                if (reader.TryReadBytes((IntPtr)(long)vFirst, vecData, out var vecRead) && vecRead == (int)totalBytes)
                                {
                                    for (var e = 0; e <= vecRead - 8; e += 8)
                                    {
                                        var ptr = (ulong)BitConverter.ToInt64(vecData, e);
                                        if (ptr == candidateAddress)
                                        {
                                            Console.ForegroundColor = ConsoleColor.Green;
                                            Console.WriteLine($"  *** DIRECT POINTER in StdVector at PlayerServerData + 0x{off:X4}, Index {e / 8} -> 0x{ptr:X12} ***");
                                            Console.ResetColor();
                                        }
                                        else if (ptr >= candidateMin && ptr <= candidateMax)
                                        {
                                            var diff = (long)candidateAddress - (long)ptr;
                                            Console.WriteLine($"      StdVector at PlayerServerData + 0x{off:X4}, Index {e / 8} -> Points to 0x{ptr:X12} (Gold at +0x{diff:X})");
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // 2. Check direct pointers in PlayerServerData
            if (ctx.PlayerServerData != 0)
            {
                CheckPointersInBlock(reader, ctx.PlayerServerData, 0x4000, "PlayerServerData", candidateAddress, candidateMin, candidateMax);
            }

            // 3. Check direct pointers in ServerData
            if (ctx.ServerData != 0)
            {
                CheckPointersInBlock(reader, ctx.ServerData, 0x1000, "ServerData", candidateAddress, candidateMin, candidateMax);
            }

            // 4. Check direct pointers in AreaInstance
            if (ctx.AreaInstance != 0)
            {
                CheckPointersInBlock(reader, ctx.AreaInstance, 0x2000, "AreaInstance", candidateAddress, candidateMin, candidateMax);
            }

            // 5. Check direct pointers in InGameState
            if (ctx.InGameState != 0)
            {
                CheckPointersInBlock(reader, ctx.InGameState, 0x1000, "InGameState", candidateAddress, candidateMin, candidateMax);
            }

            // 6. Check direct pointers in LocalPlayer & components
            if (ctx.LocalPlayer != 0)
            {
                CheckPointersInBlock(reader, ctx.LocalPlayer, 0x1000, "LocalPlayer", candidateAddress, candidateMin, candidateMax);
            }

            // 7. Check PlayerInventories
            foreach (var kvp in ctx.Inventories)
            {
                CheckPointersInBlock(reader, kvp.Value, 0x400, $"Inventory [{kvp.Key}]", candidateAddress, candidateMin, candidateMax);
            }

            // 8. Global pointer search across readable memory for pointers pointing directly to the enclosing struct
            Console.WriteLine("\n  Searching all process memory for incoming pointers to enclosing structure...");
            var incomingPointers = SearchIncomingPointers(reader, candidateMin, candidateMax);
            Console.WriteLine($"  Found {incomingPointers.Count} pointers pointing to the enclosing structure (range 0x{candidateMin:X12}..0x{candidateMax:X12}):");

            foreach (var (ptrAddr, targetPtr) in incomingPointers.Take(15))
            {
                var offsetInside = (long)candidateAddress - (long)targetPtr;
                var contextName = EvaluateCandidateContext(reader, ctx, ptrAddr);
                Console.WriteLine($"    * At 0x{ptrAddr:X12} [{contextName}]: Points to 0x{targetPtr:X12} (Gold is at Object + 0x{offsetInside:X})");
            }
        }

        private static void CheckPointersInBlock(NativeMemoryReader reader, ulong baseAddress, int blockSize, string blockName, ulong candidateAddress, ulong minTarget, ulong maxTarget)
        {
            var buffer = new byte[blockSize];
            if (!reader.TryReadBytes((IntPtr)(long)baseAddress, buffer, out var bytesRead) || bytesRead < 8)
            {
                return;
            }

            for (var offset = 0; offset <= bytesRead - 8; offset += 8)
            {
                var ptrVal = (ulong)BitConverter.ToInt64(buffer, offset);
                if (ptrVal >= minTarget && ptrVal <= maxTarget)
                {
                    var goldOffset = (long)candidateAddress - (long)ptrVal;
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"  >>> MATCH in {blockName} + 0x{offset:X4} -> Points to 0x{ptrVal:X12} (Gold at +0x{goldOffset:X}) <<<");
                    Console.ResetColor();
                }
            }
        }

        private static List<(ulong Address, ulong Target)> SearchIncomingPointers(NativeMemoryReader reader, ulong minTarget, ulong maxTarget)
        {
            var results = new List<(ulong Address, ulong Target)>();
            var regions = reader.EnumerateReadableRegions();
            var chunk = new byte[1024 * 1024];

            foreach (var reg in regions)
            {
                ulong regOffset = 0;
                while (regOffset < reg.RegionSize)
                {
                    var chunkSize = (int)Math.Min((ulong)chunk.Length, reg.RegionSize - regOffset);
                    var readAddr = (IntPtr)(long)(reg.BaseAddress + regOffset);

                    if (reader.TryReadBytes(readAddr, chunk.AsSpan(0, chunkSize), out var read) && read >= 8)
                    {
                        for (var i = 0; i <= read - 8; i += 8)
                        {
                            var val = (ulong)BitConverter.ToInt64(chunk, i);
                            if (val >= minTarget && val <= maxTarget)
                            {
                                results.Add((reg.BaseAddress + regOffset + (ulong)i, val));
                            }
                        }
                    }

                    regOffset += (ulong)chunkSize;
                }
            }

            return results;
        }

        public static string EvaluateCandidateContext(NativeMemoryReader reader, PointerContext ctx, ulong candidateAddress)
        {
            if (!ctx.IsValid)
            {
                return "Unknown Heap (SDK Roots Unresolved)";
            }

            var sb = new StringBuilder();

            // Check PlayerServerData relative offset
            if (ctx.PlayerServerData != 0)
            {
                var diff = (long)candidateAddress - (long)ctx.PlayerServerData;
                if (diff >= 0 && diff < 0x10000)
                {
                    sb.Append($"Inside PlayerServerData + 0x{diff:X4}");
                    return sb.ToString();
                }
            }

            // Check ServerData relative offset
            if (ctx.ServerData != 0)
            {
                var diff = (long)candidateAddress - (long)ctx.ServerData;
                if (diff >= 0 && diff < 0x10000)
                {
                    sb.Append($"Inside ServerDataObject + 0x{diff:X4}");
                    return sb.ToString();
                }
            }

            // Check LocalPlayer relative offset
            if (ctx.LocalPlayer != 0)
            {
                var diff = (long)candidateAddress - (long)ctx.LocalPlayer;
                if (diff >= 0 && diff < 0x10000)
                {
                    sb.Append($"Inside LocalPlayer + 0x{diff:X4}");
                    return sb.ToString();
                }
            }

            // Check AreaInstance relative offset
            if (ctx.AreaInstance != 0)
            {
                var diff = (long)candidateAddress - (long)ctx.AreaInstance;
                if (diff >= 0 && diff < 0x10000)
                {
                    sb.Append($"Inside AreaInstance + 0x{diff:X4}");
                    return sb.ToString();
                }
            }

            // Check Inventories
            foreach (var kvp in ctx.Inventories)
            {
                var diff = (long)candidateAddress - (long)kvp.Value;
                if (diff >= 0 && diff < 0x1000)
                {
                    sb.Append($"Inside {kvp.Key} + 0x{diff:X4}");
                    return sb.ToString();
                }
            }

            return "Dynamic Heap / External Struct";
        }

        private static int FindPattern(NativeMemoryReader reader, Pattern pattern)
        {
            var baseAddr = reader.MainModuleBase;
            var procSize = (int)Math.Min(reader.MainModuleSize, 0x10000000); // 256MB max search
            var chunkSize = 64 * 1024;
            var buffer = new byte[chunkSize + pattern.Data.Length];

            for (var offset = 0; offset < procSize; offset += chunkSize)
            {
                var toRead = Math.Min(buffer.Length, procSize - offset);
                if (toRead < pattern.Data.Length) break;

                if (reader.TryReadBytes((IntPtr)(baseAddr.ToInt64() + offset), buffer.AsSpan(0, toRead), out var bytesRead))
                {
                    for (var i = 0; i <= bytesRead - pattern.Data.Length; i++)
                    {
                        var match = true;
                        for (var j = 0; j < pattern.Data.Length; j++)
                        {
                            if (pattern.Mask[j] && buffer[i + j] != pattern.Data[j])
                            {
                                match = false;
                                break;
                            }
                        }

                        if (match)
                        {
                            return offset + i;
                        }
                    }
                }
            }

            return -1;
        }
    }
}
