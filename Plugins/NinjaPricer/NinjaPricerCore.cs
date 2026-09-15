namespace NinjaPricer
{
    using System;
    using System.Collections;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Numerics;
    using System.Reflection;
    using System.Runtime.InteropServices;
    using System.Text.Json;
    using Coroutine;
    using TEHhub.CoroutineEvents;
    using ImGuiNET;
    using TEHhub;
    using TEHhub.Offsets.Natives;
    using TEHhub.Offsets.Objects.Components;
    using TEHhub.Offsets.Objects.States.InGameState;
    using TEHhub.Offsets.Objects.UiElement;
    using TEHhub.Plugin;
    using TEHhub.RemoteEnums;
    using TEHhub.RemoteEnums.Entity;
    using TEHhub.RemoteObjects.Components;
    using TEHhub.RemoteObjects.States.InGameStateObjects;

    public sealed partial class NinjaPricerCore : PCore<NinjaPricerSettings>
    {
        private const int UiElementItemAddressOffset = 0x4E0;
        private const int UiElementTextOffset = 0x360;
        private const float IconHeightMul = 1.4f;

        private readonly Dictionary<string, string> uniqueArtMapping = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<RuneshapeRowTag> cachedRuneshapeRows = new();
        private DateTime lastRuneshapeScanUtc = DateTime.MinValue;

        private readonly struct ScrollBinding
        {
            public readonly IntPtr HolderAddress;
            public readonly IntPtr ThumbAddress;
            public readonly float ContentHeight;
            public readonly float ScanOffsetY;
            public readonly float ClipTop;
            public readonly float ClipBottom;
            public readonly bool IsActive;

            public ScrollBinding(
                IntPtr holderAddress,
                IntPtr thumbAddress,
                float contentHeight,
                float scanOffsetY,
                float clipTop,
                float clipBottom)
            {
                this.HolderAddress = holderAddress;
                this.ThumbAddress = thumbAddress;
                this.ContentHeight = contentHeight;
                this.ScanOffsetY = scanOffsetY;
                this.ClipTop = clipTop;
                this.ClipBottom = clipBottom;
                this.IsActive = holderAddress != IntPtr.Zero && thumbAddress != IntPtr.Zero && contentHeight > 0f;
            }
        }

        private readonly struct SlotCandidate
        {
            public readonly IntPtr ElementAddress;
            public readonly IntPtr ParentAddress;
            public readonly ScrollBinding Scroll;

            public SlotCandidate(IntPtr elem, IntPtr parent, ScrollBinding scroll)
            {
                this.ElementAddress = elem;
                this.ParentAddress = parent;
                this.Scroll = scroll;
            }
        }

        private sealed class RuneshapeRowTag
        {
            public Vector2 RowPos;
            public Vector2 RowSize;
            public Vector2 ChipPos;
            public string ChipText = string.Empty;
            public float Chaos;
            public float DisplayValue;
            public string IconPath = string.Empty;
            public bool IsBest;
        }

        private class RsRecipe
        {
            public int Row { get; set; }
            public string Id { get; set; } = string.Empty;
            public int Size { get; set; }
            public List<int> RuneIdx { get; set; } = new();
            public List<string> Runes { get; set; } = new();
            public string? Reward { get; set; }
            public int RewardCount { get; set; } = 1;
            public string Description { get; set; } = string.Empty;
            public int Category { get; set; }
            public int MinLevel { get; set; }
            public int MaxLevel { get; set; }
            public int ComboWeight { get; set; }
        }

        private sealed class RsOffer
        {
            public RsRecipe Recipe { get; set; } = null!;
            public string Name { get; set; } = string.Empty;
            public int Count { get; set; } = 1;
            public bool IsPriced { get; set; }
            public float TotalChaos { get; set; }
            public float DisplayPrice { get; set; }
            public int ComboWeight { get; set; }
            public string IconPath { get; set; } = string.Empty;
        }

        private readonly List<RsRecipe> runeshapeRecipes = new();
        private readonly Dictionary<long, int> partialMinLevel = new();
        private string rsSearchFilter = string.Empty;

        private static readonly uint[] RsMonolithColors = new uint[]
        {
            0xFF3333E5, // 0: Red
            0xFF33D5E5, // 1: Yellow
            0xFF33E533, // 2: Green
            0xFFE5D533, // 3: Cyan
            0xFF3366E5, // 4: Orange
            0xFFD533D5, // 5: Magenta
            0xFFE58833, // 6: Sky Blue
            0xFF8833E5  // 7: Violet
        };

        private static readonly Dictionary<string, int> DefaultRuneWeights = new(StringComparer.OrdinalIgnoreCase)
        {
            { "Opulent", 500 },
            { "Bond", 120 },
            { "Oath", 110 },
            { "Power", 100 },
            { "Death", 100 },
            { "Time", 80 },
            { "Rebirth", 60 },
            { "Prismatic", 30 },
            { "Arcane", 30 },
            { "Soul", 25 },
            { "Celestial", 25 },
            { "Vision", 25 },
            { "Wisdom", 25 },
            { "Rage", 25 },
            { "Protective", 20 },
        };

        private static int CalculateRecipeWeight(IEnumerable<string> runes)
        {
            int total = 0;
            foreach (var r in runes)
            {
                if (DefaultRuneWeights.TryGetValue(r, out var w)) total += w;
                else total += 20;
            }
            return total;
        }

        private bool IsPartialAllowed(int runeIdx, int pos0Based, int recipeSize, int areaLevel)
        {
            long key = ((long)runeIdx << 16) | ((long)(pos0Based + 1) << 8) | (uint)recipeSize;
            if (!this.partialMinLevel.TryGetValue(key, out var minL)) return false;
            return areaLevel <= 0 || areaLevel >= minL;
        }

        private NinjaPriceService? priceService;
        private ActiveCoroutine? onAreaChangeCoroutine;
        private string SettingPathname => this.PluginConfigPath("settings.json");

        // Texture caches
        private struct CurrencyTex
        {
            public IntPtr Ptr;
            public uint W;
            public uint H;
            public bool Valid;
        }

        private readonly CurrencyTex[] currencyTextures = new CurrencyTex[3];
        private readonly bool[] currencyTexTried = new bool[3];
        private readonly CurrencyTex[] runeTextures = new CurrencyTex[34];
        private readonly bool[] runeTexTried = new bool[34];
        private CurrencyTex runeBgRegularTex = default;
        private bool runeBgRegularTried = false;
        private CurrencyTex runeBgPurpleTex = default;
        private bool runeBgPurpleTried = false;
        private CurrencyTex runePropagationTex = default;
        private bool runePropagationTried = false;
        private readonly ConcurrentDictionary<string, CurrencyTex> itemTextures = new(StringComparer.OrdinalIgnoreCase);

        // Runtime states & caches
        private DateTime lastGroundScanUtc = DateTime.MinValue;
        private DateTime lastInvScanUtc = DateTime.MinValue;
        private DateTime lastAutoRefreshUtc = DateTime.MinValue;
        private int? captureTarget;
        private bool runeshapeWinHotkeyWasDown = false;
        private Vector2 runeshapeWinRectMin = Vector2.Zero;
        private Vector2 runeshapeWinRectMax = Vector2.Zero;
        private bool runeshapeWinRectValid = false;

        // Ground tags cache
        private struct GroundTag
        {
            public Vector3 WorldPos;
            public float DisplayValue;
            public float Chaos;
            public string IconPath;
        }
        private readonly List<GroundTag> cachedGroundTags = new();

        // Inventory / Stash slots cache
        private struct SlotTag
        {
            public Vector2 Pos;
            public Vector2 Size;
            public float DisplayValue;
            public float Chaos;
            public string IconPath;
        }
        private List<SlotTag> cachedInvSlots = new();
        private List<SlotTag> cachedStashSlots = new();
        private volatile bool isInvScanRunning = false;

        private sealed class CachedSlotItem
        {
            public string ItemName = string.Empty;
            public int StackCount = 1;
            public bool IsPriced;
            public PriceResult Price;
            public long ExpireUtcMs;
        }
        private readonly Dictionary<IntPtr, CachedSlotItem> itemSlotCache = new();

        // Performance diagnostics
        private double perfScanCallMs = 0.0;
        private double perfGetAllMs = 0.0;
        private double perfDrawInvMs = 0.0;
        private double perfGroundMs = 0.0;
        private double perfLookupMs = 0.0;
        private double perfPeakScanCallMs = 0.0;
        private double perfPeakGetAllMs = 0.0;
        private double perfPeakDrawInvMs = 0.0;
        private double perfPeakGroundMs = 0.0;
        private double perfPeakLookupMs = 0.0;
        private int perfLookupExact = 0;
        private int perfLookupMiss = 0;

        [LibraryImport("user32.dll")]
        private static partial short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll", CharSet = CharSet.Ansi)]
        private static extern uint MapVirtualKeyA(uint uCode, uint uMapType);

        [DllImport("user32.dll", CharSet = CharSet.Ansi)]
        private static extern int GetKeyNameTextA(int lParam, byte[] lpString, int nSize);

        public override void OnEnable(bool isGameOpened)
        {
            if (File.Exists(this.SettingPathname))
            {
                try
                {
                    var content = File.ReadAllText(this.SettingPathname);
                    this.Settings = JsonSerializer.Deserialize<NinjaPricerSettings>(content) ?? new NinjaPricerSettings();
                }
                catch
                {
                    this.Settings = new NinjaPricerSettings();
                }
            }

            this.priceService = new NinjaPriceService(this.PluginConfigDirectory);
            this.priceService.TriggerRefresh(this.Settings.League, this.Settings.PriceSource, this.Settings.EnabledCategories);
            this.lastAutoRefreshUtc = DateTime.UtcNow;

            this.LoadUniqueArtMapping();
            this.LoadRuneshapeRecipes();

            this.onAreaChangeCoroutine = CoroutineHandler.Start(this.OnAreaChange());
        }

        private void LoadUniqueArtMapping()
        {
            this.uniqueArtMapping.Clear();
            string[] paths =
            {
                Path.Combine(this.DllDirectory, "uniqueArtMapping.json"),
                Path.Combine(AppContext.BaseDirectory, "Plugins", "NinjaPricer", "uniqueArtMapping.json"),
                Path.Combine(AppContext.BaseDirectory, "Plugins", "LootValue", "uniqueArtMapping.json"),
            };

            foreach (var p in paths)
            {
                if (File.Exists(p))
                {
                    try
                    {
                        var json = File.ReadAllText(p);
                        var raw = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json);
                        if (raw != null)
                        {
                            foreach (var (artPath, names) in raw)
                            {
                                if (names == null || names.Count == 0 || string.IsNullOrWhiteSpace(names[0])) continue;
                                var name = names[0].Trim();
                                this.uniqueArtMapping[artPath.Trim()] = name;

                                var fn = Path.GetFileNameWithoutExtension(artPath);
                                if (!string.IsNullOrWhiteSpace(fn))
                                {
                                    this.uniqueArtMapping[fn.Trim()] = name;
                                }
                            }
                            PluginLog.Info("NinjaPricer", $"[NinjaPricer] Loaded {this.uniqueArtMapping.Count} unique art mappings from '{p}'");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        PluginLog.Error("NinjaPricer", $"[NinjaPricer] Failed reading uniqueArtMapping from '{p}': {ex.Message}");
                    }
                }
            }
        }

        private void LoadRuneshapeRecipes()
        {
            this.runeshapeRecipes.Clear();
            this.partialMinLevel.Clear();
            string[] paths =
            {
                Path.Combine(this.DllDirectory, "expedition2_recipes.json"),
                Path.Combine(AppContext.BaseDirectory, "Plugins", "NinjaPricer", "expedition2_recipes.json"),
                Path.Combine(AppContext.BaseDirectory, "Plugins", "ExpeditionPlanner", "expedition2_recipes.json"),
                Path.Combine(AppContext.BaseDirectory, "Plugins", "LootValue", "expedition2_recipes.json"),
                Path.Combine(AppContext.BaseDirectory, "resources", "runeshape", "expedition2_recipes.json"),
            };

            foreach (var p in paths)
            {
                if (File.Exists(p))
                {
                    try
                    {
                        var json = File.ReadAllText(p);
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("recipes", out var recipesElem) && recipesElem.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in recipesElem.EnumerateArray())
                            {
                                var r = new RsRecipe();
                                if (item.TryGetProperty("id", out var idProp)) r.Id = idProp.GetString() ?? string.Empty;
                                if (item.TryGetProperty("size", out var sProp)) r.Size = sProp.GetInt32();
                                if (item.TryGetProperty("description", out var dProp)) r.Description = dProp.GetString() ?? string.Empty;
                                if (item.TryGetProperty("reward", out var rwProp))
                                {
                                    if (rwProp.ValueKind == JsonValueKind.String)
                                    {
                                        r.Reward = rwProp.GetString();
                                    }
                                    else if (rwProp.ValueKind == JsonValueKind.Object)
                                    {
                                        if (rwProp.TryGetProperty("name", out var nProp))
                                        {
                                            r.Reward = nProp.GetString();
                                        }
                                    }
                                }
                                if (item.TryGetProperty("rewardCount", out var rcProp)) r.RewardCount = Math.Max(1, rcProp.GetInt32());
                                if (item.TryGetProperty("category", out var cProp)) r.Category = cProp.GetInt32();
                                if (item.TryGetProperty("minLevel", out var mlProp)) r.MinLevel = mlProp.GetInt32();
                                if (item.TryGetProperty("maxLevel", out var xlProp)) r.MaxLevel = xlProp.GetInt32();
                                if (item.TryGetProperty("runeIdx", out var rIdxElem) && rIdxElem.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var idx in rIdxElem.EnumerateArray())
                                    {
                                        r.RuneIdx.Add(idx.GetInt32());
                                    }
                                }
                                if (item.TryGetProperty("runes", out var runesElem) && runesElem.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var rune in runesElem.EnumerateArray())
                                    {
                                        var rName = rune.GetString();
                                        if (!string.IsNullOrEmpty(rName)) r.Runes.Add(rName);
                                    }
                                }
                                r.ComboWeight = CalculateRecipeWeight(r.Runes);
                                this.runeshapeRecipes.Add(r);
                            }
                        }

                        if (doc.RootElement.TryGetProperty("runeWeights", out var rwElem) && rwElem.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var w in rwElem.EnumerateArray())
                            {
                                int r = w.GetProperty("rune").GetInt32();
                                int pos = w.GetProperty("pos").GetInt32();
                                int size = w.GetProperty("size").GetInt32();
                                int ml = w.GetProperty("minLevel").GetInt32();
                                long key = ((long)r << 16) | ((long)pos << 8) | (uint)size;
                                if (!this.partialMinLevel.TryGetValue(key, out var cur) || ml < cur)
                                {
                                    this.partialMinLevel[key] = ml;
                                }
                            }
                        }

                        if (this.runeshapeRecipes.Count > 0)
                        {
                            PluginLog.Info("NinjaPricer", $"[NinjaPricer] Loaded {this.runeshapeRecipes.Count} Runeshape recipes and {this.partialMinLevel.Count} runeWeights from '{p}'");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        PluginLog.Error("NinjaPricer", $"[NinjaPricer] Failed reading recipes from '{p}': {ex.Message}");
                    }
                }
            }
        }

        public override void OnDisable()
        {
            this.onAreaChangeCoroutine?.Cancel();
            this.onAreaChangeCoroutine = null;

            this.priceService?.Dispose();
            this.priceService = null;
            this.cachedGroundTags.Clear();
            this.cachedInvSlots.Clear();
            this.cachedStashSlots.Clear();
            for (int i = 0; i < 3; i++)
            {
                this.currencyTextures[i] = default;
                this.currencyTexTried[i] = false;
            }
            for (int i = 0; i < 34; i++)
            {
                this.runeTextures[i] = default;
                this.runeTexTried[i] = false;
            }
            this.runeBgRegularTex = default;
            this.runeBgRegularTried = false;
            this.runeBgPurpleTex = default;
            this.runeBgPurpleTried = false;
            this.runePropagationTex = default;
            this.runePropagationTried = false;
            this.itemTextures.Clear();
        }

        private IEnumerator<Wait> OnAreaChange()
        {
            while (true)
            {
                yield return new Wait(RemoteEvents.AreaChanged);
                this.cachedGroundTags.Clear();
                this.cachedInvSlots.Clear();
                this.cachedStashSlots.Clear();
                this.itemSlotCache.Clear();
                this.lastGroundScanUtc = DateTime.MinValue;
                this.lastInvScanUtc = DateTime.MinValue;
            }
        }

        public override void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(this.SettingPathname) ?? string.Empty);
                var json = JsonSerializer.Serialize(this.Settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(this.SettingPathname, json);
            }
            catch
            {
                // Ignored
            }
        }

        private static bool IsGameWindowFocused()
        {
            return Core.Process.Foreground;
        }

        private static string GetVkName(int vk)
        {
            if (vk == 0) return "None";
            uint scanCode = MapVirtualKeyA((uint)vk, 0);
            byte[] buf = new byte[64];
            if (GetKeyNameTextA((int)(scanCode << 16), buf, buf.Length) > 0)
            {
                var str = System.Text.Encoding.Default.GetString(buf).TrimEnd('\0');
                if (!string.IsNullOrEmpty(str))
                    return $"{str} (0x{vk:X2})";
            }
            return $"Key 0x{vk:X2}";
        }

        private string? ResolveResourcePath(string relativePath)
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "resources", relativePath),
                Path.Combine(AppContext.BaseDirectory, relativePath),
                Path.Combine(this.DllDirectory, "resources", relativePath),
                Path.Combine(@"C:\Games\Hy-v Tool\GameHelper2-main\resources", relativePath),
                Path.Combine(@"C:\Games\Hy-v Tool\DXPEOE\TEHhub\resources", relativePath),
            };

            foreach (var c in candidates)
            {
                if (File.Exists(c)) return c;
            }

            return null;
        }

        private CurrencyTex? GetCurrencyTexture(DisplayCurrency c)
        {
            int idx = (int)c;
            if (idx < 0 || idx > 2) return null;

            if (!this.currencyTexTried[idx])
            {
                this.currencyTexTried[idx] = true;
                string[] files = { "divine.png", "exalted.png", "chaos.png" };
                var path = this.ResolveResourcePath(Path.Combine("currency", "poe2", files[idx]))
                           ?? this.ResolveResourcePath(Path.Combine("images", "currency", files[idx]));

                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    Core.Overlay.AddOrGetImagePointer(path, false, out var ptr, out var w, out var h);
                    if (ptr != IntPtr.Zero)
                    {
                        this.currencyTextures[idx] = new CurrencyTex { Ptr = ptr, W = w, H = h, Valid = true };
                    }
                }
            }

            return this.currencyTextures[idx].Valid ? this.currencyTextures[idx] : null;
        }

        private CurrencyTex? GetItemTexture(string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            if (this.itemTextures.TryGetValue(path, out var cached))
            {
                return cached.Valid ? cached : null;
            }

            var resolved = this.ResolveResourcePath(path) ?? (File.Exists(path) ? path : null);
            if (!string.IsNullOrEmpty(resolved) && File.Exists(resolved))
            {
                Core.Overlay.AddOrGetImagePointer(resolved, false, out var ptr, out var w, out var h);
                var tex = new CurrencyTex { Ptr = ptr, W = w, H = h, Valid = ptr != IntPtr.Zero };
                this.itemTextures[path] = tex;
                return tex.Valid ? tex : null;
            }

            this.itemTextures[path] = default;
            return null;
        }

        private CurrencyTex? GetRuneTexture(int runeIdx)
        {
            if (runeIdx < 0 || runeIdx >= 34) return null;
            if (!this.runeTexTried[runeIdx])
            {
                this.runeTexTried[runeIdx] = true;
                string runeName = NinjaRuneshapeHelper.RuneNames[runeIdx];
                string? path = this.ResolveResourcePath(Path.Combine("runeshape", "runes", $"{runeName}.png"));
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    Core.Overlay.AddOrGetImagePointer(path, false, out var ptr, out var w, out var h);
                    if (ptr != IntPtr.Zero)
                    {
                        this.runeTextures[runeIdx] = new CurrencyTex { Ptr = ptr, W = w, H = h, Valid = true };
                    }
                }
            }
            return this.runeTextures[runeIdx].Valid ? this.runeTextures[runeIdx] : null;
        }

        private CurrencyTex? GetRuneUiTexture(string name, ref CurrencyTex tex, ref bool tried)
        {
            if (!tried)
            {
                tried = true;
                string? path = this.ResolveResourcePath(Path.Combine("runeshape", "ui", name));
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    Core.Overlay.AddOrGetImagePointer(path, false, out var ptr, out var w, out var h);
                    if (ptr != IntPtr.Zero)
                    {
                        tex = new CurrencyTex { Ptr = ptr, W = w, H = h, Valid = true };
                    }
                }
            }
            return tex.Valid ? tex : null;
        }

        // ========================================================================
        // Item & UI Slot Helpers
        // ========================================================================

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
                if (CreateItemFast != null)
                {
                    return CreateItemFast(itemAddress);
                }
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

        private static string ExtractArtBasename(string? artPath)
        {
            if (string.IsNullOrWhiteSpace(artPath)) return string.Empty;
            var slash = artPath.LastIndexOfAny(new[] { '/', '\\' });
            var file = slash >= 0 && slash < artPath.Length - 1 ? artPath[(slash + 1)..] : artPath;
            var dot = file.LastIndexOf('.');
            return dot > 0 ? file[..dot] : file;
        }

        private string ResolveItemName(Item item, out int stackCount)
        {
            stackCount = item.TryGetComponent<TEHhub.RemoteObjects.Components.Stack>(out var s) && s.Count > 1 ? s.Count : 1;
            var baseName = item.TryGetComponent<Base>(out var b) ? b.BaseItemName?.Trim() ?? string.Empty : string.Empty;
            if (baseName.EndsWith("Tablet", StringComparison.OrdinalIgnoreCase) ||
                (item.Path != null && item.Path.Contains("Tablet", StringComparison.OrdinalIgnoreCase)))
            {
                stackCount = 1;
            }
            var rarity = item.TryGetComponent<Mods>(out var m) ? m.Rarity : Rarity.Normal;

            if (rarity == Rarity.Unique)
            {
                var artPath = item.TryGetComponent<RenderItem>(out var ri) ? ri.ResourcePath : string.Empty;
                if (!string.IsNullOrEmpty(artPath) && this.uniqueArtMapping.TryGetValue(artPath, out var uPath))
                {
                    return uPath;
                }

                var artBasename = ExtractArtBasename(artPath);
                if (!string.IsNullOrEmpty(artBasename))
                {
                    if (this.uniqueArtMapping.TryGetValue(artBasename, out var u1)) return u1;
                    if (this.priceService != null && this.priceService.TryLookupPrice(artBasename, out _)) return artBasename;

                    if (artBasename.StartsWith("The", StringComparison.OrdinalIgnoreCase) && artBasename.Length > 3)
                    {
                        var trim = artBasename[3..];
                        if (this.uniqueArtMapping.TryGetValue(trim, out var u2)) return u2;
                        if (this.priceService != null && this.priceService.TryLookupPrice(trim, out _)) return trim;
                    }
                    else
                    {
                        var addThe = "The" + artBasename;
                        if (this.uniqueArtMapping.TryGetValue(addThe, out var u3)) return u3;
                        if (this.priceService != null && this.priceService.TryLookupPrice(addThe, out _)) return addThe;
                    }
                }
            }

            if (!string.IsNullOrEmpty(baseName)) return baseName;

            var fullPath = item.Path ?? string.Empty;
            if (fullPath.Contains('/')) return fullPath[(fullPath.LastIndexOf('/') + 1)..];
            return fullPath;
        }

        private bool TryGetScrollContainer(
            IntPtr[] children,
            out IntPtr itemsAddress,
            out ScrollBinding scroll)
        {
            itemsAddress = IntPtr.Zero;
            scroll = default;
            var handle = Core.Process?.Handle;
            if (children.Length <= 2 || children[1] == IntPtr.Zero || children[2] == IntPtr.Zero || handle == null) return false;

            var contentAddress = children[1];
            var holderAddress = children[2];
            if (!handle.TryReadMemory<UiElementBaseOffset>(holderAddress, out var holderOffset) ||
                !UiElementBaseFuncs.IsVisibleChecker(holderOffset.Flags)) return false;

            var holderChildren = handle.ReadStdVector<IntPtr>(holderOffset.ChildrensPtr);
            if (holderChildren.Length == 0 || holderChildren[0] == IntPtr.Zero) return false;

            var thumbAddress = holderChildren[0];
            if (!PluginUiElementReflection.TryGetAbsoluteRect(contentAddress, out var contentPosition, out var contentSize) ||
                !PluginUiElementReflection.TryGetAbsoluteRect(holderAddress, out var holderPosition, out var holderSize) ||
                !PluginUiElementReflection.TryGetAbsoluteRect(thumbAddress, out var thumbPosition, out var thumbSize)) return false;

            if (holderSize.X < 4f || holderSize.X > 64f || holderSize.Y < 40f ||
                thumbSize.X < 2f || thumbSize.X > holderSize.X * 1.5f ||
                thumbSize.Y < 8f || thumbSize.Y >= holderSize.Y ||
                contentSize.Y <= holderSize.Y + 1f || holderPosition.X < contentPosition.X ||
                thumbPosition.Y < holderPosition.Y - 2f ||
                thumbPosition.Y + thumbSize.Y > holderPosition.Y + holderSize.Y + 2f) return false;

            var thumbTravel = holderSize.Y - thumbSize.Y;
            var contentOverflow = contentSize.Y - holderSize.Y;
            if (thumbTravel <= 0f || contentOverflow <= 0f) return false;

            var progress = Math.Clamp((thumbPosition.Y - holderPosition.Y) / thumbTravel, 0f, 1f);
            itemsAddress = contentAddress;
            scroll = new ScrollBinding(
                holderAddress,
                thumbAddress,
                contentSize.Y,
                progress * contentOverflow,
                holderPosition.Y,
                holderPosition.Y + holderSize.Y);
            return float.IsFinite(scroll.ScanOffsetY) && scroll.ClipBottom > scroll.ClipTop;
        }

        private static bool TryGetSlotRect(SlotCandidate candidate, out Vector2 position, out Vector2 size)
        {
            if (!PluginUiElementReflection.TryGetAbsoluteRect(candidate.ElementAddress, out position, out size)) return false;

            if (candidate.ParentAddress != IntPtr.Zero &&
                PluginUiElementReflection.TryGetAbsoluteRect(candidate.ParentAddress, out var parentPosition, out var parentSize) &&
                parentSize.X >= 20f && parentSize.Y >= 20f &&
                ((parentSize.X <= 160f && parentSize.Y <= 256f) ||
                 (parentSize.X <= 256f && parentSize.Y <= 160f)))
            {
                position = parentPosition;
                size = parentSize;
            }

            position.Y -= candidate.Scroll.ScanOffsetY;
            return true;
        }

        private string ReadUiElementText(IntPtr elem)
        {
            if (elem == IntPtr.Zero) return string.Empty;
            var handle = Core.Process?.Handle;
            if (handle == null) return string.Empty;

            try
            {
                var ws = handle.ReadMemory<StdWString>(elem + UiElementTextOffset);
                return handle.ReadStdWString(ws);
            }
            catch
            {
                return string.Empty;
            }
        }

        // ========================================================================
        // Price Formatting Helpers
        // ========================================================================

        private static string GetCurrencySuffix(DisplayCurrency c) => c switch
        {
            DisplayCurrency.Divine => "D",
            DisplayCurrency.Exalted => "E",
            DisplayCurrency.Chaos => "C",
            _ => "?",
        };

        private static string FormatPriceLocal(float value, DisplayCurrency currency)
        {
            var suffix = GetCurrencySuffix(currency);
            if (value >= 10000.0f)
            {
                float k = value / 1000.0f;
                return k >= 10.0f ? $"{k:F0}k {suffix}" : $"{k:F1}k {suffix}";
            }
            if (value >= 1000.0f) return $"{value / 1000.0f:F1}k {suffix}";
            if (value >= 100.0f) return $"{value:F0} {suffix}";
            if (value >= 1.0f) return $"{value:F1} {suffix}";
            if (value >= 0.01f) return $"{value:F2} {suffix}";
            return $"{value:F3} {suffix}";
        }

        private static string FormatPriceNumberLocal(float value)
        {
            if (value >= 10000.0f)
            {
                float k = value / 1000.0f;
                return k >= 10.0f ? $"{k:F0}k" : $"{k:F1}k";
            }
            if (value >= 1000.0f) return $"{value / 1000.0f:F1}k";
            if (value >= 100.0f) return $"{value:F0}";
            if (value >= 1.0f) return $"{value:F1}";
            if (value >= 0.01f) return $"{value:F2}";
            return $"{value:F3}";
        }

        private static uint GetPriceColorLocal(float chaosValue, float divineInChaos)
        {
            float divEquiv = (divineInChaos > 0) ? (chaosValue / divineInChaos) : 0.0f;
            if (divEquiv >= 1.0f) return 0xFF00D7FF; // Gold (RGBA in ImGui)
            if (divEquiv >= 0.1f) return 0xFFFFFFFF; // White
            return 0xFFB4B4B4;                       // Gray
        }

        private ref struct PriceTag
        {
            public string Text;
            public float TextW;
            public float TextH;
            public CurrencyTex? Tex;
            public float IconW;
            public float IconH;
            public float Gap;
            public float TotalW;
            public float TotalH;
            public CurrencyTex? ItemTex;
            public float ItemIconW;
            public float ItemIconH;
            public float ItemGap;
        }

        private PriceTag MeasurePriceTag(float displayValue, float fontSize, string iconPath = "", float maxWidth = 0.0f)
        {
            var tag = default(PriceTag);
            float refSize = ImGui.GetFontSize();
            float scale = (refSize > 0.0f) ? (fontSize / refSize) : 1.0f;

            var tex = (this.Settings.PriceDisplayStyle == PriceDisplayStyle.Image)
                ? this.GetCurrencyTexture(this.Settings.DisplayCurrency)
                : null;

            if (tex.HasValue && tex.Value.Valid)
            {
                tag.Text = FormatPriceNumberLocal(displayValue);
                var ts = ImGui.CalcTextSize(tag.Text);
                tag.TextW = ts.X * scale;
                tag.TextH = ts.Y * scale;
                tag.Tex = tex;
                tag.IconH = tag.TextH * IconHeightMul;
                float aspect = (tex.Value.H > 0) ? ((float)tex.Value.W / tex.Value.H) : 1.0f;
                tag.IconW = tag.IconH * aspect;
                tag.Gap = fontSize * 0.15f;
                tag.TotalW = tag.TextW + tag.Gap + tag.IconW;
                tag.TotalH = tag.IconH;
            }
            else
            {
                tag.Text = FormatPriceLocal(displayValue, this.Settings.DisplayCurrency);
                var ts = ImGui.CalcTextSize(tag.Text);
                tag.TextW = ts.X * scale;
                tag.TextH = ts.Y * scale;
                tag.TotalW = tag.TextW;
                tag.TotalH = tag.TextH;
            }

            if (!string.IsNullOrEmpty(iconPath) && this.Settings.ShowItemIcons)
            {
                var itex = this.GetItemTexture(iconPath);
                if (itex.HasValue && itex.Value.Valid)
                {
                    float ih = tag.TotalH;
                    float aspect = (itex.Value.H > 0) ? ((float)itex.Value.W / itex.Value.H) : 1.0f;
                    float iw = ih * aspect;
                    float ig = fontSize * 0.15f;
                    float candidate = iw + ig + tag.TotalW;
                    if (maxWidth <= 0.0f || candidate <= maxWidth)
                    {
                        tag.ItemTex = itex;
                        tag.ItemIconW = iw;
                        tag.ItemIconH = ih;
                        tag.ItemGap = ig;
                        tag.TotalW = candidate;
                    }
                }
            }

            return tag;
        }

        private void DrawPriceTag(ImDrawListPtr dl, float fontSize, float x, float y, in PriceTag tag, float chaosValue)
        {
            float pad = 2.0f;
            dl.AddRectFilled(
                new Vector2(x - pad, y - pad),
                new Vector2(x + tag.TotalW + pad, y + tag.TotalH + pad),
                0xC8000000, 2.0f); // 200 alpha black

            float cursorX = x;
            if (tag.ItemTex.HasValue && tag.ItemTex.Value.Valid)
            {
                float iy = y + ((tag.TotalH - tag.ItemIconH) * 0.5f);
                dl.AddImage(tag.ItemTex.Value.Ptr,
                    new Vector2(cursorX, iy),
                    new Vector2(cursorX + tag.ItemIconW, iy + tag.ItemIconH));
                cursorX += tag.ItemIconW + tag.ItemGap;
            }

            var col = GetPriceColorLocal(chaosValue, this.priceService?.DivineInChaos ?? 1.0f);
            if (tag.Tex.HasValue && tag.Tex.Value.Valid)
            {
                float textY = y + ((tag.TotalH - tag.TextH) * 0.5f);
                dl.AddText(ImGui.GetFont(), fontSize, new Vector2(cursorX, textY), col, tag.Text);
                float iconX = cursorX + tag.TextW + tag.Gap;
                float iconY = y + ((tag.TotalH - tag.IconH) * 0.5f);
                dl.AddImage(tag.Tex.Value.Ptr,
                    new Vector2(iconX, iconY),
                    new Vector2(iconX + tag.IconW, iconY + tag.IconH));
            }
            else
            {
                float textY = tag.ItemTex.HasValue ? (y + ((tag.TotalH - tag.TextH) * 0.5f)) : y;
                dl.AddText(ImGui.GetFont(), fontSize, new Vector2(cursorX, textY), col, tag.Text);
            }
        }

        private static float ComputeAdaptiveFontSize(float baseFontSize, float cellW, float cellH)
        {
            float minDim = Math.Min(cellW, cellH);
            float maxTextH = minDim * 0.35f;
            if (baseFontSize > maxTextH && maxTextH > 6.0f)
                return maxTextH;
            return baseFontSize;
        }

        // ========================================================================
        // Settings UI
        // ========================================================================

        public override void DrawSettings()
        {
            if (!ImGui.BeginTabBar("##NinjaPricerTabs")) return;

            if (ImGui.BeginTabItem("Data Source"))
            {
                this.DrawTabDataSource();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Categories"))
            {
                this.DrawTabCategories();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Display Settings"))
            {
                this.DrawTabDisplaySettings();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Overlay Toggles"))
            {
                this.DrawTabOverlayToggles();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Debug"))
            {
                this.DrawDebugPanel();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        private void DrawTabDataSource()
        {
            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.85f, 0.2f, 1.0f));
            ImGui.TextWrapped("Warning: POE2 must be set to English. Item names are matched in English only - other languages will not work.");
            ImGui.PopStyleColor();
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            var st = this.priceService?.GetStatus() ?? default;
            ImGui.Text($"Prices: {(st.Loaded ? "loaded" : "loading...")}   Items: {st.TotalItems}");

            if (st.Loaded)
            {
                ImGui.Text($"Rates: 1 Divine = {st.DivineInChaos:F1} Chaos | 1 Exalted = {st.ExaltedInChaos:F1} Chaos");
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            // League input
            var league = this.Settings.League;
            ImGui.SetNextItemWidth(200f);
            if (ImGui.InputText("League", ref league, 64))
            {
                this.Settings.League = league;
                this.SaveSettings();
            }

            ImGui.SameLine();
            if (ImGui.Button("Refresh Now"))
            {
                this.priceService?.TriggerRefresh(this.Settings.League, this.Settings.PriceSource, this.Settings.EnabledCategories);
            }

            // Price source
            ImGui.Spacing();
            ImGui.Text("Data Source:");
            int ps = this.Settings.PriceSource;
            if (ImGui.RadioButton("poe2scout", ref ps, 0))
            {
                this.Settings.PriceSource = 0;
                this.SaveSettings();
                this.priceService?.TriggerRefresh(this.Settings.League, this.Settings.PriceSource, this.Settings.EnabledCategories);
            }
            ImGui.SameLine();
            if (ImGui.RadioButton("poe.ninja", ref ps, 1))
            {
                this.Settings.PriceSource = 1;
                this.SaveSettings();
                this.priceService?.TriggerRefresh(this.Settings.League, this.Settings.PriceSource, this.Settings.EnabledCategories);
            }

            // Auto-refresh slider
            ImGui.Spacing();
            int arm = this.Settings.AutoRefreshMinutes;
            if (ImGui.SliderInt("Auto-refresh (minutes)", ref arm, 5, 60))
            {
                this.Settings.AutoRefreshMinutes = arm;
                this.SaveSettings();
            }
        }

        private void DrawTabCategories()
        {
            ImGui.Spacing();
            ImGui.Text("Select which item categories to fetch and display prices for:");
            ImGui.Spacing();

            if (ImGui.Button("Enable All"))
            {
                foreach (var k in this.Settings.EnabledCategories.Keys.ToList())
                {
                    this.Settings.EnabledCategories[k] = true;
                }
                this.SaveSettings();
            }
            ImGui.SameLine();
            if (ImGui.Button("Disable All"))
            {
                foreach (var k in this.Settings.EnabledCategories.Keys.ToList())
                {
                    this.Settings.EnabledCategories[k] = false;
                }
                this.SaveSettings();
            }
            ImGui.SameLine();
            if (ImGui.Button("Apply & Refresh Now"))
            {
                this.priceService?.TriggerRefresh(this.Settings.League, this.Settings.PriceSource, this.Settings.EnabledCategories);
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            (string Key, string Label)[] currencyCats =
            {
                ("currency", "Currency"),
                ("fragments", "Fragments"),
                ("uncutgems", "Uncut Gems"),
                ("essences", "Essences"),
                ("soulcores", "Soul Cores"),
                ("idols", "Idols"),
                ("runes", "Runes"),
                ("expedition", "Expedition"),
                ("verisium", "Verisium"),
                ("ritual", "Ritual"),
                ("delirium", "Delirium"),
                ("breach", "Breach"),
                ("abyss", "Abyss"),
                ("lineagesupportgems", "Lineage Support Gems"),
                ("vaultkeys", "Vault Keys"),
                ("incursion", "Incursion"),
                ("ultimatum", "Ultimatum"),
                ("vaal", "Vaal"),
            };

            (string Key, string Label)[] uniqueCats =
            {
                ("weapon", "Unique Weapons"),
                ("armour", "Unique Armour"),
                ("accessory", "Unique Accessories & Charms"),
                ("flask", "Unique Flasks"),
                ("jewel", "Unique Jewels"),
                ("map", "Waystones & Tablets"),
                ("sanctum", "Sanctum Research"),
            };

            ImGui.Columns(2, "##CategoryCols", true);

            ImGui.TextColored(new Vector4(0.4f, 0.85f, 1.0f, 1.0f), "Currency & Bulk Categories");
            ImGui.Separator();
            foreach (var (key, label) in currencyCats)
            {
                bool isEnabled = !this.Settings.EnabledCategories.TryGetValue(key, out var val) || val;
                if (ImGui.Checkbox($"{label}##cat_{key}", ref isEnabled))
                {
                    this.Settings.EnabledCategories[key] = isEnabled;
                    this.SaveSettings();
                }
            }

            ImGui.NextColumn();

            ImGui.TextColored(new Vector4(1.0f, 0.65f, 0.2f, 1.0f), "Unique Items & Maps");
            ImGui.Separator();
            foreach (var (key, label) in uniqueCats)
            {
                bool isEnabled = !this.Settings.EnabledCategories.TryGetValue(key, out var val) || val;
                if (ImGui.Checkbox($"{label}##cat_{key}", ref isEnabled))
                {
                    this.Settings.EnabledCategories[key] = isEnabled;
                    this.SaveSettings();
                }
            }

            ImGui.Columns(1);
        }

        private void DrawTabDisplaySettings()
        {
            ImGui.Spacing();
            ImGui.Text("Value Display:");
            int dc = (int)this.Settings.DisplayCurrency;
            if (ImGui.RadioButton("Divine (D)", ref dc, 0)) { this.Settings.DisplayCurrency = DisplayCurrency.Divine; this.SaveSettings(); }
            ImGui.SameLine();
            if (ImGui.RadioButton("Exalted (E)", ref dc, 1)) { this.Settings.DisplayCurrency = DisplayCurrency.Exalted; this.SaveSettings(); }
            ImGui.SameLine();
            if (ImGui.RadioButton("Chaos (C)", ref dc, 2)) { this.Settings.DisplayCurrency = DisplayCurrency.Chaos; this.SaveSettings(); }

            ImGui.Spacing();
            ImGui.Text("Price style:");
            int pds = (int)this.Settings.PriceDisplayStyle;
            if (ImGui.RadioButton("Currency icon", ref pds, 0)) { this.Settings.PriceDisplayStyle = PriceDisplayStyle.Image; this.SaveSettings(); }
            ImGui.SameLine();
            if (ImGui.RadioButton("Text", ref pds, 1)) { this.Settings.PriceDisplayStyle = PriceDisplayStyle.Text; this.SaveSettings(); }
            ImGui.SameLine();
            ImGui.TextDisabled("(icon = value + currency image)");

            ImGui.Spacing();
            float ts = this.Settings.TextScale;
            if (ImGui.SliderFloat("Text size", ref ts, 0.5f, 2.0f, "%.1f"))
            {
                this.Settings.TextScale = ts;
                this.SaveSettings();
            }

            ImGui.Separator();

            // Ui Price Position
            string[] uiPositions = { "Top Left", "Top Right", "Bottom Left", "Bottom Right" };
            int uiPos = (int)this.Settings.UiPricePosition;
            ImGui.SetNextItemWidth(200f);
            if (ImGui.Combo("Price position (Inventory/Stash)", ref uiPos, uiPositions, uiPositions.Length))
            {
                this.Settings.UiPricePosition = (UiPricePosition)uiPos;
                this.SaveSettings();
            }

            // Ground Price Position
            string[] gndPositions = { "Top", "Bottom", "Left", "Right" };
            int gndPos = (int)this.Settings.GroundPricePosition;
            ImGui.SetNextItemWidth(200f);
            if (ImGui.Combo("Price position (Ground items)", ref gndPos, gndPositions, gndPositions.Length))
            {
                this.Settings.GroundPricePosition = (GroundPricePosition)gndPos;
                this.SaveSettings();
            }
        }

        private bool DrawHotkeyCaptureRow(string label, string idSuffix, ref int vk)
        {
            bool boundNow = false;
            ImGui.Text(label);
            ImGui.SameLine();

            if (this.captureTarget.HasValue && this.captureTarget.Value == vk)
            {
                ImGui.TextColored(new Vector4(1.0f, 1.0f, 0.0f, 1.0f), "Press any key... (ESC to cancel)");
                if ((GetAsyncKeyState(0x1B) & 0x8000) != 0) // ESC
                {
                    this.captureTarget = null;
                }
                else
                {
                    for (int k = 0x08; k < 0xFF; k++)
                    {
                        if (k == 0x01 || k == 0x02 || k == 0x04) continue; // Mouse buttons
                        if (k == 0x1B) continue;
                        if ((GetAsyncKeyState(k) & 0x8000) != 0)
                        {
                            vk = k;
                            this.captureTarget = null;
                            boundNow = true;
                            this.SaveSettings();
                            break;
                        }
                    }
                }
            }
            else
            {
                ImGui.Text(GetVkName(vk));
                ImGui.SameLine();
                if (ImGui.Button($"Set Hotkey##{idSuffix}", new Vector2(100f, 0f)))
                {
                    this.captureTarget = vk;
                }
                if (vk != 0)
                {
                    ImGui.SameLine();
                    if (ImGui.Button($"Clear##{idSuffix}", new Vector2(60f, 0f)))
                    {
                        vk = 0;
                        this.SaveSettings();
                    }
                }
            }

            return boundNow;
        }

        private void DrawTabOverlayToggles()
        {
            ImGui.Spacing();

            bool showGnd = this.Settings.ShowGroundPrices;
            if (ImGui.Checkbox("Show prices on dropped items", ref showGnd)) { this.Settings.ShowGroundPrices = showGnd; this.SaveSettings(); }

            bool showInv = this.Settings.ShowInventoryPrices;
            if (ImGui.Checkbox("Show prices in inventory", ref showInv)) { this.Settings.ShowInventoryPrices = showInv; this.SaveSettings(); }
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1.0f, 0.75f, 0.2f, 1.0f), "(may affect FPS)");

            bool showStash = this.Settings.ShowOtherInventoryPrices;
            if (ImGui.Checkbox("Show prices in stash", ref showStash)) { this.Settings.ShowOtherInventoryPrices = showStash; this.SaveSettings(); }
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1.0f, 0.75f, 0.2f, 1.0f), "(may affect FPS)");

            bool showRitual = this.Settings.ShowRitualPrices;
            if (ImGui.Checkbox("Ritual", ref showRitual)) { this.Settings.ShowRitualPrices = showRitual; this.SaveSettings(); }
            ImGui.SameLine();
            ImGui.TextDisabled("(price items in the Ritual \"Favours\" shop)");

            bool showRs = this.Settings.ShowRuneshapePrices;
            if (ImGui.Checkbox("Runeshape", ref showRs)) { this.Settings.ShowRuneshapePrices = showRs; this.SaveSettings(); }
            ImGui.SameLine();
            ImGui.TextDisabled("(price rewards in the Runeshape Combinations panel)");

            bool showRsWin = this.Settings.ShowRuneshapeWindow;
            if (ImGui.Checkbox("Runeshape window", ref showRsWin)) { this.Settings.ShowRuneshapeWindow = showRsWin; this.SaveSettings(); }
            ImGui.SameLine();
            if (ImGui.Button(this.Settings.ShowRuneshapeWindow ? "Close Window" : "Open Window"))
            {
                this.Settings.ShowRuneshapeWindow = !this.Settings.ShowRuneshapeWindow;
                this.SaveSettings();
            }
            ImGui.SameLine();
            if (ImGui.Button("Reset Pos"))
            {
                this.Settings.RuneshapeWinX = 100f;
                this.Settings.RuneshapeWinY = 100f;
                this.Settings.ShowRuneshapeWindow = true;
                this.SaveSettings();
            }
            ImGui.SameLine();
            ImGui.TextDisabled("(movable overlay listing each Runeshape with prices)");

            // Advanced settings for Runeshape window
            ImGui.Indent();
            if (ImGui.TreeNode("Runeshape window: advanced settings"))
            {
                ImGui.Spacing();

                int rshk = this.Settings.RuneshapeWinHotkey;
                if (this.DrawHotkeyCaptureRow("Show/hide hotkey:", "rswin", ref rshk))
                {
                    this.Settings.RuneshapeWinHotkey = rshk;
                    this.runeshapeWinHotkeyWasDown = true;
                    this.SaveSettings();
                }

                bool rhoh = this.Settings.RuneshapeWinHideOnHover;
                if (ImGui.Checkbox("Hide on mouse hover", ref rhoh)) { this.Settings.RuneshapeWinHideOnHover = rhoh; this.SaveSettings(); }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("The overlay disappears while the mouse cursor is over it and reappears when cursor leaves.");

                bool rsmk = this.Settings.ShowRuneshapeWorldMarkers;
                if (ImGui.Checkbox("Show monolith markers in 3D world", ref rsmk)) { this.Settings.ShowRuneshapeWorldMarkers = rsmk; this.SaveSettings(); }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Draws numbered (#1, #2...) colored badges floating over monolith pillars in the game world.");

                float rwa = this.Settings.RuneshapeWinAlpha;
                ImGui.SetNextItemWidth(200f);
                if (ImGui.SliderFloat("Opacity##rswin", ref rwa, 0.1f, 1.0f, "%.2f"))
                {
                    this.Settings.RuneshapeWinAlpha = rwa;
                    this.SaveSettings();
                }

                ImGui.Spacing();
                ImGui.Text("Price display:");
                ImGui.SameLine();
                int rsps = (int)this.Settings.RuneshapeWinPriceStyle;
                if (ImGui.RadioButton("Currency icon##rswin", ref rsps, 0)) { this.Settings.RuneshapeWinPriceStyle = PriceDisplayStyle.Image; this.SaveSettings(); }
                ImGui.SameLine();
                if (ImGui.RadioButton("Text##rswin", ref rsps, 1)) { this.Settings.RuneshapeWinPriceStyle = PriceDisplayStyle.Text; this.SaveSettings(); }

                ImGui.Spacing();
                ImGui.TextDisabled("Header elements:");
                bool hc = this.Settings.RsShowHdrColor;
                if (ImGui.Checkbox("Color square", ref hc)) { this.Settings.RsShowHdrColor = hc; this.SaveSettings(); }
                ImGui.SameLine();
                bool hr = this.Settings.RsShowHdrRunes;
                if (ImGui.Checkbox("Rune sockets", ref hr)) { this.Settings.RsShowHdrRunes = hr; this.SaveSettings(); }
                ImGui.SameLine();
                bool hb = this.Settings.RsShowHdrBest;
                if (ImGui.Checkbox("Best reward price", ref hb)) { this.Settings.RsShowHdrBest = hb; this.SaveSettings(); }

                ImGui.TextDisabled("Expanded list elements:");
                bool ri = this.Settings.RsShowRowIcon;
                if (ImGui.Checkbox("Reward icon", ref ri)) { this.Settings.RsShowRowIcon = ri; this.SaveSettings(); }
                ImGui.SameLine();
                bool rn = this.Settings.RsShowRowName;
                if (ImGui.Checkbox("Reward name", ref rn)) { this.Settings.RsShowRowName = rn; this.SaveSettings(); }
                ImGui.SameLine();
                bool rq = this.Settings.RsShowRowQty;
                if (ImGui.Checkbox("Reward quantity", ref rq)) { this.Settings.RsShowRowQty = rq; this.SaveSettings(); }
                bool rp = this.Settings.RsShowRowPrice;
                if (ImGui.Checkbox("Price##rswinrow", ref rp)) { this.Settings.RsShowRowPrice = rp; this.SaveSettings(); }
                ImGui.SameLine();
                bool rpr = this.Settings.RsShowRowPropRunes;
                if (ImGui.Checkbox("Propagating runes (yellow glow)", ref rpr)) { this.Settings.RsShowRowPropRunes = rpr; this.SaveSettings(); }

                ImGui.Spacing();
                ImGui.TreePop();
            }
            ImGui.Unindent();

            bool showWeights = this.Settings.ShowRuneshapeWeights;
            if (ImGui.Checkbox("Runeshape weights", ref showWeights)) { this.Settings.ShowRuneshapeWeights = showWeights; this.SaveSettings(); }

            bool showIcons = this.Settings.ShowItemIcons;
            if (ImGui.Checkbox("Show item icons", ref showIcons)) { this.Settings.ShowItemIcons = showIcons; this.SaveSettings(); }

            bool hideUnfocused = this.Settings.HideWhenUnfocused;
            if (ImGui.Checkbox("Hide when game not focused", ref hideUnfocused)) { this.Settings.HideWhenUnfocused = hideUnfocused; this.SaveSettings(); }

            ImGui.Separator();
            int hhk = this.Settings.HideHotkey;
            if (this.DrawHotkeyCaptureRow("Hold-to-hide hotkey:", "hold", ref hhk))
            {
                this.Settings.HideHotkey = hhk;
                this.SaveSettings();
            }

            ImGui.Spacing();
            if (ImGui.TreeNode("Category filters"))
            {
                var keys = new List<string>(this.Settings.EnabledCategories.Keys);
                foreach (var k in keys)
                {
                    bool val = this.Settings.EnabledCategories[k];
                    if (ImGui.Checkbox(k, ref val))
                    {
                        this.Settings.EnabledCategories[k] = val;
                        this.SaveSettings();
                    }
                }
                ImGui.TreePop();
            }
        }

        private void DrawDebugPanel()
        {
            var st = this.priceService?.GetStatus() ?? default;
            ImGui.TextColored(new Vector4(0.6f, 0.8f, 1.0f, 1.0f), "Price Service:");
            ImGui.Text($"  Loaded: {(st.Loaded ? "YES" : "NO")}");
            ImGui.Text($"  Total items: {st.TotalItems}");
            ImGui.Text($"  DivineInChaos: {st.DivineInChaos:F2}");
            ImGui.Text($"  ExaltedInChaos: {st.ExaltedInChaos:F2}");
            ImGui.Text($"  Categories: ok={st.CatsOk} / pending={st.CatsPending} / failed={st.CatsFailed}");

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            ImGui.TextColored(new Vector4(1.0f, 0.6f, 0.2f, 1.0f), "Performance (render thread):");
            int scanMs = this.Settings.ScanIntervalMs;
            ImGui.SetNextItemWidth(220f);
            if (ImGui.SliderInt("Inventory scan interval (ms)", ref scanMs, 50, 2000))
            {
                this.Settings.ScanIntervalMs = scanMs;
                this.SaveSettings();
            }

            void DrawRow(string label, double last, double peak)
            {
                var c = (peak > 5.0) ? new Vector4(1.0f, 0.35f, 0.35f, 1.0f)
                      : (peak > 2.0) ? new Vector4(1.0f, 0.85f, 0.2f, 1.0f)
                      : new Vector4(0.5f, 0.9f, 0.5f, 1.0f);
                ImGui.Text($"  {label,-26}");
                ImGui.SameLine(240f);
                ImGui.TextColored(c, $"{last:F3} ms   (peak {peak:F3} ms)");
            }

            DrawRow("Scan() request:", this.perfScanCallMs, this.perfPeakScanCallMs);
            DrawRow("GetAll() (Slots):", this.perfGetAllMs, this.perfPeakGetAllMs);
            DrawRow("DrawInventory/frame:", this.perfDrawInvMs, this.perfPeakDrawInvMs);
            DrawRow("DrawGround/frame:", this.perfGroundMs, this.perfPeakGroundMs);
            DrawRow("LookupPrice/frame:", this.perfLookupMs, this.perfPeakLookupMs);

            ImGui.Spacing();
            ImGui.Text($"  LookupPrice (latest scan): {this.perfLookupExact} found, {this.perfLookupMiss} miss");
            ImGui.Text($"  Cached: ground={this.cachedGroundTags.Count} inv={this.cachedInvSlots.Count} stash={this.cachedStashSlots.Count}");

            if (ImGui.Button("Reset peaks"))
            {
                this.perfPeakScanCallMs = this.perfPeakGetAllMs = this.perfPeakDrawInvMs = 0.0;
                this.perfPeakGroundMs = this.perfPeakLookupMs = 0.0;
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            var inGame = Core.States.GameCurrentState == GameStateTypes.InGameState;
            var areaDetails = Core.States.InGameStateObject?.CurrentWorldInstance?.AreaDetails;
            var area = Core.States.InGameStateObject?.CurrentAreaInstance;
            ImGui.TextColored(new Vector4(0.6f, 0.8f, 1.0f, 1.0f), "Game State:");
            ImGui.Text($"  Attached: {(Core.Process.Pid > 0 ? "YES" : "NO")}");
            ImGui.Text($"  InGame: {(inGame ? "YES" : "NO")}");
            ImGui.Text($"  Area: {areaDetails?.Name ?? "None"} (level {area?.CurrentAreaLevel ?? 0})");
            ImGui.Text($"  IsHideout: {(areaDetails?.IsHideout == true ? "YES" : "NO")}, IsTown: {(areaDetails?.IsTown == true ? "YES" : "NO")}");
            ImGui.Text($"  Total Entities: {area?.AwakeEntities?.Count ?? 0}");
        }

        // ========================================================================
        // Overlay Rendering
        // ========================================================================

        public override void DrawUI()
        {
            if (Core.States.GameCurrentState != GameStateTypes.InGameState) return;

            var inGame = Core.States.InGameStateObject;
            if (inGame == null) return;

            var areaDetails = inGame.CurrentWorldInstance?.AreaDetails;
            bool inTownOrHideout = areaDetails?.IsTown == true || areaDetails?.IsHideout == true;

            // Runeshape show/hide hotkey
            if (this.Settings.RuneshapeWinHotkey != 0 && this.captureTarget == null)
            {
                bool down = (GetAsyncKeyState(this.Settings.RuneshapeWinHotkey) & 0x8000) != 0;
                if (down && !this.runeshapeWinHotkeyWasDown)
                {
                    this.Settings.ShowRuneshapeWindow = !this.Settings.ShowRuneshapeWindow;
                    this.SaveSettings();
                }
                this.runeshapeWinHotkeyWasDown = down;
            }

            // Runeshape Monoliths (in-world badges / large map markers & movable window)
            if (!inTownOrHideout && (this.Settings.ShowRuneshapeWorldMarkers || this.Settings.ShowRuneshapeWindow))
            {
                var activeMonoliths = this.GetActiveMonoliths();

                if (this.Settings.ShowRuneshapeWorldMarkers && activeMonoliths.Count > 0)
                {
                    bool hideOverlays = (this.Settings.HideWhenUnfocused && !IsGameWindowFocused()) ||
                                        (this.Settings.HideHotkey != 0 && (GetAsyncKeyState(this.Settings.HideHotkey) & 0x8000) != 0);
                    if (!hideOverlays)
                    {
                        this.DrawMonolithWorldMarkers(activeMonoliths);
                    }
                }

                if (this.Settings.ShowRuneshapeWindow)
                {
                    this.DrawRuneshapeWindow(activeMonoliths);
                }
            }

            // In-world overlays gate (ground & inventory)
            if (this.Settings.HideWhenUnfocused && !IsGameWindowFocused()) return;
            if (this.Settings.HideHotkey != 0 && (GetAsyncKeyState(this.Settings.HideHotkey) & 0x8000) != 0) return;

            // Auto refresh trigger
            if ((DateTime.UtcNow - this.lastAutoRefreshUtc).TotalMinutes >= Math.Max(5, this.Settings.AutoRefreshMinutes))
            {
                this.lastAutoRefreshUtc = DateTime.UtcNow;
                this.priceService?.TriggerRefresh(this.Settings.League, this.Settings.PriceSource, this.Settings.EnabledCategories);
            }

            var st = this.priceService?.GetStatus();
            if (st == null || !st.Value.Loaded) return;

            // Runeshape In-Game Combinations Panel Pricing
            if (this.Settings.ShowRuneshapePrices && !inTownOrHideout)
            {
                if ((DateTime.UtcNow - this.lastRuneshapeScanUtc).TotalMilliseconds >= 200)
                {
                    this.lastRuneshapeScanUtc = DateTime.UtcNow;
                    this.ScanRuneshapeRows();
                }
                this.DrawRuneshapeOverlay();
            }

            // Ground overlay
            if (this.Settings.ShowGroundPrices)
            {
                var swG = Stopwatch.StartNew();
                if ((DateTime.UtcNow - this.lastGroundScanUtc).TotalMilliseconds >= this.Settings.ScanIntervalMs)
                {
                    this.lastGroundScanUtc = DateTime.UtcNow;
                    this.ScanGroundItems();
                }
                this.DrawGroundTags();
                swG.Stop();
                this.perfGroundMs = swG.Elapsed.TotalMilliseconds;
                if (this.perfGroundMs > this.perfPeakGroundMs) this.perfPeakGroundMs = this.perfGroundMs;
            }

            // Inventory / Stash overlay (decoupled background scan, non-blocking render)
            if (this.Settings.ShowInventoryPrices || this.Settings.ShowOtherInventoryPrices || this.Settings.ShowRitualPrices)
            {
                var gameUi = Core.States.InGameStateObject?.GameUi;
                if (gameUi != null)
                {
                    var scanLeft = this.Settings.ShowOtherInventoryPrices && gameUi.LeftPanel.IsVisible;
                    var leftAddr = scanLeft ? gameUi.LeftPanel.Address : IntPtr.Zero;
                    var scanRight = this.Settings.ShowInventoryPrices && gameUi.RightPanel.IsVisible;
                    var rightAddr = scanRight ? gameUi.RightPanel.Address : IntPtr.Zero;

                    var now = DateTime.UtcNow;
                    if ((now - this.lastInvScanUtc).TotalMilliseconds >= this.Settings.ScanIntervalMs && !this.isInvScanRunning)
                    {
                        this.lastInvScanUtc = now;
                        this.isInvScanRunning = true;
                        _ = Task.Run(() =>
                        {
                            try
                            {
                                this.ScanItemSlots(leftAddr, rightAddr);
                            }
                            catch
                            {
                            }
                            finally
                            {
                                this.isInvScanRunning = false;
                            }
                        });
                    }
                }

                var swI = Stopwatch.StartNew();
                this.DrawSlotOverlays();
                swI.Stop();
                this.perfDrawInvMs = swI.Elapsed.TotalMilliseconds;
                if (this.perfDrawInvMs > this.perfPeakDrawInvMs) this.perfPeakDrawInvMs = this.perfDrawInvMs;
            }
        }

        private void ScanGroundItems()
        {
            var area = Core.States.InGameStateObject?.CurrentAreaInstance;
            if (area?.AwakeEntities == null || this.priceService == null) return;

            var newTags = new List<GroundTag>();
            foreach (var entity in area.AwakeEntities.Values)
            {
                if (!entity.TryGetComponent<WorldItem>(out var worldItem) || worldItem.ItemEntityAddress == IntPtr.Zero)
                    continue;
                if (!entity.TryGetComponent<Render>(out var render))
                    continue;

                var item = ReadFreshItem(worldItem.ItemEntityAddress);
                if (item == null) continue;

                var itemName = this.ResolveItemName(item, out var stackMultiplier);
                if (string.IsNullOrEmpty(itemName)) continue;

                var rarity = item.TryGetComponent<Mods>(out var m) ? m.Rarity : Rarity.Normal;
                string? variant = rarity switch
                {
                    Rarity.Normal => "normal",
                    Rarity.Magic => "magic",
                    Rarity.Rare => "rare",
                    Rarity.Unique => "unique",
                    _ => null
                };

                var clean = NinjaPriceService.CleanItemName(itemName);
                var swL = Stopwatch.StartNew();
                bool found = this.priceService.TryLookupPrice(clean, variant, out var price);
                swL.Stop();
                this.perfLookupMs += swL.Elapsed.TotalMilliseconds;

                if (found)
                {
                    this.perfLookupExact++;
                    float chaos = price.Chaos * stackMultiplier;
                    if (chaos < this.Settings.MinPriceChaos) continue;

                    float displayVal = this.Settings.DisplayCurrency switch
                    {
                        DisplayCurrency.Divine => price.Divine * stackMultiplier,
                        DisplayCurrency.Exalted => price.Exalt * stackMultiplier,
                        _ => chaos,
                    };

                    newTags.Add(new GroundTag
                    {
                        WorldPos = new Vector3(render.WorldPosition.X, render.WorldPosition.Y, render.TerrainHeight),
                        DisplayValue = displayVal,
                        Chaos = chaos,
                        IconPath = price.ItemIcon,
                    });
                }
                else
                {
                    this.perfLookupMiss++;
                }
            }

            this.cachedGroundTags.Clear();
            this.cachedGroundTags.AddRange(newTags);
        }

        private void DrawGroundTags()
        {
            if (this.cachedGroundTags.Count == 0) return;
            var world = Core.States.InGameStateObject?.CurrentWorldInstance;
            if (world == null) return;

            var dl = ImGui.GetBackgroundDrawList();
            float fontSize = ImGui.GetFontSize() * this.Settings.TextScale;

            foreach (var tag in this.cachedGroundTags)
            {
                var screenPos = world.WorldToScreen(new Vector2(tag.WorldPos.X, tag.WorldPos.Y), tag.WorldPos.Z);
                if (screenPos == Vector2.Zero) continue;

                var measured = this.MeasurePriceTag(tag.DisplayValue, fontSize, tag.IconPath);

                // Position offset relative to projected point
                float x = screenPos.X;
                float y = screenPos.Y;

                switch (this.Settings.GroundPricePosition)
                {
                    case GroundPricePosition.Top:
                        x -= measured.TotalW * 0.5f;
                        y -= measured.TotalH + 8.0f;
                        break;
                    case GroundPricePosition.Bottom:
                        x -= measured.TotalW * 0.5f;
                        y += 8.0f;
                        break;
                    case GroundPricePosition.Left:
                        x -= measured.TotalW + 8.0f;
                        y -= measured.TotalH * 0.5f;
                        break;
                    case GroundPricePosition.Right:
                        x += 8.0f;
                        y -= measured.TotalH * 0.5f;
                        break;
                }

                this.DrawPriceTag(dl, fontSize, x, y, measured, tag.Chaos);
            }
        }

        private void ScanItemSlots(IntPtr leftAddress, IntPtr rightAddress)
        {
            var service = this.priceService;
            if (service == null) return;

            var newInv = new List<SlotTag>();
            var newStash = new List<SlotTag>();
            double totalScanMs = 0;
            double totalGetMs = 0;
            int totalFound = 0;
            int totalMiss = 0;
            double totalLookupMs = 0;

            if (rightAddress != IntPtr.Zero)
            {
                this.ScanPanelSlots(rightAddress, newInv, out var sMs, out var gMs, out var fCount, out var mCount, out var lMs);
                totalScanMs += sMs;
                totalGetMs += gMs;
                totalFound += fCount;
                totalMiss += mCount;
                totalLookupMs += lMs;
            }

            if (leftAddress != IntPtr.Zero)
            {
                this.ScanPanelSlots(leftAddress, newStash, out var sMs, out var gMs, out var fCount, out var mCount, out var lMs);
                totalScanMs += sMs;
                totalGetMs += gMs;
                totalFound += fCount;
                totalMiss += mCount;
                totalLookupMs += lMs;
            }

            this.perfScanCallMs = totalScanMs;
            if (this.perfScanCallMs > this.perfPeakScanCallMs) this.perfPeakScanCallMs = this.perfScanCallMs;

            this.perfGetAllMs = totalGetMs;
            if (this.perfGetAllMs > this.perfPeakGetAllMs) this.perfPeakGetAllMs = this.perfGetAllMs;

            this.perfLookupExact = totalFound;
            this.perfLookupMiss = totalMiss;
            this.perfLookupMs = totalLookupMs;
            if (this.perfLookupMs > this.perfPeakLookupMs) this.perfPeakLookupMs = this.perfLookupMs;

            this.cachedInvSlots = newInv;
            this.cachedStashSlots = newStash;
        }

        private void ScanPanelSlots(
            IntPtr panelAddress,
            List<SlotTag> output,
            out double scanUiMs,
            out double getSlotsMs,
            out int exactFound,
            out int missFound,
            out double lookupMs)
        {
            scanUiMs = 0;
            getSlotsMs = 0;
            exactFound = 0;
            missFound = 0;
            lookupMs = 0;

            var handle = Core.Process?.Handle;
            var service = this.priceService;
            if (panelAddress == IntPtr.Zero || handle == null || service == null) return;

            if (!PluginUiElementReflection.TryGetAbsoluteRect(panelAddress, out var panelPos, out var panelSize)) return;
            var panelMax = panelPos + panelSize;

            var swScan = Stopwatch.StartNew();
            var queue = new Queue<(IntPtr Elem, IntPtr Parent, ScrollBinding Scroll)>();
            var visited = new HashSet<IntPtr>();
            queue.Enqueue((panelAddress, IntPtr.Zero, default));

            var candidatesByItem = new Dictionary<IntPtr, List<SlotCandidate>>();

            while (queue.Count > 0 && visited.Count < 2500)
            {
                var (elem, parent, scroll) = queue.Dequeue();
                if (elem == IntPtr.Zero || !visited.Add(elem)) continue;

                if (!handle.TryReadMemory<UiElementBaseOffset>(elem, out var offset)) continue;
                if (!UiElementBaseFuncs.IsVisibleChecker(offset.Flags)) continue;

                var children = handle.ReadStdVector<IntPtr>(offset.ChildrensPtr);
                if (children.Length > 0)
                {
                    var hasScrollContainer = this.TryGetScrollContainer(children, out var scrollItemsAddress, out var localScroll);
                    foreach (var child in children)
                    {
                        if (child == IntPtr.Zero || visited.Contains(child)) continue;
                        if (hasScrollContainer && child == scrollItemsAddress)
                        {
                            queue.Enqueue((child, elem, localScroll));
                        }
                        else
                        {
                            queue.Enqueue((child, elem, scroll));
                        }
                    }
                }

                var itemAddr = handle.ReadMemory<IntPtr>(elem + UiElementItemAddressOffset);
                if (itemAddr == IntPtr.Zero) continue;

                if (!candidatesByItem.TryGetValue(itemAddr, out var list))
                {
                    list = new List<SlotCandidate>();
                    candidatesByItem[itemAddr] = list;
                }
                list.Add(new SlotCandidate(elem, parent, scroll));
            }
            swScan.Stop();
            scanUiMs = swScan.Elapsed.TotalMilliseconds;

            var swGet = Stopwatch.StartNew();
            long nowMs = Environment.TickCount64;

            if (this.itemSlotCache.Count > 600)
            {
                var expiredKeys = this.itemSlotCache.Where(kv => nowMs >= kv.Value.ExpireUtcMs).Select(kv => kv.Key).ToList();
                foreach (var k in expiredKeys) this.itemSlotCache.Remove(k);
            }

            foreach (var (itemAddr, candidates) in candidatesByItem)
            {
                var hasVisibleRect = false;
                var slotPos = Vector2.Zero;
                var slotSize = Vector2.Zero;

                foreach (var candidate in candidates)
                {
                    if (!TryGetSlotRect(candidate, out var cPos, out var cSize)) continue;
                    var center = cPos + (cSize * 0.5f);
                    if (center.X < panelPos.X || center.X > panelMax.X) continue;
                    if (!candidate.Scroll.IsActive && (center.Y < panelPos.Y || center.Y > panelMax.Y)) continue;

                    slotPos = cPos;
                    slotSize = cSize;
                    hasVisibleRect = true;
                    break;
                }

                if (!hasVisibleRect) continue;

                bool isPriced = false;
                int stackCount = 1;
                PriceResult price = default;

                if (this.itemSlotCache.TryGetValue(itemAddr, out var cached) && nowMs < cached.ExpireUtcMs)
                {
                    isPriced = cached.IsPriced;
                    stackCount = cached.StackCount;
                    price = cached.Price;
                }
                else
                {
                    if (!PluginUiElementReflection.TryValidateItemAddress(itemAddr, out _, out _)) continue;

                    var item = ReadFreshItem(itemAddr);
                    if (item == null || string.IsNullOrEmpty(item.Path) ||
                        !item.Path.StartsWith("Metadata/Items/", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var itemName = this.ResolveItemName(item, out stackCount);
                    if (string.IsNullOrWhiteSpace(itemName)) continue;

                    var rarity = item.TryGetComponent<Mods>(out var m) ? m.Rarity : Rarity.Normal;
                    string? variant = rarity switch
                    {
                        Rarity.Normal => "normal",
                        Rarity.Magic => "magic",
                        Rarity.Rare => "rare",
                        Rarity.Unique => "unique",
                        _ => null
                    };

                    var clean = NinjaPriceService.CleanItemName(itemName);
                    var swL = Stopwatch.StartNew();
                    isPriced = service.TryLookupPrice(clean, variant, out price);
                    swL.Stop();
                    lookupMs += swL.Elapsed.TotalMilliseconds;

                    this.itemSlotCache[itemAddr] = new CachedSlotItem
                    {
                        ItemName = itemName,
                        StackCount = stackCount,
                        IsPriced = isPriced,
                        Price = price,
                        ExpireUtcMs = nowMs + 3000
                    };
                }

                if (isPriced)
                {
                    exactFound++;
                    float chaos = price.Chaos * stackCount;
                    if (chaos < this.Settings.MinPriceChaos) continue;

                    float displayVal = this.Settings.DisplayCurrency switch
                    {
                        DisplayCurrency.Divine => price.Divine * stackCount,
                        DisplayCurrency.Exalted => price.Exalt * stackCount,
                        _ => chaos,
                    };

                    output.Add(new SlotTag
                    {
                        Pos = slotPos,
                        Size = slotSize,
                        DisplayValue = displayVal,
                        Chaos = chaos,
                        IconPath = price.ItemIcon
                    });
                }
                else
                {
                    missFound++;
                }
            }
            swGet.Stop();
            getSlotsMs = swGet.Elapsed.TotalMilliseconds;
        }

        private void DrawSlotOverlays()
        {
            var dl = ImGui.GetBackgroundDrawList();
            float baseFontSize = ImGui.GetFontSize() * this.Settings.TextScale;

            void DrawSlots(List<SlotTag> slots)
            {
                foreach (var s in slots)
                {
                    float adaptiveFont = ComputeAdaptiveFontSize(baseFontSize, s.Size.X, s.Size.Y);
                    var measured = this.MeasurePriceTag(s.DisplayValue, adaptiveFont, s.IconPath, s.Size.X);

                    float x = s.Pos.X;
                    float y = s.Pos.Y;

                    switch (this.Settings.UiPricePosition)
                    {
                        case UiPricePosition.TopLeft:
                            x += 2.0f;
                            y += 2.0f;
                            break;
                        case UiPricePosition.TopRight:
                            x += s.Size.X - measured.TotalW - 2.0f;
                            y += 2.0f;
                            break;
                        case UiPricePosition.BottomLeft:
                            x += 2.0f;
                            y += s.Size.Y - measured.TotalH - 2.0f;
                            break;
                        case UiPricePosition.BottomRight:
                            x += s.Size.X - measured.TotalW - 2.0f;
                            y += s.Size.Y - measured.TotalH - 2.0f;
                            break;
                    }

                    this.DrawPriceTag(dl, adaptiveFont, x, y, measured, s.Chaos);
                }
            }

            if (this.Settings.ShowInventoryPrices) DrawSlots(this.cachedInvSlots);
            if (this.Settings.ShowOtherInventoryPrices) DrawSlots(this.cachedStashSlots);
        }

        private void ScanRuneshapeRows()
        {
            var handle = Core.Process?.Handle;
            var service = this.priceService;
            if (handle == null || service == null)
            {
                this.cachedRuneshapeRows.Clear();
                return;
            }

            var gameUi = Core.States.InGameStateObject?.GameUi;
            if (gameUi == null || gameUi.Address == IntPtr.Zero)
            {
                this.cachedRuneshapeRows.Clear();
                return;
            }

            var container = NinjaRuneshapeHelper.ResolveRuneforgeContainer(gameUi.Address);
            if (container == IntPtr.Zero || !handle.TryReadMemory<UiElementBaseOffset>(container, out var off))
            {
                this.cachedRuneshapeRows.Clear();
                return;
            }

            var rows = handle.ReadStdVector<IntPtr>(off.ChildrensPtr);
            if (rows == null || rows.Length == 0)
            {
                this.cachedRuneshapeRows.Clear();
                return;
            }

            var newRows = new List<RuneshapeRowTag>();
            var chaosDiv = service.DivineInChaos > 0 ? service.DivineInChaos : 200.0f;
            var candidates = new List<(Vector2 rowPos, Vector2 rowSize, string chipText, float chaos, float displayVal, string icon, double score)>();

            foreach (var row in rows)
            {
                if (row == IntPtr.Zero) continue;
                if (!handle.TryReadMemory<UiElementBaseOffset>(row, out var rowOff) || !UiElementBaseFuncs.IsVisibleChecker(rowOff.Flags)) continue;

                var rowKids = handle.ReadStdVector<IntPtr>(rowOff.ChildrensPtr);
                if (rowKids == null || rowKids.Length == 0) continue;

                var labelElem = rowKids[0];
                var rawText = this.ReadUiElementText(labelElem);
                if (string.IsNullOrWhiteSpace(rawText)) continue;

                NinjaRuneshapeHelper.ParseRuneforgeRowText(rawText, out var count, out var itemName);
                if (string.IsNullOrWhiteSpace(itemName)) continue;

                if (!PluginUiElementReflection.TryGetAbsoluteRect(row, out var rowPos, out var rowSize)) continue;
                if (rowSize.X <= 0f || rowSize.Y <= 0f) continue;

                var clean = NinjaPriceService.CleanItemName(itemName);
                double score = 0;
                string chipText;
                float chaosVal = 0f;
                float dispVal = 0f;
                string itemIcon = string.Empty;

                bool isUnique = itemName.Contains("Unique", StringComparison.OrdinalIgnoreCase) ||
                                rawText.Contains("Unique", StringComparison.OrdinalIgnoreCase) ||
                                rawText.Contains("ยูนิค", StringComparison.OrdinalIgnoreCase);

                bool isVeryRare = isUnique && (itemName.Contains("Very Rare", StringComparison.OrdinalIgnoreCase) ||
                                               rawText.Contains("Very Rare", StringComparison.OrdinalIgnoreCase));

                bool isRare = isUnique && !isVeryRare && (itemName.Contains("Rare", StringComparison.OrdinalIgnoreCase) ||
                                                         rawText.Contains("Rare", StringComparison.OrdinalIgnoreCase));

                if (isUnique)
                {
                    if (isVeryRare)
                    {
                        chipText = "Very Rare Unique";
                        score = 500;
                        chaosVal = 500;
                    }
                    else if (isRare)
                    {
                        chipText = "Rare Unique";
                        score = 100;
                        chaosVal = 100;
                    }
                    else
                    {
                        continue;
                    }
                    dispVal = this.Settings.DisplayCurrency switch
                    {
                        DisplayCurrency.Divine => chaosVal / chaosDiv,
                        DisplayCurrency.Exalted => chaosVal / (service.ExaltedInChaos > 0 ? service.ExaltedInChaos : 10f),
                        _ => chaosVal
                    };
                }
                else if (service.TryLookupPrice(clean, out var pr) && pr.Chaos > 0)
                {
                    chaosVal = pr.Chaos * count;
                    dispVal = this.Settings.DisplayCurrency switch
                    {
                        DisplayCurrency.Divine => pr.Divine * count,
                        DisplayCurrency.Exalted => pr.Exalt * count,
                        _ => chaosVal
                    };
                    chipText = FormatPriceLocal(dispVal, this.Settings.DisplayCurrency);
                    score = chaosVal;
                    itemIcon = pr.ItemIcon;
                }
                else
                {
                    continue;
                }

                candidates.Add((rowPos, rowSize, chipText, chaosVal, dispVal, itemIcon, score));
            }

            if (candidates.Count > 0)
            {
                double bestScore = -1;
                int bestIdx = -1;
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (candidates[i].score > bestScore)
                    {
                        bestScore = candidates[i].score;
                        bestIdx = i;
                    }
                }

                for (int i = 0; i < candidates.Count; i++)
                {
                    var c = candidates[i];
                    bool isBest = (i == bestIdx && bestScore >= 1.0);
                    var text = isBest ? $"[PICK] {c.chipText}" : c.chipText;
                    var chipPos = new Vector2(
                        c.rowPos.X + c.rowSize.X + 8f,
                        c.rowPos.Y + (c.rowSize.Y - 20f) / 2f);

                    newRows.Add(new RuneshapeRowTag
                    {
                        RowPos = c.rowPos,
                        RowSize = c.rowSize,
                        ChipPos = chipPos,
                        ChipText = text,
                        Chaos = c.chaos,
                        DisplayValue = c.displayVal,
                        IconPath = c.icon,
                        IsBest = isBest
                    });
                }
            }

            this.cachedRuneshapeRows.Clear();
            this.cachedRuneshapeRows.AddRange(newRows);
        }

        private void DrawRuneshapeOverlay()
        {
            if (this.cachedRuneshapeRows.Count == 0) return;
            var fg = ImGui.GetForegroundDrawList();

            foreach (var row in this.cachedRuneshapeRows)
            {
                if (row.IsBest && row.RowSize.X > 0 && row.RowSize.Y > 0)
                {
                    // Glowing highlight around the recommended recipe row to pick
                    fg.AddRectFilled(row.RowPos, row.RowPos + row.RowSize, 0x2200FF7F, 4f);
                    fg.AddRect(row.RowPos, row.RowPos + row.RowSize, 0xFFFFD700, 4f, ImDrawFlags.None, 2.5f);
                }

                var textSize = ImGui.CalcTextSize(row.ChipText);
                var pad = new Vector2(8f, 4f);
                var min = row.ChipPos;
                var max = new Vector2(row.ChipPos.X + textSize.X + pad.X * 2, row.ChipPos.Y + textSize.Y + pad.Y * 2);

                var bg = row.IsBest ? 0xF02A1E05u : 0xF0101010u;
                var border = row.IsBest ? 0xFFFFD700u : 0xFF888899u;
                var textCol = row.IsBest ? 0xFF50FF50u : 0xFFFFFFFFu;

                fg.AddRectFilled(min, max, bg, 4f);
                fg.AddRect(min, max, border, 4f, ImDrawFlags.None, row.IsBest ? 2.0f : 1.2f);
                fg.AddText(new Vector2(min.X + pad.X, min.Y + pad.Y), textCol, row.ChipText);
            }
        }

        private void DrawRuneshapeRecipeRow(RsRecipe rec, MonolithData? monolith = null)
        {
            string rewardName = !string.IsNullOrEmpty(rec.Reward) ? rec.Reward : rec.Description;
            PriceResult pr = default;
            bool isPriced = this.priceService != null && this.priceService.TryLookupPrice(rewardName, out pr);
            float rowPriceChaos = isPriced ? pr.Chaos * Math.Max(1, rec.RewardCount) : 0f;
            float rowDisplayPrice = isPriced ? (this.Settings.DisplayCurrency switch
            {
                DisplayCurrency.Divine => pr.Divine * Math.Max(1, rec.RewardCount),
                DisplayCurrency.Exalted => pr.Exalt * Math.Max(1, rec.RewardCount),
                _ => rowPriceChaos
            }) : 0f;

            bool lineStarted = false;

            // 1) Reward Icon
            if (this.Settings.RsShowRowIcon && isPriced && !string.IsNullOrEmpty(pr.ItemIcon))
            {
                var iconTex = this.GetItemTexture(pr.ItemIcon);
                if (iconTex != null && iconTex.Value.Valid)
                {
                    float lh = ImGui.GetTextLineHeight();
                    ImGui.Image(iconTex.Value.Ptr, new Vector2(lh, lh));
                    lineStarted = true;
                }
            }

            // 2) Reward Name + Quantity
            string txt = rec.RewardCount > 1 ? $"{rewardName}  x{rec.RewardCount}" : rewardName;
            if (lineStarted) ImGui.SameLine(0f, 4f);
            if (isPriced)
                ImGui.TextColored(new Vector4(0.95f, 0.95f, 0.95f, 1.0f), txt);
            else
                ImGui.TextDisabled(txt);
            lineStarted = true;

            // 3) Price
            if (this.Settings.RsShowRowPrice && isPriced)
            {
                if (lineStarted) ImGui.SameLine(0f, 10f);
                ImGui.TextUnformatted(FormatPriceNumberLocal(rowDisplayPrice));
                var curTex = this.GetCurrencyTexture(this.Settings.DisplayCurrency);
                if (curTex != null && curTex.Value.Valid)
                {
                    ImGui.SameLine(0f, 3f);
                    float lh = ImGui.GetTextLineHeight();
                    float iw = (curTex.Value.H > 0) ? lh * (float)curTex.Value.W / curTex.Value.H : lh;
                    ImGui.Image(curTex.Value.Ptr, new Vector2(iw, lh));
                }
                lineStarted = true;
            }

            // 4) Weight
            if (this.Settings.ShowRuneshapeWeights && rec.ComboWeight != 0)
            {
                if (lineStarted) ImGui.SameLine(0f, 10f);
                var wc = rec.ComboWeight > 0 ? new Vector4(0.43f, 0.92f, 0.43f, 1f) : new Vector4(0.72f, 0.72f, 0.72f, 1f);
                ImGui.TextColored(wc, rec.ComboWeight > 0 ? $"+{rec.ComboWeight}" : $"{rec.ComboWeight}");
            }

            // 5) Rune names
            if (rec.Runes.Count > 0)
            {
                ImGui.Indent(18f);
                ImGui.TextDisabled($"Runes: {string.Join(" + ", rec.Runes)}");
                ImGui.Unindent(18f);
            }
        }

        private List<MonolithData> GetActiveMonoliths()
        {
            var area = Core.States.InGameStateObject?.CurrentAreaInstance;
            var monoliths = new List<MonolithData>();
            if (area?.AwakeEntities != null)
            {
                foreach (var e in area.AwakeEntities.Values)
                {
                    if (e.Path != null && e.Path.Contains("Expedition2Encounter", StringComparison.OrdinalIgnoreCase))
                    {
                        if (NinjaRuneshapeHelper.TryReadMonolith(e, out var mData))
                        {
                            monoliths.Add(mData);
                        }
                    }
                }
            }

            if (monoliths.Count == 0) return monoliths;

            int areaLevel = area?.CurrentAreaLevel ?? 0;

            for (int mIdx = 0; mIdx < monoliths.Count; mIdx++)
            {
                var m = monoliths[mIdx];
                m.Color = RsMonolithColors[mIdx % RsMonolithColors.Length];

                if (m.BestOffer == null)
                {
                    float maxChaos = -1f;
                    int maxWeight = -1;
                    MonolithOffer? top = null;
                    foreach (var rec in this.runeshapeRecipes)
                    {
                        if (rec.Size > m.HoleCount) continue;
                        if (areaLevel > 0 && rec.MaxLevel > 0 && (areaLevel < rec.MinLevel || areaLevel > rec.MaxLevel)) continue;

                        if (!m.IsUnique && m.AnchorIdx >= 0)
                        {
                            if (rec.RuneIdx == null || rec.RuneIdx.Count <= m.AnchorPos) continue;
                            if (rec.RuneIdx[m.AnchorPos] != m.AnchorIdx) continue;
                            if (rec.Size != m.HoleCount && !this.IsPartialAllowed(m.AnchorIdx, m.AnchorPos, rec.Size, areaLevel)) continue;
                        }

                        string rName = !string.IsNullOrEmpty(rec.Reward) ? rec.Reward : rec.Description;
                        int count = Math.Max(1, rec.RewardCount);
                        float chaos = 0f;
                        float displayVal = 0f;
                        string? itemIcon = null;

                        if (this.priceService != null && this.priceService.TryLookupPrice(rName, out var pr))
                        {
                            chaos = pr.Chaos * count;
                            itemIcon = pr.ItemIcon;
                            displayVal = this.Settings.DisplayCurrency switch
                            {
                                DisplayCurrency.Divine => pr.Divine * count,
                                DisplayCurrency.Exalted => pr.Exalt * count,
                                _ => chaos
                            };
                        }

                        if (chaos > maxChaos || (Math.Abs(chaos - maxChaos) < 0.001f && rec.ComboWeight > maxWeight))
                        {
                            maxChaos = chaos;
                            maxWeight = rec.ComboWeight;
                            top = new MonolithOffer
                            {
                                RecipeId = rec.Id,
                                Description = rec.Description,
                                Reward = rName,
                                RewardCount = count,
                                Runes = rec.Runes,
                                RuneIdx = rec.RuneIdx != null ? new List<int>(rec.RuneIdx) : new List<int>(),
                                ItemIcon = itemIcon,
                                PriceChaos = chaos,
                                DisplayValue = displayVal,
                                ComboWeight = rec.ComboWeight
                            };
                        }
                    }
                    m.BestOffer = top;
                }
            }

            return monoliths;
        }

        private void DrawMonolithWorldMarkers(List<MonolithData> monoliths)
        {
            var world = Core.States.InGameStateObject?.CurrentWorldInstance;
            if (world == null || monoliths.Count == 0) return;

            var game = Core.States.InGameStateObject;
            var largeMap = game?.GameUi?.LargeMap;
            bool isLargeMapVisible = largeMap != null && largeMap.Address != IntPtr.Zero && largeMap.IsVisible;
            var player = game?.CurrentAreaInstance?.Player;
            Render? playerRender = null;
            bool canMapProject = isLargeMapVisible && player != null && player.TryGetComponent<Render>(out playerRender, false) && playerRender != null;

            Vector2 center = Vector2.Zero;
            float cos = 0, sin = 0;
            if (canMapProject && playerRender != null)
            {
                center = largeMap!.Center + largeMap.Shift + largeMap.DefaultShift;
                const float LargeMapXBias = 0.6f;
                const float LargeMapYBias = 0.3f;
                center.X += LargeMapXBias;
                center.Y += LargeMapYBias;

                var baseRes = UiElementBaseFuncs.BaseResolution;
                var baseDiag = Math.Sqrt((baseRes.X * baseRes.X) + (baseRes.Y * baseRes.Y));
                var mapHeight = largeMap.Size.Y > 0 ? largeMap.Size.Y : Core.Process.WindowArea.Size.Height;
                var largeMapDiagonalLength = baseDiag * mapHeight / baseRes.Y;

                const float LargeMapScaleBaseline = 0.187812f;
                var largeMapModifiedZoom = Math.Max(0.001f, (float)(largeMap.Zoom * LargeMapScaleBaseline));

                const double CameraAngle = 38.7 * Math.PI / 180;
                float mapScale = 240f / largeMapModifiedZoom;
                cos = (float)(largeMapDiagonalLength * Math.Cos(CameraAngle) / mapScale);
                sin = (float)(largeMapDiagonalLength * Math.Sin(CameraAngle) / mapScale);
            }

            Vector2 ToMap(Vector3 worldPos)
            {
                var gridX = worldPos.X / 10.86957f;
                var gridY = worldPos.Y / 10.86957f;
                var delta = new Vector2(gridX - playerRender!.GridPosition.X, gridY - playerRender.GridPosition.Y);
                float deltaZ = (worldPos.Z - playerRender.TerrainHeight) / 10.86957f;
                return center + new Vector2((delta.X - delta.Y) * cos, (deltaZ - (delta.X + delta.Y)) * sin);
            }

            var dl = ImGui.GetBackgroundDrawList();

            var bgReg = this.GetRuneUiTexture("RuneBgRegular.png", ref this.runeBgRegularTex, ref this.runeBgRegularTried);
            var bgPur = this.GetRuneUiTexture("RuneBgPurple.png", ref this.runeBgPurpleTex, ref this.runeBgPurpleTried);
            var glow = this.GetRuneUiTexture("RunePropagation.png", ref this.runePropagationTex, ref this.runePropagationTried);
            var curTex = this.GetCurrencyTexture(this.Settings.DisplayCurrency);

            for (int i = 0; i < monoliths.Count; i++)
            {
                var m = monoliths[i];
                if (m.WorldPos == Vector3.Zero) continue;

                Vector2 screenPos;
                if (canMapProject)
                {
                    screenPos = ToMap(m.WorldPos);
                }
                else
                {
                    // Project 3D world position to 2D screen coords (lifted by +45f to hover cleanly above the monolith)
                    screenPos = world.WorldToScreen(new Vector2(m.WorldPos.X, m.WorldPos.Y), m.WorldPos.Z + 45f);
                }

                if (screenPos == Vector2.Zero) continue;

                // 1) Sockets row sizing
                float slotSz = 22f;
                float slotGap = 2f;
                int holes = Math.Clamp(m.HoleCount, 1, 16);
                float socketsW = (holes * slotSz) + Math.Max(0, holes - 1) * slotGap;
                float socketsH = slotSz;

                // 2) Bottom info chip sizing
                float chipH = 26f;
                float padX = 6f;
                float spacing = 5f;

                float sqSz = 16f;
                float curX = padX + sqSz;

                CurrencyTex? itemTex = null;
                float iconSz = 22f;
                if (m.BestOffer != null && !string.IsNullOrEmpty(m.BestOffer.ItemIcon))
                {
                    itemTex = this.GetItemTexture(m.BestOffer.ItemIcon);
                    if (itemTex != null && itemTex.Value.Valid)
                    {
                        curX += spacing + iconSz;
                    }
                }

                float curW = 18f;
                if (curTex != null && curTex.Value.Valid)
                {
                    curW = (curTex.Value.H > 0) ? 18f * (float)curTex.Value.W / curTex.Value.H : 18f;
                    curX += spacing + curW;
                }

                string priceText = m.IsCompleted
                    ? "DONE"
                    : (m.BestOffer != null ? FormatPriceNumberLocal(m.BestOffer.DisplayValue) : string.Empty);
                Vector2 priceSz = !string.IsNullOrEmpty(priceText) ? ImGui.CalcTextSize(priceText) : Vector2.Zero;
                if (priceSz.X > 0)
                {
                    curX += 3f + priceSz.X;
                }

                string weightText = string.Empty;
                Vector2 weightSz = Vector2.Zero;
                if (!m.IsCompleted && m.BestOffer != null && m.BestOffer.ComboWeight != 0)
                {
                    weightText = m.BestOffer.ComboWeight > 0 ? $"+{m.BestOffer.ComboWeight}" : $"{m.BestOffer.ComboWeight}";
                    weightSz = ImGui.CalcTextSize(weightText);
                    curX += spacing + weightSz.X;
                }

                curX += padX;
                float chipW = curX;

                float totalW = Math.Max(socketsW, chipW);
                float totalH = socketsH + 4f + chipH;

                float bX0 = screenPos.X - totalW * 0.5f;
                float bY0 = screenPos.Y - totalH;

                uint imgTint = m.IsCompleted ? 0xC8969696u : 0xFFFFFFFFu;

                // Draw Top Sockets Row
                float sockStartX = screenPos.X - socketsW * 0.5f;
                float sockY = bY0;

                for (int slot = 0; slot < holes; slot++)
                {
                    bool prop = m.GoldenSlots.Contains(slot);
                    int rIdx = -1;
                    if (m.BestOffer?.RuneIdx != null && slot < m.BestOffer.RuneIdx.Count)
                        rIdx = m.BestOffer.RuneIdx[slot];
                    else if (slot == m.AnchorPos && m.AnchorIdx >= 0)
                        rIdx = m.AnchorIdx;

                    bool slotRare = rIdx >= 23 && rIdx <= 32;
                    float sx0 = sockStartX + slot * (slotSz + slotGap);
                    float sy0 = sockY;
                    var p0 = new Vector2(sx0, sy0);
                    var p1 = new Vector2(sx0 + slotSz, sy0 + slotSz);

                    var bg = (slotRare && bgPur != null) ? bgPur : bgReg;
                    if (bg != null && bg.Value.Valid)
                    {
                        dl.AddImage(bg.Value.Ptr, p0, p1, Vector2.Zero, Vector2.One, imgTint);
                    }
                    else
                    {
                        float dr = slotSz * 0.5f;
                        dl.AddCircleFilled(new Vector2(sx0 + dr, sy0 + dr), dr, 0xDD141414u);
                        dl.AddCircle(new Vector2(sx0 + dr, sy0 + dr), dr, prop ? 0xFFFFD23Cu : 0xFF505050u, 0, 1.5f);
                    }

                    if (rIdx >= 0 && rIdx < 34)
                    {
                        var runeTex = this.GetRuneTexture(rIdx);
                        if (runeTex != null && runeTex.Value.Valid)
                        {
                            float inset = slotSz * 0.14f;
                            dl.AddImage(runeTex.Value.Ptr, new Vector2(p0.X + inset, p0.Y + inset), new Vector2(p1.X - inset, p1.Y - inset), Vector2.Zero, Vector2.One, imgTint);
                        }
                    }

                    if (prop && !m.IsCompleted)
                    {
                        if (glow != null && glow.Value.Valid)
                        {
                            float cx2 = sx0 + slotSz * 0.5f;
                            float gw = slotSz * 1.10f;
                            float gt = sy0 - slotSz * 0.40f;
                            dl.AddImage(glow.Value.Ptr, new Vector2(cx2 - gw * 0.5f, gt), new Vector2(cx2 + gw * 0.5f, gt + slotSz * 1.5f));
                        }
                        else
                        {
                            dl.AddCircle(new Vector2(sx0 + slotSz * 0.5f, sy0 + slotSz * 0.5f), slotSz * 0.55f, 0xFFFFD23Cu, 0, 1.8f);
                        }
                    }
                }

                // Draw Bottom Info Chip
                float chipX0 = screenPos.X - chipW * 0.5f;
                float chipY0 = bY0 + socketsH + 4f;
                float chipX1 = chipX0 + chipW;
                float chipY1 = chipY0 + chipH;
                float midY = chipY0 + chipH * 0.5f;

                dl.AddRectFilled(new Vector2(chipX0, chipY0), new Vector2(chipX1, chipY1), m.IsCompleted ? 0xC0202020u : 0xF0101010u, 4f);
                dl.AddRect(new Vector2(chipX0, chipY0), new Vector2(chipX1, chipY1), m.IsCompleted ? 0x88787878u : 0x88404040u, 4f, ImDrawFlags.None, 1.0f);

                float renderX = chipX0 + padX;

                // Color square
                dl.AddRectFilled(new Vector2(renderX, midY - sqSz * 0.5f), new Vector2(renderX + sqSz, midY + sqSz * 0.5f), m.IsCompleted ? 0xFF787878u : m.Color, 3f);
                renderX += sqSz + spacing;

                // Reward icon
                if (itemTex != null && itemTex.Value.Valid)
                {
                    dl.AddImage(itemTex.Value.Ptr, new Vector2(renderX, midY - iconSz * 0.5f), new Vector2(renderX + iconSz, midY + iconSz * 0.5f), Vector2.Zero, Vector2.One, imgTint);
                    renderX += iconSz + spacing;
                }

                // Currency icon
                if (curTex != null && curTex.Value.Valid)
                {
                    dl.AddImage(curTex.Value.Ptr, new Vector2(renderX, midY - 18f * 0.5f), new Vector2(renderX + curW, midY + 18f * 0.5f), Vector2.Zero, Vector2.One, imgTint);
                    renderX += curW + 3f;
                }

                // Price text
                if (priceSz.X > 0)
                {
                    uint priceCol = m.IsCompleted ? 0xFF888888u : 0xFFFFFFFFu;
                    dl.AddText(new Vector2(renderX, midY - priceSz.Y * 0.5f), priceCol, priceText);
                    renderX += priceSz.X + spacing;
                }

                // Weight text
                if (!string.IsNullOrEmpty(weightText))
                {
                    uint wCol = (m.BestOffer != null && m.BestOffer.ComboWeight > 0) ? 0xFF70EB70u : 0xFF8080EBu;
                    dl.AddText(new Vector2(renderX, midY - weightSz.Y * 0.5f), wCol, weightText);
                }
            }
        }

        private void DrawRuneshapeWindow(List<MonolithData> monoliths)
        {
            var area = Core.States.InGameStateObject?.CurrentAreaInstance;

            if (this.Settings.RuneshapeWinHideOnHover && this.runeshapeWinRectValid)
            {
                var io = ImGui.GetIO();
                var mouse = io.MousePos;
                if (mouse.X >= this.runeshapeWinRectMin.X && mouse.X <= this.runeshapeWinRectMax.X &&
                    mouse.Y >= this.runeshapeWinRectMin.Y && mouse.Y <= this.runeshapeWinRectMax.Y)
                {
                    return; // Hidden on hover
                }
            }

            if (this.Settings.RuneshapeWinX < 0f || this.Settings.RuneshapeWinX > 3800f) this.Settings.RuneshapeWinX = 100f;
            if (this.Settings.RuneshapeWinY < 0f || this.Settings.RuneshapeWinY > 2100f) this.Settings.RuneshapeWinY = 100f;
            var winAlpha = Math.Clamp(this.Settings.RuneshapeWinAlpha, 0.2f, 1.0f);

            ImGui.SetNextWindowPos(new Vector2(this.Settings.RuneshapeWinX, this.Settings.RuneshapeWinY), ImGuiCond.Appearing);
            ImGui.SetNextWindowCollapsed(this.Settings.RuneshapeWinCollapsed, ImGuiCond.Appearing);
            ImGui.SetNextWindowBgAlpha(winAlpha);

            bool keepOpen = true;
            var flags = ImGuiWindowFlags.AlwaysAutoResize |
                        ImGuiWindowFlags.NoResize |
                        ImGuiWindowFlags.NoSavedSettings |
                        ImGuiWindowFlags.NoFocusOnAppearing |
                        ImGuiWindowFlags.NoScrollbar;

            bool open = ImGui.Begin("Runeshape###RuneshapeWindow", ref keepOpen, flags);
            this.Settings.RuneshapeWinCollapsed = !open;

            var curPos = ImGui.GetWindowPos();
            var curSize = ImGui.GetWindowSize();
            this.runeshapeWinRectMin = curPos;
            this.runeshapeWinRectMax = curPos + curSize;
            this.runeshapeWinRectValid = true;

            if (!keepOpen)
            {
                this.Settings.ShowRuneshapeWindow = false;
                this.SaveSettings();
                ImGui.End();
                return;
            }

            if (curPos.X != this.Settings.RuneshapeWinX || curPos.Y != this.Settings.RuneshapeWinY)
            {
                this.Settings.RuneshapeWinX = curPos.X;
                this.Settings.RuneshapeWinY = curPos.Y;
            }

            if (!open)
            {
                ImGui.End();
                return;
            }

            if (monoliths.Count == 0)
            {
                ImGui.TextDisabled("No Runeshapes");
            }
            else
            {
                int areaLevel = area?.CurrentAreaLevel ?? 0;
                float baseFontSize = ImGui.GetFontSize() * this.Settings.TextScale;
                float kSquareSz = Math.Max(baseFontSize * 1.35f, 22f);
                float lineH = ImGui.GetTextLineHeight();
                float slotSz = lineH;
                float slotGap = 3.0f;
                var curTex = this.GetCurrencyTexture(this.Settings.DisplayCurrency);

                var bgReg = this.GetRuneUiTexture("RuneBgRegular.png", ref this.runeBgRegularTex, ref this.runeBgRegularTried);
                var bgPur = this.GetRuneUiTexture("RuneBgPurple.png", ref this.runeBgPurpleTex, ref this.runeBgPurpleTried);
                var glow = this.GetRuneUiTexture("RunePropagation.png", ref this.runePropagationTex, ref this.runePropagationTried);

                for (int mIdx = 0; mIdx < monoliths.Count; mIdx++)
                {
                    var m = monoliths[mIdx];
                    uint mColor = RsMonolithColors[mIdx % RsMonolithColors.Length];

                    var offers = new List<RsOffer>();
                    foreach (var rec in this.runeshapeRecipes)
                    {
                        if (rec.Size > m.HoleCount) continue;
                        if (areaLevel > 0 && rec.MaxLevel > 0 && (areaLevel < rec.MinLevel || areaLevel > rec.MaxLevel)) continue;

                        if (!m.IsUnique && m.AnchorIdx >= 0)
                        {
                            if (rec.RuneIdx == null || rec.RuneIdx.Count <= m.AnchorPos) continue;
                            if (rec.RuneIdx[m.AnchorPos] != m.AnchorIdx) continue;
                            if (rec.Size != m.HoleCount && !this.IsPartialAllowed(m.AnchorIdx, m.AnchorPos, rec.Size, areaLevel)) continue;
                        }

                        string rName = !string.IsNullOrEmpty(rec.Reward) ? rec.Reward : rec.Description;
                        int count = Math.Max(1, rec.RewardCount);
                        bool isPriced = false;
                        float chaos = 0f;
                        float displayVal = 0f;
                        string iconPath = string.Empty;

                        if (this.priceService != null && this.priceService.TryLookupPrice(rName, out var pr))
                        {
                            isPriced = true;
                            chaos = pr.Chaos * count;
                            displayVal = this.Settings.DisplayCurrency switch
                            {
                                DisplayCurrency.Divine => pr.Divine * count,
                                DisplayCurrency.Exalted => pr.Exalt * count,
                                _ => chaos
                            };
                            iconPath = pr.ItemIcon ?? string.Empty;
                        }

                        int weight = rec.ComboWeight;

                        offers.Add(new RsOffer
                        {
                            Recipe = rec,
                            Name = rName,
                            Count = count,
                            IsPriced = isPriced,
                            TotalChaos = chaos,
                            DisplayPrice = displayVal,
                            ComboWeight = weight,
                            IconPath = iconPath
                        });
                    }

                    // Sort by Chaos descending
                    offers.Sort((a, b) => b.TotalChaos.CompareTo(a.TotalChaos));
                    var bestOffer = offers.Count > 0 ? offers[0] : null;

                    // Measure elements for CollapsingHeader
                    float dotsW = (this.Settings.RsShowHdrRunes && m.HoleCount > 0)
                        ? (m.HoleCount * slotSz + (m.HoleCount - 1) * slotGap) : 0f;

                    bool bestPriced = this.Settings.RsShowHdrBest && bestOffer != null && bestOffer.IsPriced;
                    string bestNum = bestPriced ? FormatPriceNumberLocal(bestOffer!.DisplayPrice) : string.Empty;
                    float priceW = 0f;
                    if (bestPriced)
                    {
                        float iw = (curTex != null && curTex.Value.H > 0) ? lineH * (float)curTex.Value.W / curTex.Value.H : lineH;
                        priceW = iw + 4f + ImGui.CalcTextSize(bestNum).X;
                    }

                    int bestWeight = bestOffer?.ComboWeight ?? (offers.Count > 0 ? offers.Max(o => o.ComboWeight) : 0);
                    string hdrWBuf = (this.Settings.ShowRuneshapeWeights && bestWeight != 0)
                        ? (bestWeight > 0 ? $"+{bestWeight}" : $"{bestWeight}") : string.Empty;
                    float hdrWW = !string.IsNullOrEmpty(hdrWBuf) ? ImGui.CalcTextSize(hdrWBuf).X : 0f;

                    float reserve = dotsW + (priceW > 0f ? priceW + 10f : 0f) + (hdrWW > 0f ? hdrWW + 10f : 0f) + 8f;
                    float spaceW = ImGui.CalcTextSize(" ").X;
                    int padCnt = spaceW > 0f ? (int)MathF.Ceiling(reserve / spaceW) : 0;

                    // Colored square / badge with monolith index #1, #2...
                    var dl = ImGui.GetWindowDrawList();
                    if (this.Settings.RsShowHdrColor)
                    {
                        float frameH = ImGui.GetFrameHeight();
                        float sqYOff = (frameH > kSquareSz) ? (frameH - kSquareSz) * 0.5f : 0f;
                        var cp = ImGui.GetCursorScreenPos();
                        dl.AddRectFilled(new Vector2(cp.X, cp.Y + sqYOff), new Vector2(cp.X + kSquareSz, cp.Y + sqYOff + kSquareSz), m.IsCompleted ? 0xFF787878u : mColor, 3f);

                        // Draw #1, #2... text centered in the badge
                        string numStr = $"#{mIdx + 1}";
                        var numSz = ImGui.CalcTextSize(numStr);
                        float numX = cp.X + (kSquareSz - numSz.X) * 0.5f;
                        float numY = cp.Y + sqYOff + (kSquareSz - numSz.Y) * 0.5f;
                        dl.AddText(new Vector2(numX, numY), 0xFFFFFFFFu, numStr);

                        ImGui.Dummy(new Vector2(kSquareSz, frameH));
                        ImGui.SameLine();
                    }

                    // CollapsingHeader
                    bool wantOpen = !this.Settings.RuneshapeCollapsed.Contains(mColor);
                    ImGui.SetNextItemOpen(wantOpen);
                    string header = $"{new string(' ', padCnt)}###rscol_{mColor:X8}_{mIdx}";
                    bool headerOpen = ImGui.CollapsingHeader(header, ImGuiTreeNodeFlags.DefaultOpen);
                    if (headerOpen != wantOpen)
                    {
                        if (headerOpen) this.Settings.RuneshapeCollapsed.Remove(mColor);
                        else this.Settings.RuneshapeCollapsed.Add(mColor);
                        this.SaveSettings();
                    }

                    // Header decorations drawn with dl
                    var hmin = ImGui.GetItemRectMin();
                    var hmax = ImGui.GetItemRectMax();
                    float midY = (hmin.Y + hmax.Y) * 0.5f;
                    uint imgTint = m.IsCompleted ? 0xC8969696u : 0xFFFFFFFFu;
                    uint txtCol = m.IsCompleted ? 0xDC969696u : 0xFFFFFFFFu;

                    float xl = hmin.X + ImGui.GetTreeNodeToLabelSpacing();

                    // 1) Rune sockets
                    if (this.Settings.RsShowHdrRunes)
                    {
                        for (int slot = 0; slot < m.HoleCount; slot++)
                        {
                            bool prop = m.GoldenSlots.Contains(slot);
                            int rIdx = -1;
                            if (bestOffer != null && bestOffer.Recipe.RuneIdx != null && slot < bestOffer.Recipe.RuneIdx.Count)
                                rIdx = bestOffer.Recipe.RuneIdx[slot];
                            else if (slot == m.AnchorPos && m.AnchorIdx >= 0)
                                rIdx = m.AnchorIdx;

                            bool slotRare = rIdx >= 23 && rIdx <= 32;
                            var p0 = new Vector2(xl, midY - slotSz * 0.5f);
                            var p1 = new Vector2(xl + slotSz, midY + slotSz * 0.5f);
                            var bg = (slotRare && bgPur != null) ? bgPur : bgReg;
                            if (bg != null && bg.Value.Valid)
                            {
                                dl.AddImage(bg.Value.Ptr, p0, p1, Vector2.Zero, Vector2.One, imgTint);
                            }
                            else
                            {
                                float dr = slotSz * 0.5f - 1f;
                                dl.AddCircleFilled(new Vector2(xl + slotSz * 0.5f, midY), dr, 0xC8141414u);
                                dl.AddCircleFilled(new Vector2(xl + slotSz * 0.5f, midY), dr - 1f, prop ? 0xFFFFD23Cu : 0xEBDCDCDCu);
                            }

                            var runeTex = (rIdx >= 0 && rIdx < 34) ? this.GetRuneTexture(rIdx) : null;
                            if (runeTex != null && runeTex.Value.Valid)
                            {
                                float inset = slotSz * 0.14f;
                                dl.AddImage(runeTex.Value.Ptr, new Vector2(p0.X + inset, p0.Y + inset), new Vector2(p1.X - inset, p1.Y - inset), Vector2.Zero, Vector2.One, imgTint);
                            }

                            if (prop && !m.IsCompleted)
                            {
                                if (glow != null && glow.Value.Valid)
                                {
                                    float cx2 = xl + slotSz * 0.5f;
                                    float gw = slotSz * 1.08f;
                                    float gt = midY - slotSz * 1.20f;
                                    dl.AddImage(glow.Value.Ptr, new Vector2(cx2 - gw * 0.5f, gt), new Vector2(cx2 + gw * 0.5f, gt + slotSz * 1.76f));
                                }
                                else
                                {
                                    dl.AddCircle(new Vector2(xl + slotSz * 0.5f, midY), slotSz * 0.52f, 0xFFFFD23Cu, 0, 1.5f);
                                }
                            }

                            xl += slotSz + ((slot + 1 < m.HoleCount) ? slotGap : 0f);
                        }
                    }

                    // 2) Best price
                    if (bestPriced)
                    {
                        xl += 10f;
                        if (curTex != null && curTex.Value.Valid)
                        {
                            float iw = (curTex.Value.H > 0) ? lineH * (float)curTex.Value.W / curTex.Value.H : lineH;
                            dl.AddImage(curTex.Value.Ptr, new Vector2(xl, midY - lineH * 0.5f), new Vector2(xl + iw, midY + lineH * 0.5f), Vector2.Zero, Vector2.One, imgTint);
                            xl += iw + 4f;
                            dl.AddText(new Vector2(xl, midY - lineH * 0.5f), txtCol, bestNum);
                            xl += ImGui.CalcTextSize(bestNum).X;
                        }
                        else
                        {
                            string btxt = FormatPriceLocal(bestOffer!.DisplayPrice, this.Settings.DisplayCurrency);
                            dl.AddText(new Vector2(xl, midY - lineH * 0.5f), txtCol, btxt);
                            xl += ImGui.CalcTextSize(btxt).X;
                        }
                    }

                    // 3) Combination weight
                    if (!string.IsNullOrEmpty(hdrWBuf))
                    {
                        xl += 10f;
                        uint wCol = m.IsCompleted ? txtCol : 0xFF70EB70u; // green
                        dl.AddText(new Vector2(xl, midY - lineH * 0.5f), wCol, hdrWBuf);
                    }

                    // Expanded rows
                    if (headerOpen && offers.Count > 0)
                    {
                        if (m.IsCompleted) ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.5f);
                        ImGui.Indent(kSquareSz + 4f);
                        foreach (var rw in offers)
                        {
                            bool lineStarted = false;

                            // 1) Reward item icon
                            if (this.Settings.RsShowRowIcon && rw.IsPriced && !string.IsNullOrEmpty(rw.IconPath))
                            {
                                var itex = this.GetItemTexture(rw.IconPath);
                                if (itex != null && itex.Value.Valid)
                                {
                                    float lh = ImGui.GetTextLineHeight();
                                    ImGui.Image(itex.Value.Ptr, new Vector2(lh, lh));
                                    lineStarted = true;
                                }
                            }

                            // 2) Name + quantity
                            string txt = this.Settings.RsShowRowQty ? $"{rw.Name}  x{rw.Count}" : rw.Name;
                            if (!string.IsNullOrEmpty(txt))
                            {
                                if (lineStarted) ImGui.SameLine(0f, 4f);
                                if (rw.IsPriced) ImGui.TextUnformatted(txt);
                                else ImGui.TextDisabled(txt);
                                lineStarted = true;
                            }

                            // 3) Price (formatted number + currency icon)
                            if (this.Settings.RsShowRowPrice && rw.IsPriced)
                            {
                                if (lineStarted) ImGui.SameLine(0f, 10f);
                                string priceNum = FormatPriceNumberLocal(rw.DisplayPrice);
                                ImGui.TextUnformatted(priceNum);
                                if (curTex != null && curTex.Value.Valid)
                                {
                                    ImGui.SameLine(0f, 3f);
                                    float lh = ImGui.GetTextLineHeight();
                                    float iw = (curTex.Value.H > 0) ? lh * (float)curTex.Value.W / curTex.Value.H : lh;
                                    ImGui.Image(curTex.Value.Ptr, new Vector2(iw, lh));
                                }
                                lineStarted = true;
                            }

                            // 4) Weight
                            if (this.Settings.ShowRuneshapeWeights && rw.ComboWeight != 0)
                            {
                                if (lineStarted) ImGui.SameLine(0f, 10f);
                                var wc = rw.ComboWeight > 0 ? new Vector4(0.43f, 0.92f, 0.43f, 1f) : new Vector4(0.72f, 0.72f, 0.72f, 1f);
                                string wText = rw.ComboWeight > 0 ? $"+{rw.ComboWeight}" : $"{rw.ComboWeight}";
                                ImGui.TextColored(wc, wText);
                                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Total rune weight of this combination");
                                lineStarted = true;
                            }

                            // 5) Propagating rune icon if this recipe uses a golden slot
                            if (this.Settings.RsShowRowPropRunes && m.GoldenSlots.Count > 0)
                            {
                                bool hasProp = false;
                                foreach (int gs in m.GoldenSlots)
                                {
                                    if (rw.Recipe.RuneIdx != null && gs < rw.Recipe.RuneIdx.Count)
                                    {
                                        hasProp = true;
                                        break;
                                    }
                                }
                                if (hasProp)
                                {
                                    if (lineStarted) ImGui.SameLine(0f, 6f);
                                    if (glow != null && glow.Value.Valid)
                                    {
                                        float lh = ImGui.GetTextLineHeight();
                                        ImGui.Image(glow.Value.Ptr, new Vector2(lh * 0.9f, lh * 0.9f));
                                    }
                                    else
                                    {
                                        ImGui.TextColored(new Vector4(1.0f, 0.82f, 0.27f, 1.0f), "★");
                                    }
                                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Uses golden socket - propagating rune carries over");
                                }
                            }
                        }
                        ImGui.Unindent(kSquareSz + 4f);
                        if (m.IsCompleted) ImGui.PopStyleVar();
                    }

                    ImGui.Spacing();
                }
            }

            // Collapsible full catalog section at bottom
            if (ImGui.TreeNode("All Recipes Catalog"))
            {
                ImGui.SetNextItemWidth(260f);
                ImGui.InputTextWithHint("##rsSearch", "Search recipe...", ref this.rsSearchFilter, 64);
                if (!string.IsNullOrEmpty(this.rsSearchFilter))
                {
                    ImGui.SameLine();
                    if (ImGui.Button("Clear")) this.rsSearchFilter = string.Empty;
                }

                string[] colors = { "Red (Physical)", "Blue (Arcane/Cold)", "Green (Chaos/Poison)", "Yellow (Fire/Currency)", "Purple (Celestial/Rare)" };
                var filter = this.rsSearchFilter.Trim();

                for (int i = 0; i < colors.Length; i++)
                {
                    uint colorKey = (uint)i;
                    bool collapsed = this.Settings.RuneshapeCollapsed.Contains(colorKey);

                    var catRecipes = this.runeshapeRecipes.Where(r => r.Category == i);
                    if (!string.IsNullOrEmpty(filter))
                    {
                        catRecipes = catRecipes.Where(r =>
                            r.Description.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                            (r.Reward != null && r.Reward.Contains(filter, StringComparison.OrdinalIgnoreCase)) ||
                            r.Runes.Any(rn => rn.Contains(filter, StringComparison.OrdinalIgnoreCase)));
                    }

                    var list = catRecipes.ToList();
                    string headerTitle = $"{colors[i]} ({list.Count})##cat{i}";

                    if (ImGui.TreeNode(headerTitle))
                    {
                        if (collapsed) this.Settings.RuneshapeCollapsed.Remove(colorKey);
                        foreach (var rec in list)
                        {
                            this.DrawRuneshapeRecipeRow(rec);
                        }
                        ImGui.TreePop();
                    }
                    else
                    {
                        if (!collapsed) this.Settings.RuneshapeCollapsed.Add(colorKey);
                    }
                }
                ImGui.TreePop();
            }

            ImGui.End();
        }
    }
}
