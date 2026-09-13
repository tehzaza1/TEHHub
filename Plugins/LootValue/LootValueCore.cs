// <copyright file="LootValueCore.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace LootValue
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Numerics;
    using System.Reflection;
    using System.Runtime.InteropServices;
    using System.Text.Json;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;
    using Coroutine;
    using TEHhub;
    using TEHhub.CoroutineEvents;
    using TEHhub.Plugin;
    using TEHhub.RemoteEnums;
    using TEHhub.RemoteObjects.Components;
    using TEHhub.RemoteObjects.States.InGameStateObjects;
    using TEHhub.Offsets.Natives;
    using TEHhub.Offsets.Objects.UiElement;
    using ImGuiNET;

    /// <summary>
    ///     LootValue plugin — prices ground, stash, inventory, Ritual reward, and Currency Exchange items.
    ///     Unidentified uniques are revealed by name via their icon art.
    /// </summary>
    public sealed partial class LootValueCore : PCore<LootValueSettings>
    {
        /// <inheritdoc/>
        public override IReadOnlyCollection<string> ConflictsWith => new[] { "RitualHelper" };

        /// <inheritdoc/>
        public override int ConflictPriority => 100;

        private const string ItemPathPrefix = "Metadata/Items";
        private const string PreviousDefaultLeague = "Runes of Aldur";
        private const string CurrentDefaultLeague = "Forbidden Rites";
        private const int CurrentLeagueMigrationVersion = 1;
        // PoE 0.5.5 shifted the UiElement tail (including the slot item pointer) by -0x18.
        private const int UiElementItemAddressOffset = 0x4E0;
        private static readonly int[] CurrencyExchangeRootPath = { 114, 20, 6 };
        private static readonly int[] RitualRewardGridPath = { 76, 13 };

        private List<LootLabel> cachedLabels = new();
        private readonly Dictionary<uint, Tracked> trackWorld = new();
        private DateTime nextRecomputeUtc = DateTime.MinValue;

        private readonly List<string> diagSamples = new();
        private string diagSummary = string.Empty;
        private DateTime nextDiagUtc = DateTime.MinValue;

        // Loot-tag mode (anchors chips to the game's loot labels via a throttled UI-tree scan).
        // PoE 0.5.5 moved inline text-element wstrings by -0x30: UiElementBase lost 0x18 and
        // the derived text class lost another 0x18. Verified live by RunecraftHelper as well.
        private const int UiElementTextOffset = 0x360;
        private List<TagChip> cachedTagChips = new();
        private readonly Dictionary<IntPtr, Tracked> trackTag = new();
        private DateTime nextTagScanUtc = DateTime.MinValue;
        private readonly HashSet<string> groundTagNames = new(StringComparer.OrdinalIgnoreCase);
        private SlotScanReport leftSlotReport = new(IntPtr.Zero);
        private SlotScanReport rightSlotReport = new(IntPtr.Zero);
        private SlotScanReport ritualSlotReport = new(IntPtr.Zero);
        private List<SlotInfo> cachedLeftSlots = new();
        private List<SlotInfo> cachedRightSlots = new();
        private List<SlotInfo> cachedRitualSlots = new();
        private IntPtr cachedLeftPanelAddress;
        private IntPtr cachedRightPanelAddress;
        private IntPtr cachedRitualGridAddress;
        private DateTime nextSlotScanUtc = DateTime.MinValue;
        private bool isSlotScanRunning = false;
        private List<ExchangePriceLabel> cachedExchangeLabels = new();
        private DateTime nextExchangeScanUtc = DateTime.MinValue;
        // Currency Exchange moved several times between game patches.  Keep a discovered list
        // address while it remains valid and only search the UI tree occasionally when it is not.
        private IntPtr cachedExchangeListAddress;
        private DateTime nextExchangeDiscoveryUtc = DateTime.MinValue;
        private string exchangeDiagnostic = "Currency Exchange: waiting for scan.";
        private bool isRecomputeRunning = false;
        private bool isTagScanRunning = false;
        private bool isAlertScanRunning = false;
        private bool isExchangeScanRunning = false;
        private bool isMonolithScanRunning = false;
        private bool isRuneshapeUiScanRunning = false;
        private List<RuneshapeRowPriceLabel> cachedRuneshapeRows = new();
        private DateTime nextRuneshapeUiScanUtc = DateTime.MinValue;
        private DateTime nextScrollRefreshUtc = DateTime.MinValue;
        private ScrollFrameState cachedLeftScroll = ScrollFrameState.None;
        private ScrollFrameState cachedRightScroll = ScrollFrameState.None;
        private ScrollFrameState cachedRitualScroll = ScrollFrameState.None;

        // Valuable Drop Alerts
        [LibraryImport("winmm.dll", EntryPoint = "PlaySound", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool WinMmPlaySound(byte[]? ptrToSound, IntPtr hmod, uint fdwSound);

        private const uint SndAsync = 0x0001;
        private const uint SndMemory = 0x0004;
        private const uint SndNoDefault = 0x0002;

        private static byte[]? rawWavBytes;
        private static byte[]? scaledWavBytes;
        private static int cachedVolumePercent = -1;

        private ActiveCoroutine? onAreaChangeCoroutine;
        private readonly HashSet<uint> alertedEntityIds = new();
        private List<ActiveDropAlert> activeAlertDrops = new();
        private readonly List<DropAlertBanner> activeAlertBanners = new();
        private DateTime nextAlertScanUtc = DateTime.MinValue;
        private DateTime lastAlertSoundUtc = DateTime.MinValue;

        private string SettingPathname => this.PluginConfigPath("settings.txt");

        /// <inheritdoc/>
        public override void OnEnable(bool isGameOpened)
        {
            var shouldMigrateStashSettings = true;
            var shouldSaveSettings = false;
            if (File.Exists(this.SettingPathname))
            {
                try
                {
                    var settingsJson = File.ReadAllText(this.SettingPathname);
                    using var settingsDocument = JsonDocument.Parse(settingsJson);
                    shouldMigrateStashSettings = !settingsDocument.RootElement.TryGetProperty(
                        nameof(LootValueSettings.ShowStashOverlay), out _);
                    this.Settings = JsonSerializer.Deserialize(
                        settingsJson,
                        LootValueJsonContext.Default.LootValueSettings) ?? new LootValueSettings();

                    // Migrate legacy single-currency values if newly added per-currency values are default
                    if (this.Settings.DisplayCurrency == 2 && this.Settings.HighlightMinChaos == 10f && this.Settings.HighlightMinEx > 0f)
                    {
                        this.Settings.HighlightMinChaos = Math.Clamp(this.Settings.HighlightMinEx, 1f, 100f);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[LootValue] Failed to load settings: {ex.Message}");
                    this.Settings = new LootValueSettings();
                }
            }

            shouldSaveSettings |= this.TryMigrateLeagueDefault();
            shouldSaveSettings |= shouldMigrateStashSettings && this.TryMigrateStashValueSettings();
            if (shouldSaveSettings)
            {
                this.SaveSettings();
            }

            PoeNinjaPriceFetcher.Configure(this.Settings.PriceSource, this.Settings.League ?? string.Empty, this.Settings.RefreshIntervalMin);
            PoeNinjaPriceFetcher.Initialize(this.DllDirectory);
            RuneshapeCatalog.Reload(this.DllDirectory);

            this.onAreaChangeCoroutine = CoroutineHandler.Start(this.OnAreaChange());
        }

        private bool TryMigrateLeagueDefault()
        {
            if (this.Settings.LeagueMigrationVersion.HasValue)
            {
                return false;
            }

            if (string.Equals(this.Settings.League, PreviousDefaultLeague, StringComparison.OrdinalIgnoreCase))
            {
                this.Settings.League = CurrentDefaultLeague;
            }

            this.Settings.LeagueMigrationVersion = CurrentLeagueMigrationVersion;
            return true;
        }

        private bool TryMigrateStashValueSettings()
        {
            var pluginsDirectory = Directory.GetParent(this.DllDirectory)?.FullName;
            if (pluginsDirectory == null) return false;

            foreach (var pluginName in new[] { "StashValueByZx0", "StashValue" })
            {
                var legacyPath = Path.Join(pluginsDirectory, pluginName, "config", "settings.txt");
                if (!File.Exists(legacyPath)) continue;

                try
                {
                    using var legacyDocument = JsonDocument.Parse(File.ReadAllText(legacyPath));
                    var legacy = legacyDocument.RootElement;
                    if (legacy.TryGetProperty("ShowOverlay", out var showOverlay) && showOverlay.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        this.Settings.ShowStashOverlay = showOverlay.GetBoolean();
                    if (legacy.TryGetProperty("ShowInventoryOverlay", out var showInventoryOverlay) && showInventoryOverlay.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        this.Settings.ShowInventoryOverlay = showInventoryOverlay.GetBoolean();
                    if (legacy.TryGetProperty("HidePriceOnHover", out var hidePriceOnHover) && hidePriceOnHover.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        this.Settings.HideSlotPricesOnHover = hidePriceOnHover.GetBoolean();
                    if (legacy.TryGetProperty("ShowDebugInfo", out var showDebugInfo) && showDebugInfo.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        this.Settings.ShowSlotDebugInfo = showDebugInfo.GetBoolean();
                    if (legacy.TryGetProperty("PriceFontScale", out var priceFontScale) && priceFontScale.TryGetSingle(out var fontScale))
                        this.Settings.SlotFontScale = fontScale;
                    if (legacy.TryGetProperty("PriceOffsetX", out var priceOffsetX) && priceOffsetX.TryGetSingle(out var offsetX))
                        this.Settings.SlotOffsetX = offsetX;
                    if (legacy.TryGetProperty("PriceOffsetY", out var priceOffsetY) && priceOffsetY.TryGetSingle(out var offsetY))
                        this.Settings.SlotOffsetY = offsetY;
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[LootValue] Failed to migrate {pluginName} settings: {ex.Message}");
                }
            }

            return false;
        }

        /// <inheritdoc/>
        public override void OnDisable()
        {
            this.onAreaChangeCoroutine?.Cancel();
            this.onAreaChangeCoroutine = null;
            this.alertedEntityIds.Clear();
            this.activeAlertDrops.Clear();
            this.activeAlertBanners.Clear();

            this.cachedLabels.Clear();
            this.cachedTagChips.Clear();
            this.trackWorld.Clear();
            this.trackTag.Clear();
            this.nextRecomputeUtc = DateTime.MinValue;
            this.nextTagScanUtc = DateTime.MinValue;
            this.groundTagNames.Clear();
            this.cachedLeftSlots.Clear();
            this.cachedRightSlots.Clear();
            this.cachedRitualSlots.Clear();
            this.cachedLeftPanelAddress = IntPtr.Zero;
            this.cachedRightPanelAddress = IntPtr.Zero;
            this.cachedRitualGridAddress = IntPtr.Zero;
            this.cachedExchangeLabels.Clear();
            this.nextExchangeScanUtc = DateTime.MinValue;
            this.cachedExchangeListAddress = IntPtr.Zero;
            this.nextExchangeDiscoveryUtc = DateTime.MinValue;
            this.exchangeDiagnostic = "Currency Exchange: waiting for scan.";
            this.cachedMonoliths.Clear();
            this.nextMonolithScanUtc = DateTime.MinValue;
            this.cachedRuneshapeRows.Clear();
            this.nextRuneshapeUiScanUtc = DateTime.MinValue;
            this.isSlotScanRunning = false;
            this.isRecomputeRunning = false;
            this.isTagScanRunning = false;
            this.isAlertScanRunning = false;
            this.isExchangeScanRunning = false;
            this.isMonolithScanRunning = false;
            this.isRuneshapeUiScanRunning = false;
            this.cachedLeftScroll = ScrollFrameState.None;
            this.cachedRightScroll = ScrollFrameState.None;
            this.cachedRitualScroll = ScrollFrameState.None;
        }

        private IEnumerable<Wait> OnAreaChange()
        {
            while (true)
            {
                yield return new Wait(RemoteEvents.AreaChanged);
                this.alertedEntityIds.Clear();
                this.activeAlertBanners.Clear();
                this.activeAlertDrops.Clear();
            }
        }

        private void PrepareWavBuffer()
        {
            try
            {
                var soundPath = Path.Combine(this.DllDirectory, "default.wav");
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
                Console.WriteLine($"[LootValue] Error preparing WAV buffer: {ex.Message}");
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
                Console.WriteLine($"[LootValue] Failed to play alert sound: {ex.Message}");
            }
        }

        private float CurrentHighlightMin
        {
            get => this.Settings.DisplayCurrency switch
            {
                0 => this.Settings.HighlightMinDiv,
                2 => this.Settings.HighlightMinChaos,
                _ => this.Settings.HighlightMinEx
            };
            set
            {
                switch (this.Settings.DisplayCurrency)
                {
                    case 0: this.Settings.HighlightMinDiv = value; break;
                    case 2: this.Settings.HighlightMinChaos = value; break;
                    default: this.Settings.HighlightMinEx = value; break;
                }
            }
        }

        /// <inheritdoc/>
        public override void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(this.SettingPathname) ?? string.Empty);
                File.WriteAllText(
                    this.SettingPathname,
                    JsonSerializer.Serialize(this.Settings, LootValueJsonContext.Default.LootValueSettings));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LootValue] Failed to save settings: {ex.Message}");
            }
        }

        /// <inheritdoc/>
        public override void DrawSettings()
        {
            ImGui.Checkbox(this.PluginText.Label("settings.show_overlay", "Show value over ground items", "LootValueShowOverlay"), ref this.Settings.ShowOverlay);
            ImGui.Checkbox(this.PluginText.Label("settings.anchor_to_loot_tags", "Anchor to loot labels (no overlap when items pile up)", "LootValueAnchorToLootTags"), ref this.Settings.AnchorToLootTags);
            ImGui.Checkbox(this.PluginText.Label("settings.show_stash_overlay", "Show value over stash items", "LootValueShowStashOverlay"), ref this.Settings.ShowStashOverlay);
            ImGui.Checkbox(this.PluginText.Label("settings.show_inventory_overlay", "Show value over inventory items", "LootValueShowInventoryOverlay"), ref this.Settings.ShowInventoryOverlay);
            ImGui.Checkbox(this.PluginText.Label("settings.show_ritual_overlay", "Show value over Ritual rewards", "LootValueShowRitualOverlay"), ref this.Settings.ShowRitualOverlay);
            ImGui.Checkbox(this.PluginText.Label("settings.show_currency_exchange_overlay", "Show owned-stack values in Currency Exchange", "LootValueShowCurrencyExchangeOverlay"), ref this.Settings.ShowCurrencyExchangeOverlay);
            ImGui.Checkbox(this.PluginText.Label("settings.hide_when_game_unfocused", "Hide values when game is not focused", "LootValueHideWhenGameUnfocused"), ref this.Settings.HideWhenGameInBackground);
            ImGui.Checkbox(this.PluginText.Label("settings.hide_slot_prices_on_hover", "Hide item-panel values while hovering an item", "LootValueHideSlotPricesOnHover"), ref this.Settings.HideSlotPricesOnHover);
            ImGui.Checkbox(this.PluginText.Label("settings.reveal_unidentified_uniques", "Reveal unidentified uniques (by art)", "LootValueRevealUnidentifiedUniques"), ref this.Settings.RevealUnidentifiedUniques);
            ImGui.Checkbox(this.PluginText.Label("settings.diagnostics_window", "Diagnostics window", "LootValueDiagnosticsWindow"), ref this.Settings.DiagnosticsMode);
            ImGui.Checkbox(this.PluginText.Label("settings.slot_diagnostics", "Item-panel slot diagnostics", "LootValueSlotDiagnostics"), ref this.Settings.ShowSlotDebugInfo);

            ImGui.Separator();
            ImGui.Text(this.PluginText.T("section.display", "Display"));
            if (ImGui.RadioButton(this.PluginText.Label("currency.chaos", "Chaos", "LootValueCurrencyChaos"), this.Settings.DisplayCurrency == 2)) this.Settings.DisplayCurrency = 2;
            ImGui.SameLine();
            if (ImGui.RadioButton(this.PluginText.Label("currency.exalted", "Exalted", "LootValueCurrencyExalted"), this.Settings.DisplayCurrency == 1)) this.Settings.DisplayCurrency = 1;
            ImGui.SameLine();
            if (ImGui.RadioButton(this.PluginText.Label("currency.divine", "Divine", "LootValueCurrencyDivine"), this.Settings.DisplayCurrency == 0)) this.Settings.DisplayCurrency = 0;

            var curSymbol = this.Settings.DisplayCurrency switch
            {
                0 => "div",
                2 => "c",
                _ => "ex"
            };

            var (minHighlight, maxHighlight) = this.Settings.DisplayCurrency switch
            {
                0 => (0f, 100f),
                2 => (1f, 100f),
                _ => (50f, 1000f)
            };

            var hlFormat = this.Settings.DisplayCurrency switch
            {
                0 => "%.1f",
                2 => "%.0f",
                _ => "%.0f"
            };

            var curHlVal = this.CurrentHighlightMin;
            if (ImGui.SliderFloat($"Highlight from ({curSymbol})###LootValueHighlightFrom", ref curHlVal, minHighlight, maxHighlight, hlFormat))
            {
                this.CurrentHighlightMin = curHlVal;
            }
            ImGui.SliderFloat(this.PluginText.Label("settings.font_size", "Font size", "LootValueFontSize"), ref this.Settings.FontSize, 8f, 48f, "%.0f");
            ImGui.SliderFloat(this.PluginText.Label("settings.highlight_font_size", "Highlight font size", "LootValueHighlightFontSize"), ref this.Settings.HighlightFontSize, 8f, 64f, "%.0f");
            ImGui.Checkbox(this.PluginText.Label("settings.highlight_bold", "Highlight bold", "LootValueHighlightBold"), ref this.Settings.HighlightBold);
            ImGui.SliderFloat(this.PluginText.Label("settings.vertical_offset", "Vertical offset", "LootValueVerticalOffset"), ref this.Settings.OffsetY, -50f, 50f);
            ImGui.SliderFloat(this.PluginText.Label("settings.slot_font_scale", "Stash/inventory font scale", "LootValueSlotFontScale"), ref this.Settings.SlotFontScale, 0.5f, 2f, "%.2f");
            ImGui.SliderFloat(this.PluginText.Label("settings.slot_horizontal_offset", "Stash/inventory horizontal offset", "LootValueSlotOffsetX"), ref this.Settings.SlotOffsetX, -50f, 50f);
            ImGui.SliderFloat(this.PluginText.Label("settings.slot_vertical_offset", "Stash/inventory vertical offset", "LootValueSlotOffsetY"), ref this.Settings.SlotOffsetY, -50f, 50f);
            ImGui.Checkbox(this.PluginText.Label("settings.smooth_label_motion", "Smooth label motion (velocity tracking)", "LootValueSmoothLabelMotion"), ref this.Settings.InterpolatePosition);
            if (this.Settings.InterpolatePosition)
            {
                ImGui.SliderInt(this.PluginText.Label("settings.jitter_filter", "Jitter filter (lower=stronger, no lag)", "LootValueJitterFilter"), ref this.Settings.InterpolationRate, 1, 1000);
            }

            ImGui.SliderInt(this.PluginText.Label("settings.rescan_interval", "Rescan interval (ms)", "LootValueRescanInterval"), ref this.Settings.RescanIntervalMs, 16, 1000);
            ImGui.TextDisabled(this.PluginText.T("settings.rescan_interval.tooltip", "Positions redraw every frame; rescan only re-detects items/prices."));
            ImGui.SliderInt(this.PluginText.Label("settings.slot_rescan_interval", "Stash/inventory rescan interval (ms)", "LootValueSlotRescanInterval"), ref this.Settings.SlotRescanIntervalMs, 100, 2000);
            ImGui.TextDisabled(this.PluginText.T("settings.slot_rescan_interval.tooltip", "Cached slot values draw every frame; panel traversal and pricing run at this interval."));

            ImGui.ColorEdit4(this.PluginText.Label("settings.text_color", "Text color", "LootValueTextColor"), ref this.Settings.TextColor);
            ImGui.ColorEdit4(this.PluginText.Label("settings.highlight_color", "Highlight text color", "LootValueHighlightColor"), ref this.Settings.HighlightColor);
            ImGui.ColorEdit4(this.PluginText.Label("settings.highlight_bg_color", "Highlight background color", "LootValueHighlightBgColor"), ref this.Settings.HighlightBackgroundColor);

            ImGui.Separator();
            ImGui.Text(this.PluginText.T("section.drop_alerts", "Valuable Drop Alerts"));
            ImGui.Checkbox(this.PluginText.Label("settings.enable_alert_sound", "Play alert sound", "LootValueEnableAlertSound"), ref this.Settings.EnableAlertSound);
            ImGui.SameLine();
            if (ImGui.Button(this.PluginText.Label("button.test_sound", "Test Sound", "LootValueTestSound")))
            {
                this.PlayAlertSound(force: true);
            }

            ImGui.SliderInt(this.PluginText.Label("settings.alert_volume", "Alert volume (%)", "LootValueAlertVolume"), ref this.Settings.AlertVolumePercent, 0, 100);
            ImGui.Checkbox(this.PluginText.Label("settings.enable_alert_banner", "Show alert banner (screen top)", "LootValueEnableAlertBanner"), ref this.Settings.EnableAlertBanner);
            ImGui.Checkbox(this.PluginText.Label("settings.enable_alert_beam", "Show beam & pointer to drop", "LootValueEnableAlertBeam"), ref this.Settings.EnableAlertBeam);

            var curName = this.Settings.DisplayCurrency switch
            {
                0 => "Divine",
                1 => "Exalted",
                _ => "Chaos"
            };
            ImGui.SliderFloat(this.PluginText.Label("settings.alert_threshold", $"Alert threshold ({curName})", "LootValueAlertThreshold"), ref this.Settings.AlertMinDisplayValue, 0.1f, 100f, $"%.1f {curName}");
            ImGui.SliderFloat(this.PluginText.Label("settings.banner_duration", "Banner duration (sec)", "LootValueBannerDuration"), ref this.Settings.AlertBannerDurationSec, 2f, 20f, "%.1f s");

            ImGui.Separator();
            ImGui.Text(this.PluginText.T("section.expedition", "PoE 2 Expedition (Runeshape Monolith & Crafting)"));
            ImGui.Checkbox(this.PluginText.Label("settings.expedition_world_overlay", "Show 3D World Badge Above Monoliths", "LootValueExpeditionWorld"), ref this.Settings.EnableExpeditionWorldOverlay);
            if (this.Settings.EnableExpeditionWorldOverlay)
            {
                ImGui.Indent();
                ImGui.SliderFloat(this.PluginText.Label("settings.expedition_badge_offset_x", "Monolith badge horizontal offset (X: Left/Right)", "LootValueExpeditionBadgeOffsetX"), ref this.Settings.ExpeditionBadgeOffsetX, -600f, 600f);
                ImGui.SliderFloat(this.PluginText.Label("settings.expedition_badge_offset_y", "Monolith badge vertical offset (Y: Up/Down)", "LootValueExpeditionBadgeOffsetY"), ref this.Settings.ExpeditionBadgeOffsetY, -600f, 600f);
                ImGui.Unindent();
            }

            ImGui.Checkbox(this.PluginText.Label("settings.runeshape_ui_prices", "Show Prices in Runeshape UI Panel", "LootValueRuneshapeUi"), ref this.Settings.EnableRuneshapeUiPrices);
            if (this.Settings.EnableRuneshapeUiPrices)
            {
                ImGui.Indent();
                ImGui.SliderFloat(this.PluginText.Label("settings.runeshape_ui_offset_x", "Runeshape UI horizontal offset (X: Left/Right)", "LootValueRuneshapeUiOffsetX"), ref this.Settings.RuneshapeUiOffsetX, -300f, 300f);
                ImGui.SliderFloat(this.PluginText.Label("settings.runeshape_ui_offset_y", "Runeshape UI vertical offset (Y: Up/Down)", "LootValueRuneshapeUiOffsetY"), ref this.Settings.RuneshapeUiOffsetY, -300f, 300f);
                ImGui.Unindent();
            }

            ImGui.Checkbox(this.PluginText.Label("settings.hide_completed_monoliths", "Hide Completed Monoliths", "LootValueHideCompletedMonoliths"), ref this.Settings.HideCompletedMonoliths);

            ImGui.Separator();
            ImGui.Text(this.PluginText.T("section.price_source", "Price source: poe.ninja (100%)"));

            var availableLeagues = PoeNinjaPriceFetcher.AvailableLeagues;
            var currentLeague = this.Settings.League ?? string.Empty;
            var selectedIndex = availableLeagues.FindIndex(l => string.Equals(l, currentLeague, StringComparison.OrdinalIgnoreCase));
            if (selectedIndex < 0 && availableLeagues.Count > 0)
            {
                selectedIndex = 0;
                this.Settings.League = availableLeagues[0];
            }

            var preview = selectedIndex >= 0 && selectedIndex < availableLeagues.Count ? availableLeagues[selectedIndex] : currentLeague;
            if (ImGui.BeginCombo(this.PluginText.Label("settings.league", "League", "LootValueLeague"), preview))
            {
                for (var i = 0; i < availableLeagues.Count; i++)
                {
                    var isSelected = i == selectedIndex;
                    if (ImGui.Selectable(availableLeagues[i], isSelected))
                    {
                        this.Settings.League = availableLeagues[i];
                        PoeNinjaPriceFetcher.Configure(this.Settings.PriceSource, this.Settings.League, this.Settings.RefreshIntervalMin);
                        PoeNinjaPriceFetcher.ForceRefresh(this.DllDirectory, ignoreCooldown: true);
                    }

                    if (isSelected)
                    {
                        ImGui.SetItemDefaultFocus();
                    }
                }

                ImGui.EndCombo();
            }

            ImGui.SliderInt(this.PluginText.Label("settings.refresh_interval", "Refresh interval (min)", "LootValueRefreshInterval"), ref this.Settings.RefreshIntervalMin, 30, 60);
            if (ImGui.Button(this.PluginText.Label("button.refresh_prices_now", "Refresh prices now", "LootValueRefreshPricesNow")))
            {
                PoeNinjaPriceFetcher.Configure(this.Settings.PriceSource, this.Settings.League ?? string.Empty, this.Settings.RefreshIntervalMin);
                PoeNinjaPriceFetcher.ForceRefresh(this.DllDirectory, ignoreCooldown: true);
            }

            ImGui.SameLine();
            if (PoeNinjaPriceFetcher.IsFetching)
            {
                var status = PoeNinjaPriceFetcher.IsFailingOver
                    ? this.PluginText.F(
                        "status.switching_provider",
                        "Switching to {0}...",
                        PoeNinjaPriceFetcher.ActiveSourceName)
                    : this.PluginText.F(
                        "status.loading_provider",
                        "Loading from {0}...",
                        PoeNinjaPriceFetcher.ActiveSourceName);
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.2f, 1f), status);
            }
            else if (PoeNinjaPriceFetcher.LastFetchUtc > DateTime.MinValue)
            {
                var mins = Math.Max(0, (int)(DateTime.UtcNow - PoeNinjaPriceFetcher.LastFetchUtc).TotalMinutes);
                ImGui.TextColored(new Vector4(0.5f, 0.8f, 0.5f, 1f), this.PluginText.F("status.loaded_items", "{0} items | {1} min ago", PoeNinjaPriceFetcher.LoadedItemCount, mins));
            }
        }

        /// <inheritdoc/>
        public override void DrawUI()
        {
            if (Core.States.GameCurrentState != GameStateTypes.InGameState) return;

            PoeNinjaPriceFetcher.Configure(this.Settings.PriceSource, this.Settings.League ?? string.Empty, this.Settings.RefreshIntervalMin);
            PoeNinjaPriceFetcher.RefreshIfNeeded();

            if (this.Settings.DiagnosticsMode)
            {
                this.RunDiagnostics();
                this.DrawDiagnosticsWindow();
            }

            if (this.Settings.HideWhenGameInBackground && !Core.Process.Foreground)
            {
                return;
            }

            var now = DateTime.UtcNow;

            if (this.Settings.EnableAlertSound || this.Settings.EnableAlertBanner || this.Settings.EnableAlertBeam)
            {
                if (now >= this.nextAlertScanUtc && !this.isAlertScanRunning)
                {
                    this.nextAlertScanUtc = now.AddMilliseconds(Math.Max(50, this.Settings.RescanIntervalMs));
                    this.isAlertScanRunning = true;
                    Task.Run(() =>
                    {
                        try
                        {
                            this.ProcessDropAlerts();
                        }
                        catch
                        {
                        }
                        finally
                        {
                            this.isAlertScanRunning = false;
                        }
                    });
                }

                this.DrawDropAlerts();
            }

            if (this.Settings.ShowOverlay && this.Settings.AnchorToLootTags)
            {
                if (this.EnsureReflection())
                {
                    if (now >= this.nextTagScanUtc && !this.isTagScanRunning)
                    {
                        this.nextTagScanUtc = now.AddMilliseconds(Math.Max(16, this.Settings.RescanIntervalMs));
                        this.isTagScanRunning = true;
                        Task.Run(() =>
                        {
                            try
                            {
                                this.ScanLootTags();
                            }
                            catch
                            {
                            }
                            finally
                            {
                                this.isTagScanRunning = false;
                            }
                        });
                    }

                    this.DrawTagChips();
                }
            }
            else if (this.Settings.ShowOverlay)
            {
                if (now >= this.nextRecomputeUtc && !this.isRecomputeRunning)
                {
                    this.nextRecomputeUtc = now.AddMilliseconds(Math.Max(16, this.Settings.RescanIntervalMs));
                    this.isRecomputeRunning = true;
                    Task.Run(() =>
                    {
                        try
                        {
                            this.RecomputeLabels();
                        }
                        catch
                        {
                        }
                        finally
                        {
                            this.isRecomputeRunning = false;
                        }
                    });
                }

                this.DrawLabels();
            }

            if (this.Settings.ShowStashOverlay || this.Settings.ShowInventoryOverlay ||
                this.Settings.ShowRitualOverlay || this.Settings.ShowSlotDebugInfo)
            {
                this.DrawItemSlotValues();
            }

            if (this.Settings.ShowCurrencyExchangeOverlay)
            {
                this.DrawCurrencyExchangeValues();
            }

            if (this.Settings.EnableExpeditionWorldOverlay)
            {
                this.DrawExpeditionMonolithOverlay();
            }

            if (this.Settings.EnableRuneshapeUiPrices)
            {
                this.DrawRuneshapeUiOverlay();
            }
        }

        /// <summary>Re-reads + reprices every ground item; throttled. The drawn position is updated live each frame.</summary>
        private void RecomputeLabels()
        {
            var area = Core.States.InGameStateObject?.CurrentAreaInstance;
            if (area == null || area.AwakeEntities == null) return;

            var newLabels = new List<LootLabel>();
            foreach (var entity in area.AwakeEntities.Values)
            {
                // Ground drops are identified by the WorldItem component (path-independent — the wrapper
                // entity's own path is not "Metadata/Items"; that's the inner item).
                if (!entity.TryGetComponent<WorldItem>(out var worldItem) || worldItem.ItemEntityAddress == IntPtr.Zero) continue;
                if (!entity.TryGetComponent<Render>(out var render)) continue;

                var item = ReadFreshItem(worldItem.ItemEntityAddress);
                if (item == null) continue;

                if (!this.TryPriceItem(item, out var valueEx, out var label)) continue;

                var highlight = valueEx >= this.CurrentHighlightMin;
                var color = ImGui.ColorConvertFloat4ToU32(highlight ? this.Settings.HighlightColor : this.Settings.TextColor);
                newLabels.Add(new LootLabel(entity.Id, render, label, color, highlight));
            }

            this.cachedLabels = newLabels;

            // Drop tracker state for items no longer present (picked up / left the area).
            if (this.trackWorld.Count > 0)
            {
                var live = new HashSet<uint>(newLabels.Count);
                foreach (var l in newLabels) live.Add(l.EntityId);
                this.trackWorld.Keys.Where(k => !live.Contains(k)).ToList().ForEach(k => this.trackWorld.Remove(k));
            }
        }

        private void DrawLabels()
        {
            if (this.cachedLabels.Count == 0) return;

            var fg = ImGui.GetBackgroundDrawList();
            var font = ImGui.GetFont();
            var baseSize = ImGui.GetFontSize();
            var world = Core.States.InGameStateObject.CurrentWorldInstance;

            foreach (var label in this.cachedLabels)
            {
                // Anchor to the GROUND (stable TerrainHeight), not WorldPosition.Z — that Z is the item's
                // animated/bobbing model height, which makes the projected point oscillate. TerrainHeight is
                // constant for a stationary drop, so the only moving input becomes the camera (smoothed below).
                var screen = world.WorldToScreen(label.Render.WorldPosition, label.Render.TerrainHeight);
                if (screen == Vector2.Zero) continue;

                // Velocity-tracking filter: GH samples the camera at 120Hz from a 90Hz source, so the raw
                // projected point of a STATIC item beats ~1-2px along the path. Tracking screen velocity and
                // advancing by it each frame removes that without the lag a plain low-pass would add.
                if (this.Settings.InterpolatePosition)
                {
                    screen = Track(this.trackWorld, label.EntityId, screen, this.Settings.InterpolationRate);
                }

                var fontSize = label.Highlight ? this.Settings.HighlightFontSize : this.Settings.FontSize;
                var textWidth = ImGui.CalcTextSize(label.Text).X * (fontSize / baseSize);
                var pos = new Vector2(screen.X - (textWidth / 2f), screen.Y + this.Settings.OffsetY);
                this.DrawValueLabel(fg, font, baseSize, pos, label.Text, label.Color, label.Highlight);
            }
        }

        /// <summary>Draws one value label (background chip + shadowed text, faux-bold when highlighted)
        /// at the given top-left screen position. Shared by world-space and loot-tag modes.</summary>
        private void DrawValueLabel(ImDrawListPtr fg, ImFontPtr font, float baseSize, Vector2 pos, string text, uint color, bool highlight)
        {
            const uint shadow = 0xCC000000u;
            var fontSize = highlight ? this.Settings.HighlightFontSize : this.Settings.FontSize;
            var bold = highlight && this.Settings.HighlightBold;
            var textWidth = ImGui.CalcTextSize(text).X * (fontSize / baseSize);

            var bg = ImGui.ColorConvertFloat4ToU32(highlight ? this.Settings.HighlightBackgroundColor : this.Settings.BackgroundColor);
            fg.AddRectFilled(pos - new Vector2(3f, 1f), pos + new Vector2(textWidth + 3f, fontSize + 1f), bg, 3f);
            fg.AddText(font, fontSize, pos + new Vector2(1f, 1f), shadow, text);
            fg.AddText(font, fontSize, pos, color, text);
            if (bold)
            {
                // Faux-bold: redraw offset by 1px so the glyphs thicken.
                fg.AddText(font, fontSize, pos + new Vector2(1f, 0f), color, text);
            }
        }

        // ---- Loot-tag mode: anchor value chips to the game's loot labels (found via a UI-tree scan) ----

        private bool EnsureReflection()
        {
            return Core.Process?.Handle != null;
        }

        private string ReadUiElementText(IntPtr element)
        {
            try
            {
                var handle = Core.Process?.Handle;
                if (handle == null) return string.Empty;
                var ws = handle.ReadMemory<StdWString>(element + UiElementTextOffset);
                return handle.ReadStdWString(ws);
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>BFS the visible UI tree; any text element that prices as a loot drop becomes a chip
        /// anchored to that element. Throttled; the element's live rect is re-read each frame when drawing.</summary>
        private void ScanLootTags()
        {
            this.RefreshGroundTagNames();
            var gameUi = Core.States.InGameStateObject?.GameUi;
            if (gameUi == null) return;
            var root = gameUi.Address;
            var leftPanel = gameUi.LeftPanel.Address;
            var rightPanel = gameUi.RightPanel.Address;
            var handle = Core.Process?.Handle;
            if (root == IntPtr.Zero || handle == null) return;

            var newTagChips = new List<TagChip>();
            var queue = new Queue<IntPtr>();
            var visited = new HashSet<IntPtr>();
            queue.Enqueue(root);
            while (queue.Count > 0 && visited.Count < 20000)
            {
                var el = queue.Dequeue();
                if (el == IntPtr.Zero || !visited.Add(el)) continue;
                // Stash, inventory, vendor, and other large-panel text cannot be a ground loot label.
                // Do not traverse those potentially enormous subtrees when a panel is open.
                if (el != root && (el == leftPanel || el == rightPanel)) continue;
                if (!handle.TryReadMemory<UiElementBaseOffset>(el, out var off)) continue;
                if (el != root && !UiElementBaseFuncs.IsVisibleChecker(off.Flags)) continue;

                var kids = handle.ReadStdVector<IntPtr>(off.ChildrensPtr);
                if (kids.Length > 0)
                {
                    foreach (var k in kids) queue.Enqueue(k);
                }

                var text = this.ReadUiElementText(el);
                if (text.Length < 3) continue;
                var firstLine = text.Split('\n')[0].Trim();
                if (firstLine.Length < 3) continue;

                if (this.TryPriceTagText(firstLine, out var chipText, out var color, out var highlight))
                {
                    newTagChips.Add(new TagChip(el, chipText, color, highlight));
                }
            }

            this.cachedTagChips = newTagChips;

            // Drop tracker state for labels that are gone (item picked up / left the area).
            if (this.trackTag.Count > 0)
            {
                var live = new HashSet<IntPtr>(newTagChips.Count);
                foreach (var c in newTagChips) live.Add(c.ElementAddress);
                this.trackTag.Keys.Where(k => !live.Contains(k)).ToList().ForEach(k => this.trackTag.Remove(k));
            }
        }

        private bool TryPriceTagText(string text, out string chipText, out uint color, out bool highlight)
        {
            chipText = string.Empty;
            color = 0;
            highlight = false;

            var count = 1;
            var name = text;
            var m = Regex.Match(text, @"^(\d+)\s*x\s+(.+)$", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                int.TryParse(m.Groups[1].Value, out count);
                name = m.Groups[2].Value;
            }

            name = name.Trim();
            if (name.Length < 3) return false;
            if (!this.groundTagNames.Contains(name)) return false;

            var price = PoeNinjaPriceFetcher.GetPrice(name);
            if (price == null) return false;

            var priced = new PoeNinjaPrice { PriceChaos = price.PriceChaos * Math.Max(1, count) };
            var (disp, cur) = PoeNinjaPriceFetcher.GetDisplayPrice(priced, this.Settings.DisplayCurrency);
            if (disp <= 0) return false;

            chipText = FormatValue(disp, cur);
            highlight = disp >= this.CurrentHighlightMin;
            color = ImGui.ColorConvertFloat4ToU32(highlight ? this.Settings.HighlightColor : this.Settings.TextColor);
            return true;
        }

        private void DrawTagChips()
        {
            if (this.cachedTagChips.Count == 0) return;
            var handle = Core.Process?.Handle;
            if (handle == null) return;

            var fg = ImGui.GetBackgroundDrawList();
            var font = ImGui.GetFont();
            var baseSize = ImGui.GetFontSize();

            foreach (var chip in this.cachedTagChips)
            {
                // Pre-validate the address with a cheap raw read BEFORE constructing the UiElement: a real
                // UI element is self-referential (Self == its own address). If the element was freed since
                // the scan (e.g. item picked up), this no longer holds — skip it so CreateUiElement (which
                // would THROW on an invalid address) is never reached. try/catch remains as a backstop.
                if (!handle.TryReadMemory<UiElementBaseOffset>(chip.ElementAddress, out var off)) continue;
                if (off.Self != IntPtr.Zero && off.Self != chip.ElementAddress) continue; // exact inverse of the game's "not a Ui Element" guard
                if (!UiElementBaseFuncs.IsVisibleChecker(off.Flags)) continue;

                try
                {
                    if (!PluginUiElementReflection.TryGetAbsoluteRect(chip.ElementAddress, out var pos, out var size)) continue;
                    if (size.X <= 0f || size.Y <= 0f || pos == Vector2.Zero) continue;

                    var fontSize = chip.Highlight ? this.Settings.HighlightFontSize : this.Settings.FontSize;
                    var chipPos = new Vector2(pos.X + size.X + 6f, pos.Y + ((size.Y - fontSize) / 2f));

                    // Same velocity-tracking filter as world mode (the read rect beats against the game's
                    // update rate the same way), keyed by the label element.
                    if (this.Settings.InterpolatePosition)
                    {
                        chipPos = Track(this.trackTag, chip.ElementAddress, chipPos, this.Settings.InterpolationRate);
                    }

                    this.DrawValueLabel(fg, font, baseSize, chipPos, chip.Text, chip.Color, chip.Highlight);
                }
                catch
                {
                    // Stale/freed loot label — drop it; the next scan rebuilds from live elements.
                }
            }
        }

        /// <summary>
        /// Restricts loot-label matching to names backed by live ground-item entities. The game UI contains
        /// many unrelated text nodes (stash search, vendor listings, tooltips) whose text can also be priced;
        /// those must not be mistaken for ground labels.
        /// </summary>
        private void RefreshGroundTagNames()
        {
            this.groundTagNames.Clear();
            var area = Core.States.InGameStateObject.CurrentAreaInstance;
            foreach (var entity in area.AwakeEntities.Values)
            {
                if (!entity.TryGetComponent<WorldItem>(out var worldItem) || worldItem.ItemEntityAddress == IntPtr.Zero) continue;
                var item = ReadFreshItem(worldItem.ItemEntityAddress);
                if (item == null) continue;

                if (item.TryGetComponent<Base>(out var baseComp) && !string.IsNullOrWhiteSpace(baseComp.BaseItemName))
                {
                    this.groundTagNames.Add(baseComp.BaseItemName.Trim());
                }

                if (!item.TryGetComponent<Mods>(out var mods) || mods.Rarity != Rarity.Unique ||
                    !item.TryGetComponent<RenderItem>(out var renderItem)) continue;

                var artPath = renderItem.ResourcePath;
                if (!string.IsNullOrEmpty(artPath) &&
                    PoeNinjaPriceFetcher.TryResolveDisplayName(artPath, out var uniqueNameFromPath) &&
                    !PoeNinjaPriceFetcher.IsGenericLookupName(uniqueNameFromPath))
                {
                    this.groundTagNames.Add(uniqueNameFromPath.Trim());
                }
                else
                {
                    foreach (var key in ArtKeyVariants(ExtractArtBasename(artPath)))
                    {
                        if (PoeNinjaPriceFetcher.TryResolveDisplayName(key, out var uniqueName) &&
                            !PoeNinjaPriceFetcher.IsGenericLookupName(uniqueName))
                        {
                            this.groundTagNames.Add(uniqueName.Trim());
                        }
                    }
                }
            }
        }

        /// <summary>Draws cached owned-stack values in the Currency Exchange item browser.</summary>
        private void DrawCurrencyExchangeValues()
        {
            if (!this.EnsureReflection()) return;

            var now = DateTime.UtcNow;
            if (now >= this.nextExchangeScanUtc && !this.isExchangeScanRunning)
            {
                this.nextExchangeScanUtc = now.AddMilliseconds(Math.Clamp(this.Settings.SlotRescanIntervalMs, 100, 2000));
                this.isExchangeScanRunning = true;
                Task.Run(() =>
                {
                    try
                    {
                        this.ScanCurrencyExchange();
                    }
                    catch
                    {
                    }
                    finally
                    {
                        this.isExchangeScanRunning = false;
                    }
                });
            }

            if (this.cachedExchangeLabels.Count == 0) return;
            var foreground = ImGui.GetBackgroundDrawList();
            var font = ImGui.GetFont();
            var baseSize = ImGui.GetFontSize();
            foreach (var label in this.cachedExchangeLabels)
            {
                this.DrawValueLabel(
                    foreground,
                    font,
                    baseSize,
                    label.Position,
                    label.Text,
                    label.Color,
                    label.Highlight);
            }
        }

        private void ScanCurrencyExchange()
        {
            try
            {
            var gameUiAddress = Core.States.InGameStateObject?.GameUi.Address ?? IntPtr.Zero;
            var listAddress = this.ResolveCurrencyExchangeList(gameUiAddress);
            if (listAddress == IntPtr.Zero || !this.TryGetVisibleChildren(listAddress, out var categoryAddresses))
            {
                this.cachedExchangeLabels = new List<ExchangePriceLabel>();
                return;
            }

            if (!PluginUiElementReflection.TryGetAbsoluteRect(listAddress, out var viewportPosition, out var viewportSize))
            {
                this.cachedExchangeLabels = new List<ExchangePriceLabel>();
                return;
            }
            var viewportMax = viewportPosition + viewportSize;
            var newLabels = new List<ExchangePriceLabel>();

            foreach (var categoryAddress in categoryAddresses)
            {
                if (!this.TryGetVisibleChildren(categoryAddress, out var groupAddresses)) continue;
                foreach (var groupAddress in groupAddresses)
                {
                    if (!this.TryGetVisibleChildren(groupAddress, out var rowAddresses)) continue;

                    // Child 0 is the group headline. Every following populated child is an item row:
                    // [0] name, [1] icon container, [1][0] owned amount.
                    for (var rowIndex = 1; rowIndex < rowAddresses.Length; rowIndex++)
                    {
                        var rowAddress = rowAddresses[rowIndex];
                        if (!this.TryGetVisibleChildren(rowAddress, out var rowChildren) || rowChildren.Length <= 1) continue;
                        var nameAddress = rowChildren[0];
                        var iconAddress = rowChildren[1];
                        if (!this.TryGetVisibleChildren(iconAddress, out var iconChildren) || iconChildren.Length == 0) continue;

                        var name = this.ReadUiElementText(nameAddress).Split('\n')[0].Trim();
                        var amountText = this.ReadUiElementText(iconChildren[0]);
                        if (name.Length < 2 || !TryParseOwnedAmount(amountText, out var amount) || amount <= 0) continue;
                        if (!this.TryPriceNamedStack(name, amount, out var text, out var color, out var highlight)) continue;
                        if (!PluginUiElementReflection.TryGetAbsoluteRect(iconAddress, out var iconPosition, out var iconSize)) continue;

                        var center = iconPosition + (iconSize * 0.5f);
                        if (center.X < viewportPosition.X || center.X > viewportMax.X ||
                            center.Y < viewportPosition.Y || center.Y > viewportMax.Y) continue;

                        var fontSize = highlight ? this.Settings.HighlightFontSize : this.Settings.FontSize;
                        var labelPosition = new Vector2(
                            iconPosition.X + this.Settings.SlotOffsetX,
                            iconPosition.Y + iconSize.Y - fontSize + this.Settings.SlotOffsetY);
                        newLabels.Add(new ExchangePriceLabel(labelPosition, text, color, highlight));
                    }
                }
            }

            this.cachedExchangeLabels = newLabels;
            this.exchangeDiagnostic = $"Currency Exchange: list=0x{listAddress.ToInt64():X}, validatedRows={this.CountCurrencyExchangeRows(categoryAddresses)}+, priced={newLabels.Count}.";
            }
            finally
            {
#if DEBUG
                TEHhub.Ui.ToolDiagnostics.Publish("loot-value-exchange", new Dictionary<string, string>
                {
                    ["status"] = this.exchangeDiagnostic,
                    ["listAddress"] = this.cachedExchangeListAddress.ToString("X"),
                    ["pricedLabels"] = this.cachedExchangeLabels.Count.ToString(),
                    ["enabled"] = this.Settings.ShowCurrencyExchangeOverlay.ToString()
                });
#endif
            }
        }

        /// <summary>
        /// Resolves the exchange list using the previous fixed path first, then a bounded semantic
        /// search. The latter recognizes the stable category/group/row shape rather than a patch-fragile
        /// absolute GameUi child path.
        /// </summary>
        private IntPtr ResolveCurrencyExchangeList(IntPtr gameUiAddress)
        {
            if (gameUiAddress == IntPtr.Zero)
            {
                this.exchangeDiagnostic = "Currency Exchange: GameUi is unavailable.";
                return IntPtr.Zero;
            }

            if (this.IsCurrencyExchangeList(this.cachedExchangeListAddress))
            {
                return this.cachedExchangeListAddress;
            }

            var fixedRoot = this.ResolveUiPath(gameUiAddress, CurrencyExchangeRootPath);
            if (this.TryGetVisibleChildren(fixedRoot, out var fixedRootChildren) && fixedRootChildren.Length > 1 &&
                this.IsCurrencyExchangeList(fixedRootChildren[1]))
            {
                this.cachedExchangeListAddress = fixedRootChildren[1];
                return this.cachedExchangeListAddress;
            }

            var now = DateTime.UtcNow;
            if (now < this.nextExchangeDiscoveryUtc)
            {
                this.exchangeDiagnostic = "Currency Exchange: fixed path is stale; semantic search is throttled.";
                return IntPtr.Zero;
            }

            this.nextExchangeDiscoveryUtc = now.AddSeconds(2);
            this.cachedExchangeListAddress = this.FindCurrencyExchangeList(gameUiAddress);
            if (this.cachedExchangeListAddress == IntPtr.Zero)
            {
                this.exchangeDiagnostic = "Currency Exchange: no visible list with currency row structure found.";
            }

            return this.cachedExchangeListAddress;
        }

        private IntPtr FindCurrencyExchangeList(IntPtr gameUiAddress)
        {
            const int maxVisited = 6000;
            var queue = new Queue<IntPtr>();
            var visited = new HashSet<IntPtr>();
            queue.Enqueue(gameUiAddress);

            while (queue.Count > 0 && visited.Count < maxVisited)
            {
                var candidate = queue.Dequeue();
                if (candidate == IntPtr.Zero || !visited.Add(candidate)) continue;

                if (candidate != gameUiAddress && this.IsCurrencyExchangeList(candidate))
                {
                    return candidate;
                }

                if (!this.TryGetVisibleChildren(candidate, out var children)) continue;
                foreach (var child in children)
                {
                    queue.Enqueue(child);
                }
            }

            return IntPtr.Zero;
        }

        private bool IsCurrencyExchangeList(IntPtr candidate)
        {
            if (candidate == IntPtr.Zero || !this.TryGetVisibleChildren(candidate, out var categories) || categories.Length == 0)
            {
                return false;
            }

            // A populated exchange list has many currency rows. Requiring two parseable rows prevents
            // matching a regular item panel which happens to use a similar two-child layout.
            return this.CountCurrencyExchangeRows(categories) >= 2;
        }

        private int CountCurrencyExchangeRows(IReadOnlyList<IntPtr> categoryAddresses)
        {
            var count = 0;
            foreach (var categoryAddress in categoryAddresses)
            {
                if (!this.TryGetVisibleChildren(categoryAddress, out var groupAddresses)) continue;
                foreach (var groupAddress in groupAddresses)
                {
                    if (!this.TryGetVisibleChildren(groupAddress, out var rowAddresses)) continue;
                    for (var rowIndex = 1; rowIndex < rowAddresses.Length; rowIndex++)
                    {
                        if (!this.TryGetVisibleChildren(rowAddresses[rowIndex], out var rowChildren) || rowChildren.Length <= 1 ||
                            !this.TryGetVisibleChildren(rowChildren[1], out var iconChildren) || iconChildren.Length == 0) continue;

                        var name = this.ReadUiElementText(rowChildren[0]).Split('\n')[0].Trim();
                        if (name.Length < 2 || !TryParseOwnedAmount(this.ReadUiElementText(iconChildren[0]), out _)) continue;
                        if (++count >= 2) return count;
                    }
                }
            }

            return count;
        }

        private bool TryGetVisibleChildren(IntPtr address, out IntPtr[] children)
        {
            return this.TryGetChildren(address, requireVisible: true, out children);
        }

        private bool TryGetChildren(IntPtr address, bool requireVisible, out IntPtr[] children)
        {
            children = Array.Empty<IntPtr>();
            var handle = Core.Process?.Handle;
            if (address == IntPtr.Zero || handle == null ||
                !handle.TryReadMemory<UiElementBaseOffset>(address, out var offset) ||
                (requireVisible && !UiElementBaseFuncs.IsVisibleChecker(offset.Flags))) return false;

            children = handle.ReadStdVector<IntPtr>(offset.ChildrensPtr);
            return true;
        }

        private IntPtr ResolveUiPath(IntPtr root, IReadOnlyList<int> path)
        {
            var current = root;
            foreach (var childIndex in path)
            {
                if (!this.TryGetChildren(current, requireVisible: false, out var children) ||
                    childIndex < 0 || childIndex >= children.Length) return IntPtr.Zero;
                current = children[childIndex];
            }

            return current;
        }

        private static bool TryParseOwnedAmount(string text, out long amount)
        {
            amount = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var digits = Regex.Replace(text, @"[^0-9]", string.Empty);
            return digits.Length > 0 && long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out amount);
        }

        private bool TryPriceNamedStack(
            string itemName,
            long amount,
            out string text,
            out uint color,
            out bool highlight)
        {
            text = string.Empty;
            color = 0;
            highlight = false;
            var price = PoeNinjaPriceFetcher.GetPrice(itemName);
            if (price == null) return false;

            var priced = new PoeNinjaPrice { PriceChaos = price.PriceChaos * amount };
            var (displayValue, displayCurrency) = PoeNinjaPriceFetcher.GetDisplayPrice(priced, this.Settings.DisplayCurrency);
            if (displayValue <= 0) return false;

            text = FormatValue(displayValue, displayCurrency);
            highlight = displayValue >= this.CurrentHighlightMin;
            color = ImGui.ColorConvertFloat4ToU32(highlight ? this.Settings.HighlightColor : this.Settings.TextColor);
            return true;
        }

        /// <summary>Prices item slots in open stash, inventory, and Ritual reward panels.</summary>
        private void DrawItemSlotValues()
        {
            var gameUi = Core.States.InGameStateObject.GameUi;
            if (gameUi.Address == IntPtr.Zero || !this.EnsureReflection()) return;

            var scanLeft = this.Settings.ShowStashOverlay || this.Settings.ShowSlotDebugInfo;
            var scanRight = this.Settings.ShowInventoryOverlay || this.Settings.ShowSlotDebugInfo;
            var scanRitual = this.Settings.ShowRitualOverlay || this.Settings.ShowSlotDebugInfo;
            var leftAddress = scanLeft && gameUi.LeftPanel.IsVisible ? gameUi.LeftPanel.Address : IntPtr.Zero;
            var rightAddress = scanRight && gameUi.RightPanel.IsVisible ? gameUi.RightPanel.Address : IntPtr.Zero;
            var ritualAddress = scanRitual ? this.ResolveVisibleRitualRewardGrid(gameUi.Address) : IntPtr.Zero;

            if (leftAddress != this.cachedLeftPanelAddress || rightAddress != this.cachedRightPanelAddress ||
                ritualAddress != this.cachedRitualGridAddress)
            {
                this.cachedLeftPanelAddress = leftAddress;
                this.cachedRightPanelAddress = rightAddress;
                this.cachedRitualGridAddress = ritualAddress;
                this.nextSlotScanUtc = DateTime.MinValue;
            }

            var now = DateTime.UtcNow;
            if (now >= this.nextSlotScanUtc && !this.isSlotScanRunning)
            {
                this.nextSlotScanUtc = now.AddMilliseconds(Math.Clamp(this.Settings.SlotRescanIntervalMs, 100, 2000));
                this.isSlotScanRunning = true;

                var leftPos = gameUi.LeftPanel.Position;
                var leftSize = gameUi.LeftPanel.Size;
                var rightPos = gameUi.RightPanel.Position;
                var rightSize = gameUi.RightPanel.Size;
                var ritualPos = Vector2.Zero;
                var ritualSize = Vector2.Zero;
                var hasRitualRect = ritualAddress != IntPtr.Zero && PluginUiElementReflection.TryGetAbsoluteRect(ritualAddress, out ritualPos, out ritualSize);

                Task.Run(() =>
                {
                    try
                    {
                        List<SlotInfo> newLeftSlots;
                        SlotScanReport newLeftReport;
                        if (leftAddress != IntPtr.Zero)
                        {
                            newLeftSlots = this.ScanItemSlots(leftAddress, leftPos, leftSize, out newLeftReport);
                        }
                        else
                        {
                            newLeftSlots = new List<SlotInfo>();
                            newLeftReport = new SlotScanReport(IntPtr.Zero);
                        }

                        List<SlotInfo> newRightSlots;
                        SlotScanReport newRightReport;
                        if (rightAddress != IntPtr.Zero)
                        {
                            newRightSlots = this.ScanItemSlots(rightAddress, rightPos, rightSize, out newRightReport);
                        }
                        else
                        {
                            newRightSlots = new List<SlotInfo>();
                            newRightReport = new SlotScanReport(IntPtr.Zero);
                        }

                        List<SlotInfo> newRitualSlots;
                        SlotScanReport newRitualReport;
                        if (hasRitualRect)
                        {
                            newRitualSlots = this.ScanItemSlots(ritualAddress, ritualPos, ritualSize, out newRitualReport);
                        }
                        else
                        {
                            newRitualSlots = new List<SlotInfo>();
                            newRitualReport = new SlotScanReport(IntPtr.Zero);
                        }

                        this.cachedLeftSlots = newLeftSlots;
                        this.leftSlotReport = newLeftReport;
                        this.cachedRightSlots = newRightSlots;
                        this.rightSlotReport = newRightReport;
                        this.cachedRitualSlots = newRitualSlots;
                        this.ritualSlotReport = newRitualReport;
                    }
                    catch
                    {
                        // Background scan error tolerance
                    }
                    finally
                    {
                        this.isSlotScanRunning = false;
                    }
                });
            }

            if (now >= this.nextScrollRefreshUtc)
            {
                this.nextScrollRefreshUtc = now.AddMilliseconds(50);
                this.cachedLeftScroll = GetScrollFrameState(this.cachedLeftSlots);
                this.cachedRightScroll = GetScrollFrameState(this.cachedRightSlots);
                this.cachedRitualScroll = GetScrollFrameState(this.cachedRitualSlots);
            }

            var leftScroll = this.cachedLeftScroll;
            var rightScroll = this.cachedRightScroll;
            var ritualScroll = this.cachedRitualScroll;
            var hidePrices = this.Settings.HideSlotPricesOnHover &&
                             (IsAnySlotHovered(this.cachedLeftSlots, leftScroll) ||
                              IsAnySlotHovered(this.cachedRightSlots, rightScroll) ||
                              IsAnySlotHovered(this.cachedRitualSlots, ritualScroll));
            this.DrawItemSlots(this.cachedLeftSlots, this.Settings.ShowStashOverlay, hidePrices, leftScroll);
            this.DrawItemSlots(this.cachedRightSlots, this.Settings.ShowInventoryOverlay, hidePrices, rightScroll);
            this.DrawItemSlots(this.cachedRitualSlots, this.Settings.ShowRitualOverlay, hidePrices, ritualScroll);
            if (this.Settings.ShowSlotDebugInfo)
            {
                this.DrawSlotDiagnosticsWindow();
            }
        }

        private IntPtr ResolveVisibleRitualRewardGrid(IntPtr gameUiAddress)
        {
            var candidate = this.ResolveUiPath(gameUiAddress, RitualRewardGridPath);
            if (!this.TryGetVisibleChildren(candidate, out var tiles) || tiles.Length is < 1 or > 32)
            {
                return IntPtr.Zero;
            }

            // The fixed path is only accepted while at least one direct reward tile carries a valid
            // item pointer. This prevents a future UI-tree shift from pricing an unrelated panel.
            var handle = Core.Process?.Handle;
            if (handle == null) return IntPtr.Zero;

            foreach (var tile in tiles)
            {
                var itemAddress = handle.ReadMemory<IntPtr>(tile + UiElementItemAddressOffset);
                if (itemAddress != IntPtr.Zero &&
                    PluginUiElementReflection.TryValidateItemAddress(itemAddress, out _, out _))
                {
                    return candidate;
                }
            }

            return IntPtr.Zero;
        }

        private List<SlotInfo> ScanItemSlots(
            IntPtr panelAddress,
            Vector2 panelPosition,
            Vector2 panelSize,
            out SlotScanReport report)
        {
            report = new SlotScanReport(panelAddress);
            var candidatesByItem = new Dictionary<IntPtr, List<SlotElementCandidate>>();
            var handle = Core.Process?.Handle;
            if (panelAddress == IntPtr.Zero || handle == null) return new List<SlotInfo>();

            var queue = new Queue<(IntPtr Address, IntPtr Parent, ScrollBinding Scroll)>();
            var visited = new HashSet<IntPtr>();
            queue.Enqueue((panelAddress, IntPtr.Zero, default));

            while (queue.Count > 0 && visited.Count < 5000)
            {
                var (element, parent, scroll) = queue.Dequeue();
                if (element == IntPtr.Zero || !visited.Add(element)) continue;
                report.VisitedElements++;
                if (!handle.TryReadMemory<UiElementBaseOffset>(element, out var offset)) continue;
                if (!UiElementBaseFuncs.IsVisibleChecker(offset.Flags)) continue;

                var children = handle.ReadStdVector<IntPtr>(offset.ChildrensPtr);
                if (children.Length > 0)
                {
                    var hasScrollContainer = this.TryGetScrollContainer(
                        children,
                        out var scrollItemsAddress,
                        out var localScroll);
                    if (hasScrollContainer)
                    {
                        report.ScrollContainers++;
                        report.ScrollOffsetY = localScroll.ScanOffsetY;
                    }

                    foreach (var child in children)
                    {
                        if (hasScrollContainer && child == scrollItemsAddress)
                        {
                            queue.Enqueue((child, element, localScroll));
                        }
                        else
                        {
                            queue.Enqueue((child, element, scroll));
                        }
                    }
                }

                // Slot discovery/rendering adapted from StashValueByZx0 by zx0CF1.
                var itemAddress = handle.ReadMemory<IntPtr>(element + UiElementItemAddressOffset);
                if (itemAddress == IntPtr.Zero) continue;
                report.NonZeroPointers++;

                if (!candidatesByItem.TryGetValue(itemAddress, out var itemCandidates))
                {
                    itemCandidates = new List<SlotElementCandidate>();
                    candidatesByItem[itemAddress] = itemCandidates;
                }

                itemCandidates.Add(new SlotElementCandidate(element, parent, scroll));
            }

            // Premium tabs expose many ghost UI copies. Deduplicate by item pointer before validating,
            // constructing components, reading mods, or pricing so each real item pays those costs once.
            report.UniquePointers = candidatesByItem.Count;
            var panelMax = panelPosition + panelSize;
            var slots = new List<SlotInfo>();
            foreach (var (itemAddress, itemCandidates) in candidatesByItem)
            {
                var hasVisibleRect = false;
                var position = Vector2.Zero;
                var size = Vector2.Zero;
                var selectedScroll = default(ScrollBinding);
                var diagnosticElement = itemCandidates[0].ElementAddress;
                foreach (var candidate in itemCandidates)
                {
                    if (!TryGetSlotRect(candidate, out var candidatePosition, out var candidateSize)) continue;
                    var center = candidatePosition + (candidateSize * 0.5f);
                    if (center.X < panelPosition.X || center.X > panelMax.X) continue;
                    if (!candidate.Scroll.IsActive &&
                        (center.Y < panelPosition.Y || center.Y > panelMax.Y)) continue;

                    diagnosticElement = candidate.ElementAddress;
                    position = candidatePosition;
                    size = candidateSize;
                    selectedScroll = candidate.Scroll;
                    hasVisibleRect = true;
                    break;
                }

                if (!hasVisibleRect) continue;
                if (!PluginUiElementReflection.TryValidateItemAddress(itemAddress, out _, out var failureReason))
                {
                    report.AddRejected(diagnosticElement, itemAddress, failureReason);
                    continue;
                }

                var item = ReadFreshItem(itemAddress);
                if (item == null || string.IsNullOrEmpty(item.Path) ||
                    !item.Path.StartsWith(ItemPathPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    report.AddRejected(diagnosticElement, itemAddress, "item changed after validation");
                    continue;
                }

                report.ValidItems++;
                if (!this.TryPriceItem(item, out var valueEx, out var valueText, includeUniqueName: false)) continue;
                report.PricedCandidates++;

                var highlight = valueEx >= this.CurrentHighlightMin;
                slots.Add(new SlotInfo(itemAddress, position, size, valueText, selectedScroll, highlight));
            }

            report.VisibleSlots = slots.Count;

            return slots;
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

            // Shape checks keep ordinary [1]/[2] child layouts from being mistaken for scroll views.
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

        private static bool TryGetSlotRect(SlotElementCandidate candidate, out Vector2 position, out Vector2 size)
        {
            if (!PluginUiElementReflection.TryGetAbsoluteRect(candidate.ElementAddress, out position, out size)) return false;

            // Premium tabs can keep the item pointer on a small bookkeeping child while its parent
            // owns the visible cell rectangle.
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

        private static bool IsAnySlotHovered(IReadOnlyList<SlotInfo> slots, ScrollFrameState scroll)
        {
            var mousePosition = ImGui.GetIO().MousePos;
            foreach (var slot in slots)
            {
                var position = GetLiveSlotPosition(slot, scroll);
                var centerY = position.Y + (slot.Size.Y * 0.5f);
                if (centerY < scroll.ClipTop || centerY > scroll.ClipBottom) continue;
                if (mousePosition.X >= position.X && mousePosition.X <= position.X + slot.Size.X &&
                    mousePosition.Y >= position.Y && mousePosition.Y <= position.Y + slot.Size.Y)
                {
                    return true;
                }
            }

            return false;
        }

        private static ScrollFrameState GetScrollFrameState(IReadOnlyList<SlotInfo> slots)
        {
            foreach (var slot in slots)
            {
                var binding = slot.Scroll;
                if (!binding.IsActive) continue;
                if (!PluginUiElementReflection.TryGetAbsoluteRect(binding.HolderAddress, out var holderPosition, out var holderSize) ||
                    !PluginUiElementReflection.TryGetAbsoluteRect(binding.ThumbAddress, out var thumbPosition, out var thumbSize))
                {
                    return new ScrollFrameState(binding.HolderAddress, 0f, binding.ClipTop, binding.ClipBottom);
                }

                var thumbTravel = holderSize.Y - thumbSize.Y;
                var contentOverflow = binding.ContentHeight - holderSize.Y;
                if (thumbTravel <= 0f || contentOverflow <= 0f)
                {
                    return new ScrollFrameState(binding.HolderAddress, 0f, holderPosition.Y, holderPosition.Y + holderSize.Y);
                }

                var progress = Math.Clamp((thumbPosition.Y - holderPosition.Y) / thumbTravel, 0f, 1f);
                var currentOffset = progress * contentOverflow;
                return new ScrollFrameState(
                    binding.HolderAddress,
                    currentOffset - binding.ScanOffsetY,
                    holderPosition.Y,
                    holderPosition.Y + holderSize.Y);
            }

            return ScrollFrameState.None;
        }

        private static Vector2 GetLiveSlotPosition(SlotInfo slot, ScrollFrameState scroll)
        {
            if (!slot.Scroll.IsActive || slot.Scroll.HolderAddress != scroll.HolderAddress) return slot.Position;
            return slot.Position - new Vector2(0f, scroll.OffsetDeltaY);
        }

        private void DrawSlotDiagnosticsWindow()
        {
            ImGui.SetNextWindowSize(new Vector2(720f, 420f), ImGuiCond.FirstUseEver);
            if (ImGui.Begin(
                    this.PluginText.Title("diagnostics.slots.window_title", "LootValue Slot Diagnostics", "LootValueSlotDiagnostics"),
                    ref this.Settings.ShowSlotDebugInfo))
            {
                this.DrawSlotScanReport(this.PluginText.T("diagnostics.slots.left_panel", "Left panel (stash)"), this.leftSlotReport);
                ImGui.Separator();
                this.DrawSlotScanReport(this.PluginText.T("diagnostics.slots.right_panel", "Right panel (inventory)"), this.rightSlotReport);
                ImGui.Separator();
                this.DrawSlotScanReport(this.PluginText.T("diagnostics.slots.ritual_rewards", "Ritual rewards"), this.ritualSlotReport);
            }

            ImGui.End();
        }

        private void DrawSlotScanReport(string label, SlotScanReport report)
        {
            ImGui.TextUnformatted($"{label}: 0x{report.PanelAddress.ToInt64():X}");
            ImGui.TextUnformatted(this.PluginText.F(
                "diagnostics.slots.summary",
                "UI elements={0}  non-zero +0x{1:X}={2}  unique pointers={3}  valid items={4}  priced={5}  visible={6}  scroll views={7}  scroll Y={8:0.0}",
                report.VisitedElements,
                UiElementItemAddressOffset,
                report.NonZeroPointers,
                report.UniquePointers,
                report.ValidItems,
                report.PricedCandidates,
                report.VisibleSlots,
                report.ScrollContainers,
                report.ScrollOffsetY));
            ImGui.TextUnformatted(this.PluginText.F(
                "diagnostics.slots.rejected",
                "Rejected candidates={0} (showing up to {1})",
                report.RejectedCandidates,
                SlotScanReport.MaxSamples));
            foreach (var sample in report.RejectedSamples)
            {
                ImGui.TextUnformatted(sample);
            }
        }

        private void DrawItemSlots(
            IReadOnlyList<SlotInfo> slots,
            bool drawPrices,
            bool hidePrices,
            ScrollFrameState scroll)
        {
            var foreground = ImGui.GetBackgroundDrawList();
            var font = ImGui.GetFont();
            var fontSize = ImGui.GetFontSize() * this.Settings.SlotFontScale;
            var color = ImGui.ColorConvertFloat4ToU32(this.Settings.TextColor);

            foreach (var slot in slots)
            {
                var position = GetLiveSlotPosition(slot, scroll);
                var centerY = position.Y + (slot.Size.Y * 0.5f);
                if (centerY < scroll.ClipTop || centerY > scroll.ClipBottom) continue;

                if (this.Settings.ShowSlotDebugInfo)
                {
                    foreground.AddRect(position, position + slot.Size, 0xFFFF00FFu, 0f, ImDrawFlags.None, 2f);
                    foreground.AddText(font, fontSize, position, 0xFFFFFFFFu, $"E: {slot.ItemAddress.ToInt64():X}");
                }

                if (!drawPrices || hidePrices) continue;
                var textWidth = ImGui.CalcTextSize(slot.ValueText).X * this.Settings.SlotFontScale;
                var drawPosition = new Vector2(
                    position.X + this.Settings.SlotOffsetX,
                    position.Y + slot.Size.Y - fontSize + this.Settings.SlotOffsetY);
                var textColor = ImGui.ColorConvertFloat4ToU32(slot.Highlight ? this.Settings.HighlightColor : this.Settings.TextColor);
                var bg = ImGui.ColorConvertFloat4ToU32(slot.Highlight ? this.Settings.HighlightBackgroundColor : this.Settings.BackgroundColor);
                foreground.AddRectFilled(
                    drawPosition - new Vector2(3f, 1f),
                    drawPosition + new Vector2(textWidth + 3f, fontSize + 1f),
                    bg,
                    3f);
                foreground.AddText(font, fontSize, drawPosition + new Vector2(1f, 1f), 0xCC000000u, slot.ValueText);
                foreground.AddText(font, fontSize, drawPosition, textColor, slot.ValueText);
            }
        }

        private static string FormatValue(double value, string currency) => currency switch
        {
            "divine" => value.ToString("0.00", CultureInfo.InvariantCulture) + " div",
            "chaos" => value.ToString("0.#", CultureInfo.InvariantCulture) + " c",
            _ => value.ToString("0.#", CultureInfo.InvariantCulture) + " ex",
        };

        /// <summary>Alpha-beta filter on a screen position (per tracked key). It estimates screen-space
        /// VELOCITY and advances by it each frame, then nudges toward the noisy measurement by alpha — so
        /// constant-velocity motion tracks with no lag while the per-frame sampling jitter is rejected.
        /// A large jump (teleport / zone change) resets the tracker. Velocity is in px/frame (assumes a
        /// roughly steady frame rate, which is fine for jitter rejection).</summary>
        private static Vector2 Track<TKey>(Dictionary<TKey, Tracked> dict, TKey key, Vector2 measure, int rate)
            where TKey : notnull
        {
            var alpha = Math.Clamp(rate / 1000f, 0.01f, 1f);
            var beta = alpha * alpha / (2f - alpha);
            if (dict.TryGetValue(key, out var t))
            {
                var predicted = t.Pos + t.Vel;
                var residual = measure - predicted;
                if (residual.LengthSquared() <= 150f * 150f)
                {
                    var pos = predicted + (residual * alpha);
                    var vel = t.Vel + (residual * beta);
                    dict[key] = new Tracked(pos, vel);
                    return pos;
                }
            }

            dict[key] = new Tracked(measure, Vector2.Zero);
            return measure;
        }

        /// <summary>Walks every awake entity and reports the ground-item detection funnel + sample reads,
        /// so we can see which stage drops items. Throttled. Independent of the overlay gates.</summary>
        private void RunDiagnostics()
        {
            var now = DateTime.UtcNow;
            if (now < this.nextDiagUtc) return;
            this.nextDiagUtc = now.AddMilliseconds(500);

            this.diagSamples.Clear();
            int total = 0, wiPath = 0, metaItemsPath = 0, wiComp = 0, innerOk = 0, priced = 0;

            var area = Core.States.InGameStateObject.CurrentAreaInstance;
            foreach (var entity in area.AwakeEntities.Values)
            {
                total++;
                var p = entity.Path ?? string.Empty;
                if (p.Contains("WorldItem", StringComparison.Ordinal)) wiPath++;
                if (p.StartsWith(ItemPathPrefix, StringComparison.Ordinal)) metaItemsPath++;

                if (!entity.TryGetComponent<WorldItem>(out var wi) || wi.ItemEntityAddress == IntPtr.Zero) continue;
                wiComp++;

                var item = ReadFreshItem(wi.ItemEntityAddress);
                if (item == null) continue;
                innerOk++;

                var rarity = item.TryGetComponent<Mods>(out var m) ? m.Rarity : Rarity.Normal;
                var baseName = item.TryGetComponent<Base>(out var b) ? b.BaseItemName : string.Empty;
                var art = item.TryGetComponent<RenderItem>(out var ri) ? ExtractArtBasename(ri.ResourcePath) : string.Empty;
                var ok = this.TryPriceItem(item, out var ex, out var lbl);
                if (ok)
                {
                    priced++;
                }

                if (this.diagSamples.Count < 20)
                {
                    this.diagSamples.Add(ok
                        ? $"{rarity} {baseName} [art={art}] -> {lbl} ({ex:0.##})"
                        : $"{rarity} {baseName} [art={art}] -> {this.PluginText.T("diagnostics.no_price", "NO PRICE")}");
                }
            }

            this.diagSummary =
                this.PluginText.F("diagnostics.summary.ingame", "InGame={0}  PanelOpen={1}", Core.States.GameCurrentState == GameStateTypes.InGameState, Core.States.InGameStateObject.GameUi.IsAnyLargePanelOpen) + "\n" +
                this.PluginText.F("diagnostics.summary.awake_entities", "AwakeEntities={0}", total) + "\n" +
                this.PluginText.F("diagnostics.summary.paths", "path contains 'WorldItem'={0}    path starts 'Metadata/Items'={1}", wiPath, metaItemsPath) + "\n" +
                this.PluginText.F("diagnostics.summary.components", "WorldItem component (inner!=0)={0}    inner item read OK={1}", wiComp, innerOk) + "\n" +
                this.PluginText.F("diagnostics.summary.pricing", "priced={0}    would draw={1}", priced, priced) + "\n" +
                this.PluginText.F("diagnostics.summary.price_db", "priceDB items={0}  fetching={1}", PoeNinjaPriceFetcher.LoadedItemCount, PoeNinjaPriceFetcher.IsFetching);
        }

        private void DrawDiagnosticsWindow()
        {
            ImGui.SetNextWindowSize(new Vector2(580, 440), ImGuiCond.FirstUseEver);
            if (ImGui.Begin(this.PluginText.Title("diagnostics.window_title", "LootValue Diagnostics", "LootValueDiagnostics"), ref this.Settings.DiagnosticsMode))
            {
                ImGui.TextUnformatted(this.diagSummary);
                ImGui.TextUnformatted(this.exchangeDiagnostic);
                ImGui.Separator();
                ImGui.TextUnformatted(this.PluginText.F("diagnostics.samples", "Samples ({0}):", this.diagSamples.Count));
                foreach (var s in this.diagSamples)
                {
                    ImGui.TextUnformatted(s);
                }
            }

            ImGui.End();
        }

        /// <summary>Resolve an item's display value + label text. Uniques price by icon art (revealing
        /// unidentified ones); everything else by base-type name.</summary>
        private bool TryPriceItem(Item item, out double valueEx, out string label, bool includeUniqueName = true)
        {
            return this.TryPriceItem(item, out valueEx, out label, out _, out _, out _, includeUniqueName);
        }

        private bool TryPriceItem(
            Item item,
            out double valueEx,
            out string label,
            out double displayValue,
            out string displayCurrency,
            out string resolvedItemName,
            bool includeUniqueName = true)
        {
            valueEx = 0;
            label = string.Empty;
            displayValue = 0;
            displayCurrency = "ex";
            resolvedItemName = string.Empty;

            var rarity = Rarity.Normal;
            if (item.TryGetComponent<Mods>(out var mods)) rarity = mods.Rarity;

            var baseName = item.TryGetComponent<Base>(out var baseComp) ? baseComp.BaseItemName?.Trim() ?? string.Empty : string.Empty;
            var artPath = item.TryGetComponent<RenderItem>(out var renderItem) ? renderItem.ResourcePath : string.Empty;
            var artBasename = ExtractArtBasename(artPath);
            var fullItemPath = item.Path ?? string.Empty;
            var internalName = fullItemPath.Contains('/') ? fullItemPath[(fullItemPath.LastIndexOf('/') + 1)..] : fullItemPath;

            var itemName = baseName;
            if (rarity == Rarity.Unique)
            {
                // 1. Check full art path (e.g. Art/2DItems/... from uniqueArtMapping.json)
                if (!string.IsNullOrEmpty(artPath) &&
                    PoeNinjaPriceFetcher.TryResolveDisplayName(artPath, out var uniqueNameFromPath) &&
                    !PoeNinjaPriceFetcher.IsGenericLookupName(uniqueNameFromPath))
                {
                    itemName = uniqueNameFromPath;
                }
                else if (!string.IsNullOrEmpty(artBasename))
                {
                    foreach (var key in ArtKeyVariants(artBasename))
                    {
                        if (PoeNinjaPriceFetcher.TryResolveDisplayName(key, out var uniqueName) &&
                            !PoeNinjaPriceFetcher.IsGenericLookupName(uniqueName))
                        {
                            itemName = uniqueName;
                            break;
                        }

                        if (PoeNinjaPriceFetcher.HasPriceDataForName(key))
                        {
                            itemName = key;
                            break;
                        }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(itemName)) return false;
            resolvedItemName = itemName;

            var modLines = ItemModHelper.GetModLines(item);
            var price = PoeNinjaPriceFetcher.GetPrice(itemName, modLines, internalName, fullItemPath);
            if (price == null) return false;

            var stack = item.TryGetComponent<Stack>(out var stackComp) && stackComp.Count > 1 ? stackComp.Count : 1;
            var priceChaos = price.PriceChaos * stack;

            var priced = new PoeNinjaPrice { PriceChaos = priceChaos };
            var (dispVal, dispCur) = PoeNinjaPriceFetcher.GetDisplayPrice(priced, this.Settings.DisplayCurrency);
            displayValue = dispVal;
            displayCurrency = dispCur;

            // Value floor / highlight compare in the chosen display currency (Divine, Exalted, or Chaos).
            valueEx = displayValue;

            var valueText = FormatValue(displayValue, displayCurrency);

            // valueText is already the stack TOTAL; only uniques get a name prefix.
            var nameForLabel = includeUniqueName && rarity == Rarity.Unique && this.Settings.RevealUnidentifiedUniques ? $"{itemName} — " : string.Empty;
            label = $"{nameForLabel}{valueText}";
            return true;
        }

        private static Item? ReadFreshItem(IntPtr itemAddress)
        {
            if (itemAddress == IntPtr.Zero) return null;
            try
            {
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

        // "Art/2DItems/.../Uniques/Deidbell.dds" -> "Deidbell".
        private static string ExtractArtBasename(string? artPath)
        {
            if (string.IsNullOrWhiteSpace(artPath)) return string.Empty;
            var slash = artPath.LastIndexOfAny(new[] { '/', '\\' });
            var file = slash >= 0 && slash < artPath.Length - 1 ? artPath[(slash + 1)..] : artPath;
            var dot = file.LastIndexOf('.');
            return dot > 0 ? file[..dot] : file;
        }

        // GGG art basenames and the price DB disagree on a leading "The" (both directions).
        private static IEnumerable<string> ArtKeyVariants(string artBasename)
        {
            if (string.IsNullOrWhiteSpace(artBasename)) yield break;
            yield return artBasename;
            if (artBasename.StartsWith("The", StringComparison.OrdinalIgnoreCase) && artBasename.Length > 3)
                yield return artBasename[3..];
            else
                yield return "The" + artBasename;
        }

        private readonly struct LootLabel
        {
            public LootLabel(uint entityId, Render render, string text, uint color, bool highlight)
            {
                this.EntityId = entityId;
                this.Render = render;
                this.Text = text;
                this.Color = color;
                this.Highlight = highlight;
            }

            public uint EntityId { get; }

            public Render Render { get; }

            public string Text { get; }

            public uint Color { get; }

            public bool Highlight { get; }
        }

        private readonly struct SlotInfo
        {
            public SlotInfo(
                IntPtr itemAddress,
                Vector2 position,
                Vector2 size,
                string valueText,
                ScrollBinding scroll,
                bool highlight = false)
            {
                this.ItemAddress = itemAddress;
                this.Position = position;
                this.Size = size;
                this.ValueText = valueText;
                this.Scroll = scroll;
                this.Highlight = highlight;
            }

            public IntPtr ItemAddress { get; }

            public Vector2 Position { get; }

            public Vector2 Size { get; }

            public string ValueText { get; }

            public ScrollBinding Scroll { get; }

            public bool Highlight { get; }
        }

        private readonly struct ExchangePriceLabel
        {
            public ExchangePriceLabel(Vector2 position, string text, uint color, bool highlight)
            {
                this.Position = position;
                this.Text = text;
                this.Color = color;
                this.Highlight = highlight;
            }

            public Vector2 Position { get; }

            public string Text { get; }

            public uint Color { get; }

            public bool Highlight { get; }
        }

        private readonly struct RuneshapeRowPriceLabel
        {
            public RuneshapeRowPriceLabel(Vector2 position, string text, bool isHigh, bool isBest = false, Vector2 rowPos = default, Vector2 rowSize = default)
            {
                this.Position = position;
                this.Text = text;
                this.IsHigh = isHigh;
                this.IsBest = isBest;
                this.RowPos = rowPos;
                this.RowSize = rowSize;
            }

            public Vector2 Position { get; }

            public string Text { get; }

            public bool IsHigh { get; }

            public bool IsBest { get; }

            public Vector2 RowPos { get; }

            public Vector2 RowSize { get; }
        }

        private readonly struct SlotElementCandidate
        {
            public SlotElementCandidate(
                IntPtr elementAddress,
                IntPtr parentAddress,
                ScrollBinding scroll)
            {
                this.ElementAddress = elementAddress;
                this.ParentAddress = parentAddress;
                this.Scroll = scroll;
            }

            public IntPtr ElementAddress { get; }

            public IntPtr ParentAddress { get; }

            public ScrollBinding Scroll { get; }
        }

        private readonly struct ScrollBinding
        {
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
            }

            public bool IsActive => this.HolderAddress != IntPtr.Zero && this.ThumbAddress != IntPtr.Zero;

            public IntPtr HolderAddress { get; }

            public IntPtr ThumbAddress { get; }

            public float ContentHeight { get; }

            public float ScanOffsetY { get; }

            public float ClipTop { get; }

            public float ClipBottom { get; }
        }

        private readonly struct ScrollFrameState
        {
            public static ScrollFrameState None { get; } = new(
                IntPtr.Zero,
                0f,
                float.NegativeInfinity,
                float.PositiveInfinity);

            public ScrollFrameState(IntPtr holderAddress, float offsetDeltaY, float clipTop, float clipBottom)
            {
                this.HolderAddress = holderAddress;
                this.OffsetDeltaY = offsetDeltaY;
                this.ClipTop = clipTop;
                this.ClipBottom = clipBottom;
            }

            public IntPtr HolderAddress { get; }

            public float OffsetDeltaY { get; }

            public float ClipTop { get; }

            public float ClipBottom { get; }
        }

        private sealed class SlotScanReport
        {
            public const int MaxSamples = 8;
            private readonly HashSet<IntPtr> sampledPointers = new();

            public SlotScanReport(IntPtr panelAddress)
            {
                this.PanelAddress = panelAddress;
            }

            public IntPtr PanelAddress { get; }

            public int VisitedElements { get; set; }

            public int NonZeroPointers { get; set; }

            public int UniquePointers { get; set; }

            public int ScrollContainers { get; set; }

            public float ScrollOffsetY { get; set; }

            public int ValidItems { get; set; }

            public int PricedCandidates { get; set; }

            public int VisibleSlots { get; set; }

            public int RejectedCandidates { get; private set; }

            public List<string> RejectedSamples { get; } = new();

            public void AddRejected(IntPtr elementAddress, IntPtr itemAddress, string reason)
            {
                this.RejectedCandidates++;
                if (this.RejectedSamples.Count >= MaxSamples || !this.sampledPointers.Add(itemAddress)) return;
                this.RejectedSamples.Add(
                    $"ui=0x{elementAddress.ToInt64():X}  candidate=0x{itemAddress.ToInt64():X}  {reason}");
            }
        }

        private readonly struct TagChip
        {
            public TagChip(IntPtr elementAddress, string text, uint color, bool highlight)
            {
                this.ElementAddress = elementAddress;
                this.Text = text;
                this.Color = color;
                this.Highlight = highlight;
            }

            public IntPtr ElementAddress { get; }

            public string Text { get; }

            public uint Color { get; }

            public bool Highlight { get; }
        }

        private readonly struct Tracked
        {
            public Tracked(Vector2 pos, Vector2 vel)
            {
                this.Pos = pos;
                this.Vel = vel;
            }

            public Vector2 Pos { get; }

            public Vector2 Vel { get; }
        }

        private void ProcessDropAlerts()
        {
            var now = DateTime.UtcNow;
            var area = Core.States.InGameStateObject?.CurrentAreaInstance;
            if (area == null || area.AwakeEntities == null) return;

            var newAlertDrops = new List<ActiveDropAlert>();

            foreach (var entity in area.AwakeEntities.Values)
            {
                if (!entity.TryGetComponent<WorldItem>(out var worldItem) || worldItem.ItemEntityAddress == IntPtr.Zero) continue;
                if (!entity.TryGetComponent<Render>(out var render)) continue;

                var item = ReadFreshItem(worldItem.ItemEntityAddress);
                if (item == null) continue;

                if (!this.TryPriceItem(item, out _, out _, out var displayVal, out var displayCur, out var itemName)) continue;
                if (displayVal < this.Settings.AlertMinDisplayValue) continue;

                var highlightColor = ImGui.ColorConvertFloat4ToU32(this.Settings.HighlightColor);
                newAlertDrops.Add(new ActiveDropAlert(entity.Id, render, itemName, displayVal, displayCur, highlightColor));

                // If newly discovered drop above threshold: trigger sound and banner!
                if (this.alertedEntityIds.Add(entity.Id))
                {
                    this.PlayAlertSound();

                    if (this.Settings.EnableAlertBanner)
                    {
                        lock (this.activeAlertBanners)
                        {
                            this.activeAlertBanners.Add(new DropAlertBanner(entity.Id, itemName, displayVal, displayCur, now));
                            if (this.activeAlertBanners.Count > 5)
                            {
                                this.activeAlertBanners.RemoveRange(0, this.activeAlertBanners.Count - 5);
                            }
                        }
                    }
                }
            }

            this.activeAlertDrops = newAlertDrops;
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
                    var subText = "★ VALUABLE DROP DETECTED ★";
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

        // =========================================================================
        // PoE 2 Expedition (Runeshape Monolith & Crafting Panel)
        // =========================================================================

        private DateTime nextMonolithScanUtc = DateTime.MinValue;
        private List<MonolithInfo> cachedMonoliths = new();

        private void DrawExpeditionMonolithOverlay()
        {
            var now = DateTime.UtcNow;
            if (now >= this.nextMonolithScanUtc && !this.isMonolithScanRunning)
            {
                this.nextMonolithScanUtc = now.AddMilliseconds(350);
                this.isMonolithScanRunning = true;
                Task.Run(() =>
                {
                    try
                    {
                        this.ScanExpeditionMonoliths();
                    }
                    catch
                    {
                    }
                    finally
                    {
                        this.isMonolithScanRunning = false;
                    }
                });
            }

            if (this.cachedMonoliths.Count == 0) return;

            var world = Core.States.InGameStateObject.CurrentWorldInstance;
            if (world == null) return;

            var fg = ImGui.GetForegroundDrawList();

            foreach (var m in this.cachedMonoliths)
            {
                if (this.Settings.HideCompletedMonoliths && m.IsCompleted) continue;

                var worldPos = new StdTuple3D<float>
                {
                    X = m.WorldPosition.X,
                    Y = m.WorldPosition.Y,
                    Z = m.TerrainHeight - 220f
                };

                var screenPos = world.WorldToScreen(worldPos, worldPos.Z);
                if (screenPos == Vector2.Zero) continue;

                screenPos.X += this.Settings.ExpeditionBadgeOffsetX;
                screenPos.Y += this.Settings.ExpeditionBadgeOffsetY;

                this.DrawMonolithBadge(fg, screenPos, m);
            }
        }

        private void ScanExpeditionMonoliths()
        {
            var area = Core.States.InGameStateObject?.CurrentAreaInstance;
            if (area == null || area.AwakeEntities == null) return;

            var areaLevel = area.CurrentAreaLevel;
            var newMonoliths = new List<MonolithInfo>();
            foreach (var entity in area.AwakeEntities.Values)
            {
                if (entity == null || entity.Address == IntPtr.Zero) continue;
                if (!entity.Path.Contains("Expedition2Encounter", StringComparison.OrdinalIgnoreCase) && entity.EntityCustomGroup != 103)
                {
                    continue;
                }

                if (RuneshapeReader.TryReadMonolith(entity, areaLevel, this.Settings.DisplayCurrency, out var info))
                {
                    newMonoliths.Add(info);
                }
            }

            this.cachedMonoliths = newMonoliths;
        }

        private void DrawMonolithBadge(ImDrawListPtr fg, Vector2 screenPos, MonolithInfo m)
        {
            var best = m.BestOffer;
            var anchorName = m.IsUnique ? "Unique" : (m.AnchorIdx >= 0 ? RuneshapeCatalog.Instance.GetRuneName(m.AnchorIdx) : "Random");
            var headerText = $"[{m.HoleCount} Sockets] {anchorName}";

            string rewardText;
            string priceText = string.Empty;
            bool isHigh = false;

            if (best != null && !string.IsNullOrEmpty(best.Name) && best.ChaosValue > 0)
            {
                rewardText = best.Count > 1 ? $"{best.Count}x {best.Name}" : best.Name;
                if (best.DisplayPrice > 0)
                {
                    priceText = best.DisplayPrice >= 100 ? $"{best.DisplayPrice:F0} {best.CurrencySymbol}" : $"{best.DisplayPrice:F1} {best.CurrencySymbol}";
                }
                var chaosDiv = PoeNinjaPriceFetcher.ChaosPerDivine > 0 ? PoeNinjaPriceFetcher.ChaosPerDivine : 200.0;
                isHigh = best.ChaosValue >= chaosDiv;
            }
            else
            {
                rewardText = m.IsUnique ? "Unique Monolith" : "Expedition Encounter";
            }

            var headerSize = ImGui.CalcTextSize(headerText);
            var rewardSize = ImGui.CalcTextSize(rewardText);
            var priceSize = !string.IsNullOrEmpty(priceText) ? ImGui.CalcTextSize(priceText) : Vector2.Zero;

            var contentW = Math.Max(headerSize.X, rewardSize.X + (priceSize.X > 0 ? priceSize.X + 14f : 0f));
            var pad = new Vector2(9f, 6f);
            var boxW = contentW + pad.X * 2;
            var boxH = headerSize.Y + rewardSize.Y + pad.Y * 2 + 3f;

            var min = new Vector2(screenPos.X - boxW / 2f, screenPos.Y - boxH / 2f);
            var max = new Vector2(min.X + boxW, min.Y + boxH);

            var bg = isHigh ? 0xF5181206u : 0xF0121216u;
            var border = isHigh ? 0xFF40E040u : (m.HoleCount >= 7 ? 0xFFE6C84Du : 0xFF888899u);

            // Shadow & Box
            fg.AddRectFilled(min + new Vector2(2, 2), max + new Vector2(2, 2), 0x70000000u, 6f);
            fg.AddRectFilled(min, max, bg, 6f);
            fg.AddRect(min, max, border, 6f, ImDrawFlags.None, isHigh ? 2.2f : 1.4f);

            // Header line: [X Sockets] AnchorName
            var headerColor = m.HoleCount >= 5 ? 0xFF4040FFu : 0xFFE0E0E0u;
            fg.AddText(new Vector2(min.X + pad.X, min.Y + pad.Y), headerColor, headerText);

            // Reward name & price
            var line2Y = min.Y + pad.Y + headerSize.Y + 3f;
            fg.AddText(new Vector2(min.X + pad.X, line2Y), 0xFFFFFFFFu, rewardText);

            if (!string.IsNullOrEmpty(priceText))
            {
                var priceColor = isHigh ? 0xFF40FF40u : 0xFFE6C84Du;
                var priceX = max.X - pad.X - priceSize.X;
                fg.AddText(new Vector2(priceX, line2Y), priceColor, priceText);
            }

            // Hover tooltip showing all possible offers
            var mouse = ImGui.GetIO().MousePos;
            if (mouse.X >= min.X && mouse.X <= max.X && mouse.Y >= min.Y && mouse.Y <= max.Y)
            {
                ImGui.BeginTooltip();
                ImGui.TextColored(new Vector4(1f, 0.85f, 0.4f, 1f), $"=== Monolith ({m.HoleCount} Sockets) Recipes & Rewards ===");
                ImGui.Separator();
                if (m.AllOffers.Count > 0)
                {
                    var chaosDiv = PoeNinjaPriceFetcher.ChaosPerDivine > 0 ? PoeNinjaPriceFetcher.ChaosPerDivine : 200.0;
                    foreach (var o in m.AllOffers.Take(10))
                    {
                        var pStr = o.DisplayPrice > 0 ? $"{o.DisplayPrice:F1} {o.CurrencySymbol}" : "---";
                        var c = o.ChaosValue >= chaosDiv
                            ? new Vector4(0.4f, 1f, 0.4f, 1f) : new Vector4(0.9f, 0.9f, 0.9f, 1f);
                        ImGui.TextColored(c, $"[{o.Size}h] {o.Name} : {pStr}");
                        if (!string.IsNullOrEmpty(o.RunesSummary))
                        {
                            ImGui.SameLine();
                            ImGui.TextDisabled($"({o.RunesSummary})");
                        }
                    }
                }
                else
                {
                    ImGui.TextDisabled("No recipes unlocked at this area level.");
                }
                ImGui.EndTooltip();
            }
        }

        private void DrawRuneshapeUiOverlay()
        {
            var now = DateTime.UtcNow;
            if (now >= this.nextRuneshapeUiScanUtc && !this.isRuneshapeUiScanRunning)
            {
                this.nextRuneshapeUiScanUtc = now.AddMilliseconds(Math.Clamp(this.Settings.SlotRescanIntervalMs, 100, 2000));
                this.isRuneshapeUiScanRunning = true;
                Task.Run(() =>
                {
                    try
                    {
                        this.ScanRuneshapeUi();
                    }
                    catch
                    {
                    }
                    finally
                    {
                        this.isRuneshapeUiScanRunning = false;
                    }
                });
            }

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
                DrawRuneforgePriceChip(fg, row.Position, row.Text, row.IsHigh, row.IsBest);
            }
        }

        private void ScanRuneshapeUi()
        {
            var handle = Core.Process?.Handle;
            if (handle == null)
            {
                this.cachedRuneshapeRows = new List<RuneshapeRowPriceLabel>();
                return;
            }
            var gameUi = Core.States.InGameStateObject?.GameUi;
            if (gameUi == null || gameUi.Address == IntPtr.Zero)
            {
                this.cachedRuneshapeRows = new List<RuneshapeRowPriceLabel>();
                return;
            }

            var container = RuneshapeReader.ResolveRuneforgeContainer(
                gameUi.Address,
                addr => handle.TryReadMemory<UiElementBaseOffset>(addr, out var off) ? off : null,
                vec => handle.ReadStdVector<IntPtr>(vec));

            if (container == IntPtr.Zero || !handle.TryReadMemory<UiElementBaseOffset>(container, out var off))
            {
                this.cachedRuneshapeRows = new List<RuneshapeRowPriceLabel>();
                return;
            }

            var rows = handle.ReadStdVector<IntPtr>(off.ChildrensPtr);
            if (rows.Length == 0)
            {
                this.cachedRuneshapeRows = new List<RuneshapeRowPriceLabel>();
                return;
            }

            var newRows = new List<RuneshapeRowPriceLabel>();
            var chaosDiv = PoeNinjaPriceFetcher.ChaosPerDivine > 0 ? PoeNinjaPriceFetcher.ChaosPerDivine : 200.0;
            var rowCandidates = new List<(Vector2 pos, Vector2 size, string chip, bool isHigh, double score)>();

            foreach (var row in rows)
            {
                if (row == IntPtr.Zero) continue;
                if (!handle.TryReadMemory<UiElementBaseOffset>(row, out var rowOff) || !UiElementBaseFuncs.IsVisibleChecker(rowOff.Flags)) continue;

                var rowKids = handle.ReadStdVector<IntPtr>(rowOff.ChildrensPtr);
                if (rowKids.Length == 0) continue;

                var label = rowKids[0];
                var rawText = this.ReadUiElementText(label);
                if (string.IsNullOrWhiteSpace(rawText)) continue;

                ParseRuneforgeRowText(rawText, out var count, out var itemName);
                if (string.IsNullOrWhiteSpace(itemName)) continue;

                if (!PluginUiElementReflection.TryGetAbsoluteRect(row, out var rowPos, out var rowSize)) continue;
                if (rowSize.X <= 0f || rowSize.Y <= 0f) continue;

                var price = PoeNinjaPriceFetcher.GetPrice(itemName);
                double score = 0;
                string chipText;
                bool isHigh = false;
                bool isUnique = itemName.Contains("Unique", StringComparison.OrdinalIgnoreCase) ||
                                rawText.Contains("Unique", StringComparison.OrdinalIgnoreCase) ||
                                rawText.Contains("ยูนิค", StringComparison.OrdinalIgnoreCase) ||
                                rawText.Contains("ส้ม", StringComparison.OrdinalIgnoreCase);

                bool isVeryRareUnique = isUnique &&
                                        (itemName.Contains("Very Rare", StringComparison.OrdinalIgnoreCase) ||
                                         rawText.Contains("Very Rare", StringComparison.OrdinalIgnoreCase) ||
                                         rawText.Contains("หายากมาก", StringComparison.OrdinalIgnoreCase));

                bool isRareUnique = isUnique && !isVeryRareUnique &&
                                    (itemName.Contains("Rare", StringComparison.OrdinalIgnoreCase) ||
                                     rawText.Contains("Rare", StringComparison.OrdinalIgnoreCase) ||
                                     rawText.Contains("หายาก", StringComparison.OrdinalIgnoreCase));

                if (isUnique)
                {
                    if (isVeryRareUnique)
                    {
                        chipText = "Very Rare Unique";
                        score = 500;
                        isHigh = true;
                    }
                    else if (isRareUnique)
                    {
                        chipText = "Rare Unique";
                        score = 100;
                        isHigh = false;
                    }
                    else
                    {
                        // Generic unique crafts (Body Armour, Boots, ขวาน ยูนิค, etc.) show NOTHING AT ALL!
                        continue;
                    }
                }
                else if (price != null && price.PriceChaos > 0)
                {
                    var totalChaos = price.PriceChaos * count;
                    var (dispVal, dispCur) = PoeNinjaPriceFetcher.GetDisplayPrice(totalChaos, this.Settings.DisplayCurrency);
                    chipText = dispVal >= 100 ? $"{dispVal:F0} {dispCur}" : $"{dispVal:F1} {dispCur}";
                    isHigh = (totalChaos / chaosDiv) >= 1.0;
                    score = totalChaos;
                }
                else
                {
                    // Generic unpriced non-unique items show NOTHING AT ALL!
                    continue;
                }

                rowCandidates.Add((rowPos, rowSize, chipText, isHigh, score));
            }

            if (rowCandidates.Count > 0)
            {
                double bestScore = -1;
                int bestIdx = -1;

                // Pick highest market value in Chaos (Very Rare Unique = 500, Rare Unique = 100)
                for (int i = 0; i < rowCandidates.Count; i++)
                {
                    if (rowCandidates[i].score > bestScore)
                    {
                        bestScore = rowCandidates[i].score;
                        bestIdx = i;
                    }
                }

                for (int i = 0; i < rowCandidates.Count; i++)
                {
                    var cand = rowCandidates[i];
                    // Only mark as recommended pick if it has meaningful value (>= 1.0 Chaos)
                    bool isBest = (i == bestIdx && bestScore >= 1.0);
                    var text = isBest ? $"[PICK] {cand.chip}" : cand.chip;
                    var chipPos = new Vector2(
                        cand.pos.X + cand.size.X + 8f + this.Settings.RuneshapeUiOffsetX,
                        cand.pos.Y + (cand.size.Y - 20f) / 2f + this.Settings.RuneshapeUiOffsetY);

                    newRows.Add(new RuneshapeRowPriceLabel(chipPos, text, cand.isHigh, isBest, cand.pos, cand.size));
                }
            }

            this.cachedRuneshapeRows = newRows;
        }

        private static void ParseRuneforgeRowText(string raw, out int count, out string name)
        {
            count = 1;
            name = raw.Trim();

            var match = Regex.Match(name, @"^(\d+)[xX]\s*(.+)$");
            if (match.Success)
            {
                if (int.TryParse(match.Groups[1].Value, out var c)) count = Math.Max(1, c);
                name = match.Groups[2].Value.Trim();
            }
        }

        private static void DrawRuneforgePriceChip(ImDrawListPtr fg, Vector2 pos, string text, bool isHigh, bool isBest = false)
        {
            var textSize = ImGui.CalcTextSize(text);
            var pad = new Vector2(8f, 4f);
            var min = pos;
            var max = new Vector2(pos.X + textSize.X + pad.X * 2, pos.Y + textSize.Y + pad.Y * 2);

            var bg = isBest ? 0xF02A1E05u : (isHigh ? 0xF0201505u : 0xF0101010u);
            var border = isBest ? 0xFFFFD700u : (isHigh ? 0xFF40E040u : 0xFF888899u);
            var textColor = isBest ? 0xFF50FF50u : (isHigh ? 0xFF50FF50u : 0xFFFFFFFFu);

            fg.AddRectFilled(min, max, bg, 4f);
            fg.AddRect(min, max, border, 4f, ImDrawFlags.None, isBest ? 2.0f : 1.2f);
            fg.AddText(new Vector2(min.X + pad.X, min.Y + pad.Y), textColor, text);
        }
    }
}
