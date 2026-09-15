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

        private static T[] ReadVector<T>(TEHhub.Utils.SafeMemoryHandle reader, StdVector vec) where T : unmanaged
        {
            int size = Unsafe.SizeOf<T>();
            long length = vec.Last.ToInt64() - vec.First.ToInt64();
            if (length <= 0 || length % size != 0 || length > 50_000_000)
            {
                return Array.Empty<T>();
            }
            return reader.ReadMemoryArray<T>(vec.First, (int)(length / size));
        }

        private Dictionary<string, InvSnapshot> baselineSnapshot = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, InvSnapshot> previousCandidate = new(StringComparer.OrdinalIgnoreCase);
        private List<LootEntry> carryoverLoot = new();

        public bool BaselineLocked { get; private set; } = false;
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
            this.carryoverLoot.Clear();
            this.BaselineLocked = false;
        }

        public void ResumeMap(string areaHash, List<LootEntry> previousLoot)
        {
            this.CurrentAreaHash = areaHash;
            this.ZoneEnteredUtc = DateTime.UtcNow;
            this.baselineSnapshot.Clear();
            this.previousCandidate.Clear();
            this.carryoverLoot = new List<LootEntry>(previousLoot);
            this.BaselineLocked = false;
        }

        public void SuspendMap()
        {
            // Retain state if needed
        }

        public Dictionary<string, InvSnapshot> ReadCurrentBackpack(PriceHelper priceHelper)
        {
            var result = new Dictionary<string, InvSnapshot>(StringComparer.OrdinalIgnoreCase);
            var inGame = Core.States.InGameStateObject;
            if (inGame == null) return result;

            var area = inGame.CurrentAreaInstance;
            if (area == null) return result;

            var serverData = area.ServerDataObject;
            if (serverData == null || serverData.Address == IntPtr.Zero) return result;

            var reader = Core.Process.Handle;
            if (reader == null) return result;

            try
            {
                var sData = reader.ReadMemory<ServerDataOffsets>(serverData.Address);
                var playerDataArray = ReadVector<IntPtr>(reader, sData.PlayerServerDataPtr);
                if (playerDataArray.Length == 0 || playerDataArray[0] == IntPtr.Zero) return result;

                var playerData = reader.ReadMemory<ServerDataStructure>(playerDataArray[0]);
                var inventoryData = ReadVector<InventoryArrayStruct>(reader, playerData.PlayerInventories);

                IntPtr backpackAddr = IntPtr.Zero;
                for (int i = 0; i < inventoryData.Length; i++)
                {
                    if (inventoryData[i].InventoryId == 1) // MainInventory1
                    {
                        backpackAddr = inventoryData[i].InventoryPtr0;
                        break;
                    }
                }

                if (backpackAddr == IntPtr.Zero) return result;

                var invInfo = reader.ReadMemory<InventoryStruct>(backpackAddr);
                var itemSlotPtrs = ReadVector<IntPtr>(reader, invInfo.ItemList);
                if (itemSlotPtrs.Length == 0) return result;

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
                        float chaos = priceHelper.LookupPrice(itemName, out var icon);
                        snap = new InvSnapshot
                        {
                            Name = itemName,
                            BaseTypeName = itemName,
                            StackCount = stack,
                            ChaosEach = chaos,
                            Rarity = rarity,
                            IconPath = icon,
                        };
                        result[itemName] = snap;
                    }
                    else
                    {
                        snap.StackCount += stack;
                    }
                }
            }
            catch
            {
                // Memory read safety
            }

            return result;
        }

        public List<LootEntry> Tick(PriceHelper priceHelper, out float totalChaos)
        {
            totalChaos = 0f;
            var current = this.ReadCurrentBackpack(priceHelper);
            if (current.Count == 0)
            {
                return this.carryoverLoot;
            }

            // Stability gate: wait until two consecutive scans match before locking baseline
            if (!this.BaselineLocked)
            {
                if (this.previousCandidate.Count > 0 && AreSnapshotsEqual(this.previousCandidate, current))
                {
                    this.baselineSnapshot = new Dictionary<string, InvSnapshot>(current, StringComparer.OrdinalIgnoreCase);
                    this.BaselineLocked = true;
                }
                else
                {
                    this.previousCandidate = new Dictionary<string, InvSnapshot>(current, StringComparer.OrdinalIgnoreCase);
                }

                totalChaos = this.carryoverLoot.Sum(l => l.TotalChaos);
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

                float chaosEach = cs?.ChaosEach ?? bs?.ChaosEach ?? priceHelper.LookupPrice(name, out _);
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
