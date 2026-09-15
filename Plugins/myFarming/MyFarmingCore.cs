namespace myFarming
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Numerics;
    using System.Text.Encodings.Web;
    using System.Text.Json;
    using ImGuiNET;
    using TEHhub;
    using TEHhub.Plugin;
    using TEHhub.RemoteEnums;

    public sealed class MyFarmingCore : PCore<MyFarmingSettings>
    {
        private PriceHelper priceHelper = null!;
        private LootDiffEngine diffEngine = null!;
        private KillTracker killTracker = null!;
        private HistoryStore historyStore = null!;

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

        public override void OnEnable(bool isGameOpened)
        {
            this.priceHelper = new PriceHelper();
            this.diffEngine = new LootDiffEngine();
            this.killTracker = new KillTracker();
            this.historyStore = new HistoryStore(this.PluginConfigDirectory);

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
            if (Core.States.GameCurrentState != GameStateTypes.InGameState) return;

            var inGame = Core.States.InGameStateObject;
            if (inGame == null) return;

            var areaDetails = inGame.CurrentWorldInstance?.AreaDetails;
            var area = inGame.CurrentAreaInstance;
            bool inTownOrHideout = areaDetails?.IsTown == true || areaDetails?.IsHideout == true;

            var areaHash = $"{areaDetails?.Id ?? "Unknown"}_{area?.CurrentAreaLevel ?? 0}";
            var now = DateTime.UtcNow;

            // Handle Zone / Map transitions
            if (areaHash != this.lastAreaHash)
            {
                this.OnAreaChanged(areaHash, areaDetails?.Name ?? "Unknown Area", inTownOrHideout);
                this.lastAreaHash = areaHash;
            }

            // Run updates
            if (this.isRunActive && this.currentRun != null && !this.isRunPaused)
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
                this.DrawOverlay(inTownOrHideout, areaDetails?.Name ?? "None");
            }
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
                SessionId = this.Settings.CurrentSessionId,
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
            }

            this.isRunActive = false;
            this.isRunPaused = false;
            this.currentRun = null;
            this.currentLoot.Clear();
            this.currentTotalChaos = 0f;
        }

        private void DrawOverlay(bool inTownOrHideout, string currentZoneName)
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

                // Status line
                string statusText = this.isRunActive
                    ? (this.isRunPaused ? "⏸ PAUSED" : "▶ FARMING")
                    : "IDLE";
                Vector4 statusColor = this.isRunActive
                    ? (this.isRunPaused ? new Vector4(1f, 0.8f, 0.2f, 1f) : new Vector4(0.3f, 1f, 0.3f, 1f))
                    : new Vector4(0.7f, 0.7f, 0.7f, 1f);

                ImGui.TextColored(statusColor, statusText);
                ImGui.SameLine();
                ImGui.TextDisabled($"| Session #{this.Settings.CurrentSessionId}");

                // Current Map Info
                string mapTitle = this.currentRun != null ? this.currentRun.MapName : currentZoneName;
                int duration = this.currentRun?.DurationSec ?? 0;
                int min = duration / 60;
                int sec = duration % 60;
                ImGui.Text($"Map: {mapTitle} ({min:D2}:{sec:D2})");

                ImGui.Separator();

                // Value Display
                float totalChaos = this.currentTotalChaos;
                string valueStr = this.FormatCurrency(totalChaos);
                ImGui.TextColored(new Vector4(1f, 0.84f, 0.2f, 1f), $"Loot: {valueStr}");

                if (this.Settings.ShowProfitPerHour && duration > 5)
                {
                    float chaosPerHour = totalChaos * 3600f / duration;
                    string rateStr = this.FormatCurrency(chaosPerHour);
                    ImGui.TextDisabled($"Rate: {rateStr}/hr");
                }

                // Kills
                if (this.Settings.ShowKills)
                {
                    int kills = this.killTracker.KillsTotal;
                    ImGui.Text($"Kills: {kills} (M:{this.killTracker.KillsMagic} R:{this.killTracker.KillsRare} U:{this.killTracker.KillsUnique})");
                }

                // Action buttons
                if (this.isRunActive)
                {
                    if (ImGui.SmallButton("Finish Run"))
                    {
                        this.FinalizeRun();
                    }
                    ImGui.SameLine();
                    if (ImGui.SmallButton(this.isRunPaused ? "Resume" : "Pause"))
                    {
                        this.isRunPaused = !this.isRunPaused;
                    }
                }
                else
                {
                    if (ImGui.SmallButton("Start Run"))
                    {
                        this.StartNewRun(this.lastAreaHash, currentZoneName);
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
            if (ImGui.Checkbox("Show Kills Count", ref showKills))
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

            ImGui.Text($"Current Session: #{this.Settings.CurrentSessionId}");
            ImGui.SameLine();
            if (ImGui.Button("Start New Session"))
            {
                this.Settings.CurrentSessionId++;
                this.SaveSettings();
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

            if (ImGui.Button("Clear History"))
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
    }
}
