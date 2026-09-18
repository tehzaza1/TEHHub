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
