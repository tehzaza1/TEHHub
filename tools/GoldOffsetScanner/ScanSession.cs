namespace GoldOffsetScanner
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
    using System.Text.Json;

    public enum CandidateType
    {
        Int32,
        UInt32,
        Int64,
        UInt64,
        Utf16Text,
        AsciiText
    }

    public sealed class GoldCandidate
    {
        public ulong Address { get; set; }
        public CandidateType Type { get; set; }
        public ulong RegionBase { get; set; }
        public ulong RegionSize { get; set; }
        public string RegionProtect { get; set; } = string.Empty;
        public string RegionType { get; set; } = string.Empty;
        public long OffsetInRegion => (long)(Address - RegionBase);
        public long CurrentValue { get; set; }
        public List<long> ValueHistory { get; set; } = new();
        public string TextRepresentation { get; set; } = string.Empty;
        public bool IsUiTextCandidate => Type == CandidateType.Utf16Text || Type == CandidateType.AsciiText;
        public string PossibleStructureContext { get; set; } = "Unknown Heap";
        public string PointerChainFromRoot { get; set; } = string.Empty;
    }

    public sealed class ScanPassInfo
    {
        public int PassNumber { get; set; }
        public long TargetGoldValue { get; set; }
        public int CandidatesFound { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public sealed class ScanSessionData
    {
        public int ProcessId { get; set; }
        public List<ScanPassInfo> Passes { get; set; } = new();
        public List<GoldCandidate> Candidates { get; set; } = new();
    }

    public sealed class ScanSession
    {
        private const string SessionFileName = "gold_scan_session.json";

        public ScanSessionData Data { get; private set; } = new();

        public static ScanSession LoadOrCreate()
        {
            var session = new ScanSession();
            if (File.Exists(SessionFileName))
            {
                try
                {
                    var json = File.ReadAllText(SessionFileName);
                    var data = JsonSerializer.Deserialize<ScanSessionData>(json);
                    if (data != null)
                    {
                        session.Data = data;
                    }
                }
                catch
                {
                    session.Data = new ScanSessionData();
                }
            }
            return session;
        }

        public void Save()
        {
            var json = JsonSerializer.Serialize(this.Data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SessionFileName, json);
        }

        public void Reset()
        {
            this.Data = new ScanSessionData();
            if (File.Exists(SessionFileName))
            {
                File.Delete(SessionFileName);
            }
        }

        public void ExecuteScanPass(NativeMemoryReader reader, long goldValue)
        {
            this.Data.ProcessId = reader.ProcessId;

            if (this.Data.Passes.Count == 0 || this.Data.Candidates.Count == 0)
            {
                // Initial Scan #1: Full Process Memory Scan
                Console.WriteLine($"\n[Scan #1] Starting Initial Full Process Scan for Gold Value = {goldValue:N0}...");
                var initialCandidates = PerformInitialScan(reader, goldValue);

                this.Data.Candidates = initialCandidates;
                this.Data.Passes.Add(new ScanPassInfo
                {
                    PassNumber = 1,
                    TargetGoldValue = goldValue,
                    CandidatesFound = initialCandidates.Count,
                    Timestamp = DateTime.UtcNow
                });
                this.Save();

                Console.WriteLine($"[Scan #1] Complete. Total Candidates Found: {initialCandidates.Count:N0}");
            }
            else
            {
                // Differential Rescan (Scan #2, #3, ...): Only inspect existing candidate addresses
                var passNum = this.Data.Passes.Count + 1;
                Console.WriteLine($"\n[Scan #{passNum}] Starting Differential Rescan for New Gold Value = {goldValue:N0}...");
                Console.WriteLine($"[Scan #{passNum}] Verifying {this.Data.Candidates.Count:N0} previous candidates...");

                var survivingCandidates = new List<GoldCandidate>();
                foreach (var candidate in this.Data.Candidates)
                {
                    if (VerifyCandidate(reader, candidate, goldValue))
                    {
                        candidate.CurrentValue = goldValue;
                        candidate.ValueHistory.Add(goldValue);
                        survivingCandidates.Add(candidate);
                    }
                }

                this.Data.Candidates = survivingCandidates;
                this.Data.Passes.Add(new ScanPassInfo
                {
                    PassNumber = passNum,
                    TargetGoldValue = goldValue,
                    CandidatesFound = survivingCandidates.Count,
                    Timestamp = DateTime.UtcNow
                });
                this.Save();

                Console.WriteLine($"[Scan #{passNum}] Complete. Surviving Candidates: {survivingCandidates.Count:N0}");
            }
        }

        private static List<GoldCandidate> PerformInitialScan(NativeMemoryReader reader, long targetValue)
        {
            var candidates = new List<GoldCandidate>();
            var regions = reader.EnumerateReadableRegions();
            Console.WriteLine($"  -> Enumerated {regions.Count:N0} readable memory regions in target process.");

            var valInt32 = (int)targetValue;
            var valUInt32 = (uint)targetValue;
            var valInt64 = targetValue;
            var valUInt64 = (ulong)targetValue;

            var valStrRaw = targetValue.ToString();
            var valStrComma = $"{targetValue:N0}";
            var valBytesUtf16Raw = Encoding.Unicode.GetBytes(valStrRaw);
            var valBytesUtf16Comma = Encoding.Unicode.GetBytes(valStrComma);
            var valBytesAsciiRaw = Encoding.ASCII.GetBytes(valStrRaw);
            var valBytesAsciiComma = Encoding.ASCII.GetBytes(valStrComma);

            var chunkBuffer = new byte[1024 * 1024]; // 1 MB chunk buffer
            var regionCount = 0;

            foreach (var reg in regions)
            {
                regionCount++;
                if (regionCount % 500 == 0)
                {
                    Console.Write($"\r  -> Scanning region {regionCount:N0}/{regions.Count:N0} (Candidates: {candidates.Count:N0})...");
                }

                ulong regOffset = 0;
                while (regOffset < reg.RegionSize)
                {
                    var chunkSize = (int)Math.Min((ulong)chunkBuffer.Length, reg.RegionSize - regOffset);
                    var readAddr = (IntPtr)(long)(reg.BaseAddress + regOffset);

                    if (reader.TryReadBytes(readAddr, chunkBuffer.AsSpan(0, chunkSize), out var bytesRead) && bytesRead >= 4)
                    {
                        ScanChunk(
                            reg.BaseAddress + regOffset,
                            chunkBuffer,
                            bytesRead,
                            valInt32,
                            valUInt32,
                            valInt64,
                            valUInt64,
                            valBytesUtf16Raw,
                            valBytesUtf16Comma,
                            valBytesAsciiRaw,
                            valBytesAsciiComma,
                            valStrRaw,
                            valStrComma,
                            reg,
                            candidates);
                    }

                    regOffset += (ulong)chunkSize;
                }
            }

            Console.WriteLine($"\r  -> Scanned all {regions.Count:N0} regions. Found {candidates.Count:N0} total candidate addresses.");
            return candidates;
        }

        private static void ScanChunk(
            ulong chunkBaseAddr,
            byte[] buffer,
            int length,
            int targetInt32,
            uint targetUInt32,
            long targetInt64,
            ulong targetUInt64,
            byte[] targetUtf16Raw,
            byte[] targetUtf16Comma,
            byte[] targetAsciiRaw,
            byte[] targetAsciiComma,
            string targetStrRaw,
            string targetStrComma,
            NativeMemoryReader.MemoryRegionInfo reg,
            List<GoldCandidate> results)
        {
            // 4-byte aligned integer scan
            for (var i = 0; i <= length - 4; i += 4)
            {
                var val32 = BitConverter.ToInt32(buffer, i);
                if (val32 == targetInt32 && targetInt32 > 0)
                {
                    results.Add(new GoldCandidate
                    {
                        Address = chunkBaseAddr + (ulong)i,
                        Type = CandidateType.Int32,
                        RegionBase = reg.BaseAddress,
                        RegionSize = reg.RegionSize,
                        RegionProtect = reg.ProtectDescription,
                        RegionType = reg.TypeDescription,
                        CurrentValue = val32,
                        ValueHistory = new List<long> { val32 }
                    });
                }

                var uval32 = BitConverter.ToUInt32(buffer, i);
                if (uval32 == targetUInt32 && targetUInt32 > 0 && targetUInt32 != (uint)val32)
                {
                    results.Add(new GoldCandidate
                    {
                        Address = chunkBaseAddr + (ulong)i,
                        Type = CandidateType.UInt32,
                        RegionBase = reg.BaseAddress,
                        RegionSize = reg.RegionSize,
                        RegionProtect = reg.ProtectDescription,
                        RegionType = reg.TypeDescription,
                        CurrentValue = uval32,
                        ValueHistory = new List<long> { (long)uval32 }
                    });
                }
            }

            // 8-byte aligned integer scan
            for (var i = 0; i <= length - 8; i += 8)
            {
                var val64 = BitConverter.ToInt64(buffer, i);
                if (val64 == targetInt64 && targetInt64 > 0)
                {
                    results.Add(new GoldCandidate
                    {
                        Address = chunkBaseAddr + (ulong)i,
                        Type = CandidateType.Int64,
                        RegionBase = reg.BaseAddress,
                        RegionSize = reg.RegionSize,
                        RegionProtect = reg.ProtectDescription,
                        RegionType = reg.TypeDescription,
                        CurrentValue = val64,
                        ValueHistory = new List<long> { val64 }
                    });
                }
            }

            // Text search (UTF-16 Raw)
            ScanTextUtf16(chunkBaseAddr, buffer, length, targetUtf16Raw, targetStrRaw, reg, results);

            // Text search (UTF-16 Comma)
            if (targetUtf16Comma.Length != targetUtf16Raw.Length)
            {
                ScanTextUtf16(chunkBaseAddr, buffer, length, targetUtf16Comma, targetStrComma, reg, results);
            }

            // Text search (ASCII Raw)
            ScanTextAscii(chunkBaseAddr, buffer, length, targetAsciiRaw, targetStrRaw, reg, results);

            // Text search (ASCII Comma)
            if (targetAsciiComma.Length != targetAsciiRaw.Length)
            {
                ScanTextAscii(chunkBaseAddr, buffer, length, targetAsciiComma, targetStrComma, reg, results);
            }
        }

        private static void ScanTextUtf16(ulong chunkBaseAddr, byte[] buffer, int length, byte[] pattern, string textRep, NativeMemoryReader.MemoryRegionInfo reg, List<GoldCandidate> results)
        {
            if (pattern.Length == 0 || pattern.Length > length) return;

            for (var i = 0; i <= length - pattern.Length; i += 2)
            {
                var match = true;
                for (var j = 0; j < pattern.Length; j++)
                {
                    if (buffer[i + j] != pattern[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    results.Add(new GoldCandidate
                    {
                        Address = chunkBaseAddr + (ulong)i,
                        Type = CandidateType.Utf16Text,
                        RegionBase = reg.BaseAddress,
                        RegionSize = reg.RegionSize,
                        RegionProtect = reg.ProtectDescription,
                        RegionType = reg.TypeDescription,
                        CurrentValue = 0,
                        ValueHistory = new List<long>(),
                        TextRepresentation = textRep
                    });
                }
            }
        }

        private static void ScanTextAscii(ulong chunkBaseAddr, byte[] buffer, int length, byte[] pattern, string textRep, NativeMemoryReader.MemoryRegionInfo reg, List<GoldCandidate> results)
        {
            if (pattern.Length == 0 || pattern.Length > length) return;

            for (var i = 0; i <= length - pattern.Length; i++)
            {
                var match = true;
                for (var j = 0; j < pattern.Length; j++)
                {
                    if (buffer[i + j] != pattern[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    results.Add(new GoldCandidate
                    {
                        Address = chunkBaseAddr + (ulong)i,
                        Type = CandidateType.AsciiText,
                        RegionBase = reg.BaseAddress,
                        RegionSize = reg.RegionSize,
                        RegionProtect = reg.ProtectDescription,
                        RegionType = reg.TypeDescription,
                        CurrentValue = 0,
                        ValueHistory = new List<long>(),
                        TextRepresentation = textRep
                    });
                }
            }
        }

        private static bool VerifyCandidate(NativeMemoryReader reader, GoldCandidate candidate, long targetValue)
        {
            var addr = (IntPtr)(long)candidate.Address;

            switch (candidate.Type)
            {
                case CandidateType.Int32:
                    if (reader.TryRead<int>(addr, out var val32))
                    {
                        return val32 == (int)targetValue;
                    }
                    break;

                case CandidateType.UInt32:
                    if (reader.TryRead<uint>(addr, out var uval32))
                    {
                        return uval32 == (uint)targetValue;
                    }
                    break;

                case CandidateType.Int64:
                    if (reader.TryRead<long>(addr, out var val64))
                    {
                        return val64 == targetValue;
                    }
                    break;

                case CandidateType.UInt64:
                    if (reader.TryRead<ulong>(addr, out var uval64))
                    {
                        return uval64 == (ulong)targetValue;
                    }
                    break;

                case CandidateType.Utf16Text:
                    var strUtf16 = candidate.TextRepresentation.Contains(',') ? $"{targetValue:N0}" : targetValue.ToString();
                    var bytesUtf16 = Encoding.Unicode.GetBytes(strUtf16);
                    Span<byte> bufUtf16 = stackalloc byte[bytesUtf16.Length];
                    if (reader.TryReadBytes(addr, bufUtf16, out var readUtf16) && readUtf16 == bytesUtf16.Length)
                    {
                        return bufUtf16.SequenceEqual(bytesUtf16);
                    }
                    break;

                case CandidateType.AsciiText:
                    var strAscii = candidate.TextRepresentation.Contains(',') ? $"{targetValue:N0}" : targetValue.ToString();
                    var bytesAscii = Encoding.ASCII.GetBytes(strAscii);
                    Span<byte> bufAscii = stackalloc byte[bytesAscii.Length];
                    if (reader.TryReadBytes(addr, bufAscii, out var readAscii) && readAscii == bytesAscii.Length)
                    {
                        return bufAscii.SequenceEqual(bytesAscii);
                    }
                    break;
            }

            return false;
        }
    }
}
