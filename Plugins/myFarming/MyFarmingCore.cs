namespace myFarming
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Numerics;
    using System.Runtime.InteropServices;
    using System.Text.Encodings.Web;
    using System.Text.Json;
    using ImGuiNET;
    using TEHhub;
    using TEHhub.Plugin;
    using TEHhub.RemoteEnums;

    public sealed partial class MyFarmingCore : PCore<MyFarmingSettings>
    {
        private PriceHelper priceHelper = null!;
        private LootDiffEngine diffEngine = null!;
        private KillTracker killTracker = null!;
        private GoldTracker goldTracker = null!;
        private HistoryStore historyStore = null!;
        private SessionStore sessionStore = null!;

        // Runtime run state
        private bool isRunActive = false;
        private bool isRunPaused = false;
        private MapRun? currentRun = null;
        private DateTime runStartTimeUtc = DateTime.MinValue;
        private DateTime lastTickUtc = DateTime.MinValue;
        private double activeRunDurationSec = 0.0;
        private string lastAreaHash = string.Empty;
        private bool lastAreaWasTownOrHideout = true;
        private List<LootEntry> currentLoot = new();
        private float currentTotalChaos = 0f;

        // Custom price editor state
        private string customItemInput = string.Empty;
        private float customPriceInput = 1.0f;

        // Currency texture caching (0: Chaos, 1: Divine, 2: Exalted, 3: Gold)
        private struct CurrencyTex
        {
            public IntPtr Ptr;
            public int W;
            public int H;
            public bool Valid;
        }

        private readonly CurrencyTex[] currencyTextures = new CurrencyTex[4];
        private readonly bool[] currencyTexTried = new bool[4];

        public override void OnEnable(bool isGameOpened)
        {
            var settingsPath = this.PluginConfigPath("settings.json");
            if (File.Exists(settingsPath))
            {
                try
                {
                    var content = File.ReadAllText(settingsPath);
                    this.Settings = JsonSerializer.Deserialize<MyFarmingSettings>(content) ?? new MyFarmingSettings();
                }
                catch (Exception ex)
                {
                    PluginLog.Error("myFarming", $"Failed to load settings from {settingsPath}: {ex.Message}");
                }
            }

            this.priceHelper = new PriceHelper();
            this.diffEngine = new LootDiffEngine();
            this.killTracker = new KillTracker();
            this.goldTracker = new GoldTracker();
            this.historyStore = new HistoryStore(this.PluginConfigDirectory);
            this.sessionStore = new SessionStore(this.PluginConfigDirectory);

            this.priceHelper.ReloadPrices(this.Settings.CustomPrices);
            this.lastTickUtc = DateTime.UtcNow;

            PluginLog.Info("myFarming", "myFarming plugin enabled.");
        }

        public override void OnDisable()
        {
            if (this.isRunActive && this.currentRun != null)
            {
                this.FinalizeRun();
            }

            this.historyStore?.Save();
            this.sessionStore?.Save();
            this.SaveSettings();
            PluginLog.Info("myFarming", "myFarming plugin disabled.");
        }

        public override void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(this.PluginConfigDirectory);
                var path = this.PluginConfigPath("settings.json");
                var json = JsonSerializer.Serialize(this.Settings, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                PluginLog.Error("myFarming", $"Failed to save settings: {ex.Message}");
            }
        }

        public override void DrawUI()
        {
            var currentState = Core.States.GameCurrentState;
            // Only active in InGameState or EscapeState (when game is paused via ESC menu)
            if (currentState != GameStateTypes.InGameState && currentState != GameStateTypes.EscapeState)
            {
                if (this.isRunActive && this.currentRun != null)
                {
                    this.FinalizeRun();
                }
                this.lastAreaHash = string.Empty;
                this.lastAreaWasTownOrHideout = true;
                this.lastTickUtc = DateTime.UtcNow;
                return;
            }

            bool isGamePaused = currentState == GameStateTypes.EscapeState;
            var now = DateTime.UtcNow;

            var inGame = Core.States.InGameStateObject;
            if (inGame == null)
            {
                this.lastTickUtc = now;
                return;
            }

            bool shouldHideOverlayForPanels = this.Settings.HideWhenUiPanelsOpen &&
                                              inGame.GameUi != null &&
                                              inGame.GameUi.IsAnyLargePanelOpen;

            var areaDetails = inGame.CurrentWorldInstance?.AreaDetails;
            var area = inGame.CurrentAreaInstance;
            bool inTownOrHideout = areaDetails?.IsTown == true || areaDetails?.IsHideout == true;

            var areaHash = $"{areaDetails?.Id ?? "Unknown"}_{area?.CurrentAreaLevel ?? 0}";

            // Handle Zone / Map transitions
            if (areaHash != this.lastAreaHash)
            {
                this.OnAreaChanged(areaHash, areaDetails?.Name ?? "Unknown Area", inTownOrHideout);
                this.lastAreaHash = areaHash;
            }

            bool isPaused = this.isRunPaused || isGamePaused;

            // Run updates (0.5s rate-limited interval for smooth and lightweight scanning)
            if (this.isRunActive && this.currentRun != null && !isPaused)
            {
                double deltaSec = (now - this.lastTickUtc).TotalSeconds;
                if (deltaSec >= 0.5)
                {
                    this.activeRunDurationSec += deltaSec;
                    this.currentRun.DurationSec = (int)this.activeRunDurationSec;
                    this.lastTickUtc = now;

                    // Periodic price refresh check
                    this.priceHelper.ReloadPrices(this.Settings.CustomPrices);

                    // Scan backpack
                    this.currentLoot = this.diffEngine.Tick(this.priceHelper, out this.currentTotalChaos);
                    this.currentRun.TotalChaos = this.currentTotalChaos;
                    this.currentRun.Loot = this.currentLoot;

                    // Update kills
                    this.killTracker.Update(area, inTownOrHideout);
                    this.currentRun.KillsNormal = this.killTracker.KillsNormal;
                    this.currentRun.KillsMagic = this.killTracker.KillsMagic;
                    this.currentRun.KillsRare = this.killTracker.KillsRare;
                    this.currentRun.KillsUnique = this.killTracker.KillsUnique;

                    // Update Gold
                    this.goldTracker.Update();
                    this.currentRun.GoldGain = this.goldTracker.MapGoldGain;
                }
            }
            else
            {
                this.lastTickUtc = now;
            }

            // Render HUD overlay
            if (this.Settings.ShowOverlay && !shouldHideOverlayForPanels)
            {
                this.DrawOverlay(inTownOrHideout, areaDetails?.Name ?? "None", isPaused);
            }
        }

        private void OnAreaChanged(string areaHash, string areaName, bool inTownOrHideout)
        {
            if (inTownOrHideout)
            {
                // In town or hideout -> finalize active map run and save session
                if (this.isRunActive && this.currentRun != null)
                {
                    this.FinalizeRun();
                }

                this.isRunPaused = true;
            }
            else
            {
                // Entering a combat map
                if (this.isRunActive && this.currentRun != null)
                {
                    if (this.currentRun.AreaHash == areaHash)
                    {
                        // Returning to same map portal
                        this.isRunPaused = false;
                        this.diffEngine.ResumeMap(areaHash, this.currentLoot);
                        this.goldTracker.ResumeMap(this.currentRun.GoldGain);
                    }
                    else
                    {
                        // Different map entered -> finalize previous run and start new one
                        this.FinalizeRun();
                        this.StartNewRun(areaHash, areaName);
                    }
                }
                else
                {
                    this.StartNewRun(areaHash, areaName);
                }
            }

            this.lastAreaWasTownOrHideout = inTownOrHideout;
        }

        private void StartNewRun(string areaHash, string areaName)
        {
            this.sessionStore.EnsureTodaySession();
            this.currentRun = new MapRun
            {
                MapName = areaName,
                AreaHash = areaHash,
                DateKey = this.sessionStore.Current.DateKey,
                StartedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                StartedText = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                DurationSec = 0,
                SessionId = this.sessionStore.Current.SessionId,
                DivineRate = this.priceHelper.DivineInChaos,
                ExaltedRate = this.priceHelper.ExaltedInChaos,
            };

            this.isRunActive = true;
            this.isRunPaused = false;
            this.activeRunDurationSec = 0.0;
            this.runStartTimeUtc = DateTime.UtcNow;
            this.lastTickUtc = DateTime.UtcNow;
            this.currentTotalChaos = 0f;
            this.currentLoot.Clear();

            this.diffEngine.StartNewMap(areaHash, areaName);
            this.killTracker.Reset();
            this.goldTracker.StartMap();
        }

        private void FinalizeRun()
        {
            if (this.currentRun == null) return;

            // Only record runs that lasted at least 5 seconds or found loot/gold
            if (this.currentRun.DurationSec >= 5 || this.currentRun.TotalChaos > 0.05f || this.goldTracker.MapGoldGain > 0)
            {
                this.currentRun.Loot = new List<LootEntry>(this.currentLoot);
                this.currentRun.TotalChaos = this.currentTotalChaos;
                this.currentRun.GoldGain = this.goldTracker.MapGoldGain;
                this.currentRun.KillsNormal = this.killTracker.KillsNormal;
                this.currentRun.KillsMagic = this.killTracker.KillsMagic;
                this.currentRun.KillsRare = this.killTracker.KillsRare;
                this.currentRun.KillsUnique = this.killTracker.KillsUnique;
                this.historyStore.AddRun(this.currentRun);

                // Accumulate run into persistent daily session totals
                this.sessionStore.EnsureTodaySession();
                this.sessionStore.Current.TotalDurationSec += this.currentRun.DurationSec;
                this.sessionStore.Current.TotalChaos += this.currentRun.TotalChaos;
                this.sessionStore.Current.TotalGold += this.currentRun.GoldGain;
                this.sessionStore.Current.TotalMaps++;
                this.sessionStore.Current.KillsNormal += this.killTracker.KillsNormal;
                this.sessionStore.Current.KillsMagic += this.killTracker.KillsMagic;
                this.sessionStore.Current.KillsRare += this.killTracker.KillsRare;
                this.sessionStore.Current.KillsUnique += this.killTracker.KillsUnique;
                this.sessionStore.Save();
            }

            this.isRunActive = false;
            this.isRunPaused = false;
            this.currentRun = null;
            this.activeRunDurationSec = 0.0;
            this.currentLoot.Clear();
            this.currentTotalChaos = 0f;
            this.goldTracker.Reset();
        }

        public void ResetSession()
        {
            if (this.isRunActive && this.currentRun != null)
            {
                this.FinalizeRun();
            }

            this.sessionStore.Reset();
            PluginLog.Info("myFarming", $"Daily session for {this.sessionStore.Current.DateKey} reset.");
        }

        #region Resource & Texture Loading

        private string? ResolveResourcePath(string relativePath)
        {
            var candidates = new[]
            {
                Path.Combine(this.DllDirectory, "resources", relativePath),
                Path.Combine(AppContext.BaseDirectory, "resources", relativePath),
                Path.Combine(AppContext.BaseDirectory, "Plugins", "myFarming", "resources", relativePath),
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
                // Index 0: Chaos, 1: Divine, 2: Exalted
                string[] files = { "chaos.png", "divine.png", "exalted.png" };
                var path = this.ResolveResourcePath(Path.Combine("currency", "poe2", files[idx]))
                           ?? this.ResolveResourcePath(Path.Combine("images", "currency", files[idx]));

                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    Core.Overlay.AddOrGetImagePointer(path, false, out var ptr, out var w, out var h);
                    if (ptr != IntPtr.Zero)
                    {
                        this.currencyTextures[idx] = new CurrencyTex { Ptr = ptr, W = (int)w, H = (int)h, Valid = true };
                    }
                }
            }

            return this.currencyTextures[idx].Valid ? this.currencyTextures[idx] : null;
        }

        private void DrawCurrencyIcon(DisplayCurrency c, float sizeMultiplier = 1.35f)
        {
            var tex = this.GetCurrencyTexture(c);
            if (tex != null && tex.Value.Valid)
            {
                float lineH = ImGui.GetTextLineHeight();
                float size = lineH * sizeMultiplier;
                float curY = ImGui.GetCursorPosY();
                float yOffset = (lineH - size) * 0.5f;
                ImGui.SetCursorPosY(curY + yOffset);
                ImGui.Image(tex.Value.Ptr, new Vector2(size, size));
                ImGui.SetCursorPosY(curY);
                ImGui.SameLine(0, 4);
            }
            else
            {
                string sym = c == DisplayCurrency.Divine ? "Div" : (c == DisplayCurrency.Exalted ? "Ex" : "c");
                ImGui.Text(sym);
                ImGui.SameLine(0, 4);
            }
        }

        private void DrawGoldIcon(float sizeMultiplier = 1.25f)
        {
            if (!this.currencyTexTried[3])
            {
                this.currencyTexTried[3] = true;
                var path = this.ResolveResourcePath(Path.Combine("currency", "poe2", "gold.png"))
                           ?? this.ResolveResourcePath(Path.Combine("images", "currency", "gold.png"));

                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    Core.Overlay.AddOrGetImagePointer(path, false, out var ptr, out var w, out var h);
                    if (ptr != IntPtr.Zero)
                    {
                        this.currencyTextures[3] = new CurrencyTex { Ptr = ptr, W = (int)w, H = (int)h, Valid = true };
                    }
                }
            }

            if (this.currencyTextures[3].Valid)
            {
                float lineH = ImGui.GetTextLineHeight();
                float size = lineH * sizeMultiplier;
                float curY = ImGui.GetCursorPosY();
                float yOffset = (lineH - size) * 0.5f;
                ImGui.SetCursorPosY(curY + yOffset);
                ImGui.Image(this.currencyTextures[3].Ptr, new Vector2(size, size));
                ImGui.SetCursorPosY(curY);
                ImGui.SameLine(0, 4);
            }
            else
            {
                ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f), "Gold");
                ImGui.SameLine(0, 4);
            }
        }

        #endregion

        #region UI Rendering

        private void DrawColoredKillBadge(uint color, int count)
        {
            var dl = ImGui.GetWindowDrawList();
            float lineH = ImGui.GetTextLineHeight();
            float badgeSize = lineH * 0.72f;
            var screenPos = ImGui.GetCursorScreenPos();
            float yOff = (lineH - badgeSize) * 0.5f;

            dl.AddRectFilled(
                new Vector2(screenPos.X, screenPos.Y + yOff),
                new Vector2(screenPos.X + badgeSize, screenPos.Y + yOff + badgeSize),
                color,
                2.5f);

            ImGui.Dummy(new Vector2(badgeSize, lineH));
            ImGui.SameLine(0, 3);
            ImGui.Text($"{count}");
            ImGui.SameLine(0, 6);
        }

        private void DrawColoredKills(int normal, int magic, int rare, int unique)
        {
            uint colNormal = ImGui.GetColorU32(new Vector4(0.88f, 0.88f, 0.88f, 1f));
            uint colMagic = ImGui.GetColorU32(new Vector4(0.35f, 0.60f, 1.0f, 1f));
            uint colRare = ImGui.GetColorU32(new Vector4(1.0f, 0.85f, 0.25f, 1f));
            uint colUnique = ImGui.GetColorU32(new Vector4(1.0f, 0.50f, 0.15f, 1f));

            this.DrawColoredKillBadge(colNormal, normal);
            this.DrawColoredKillBadge(colMagic, magic);
            this.DrawColoredKillBadge(colRare, rare);
            this.DrawColoredKillBadge(colUnique, unique);

            int total = normal + magic + rare + unique;
            ImGui.TextDisabled($"({total})");
        }

        private void DrawOverlay(bool inTownOrHideout, string currentZoneName, bool isPaused)
        {
            var flags = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse;
            if (this.Settings.LockOverlay)
            {
                flags |= ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize;
            }

            ImGui.SetNextWindowPos(new Vector2(this.Settings.OverlayX, this.Settings.OverlayY), ImGuiCond.FirstUseEver);

            float bgAlpha = Math.Clamp(this.Settings.BackgroundOpacity, 0.0f, 1.0f);
            ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.06f, 0.07f, 0.09f, bgAlpha));
            ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.25f, 0.30f, 0.40f, Math.Min(bgAlpha, 0.50f)));

            bool open = true;
            bool shouldDraw = this.Settings.LockOverlay
                ? ImGui.Begin("myFarming###myFarmingOverlay", flags)
                : ImGui.Begin("myFarming###myFarmingOverlay", ref open, flags);

            if (!open && !this.Settings.LockOverlay)
            {
                this.Settings.ShowOverlay = false;
            }

            if (shouldDraw)
            {
                float fontScale = Math.Clamp(this.Settings.TextScale, 0.5f, 2.5f);
                ImGui.SetWindowFontScale(fontScale);
                var curPos = ImGui.GetWindowPos();
                this.Settings.OverlayX = curPos.X;
                this.Settings.OverlayY = curPos.Y;

                // Live Session Totals for Today (accumulated finished runs + currently active run)
                int liveSessionSec = this.sessionStore.Current.TotalDurationSec + (this.currentRun?.DurationSec ?? 0);
                float liveSessionChaos = this.sessionStore.Current.TotalChaos + this.currentTotalChaos;
                int liveSessionMaps = this.sessionStore.Current.TotalMaps + (this.isRunActive ? 1 : 0);

                // Line 1: Kills (White / Magic / Rare / Unique / Total)
                if (this.Settings.ShowKills)
                {
                    int kNorm = (this.isRunActive && !inTownOrHideout) ? this.killTracker.KillsNormal : this.sessionStore.Current.KillsNormal;
                    int kMag = (this.isRunActive && !inTownOrHideout) ? this.killTracker.KillsMagic : this.sessionStore.Current.KillsMagic;
                    int kRare = (this.isRunActive && !inTownOrHideout) ? this.killTracker.KillsRare : this.sessionStore.Current.KillsRare;
                    int kUniq = (this.isRunActive && !inTownOrHideout) ? this.killTracker.KillsUnique : this.sessionStore.Current.KillsUnique;

                    this.DrawColoredKills(kNorm, kMag, kRare, kUniq);
                }

                // Line 2: Session info & Total Profit & Profit per hour & maps count & Gold
                if (this.Settings.ShowSessionSummary)
                {
                    int sHrs = liveSessionSec / 3600;
                    int sMin = (liveSessionSec % 3600) / 60;
                    int sSec = liveSessionSec % 60;
                    string sessionTimeStr = sHrs > 0 ? $"{sHrs:D2}:{sMin:D2}:{sSec:D2}" : $"{sMin:D2}:{sSec:D2}";

                    float sessionRateChaos = liveSessionSec > 5 ? (liveSessionChaos * 3600f / liveSessionSec) : 0f;
                    string totalProfitStr = this.FormatCurrencyValueOnly(liveSessionChaos);
                    string rateProfitStr = this.FormatCurrencyValueOnly(sessionRateChaos);

                    ImGui.Text($"Session: {sessionTimeStr} | Total: +{totalProfitStr}");
                    ImGui.SameLine(0, 3);
                    this.DrawCurrencyIcon(this.Settings.Currency, 1.15f);

                    if (this.Settings.ShowProfitPerHour)
                    {
                        ImGui.Text($"({rateProfitStr}/h) [{liveSessionMaps} maps]");
                    }
                    else
                    {
                        ImGui.Text($"[{liveSessionMaps} maps]");
                    }

                    if (this.Settings.ShowGold)
                    {
                        long liveSessionGold = this.sessionStore.Current.TotalGold + (this.isRunActive ? this.goldTracker.MapGoldGain : 0);
                        ImGui.SameLine(0, 4);
                        ImGui.TextDisabled("|");
                        ImGui.SameLine(0, 4);
                        this.DrawGoldIcon(1.15f);
                        ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f), GoldTracker.FormatGold(liveSessionGold));
                    }
                }

                // Line 3: Current Map / Zone status & loot earned & Gold
                if (this.Settings.ShowCurrentMapStats)
                {
                    if (this.isRunActive && !inTownOrHideout && this.currentRun != null)
                    {
                        int mDur = this.currentRun.DurationSec;
                        int mMin = mDur / 60;
                        int mSec = mDur % 60;
                        string mapTimeStr = $"{mMin:D2}:{mSec:D2}";
                        string mapValStr = this.FormatCurrencyValueOnly(this.currentTotalChaos);

                        ImGui.Text($"{this.currentRun.MapName} ({mapTimeStr}) | +{mapValStr}");
                        ImGui.SameLine(0, 3);
                        this.DrawCurrencyIcon(this.Settings.Currency, 1.15f);

                        if (this.Settings.ShowGold)
                        {
                            ImGui.SameLine(0, 4);
                            this.DrawGoldIcon(1.15f);
                            ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f), $"+{GoldTracker.FormatGold(this.goldTracker.MapGoldGain)}");
                        }
                    }
                    else
                    {
                        string statusText = inTownOrHideout ? (currentZoneName.Length > 0 ? currentZoneName : "Town / Hideout") : "Idle";
                        string totalTodayValStr = this.FormatCurrencyValueOnly(liveSessionChaos);
                        ImGui.TextDisabled($"{statusText} | Total: +{totalTodayValStr}");
                        ImGui.SameLine(0, 3);
                        this.DrawCurrencyIcon(this.Settings.Currency, 1.15f);

                        if (this.Settings.ShowGold && this.sessionStore.Current.TotalGold > 0)
                        {
                            ImGui.SameLine(0, 4);
                            this.DrawGoldIcon(1.15f);
                            ImGui.TextDisabled($"+{GoldTracker.FormatGold(this.sessionStore.Current.TotalGold)}");
                        }
                    }
                }

                // Optional: Loot list preview if enabled
                if (this.Settings.ShowLootList && this.currentLoot.Count > 0 && !inTownOrHideout)
                {
                    ImGui.Separator();
                    ImGui.TextDisabled($"Looted ({this.currentLoot.Count}):");

                    int shown = 0;
                    foreach (var item in this.currentLoot)
                    {
                        if (item.ChaosEach < this.Settings.MinItemPriceToDisplay) continue;
                        if (shown >= this.Settings.MaxLootListRows)
                        {
                            ImGui.TextDisabled($"... and {this.currentLoot.Count - shown} more");
                            break;
                        }

                        string itemVal = this.FormatCurrency(item.TotalChaos);
                        ImGui.Bullet();
                        ImGui.SameLine(0, 4);
                        ImGui.Text($"{item.StackCount}x {item.Name} ({itemVal})");
                        shown++;
                    }
                }

                ImGui.End();
            }

            ImGui.PopStyleColor(2);
        }

        private string FormatCurrencyValueOnly(float chaos)
        {
            switch (this.Settings.Currency)
            {
                case DisplayCurrency.Divine:
                    float div = this.priceHelper.DivineInChaos > 0 ? (chaos / this.priceHelper.DivineInChaos) : 0f;
                    return $"{div:F2}";
                case DisplayCurrency.Exalted:
                    float ex = this.priceHelper.ExaltedInChaos > 0 ? (chaos / this.priceHelper.ExaltedInChaos) : 0f;
                    return $"{ex:F2}";
                default:
                    return $"{chaos:F1}";
            }
        }

        private string FormatCurrency(float chaos)
        {
            switch (this.Settings.Currency)
            {
                case DisplayCurrency.Divine:
                    float div = this.priceHelper.DivineInChaos > 0 ? (chaos / this.priceHelper.DivineInChaos) : 0f;
                    return $"{div:F2} Div";
                case DisplayCurrency.Exalted:
                    float ex = this.priceHelper.ExaltedInChaos > 0 ? (chaos / this.priceHelper.ExaltedInChaos) : 0f;
                    return $"{ex:F2} Ex";
                default:
                    return $"{chaos:F1} c";
            }
        }

        public override void DrawSettings()
        {
            if (ImGui.BeginTabBar("myFarmingTabBar"))
            {
                if (ImGui.BeginTabItem("General"))
                {
                    this.DrawTabGeneral();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Statistics & History"))
                {
                    this.DrawTabHistory();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Custom Prices"))
                {
                    this.DrawTabCustomPrices();
                    ImGui.EndTabItem();
                }

                ImGui.EndTabBar();
            }
        }

        private void DrawTabGeneral()
        {
            bool showOverlay = this.Settings.ShowOverlay;
            if (ImGui.Checkbox("Show HUD Overlay", ref showOverlay))
            {
                this.Settings.ShowOverlay = showOverlay;
            }

            bool hidePanels = this.Settings.HideWhenUiPanelsOpen;
            if (ImGui.Checkbox("Hide HUD when Panels are Open (Stash, Inventory, Passives, etc.)", ref hidePanels))
            {
                this.Settings.HideWhenUiPanelsOpen = hidePanels;
            }

            bool lockOverlay = this.Settings.LockOverlay;
            if (ImGui.Checkbox("Lock HUD (Hide Titlebar & Close Button)", ref lockOverlay))
            {
                this.Settings.LockOverlay = lockOverlay;
            }

            float bgOpacity = this.Settings.BackgroundOpacity;
            if (ImGui.SliderFloat("Background Opacity", ref bgOpacity, 0.0f, 1.0f, "%.2f"))
            {
                this.Settings.BackgroundOpacity = bgOpacity;
            }

            float textScale = this.Settings.TextScale;
            if (ImGui.SliderFloat("Text Scale", ref textScale, 0.5f, 2.0f, "%.2fx"))
            {
                this.Settings.TextScale = textScale;
            }

            bool showKills = this.Settings.ShowKills;
            if (ImGui.Checkbox("Show Kills Count Badges (Normal / Magic / Rare / Unique)", ref showKills))
            {
                this.Settings.ShowKills = showKills;
            }

            bool showGold = this.Settings.ShowGold;
            if (ImGui.Checkbox("Show Gold Count in HUD", ref showGold))
            {
                this.Settings.ShowGold = showGold;
            }

            bool showSessionSummary = this.Settings.ShowSessionSummary;
            if (ImGui.Checkbox("Show Daily Session Line in HUD", ref showSessionSummary))
            {
                this.Settings.ShowSessionSummary = showSessionSummary;
            }

            bool showCurrentMap = this.Settings.ShowCurrentMapStats;
            if (ImGui.Checkbox("Show Current Map Line in HUD", ref showCurrentMap))
            {
                this.Settings.ShowCurrentMapStats = showCurrentMap;
            }

            bool showLootList = this.Settings.ShowLootList;
            if (ImGui.Checkbox("Show Looted Items List in HUD", ref showLootList))
            {
                this.Settings.ShowLootList = showLootList;
            }

            if (this.Settings.ShowLootList)
            {
                int maxRows = this.Settings.MaxLootListRows;
                if (ImGui.SliderInt("Max Loot Rows Shown", ref maxRows, 3, 30))
                {
                    this.Settings.MaxLootListRows = maxRows;
                }
            }

            bool showRate = this.Settings.ShowProfitPerHour;
            if (ImGui.Checkbox("Show Profit / Hour", ref showRate))
            {
                this.Settings.ShowProfitPerHour = showRate;
            }

            bool autoPause = this.Settings.AutoPauseInTownOrHideout;
            if (ImGui.Checkbox("Auto-Pause in Town & Hideout", ref autoPause))
            {
                this.Settings.AutoPauseInTownOrHideout = autoPause;
            }

            ImGui.Separator();

            int curIdx = (int)this.Settings.Currency;
            string[] currencies = { "Chaos (c)", "Divine (Div)", "Exalted (Ex)" };
            if (ImGui.Combo("Display Currency", ref curIdx, currencies, currencies.Length))
            {
                this.Settings.Currency = (DisplayCurrency)curIdx;
            }

            float minPrice = this.Settings.MinItemPriceToDisplay;
            if (ImGui.SliderFloat("Min Item Price (c) to Display", ref minPrice, 0f, 10f, "%.1f c"))
            {
                this.Settings.MinItemPriceToDisplay = minPrice;
            }

            ImGui.Separator();

            // Daily Session Status
            ImGui.TextColored(new Vector4(0.35f, 0.75f, 1f, 1f), $"Today's Session ({this.sessionStore.Current.DateKey}):");
            string goldStr = GoldTracker.FormatGold(this.sessionStore.Current.TotalGold);
            ImGui.Text($"Maps Completed: {this.sessionStore.Current.TotalMaps} maps | Total Loot: {this.FormatCurrency(this.sessionStore.Current.TotalChaos)} | Gold: {goldStr}");
            int sHrs = this.sessionStore.Current.TotalDurationSec / 3600;
            int sMin = (this.sessionStore.Current.TotalDurationSec % 3600) / 60;
            int sSec = this.sessionStore.Current.TotalDurationSec % 60;
            ImGui.Text($"Total Time: {sHrs:D2}:{sMin:D2}:{sSec:D2} | Kills: {this.sessionStore.Current.TotalKills}");

            if (ImGui.Button("Reset Today's Session"))
            {
                this.ResetSession();
            }
        }

        private void DrawTabHistory()
        {
            var runs = this.historyStore.Runs;
            int totalRuns = runs.Count;
            float totalChaos = runs.Sum(r => r.TotalChaos);
            long totalGold = runs.Sum(r => r.GoldGain);
            int totalSec = runs.Sum(r => r.DurationSec);
            int totalKills = runs.Sum(r => r.KillsTotal);

            ImGui.TextColored(new Vector4(0.4f, 0.85f, 1f, 1f), "All-Time Summary:");
            int hrs = totalSec / 3600;
            int mins = (totalSec % 3600) / 60;
            string totalGoldStr = GoldTracker.FormatGold(totalGold);
            ImGui.Text($"Total Maps: {totalRuns} | Time: {hrs}h {mins}m | Total Loot: {this.FormatCurrency(totalChaos)} | Gold: {totalGoldStr} | Kills: {totalKills}");

            if (totalSec > 60)
            {
                float avgChaosPerHour = totalChaos * 3600f / totalSec;
                ImGui.TextDisabled($"Average Rate: {this.FormatCurrency(avgChaosPerHour)}/hr");
            }

            ImGui.Separator();

            if (ImGui.Button("Clear All History"))
            {
                this.historyStore.ClearAll();
            }

            ImGui.Spacing();

            if (runs.Count == 0)
            {
                ImGui.TextDisabled("No completed runs recorded yet.");
                return;
            }

            if (ImGui.BeginTable("RunsTable", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(0, 350)))
            {
                ImGui.TableSetupColumn("Date / Time", ImGuiTableColumnFlags.WidthFixed, 130);
                ImGui.TableSetupColumn("Map", ImGuiTableColumnFlags.WidthStretch, 130);
                ImGui.TableSetupColumn("Duration", ImGuiTableColumnFlags.WidthFixed, 65);
                ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 85);
                ImGui.TableSetupColumn("Gold", ImGuiTableColumnFlags.WidthFixed, 65);
                ImGui.TableSetupColumn("Kills", ImGuiTableColumnFlags.WidthFixed, 55);
                ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 55);
                ImGui.TableHeadersRow();

                string? toDelete = null;

                foreach (var run in runs)
                {
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    ImGui.Text(run.StartedText);

                    ImGui.TableNextColumn();
                    ImGui.Text(run.MapName);

                    ImGui.TableNextColumn();
                    int m = run.DurationSec / 60;
                    int s = run.DurationSec % 60;
                    ImGui.Text($"{m:D2}:{s:D2}");

                    ImGui.TableNextColumn();
                    ImGui.TextColored(new Vector4(1f, 0.84f, 0.2f, 1f), this.FormatCurrency(run.TotalChaos));

                    ImGui.TableNextColumn();
                    ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f), GoldTracker.FormatGold(run.GoldGain));

                    ImGui.TableNextColumn();
                    ImGui.Text($"{run.KillsTotal}");

                    ImGui.TableNextColumn();
                    if (ImGui.SmallButton($"Del##{run.Id}"))
                    {
                        toDelete = run.Id;
                    }
                }

                if (toDelete != null)
                {
                    this.historyStore.DeleteRun(toDelete);
                }

                ImGui.EndTable();
            }
        }

        private void DrawTabCustomPrices()
        {
            ImGui.TextDisabled("Override prices (in Chaos) for items missed or undervalued by ninja:");

            ImGui.InputText("Item Name", ref this.customItemInput, 64);
            ImGui.InputFloat("Price (Chaos)", ref this.customPriceInput, 0.5f, 5.0f, "%.2f c");

            if (ImGui.Button("Add / Update Price") && !string.IsNullOrWhiteSpace(this.customItemInput))
            {
                this.Settings.CustomPrices[this.customItemInput.Trim()] = this.customPriceInput;
                this.priceHelper.ReloadPrices(this.Settings.CustomPrices);
                this.SaveSettings();
                this.customItemInput = string.Empty;
            }

            ImGui.Separator();

            if (this.Settings.CustomPrices.Count == 0)
            {
                ImGui.TextDisabled("No custom prices added.");
                return;
            }

            string? toRemove = null;
            foreach (var kvp in this.Settings.CustomPrices)
            {
                ImGui.Text($"{kvp.Key}: {kvp.Value:F1} c");
                ImGui.SameLine();
                if (ImGui.SmallButton($"Remove##{kvp.Key}"))
                {
                    toRemove = kvp.Key;
                }
            }

            if (toRemove != null)
            {
                this.Settings.CustomPrices.Remove(toRemove);
                this.priceHelper.ReloadPrices(this.Settings.CustomPrices);
                this.SaveSettings();
            }
        }

        #endregion
    }
}
