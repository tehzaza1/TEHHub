namespace AreaModOffsetScanner
{
    using System;
    using System.Text;
    using TEHhub.Offsets.Natives;
    using TEHhub.Offsets.Objects.States.InGameState;

    /// <summary>
    ///     Derives the playerServerDataAddress from the user-supplied ServerDataObject address
    ///     using the current verified TEHhub layout.
    /// </summary>
    public static class PointerDerivation
    {
        public sealed class DerivationResult
        {
            public bool IsValid { get; init; }
            public IntPtr ServerDataObjectAddress { get; init; }
            public int PlayerServerDataPtrOffset { get; init; }
            public IntPtr PlayerServerDataVectorAddress { get; init; }
            public StdVector PlayerServerDataVector { get; init; }
            public long PointerCount { get; init; }
            public IntPtr PlayerServerDataAddress { get; init; }
            public string DerivationLog { get; init; } = string.Empty;
            public string FailureReason { get; init; } = string.Empty;
        }

        public static DerivationResult DerivePlayerServerData(NativeMemoryReader reader, IntPtr serverDataObjectAddress)
        {
            var sb = new StringBuilder();
            sb.AppendLine("================================================================================");
            sb.AppendLine("                       POINTER CHAIN DERIVATION LOG                             ");
            sb.AppendLine("================================================================================");
            sb.AppendLine($"[1] ServerDataObject Address: 0x{serverDataObjectAddress.ToInt64():X11} ({serverDataObjectAddress.ToInt64()})");

            if (!NativeMemoryReader.IsValidAddress(serverDataObjectAddress))
            {
                var reason = $"ServerDataObject address 0x{serverDataObjectAddress.ToInt64():X} is not within valid user-mode range [0x10000..0x7FFFFFFFFFFF].";
                sb.AppendLine($"[ERROR] {reason}");
                return new DerivationResult
                {
                    IsValid = false,
                    ServerDataObjectAddress = serverDataObjectAddress,
                    FailureReason = reason,
                    DerivationLog = sb.ToString()
                };
            }

            // Offset to PlayerServerDataPtr vector in ServerDataOffsets is 0x48
            const int playerServerDataOffset = 0x48;
            var vectorAddr = serverDataObjectAddress + playerServerDataOffset;
            sb.AppendLine($"[2] PlayerServerDataPtr Offset in ServerDataOffsets: +0x{playerServerDataOffset:X2} (Address: 0x{vectorAddr.ToInt64():X11})");

            if (!reader.TryRead<StdVector>(vectorAddr, out var playerVec))
            {
                var reason = $"Failed to read StdVector at ServerDataObject + 0x48 (Address: 0x{vectorAddr.ToInt64():X11}).";
                sb.AppendLine($"[ERROR] {reason}");
                return new DerivationResult
                {
                    IsValid = false,
                    ServerDataObjectAddress = serverDataObjectAddress,
                    PlayerServerDataPtrOffset = playerServerDataOffset,
                    PlayerServerDataVectorAddress = vectorAddr,
                    FailureReason = reason,
                    DerivationLog = sb.ToString()
                };
            }

            sb.AppendLine($"[3] PlayerServerDataPtr StdVector layout:");
            sb.AppendLine($"    First: 0x{playerVec.First.ToInt64():X11}");
            sb.AppendLine($"    Last:  0x{playerVec.Last.ToInt64():X11}");
            sb.AppendLine($"    End:   0x{playerVec.End.ToInt64():X11}");

            var diff = playerVec.Last.ToInt64() - playerVec.First.ToInt64();
            if (playerVec.First == IntPtr.Zero || !NativeMemoryReader.IsValidAddress(playerVec.First))
            {
                var reason = $"PlayerServerDataPtr vector First pointer is null or invalid (0x{playerVec.First.ToInt64():X}).";
                sb.AppendLine($"[ERROR] {reason}");
                return new DerivationResult
                {
                    IsValid = false,
                    ServerDataObjectAddress = serverDataObjectAddress,
                    PlayerServerDataPtrOffset = playerServerDataOffset,
                    PlayerServerDataVectorAddress = vectorAddr,
                    PlayerServerDataVector = playerVec,
                    FailureReason = reason,
                    DerivationLog = sb.ToString()
                };
            }

            if (diff <= 0 || diff % IntPtr.Size != 0 || diff > 100 * IntPtr.Size)
            {
                var reason = $"PlayerServerDataPtr vector length ({diff} bytes) is invalid or not aligned to pointer size.";
                sb.AppendLine($"[ERROR] {reason}");
                return new DerivationResult
                {
                    IsValid = false,
                    ServerDataObjectAddress = serverDataObjectAddress,
                    PlayerServerDataPtrOffset = playerServerDataOffset,
                    PlayerServerDataVectorAddress = vectorAddr,
                    PlayerServerDataVector = playerVec,
                    FailureReason = reason,
                    DerivationLog = sb.ToString()
                };
            }

            var pointerCount = diff / IntPtr.Size;
            sb.AppendLine($"    Pointer Count: {pointerCount} (Length: {diff} bytes)");

            if (!reader.TryRead<IntPtr>(playerVec.First, out var playerServerDataAddr))
            {
                var reason = $"Failed to read first pointer element from address 0x{playerVec.First.ToInt64():X11}.";
                sb.AppendLine($"[ERROR] {reason}");
                return new DerivationResult
                {
                    IsValid = false,
                    ServerDataObjectAddress = serverDataObjectAddress,
                    PlayerServerDataPtrOffset = playerServerDataOffset,
                    PlayerServerDataVectorAddress = vectorAddr,
                    PlayerServerDataVector = playerVec,
                    PointerCount = pointerCount,
                    FailureReason = reason,
                    DerivationLog = sb.ToString()
                };
            }

            if (playerServerDataAddr == IntPtr.Zero || !NativeMemoryReader.IsValidAddress(playerServerDataAddr))
            {
                var reason = $"Resolved playerServerDataAddress is null or invalid (0x{playerServerDataAddr.ToInt64():X}).";
                sb.AppendLine($"[ERROR] {reason}");
                return new DerivationResult
                {
                    IsValid = false,
                    ServerDataObjectAddress = serverDataObjectAddress,
                    PlayerServerDataPtrOffset = playerServerDataOffset,
                    PlayerServerDataVectorAddress = vectorAddr,
                    PlayerServerDataVector = playerVec,
                    PointerCount = pointerCount,
                    PlayerServerDataAddress = playerServerDataAddr,
                    FailureReason = reason,
                    DerivationLog = sb.ToString()
                };
            }

            sb.AppendLine($"[4] Resolved playerServerDataAddress: 0x{playerServerDataAddr.ToInt64():X11} ({playerServerDataAddr.ToInt64()})");
            sb.AppendLine("================================================================================");

            return new DerivationResult
            {
                IsValid = true,
                ServerDataObjectAddress = serverDataObjectAddress,
                PlayerServerDataPtrOffset = playerServerDataOffset,
                PlayerServerDataVectorAddress = vectorAddr,
                PlayerServerDataVector = playerVec,
                PointerCount = pointerCount,
                PlayerServerDataAddress = playerServerDataAddr,
                DerivationLog = sb.ToString()
            };
        }
    }
}
