namespace myFarming
{
    using System;
    using System.Collections.Generic;

    public sealed class InvSnapshot
    {
        public string Name { get; set; } = string.Empty;
        public string UniqueName { get; set; } = string.Empty;
        public string BaseTypeName { get; set; } = string.Empty;
        public int StackCount { get; set; }
        public float ChaosEach { get; set; }
        public int Rarity { get; set; }
        public string IconPath { get; set; } = string.Empty;
    }

    public sealed class LootEntry
    {
        public string Name { get; set; } = string.Empty;
        public int StackCount { get; set; }
        public float ChaosEach { get; set; }
        public float TotalChaos => this.StackCount * this.ChaosEach;
        public int Rarity { get; set; }
        public string IconPath { get; set; } = string.Empty;
    }

    public sealed class MapRun
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string MapName { get; set; } = string.Empty;
        public string AreaHash { get; set; } = string.Empty;
        public long StartedAt { get; set; }
        public string StartedText { get; set; } = string.Empty;
        public int DurationSec { get; set; }
        public float TotalChaos { get; set; }
        public float DivineRate { get; set; } = 9.43f;
        public float ExaltedRate { get; set; } = 1.0f;
        public List<LootEntry> Loot { get; set; } = new();
        public int SessionId { get; set; }
        public int GoldGain { get; set; }
        public int KillsNormal { get; set; }
        public int KillsMagic { get; set; }
        public int KillsRare { get; set; }
        public int KillsUnique { get; set; }
        public int KillsTotal => this.KillsNormal + this.KillsMagic + this.KillsRare + this.KillsUnique;
    }

    public sealed class FarmSession
    {
        public int SessionId { get; set; } = 1;
        public long StartedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        public int TotalDurationSec { get; set; } = 0;
        public int TotalMaps { get; set; } = 0;
        public float TotalChaos { get; set; } = 0f;
        public int KillsNormal { get; set; } = 0;
        public int KillsMagic { get; set; } = 0;
        public int KillsRare { get; set; } = 0;
        public int KillsUnique { get; set; } = 0;
        public int TotalKills => this.KillsNormal + this.KillsMagic + this.KillsRare + this.KillsUnique;
    }
}
