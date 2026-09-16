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
    using TEHhub.Utils;

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
        private readonly HashSet<long> expandedMonoliths = new();
        private string rsSearchFilter = string.Empty;

        private static readonly uint[] RsMonolithColors = new uint[]
        {
            0xFF2A2AEB, // 0: Crimson Red
            0xFF14D7FF, // 1: Gold Yellow
            0xFF32E632, // 2: Bright Lime
            0xFFE6D200, // 3: Vivid Cyan
            0xFF0082FF, // 4: Electric Orange
            0xFFD814FF, // 5: Fuchsia Pink
            0xFFFFA01E, // 6: Sky Blue
            0xFFE6288C, // 7: Deep Violet
            0xFF6E7FFF, // 8: Coral Salmon
            0xFF78E100, // 9: Bright Emerald
            0xFFB42864, // 10: Royal Purple
            0xFF1EC8FF, // 11: Amber Bronze
            0xFFB4C800, // 12: Teal Turquoise
            0xFF8C14FF, // 13: Hot Rose
            0xFFA0FF46, // 14: Neon Mint
            0xFFFF6400, // 15: Dodger Azure
            0xFF00FFB4, // 16: Spring Chartreuse
            0xFFE696B4, // 17: Lavender
            0xFF28A5FF, // 18: Tangerine
            0xFFD2E646, // 19: Aquamarine
            0xFF4632FF, // 20: Ruby Scarlet
            0xFF14C8A0, // 21: Olive Chartreuse
            0xFFFF5064, // 22: Indigo Periwinkle
            0xFF96B4FF, // 23: Apricot Peach
            0xFF50D200, // 24: Bright Malachite
            0xFF9628C8, // 25: Plum Wine
            0xFF00E6FF, // 26: Sunburst Ochre
            0xFFFFE164, // 27: Ice Blue
            0xFF5014DC, // 28: Crimson Cherry
            0xFF82C828, // 29: Sea Green
            0xFFE65AD2, // 30: Orchid Purple
            0xFF64FAFF  // 31: Light Gold
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

        private int CalculateRecipeWeight(IEnumerable<string> runes)
        {
            var activeWeights = this.Settings.GetActiveWeights();
            int total = 0;
            foreach (var r in runes)
            {
                if (activeWeights != null && activeWeights.TryGetValue(r, out var w)) total += w;
                else if (DefaultRuneWeights.TryGetValue(r, out var defW)) total += defW;
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
        private CurrencyTex rsWeightIconTex = default;
        private bool rsWeightIconTried = false;
        private CurrencyTex rsPriceIconTex = default;
        private bool rsPriceIconTried = false;
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
        private readonly Dictionary<uint, MonolithData> trackedMonoliths = new();
        private string lastMonolithAreaHash = string.Empty;
        private string newProfileInput = string.Empty;

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
        private List<Vector4> cachedRealItemRects = new();
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

        [LibraryImport("winmm.dll", EntryPoint = "PlaySound", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool WinMmPlaySound(byte[]? ptrToSound, IntPtr hmod, uint fdwSound);

        private const uint SndAsync = 0x0001;
        private const uint SndMemory = 0x0004;
        private const uint SndNoDefault = 0x0002;

        private static byte[]? rawWavBytes;
        private static byte[]? scaledWavBytes;
        private static int cachedVolumePercent = -1;

        private readonly HashSet<uint> alertedEntityIds = new();
        private List<ActiveDropAlert> activeAlertDrops = new();
        private readonly List<DropAlertBanner> activeAlertBanners = new();
        private DateTime lastAlertSoundUtc = DateTime.MinValue;
        private readonly Dictionary<string, string> pathBasenameToItemName = new(StringComparer.OrdinalIgnoreCase);

        private readonly struct ActiveDropAlert
        {
            public ActiveDropAlert(uint entityId, Render render, string itemName, double displayValue, string displayCurrency, uint beamColor)
            {
                this.EntityId = entityId;
                this.Render = render;
                this.ItemName = itemName;
                this.DisplayValue = displayValue;
                this.DisplayCurrency = displayCurrency;
                this.BeamColor = beamColor;
            }

            public uint EntityId { get; }
            public Render Render { get; }
            public string ItemName { get; }
            public double DisplayValue { get; }
            public string DisplayCurrency { get; }
            public uint BeamColor { get; }
        }

        private sealed class DropAlertBanner
        {
            public DropAlertBanner(uint entityId, string itemName, double displayValue, string displayCurrency, DateTime createdUtc)
            {
                this.EntityId = entityId;
                this.ItemName = itemName;
                this.DisplayValue = displayValue;
                this.DisplayCurrency = displayCurrency;
                this.CreatedUtc = createdUtc;
            }

            public uint EntityId { get; }
            public string ItemName { get; }
            public double DisplayValue { get; }
            public string DisplayCurrency { get; }
            public DateTime CreatedUtc { get; }
        }

        private void PrepareWavBuffer()
        {
            try
            {
                var soundPath = Path.Combine(this.DllDirectory, "default.wav");
                if (!File.Exists(soundPath))
                {
                    soundPath = Path.Combine(AppContext.BaseDirectory, "Plugins", "NinjaPricer", "default.wav");
                }
                if (!File.Exists(soundPath)) return;

                if (rawWavBytes == null)
                {
                    rawWavBytes = File.ReadAllBytes(soundPath);
                }

                var vol = Math.Clamp(this.Settings.AlertVolumePercent, 0, 100);
                if (cachedVolumePercent == vol && scaledWavBytes != null)
                {
                    return;
                }

                cachedVolumePercent = vol;
                if (vol <= 0)
                {
                    scaledWavBytes = null;
                    return;
                }

                var copy = (byte[])rawWavBytes.Clone();
                int dataIndex = -1;
                for (int i = 0; i < copy.Length - 4; i++)
                {
                    if (copy[i] == 'd' && copy[i + 1] == 'a' && copy[i + 2] == 't' && copy[i + 3] == 'a')
                    {
                        dataIndex = i + 8;
                        break;
                    }
                }

                if (dataIndex != -1)
                {
                    float factor = vol / 100f;
                    for (int i = dataIndex; i < copy.Length - 1; i += 2)
                    {
                        short sample = (short)(copy[i] | (copy[i + 1] << 8));
                        short newSample = (short)Math.Clamp((int)(sample * factor), short.MinValue, short.MaxValue);
                        copy[i] = (byte)(newSample & 0xFF);
                        copy[i + 1] = (byte)((newSample >> 8) & 0xFF);
                    }
                }

                scaledWavBytes = copy;
            }
            catch (Exception ex)
            {
                PluginLog.Error("NinjaPricer", $"[NinjaPricer] Error preparing WAV buffer: {ex.Message}");
            }
        }

        private void PlayAlertSound(bool force = false)
        {
            if (!this.Settings.EnableAlertSound && !force) return;
            if (this.Settings.AlertVolumePercent <= 0) return;

            var now = DateTime.UtcNow;
            if (!force && (now - this.lastAlertSoundUtc).TotalMilliseconds < 500) return;
            this.lastAlertSoundUtc = now;

            try
            {
                this.PrepareWavBuffer();
                if (scaledWavBytes != null)
                {
                    WinMmPlaySound(scaledWavBytes, IntPtr.Zero, SndAsync | SndMemory | SndNoDefault);
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error("NinjaPricer", $"[NinjaPricer] Failed to play alert sound: {ex.Message}");
            }
        }

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
            this.LoadPathBasenameMapping();
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

        private void LoadPathBasenameMapping()
        {
            this.pathBasenameToItemName.Clear();
            string[] paths =
            {
                Path.Combine(this.DllDirectory, "pathBasenameToItemName.json"),
                Path.Combine(AppContext.BaseDirectory, "Plugins", "NinjaPricer", "pathBasenameToItemName.json"),
            };

            foreach (var p in paths)
            {
                if (File.Exists(p))
                {
                    try
                    {
                        var json = File.ReadAllText(p);
                        var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                        if (raw != null)
                        {
                            foreach (var (k, v) in raw)
                            {
                                if (!string.IsNullOrWhiteSpace(k) && !string.IsNullOrWhiteSpace(v))
                                {
                                    this.pathBasenameToItemName[k.Trim()] = v.Trim();
                                }
                            }
                            PluginLog.Info("NinjaPricer", $"[NinjaPricer] Loaded {this.pathBasenameToItemName.Count} path basename mappings from '{p}'");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        PluginLog.Error("NinjaPricer", $"[NinjaPricer] Failed reading pathBasenameToItemName from '{p}': {ex.Message}");
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
            this.cachedRealItemRects.Clear();
            this.alertedEntityIds.Clear();
            this.activeAlertDrops.Clear();
            lock (this.activeAlertBanners) this.activeAlertBanners.Clear();
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
                this.cachedRealItemRects.Clear();
                this.itemSlotCache.Clear();
                this.alertedEntityIds.Clear();
                this.activeAlertDrops.Clear();
                lock (this.activeAlertBanners) this.activeAlertBanners.Clear();
                this.lastGroundScanUtc = DateTime.MinValue;
                this.lastInvScanUtc = DateTime.MinValue;
                this.trackedMonoliths.Clear();
                this.expandedMonoliths.Clear();
                this.lastMonolithAreaHash = string.Empty;
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
                Path.Combine(this.DllDirectory, "resources", relativePath),
                Path.Combine(AppContext.BaseDirectory, "Plugins", "NinjaPricer", "resources", relativePath),
                Path.Combine(AppContext.BaseDirectory, "resources", relativePath),
                Path.Combine(AppContext.BaseDirectory, relativePath),
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
            var pathBasename = fullPath.Contains('/') ? fullPath[(fullPath.LastIndexOf('/') + 1)..] : fullPath;
            if (!string.IsNullOrEmpty(pathBasename) && this.pathBasenameToItemName.TryGetValue(pathBasename, out var mappedName))
            {
                return mappedName;
            }
            return pathBasename;
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

            if (ImGui.BeginTabItem(this.PluginText.Title("ninjapricer.tab.datasource", "Data Source", "tab_datasource")))
            {
                this.DrawTabDataSource();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem(this.PluginText.Title("ninjapricer.tab.drop_alerts", "Valuable Drop Alerts", "tab_drop_alerts")))
            {
                this.DrawTabDropAlerts();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem(this.PluginText.Title("ninjapricer.tab.categories", "Categories", "tab_categories")))
            {
                this.DrawTabCategories();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem(this.PluginText.Title("ninjapricer.tab.display", "Display Settings", "tab_display")))
            {
                this.DrawTabDisplaySettings();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem(this.PluginText.Title("ninjapricer.tab.expedition", "Expedition", "tab_expedition")))
            {
                this.DrawTabExpedition();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem(this.PluginText.Title("ninjapricer.tab.overlays", "Overlay Toggles", "tab_overlays")))
            {
                this.DrawTabOverlayToggles();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem(this.PluginText.Title("ninjapricer.tab.debug", "Debug", "tab_debug")))
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
            ImGui.TextWrapped(this.PluginText.T("ninjapricer.datasource.warning", "Warning: POE2 must be set to English. Item names are matched in English only - other languages will not work."));
            ImGui.PopStyleColor();
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            var st = this.priceService?.GetStatus() ?? default;
            var statusStr = st.Loaded
                ? this.PluginText.T("ninjapricer.datasource.status.loaded", "loaded")
                : this.PluginText.T("ninjapricer.datasource.status.loading", "loading...");
            ImGui.Text(string.Format(this.PluginText.T("ninjapricer.datasource.status", "Prices: {0}   Items: {1}"), statusStr, st.TotalItems));

            if (st.Loaded)
            {
                ImGui.Text(string.Format(this.PluginText.T("ninjapricer.datasource.rates", "Rates: 1 Divine = {0:F1} Chaos | 1 Exalted = {1:F1} Chaos"), st.DivineInChaos, st.ExaltedInChaos));
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            // League selection dropdown combo
            var leagues = NinjaPriceService.AvailableLeagues.ToArray();
            int selectedLeagueIdx = Array.IndexOf(leagues, this.Settings.League);
            if (selectedLeagueIdx < 0) selectedLeagueIdx = 0;
            ImGui.SetNextItemWidth(220f);
            if (ImGui.Combo(this.PluginText.Label("settings.league", "League", "LeagueSelect"), ref selectedLeagueIdx, leagues, leagues.Length))
            {
                this.Settings.League = leagues[selectedLeagueIdx];
                this.SaveSettings();
                this.priceService?.TriggerRefresh(this.Settings.League, this.Settings.PriceSource, this.Settings.EnabledCategories);
            }

            ImGui.SameLine();
            if (ImGui.Button(this.PluginText.T("button.refresh_prices_now", "Refresh Now")))
            {
                this.priceService?.TriggerRefresh(this.Settings.League, this.Settings.PriceSource, this.Settings.EnabledCategories);
            }

            // Price source
            ImGui.Spacing();
            ImGui.Text(this.PluginText.T("ninjapricer.datasource.source", "Data Source:"));
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
            if (ImGui.SliderInt(this.PluginText.Label("settings.refresh_interval", "Auto-refresh (minutes)", "AutoRefreshSlider"), ref arm, 5, 60))
            {
                this.Settings.AutoRefreshMinutes = arm;
                this.SaveSettings();
            }
        }

        private void DrawTabDropAlerts()
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.3f, 1.0f), this.PluginText.T("section.drop_alerts", "Valuable Drop Alerts"));
            ImGui.Spacing();

            bool sound = this.Settings.EnableAlertSound;
            if (ImGui.Checkbox(this.PluginText.Label("settings.enable_alert_sound", "Play sound on valuable drop", "AlertSoundCheck"), ref sound))
            {
                this.Settings.EnableAlertSound = sound;
                this.SaveSettings();
            }

            if (this.Settings.EnableAlertSound)
            {
                ImGui.Indent();
                int vol = this.Settings.AlertVolumePercent;
                if (ImGui.SliderInt(this.PluginText.Label("settings.alert_volume", "Alert sound volume (%)", "AlertVolSlider"), ref vol, 0, 100))
                {
                    this.Settings.AlertVolumePercent = vol;
                    this.SaveSettings();
                }

                ImGui.SameLine();
                if (ImGui.Button(this.PluginText.T("settings.test_sound", "Test Sound")))
                {
                    this.PlayAlertSound(force: true);
                }
                ImGui.Unindent();
            }

            ImGui.Spacing();
            bool banner = this.Settings.EnableAlertBanner;
            if (ImGui.Checkbox(this.PluginText.Label("settings.enable_alert_banner", "Show on-screen alert banner", "AlertBannerCheck"), ref banner))
            {
                this.Settings.EnableAlertBanner = banner;
                this.SaveSettings();
            }

            if (this.Settings.EnableAlertBanner)
            {
                ImGui.Indent();
                float dur = this.Settings.AlertBannerDurationSec;
                if (ImGui.SliderFloat(this.PluginText.Label("settings.alert_banner_duration", "Alert banner duration (sec)", "AlertDurSlider"), ref dur, 2f, 20f, "%.1f s"))
                {
                    this.Settings.AlertBannerDurationSec = dur;
                    this.SaveSettings();
                }
                ImGui.Unindent();
            }

            ImGui.Spacing();
            bool beam = this.Settings.EnableAlertBeam;
            if (ImGui.Checkbox(this.PluginText.Label("settings.enable_alert_beam", "Show light beam & pointers to drop", "AlertBeamCheck"), ref beam))
            {
                this.Settings.EnableAlertBeam = beam;
                this.SaveSettings();
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            string curName = this.Settings.DisplayCurrency switch
            {
                DisplayCurrency.Divine => "Divine",
                DisplayCurrency.Exalted => "Exalted",
                _ => "Chaos",
            };

            float minVal = this.Settings.AlertMinDisplayValue;
            if (ImGui.SliderFloat(this.PluginText.Label("settings.alert_min_value", $"Min drop value to alert ({curName})", "AlertMinValSlider"), ref minVal, 0.1f, 100f, $"%.1f {curName}"))
            {
                this.Settings.AlertMinDisplayValue = minVal;
                this.SaveSettings();
            }
        }

        private void DrawTabCategories()
        {
            ImGui.Spacing();
            ImGui.Text(this.PluginText.T("ninjapricer.categories.intro", "Select which item categories to fetch and display prices for:"));
            ImGui.Spacing();

            if (ImGui.Button(this.PluginText.T("button.enable_all", "Enable All")))
            {
                foreach (var k in this.Settings.EnabledCategories.Keys.ToList())
                {
                    this.Settings.EnabledCategories[k] = true;
                }
                this.SaveSettings();
            }
            ImGui.SameLine();
            if (ImGui.Button(this.PluginText.T("button.disable_all", "Disable All")))
            {
                foreach (var k in this.Settings.EnabledCategories.Keys.ToList())
                {
                    this.Settings.EnabledCategories[k] = false;
                }
                this.SaveSettings();
            }
            ImGui.SameLine();
            if (ImGui.Button(this.PluginText.T("button.apply_refresh", "Apply & Refresh Now")))
            {
                this.priceService?.TriggerRefresh(this.Settings.League, this.Settings.PriceSource, this.Settings.EnabledCategories);
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            (string Key, string Fallback)[] currencyCats =
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

            (string Key, string Fallback)[] uniqueCats =
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

            ImGui.TextColored(new Vector4(0.4f, 0.85f, 1.0f, 1.0f), this.PluginText.T("ninjapricer.category.currency_bulk", "Currency & Bulk Categories"));
            ImGui.Separator();
            foreach (var (key, fallback) in currencyCats)
            {
                bool isEnabled = !this.Settings.EnabledCategories.TryGetValue(key, out var val) || val;
                var localizedLabel = this.PluginText.Label($"category.{key}", fallback, $"cat_{key}");
                if (ImGui.Checkbox(localizedLabel, ref isEnabled))
                {
                    this.Settings.EnabledCategories[key] = isEnabled;
                    this.SaveSettings();
                }
            }

            ImGui.NextColumn();

            ImGui.TextColored(new Vector4(1.0f, 0.65f, 0.2f, 1.0f), this.PluginText.T("ninjapricer.category.uniques_maps", "Unique Items & Maps"));
            ImGui.Separator();
            foreach (var (key, fallback) in uniqueCats)
            {
                bool isEnabled = !this.Settings.EnabledCategories.TryGetValue(key, out var val) || val;
                var localizedLabel = this.PluginText.Label($"category.{key}", fallback, $"cat_{key}");
                if (ImGui.Checkbox(localizedLabel, ref isEnabled))
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
            ImGui.Text(this.PluginText.T("ninjapricer.display.value_display", "Value Display:"));
            int dc = (int)this.Settings.DisplayCurrency;
            if (ImGui.RadioButton(this.PluginText.T("currency.divine", "Divine (D)"), ref dc, 0)) { this.Settings.DisplayCurrency = DisplayCurrency.Divine; this.SaveSettings(); }
            ImGui.SameLine();
            if (ImGui.RadioButton(this.PluginText.T("currency.exalted", "Exalted (E)"), ref dc, 1)) { this.Settings.DisplayCurrency = DisplayCurrency.Exalted; this.SaveSettings(); }
            ImGui.SameLine();
            if (ImGui.RadioButton(this.PluginText.T("currency.chaos", "Chaos (C)"), ref dc, 2)) { this.Settings.DisplayCurrency = DisplayCurrency.Chaos; this.SaveSettings(); }

            ImGui.Spacing();
            ImGui.Text(this.PluginText.T("ninjapricer.display.price_style", "Price style:"));
            int pds = (int)this.Settings.PriceDisplayStyle;
            if (ImGui.RadioButton(this.PluginText.T("ninjapricer.display.style_icon", "Currency icon"), ref pds, 0)) { this.Settings.PriceDisplayStyle = PriceDisplayStyle.Image; this.SaveSettings(); }
            ImGui.SameLine();
            if (ImGui.RadioButton(this.PluginText.T("ninjapricer.display.style_text", "Text"), ref pds, 1)) { this.Settings.PriceDisplayStyle = PriceDisplayStyle.Text; this.SaveSettings(); }
            ImGui.SameLine();
            ImGui.TextDisabled(this.PluginText.T("ninjapricer.display.style_hint", "(icon = value + currency image)"));

            ImGui.Spacing();
            float ts = this.Settings.TextScale;
            if (ImGui.SliderFloat(this.PluginText.Label("settings.font_size", "Text size (Ninja / Item prices)", "TextScaleSlider"), ref ts, 0.5f, 2.5f, "%.1f"))
            {
                this.Settings.TextScale = ts;
                this.SaveSettings();
            }

            float us = this.Settings.UiScale;
            if (ImGui.SliderFloat(this.PluginText.Label("settings.ui_size", "UI size (Ninja / Item prices)", "UiScaleSlider"), ref us, 0.5f, 2.5f, "%.1f"))
            {
                this.Settings.UiScale = us;
                this.SaveSettings();
            }

            ImGui.Separator();

            // Ui Price Position
            string[] uiPositions = {
                this.PluginText.T("pos.top_left", "Top Left"),
                this.PluginText.T("pos.top_right", "Top Right"),
                this.PluginText.T("pos.bottom_left", "Bottom Left"),
                this.PluginText.T("pos.bottom_right", "Bottom Right")
            };
            int uiPos = (int)this.Settings.UiPricePosition;
            ImGui.SetNextItemWidth(200f);
            if (ImGui.Combo(this.PluginText.Label("ninjapricer.display.ui_price_pos", "Price position (Inventory/Stash)", "UiPosCombo"), ref uiPos, uiPositions, uiPositions.Length))
            {
                this.Settings.UiPricePosition = (UiPricePosition)uiPos;
                this.SaveSettings();
            }

            // Ground Price Position
            string[] gndPositions = {
                this.PluginText.T("pos.top", "Top"),
                this.PluginText.T("pos.bottom", "Bottom"),
                this.PluginText.T("pos.left", "Left"),
                this.PluginText.T("pos.right", "Right")
            };
            int gndPos = (int)this.Settings.GroundPricePosition;
            ImGui.SetNextItemWidth(200f);
            if (ImGui.Combo(this.PluginText.Label("ninjapricer.display.gnd_price_pos", "Price position (Ground items)", "GndPosCombo"), ref gndPos, gndPositions, gndPositions.Length))
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
                ImGui.TextColored(new Vector4(1.0f, 1.0f, 0.0f, 1.0f), this.PluginText.T("hotkey.press_any_key", "Press any key... (ESC to cancel)"));
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
                if (ImGui.Button($"{this.PluginText.T("button.set_hotkey", "Set Hotkey")}##{idSuffix}", new Vector2(100f, 0f)))
                {
                    this.captureTarget = vk;
                }
                if (vk != 0)
                {
                    ImGui.SameLine();
                    if (ImGui.Button($"{this.PluginText.T("button.clear", "Clear")}##{idSuffix}", new Vector2(60f, 0f)))
                    {
                        vk = 0;
                        this.captureTarget = null;
                        boundNow = true;
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
            if (ImGui.Checkbox(this.PluginText.Label("settings.show_overlay", "Show prices on dropped items", "ShowGroundCheck"), ref showGnd)) { this.Settings.ShowGroundPrices = showGnd; this.SaveSettings(); }

            bool showInv = this.Settings.ShowInventoryPrices;
            if (ImGui.Checkbox(this.PluginText.Label("settings.show_inventory_overlay", "Show prices in inventory", "ShowInvCheck"), ref showInv)) { this.Settings.ShowInventoryPrices = showInv; this.SaveSettings(); }
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1.0f, 0.75f, 0.2f, 1.0f), this.PluginText.T("ninjapricer.overlays.fps_warning", "(may affect FPS)"));

            bool showStash = this.Settings.ShowOtherInventoryPrices;
            if (ImGui.Checkbox(this.PluginText.Label("settings.show_stash_overlay", "Show prices in stash", "ShowStashCheck"), ref showStash)) { this.Settings.ShowOtherInventoryPrices = showStash; this.SaveSettings(); }
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1.0f, 0.75f, 0.2f, 1.0f), this.PluginText.T("ninjapricer.overlays.fps_warning", "(may affect FPS)"));

            bool showRitual = this.Settings.ShowRitualPrices;
            if (ImGui.Checkbox(this.PluginText.Label("settings.show_ritual_overlay", "Ritual", "ShowRitualCheck"), ref showRitual)) { this.Settings.ShowRitualPrices = showRitual; this.SaveSettings(); }
            ImGui.SameLine();
            ImGui.TextDisabled(this.PluginText.T("ninjapricer.overlays.ritual_hint", "(price items in the Ritual \"Favours\" shop)"));

            bool showIcons = this.Settings.ShowItemIcons;
            if (ImGui.Checkbox(this.PluginText.Label("settings.show_item_icons", "Show item icons", "ShowIconsCheck"), ref showIcons)) { this.Settings.ShowItemIcons = showIcons; this.SaveSettings(); }

            bool hideSlotHover = this.Settings.HideSlotPriceOnHover;
            if (ImGui.Checkbox(this.PluginText.Label("settings.hide_slot_price_on_hover", "Hide price on hovered item (Inventory / Stash)", "HideSlotHoverCheck"), ref hideSlotHover))
            {
                this.Settings.HideSlotPriceOnHover = hideSlotHover;
                this.SaveSettings();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(this.PluginText.T("settings.hide_slot_price_on_hover.tooltip", "Hides the price overlay when the mouse cursor is over an item slot in Inventory or Stash, allowing you to read game item tooltips clearly."));

            ImGui.Separator();
            int hhk = this.Settings.HideHotkey;
            if (this.DrawHotkeyCaptureRow(this.PluginText.T("ninjapricer.overlays.hold_to_hide", "Hold-to-hide hotkey:"), "hold", ref hhk))
            {
                this.Settings.HideHotkey = hhk;
                this.SaveSettings();
            }

            ImGui.Spacing();
            if (ImGui.TreeNode(this.PluginText.Title("ninjapricer.overlays.category_filters", "Category filters", "CatFilters")))
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

        private void DrawTabExpedition()
        {
            ImGui.Spacing();

            // ================================================================
            // 1. LargeMap & World Markers (Map Badges & 3D World Markers)
            // ================================================================
            ImGui.TextColored(new Vector4(0.4f, 0.85f, 1.0f, 1.0f), this.PluginText.T("ninjapricer.expedition.map_markers_header", "LargeMap & World Markers"));
            ImGui.Separator();
            ImGui.Spacing();

            bool rsMini = this.Settings.RsMinimalMapBadges;
            if (ImGui.Checkbox(this.PluginText.Label("ninjapricer.overlays.minimal_map_badges", "Minimal LargeMap/World badges (Color + Price only)", "RsMinimalMapCheck"), ref rsMini))
            {
                this.Settings.RsMinimalMapBadges = rsMini;
                this.SaveSettings();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(this.PluginText.T("ninjapricer.overlays.minimal_map_badges.tooltip", "Hides rune sockets and item icons on LargeMap/World badges, showing only the monolith color square and price."));

            bool rsmk = this.Settings.ShowRuneshapeWorldMarkers;
            if (ImGui.Checkbox(this.PluginText.Label("ninjapricer.overlays.show_monolith_markers", "Show monolith markers in 3D world", "ShowMonolithMarkersCheck"), ref rsmk))
            {
                this.Settings.ShowRuneshapeWorldMarkers = rsmk;
                this.SaveSettings();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(this.PluginText.T("ninjapricer.overlays.show_monolith_markers.tooltip", "Draws numbered (#1, #2...) colored badges floating over monolith pillars in the game world."));

            ImGui.Spacing();
            float expTs = this.Settings.ExpeditionTextScale;
            if (ImGui.SliderFloat(this.PluginText.Label("settings.expedition_font_size", "Text size (Expedition / Runeshape)", "ExpTextScaleSlider"), ref expTs, 0.5f, 2.5f, "%.1f"))
            {
                this.Settings.ExpeditionTextScale = expTs;
                this.SaveSettings();
            }

            float expUs = this.Settings.ExpeditionUiScale;
            if (ImGui.SliderFloat(this.PluginText.Label("settings.expedition_ui_size", "UI size (Expedition / LargeMap)", "ExpUiScaleSlider"), ref expUs, 0.5f, 2.5f, "%.1f"))
            {
                this.Settings.ExpeditionUiScale = expUs;
                this.SaveSettings();
            }

            float rsBgAlpha = this.Settings.RsBadgeBgAlpha;
            if (ImGui.SliderFloat(this.PluginText.Label("ninjapricer.overlays.monolith_badge_bg_alpha", "Badge background opacity", "RsBadgeBgAlphaSlider"), ref rsBgAlpha, 0.0f, 1.0f, "%.2f"))
            {
                this.Settings.RsBadgeBgAlpha = Math.Clamp(rsBgAlpha, 0.0f, 1.0f);
                this.SaveSettings();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(this.PluginText.T("ninjapricer.overlays.monolith_badge_bg_alpha.tooltip", "Adjusts the opacity/transparency of the monolith badge background (0.0 = completely transparent, 1.0 = solid)."));

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            // ================================================================
            // 2. Runeshape Overlay & Movable Window
            // ================================================================
            ImGui.TextColored(new Vector4(0.4f, 0.85f, 1.0f, 1.0f), this.PluginText.T("ninjapricer.expedition.window_header", "Runeshape Overlay Window"));
            ImGui.Separator();
            ImGui.Spacing();

            bool showRs = this.Settings.ShowRuneshapePrices;
            if (ImGui.Checkbox(this.PluginText.Label("ninjapricer.overlays.runeshape", "Runeshape (in-game combination panel)", "ShowRsCheck"), ref showRs)) { this.Settings.ShowRuneshapePrices = showRs; this.SaveSettings(); }
            ImGui.SameLine();
            ImGui.TextDisabled(this.PluginText.T("ninjapricer.overlays.runeshape_hint", "(price rewards in the Runeshape Combinations panel)"));

            bool showRsWin = this.Settings.ShowRuneshapeWindow;
            if (ImGui.Checkbox(this.PluginText.Label("ninjapricer.overlays.runeshape_win", "Runeshape window", "ShowRsWinCheck"), ref showRsWin)) { this.Settings.ShowRuneshapeWindow = showRsWin; this.SaveSettings(); }
            ImGui.SameLine();
            if (ImGui.Button(this.Settings.ShowRuneshapeWindow ? this.PluginText.T("button.close_window", "Close Window") : this.PluginText.T("button.open_window", "Open Window")))
            {
                this.Settings.ShowRuneshapeWindow = !this.Settings.ShowRuneshapeWindow;
                this.SaveSettings();
            }
            ImGui.SameLine();
            if (ImGui.Button(this.PluginText.T("button.reset_pos", "Reset Pos")))
            {
                this.Settings.RuneshapeWinX = 100f;
                this.Settings.RuneshapeWinY = 100f;
                this.Settings.ShowRuneshapeWindow = true;
                this.SaveSettings();
            }
            ImGui.SameLine();
            ImGui.TextDisabled(this.PluginText.T("ninjapricer.overlays.runeshape_win_hint", "(movable overlay listing each Runeshape with prices)"));

            // Advanced settings for Runeshape window
            ImGui.Indent();
            if (ImGui.TreeNode(this.PluginText.Title("ninjapricer.overlays.runeshape_advanced", "Runeshape window: advanced settings", "RsWinAdv")))
            {
                ImGui.Spacing();

                int rshk = this.Settings.RuneshapeWinHotkey;
                if (this.DrawHotkeyCaptureRow(this.PluginText.T("ninjapricer.overlays.hotkey_show_hide", "Show/hide hotkey:"), "rswin", ref rshk))
                {
                    this.Settings.RuneshapeWinHotkey = rshk;
                    this.runeshapeWinHotkeyWasDown = false;
                    this.SaveSettings();
                }

                bool rhoh = this.Settings.RuneshapeWinHideOnHover;
                if (ImGui.Checkbox(this.PluginText.Label("ninjapricer.overlays.hide_on_hover", "Hide on mouse hover", "HideOnHoverCheck"), ref rhoh)) { this.Settings.RuneshapeWinHideOnHover = rhoh; this.SaveSettings(); }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(this.PluginText.T("ninjapricer.overlays.hide_on_hover.tooltip", "The overlay disappears while the mouse cursor is over it and reappears when cursor leaves."));

                float rwa = this.Settings.RuneshapeWinAlpha;
                ImGui.SetNextItemWidth(200f);
                if (ImGui.SliderFloat(this.PluginText.Label("settings.opacity", "Opacity", "RsWinAlphaSlider"), ref rwa, 0.1f, 1.0f, "%.2f"))
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
                ImGui.TextDisabled(this.PluginText.T("ninjapricer.expedition.header_elements", "Header elements:"));
                bool hc = this.Settings.RsShowHdrColor;
                if (ImGui.Checkbox("Color square", ref hc)) { this.Settings.RsShowHdrColor = hc; this.SaveSettings(); }
                ImGui.SameLine();
                bool hr = this.Settings.RsShowHdrRunes;
                if (ImGui.Checkbox("Rune sockets", ref hr)) { this.Settings.RsShowHdrRunes = hr; this.SaveSettings(); }
                ImGui.SameLine();
                bool hb = this.Settings.RsShowHdrBest;
                if (ImGui.Checkbox("Best reward price", ref hb)) { this.Settings.RsShowHdrBest = hb; this.SaveSettings(); }

                ImGui.TextDisabled(this.PluginText.T("ninjapricer.expedition.expanded_elements", "Expanded list elements:"));
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
                bool compactRows = this.Settings.RsCompactRows;
                if (ImGui.Checkbox(this.PluginText.Label("settings.runeshape_compact_rows", "Compact layout (optimized for 1080p / FullHD)", "RsCompactRowsCheck"), ref compactRows))
                {
                    this.Settings.RsCompactRows = compactRows;
                    this.SaveSettings();
                }

                if (this.Settings.RsCompactRows)
                {
                    ImGui.SameLine();
                    float rScale = this.Settings.RsRowScale;
                    ImGui.SetNextItemWidth(140f);
                    if (ImGui.SliderFloat(this.PluginText.Label("settings.runeshape_row_scale", "Row scale", "RsRowScaleSlider"), ref rScale, 0.5f, 1.2f, "%.2f"))
                    {
                        this.Settings.RsRowScale = rScale;
                        this.SaveSettings();
                    }
                }

                ImGui.Spacing();
                ImGui.TreePop();
            }
            ImGui.Unindent();

            bool showWeights = this.Settings.ShowRuneshapeWeights;
            if (ImGui.Checkbox(this.PluginText.Label("settings.show_runeshape_weights", "Runeshape weights", "ShowWeightsCheck"), ref showWeights)) { this.Settings.ShowRuneshapeWeights = showWeights; this.SaveSettings(); }

            bool prioritizeWeight = this.Settings.RsPrioritizeWeight;
            if (ImGui.Checkbox(this.PluginText.Label("settings.runeshape_prioritize_weight", "Prioritize highest weight (+)", "RsPrioritizeWeightCheck"), ref prioritizeWeight)) { this.Settings.RsPrioritizeWeight = prioritizeWeight; this.SaveSettings(); }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            // ================================================================
            // 2. Rune Weights Editor (Profiles + Sliders)
            // ================================================================
            ImGui.TextColored(new Vector4(0.4f, 0.85f, 1.0f, 1.0f), this.PluginText.T("ninjapricer.expedition.weights_header", "Rune weights"));
            ImGui.TextDisabled(this.PluginText.T("ninjapricer.expedition.weights_desc", "Independent of the RuneShape tab. These weights drive the Glow runes placement mode: the planner stacks unique glow runes by them."));
            ImGui.Spacing();

            // Ensure profile exists
            if (this.Settings.RuneWeightProfiles == null || this.Settings.RuneWeightProfiles.Count == 0)
            {
                var def = RuneWeightProfile.CreateDefault("Default");
                this.Settings.RuneWeightProfiles = new List<RuneWeightProfile> { def };
                this.Settings.ActiveRuneWeightProfile = "Default";
                this.SaveSettings();
            }

            var currentProfile = this.Settings.RuneWeightProfiles.Find(p => string.Equals(p.Name, this.Settings.ActiveRuneWeightProfile, StringComparison.OrdinalIgnoreCase))
                                 ?? this.Settings.RuneWeightProfiles[0];

            // Profile bar
            ImGui.SetNextItemWidth(140f);
            if (ImGui.BeginCombo("##ProfileSelector", currentProfile.Name))
            {
                foreach (var prof in this.Settings.RuneWeightProfiles)
                {
                    bool isSelected = string.Equals(prof.Name, currentProfile.Name, StringComparison.OrdinalIgnoreCase);
                    if (ImGui.Selectable(prof.Name, isSelected))
                    {
                        this.Settings.ActiveRuneWeightProfile = prof.Name;
                        this.SaveSettings();
                        this.RecalculateRecipeWeights();
                    }
                    if (isSelected) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(130f);
            ImGui.InputTextWithHint("##NewProfName", this.PluginText.T("ninjapricer.expedition.new_profile_hint", "profile name"), ref this.newProfileInput, 32);

            ImGui.SameLine();
            if (ImGui.Button(this.PluginText.T("button.new_profile", "New")))
            {
                string pName = string.IsNullOrWhiteSpace(this.newProfileInput) ? $"Profile {this.Settings.RuneWeightProfiles.Count + 1}" : this.newProfileInput.Trim();
                if (!this.Settings.RuneWeightProfiles.Any(p => string.Equals(p.Name, pName, StringComparison.OrdinalIgnoreCase)))
                {
                    var np = RuneWeightProfile.CreateDefault(pName);
                    this.Settings.RuneWeightProfiles.Add(np);
                    this.Settings.ActiveRuneWeightProfile = pName;
                    this.newProfileInput = string.Empty;
                    this.SaveSettings();
                    this.RecalculateRecipeWeights();
                }
            }

            ImGui.SameLine();
            if (ImGui.Button(this.PluginText.T("button.rename_profile", "Rename")))
            {
                if (!string.IsNullOrWhiteSpace(this.newProfileInput))
                {
                    string pName = this.newProfileInput.Trim();
                    if (!this.Settings.RuneWeightProfiles.Any(p => string.Equals(p.Name, pName, StringComparison.OrdinalIgnoreCase)))
                    {
                        currentProfile.Name = pName;
                        this.Settings.ActiveRuneWeightProfile = pName;
                        this.newProfileInput = string.Empty;
                        this.SaveSettings();
                    }
                }
            }

            ImGui.SameLine();
            if (ImGui.Button(this.PluginText.T("button.dup_profile", "Dup")))
            {
                string copyName = $"{currentProfile.Name} (Copy)";
                int count = 1;
                while (this.Settings.RuneWeightProfiles.Any(p => string.Equals(p.Name, copyName, StringComparison.OrdinalIgnoreCase)))
                {
                    count++;
                    copyName = $"{currentProfile.Name} (Copy {count})";
                }
                var copyProf = new RuneWeightProfile
                {
                    Name = copyName,
                    Weights = new Dictionary<string, int>(currentProfile.Weights, StringComparer.OrdinalIgnoreCase)
                };
                this.Settings.RuneWeightProfiles.Add(copyProf);
                this.Settings.ActiveRuneWeightProfile = copyName;
                this.SaveSettings();
                this.RecalculateRecipeWeights();
            }

            if (this.Settings.RuneWeightProfiles.Count > 1)
            {
                ImGui.SameLine();
                if (ImGui.Button(this.PluginText.T("button.del_profile", "Del")))
                {
                    this.Settings.RuneWeightProfiles.Remove(currentProfile);
                    this.Settings.ActiveRuneWeightProfile = this.Settings.RuneWeightProfiles[0].Name;
                    this.SaveSettings();
                    this.RecalculateRecipeWeights();
                }
            }

            // Quick reset buttons aligned to right
            float availW = ImGui.GetContentRegionAvail().X;
            float btnW1 = 150f;
            float btnW2 = 80f;
            if (availW > (btnW1 + btnW2 + 20f))
            {
                ImGui.SameLine(ImGui.GetCursorPosX() + availW - (btnW1 + btnW2 + 10f));
            }
            else
            {
                ImGui.Spacing();
            }

            if (ImGui.Button(this.PluginText.T("button.reset_tier_defaults", "Reset to tier defaults"), new Vector2(btnW1, 0f)))
            {
                var def = RuneWeightProfile.CreateDefault(currentProfile.Name);
                currentProfile.Weights = def.Weights;
                this.SaveSettings();
                this.RecalculateRecipeWeights();
            }
            ImGui.SameLine();
            if (ImGui.Button(this.PluginText.T("button.zero_all", "Zero all"), new Vector2(btnW2, 0f)))
            {
                foreach (var rName in NinjaRuneshapeHelper.RuneNames)
                {
                    currentProfile.Weights[rName] = 0;
                }
                this.SaveSettings();
                this.RecalculateRecipeWeights();
            }

            ImGui.Spacing();

            // Rare runes list (11 runes) and Common runes list (23 runes)
            string[] rareRunes = { "Opulent", "Power", "Bond", "Sky", "Death", "Soul", "Earth", "Time", "Life", "Ward", "Oath" };
            var rareSet = new HashSet<string>(rareRunes, StringComparer.OrdinalIgnoreCase);
            var commonRunes = NinjaRuneshapeHelper.RuneNames.Where(r => !rareSet.Contains(r)).ToArray();

            void DrawRuneSlidersSection(string sectionTitle, string[] runes, Vector4 headerCol)
            {
                ImGui.TextColored(headerCol, sectionTitle);
                ImGui.Spacing();

                float colWidth = (ImGui.GetContentRegionAvail().X - 30f) * 0.5f;
                int half = (runes.Length + 1) / 2;

                ImGui.Columns(2, $"##{sectionTitle}Cols", false);
                ImGui.SetColumnWidth(0, colWidth + 15f);
                ImGui.SetColumnWidth(1, colWidth + 15f);

                for (int i = 0; i < runes.Length; i++)
                {
                    if (i == half) ImGui.NextColumn();

                    var rName = runes[i];
                    int rIdx = Array.IndexOf(NinjaRuneshapeHelper.RuneNames, rName);
                    var runeTex = this.GetRuneTexture(rIdx);

                    float lh = ImGui.GetTextLineHeight();
                    float iconSz = lh * 1.25f;

                    if (runeTex != null && runeTex.Value.Valid)
                    {
                        ImGui.Image(runeTex.Value.Ptr, new Vector2(iconSz, iconSz));
                        ImGui.SameLine(0f, 6f);
                    }

                    // Rune Name with fixed label width
                    ImGui.AlignTextToFramePadding();
                    if (string.Equals(rName, "Opulent", StringComparison.OrdinalIgnoreCase))
                        ImGui.TextColored(new Vector4(1f, 0.85f, 0.2f, 1f), $"{rName,-11}");
                    else if (rareSet.Contains(rName))
                        ImGui.TextColored(new Vector4(0.85f, 0.6f, 1.0f, 1f), $"{rName,-11}");
                    else
                        ImGui.TextColored(new Vector4(0.7f, 0.85f, 1.0f, 1f), $"{rName,-11}");

                    ImGui.SameLine(0f, 8f);

                    if (!currentProfile.Weights.TryGetValue(rName, out int curVal))
                    {
                        curVal = DefaultRuneWeights.TryGetValue(rName, out int defW) ? defW : 20;
                        currentProfile.Weights[rName] = curVal;
                    }

                    ImGui.SetNextItemWidth(Math.Max(120f, colWidth - iconSz - 160f));
                    if (ImGui.SliderInt($"##slider_{rName}", ref curVal, 0, 500, ""))
                    {
                        currentProfile.Weights[rName] = curVal;
                        this.SaveSettings();
                        this.RecalculateRecipeWeights();
                    }

                    ImGui.SameLine(0f, 8f);
                    if (curVal > 0)
                        ImGui.TextColored(new Vector4(0.43f, 0.92f, 0.43f, 1f), $"+{curVal,3}");
                    else
                        ImGui.TextDisabled($"{curVal,4}");
                }

                ImGui.Columns(1);
                ImGui.Spacing();
            }

            DrawRuneSlidersSection(this.PluginText.T("ninjapricer.expedition.rare_runes", "Rare runes"), rareRunes, new Vector4(0.85f, 0.6f, 1.0f, 1f));
            ImGui.Separator();
            ImGui.Spacing();
            DrawRuneSlidersSection(this.PluginText.T("ninjapricer.expedition.common_runes", "Common runes"), commonRunes, new Vector4(0.5f, 0.8f, 1.0f, 1f));
        }

        private void RecalculateRecipeWeights()
        {
            if (this.runeshapeRecipes == null || this.runeshapeRecipes.Count == 0) return;
            foreach (var r in this.runeshapeRecipes)
            {
                r.ComboWeight = this.CalculateRecipeWeight(r.Runes);
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

            // Global overlay visibility gate (hold-to-hide hotkey active)
            if (this.Settings.HideHotkey != 0 && (GetAsyncKeyState(this.Settings.HideHotkey) & 0x8000) != 0) return;

            // Runeshape Monoliths (in-world badges / large map markers & movable window)
            if (!inTownOrHideout && (this.Settings.ShowRuneshapeWorldMarkers || this.Settings.ShowRuneshapeWindow))
            {
                var activeMonoliths = this.GetActiveMonoliths();

                if (this.Settings.ShowRuneshapeWorldMarkers && activeMonoliths.Count > 0)
                {
                    this.DrawMonolithWorldMarkers(activeMonoliths);
                }

                if (this.Settings.ShowRuneshapeWindow && activeMonoliths.Count > 0)
                {
                    this.DrawRuneshapeWindow(activeMonoliths);
                }
            }
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

            // Ground overlay & drop alerts
            bool needGroundScan = this.Settings.ShowGroundPrices ||
                                  this.Settings.EnableAlertSound ||
                                  this.Settings.EnableAlertBanner ||
                                  this.Settings.EnableAlertBeam;

            if (needGroundScan)
            {
                var swG = Stopwatch.StartNew();
                if ((DateTime.UtcNow - this.lastGroundScanUtc).TotalMilliseconds >= this.Settings.ScanIntervalMs)
                {
                    this.lastGroundScanUtc = DateTime.UtcNow;
                    this.ScanGroundItems();
                }

                if (this.Settings.ShowGroundPrices)
                {
                    this.DrawGroundTags();
                }

                if (this.Settings.EnableAlertBeam || this.Settings.EnableAlertBanner)
                {
                    this.DrawDropAlerts();
                }

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
            var newAlertDrops = new List<ActiveDropAlert>();

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

                    string curSuffix = this.Settings.DisplayCurrency switch
                    {
                        DisplayCurrency.Divine => "Divine",
                        DisplayCurrency.Exalted => "Exalted",
                        _ => "Chaos",
                    };

                    if (this.Settings.ShowGroundPrices)
                    {
                        newTags.Add(new GroundTag
                        {
                            // Use WorldPosition.Z (label/healthbar height) so the price tag
                            // projects to the same screen point as the floating item name label.
                            WorldPos = new Vector3(render.WorldPosition.X, render.WorldPosition.Y, render.WorldPosition.Z),
                            DisplayValue = displayVal,
                            Chaos = chaos,
                            IconPath = price.ItemIcon,
                        });
                    }

                    if (displayVal >= this.Settings.AlertMinDisplayValue)
                    {
                        uint beamCol = 0xFFE5B826u;
                        newAlertDrops.Add(new ActiveDropAlert(entity.Id, render, itemName, displayVal, curSuffix, beamCol));

                        if (this.alertedEntityIds.Add(entity.Id))
                        {
                            this.PlayAlertSound();
                            if (this.Settings.EnableAlertBanner)
                            {
                                lock (this.activeAlertBanners)
                                {
                                    this.activeAlertBanners.Add(new DropAlertBanner(entity.Id, itemName, displayVal, curSuffix, DateTime.UtcNow));
                                    if (this.activeAlertBanners.Count > 5)
                                    {
                                        this.activeAlertBanners.RemoveRange(0, this.activeAlertBanners.Count - 5);
                                    }
                                }
                            }
                        }
                    }
                }
                else
                {
                    this.perfLookupMiss++;
                }
            }

            this.cachedGroundTags.Clear();
            this.cachedGroundTags.AddRange(newTags);
            this.activeAlertDrops = newAlertDrops;
        }

        private void DrawGroundTags()
        {
            if (this.cachedGroundTags.Count == 0) return;
            var gameUi = Core.States.InGameStateObject?.GameUi;
            if (gameUi != null && gameUi.IsAnyLargePanelOpen) return;

            var world = Core.States.InGameStateObject?.CurrentWorldInstance;
            if (world == null) return;

            var dl = ImGui.GetBackgroundDrawList();
            float fontSize = ImGui.GetFontSize() * this.Settings.TextScale;
            Vector2 mousePos = ImGui.GetMousePos();
            bool hideHover = this.Settings.HideSlotPriceOnHover;

            if (hideHover)
            {
                var inGame = Core.States.InGameStateObject;
                if (inGame?.MouseOverEntity != null && inGame.MouseOverEntity.IsValid && inGame.MouseOverEntity.Address != IntPtr.Zero)
                {
                    return; // Hide ALL ground tags when pointing at any world entity/loot
                }

                foreach (var tag in this.cachedGroundTags)
                {
                    var screenPos = world.WorldToScreen(new Vector2(tag.WorldPos.X, tag.WorldPos.Y), tag.WorldPos.Z);
                    if (screenPos == Vector2.Zero) continue;

                    var measured = this.MeasurePriceTag(tag.DisplayValue, fontSize, tag.IconPath);
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

                    if (mousePos.X >= x && mousePos.X <= x + measured.TotalW &&
                        mousePos.Y >= y && mousePos.Y <= y + measured.TotalH)
                    {
                        return; // Hide ALL ground tags when pointing at any ground price tag
                    }
                }
            }

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

        private void DrawDropAlerts()
        {
            var inGameState = Core.States.InGameStateObject;
            if (inGameState == null) return;
            var world = inGameState.CurrentWorldInstance;
            if (world == null) return;
            var windowArea = Core.Process.WindowArea;
            var fg = ImGui.GetForegroundDrawList();
            var now = DateTime.UtcNow;

            // 1. Draw Beams & Pointers to ground items
            if (this.Settings.EnableAlertBeam && this.activeAlertDrops.Count > 0)
            {
                var playerRender = inGameState.CurrentAreaInstance?.Player?.TryGetComponent<Render>(out var pR) == true ? pR : null;
                Vector2 playerScreen = Vector2.Zero;
                if (playerRender != null)
                {
                    playerScreen = world.WorldToScreen(playerRender.WorldPosition, playerRender.TerrainHeight);
                }

                foreach (var drop in this.activeAlertDrops)
                {
                    var screen = world.WorldToScreen(drop.Render.WorldPosition, drop.Render.TerrainHeight);
                    if (screen == Vector2.Zero) continue;

                    bool isOnScreen = screen.X >= 20 && screen.X <= windowArea.Width - 20 &&
                                      screen.Y >= 20 && screen.Y <= windowArea.Height - 20;

                    var colorRgba = ImGui.ColorConvertU32ToFloat4(drop.BeamColor);
                    var glowInner = ImGui.ColorConvertFloat4ToU32(new Vector4(colorRgba.X, colorRgba.Y, colorRgba.Z, 0.85f));
                    var glowOuter = ImGui.ColorConvertFloat4ToU32(new Vector4(colorRgba.X, colorRgba.Y, colorRgba.Z, 0.35f));

                    if (isOnScreen)
                    {
                        // Pulsing / glowing ground rings
                        fg.AddCircleFilled(screen, 6f, glowInner);
                        fg.AddCircle(screen, 16f, glowInner, 24, 2.5f);
                        fg.AddCircle(screen, 26f, glowOuter, 24, 1.5f);

                        // Vertical light beam rising into sky
                        var topScreen = world.WorldToScreen(drop.Render.WorldPosition, drop.Render.TerrainHeight - 350f);
                        if (topScreen == Vector2.Zero)
                        {
                            topScreen = new Vector2(screen.X, screen.Y - 220f);
                        }

                        // 3-layer glowing beam
                        fg.AddLine(screen, topScreen, glowOuter, 8f);
                        fg.AddLine(screen, topScreen, glowInner, 4f);
                        fg.AddLine(screen, topScreen, 0xFFFFFFFFu, 1.5f);

                        // Top star/circle cap
                        fg.AddCircleFilled(topScreen, 4f, glowInner);

                        // Direction trace line from player if far away
                        if (playerScreen != Vector2.Zero && Vector2.Distance(playerScreen, screen) > 180f)
                        {
                            var lineCol = ImGui.ColorConvertFloat4ToU32(new Vector4(colorRgba.X, colorRgba.Y, colorRgba.Z, 0.45f));
                            fg.AddLine(playerScreen, screen, lineCol, 1.5f);
                        }
                    }
                    else
                    {
                        // Off-screen indicator arrow clamped to screen border
                        var screenCenter = new Vector2(windowArea.Width / 2f, windowArea.Height / 2f);
                        var diff = screen - screenCenter;
                        var dir = diff.LengthSquared() > 0.001f ? Vector2.Normalize(diff) : new Vector2(0, -1);

                        var margin = 45f;
                        var clampedX = Math.Clamp(screenCenter.X + (dir.X * (windowArea.Width / 2f - margin)), margin, windowArea.Width - margin);
                        var clampedY = Math.Clamp(screenCenter.Y + (dir.Y * (windowArea.Height / 2f - margin)), margin, windowArea.Height - margin);
                        var edgePos = new Vector2(clampedX, clampedY);

                        var indicatorCol = ImGui.ColorConvertFloat4ToU32(new Vector4(colorRgba.X, colorRgba.Y, colorRgba.Z, 0.95f));

                        // Draw pointer diamond & line towards target
                        fg.AddCircleFilled(edgePos, 8f, indicatorCol);
                        fg.AddLine(edgePos, edgePos + (dir * 18f), indicatorCol, 3f);

                        // Offscreen label chip
                        var text = $"{drop.ItemName} ({drop.DisplayValue:0.##} {drop.DisplayCurrency})";
                        var textSize = ImGui.CalcTextSize(text);
                        var textPos = edgePos + new Vector2(-textSize.X / 2f, 12f);
                        textPos.X = Math.Clamp(textPos.X, 10f, windowArea.Width - textSize.X - 10f);
                        textPos.Y = Math.Clamp(textPos.Y, 10f, windowArea.Height - textSize.Y - 10f);

                        fg.AddRectFilled(textPos - new Vector2(4f, 2f), textPos + textSize + new Vector2(4f, 2f), 0xDD101015u, 4f);
                        fg.AddRect(textPos - new Vector2(4f, 2f), textPos + textSize + new Vector2(4f, 2f), indicatorCol, 4f);
                        fg.AddText(textPos, 0xFFFFFFFFu, text);
                    }
                }
            }

            // 2. Draw On-Screen Alert Banner
            if (this.Settings.EnableAlertBanner && this.activeAlertBanners.Count > 0)
            {
                lock (this.activeAlertBanners)
                {
                    var bannerDuration = Math.Max(2f, this.Settings.AlertBannerDurationSec);
                    var bannerW = 460f;
                    var bannerH = 68f;
                    var startY = 110f;

                    for (var i = this.activeAlertBanners.Count - 1; i >= 0; i--)
                    {
                        var banner = this.activeAlertBanners[i];
                        var elapsedSec = (float)(now - banner.CreatedUtc).TotalSeconds;

                        if (elapsedSec >= bannerDuration)
                        {
                            this.activeAlertBanners.RemoveAt(i);
                            continue;
                        }

                        // Compute smooth alpha (fade in first 0.3s, fade out last 1.2s)
                        var alpha = 1.0f;
                        if (elapsedSec < 0.3f)
                        {
                            alpha = elapsedSec / 0.3f;
                        }
                        else if (elapsedSec > bannerDuration - 1.2f)
                        {
                            alpha = Math.Max(0f, (bannerDuration - elapsedSec) / 1.2f);
                        }

                        var bannerX = (windowArea.Width - bannerW) / 2f;
                        var bannerY = startY + (i * (bannerH + 10f));
                        var min = new Vector2(bannerX, bannerY);
                        var max = new Vector2(bannerX + bannerW, bannerY + bannerH);

                        var bgCol = ImGui.ColorConvertFloat4ToU32(new Vector4(0.04f, 0.04f, 0.07f, 0.92f * alpha));
                        var borderCol = ImGui.ColorConvertFloat4ToU32(new Vector4(1.0f, 0.78f, 0.15f, 0.90f * alpha));
                        var headerCol = ImGui.ColorConvertFloat4ToU32(new Vector4(1.0f, 0.85f, 0.30f, 1.0f * alpha));
                        var titleCol = ImGui.ColorConvertFloat4ToU32(new Vector4(1.0f, 1.0f, 1.0f, 1.0f * alpha));
                        var valueCol = ImGui.ColorConvertFloat4ToU32(new Vector4(0.40f, 1.0f, 0.40f, 1.0f * alpha));

                        // Rounded card background + glowing gold border
                        fg.AddRectFilled(min, max, bgCol, 8f);
                        fg.AddRect(min, max, borderCol, 8f, ImDrawFlags.None, 2f);

                        // Header subtitle
                        var subText = this.PluginText.T("banner.valuable_drop_detected", "★ VALUABLE DROP DETECTED ★");
                        var subSize = ImGui.CalcTextSize(subText);
                        fg.AddText(new Vector2(bannerX + (bannerW - subSize.X) / 2f, bannerY + 8f), headerCol, subText);

                        // Item Name + Value
                        var itemLine = banner.ItemName;
                        var valueLine = $" ({banner.DisplayValue:0.##} {banner.DisplayCurrency})";
                        var itemSize = ImGui.CalcTextSize(itemLine);
                        var valSize = ImGui.CalcTextSize(valueLine);
                        var totalTextW = itemSize.X + valSize.X;

                        var itemTextX = bannerX + (bannerW - totalTextW) / 2f;
                        var itemTextY = bannerY + 34f;

                        fg.AddText(new Vector2(itemTextX, itemTextY), titleCol, itemLine);
                        fg.AddText(new Vector2(itemTextX + itemSize.X, itemTextY), valueCol, valueLine);
                    }
                }
            }
        }

        private void ScanItemSlots(IntPtr leftAddress, IntPtr rightAddress)
        {
            var service = this.priceService;
            if (service == null) return;

            var newInv = new List<SlotTag>();
            var newStash = new List<SlotTag>();
            var newRealItemRects = new List<Vector4>();
            double totalScanMs = 0;
            double totalGetMs = 0;
            int totalFound = 0;
            int totalMiss = 0;
            double totalLookupMs = 0;

            if (rightAddress != IntPtr.Zero)
            {
                this.ScanPanelSlots(rightAddress, newInv, newRealItemRects, out var sMs, out var gMs, out var fCount, out var mCount, out var lMs);
                totalScanMs += sMs;
                totalGetMs += gMs;
                totalFound += fCount;
                totalMiss += mCount;
                totalLookupMs += lMs;
            }

            if (leftAddress != IntPtr.Zero)
            {
                this.ScanPanelSlots(leftAddress, newStash, newRealItemRects, out var sMs, out var gMs, out var fCount, out var mCount, out var lMs);
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
            this.cachedRealItemRects = newRealItemRects;
        }

        private void ScanPanelSlots(
            IntPtr panelAddress,
            List<SlotTag> output,
            List<Vector4> realItemRects,
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
                    if (center.Y < panelPos.Y || center.Y > panelMax.Y) continue;
                    if (candidate.Scroll.IsActive && (center.Y < candidate.Scroll.ClipTop || center.Y > candidate.Scroll.ClipBottom)) continue;

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

                // Record real item slot rect for hover suppression (only valid slot dimensions)
                if (slotSize.X >= 15f && slotSize.Y >= 15f && slotSize.X <= 250f && slotSize.Y <= 300f)
                {
                    realItemRects.Add(new Vector4(slotPos.X, slotPos.Y, slotPos.X + slotSize.X, slotPos.Y + slotSize.Y));
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

        private static bool IsStashTabDropdownOpen(IntPtr leftAddress, IntPtr gameUiAddress)
        {
            var handle = Core.Process?.Handle;
            if (handle == null) return false;

            if (leftAddress != IntPtr.Zero && handle.TryReadMemory<UiElementBaseOffset>(leftAddress, out var leftOff) && UiElementBaseFuncs.IsVisibleChecker(leftOff.Flags))
            {
                var children = handle.ReadStdVector<IntPtr>(leftOff.ChildrensPtr);
                if (children != null && children.Length > 0)
                {
                    foreach (var child in children)
                    {
                        if (child == IntPtr.Zero) continue;
                        if (!handle.TryReadMemory<UiElementBaseOffset>(child, out var cOff) || !UiElementBaseFuncs.IsVisibleChecker(cOff.Flags))
                            continue;

                        var subKids = handle.ReadStdVector<IntPtr>(cOff.ChildrensPtr);
                        if (subKids != null && subKids.Length >= 2)
                        {
                            if (PluginUiElementReflection.TryGetAbsoluteRect(child, out var cPos, out var cSize))
                            {
                                if (cSize.X > 120 && cSize.Y > 120)
                                {
                                    int visibleSubCount = 0;
                                    for (int k = 0; k < Math.Min(subKids.Length, 20); k++)
                                    {
                                        if (subKids[k] != IntPtr.Zero &&
                                            handle.TryReadMemory<UiElementBaseOffset>(subKids[k], out var kOff) &&
                                            UiElementBaseFuncs.IsVisibleChecker(kOff.Flags))
                                        {
                                            visibleSubCount++;
                                        }
                                    }
                                    if (visibleSubCount >= 2)
                                    {
                                        return true;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return false;
        }

        private void DrawSlotOverlays()
        {
            var gameUi = Core.States.InGameStateObject?.GameUi;
            if (gameUi == null) return;

            // Hide slot prices when large fullscreen blocking panels are open
            if (gameUi.WorldMapPanel.IsVisible ||
                gameUi.Atlas.IsVisible ||
                gameUi.AtlasSkillsPanel.IsVisible ||
                gameUi.IsPassiveSkillTreeOpen ||
                gameUi.SekhemasTrialMapPanel.IsVisible ||
                (gameUi.RuneshapeCombinationsPanel.Address != IntPtr.Zero && gameUi.RuneshapeCombinationsPanel.IsVisible))
            {
                return;
            }

            var isLeftVisible = gameUi.LeftPanel.Address != IntPtr.Zero && gameUi.LeftPanel.IsVisible;
            var isRightVisible = gameUi.RightPanel.Address != IntPtr.Zero && gameUi.RightPanel.IsVisible;

            if (!isLeftVisible && !isRightVisible)
            {
                return;
            }

            var stashDropdownOpen = isLeftVisible && IsStashTabDropdownOpen(gameUi.LeftPanel.Address, gameUi.Address);

            if (this.Settings.HideSlotPriceOnHover)
            {
                Vector2 mousePos = ImGui.GetMousePos();
                bool isHoveringRealItem = false;

                for (int i = 0; i < this.cachedRealItemRects.Count; i++)
                {
                    var r = this.cachedRealItemRects[i];
                    if (mousePos.X >= r.X && mousePos.X <= r.Z &&
                        mousePos.Y >= r.Y && mousePos.Y <= r.W)
                    {
                        isHoveringRealItem = true;
                        break;
                    }
                }

                if (isHoveringRealItem)
                {
                    return; // Hide ALL slot prices when mouse hovers over ANY real item so loot/tooltips can be viewed cleanly
                }
            }

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

            if (this.Settings.ShowInventoryPrices && isRightVisible) DrawSlots(this.cachedInvSlots);
            if (this.Settings.ShowOtherInventoryPrices && isLeftVisible && !stashDropdownOpen) DrawSlots(this.cachedStashSlots);
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

        private List<MonolithData> GetActiveMonoliths()
        {
            var area = Core.States.InGameStateObject?.CurrentAreaInstance;
            if (area == null) return new List<MonolithData>();

            var currentAreaHash = area.AreaHash ?? string.Empty;
            if (this.lastMonolithAreaHash != currentAreaHash)
            {
                this.lastMonolithAreaHash = currentAreaHash;
                this.trackedMonoliths.Clear();
            }

            var currentMonolithIds = new HashSet<uint>();

            void ProcessMonolithEntity(Entity e)
            {
                if (e == null || !e.IsValid || string.IsNullOrEmpty(e.Path)) return;

                bool isCandidate = e.Path.Contains("Expedition2Encounter", StringComparison.OrdinalIgnoreCase) ||
                                   e.Path.Contains("ExpeditionEncounter", StringComparison.OrdinalIgnoreCase) ||
                                   (e.Path.Contains("Expedition", StringComparison.OrdinalIgnoreCase) && e.Path.Contains("Encounter", StringComparison.OrdinalIgnoreCase));

                if (!isCandidate) return;

                currentMonolithIds.Add(e.Id);

                if (NinjaRuneshapeHelper.TryReadMonolith(e, out var mData))
                {
                    if (mData.IsCompleted)
                    {
                        this.trackedMonoliths.Remove(e.Id);
                        return;
                    }

                    this.trackedMonoliths[e.Id] = mData;
                }
            }

            // Live update only from AwakeEntities
            if (area.AwakeEntities != null)
            {
                foreach (var e in area.AwakeEntities.Values)
                {
                    ProcessMonolithEntity(e);
                }
            }

            // Evict monoliths that no longer exist in AwakeEntities
            if (this.trackedMonoliths.Count > 0)
            {
                var toRemove = new List<uint>();
                foreach (var id in this.trackedMonoliths.Keys)
                {
                    if (!currentMonolithIds.Contains(id))
                    {
                        toRemove.Add(id);
                    }
                }
                foreach (var id in toRemove)
                {
                    this.trackedMonoliths.Remove(id);
                }
            }

            if (this.trackedMonoliths.Count == 0) return new List<MonolithData>();

            // Spatial deduplication: prevent duplicate entities at virtually the same world position (< 35 units)
            var rawList = this.trackedMonoliths.Values.Where(m => !m.IsCompleted).ToList();
            var monoliths = new List<MonolithData>();
            foreach (var m in rawList)
            {
                bool isDuplicate = false;
                for (int i = 0; i < monoliths.Count; i++)
                {
                    var existing = monoliths[i];
                    float dx = existing.WorldPos.X - m.WorldPos.X;
                    float dy = existing.WorldPos.Y - m.WorldPos.Y;
                    if ((dx * dx + dy * dy) < (35f * 35f))
                    {
                        if (m.HoleCount > existing.HoleCount || (m.HoleCount == existing.HoleCount && m.EntityId > existing.EntityId))
                        {
                            monoliths[i] = m;
                        }
                        isDuplicate = true;
                        break;
                    }
                }
                if (!isDuplicate)
                {
                    monoliths.Add(m);
                }
            }

            if (monoliths.Count == 0) return monoliths;

            int areaLevel = area?.CurrentAreaLevel ?? 0;

            for (int mIdx = 0; mIdx < monoliths.Count; mIdx++)
            {
                var m = monoliths[mIdx];
                var offers = new List<MonolithOffer>();

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

                    offers.Add(new MonolithOffer
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
                    });
                }

                if (this.Settings.RsPrioritizeWeight)
                {
                    offers.Sort((a, b) =>
                    {
                        int wCmp = b.ComboWeight.CompareTo(a.ComboWeight);
                        if (wCmp != 0) return wCmp;
                        return b.PriceChaos.CompareTo(a.PriceChaos);
                    });
                }
                else
                {
                    offers.Sort((a, b) =>
                    {
                        int pCmp = b.PriceChaos.CompareTo(a.PriceChaos);
                        if (pCmp != 0) return pCmp;
                        return b.ComboWeight.CompareTo(a.ComboWeight);
                    });
                }

                m.Offers = offers;
                m.BestOffer = offers.Count > 0 ? offers[0] : null;
            }

            // Sort active monoliths globally so both 3D world markers and overlay window match chosen sort mode
            monoliths.Sort((a, b) =>
            {
                int compCmp = a.IsCompleted.CompareTo(b.IsCompleted);
                if (compCmp != 0) return compCmp;

                if (this.Settings.RsPrioritizeWeight)
                {
                    int wA = a.BestOffer?.ComboWeight ?? 0;
                    int wB = b.BestOffer?.ComboWeight ?? 0;
                    int wCmp = wB.CompareTo(wA);
                    if (wCmp != 0) return wCmp;

                    float pA = a.BestOffer?.PriceChaos ?? 0f;
                    float pB = b.BestOffer?.PriceChaos ?? 0f;
                    return pB.CompareTo(pA);
                }
                else
                {
                    float pA = a.BestOffer?.PriceChaos ?? 0f;
                    float pB = b.BestOffer?.PriceChaos ?? 0f;
                    int pCmp = pB.CompareTo(pA);
                    if (pCmp != 0) return pCmp;

                    int wA = a.BestOffer?.ComboWeight ?? 0;
                    int wB = b.BestOffer?.ComboWeight ?? 0;
                    return wB.CompareTo(wA);
                }
            });

            // Assign colors based on sorted rank
            for (int mIdx = 0; mIdx < monoliths.Count; mIdx++)
            {
                monoliths[mIdx].Color = RsMonolithColors[mIdx % RsMonolithColors.Length];
            }

            return monoliths;
        }

        private void DrawMonolithWorldMarkers(List<MonolithData> monoliths)
        {
            var gameUi = Core.States.InGameStateObject?.GameUi;
            if (gameUi != null && (gameUi.IsAnyLargePanelOpen || (gameUi.RuneshapeCombinationsPanel.Address != IntPtr.Zero && gameUi.RuneshapeCombinationsPanel.IsVisible)))
            {
                return; // Hide world markers when in-game rune selection window or any large panel/window is open
            }

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
            var weightTex = this.GetRuneUiTexture("Weight.png", ref this.rsWeightIconTex, ref this.rsWeightIconTried);
            var curTex = this.GetCurrencyTexture(this.Settings.DisplayCurrency);

            var winSize = Core.Process.WindowArea.Size;
            float winW = winSize.Width > 0 ? winSize.Width : 2560f;
            float winH = winSize.Height > 0 ? winSize.Height : 1440f;

            for (int i = 0; i < monoliths.Count; i++)
            {
                var m = monoliths[i];
                if (m.WorldPos == Vector3.Zero) continue;

                if (!canMapProject && playerRender != null)
                {
                    // In 3D world view, only draw monoliths within nearby radius (~170 grid units) to prevent off-screen horizon artifacts
                    var pGrid = playerRender.GridPosition;
                    var mGridX = m.WorldPos.X / 10.86957f;
                    var mGridY = m.WorldPos.Y / 10.86957f;
                    float distGridSq = (pGrid.X - mGridX) * (pGrid.X - mGridX) + (pGrid.Y - mGridY) * (pGrid.Y - mGridY);
                    if (distGridSq > 170f * 170f) continue;
                }

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

                // Culling: Skip markers that fall outside the screen viewport
                if (screenPos.X < -50f || screenPos.X > winW + 50f ||
                    screenPos.Y < -50f || screenPos.Y > winH + 50f)
                {
                    continue;
                }

                bool isMinimal = this.Settings.RsMinimalMapBadges;

                // 1) Sockets row sizing
                float uiScale = Math.Clamp(this.Settings.ExpeditionUiScale, 0.5f, 2.5f);
                float textScale = Math.Clamp(this.Settings.ExpeditionTextScale, 0.5f, 2.5f);
                float fontSize = ImGui.GetFontSize() * textScale;

                float slotSz = 22f * uiScale;
                float slotGap = 2f * uiScale;
                int holes = Math.Clamp(m.HoleCount, 1, 16);
                float socketsW = isMinimal ? 0f : ((holes * slotSz) + Math.Max(0, holes - 1) * slotGap);
                float socketsH = isMinimal ? 0f : slotSz;

                // 2) Bottom info chip sizing
                float chipH = (isMinimal ? 20f : 26f) * uiScale;
                float padX = (isMinimal ? 5f : 6f) * uiScale;
                float spacing = (isMinimal ? 4f : 5f) * uiScale;

                float sqSz = (isMinimal ? 14f : 16f) * uiScale;
                float curX = padX + sqSz;

                CurrencyTex? itemTex = null;
                float iconSz = 22f * uiScale;
                if (!isMinimal && m.BestOffer != null && !string.IsNullOrEmpty(m.BestOffer.ItemIcon))
                {
                    itemTex = this.GetItemTexture(m.BestOffer.ItemIcon);
                    if (itemTex != null && itemTex.Value.Valid)
                    {
                        curX += spacing + iconSz;
                    }
                }

                float curW = (isMinimal ? 15f : 18f) * uiScale;
                if (curTex != null && curTex.Value.Valid)
                {
                    curW = (curTex.Value.H > 0) ? curW * (float)curTex.Value.W / curTex.Value.H : curW;
                    curX += spacing + curW;
                }

                string priceText = m.IsCompleted
                    ? "DONE"
                    : (m.BestOffer != null ? FormatPriceNumberLocal(m.BestOffer.DisplayValue) : string.Empty);
                Vector2 priceSz = !string.IsNullOrEmpty(priceText) ? ImGui.CalcTextSize(priceText) * textScale : Vector2.Zero;
                if (priceSz.X > 0)
                {
                    curX += 3f + priceSz.X;
                }

                string weightText = string.Empty;
                Vector2 weightSz = Vector2.Zero;
                if (!isMinimal && !m.IsCompleted && m.BestOffer != null && m.BestOffer.ComboWeight != 0)
                {
                    weightText = m.BestOffer.ComboWeight > 0 ? $"+{m.BestOffer.ComboWeight}" : $"{m.BestOffer.ComboWeight}";
                    weightSz = ImGui.CalcTextSize(weightText) * textScale;
                    if (weightTex != null && weightTex.Value.Valid)
                    {
                        curX += spacing + (14f * uiScale) + 3f + weightSz.X;
                    }
                    else
                    {
                        curX += spacing + weightSz.X;
                    }
                }

                curX += padX;
                float chipW = curX;

                float totalW = isMinimal ? chipW : Math.Max(socketsW, chipW);
                float totalH = isMinimal ? chipH : (socketsH + 4f + chipH);

                float bX0 = screenPos.X - totalW * 0.5f;
                float bY0 = screenPos.Y - (isMinimal ? chipH * 0.5f : totalH);

                uint imgTint = m.IsCompleted ? 0xC8969696u : 0xFFFFFFFFu;

                // Draw Top Sockets Row (only if not minimal)
                if (!isMinimal)
                {
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
                }

                // Draw Bottom Info Chip
                float chipX0 = screenPos.X - chipW * 0.5f;
                float chipY0 = isMinimal ? (screenPos.Y - chipH * 0.5f) : (bY0 + socketsH + 4f);
                float chipX1 = chipX0 + chipW;
                float chipY1 = chipY0 + chipH;
                float midY = chipY0 + chipH * 0.5f;

                uint bgAlpha = (uint)(Math.Clamp(this.Settings.RsBadgeBgAlpha, 0.0f, 1.0f) * 255f);
                uint chipBg = m.IsCompleted
                    ? ((Math.Min(bgAlpha, 0xC0u)) << 24) | 0x00202020u
                    : (bgAlpha << 24) | 0x00101010u;
                dl.AddRectFilled(new Vector2(chipX0, chipY0), new Vector2(chipX1, chipY1), chipBg, 4f);
                uint borderCol = m.IsCompleted ? 0x88787878u : 0x88404040u;
                dl.AddRect(new Vector2(chipX0, chipY0), new Vector2(chipX1, chipY1), borderCol, 4f, ImDrawFlags.None, 1.0f);

                float renderX = chipX0 + padX;

                // Color square
                dl.AddRectFilled(new Vector2(renderX, midY - sqSz * 0.5f), new Vector2(renderX + sqSz, midY + sqSz * 0.5f), m.IsCompleted ? 0xFF787878u : m.Color, 3f);
                renderX += sqSz + spacing;

                // Reward icon (only if not minimal)
                if (!isMinimal && itemTex != null && itemTex.Value.Valid)
                {
                    dl.AddImage(itemTex.Value.Ptr, new Vector2(renderX, midY - iconSz * 0.5f), new Vector2(renderX + iconSz, midY + iconSz * 0.5f), Vector2.Zero, Vector2.One, imgTint);
                    renderX += iconSz + spacing;
                }

                // Currency icon
                if (curTex != null && curTex.Value.Valid)
                {
                    dl.AddImage(curTex.Value.Ptr, new Vector2(renderX, midY - curW * 0.5f), new Vector2(renderX + curW, midY + curW * 0.5f), Vector2.Zero, Vector2.One, imgTint);
                    renderX += curW + 3f;
                }

                // Price text
                if (priceSz.X > 0)
                {
                    uint priceCol = m.IsCompleted ? 0xFF888888u : 0xFFFFFFFFu;
                    dl.AddText(ImGui.GetFont(), fontSize, new Vector2(renderX, midY - priceSz.Y * 0.5f), priceCol, priceText);
                    renderX += priceSz.X + spacing;
                }

                // Weight text (only if not minimal)
                if (!isMinimal && !string.IsNullOrEmpty(weightText))
                {
                    if (weightTex != null && weightTex.Value.Valid)
                    {
                        dl.AddImage(weightTex.Value.Ptr, new Vector2(renderX, midY - (14f * uiScale) * 0.5f), new Vector2(renderX + 14f * uiScale, midY + (14f * uiScale) * 0.5f), Vector2.Zero, Vector2.One, imgTint);
                        renderX += 14f * uiScale + 3f;
                    }
                    uint wCol = (m.BestOffer != null && m.BestOffer.ComboWeight > 0) ? 0xFF70EB70u : 0xFF8080EBu;
                    dl.AddText(ImGui.GetFont(), fontSize, new Vector2(renderX, midY - weightSz.Y * 0.5f), wCol, weightText);
                }
            }
        }

        private bool DrawSortButton(string id, string text, CurrencyTex? icon, bool isSelected, Vector4 activeBg, Vector4 hoverBg)
        {
            float lineH = ImGui.GetTextLineHeight();
            float iconSz = lineH;
            Vector2 textSz = ImGui.CalcTextSize(text);
            float padX = 8f;
            bool hasIcon = icon != null && icon.Value.Valid;
            float spacing = hasIcon ? 6f : 0f;
            float btnW = padX * 2 + (hasIcon ? iconSz : 0f) + spacing + textSz.X;
            float btnH = lineH + 6f;

            ImGui.PushStyleColor(ImGuiCol.Button, isSelected ? activeBg : new Vector4(0.22f, 0.22f, 0.22f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, isSelected ? hoverBg : new Vector4(0.35f, 0.35f, 0.35f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, isSelected ? hoverBg : new Vector4(0.45f, 0.45f, 0.45f, 1f));

            Vector2 p0 = ImGui.GetCursorScreenPos();
            bool clicked = ImGui.Button($"###{id}", new Vector2(btnW, btnH));
            ImGui.PopStyleColor(3);

            var dl = ImGui.GetWindowDrawList();
            float curX = p0.X + padX;
            float midY = p0.Y + btnH * 0.5f;

            if (hasIcon)
            {
                dl.AddImage(icon!.Value.Ptr, new Vector2(curX, midY - iconSz * 0.5f), new Vector2(curX + iconSz, midY + iconSz * 0.5f));
                curX += iconSz + spacing;
            }

            uint txtCol = isSelected ? 0xFFFFFFFFu : 0xFFCCCCCCu;
            dl.AddText(new Vector2(curX, midY - textSz.Y * 0.5f), txtCol, text);

            return clicked;
        }
        private void DrawRuneshapeWindow(List<MonolithData> monoliths)
        {
            if (monoliths == null || monoliths.Count == 0) return;

            var gameUi = Core.States.InGameStateObject?.GameUi;
            if (gameUi != null && (gameUi.IsAnyLargePanelOpen || (gameUi.RuneshapeCombinationsPanel.Address != IntPtr.Zero && gameUi.RuneshapeCombinationsPanel.IsVisible)))
            {
                return;
            }

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

            float fontScale = Math.Clamp(this.Settings.ExpeditionTextScale, 0.5f, 2.5f);
            ImGui.SetWindowFontScale(fontScale);

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

            var wTex = this.GetRuneUiTexture("Weight.png", ref this.rsWeightIconTex, ref this.rsWeightIconTried);
            var pTex = this.GetRuneUiTexture("Price.png", ref this.rsPriceIconTex, ref this.rsPriceIconTried);

            // Sort mode toggle bar (Unified Icon + Text single button)
            {
                bool byWeight = this.Settings.RsPrioritizeWeight;
                string wText = this.PluginText.T("ninjapricer.runeshape.sort_weight", "Weight");
                string pText = this.PluginText.T("ninjapricer.runeshape.sort_price", "Price");

                if (this.DrawSortButton("sort_w_btn", wText, wTex, byWeight, new Vector4(0.22f, 0.55f, 0.22f, 1f), new Vector4(0.28f, 0.68f, 0.28f, 1f)))
                {
                    this.Settings.RsPrioritizeWeight = true;
                    this.SaveSettings();
                }

                ImGui.SameLine(0f, 4f);

                if (this.DrawSortButton("sort_p_btn", pText, pTex, !byWeight, new Vector4(0.22f, 0.45f, 0.62f, 1f), new Vector4(0.28f, 0.58f, 0.78f, 1f)))
                {
                    this.Settings.RsPrioritizeWeight = false;
                    this.SaveSettings();
                }

                ImGui.Separator();
            }

            if (monoliths.Count == 0)
            {
                ImGui.TextDisabled(this.PluginText.T("ninjapricer.runeshape.no_runeshapes", "No Runeshapes"));
            }
            else
            {
                int areaLevel = area?.CurrentAreaLevel ?? 0;
                float lineH = ImGui.GetTextLineHeight();
                float uiScale = Math.Clamp(this.Settings.ExpeditionUiScale, 0.5f, 2.5f);
                float rowScale = this.Settings.RsCompactRows ? Math.Clamp(this.Settings.RsRowScale, 0.5f, 1.0f) : 1.0f;
                float effectiveScale = uiScale * rowScale;

                float kSquareSz = Math.Max(lineH * 0.75f, 12f) * effectiveScale;
                float slotSz = Math.Max(lineH * 0.65f, 10f) * effectiveScale;
                float slotGap = 2f * effectiveScale;
                var curTex = this.GetCurrencyTexture(this.Settings.DisplayCurrency);

                var bgReg = this.GetRuneUiTexture("RuneBgRegular.png", ref this.runeBgRegularTex, ref this.runeBgRegularTried);
                var bgPur = this.GetRuneUiTexture("RuneBgPurple.png", ref this.runeBgPurpleTex, ref this.runeBgPurpleTried);
                var glow = this.GetRuneUiTexture("RunePropagation.png", ref this.runePropagationTex, ref this.runePropagationTried);

                // Sort monoliths
                var displayMonoliths = new List<MonolithData>(monoliths);
                displayMonoliths.Sort((a, b) =>
                {
                    int compCmp = a.IsCompleted.CompareTo(b.IsCompleted);
                    if (compCmp != 0) return compCmp;

                    if (this.Settings.RsPrioritizeWeight)
                    {
                        int wA = a.BestOffer?.ComboWeight ?? 0;
                        int wB = b.BestOffer?.ComboWeight ?? 0;
                        int wCmp = wB.CompareTo(wA);
                        if (wCmp != 0) return wCmp;

                        float pA = a.BestOffer?.PriceChaos ?? 0f;
                        float pB = b.BestOffer?.PriceChaos ?? 0f;
                        return pB.CompareTo(pA);
                    }
                    else
                    {
                        float pA = a.BestOffer?.PriceChaos ?? 0f;
                        float pB = b.BestOffer?.PriceChaos ?? 0f;
                        int pCmp = pB.CompareTo(pA);
                        if (pCmp != 0) return pCmp;

                        int wA = a.BestOffer?.ComboWeight ?? 0;
                        int wB = b.BestOffer?.ComboWeight ?? 0;
                        return wB.CompareTo(wA);
                    }
                });

                // Pre-calculate natural content width across all visible rows for dynamic auto-sizing
                float maxContentW = 80f * effectiveScale;

                // 1. Sort bar natural width
                {
                    float padX = 8f;
                    float lineHBtn = ImGui.GetTextLineHeight();
                    Vector2 wSz = ImGui.CalcTextSize(this.PluginText.T("ninjapricer.runeshape.sort_weight", "Weight"));
                    Vector2 pSz = ImGui.CalcTextSize(this.PluginText.T("ninjapricer.runeshape.sort_price", "Price"));
                    float sortBarW = (padX * 2 + lineHBtn + 6f + wSz.X) + 4f + (padX * 2 + lineHBtn + 6f + pSz.X);
                    if (sortBarW > maxContentW) maxContentW = sortBarW;
                }

                // 2. Measure headers and expanded rows
                for (int mIdx = 0; mIdx < displayMonoliths.Count; mIdx++)
                {
                    var m = displayMonoliths[mIdx];
                    float dotsW = (this.Settings.RsShowHdrRunes && m.HoleCount > 0)
                        ? (m.HoleCount * slotSz + (m.HoleCount - 1) * slotGap) : 0f;

                    bool bestPriced = this.Settings.RsShowHdrBest && m.BestOffer != null && m.BestOffer.PriceChaos > 0f;
                    string bestNum = bestPriced ? FormatPriceNumberLocal(m.BestOffer!.DisplayValue) : string.Empty;
                    float priceW = 0f;
                    if (bestPriced)
                    {
                        float iw = (curTex != null && curTex.Value.H > 0) ? lineH * (float)curTex.Value.W / curTex.Value.H : lineH;
                        priceW = iw + 4f + ImGui.CalcTextSize(bestNum).X;
                    }

                    int bestWeight = m.BestOffer?.ComboWeight ?? 0;
                    string hdrWBuf = (this.Settings.ShowRuneshapeWeights && bestWeight != 0)
                        ? (bestWeight > 0 ? $"+{bestWeight}" : $"{bestWeight}") : string.Empty;
                    float hdrWW = !string.IsNullOrEmpty(hdrWBuf) ? ImGui.CalcTextSize(hdrWBuf).X + ((wTex != null && wTex.Value.Valid) ? lineH + 3f : 0f) : 0f;

                    float hdrW = (this.Settings.RsShowHdrColor ? kSquareSz + 4f * effectiveScale : 0f)
                        + 4f * effectiveScale
                        + dotsW
                        + (bestPriced ? 8f + priceW : 0f)
                        + (!string.IsNullOrEmpty(hdrWBuf) ? 8f + hdrWW : 0f)
                        + 8f * effectiveScale;
                    if (hdrW > maxContentW) maxContentW = hdrW;

                    long mAddr = m.EntityAddress != IntPtr.Zero ? m.EntityAddress.ToInt64() : (long)m.WorldPos.GetHashCode();
                    if (this.expandedMonoliths.Contains(mAddr) && m.Offers != null)
                    {
                        foreach (var offer in m.Offers)
                        {
                            float rowW = (this.Settings.RsShowHdrColor ? kSquareSz + 4f * effectiveScale : 0f) + 4f;
                            if (this.Settings.RsShowRowIcon && offer.PriceChaos > 0f && !string.IsNullOrEmpty(offer.ItemIcon))
                            {
                                rowW += lineH * uiScale + 4f;
                            }
                            string namePart = this.Settings.RsShowRowName ? offer.Reward : string.Empty;
                            string qtyPart = this.Settings.RsShowRowQty ? (offer.RewardCount > 1 || !this.Settings.RsShowRowName ? $"x{offer.RewardCount}" : string.Empty) : string.Empty;
                            string txt = !string.IsNullOrEmpty(namePart) && !string.IsNullOrEmpty(qtyPart) ? $"{namePart}  {qtyPart}" : $"{namePart}{qtyPart}";
                            if (!string.IsNullOrEmpty(txt))
                            {
                                rowW += ImGui.CalcTextSize(txt).X + 4f;
                            }
                            if (this.Settings.RsShowRowPrice && offer.PriceChaos > 0f)
                            {
                                string pNum = FormatPriceNumberLocal(offer.DisplayValue);
                                float iw = (curTex != null && curTex.Value.H > 0) ? lineH * (float)curTex.Value.W / curTex.Value.H : lineH;
                                rowW += 10f + ImGui.CalcTextSize(pNum).X + 3f + iw;
                            }
                            if (this.Settings.ShowRuneshapeWeights && offer.ComboWeight != 0)
                            {
                                string wText = offer.ComboWeight > 0 ? $"+{offer.ComboWeight}" : $"{offer.ComboWeight}";
                                rowW += 10f + ((wTex != null && wTex.Value.Valid) ? lineH + 3f : 0f) + ImGui.CalcTextSize(wText).X;
                            }
                            if (this.Settings.RsShowRowPropRunes && m.GoldenSlots.Count > 0)
                            {
                                rowW += m.GoldenSlots.Count * (lineH * 1.05f * uiScale + 5f);
                            }
                            if (rowW > maxContentW) maxContentW = rowW;
                        }
                    }
                }

                if (this.Settings.RsCompactRows)
                {
                    ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(4f * uiScale, 2f * rowScale));
                }

                for (int mIdx = 0; mIdx < displayMonoliths.Count; mIdx++)
                {
                    ImGui.PushID(mIdx);
                    var m = displayMonoliths[mIdx];
                    uint mColor = m.Color != 0 ? m.Color : RsMonolithColors[mIdx % RsMonolithColors.Length];

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

                    if (this.Settings.RsPrioritizeWeight)
                    {
                        offers.Sort((a, b) =>
                        {
                            int wCmp = b.ComboWeight.CompareTo(a.ComboWeight);
                            if (wCmp != 0) return wCmp;
                            return b.TotalChaos.CompareTo(a.TotalChaos);
                        });
                    }
                    else
                    {
                        offers.Sort((a, b) =>
                        {
                            int pCmp = b.TotalChaos.CompareTo(a.TotalChaos);
                            if (pCmp != 0) return pCmp;
                            return b.ComboWeight.CompareTo(a.ComboWeight);
                        });
                    }
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
                    float hdrWW = !string.IsNullOrEmpty(hdrWBuf) ? ImGui.CalcTextSize(hdrWBuf).X + ((wTex != null && wTex.Value.Valid) ? lineH + 3f : 0f) : 0f;

                    float reserve = dotsW + (priceW > 0f ? priceW + 10f : 0f) + (hdrWW > 0f ? hdrWW + 10f : 0f) + 8f;
                    float spaceW = ImGui.CalcTextSize(" ").X;
                    int padCnt = spaceW > 0f ? (int)MathF.Ceiling(reserve / spaceW) : 0;

                    // Colored square / badge for monolith (clean color without number text)
                    var dl = ImGui.GetWindowDrawList();
                    float baseFrameH = ImGui.GetFrameHeight() * rowScale;
                    if (this.Settings.RsShowHdrColor)
                    {
                        float sqYOff = (baseFrameH > kSquareSz) ? (baseFrameH - kSquareSz) * 0.5f : 0f;
                        var cp = ImGui.GetCursorScreenPos();
                        dl.AddRectFilled(new Vector2(cp.X, cp.Y + sqYOff), new Vector2(cp.X + kSquareSz, cp.Y + sqYOff + kSquareSz), m.IsCompleted ? 0xFF787878u : mColor, 3f);

                        ImGui.Dummy(new Vector2(kSquareSz, baseFrameH));
                        ImGui.SameLine(0f, 4f * effectiveScale);
                    }

                    // Custom header row without triangle arrow (collapsed by default until clicked)
                    long mAddr = m.EntityAddress != IntPtr.Zero ? m.EntityAddress.ToInt64() : (long)m.WorldPos.GetHashCode();
                    bool headerOpen = this.expandedMonoliths.Contains(mAddr);
                    float headerH = Math.Max(baseFrameH, slotSz + 2f);

                    // Dynamically size button width to fit maxContentW
                    float btnW = Math.Max(80f * effectiveScale, maxContentW - (this.Settings.RsShowHdrColor ? kSquareSz + 4f * effectiveScale : 0f));

                    var hmin = ImGui.GetCursorScreenPos();
                    var hmax = new Vector2(hmin.X + btnW, hmin.Y + headerH);

                    bool clicked = ImGui.InvisibleButton($"###rscol_{mIdx}_{mAddr:X}", new Vector2(btnW, headerH));
                    bool isHovered = ImGui.IsItemHovered();
                    if (clicked)
                    {
                        if (headerOpen) this.expandedMonoliths.Remove(mAddr);
                        else this.expandedMonoliths.Add(mAddr);
                        headerOpen = !headerOpen;
                    }

                    // Background with rounded corners
                    uint bgCol = isHovered
                        ? ImGui.GetColorU32(ImGuiCol.HeaderHovered)
                        : (headerOpen ? ImGui.GetColorU32(ImGuiCol.Header) : ImGui.GetColorU32(ImGuiCol.FrameBg));
                    dl.AddRectFilled(hmin, hmax, bgCol, 3f);

                    // Header decorations drawn with dl
                    float midY = (hmin.Y + hmax.Y) * 0.5f;
                    uint imgTint = m.IsCompleted ? 0xC8969696u : 0xFFFFFFFFu;
                    uint txtCol = m.IsCompleted ? 0xDC969696u : 0xFFFFFFFFu;

                    // Start runes forward (directly after left margin, no triangle arrow)
                    float xl = hmin.X + 4.0f * effectiveScale;

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
                        if (wTex != null && wTex.Value.Valid)
                        {
                            float iw = lineH;
                            dl.AddImage(wTex.Value.Ptr, new Vector2(xl, midY - lineH * 0.5f), new Vector2(xl + iw, midY + lineH * 0.5f), Vector2.Zero, Vector2.One, imgTint);
                            xl += iw + 3f;
                        }
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
                                    float itemIconSz = lh * uiScale;
                                    ImGui.Image(itex.Value.Ptr, new Vector2(itemIconSz, itemIconSz));
                                    lineStarted = true;
                                }
                            }

                            // 2) Name + quantity with full recipe runes tooltip
                            string namePart = this.Settings.RsShowRowName ? rw.Name : string.Empty;
                            string qtyPart = this.Settings.RsShowRowQty ? (rw.Count > 1 || !this.Settings.RsShowRowName ? $"x{rw.Count}" : string.Empty) : string.Empty;
                            string txt = !string.IsNullOrEmpty(namePart) && !string.IsNullOrEmpty(qtyPart) ? $"{namePart}  {qtyPart}" : $"{namePart}{qtyPart}";
                            if (!string.IsNullOrEmpty(txt))
                            {
                                if (lineStarted) ImGui.SameLine(0f, 4f);
                                if (rw.IsPriced) ImGui.TextUnformatted(txt);
                                else ImGui.TextDisabled(txt);

                                if (ImGui.IsItemHovered() && rw.Recipe.Runes != null && rw.Recipe.Runes.Count > 0)
                                {
                                    string rList = string.Join(" + ", rw.Recipe.Runes);
                                    ImGui.SetTooltip(string.Format(this.PluginText.T("ninjapricer.runeshape.recipe_runes_tooltip", "Recipe: {0}\nRunes: {1}"), rw.Name, rList));
                                }
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
                                if (wTex != null && wTex.Value.Valid)
                                {
                                    float lh = ImGui.GetTextLineHeight();
                                    ImGui.Image(wTex.Value.Ptr, new Vector2(lh, lh));
                                    ImGui.SameLine(0f, 3f);
                                }
                                var wc = rw.ComboWeight > 0 ? new Vector4(0.43f, 0.92f, 0.43f, 1f) : new Vector4(0.72f, 0.72f, 0.72f, 1f);
                                string wText = rw.ComboWeight > 0 ? $"+{rw.ComboWeight}" : $"{rw.ComboWeight}";
                                ImGui.TextColored(wc, wText);
                                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Total rune weight of this combination");
                                lineStarted = true;
                            }

                            // 5) Propagating rune icon(s) if this recipe uses golden socket(s) (supports 1, 2 or more golden slots)
                            if (this.Settings.RsShowRowPropRunes && m.GoldenSlots.Count > 0)
                            {
                                foreach (int gs in m.GoldenSlots)
                                {
                                    if (rw.Recipe.RuneIdx != null && gs >= 0 && gs < rw.Recipe.RuneIdx.Count)
                                    {
                                        int rIdx = rw.Recipe.RuneIdx[gs];
                                        string rName = (rIdx >= 0 && rIdx < NinjaRuneshapeHelper.RuneNames.Length)
                                            ? NinjaRuneshapeHelper.RuneNames[rIdx]
                                            : "Rune";

                                        if (lineStarted) ImGui.SameLine(0f, 5f);

                                        float lh = ImGui.GetTextLineHeight();
                                        float iconSz = lh * 1.05f * uiScale;
                                        var cp = ImGui.GetCursorScreenPos();

                                        // Dark background circle
                                        dl.AddCircleFilled(new Vector2(cp.X + iconSz * 0.5f, cp.Y + iconSz * 0.5f), iconSz * 0.5f, 0xD0141414u);

                                        // Regular or Purple Rune background frame
                                        bool slotRare = rIdx >= 23 && rIdx <= 32;
                                        var bg = (slotRare && bgPur != null && bgPur.Value.Valid) ? bgPur : bgReg;
                                        if (bg != null && bg.Value.Valid)
                                        {
                                            dl.AddImage(bg.Value.Ptr, cp, cp + new Vector2(iconSz, iconSz));
                                        }

                                        // Actual Rune icon
                                        var runeTex = (rIdx >= 0 && rIdx < 34) ? this.GetRuneTexture(rIdx) : null;
                                        if (runeTex != null && runeTex.Value.Valid)
                                        {
                                            float inset = iconSz * 0.12f;
                                            dl.AddImage(runeTex.Value.Ptr, new Vector2(cp.X + inset, cp.Y + inset), new Vector2(cp.X + iconSz - inset, cp.Y + iconSz - inset));
                                        }

                                        // Golden propagation glow / aura
                                        if (glow != null && glow.Value.Valid)
                                        {
                                            float cx2 = cp.X + iconSz * 0.5f;
                                            float gw = iconSz * 1.15f;
                                            float gt = cp.Y - iconSz * 0.40f;
                                            dl.AddImage(glow.Value.Ptr, new Vector2(cx2 - gw * 0.5f, gt), new Vector2(cx2 + gw * 0.5f, gt + iconSz * 1.6f));
                                        }
                                        else
                                        {
                                            dl.AddCircle(new Vector2(cp.X + iconSz * 0.5f, cp.Y + iconSz * 0.5f), iconSz * 0.52f, 0xFFFFD23Cu, 0, 1.5f);
                                        }

                                        ImGui.Dummy(new Vector2(iconSz, lh));
                                        if (ImGui.IsItemHovered())
                                        {
                                            string tt = string.Format(this.PluginText.T("ninjapricer.runeshape.prop_rune_tooltip", "Golden Socket (Slot {0}): {1} rune carries over"), gs + 1, rName);
                                            ImGui.SetTooltip(tt);
                                        }
                                        lineStarted = true;
                                    }
                                }
                            }
                        }
                        ImGui.Unindent(kSquareSz + 4f);
                        if (m.IsCompleted) ImGui.PopStyleVar();
                    }

                    if (!this.Settings.RsCompactRows)
                    {
                        ImGui.Spacing();
                    }
                    ImGui.PopID();
                }

                if (this.Settings.RsCompactRows)
                {
                    ImGui.PopStyleVar();
                }
            }

            ImGui.End();
        }
    }
}
