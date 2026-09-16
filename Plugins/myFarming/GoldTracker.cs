namespace myFarming
{
    using System;
    using TEHhub;
    using TEHhub.Offsets.Objects.States.InGameState;
    using TEHhub.RemoteObjects.States.InGameStateObjects;

    public sealed class GoldTracker
    {
        private static readonly int[] CandidateOffsets = new[]
        {
            0x918, 0x8F0, 0x920, 0x908, 0x8E8, 0x900, 0x910, 0x928,
            0x930, 0x938, 0x940, 0x8B0, 0x8B8, 0x8A0, 0x7A0, 0x7B0,
            0x800, 0x808, 0x810, 0x818, 0x820, 0x840, 0x850, 0x860,
            0x880, 0x890, 0x8C0, 0x8D0, 0x8E0, 0x950, 0x960, 0x980,
            0x9A0, 0x9C0, 0xA00, 0xA10, 0xA20, 0xA40, 0xA80, 0xAC0
        };

        private int discoveredOffset = -1;
        private bool isLongType = false;

        public long BaselineGold { get; private set; } = 0;
        public long CurrentGold { get; private set; } = 0;
        public long MapGoldGain { get; private set; } = 0;

        public void StartMap()
        {
            this.CurrentGold = this.ReadPlayerGold();
            this.BaselineGold = this.CurrentGold;
            this.MapGoldGain = 0;
        }

        public void ResumeMap(long previousGoldGain)
        {
            this.CurrentGold = this.ReadPlayerGold();
            this.BaselineGold = this.CurrentGold - previousGoldGain;
            this.MapGoldGain = previousGoldGain;
        }

        public void Update()
        {
            long nowGold = this.ReadPlayerGold();
            if (nowGold > 0)
            {
                this.CurrentGold = nowGold;
                if (this.BaselineGold == 0 && nowGold > 0)
                {
                    this.BaselineGold = nowGold;
                }

                if (this.CurrentGold >= this.BaselineGold)
                {
                    this.MapGoldGain = this.CurrentGold - this.BaselineGold;
                }
            }
        }

        public void Reset()
        {
            this.BaselineGold = 0;
            this.CurrentGold = 0;
            this.MapGoldGain = 0;
            this.discoveredOffset = -1;
        }

        public long ReadPlayerGold()
        {
            var area = Core.States.InGameStateObject?.CurrentAreaInstance;
            if (area == null) return 0;

            var serverData = area.ServerDataObject;
            if (serverData == null || serverData.Address == IntPtr.Zero) return 0;

            var reader = Core.Process.Handle;
            if (reader == null) return 0;

            try
            {
                var sData = reader.ReadMemory<ServerDataOffsets>(serverData.Address);
                var playerDataArray = reader.ReadStdVector<IntPtr>(sData.PlayerServerDataPtr);
                if (playerDataArray.Length == 0 || playerDataArray[0] == IntPtr.Zero) return 0;

                IntPtr pServer = playerDataArray[0];

                // 1. If we already have a cached discovered offset, read directly
                if (this.discoveredOffset >= 0)
                {
                    if (this.isLongType)
                    {
                        long val = reader.ReadMemory<long>(pServer + this.discoveredOffset);
                        if (val >= 0 && val <= 2_000_000_000L) return val;
                    }
                    else
                    {
                        int val = reader.ReadMemory<int>(pServer + this.discoveredOffset);
                        if (val >= 0) return (long)val;
                    }

                    // Invalidate if invalid read
                    this.discoveredOffset = -1;
                }

                // 2. Scan candidate offsets
                foreach (int offset in CandidateOffsets)
                {
                    long valLong = reader.ReadMemory<long>(pServer + offset);
                    if (valLong > 0 && valLong <= 2_000_000_000L)
                    {
                        int valInt = reader.ReadMemory<int>(pServer + offset);
                        int valNext = reader.ReadMemory<int>(pServer + offset + 4);

                        // If higher 4 bytes is 0, it's a valid 32-bit/64-bit integer
                        if (valNext == 0 && valInt > 0)
                        {
                            this.discoveredOffset = offset;
                            this.isLongType = false;
                            return (long)valInt;
                        }
                        else if (valLong > 0)
                        {
                            this.discoveredOffset = offset;
                            this.isLongType = true;
                            return valLong;
                        }
                    }
                }
            }
            catch
            {
                // Safety
            }

            return 0;
        }

        public static string FormatGold(long gold)
        {
            if (gold <= 0) return "0";
            if (gold < 1_000)
            {
                return gold.ToString();
            }
            if (gold < 1_000_000)
            {
                double k = gold / 1000.0;
                return k < 10.0 ? $"{k:0.#}k" : $"{k:0}k";
            }
            double m = gold / 1_000_000.0;
            return $"{m:0.0}m";
        }
    }
}
