namespace myFarming
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using TEHhub;
    using TEHhub.Offsets.Natives;
    using TEHhub.Offsets.Objects.States.InGameState;
    using TEHhub.RemoteEnums;
    using TEHhub.RemoteObjects.Components;
    using TEHhub.RemoteObjects.States.InGameStateObjects;

    public sealed class LootDiffEngine
    {
        private static readonly Func<IntPtr, Item>? CreateItemFast = InitItemFactory();

        private static Func<IntPtr, Item>? InitItemFactory()
        {
            try
            {
                var ctor = typeof(Item).GetConstructor(
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    null,
                    new[] { typeof(IntPtr) },
                    null);
                if (ctor == null) return null;
                var param = System.Linq.Expressions.Expression.Parameter(typeof(IntPtr), "addr");
                var newExp = System.Linq.Expressions.Expression.New(ctor, param);
                return System.Linq.Expressions.Expression.Lambda<Func<IntPtr, Item>>(newExp, param).Compile();
            }
            catch
            {
                return null;
            }
        }

        private static Item? ReadFreshItem(IntPtr itemAddress)
        {
            if (itemAddress == IntPtr.Zero) return null;
            try
            {
                if (CreateItemFast != null) return CreateItemFast(itemAddress);
                return Activator.CreateInstance(
                    typeof(Item),
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    null,
                    new object[] { itemAddress },
                    null) as Item;
            }
            catch
            {
                return null;
            }
        }

        private static bool TryReadVector<T>(TEHhub.Utils.SafeMemoryHandle reader, StdVector vec, out T[] result) where T : unmanaged
        {
            result = Array.Empty<T>();
            if (reader == null || reader.IsInvalid) return false;

            long first = vec.First.ToInt64();
            long last = vec.Last.ToInt64();
            long length = last - first;

            // Valid empty vector: First == Last
            if (first == last)
            {
                result = Array.Empty<T>();
                return true;
            }

            // Reject negative length, not divisible by sizeof(T), unreasonable length, or null/invalid First pointer
            int size = Unsafe.SizeOf<T>();
            if (length < 0 || length % size != 0 || length > 50_000_000 || vec.First == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                int count = (int)(length / size);
                var buffer = new T[count];
                if (reader.TryReadMemoryArray(vec.First, buffer, out _))
                {
                    result = buffer;
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private static T[] ReadVector<T>(TEHhub.Utils.SafeMemoryHandle reader, StdVector vec) where T : unmanaged
        {
            return TryReadVector<T>(reader, vec, out var result) ? result : Array.Empty<T>();
        }

        private static Dictionary<string, InvSnapshot> CloneSnapshot(Dictionary<string, InvSnapshot> source)
        {
            var clone = new Dictionary<string, InvSnapshot>(source.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in source)
            {
                var s = kvp.Value;
                clone[kvp.Key] = new InvSnapshot
                {
                    Name = s.Name,
                    UniqueName = s.UniqueName,
                    BaseTypeName = s.BaseTypeName,
                    StackCount = s.StackCount,
                    ChaosEach = s.ChaosEach,
                    Rarity = s.Rarity,
                    IconPath = s.IconPath,
                };
            }
            return clone;
        }

        private Dictionary<string, InvSnapshot> baselineSnapshot = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, InvSnapshot> previousCandidate = new(StringComparer.OrdinalIgnoreCase);
        private bool hasPreviousCandidate = false;
        private List<LootEntry> carryoverLoot = new();
        private List<LootEntry> lastCalculatedLoot = new();
        private float lastTotalChaos = 0f;

        public bool BaselineLocked { get; private set; } = false;
        public bool HasPreviousCandidate => this.hasPreviousCandidate;
        public IReadOnlyDictionary<string, InvSnapshot> BaselineSnapshot => this.baselineSnapshot;
        public string CurrentAreaHash { get; private set; } = string.Empty;
        public string CurrentMapName { get; private set; } = string.Empty;
        public DateTime ZoneEnteredUtc { get; private set; } = DateTime.MinValue;

        public void StartNewMap(string areaHash, string mapName)
        {
            this.CurrentAreaHash = areaHash;
            this.CurrentMapName = mapName;
            this.ZoneEnteredUtc = DateTime.UtcNow;
            this.baselineSnapshot.Clear();
            this.previousCandidate.Clear();
            this.hasPreviousCandidate = false;
            this.carryoverLoot.Clear();
            this.lastCalculatedLoot.Clear();
            this.lastTotalChaos = 0f;
            this.BaselineLocked = false;
        }

        public void ResumeMap(string areaHash, List<LootEntry> previousLoot)
        {
            this.CurrentAreaHash = areaHash;
            this.ZoneEnteredUtc = DateTime.UtcNow;
            this.baselineSnapshot.Clear();
            this.previousCandidate.Clear();
            this.hasPreviousCandidate = false;
            this.carryoverLoot = new List<LootEntry>(previousLoot);
            this.lastCalculatedLoot = new List<LootEntry>(previousLoot);
            this.lastTotalChaos = previousLoot.Sum(l => l.TotalChaos);
            this.BaselineLocked = false;
        }

        public void SuspendMap()
        {
            // Retain state if needed
        }

        public bool TryReadCurrentBackpack(PriceHelper? priceHelper, out Dictionary<string, InvSnapshot> result)
        {
            result = new Dictionary<string, InvSnapshot>(StringComparer.OrdinalIgnoreCase);
            var inGame = Core.States.InGameStateObject;
            if (inGame == null) return false;

            var area = inGame.CurrentAreaInstance;
            if (area == null) return false;

            var serverData = area.ServerDataObject;
            if (serverData == null || serverData.Address == IntPtr.Zero) return false;

            var reader = Core.Process.Handle;
            if (reader == null || reader.IsInvalid) return false;

            try
            {
                var sData = reader.ReadMemory<ServerDataOffsets>(serverData.Address);
                if (!TryReadVector<IntPtr>(reader, sData.PlayerServerDataPtr, out var playerDataArray))
                    return false;

                if (playerDataArray.Length == 0 || playerDataArray[0] == IntPtr.Zero)
                    return false;

                var playerData = reader.ReadMemory<ServerDataStructure>(playerDataArray[0]);
                if (!TryReadVector<InventoryArrayStruct>(reader, playerData.PlayerInventories, out var inventoryData))
                    return false;

                if (inventoryData.Length == 0)
                    return false;

                IntPtr backpackAddr = IntPtr.Zero;
                for (int i = 0; i < inventoryData.Length; i++)
                {
                    if (inventoryData[i].InventoryId == 1) // MainInventory1
                    {
                        backpackAddr = inventoryData[i].InventoryPtr0;
                        break;
                    }
                }

                if (backpackAddr == IntPtr.Zero) return false;

                var invInfo = reader.ReadMemory<InventoryStruct>(backpackAddr);
                if (!TryReadVector<IntPtr>(reader, invInfo.ItemList, out var itemSlotPtrs))
                    return false;

                // Structurally valid inventory reached. If ItemList is empty, snapshot is valid with 0 items.
                if (itemSlotPtrs.Length == 0)
                {
                    return true;
                }

                var distinctSlots = itemSlotPtrs.Distinct();
                foreach (var slotPtr in distinctSlots)
                {
                    if (slotPtr == IntPtr.Zero) continue;
                    var invItem = reader.ReadMemory<InventoryItemStruct>(slotPtr);
                    if (invItem.Item == IntPtr.Zero) continue;

                    var item = ReadFreshItem(invItem.Item);
                    if (item == null || !item.IsValid) continue;

                    string itemName = string.Empty;
                    int rarity = 0;

                    if (item.TryGetComponent<Base>(out var baseComp) && baseComp != null)
                    {
                        itemName = baseComp.BaseItemName?.Trim() ?? string.Empty;
                    }

                    if (item.TryGetComponent<Mods>(out var modsComp) && modsComp != null)
                    {
                        rarity = (int)modsComp.Rarity;
                    }

                    if (string.IsNullOrWhiteSpace(itemName))
                    {
                        var path = item.Path ?? string.Empty;
                        itemName = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
                    }

                    if (string.IsNullOrWhiteSpace(itemName)) continue;

                    int stack = 1;
                    if (item.TryGetComponent<TEHhub.RemoteObjects.Components.Stack>(out var stackComp) && stackComp != null && stackComp.Count > 1)
                    {
                        stack = stackComp.Count;
                    }

                    if (!result.TryGetValue(itemName, out var snap))
                    {
                        float chaos = 0f;
                        string iconStr = string.Empty;
                        if (priceHelper != null)
                        {
                            chaos = priceHelper.LookupPrice(itemName, out var icon);
                            iconStr = icon ?? string.Empty;
                        }

                        snap = new InvSnapshot
                        {
                            Name = itemName,
                            BaseTypeName = itemName,
                            StackCount = stack,
                            ChaosEach = chaos,
                            Rarity = rarity,
                            IconPath = iconStr,
                        };
                        result[itemName] = snap;
                    }
                    else
                    {
                        snap.StackCount += stack;
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        public Dictionary<string, InvSnapshot> ReadCurrentBackpack(PriceHelper priceHelper)
        {
            return this.TryReadCurrentBackpack(priceHelper, out var result)
                ? result
                : new Dictionary<string, InvSnapshot>(StringComparer.OrdinalIgnoreCase);
        }

        public List<LootEntry> Tick(PriceHelper priceHelper, out float totalChaos)
        {
            bool readSuccess = this.TryReadCurrentBackpack(priceHelper, out var current);
            return this.ProcessTick(readSuccess, current, priceHelper, out totalChaos);
        }

        public List<LootEntry> ProcessTick(
            bool readSuccess,
            Dictionary<string, InvSnapshot>? current,
            PriceHelper? priceHelper,
            out float totalChaos)
        {
            if (!readSuccess || current == null)
            {
                // Transient read failure: preserve trusted state without mutating baseline/candidate
                totalChaos = this.lastTotalChaos;
                return this.lastCalculatedLoot;
            }

            // Stability gate: wait until valid baseline is established
            if (!this.BaselineLocked)
            {
                if (current.Count == 0)
                {
                    // Case 2: First successful snapshot is empty -> lock immediately to avoid swallowing future loot
                    this.baselineSnapshot.Clear();
                    this.previousCandidate.Clear();
                    this.hasPreviousCandidate = false;
                    this.BaselineLocked = true;
                }
                else if (this.hasPreviousCandidate && AreSnapshotsEqual(this.previousCandidate, current))
                {
                    // Case 1: First successful snapshot is non-empty -> require 2 identical consecutive scans
                    this.baselineSnapshot = CloneSnapshot(current);
                    this.previousCandidate.Clear();
                    this.hasPreviousCandidate = false;
                    this.BaselineLocked = true;
                }
                else
                {
                    this.previousCandidate = CloneSnapshot(current);
                    this.hasPreviousCandidate = true;
                }

                totalChaos = this.carryoverLoot.Sum(l => l.TotalChaos);
                this.lastTotalChaos = totalChaos;
                this.lastCalculatedLoot = new List<LootEntry>(this.carryoverLoot);
                return this.carryoverLoot;
            }

            // Diff current against baseline
            var merged = new Dictionary<string, LootEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var carry in this.carryoverLoot)
            {
                merged[carry.Name] = new LootEntry
                {
                    Name = carry.Name,
                    StackCount = carry.StackCount,
                    ChaosEach = carry.ChaosEach,
                    Rarity = carry.Rarity,
                    IconPath = carry.IconPath,
                };
            }

            var allNames = new HashSet<string>(current.Keys.Concat(this.baselineSnapshot.Keys), StringComparer.OrdinalIgnoreCase);
            foreach (var name in allNames)
            {
                int curCount = current.TryGetValue(name, out var cs) ? cs.StackCount : 0;
                int baseCount = this.baselineSnapshot.TryGetValue(name, out var bs) ? bs.StackCount : 0;
                int diff = curCount - baseCount;

                float chaosEach = cs?.ChaosEach ?? bs?.ChaosEach ?? (priceHelper != null ? priceHelper.LookupPrice(name, out _) : 0f);
                string iconPath = cs?.IconPath ?? bs?.IconPath ?? string.Empty;
                int rarity = cs?.Rarity ?? bs?.Rarity ?? 0;

                if (diff == 0 && !merged.ContainsKey(name)) continue;

                if (!merged.TryGetValue(name, out var entry))
                {
                    if (diff > 0)
                    {
                        merged[name] = new LootEntry
                        {
                            Name = name,
                            StackCount = diff,
                            ChaosEach = chaosEach,
                            Rarity = rarity,
                            IconPath = iconPath,
                        };
                    }
                }
                else
                {
                    entry.StackCount += diff;
                    if (entry.ChaosEach == 0f) entry.ChaosEach = chaosEach;
                    if (string.IsNullOrEmpty(entry.IconPath)) entry.IconPath = iconPath;
                }
            }

            var outList = merged.Values.Where(v => v.StackCount > 0).OrderByDescending(v => v.TotalChaos).ToList();
            totalChaos = outList.Sum(v => v.TotalChaos);
            this.lastTotalChaos = totalChaos;
            this.lastCalculatedLoot = outList;
            return outList;
        }

        private static bool AreSnapshotsEqual(Dictionary<string, InvSnapshot> a, Dictionary<string, InvSnapshot> b)
        {
            if (a.Count != b.Count) return false;
            foreach (var kvp in a)
            {
                if (!b.TryGetValue(kvp.Key, out var val) || val.StackCount != kvp.Value.StackCount)
                {
                    return false;
                }
            }
            return true;
        }
    }
}
