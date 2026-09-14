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
    using TEHhub.RemoteObjects.Components;

    public sealed partial class NinjaPricerCore : PCore<NinjaPricerSettings>
    {
        private const int UiElementItemAddressOffset = 0x3A0;
        private const float IconHeightMul = 1.4f;

        private class RsRecipe
        {
            public int Row { get; set; }
            public string Id { get; set; } = string.Empty;
            public int Size { get; set; }
            public List<string> Runes { get; set; } = new();
            public string? Reward { get; set; }
            public int RewardCount { get; set; }
            public string Description { get; set; } = string.Empty;
            public int Category { get; set; }
            public int MinLevel { get; set; }
            public int MaxLevel { get; set; }
        }

        private readonly List<RsRecipe> runeshapeRecipes = new();
        private string rsSearchFilter = string.Empty;

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
        private readonly List<SlotTag> cachedInvSlots = new();
        private readonly List<SlotTag> cachedStashSlots = new();

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

            this.LoadRuneshapeRecipes();

            this.onAreaChangeCoroutine = CoroutineHandler.Start(this.OnAreaChange());
        }

        private void LoadRuneshapeRecipes()
        {
            this.runeshapeRecipes.Clear();
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
                                if (item.TryGetProperty("reward", out var rwProp) && rwProp.ValueKind == JsonValueKind.String) r.Reward = rwProp.GetString();
                                if (item.TryGetProperty("rewardCount", out var rcProp)) r.RewardCount = rcProp.GetInt32();
                                if (item.TryGetProperty("category", out var cProp)) r.Category = cProp.GetInt32();
                                if (item.TryGetProperty("minLevel", out var mlProp)) r.MinLevel = mlProp.GetInt32();
                                if (item.TryGetProperty("maxLevel", out var xlProp)) r.MaxLevel = xlProp.GetInt32();
                                if (item.TryGetProperty("runes", out var runesElem) && runesElem.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var rune in runesElem.EnumerateArray())
                                    {
                                        var rName = rune.GetString();
                                        if (!string.IsNullOrEmpty(rName)) r.Runes.Add(rName);
                                    }
                                }
                                this.runeshapeRecipes.Add(r);
                            }
                        }
                        if (this.runeshapeRecipes.Count > 0)
                        {
                            PluginLog.Info("NinjaPricer", $"[NinjaPricer] Loaded {this.runeshapeRecipes.Count} Runeshape recipes from '{p}'");
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
            ImGui.Text($"  LookupPrice this frame: {this.perfLookupExact} found, {this.perfLookupMiss} miss");
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

            // Runeshape Window (always available in hideout, towns, and maps)
            if (this.Settings.ShowRuneshapeWindow)
            {
                this.DrawRuneshapeWindow();
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

            // Reset per-frame counters
            this.perfLookupMs = 0.0;
            this.perfLookupExact = 0;
            this.perfLookupMiss = 0;

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

            // Inventory / Stash overlay
            if (this.Settings.ShowInventoryPrices || this.Settings.ShowOtherInventoryPrices || this.Settings.ShowRitualPrices)
            {
                var swI = Stopwatch.StartNew();
                if ((DateTime.UtcNow - this.lastInvScanUtc).TotalMilliseconds >= this.Settings.ScanIntervalMs)
                {
                    this.lastInvScanUtc = DateTime.UtcNow;
                    this.ScanItemSlots();
                }
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

                // Read item name
                string itemName = string.Empty;
                int stackMultiplier = 1;
                var reader = Core.Process.Handle;

                if (PluginUiElementReflection.TryValidateItemAddress(worldItem.ItemEntityAddress, out var path, out _))
                {
                    // Item details
                    if (reader.TryReadMemory<ItemStruct>(worldItem.ItemEntityAddress, out var itemStruct) &&
                        reader.TryReadMemory<EntityDetails>(itemStruct.EntityDetailsPtr, out var details))
                    {
                        itemName = reader.ReadStdWString(details.name);
                    }
                }

                if (string.IsNullOrEmpty(itemName))
                {
                    // Fallback to path basename
                    int slash = path.LastIndexOf('/');
                    if (slash >= 0) itemName = path.Substring(slash + 1);
                }

                if (string.IsNullOrEmpty(itemName)) continue;

                var clean = NinjaPriceService.CleanItemName(itemName);
                var swL = Stopwatch.StartNew();
                bool found = this.priceService.TryLookupPrice(clean, out var price);
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

        private void ScanItemSlots()
        {
            var gameUi = Core.States.InGameStateObject?.GameUi;
            if (gameUi == null || this.priceService == null) return;

            var newInv = new List<SlotTag>();
            var newStash = new List<SlotTag>();

            if (this.Settings.ShowInventoryPrices && gameUi.RightPanel.IsVisible)
            {
                this.ScanPanelSlots(gameUi.RightPanel.Address, newInv);
            }

            if (this.Settings.ShowOtherInventoryPrices && gameUi.LeftPanel.IsVisible)
            {
                this.ScanPanelSlots(gameUi.LeftPanel.Address, newStash);
            }

            this.cachedInvSlots.Clear();
            this.cachedInvSlots.AddRange(newInv);

            this.cachedStashSlots.Clear();
            this.cachedStashSlots.AddRange(newStash);
        }

        private void ScanPanelSlots(IntPtr panelAddress, List<SlotTag> output)
        {
            var handle = Core.Process?.Handle;
            var service = this.priceService;
            if (panelAddress == IntPtr.Zero || handle == null || service == null) return;

            var queue = new Queue<IntPtr>();
            var visited = new HashSet<IntPtr>();
            queue.Enqueue(panelAddress);

            while (queue.Count > 0 && visited.Count < 2000)
            {
                var elem = queue.Dequeue();
                if (elem == IntPtr.Zero || !visited.Add(elem)) continue;

                if (!handle.TryReadMemory<UiElementBaseOffset>(elem, out var offset)) continue;
                if (!UiElementBaseFuncs.IsVisibleChecker(offset.Flags)) continue;

                var children = handle.ReadStdVector<IntPtr>(offset.ChildrensPtr);
                foreach (var c in children)
                {
                    if (c != IntPtr.Zero && !visited.Contains(c)) queue.Enqueue(c);
                }

                var itemAddr = handle.ReadMemory<IntPtr>(elem + UiElementItemAddressOffset);
                if (itemAddr == IntPtr.Zero) continue;

                if (!PluginUiElementReflection.TryValidateItemAddress(itemAddr, out var path, out _)) continue;
                if (!PluginUiElementReflection.TryGetAbsoluteRect(elem, out var pos, out var size)) continue;
                if (size.X <= 5 || size.Y <= 5) continue;

                string itemName = string.Empty;
                if (handle.TryReadMemory<ItemStruct>(itemAddr, out var itemStruct) &&
                    handle.TryReadMemory<EntityDetails>(itemStruct.EntityDetailsPtr, out var details))
                {
                    itemName = handle.ReadStdWString(details.name);
                }

                if (string.IsNullOrEmpty(itemName))
                {
                    int slash = path.LastIndexOf('/');
                    if (slash >= 0) itemName = path.Substring(slash + 1);
                }

                if (string.IsNullOrEmpty(itemName)) continue;

                var clean = NinjaPriceService.CleanItemName(itemName);
                if (service.TryLookupPrice(clean, out var price))
                {
                    float chaos = price.Chaos;
                    if (chaos < this.Settings.MinPriceChaos) continue;

                    float displayVal = this.Settings.DisplayCurrency switch
                    {
                        DisplayCurrency.Divine => price.Divine,
                        DisplayCurrency.Exalted => price.Exalt,
                        _ => chaos,
                    };

                    output.Add(new SlotTag
                    {
                        Pos = pos,
                        Size = size,
                        DisplayValue = displayVal,
                        Chaos = chaos,
                        IconPath = price.ItemIcon
                    });
                }
            }
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

        private void DrawRuneshapeWindow()
        {
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

            ImGui.SetNextWindowPos(new Vector2(this.Settings.RuneshapeWinX, this.Settings.RuneshapeWinY), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSize(new Vector2(500, 550), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowBgAlpha(winAlpha);

            bool keepOpen = this.Settings.ShowRuneshapeWindow;
            var flags = ImGuiWindowFlags.NoFocusOnAppearing;
            if (ImGui.Begin("Runeshapes###NinjaRuneshapeWindow", ref keepOpen, flags))
            {
                if (!keepOpen)
                {
                    this.Settings.ShowRuneshapeWindow = false;
                    this.SaveSettings();
                    ImGui.End();
                    return;
                }

                var curPos = ImGui.GetWindowPos();
                if (curPos.X != this.Settings.RuneshapeWinX || curPos.Y != this.Settings.RuneshapeWinY)
                {
                    this.Settings.RuneshapeWinX = curPos.X;
                    this.Settings.RuneshapeWinY = curPos.Y;
                }

                this.runeshapeWinRectMin = curPos;
                this.runeshapeWinRectMax = curPos + ImGui.GetWindowSize();
                this.runeshapeWinRectValid = true;

                ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.2f, 1.0f), "Runeshape Combinations");
                ImGui.SameLine();
                var st = this.priceService?.GetStatus();
                if (st?.Loaded == true)
                {
                    ImGui.TextDisabled($"(Rates: 1D = {st.Value.DivineInChaos:F1}c | 1E = {st.Value.ExaltedInChaos:F2}c)");
                }
                else
                {
                    ImGui.TextColored(new Vector4(1.0f, 0.5f, 0.0f, 1.0f), $"({st?.Message ?? "Loading..."})");
                }

                ImGui.Separator();

                // Search Filter
                ImGui.SetNextItemWidth(320f);
                ImGui.InputTextWithHint("##rsSearch", "Search reward or rune...", ref this.rsSearchFilter, 64);
                if (!string.IsNullOrEmpty(this.rsSearchFilter))
                {
                    ImGui.SameLine();
                    if (ImGui.Button("Clear"))
                    {
                        this.rsSearchFilter = string.Empty;
                    }
                }

                ImGui.Spacing();

                if (this.runeshapeRecipes.Count == 0)
                {
                    this.LoadRuneshapeRecipes();
                    if (this.runeshapeRecipes.Count == 0)
                    {
                        ImGui.TextDisabled("No recipes loaded. Make sure expedition2_recipes.json exists.");
                    }
                }

                string[] colors = { "Red (Physical)", "Blue (Arcane/Cold)", "Green (Chaos/Poison)", "Yellow (Fire/Currency)", "Purple (Celestial/Rare)" };
                uint[] colorValues = { 0xFF3333E5, 0xFFE56633, 0xFF33E533, 0xFF33D5E5, 0xFFD533D5 };

                var filter = this.rsSearchFilter.Trim();

                for (int i = 0; i < colors.Length; i++)
                {
                    uint colorKey = (uint)i;
                    bool collapsed = this.Settings.RuneshapeCollapsed.Contains(colorKey);

                    if (this.Settings.RsShowHdrColor)
                    {
                        var dl = ImGui.GetWindowDrawList();
                        var cp = ImGui.GetCursorScreenPos();
                        dl.AddRectFilled(cp, cp + new Vector2(12f, 12f), colorValues[i], 2f);
                        ImGui.Dummy(new Vector2(14f, 12f));
                        ImGui.SameLine();
                    }

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

                    bool treeOpen = ImGui.TreeNode(headerTitle);
                    if (treeOpen)
                    {
                        if (collapsed) this.Settings.RuneshapeCollapsed.Remove(colorKey);

                        if (list.Count == 0)
                        {
                            ImGui.TextDisabled("  No matching recipes");
                        }
                        else
                        {
                            foreach (var rec in list)
                            {
                                string rewardName = !string.IsNullOrEmpty(rec.Reward) ? rec.Reward : rec.Description;
                                string priceStr = string.Empty;

                                if (this.priceService != null && this.priceService.TryLookupPrice(rewardName, out var pr))
                                {
                                    float priceChaos = pr.Chaos * Math.Max(1, rec.RewardCount);
                                    priceStr = this.Settings.DisplayCurrency switch
                                    {
                                        DisplayCurrency.Divine => $"{pr.Divine * Math.Max(1, rec.RewardCount):F2} D",
                                        DisplayCurrency.Exalted => $"{pr.Exalt * Math.Max(1, rec.RewardCount):F1} E",
                                        _ => $"{priceChaos:F0} c"
                                    };
                                }

                                string runesList = string.Join(" + ", rec.Runes);

                                ImGui.Bullet();
                                ImGui.TextColored(new Vector4(0.9f, 0.9f, 0.9f, 1.0f), $"{rec.Description}");
                                if (!string.IsNullOrEmpty(priceStr))
                                {
                                    ImGui.SameLine();
                                    ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.2f, 1.0f), $"[{priceStr}]");
                                }

                                ImGui.Indent(18f);
                                ImGui.TextDisabled($"Slots: {rec.Size} | Runes: {runesList}");
                                ImGui.Unindent(18f);
                            }
                        }

                        ImGui.TreePop();
                    }
                    else
                    {
                        if (!collapsed) this.Settings.RuneshapeCollapsed.Add(colorKey);
                    }
                }
            }
            ImGui.End();
        }
    }
}
