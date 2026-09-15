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
        [LibraryImport("user32.dll")]
        private static partial IntPtr GetForegroundWindow();

        private PriceHelper priceHelper = null!;
        private LootDiffEngine diffEngine = null!;
        private KillTracker killTracker = null!;
        private HistoryStore historyStore = null!;
        private SessionStore sessionStore = null!;

        // Runtime run state
        private bool isRunActive = false;
        private bool isRunPaused = false;
        private MapRun? currentRun = null;
        private DateTime runStartTimeUtc = DateTime.MinValue;
        private DateTime lastTickUtc = DateTime.MinValue;
        private string lastAreaHash = string.Empty;
        private List<LootEntry> currentLoot = new();
        private float currentTotalChaos = 0f;

        // Custom price editor state
        private string customItemInput = string.Empty;
        private float customPriceInput = 1.0f;

        // Currency texture caching
        private struct CurrencyTex
        {
            public IntPtr Ptr;
            public int W;
            public int H;
            public bool Valid;
        }

        private readonly CurrencyTex[] currencyTextures = new CurrencyTex[3];
        private readonly bool[] currencyTexTried = new bool[3];

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

            // Run updates
            if (this.isRunActive && this.currentRun != null && !isPaused)
            {
                double deltaSec = (now - this.lastTickUtc).TotalSeconds;
                if (deltaSec >= 1.0)
                {
                    this.currentRun.DurationSec += (int)deltaSec;
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
                }
            }
            else
            {
                this.lastTickUtc = now;
            }

            // Render HUD overlay
            if (this.Settings.ShowOverlay)
            {
                if (!this.Settings.HideWhenGameNotFocused || this.IsGameOrOverlayForeground())
                {
                    this.DrawOverlay(inTownOrHideout, areaDetails?.Name ?? "None", isPaused);
                }
            }
        }

        private bool IsGameOrOverlayForeground()
        {
            if (Core.Process.Foreground) return true;
            var fg = GetForegroundWindow();
            var mainHwnd = Process.GetCurrentProcess().MainWindowHandle;
            return fg != IntPtr.Zero && fg == mainHwnd;
        }

        private void OnAreaChanged(string areaHash, string areaName, bool inTownOrHideout)
        {
            if (inTownOrHideout)
            {
                // In town or hideout
                if (this.isRunActive && this.Settings.AutoPauseInTownOrHideout)
                {
                    this.isRunPaused = true;
                }
            }
            else
            {
                // In combat map
                if (this.isRunActive && this.currentRun != null)
                {
                    if (this.currentRun.AreaHash == areaHash)
                    {
                        // Returning to same map
                        this.isRunPaused = false;
                        this.diffEngine.ResumeMap(areaHash, this.currentLoot);
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
        }

        private void StartNewRun(string areaHash, string areaName)
        {
            this.currentRun = new MapRun
            {
                MapName = areaName,
                AreaHash = areaHash,
                StartedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                StartedText = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                DurationSec = 0,
                SessionId = this.sessionStore.Current.SessionId,
                DivineRate = this.priceHelper.DivineInChaos,
                ExaltedRate = this.priceHelper.ExaltedInChaos,
            };

            this.isRunActive = true;
            this.isRunPaused = false;
            this.runStartTimeUtc = DateTime.UtcNow;
            this.lastTickUtc = DateTime.UtcNow;
            this.currentTotalChaos = 0f;
            this.currentLoot.Clear();

            this.diffEngine.StartNewMap(areaHash, areaName);
            this.killTracker.Reset();
        }

        private void FinalizeRun()
        {
            if (this.currentRun == null) return;

            // Only record runs that lasted at least 5 seconds or found loot
            if (this.currentRun.DurationSec >= 5 || this.currentRun.TotalChaos > 0.05f)
            {
                this.currentRun.Loot = new List<LootEntry>(this.currentLoot);
                this.currentRun.TotalChaos = this.currentTotalChaos;
                this.historyStore.AddRun(this.currentRun);

                // Accumulate run into persistent session totals
                this.sessionStore.Current.TotalDurationSec += this.currentRun.DurationSec;
                this.sessionStore.Current.TotalChaos += this.currentRun.TotalChaos;
                this.sessionStore.Current.TotalMaps++;
                this.sessionStore.Current.KillsNormal += this.currentRun.KillsNormal;
                this.sessionStore.Current.KillsMagic += this.currentRun.KillsMagic;
                this.sessionStore.Current.KillsRare += this.currentRun.KillsRare;
                this.sessionStore.Current.KillsUnique += this.currentRun.KillsUnique;
                this.sessionStore.Save();
            }

            this.isRunActive = false;
            this.isRunPaused = false;
            this.currentRun = null;
            this.currentLoot.Clear();
            this.currentTotalChaos = 0f;
        }

        public void ResetSession()
        {
            if (this.isRunActive && this.currentRun != null)
            {
                this.FinalizeRun();
            }

            this.sessionStore.Reset();
            PluginLog.Info("myFarming", $"Session #{this.sessionStore.Current.SessionId} started.");
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
        }

        #endregion

        #region UI Rendering

        private void DrawColoredKills(int normal, int magic, int rare, int unique)
        {
            int total = normal + magic + rare + unique;
            ImGui.Text($"Kills: {total} (");
            ImGui.SameLine(0, 0);
            ImGui.TextColored(new Vector4(0.85f, 0.85f, 0.85f, 1f), $"W:{normal} ");
            ImGui.SameLine(0, 0);
            ImGui.TextColored(new Vector4(0.53f, 0.63f, 1f, 1f), $"M:{magic} ");
            ImGui.SameLine(0, 0);
            ImGui.TextColored(new Vector4(1f, 0.9f, 0.35f, 1f), $"R:{rare} ");
            ImGui.SameLine(0, 0);
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.2f, 1f), $"U:{unique}");
            ImGui.SameLine(0, 0);
            ImGui.Text(")");
        }

        private void DrawOverlay(bool inTownOrHideout, string currentZoneName, bool isPaused)
        {
            var flags = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse;
            if (this.Settings.LockOverlay)
            {
                flags |= ImGuiWindowFlags.NoMove;
            }

            ImGui.SetNextWindowPos(new Vector2(this.Settings.OverlayX, this.Settings.OverlayY), ImGuiCond.FirstUseEver);

            bool open = true;
            if (ImGui.Begin("myFarming###myFarmingOverlay", ref open, flags))
            {
                float fontScale = Math.Clamp(this.Settings.TextScale, 0.5f, 2.5f);
                ImGui.SetWindowFontScale(fontScale);
                var curPos = ImGui.GetWindowPos();
                this.Settings.OverlayX = curPos.X;
                this.Settings.OverlayY = curPos.Y;

                // Live Session Totals (accumulated finished runs + currently active run)
                int liveSessionSec = this.sessionStore.Current.TotalDurationSec + (this.currentRun?.DurationSec ?? 0);
                float liveSessionChaos = this.sessionStore.Current.TotalChaos + this.currentTotalChaos;
                int liveSessionMaps = this.sessionStore.Current.TotalMaps + (this.isRunActive ? 1 : 0);
                int liveKillsNormal = this.sessionStore.Current.KillsNormal + this.killTracker.KillsNormal;
                int liveKillsMagic = this.sessionStore.Current.KillsMagic + this.killTracker.KillsMagic;
                int liveKillsRare = this.sessionStore.Current.KillsRare + this.killTracker.KillsRare;
                int liveKillsUnique = this.sessionStore.Current.KillsUnique + this.killTracker.KillsUnique;

                // Status line
                string statusText = this.isRunActive
                    ? (isPaused ? "⏸ PAUSED" : "▶ FARMING")
                    : "IDLE";
                Vector4 statusColor = this.isRunActive
                    ? (isPaused ? new Vector4(1f, 0.8f, 0.2f, 1f) : new Vector4(0.3f, 1f, 0.3f, 1f))
                    : new Vector4(0.7f, 0.7f, 0.7f, 1f);

                ImGui.TextColored(statusColor, statusText);
                ImGui.SameLine();
                ImGui.TextDisabled($"| Session #{this.sessionStore.Current.SessionId}");
                ImGui.SameLine();
                if (ImGui.SmallButton("New Session"))
                {
                    this.ResetSession();
                }

                // Session Summary (retained across maps until reset)
                if (this.Settings.ShowSessionSummary)
                {
                    int sHrs = liveSessionSec / 3600;
                    int sMin = (liveSessionSec % 3600) / 60;
                    int sSec = liveSessionSec % 60;
                    ImGui.Text($"Session: {liveSessionMaps} maps ({sHrs:D2}:{sMin:D2}:{sSec:D2})");

                    // Session Loot
                    ImGui.TextColored(new Vector4(1f, 0.84f, 0.2f, 1f), "Session Loot: ");
                    ImGui.SameLine(0, 2);
                    this.DrawCurrencyIcon(this.Settings.Currency, 1.4f);
                    ImGui.TextColored(new Vector4(1f, 0.84f, 0.2f, 1f), this.FormatCurrency(liveSessionChaos));

                    if (this.Settings.ShowProfitPerHour && liveSessionSec > 5)
                    {
                        float sessionRate = liveSessionChaos * 3600f / liveSessionSec;
                        ImGui.TextDisabled("Session Rate: ");
                        ImGui.SameLine(0, 2);
                        this.DrawCurrencyIcon(this.Settings.Currency, 1.25f);
                        ImGui.TextDisabled($"{this.FormatCurrency(sessionRate)}/hr");
                    }

                    if (this.Settings.ShowKills && this.sessionStore.Current.TotalMaps > 0)
                    {
                        this.DrawColoredKills(liveKillsNormal, liveKillsMagic, liveKillsRare, liveKillsUnique);
                    }
                }

                // Current Map Section
                if (this.Settings.ShowCurrentMapStats && (this.isRunActive || !inTownOrHideout))
                {
                    ImGui.Separator();
                    string mapTitle = this.currentRun != null ? this.currentRun.MapName : currentZoneName;
                    int duration = this.currentRun?.DurationSec ?? 0;
                    int min = duration / 60;
                    int sec = duration % 60;
                    ImGui.Text($"Map: {mapTitle} ({min:D2}:{sec:D2})");

                    if (this.sessionStore.Current.TotalMaps > 0)
                    {
                        ImGui.Text("Map Loot: ");
                        ImGui.SameLine(0, 2);
                        this.DrawCurrencyIcon(this.Settings.Currency, 1.3f);
                        ImGui.TextColored(new Vector4(1f, 0.84f, 0.2f, 1f), this.FormatCurrency(this.currentTotalChaos));
                    }

                    if (this.Settings.ShowKills)
                    {
                        this.DrawColoredKills(this.killTracker.KillsNormal, this.killTracker.KillsMagic, this.killTracker.KillsRare, this.killTracker.KillsUnique);
                    }
                }

                // Loot list preview
                if (this.Settings.ShowLootList && this.currentLoot.Count > 0)
                {
                    ImGui.Separator();
                    ImGui.TextDisabled($"Looted Items ({this.currentLoot.Count}):");

                    int shown = 0;
                    foreach (var item in this.currentLoot)
                    {
                        if (item.ChaosEach < this.Settings.MinItemPriceToDisplay) continue;
                        if (shown >= this.Settings.MaxLootListRows)
                        {
                            ImGui.TextDisabled($"... and {this.currentLoot.Count - shown} more items");
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

            bool hideNotFocused = this.Settings.HideWhenGameNotFocused;
            if (ImGui.Checkbox("Hide When Game Not Focused", ref hideNotFocused))
            {
                this.Settings.HideWhenGameNotFocused = hideNotFocused;
            }

            bool lockOverlay = this.Settings.LockOverlay;
            if (ImGui.Checkbox("Lock HUD Position", ref lockOverlay))
            {
                this.Settings.LockOverlay = lockOverlay;
            }

            float textScale = this.Settings.TextScale;
            if (ImGui.SliderFloat("Text Scale", ref textScale, 0.5f, 2.0f, "%.2fx"))
            {
                this.Settings.TextScale = textScale;
            }

            bool showSessionSummary = this.Settings.ShowSessionSummary;
            if (ImGui.Checkbox("Show Multi-Map Session Summary in HUD", ref showSessionSummary))
            {
                this.Settings.ShowSessionSummary = showSessionSummary;
            }

            bool showCurrentMap = this.Settings.ShowCurrentMapStats;
            if (ImGui.Checkbox("Show Current Map Details in HUD", ref showCurrentMap))
            {
                this.Settings.ShowCurrentMapStats = showCurrentMap;
            }

            bool showLootList = this.Settings.ShowLootList;
            if (ImGui.Checkbox("Show Looted Items List in HUD", ref showLootList))
            {
                this.Settings.ShowLootList = showLootList;
            }

            int maxRows = this.Settings.MaxLootListRows;
            if (ImGui.SliderInt("Max Loot Rows Shown", ref maxRows, 3, 30))
            {
                this.Settings.MaxLootListRows = maxRows;
            }

            bool showKills = this.Settings.ShowKills;
            if (ImGui.Checkbox("Show Kills Count (White / Magic / Rare / Unique)", ref showKills))
            {
                this.Settings.ShowKills = showKills;
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

            // Session Control
            ImGui.Text($"Current Session: #{this.sessionStore.Current.SessionId}");
            ImGui.Text($"Session Progress: {this.sessionStore.Current.TotalMaps} maps completed | Total Loot: {this.FormatCurrency(this.sessionStore.Current.TotalChaos)}");
            int sHrs = this.sessionStore.Current.TotalDurationSec / 3600;
            int sMin = (this.sessionStore.Current.TotalDurationSec % 3600) / 60;
            int sSec = this.sessionStore.Current.TotalDurationSec % 60;
            ImGui.Text($"Session Duration: {sHrs:D2}:{sMin:D2}:{sSec:D2}");

            if (ImGui.Button("Start New Session (Reset Counters)"))
            {
                this.ResetSession();
            }
        }

        private void DrawTabHistory()
        {
            var runs = this.historyStore.Runs;
            int totalRuns = runs.Count;
            float totalChaos = runs.Sum(r => r.TotalChaos);
            int totalSec = runs.Sum(r => r.DurationSec);
            int totalKills = runs.Sum(r => r.KillsTotal);

            ImGui.TextColored(new Vector4(0.4f, 0.85f, 1f, 1f), "All-Time Summary:");
            int hrs = totalSec / 3600;
            int mins = (totalSec % 3600) / 60;
            ImGui.Text($"Total Maps: {totalRuns} | Time: {hrs}h {mins}m | Total Loot: {this.FormatCurrency(totalChaos)} | Kills: {totalKills}");

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

            if (ImGui.BeginTable("RunsTable", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(0, 350)))
            {
                ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 120);
                ImGui.TableSetupColumn("Map", ImGuiTableColumnFlags.WidthStretch, 140);
                ImGui.TableSetupColumn("Duration", ImGuiTableColumnFlags.WidthFixed, 70);
                ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 90);
                ImGui.TableSetupColumn("Kills", ImGuiTableColumnFlags.WidthFixed, 60);
                ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 60);
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
