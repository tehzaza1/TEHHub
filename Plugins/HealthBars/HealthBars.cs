// <copyright file="HealthBars.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace HealthBars
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Numerics;
    using System.Text.Json;
    using Coroutine;
    using TEHhub;
    using TEHhub.CoroutineEvents;
    using TEHhub.Plugin;
    using TEHhub.RemoteEnums;
    using TEHhub.RemoteEnums.Entity;
    using TEHhub.RemoteObjects.Components;
    using TEHhub.RemoteObjects.States.InGameStateObjects;
    using TEHhub.Utils;
    using ImGuiNET;

    /// <summary>
    ///     <see cref="HealthBars" /> plugin.
    /// </summary>
    public sealed class HealthBars : PCore<HealthBarsSettings>
    {
        private readonly List<string> textureToValidate = new()
        {
            "full_bar.png",
            "hollow_bar.png"
        };

        private int poiMonsterConfigToDelete = 0;
        private int poiMonsterConfigToAdd = 0;
        private float graduationsThickness = 0f;
        private Vector2 fontSize = Vector2.Zero;

        private string SettingPathname => this.PluginConfigPath("settings.txt");

        private string TexturesPath => Path.IsPathRooted(this.DllDirectory)
            ? Path.Join(this.DllDirectory, "Textures")
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, this.DllDirectory, "Textures"));

        private readonly TextureLoader textures = new();

        private readonly Dictionary<uint, Vector2> bPositions = new();

        private ActiveCoroutine? onAreaChange = null;

        /// <inheritdoc />
        public override void DrawSettings()
        {
            ImGui.Text(this.PluginText.T("settings.turn_off_game_health_bars", "Turn off in game health bars for best result."));
            ImGui.Text(this.PluginText.T("settings.reload_textures_hint", "Enable/Disable plugin to reload textures."));
            ImGui.Text(this.PluginText.F("settings.total_textures_loaded", "Total Textures loaded: {0}", this.textures.TotalTexturesLoaded));
            if (ImGui.CollapsingHeader(this.PluginText.Title("section.common_configuration", "Common Configuration", "HealthBarsCommonConfiguration")))
            {
                if (ImGui.BeginTable("common_config_table", 2))
                {
                    ImGui.TableNextColumn();
                    ImGui.Checkbox(this.PluginText.Label("settings.draw_in_town", "Draw healthbars in town", "HealthBarsDrawInTown"), ref this.Settings.DrawInTown);
                    ImGui.TableNextColumn();
                    ImGui.Checkbox(this.PluginText.Label("settings.draw_in_hideout", "Draw healthbars in hideout", "HealthBarsDrawInHideout"), ref this.Settings.DrawInHideout);
                    ImGui.TableNextColumn();
                    ImGui.Checkbox(this.PluginText.Label("settings.draw_in_background", "Draw healthbars when game is in background", "HealthBarsDrawInBackground"), ref this.Settings.DrawWhenGameInBackground);
                    ImGui.TableNextColumn();
                    ImGui.Checkbox(this.PluginText.Label("settings.interpolate_position", "Interpolate position", "HealthBarsInterpolatePosition"), ref this.Settings.InterpolatePosition);
                    ImGuiHelper.ToolTip(this.PluginText.T("settings.interpolate_position.tooltip", "Enable this if your healthbar is stuttering."));
                    if (this.Settings.InterpolatePosition)
                    {
                        if (ImGui.DragInt(this.PluginText.Label("settings.interpolation_rate", "Interpolation Rate", "HealthBarsInterpolationRate"), ref this.Settings.InterpolationRate, 1f, 1, 1000))
                        {
                            if (this.Settings.InterpolationRate <= 0)
                            {
                                this.Settings.InterpolationRate = 1;
                            }
                            else if (this.Settings.InterpolationRate >= 1000)
                            {
                                this.Settings.InterpolationRate = 1000;
                            }
                        }
                    }

                    ImGui.TableNextColumn();
                    ImGui.Text(this.PluginText.T("settings.rarity_row", "white       magic      rare         unique"));
                    ImGui.DragInt4(this.PluginText.Label("settings.cull_strike_percent", "Cull Strike (%health)", "HealthBarsCullStrikePercent"), ref this.Settings.CullingStrikeRangePerRarity[0], 1, 0, 100);
                    ImGui.TableNextColumn();
                    ImGui.Checkbox(this.PluginText.Label("settings.show_mana_instead_of_es", "Show mana rather than ES on self player", "HealthBarsShowManaInsteadOfES"), ref this.Settings.ShowManaRatherThanESOnSelf);
                    ImGui.TableNextColumn();
                    ImGui.Checkbox(this.PluginText.Label("settings.show_runic_ward_bar", "Show Runic Ward bar", "HealthBarsShowRunicWardBar"), ref this.Settings.ShowRunicWardBar);
                    ImGui.TableNextColumn();
                    ImGui.Checkbox(this.PluginText.Label("settings.show_runic_ward_text", "Show Runic Ward value", "HealthBarsShowRunicWardText"), ref this.Settings.ShowRunicWardText);
                    ImGui.TableNextColumn();
                    ImGui.ColorEdit4(this.PluginText.Label("settings.runic_ward_color", "Runic Ward color", "HealthBarsRunicWardColor"), ref this.Settings.RunicWardColor);
                    ImGui.EndTable();
                }
            }

            if (ImGui.CollapsingHeader(this.PluginText.Title("section.monster_configuration", "Monster Configuration", "HealthBarsMonsterConfiguration")))
            {
                if (ImGui.BeginTabBar("monster_config"))
                {
                    foreach (var item in this.Settings.Monster)
                    {
                        if (ImGui.BeginTabItem(this.PluginText.Title($"tab.monster.{item.Key}", item.Key, $"HealthBarsMonster{item.Key}")))
                        {
                            item.Value.Draw(this.PluginText);
                            ImGui.EndTabItem();
                        }
                    }

                    ImGui.EndTabBar();
                }
            }

            if (ImGui.CollapsingHeader(this.PluginText.Title("section.poi_configuration", "POI Configuration", "HealthBarsPoiConfiguration")))
            {
                ImGui.SetNextItemWidth(ImGui.GetFontSize() * 10);
                if(ImGui.InputInt(this.PluginText.Label("settings.group_number", "Group Number", "poimonsterconfig"), ref this.poiMonsterConfigToAdd) && this.poiMonsterConfigToAdd < 0)
                {
                    this.poiMonsterConfigToAdd = 0;
                }

                ImGui.SameLine();
                if (ImGui.Button(this.PluginText.Label("button.add", "Add", "HealthBarsAddPoiGroup")))
                {
                    this.Settings.POIMonster.TryAdd(this.poiMonsterConfigToAdd, new());
                }

                if (ImGui.BeginTabBar("poimonster_config", ImGuiTabBarFlags.AutoSelectNewTabs))
                {
                    foreach (var conf in this.Settings.POIMonster)
                    {
                        var text = conf.Key < 0
                            ? this.PluginText.Title("tab.default", "Default", "HealthBarsPoiDefault")
                            : $"{this.PluginText.F("tab.group", "Group {0}", conf.Key)}###HealthBarsPoiGroup{conf.Key}";
                        var shouldNotDelete = true;
                        if (ImGui.BeginTabItem(text, ref shouldNotDelete, ImGuiTabItemFlags.NoAssumedClosure))
                        {
                            conf.Value.Draw(this.PluginText);
                            ImGui.EndTabItem();
                        }

                        if (conf.Key >= 0 && !shouldNotDelete)
                        {
                            this.poiMonsterConfigToDelete = conf.Key;
                            ImGui.OpenPopup("POIConfigHealthbarDeleteConfirmation");
                        }
                    }

                    this.DrawConfirmationPopup();
                    ImGui.EndTabBar();
                }
            }

            if (ImGui.CollapsingHeader(this.PluginText.Title("section.player_configuration", "Player Configuration", "HealthBarsPlayerConfiguration")))
            {
                if (ImGui.BeginTabBar("player_config"))
                {
                    foreach (var item in this.Settings.Player)
                    {
                        if (ImGui.BeginTabItem(this.PluginText.Title($"tab.player.{item.Key}", item.Key, $"HealthBarsPlayer{item.Key}")))
                        {
                            item.Value.Draw(this.PluginText);
                            ImGui.EndTabItem();
                        }
                    }

                    ImGui.EndTabBar();
                }
            }
        }

        /// <inheritdoc />
        public override void DrawUI()
        {
            var cAreaInstance = Core.States.InGameStateObject.CurrentAreaInstance;
            var cWorldInstance = Core.States.InGameStateObject.CurrentWorldInstance;
            if ((!this.Settings.DrawInTown && cWorldInstance.AreaDetails.IsTown) ||
                (!this.Settings.DrawInHideout && cWorldInstance.AreaDetails.IsHideout))
            {
                return;
            }

            if (Core.States.GameCurrentState != GameStateTypes.InGameState)
            {
                return;
            }

            if (!this.Settings.DrawWhenGameInBackground && !Core.Process.Foreground)
            {
                return;
            }

            if (Core.States.InGameStateObject.GameUi.IsAnyLargePanelOpen)
            {
                return;
            }

            this.UpdateOncePerDraw();
            foreach (var entity in cAreaInstance.AwakeEntities)
            {
                if (!entity.Value.IsValid || entity.Value.EntityState == EntityStates.Useless ||
                    entity.Value.EntityType == EntityTypes.Renderable ||
                    entity.Value.EntityState == EntityStates.PinnacleBossHidden)
                {
                    continue;
                }

                switch (entity.Value.EntityType)
                {
                    case EntityTypes.Player:
                        if (entity.Value.EntitySubtype == EntitySubtypes.PlayerOther)
                        {
                            if (entity.Value.EntityState == EntityStates.PlayerLeader)
                            {
                                this.DrawHealthbar(entity.Value, this.Settings.Player["leader"], (int)Rarity.Rare);
                            }
                            else
                            {
                                this.DrawHealthbar(entity.Value, this.Settings.Player["member"], (int)Rarity.Rare);
                            }
                        }
                        else
                        {
                            this.DrawHealthbar(entity.Value, this.Settings.Player["self"], (int)Rarity.Rare, true);
                        }

                        break;
                    case EntityTypes.Monster:
                        if (entity.Value.EntitySubtype == EntitySubtypes.POIMonster)
                        {
                            if (!this.Settings.POIMonster.TryGetValue(entity.Value.EntityCustomGroup, out var poiConfig))
                            {
                                poiConfig = this.Settings.POIMonster[-1];
                            }

                            this.DrawHealthbar(entity.Value, poiConfig,
                                entity.Value.TryGetComponent<ObjectMagicProperties>(out var oComp) ?
                                (int)oComp.Rarity :
                                (int)Rarity.Rare);
                        }
                        else if (entity.Value.EntityState == EntityStates.MonsterFriendly)
                        {
                            this.DrawHealthbar(entity.Value, this.Settings.Monster["friendly"], (int)Rarity.Rare);
                        }
                        else if (entity.Value.TryGetComponent<ObjectMagicProperties>(out var oComp))
                        {
                            switch (oComp.Rarity)
                            {
                                case Rarity.Normal:
                                    this.DrawHealthbar(entity.Value, this.Settings.Monster["white"], (int)Rarity.Normal);
                                    break;
                                case Rarity.Magic:
                                    this.DrawHealthbar(entity.Value, this.Settings.Monster["magic"], (int)Rarity.Magic);
                                    break;
                                case Rarity.Rare:
                                    this.DrawHealthbar(entity.Value, this.Settings.Monster["rare"], (int)Rarity.Rare);
                                    break;
                                case Rarity.Unique:
                                    this.DrawHealthbar(entity.Value, this.Settings.Monster["unique"], (int)Rarity.Unique);
                                    break;
                            }
                        }

                        break;
                }
            }
        }

        /// <inheritdoc />
        public override void OnDisable()
        {
            this.textures.cleanup(this.TexturesPath);
            this.onAreaChange?.Cancel();
            this.onAreaChange = null;
        }

        /// <inheritdoc />
        public override void OnEnable(bool isGameOpened)
        {
            this.textures.Load(this.TexturesPath);
            if (File.Exists(this.SettingPathname))
            {
                var content = File.ReadAllText(this.SettingPathname);
                this.Settings = JsonSerializer.Deserialize(
                    content,
                    HealthBarsJsonContext.Default.HealthBarsSettings) ?? new HealthBarsSettings();
            }

            for (var i = 0; i < this.textureToValidate.Count; i++)
            {
                if (!this.textures.TextureKeys.Contains(this.textureToValidate[i]))
                {
                    throw new Exception($"Missing texture file {this.textureToValidate[i]} in {this.TexturesPath} folder.");
                }
            }

            this.onAreaChange = CoroutineHandler.Start(this.OnAreaChange());
        }

        /// <inheritdoc />
        public override void SaveSettings()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(this.SettingPathname) ?? string.Empty);
            var settingsData = JsonSerializer.Serialize(
                this.Settings,
                HealthBarsJsonContext.Default.HealthBarsSettings);
            File.WriteAllText(this.SettingPathname, settingsData);
        }

        private void DrawHealthbar(Entity entity, Config healthbarConfig, int rarity, bool isSelf = false)
        {
            if (!healthbarConfig.Enable)
            {
                return;
            }

            if (!entity.TryGetComponent<Render>(out var rComp))
            {
                return;
            }

            var curPos = rComp.WorldPosition;
            curPos.Z -= rComp.ModelBounds.Z + healthbarConfig.Shift.Y;
            var location = Core.States.InGameStateObject.CurrentWorldInstance.WorldToScreen(curPos, curPos.Z);
            location.X += healthbarConfig.Shift.X;
            if (!entity.TryGetComponent<Life>(out var hComp))
            {
                return;
            }

            if (this.Settings.InterpolatePosition)
            {
                if (this.bPositions.TryGetValue(entity.Id, out var prevLocation))
                {
                    location = MathHelper.Lerp(prevLocation, location, this.Settings.InterpolationRate / 1000f);
                }

                this.bPositions[entity.Id] = location;
            }

            var ptr = ImGui.GetBackgroundDrawList();
            var start = location - healthbarConfig.HalfOfScale;
            var end = location + healthbarConfig.HalfOfScale;
            var secondary = isSelf && this.Settings.ShowManaRatherThanESOnSelf ? hComp.Mana : hComp.EnergyShield;
            var hasHealth = HasVisibleVital(hComp.Health);
            var hasSecondary = HasVisibleVital(secondary);
            var hasWard = !isSelf && this.Settings.ShowRunicWardBar && HasVisibleVital(hComp.Ward);

            if (!hasHealth && !hasSecondary && !hasWard)
            {
                return;
            }

            // Draw layers from back to front. As HP or ES is depleted, the layer below is
            // revealed in the same rectangle: Ward (purple) -> ES/Mana (blue) -> HP.
            this.DrawBarBackdrop(ptr, start, end, healthbarConfig.BackgroundColor);
            if (hasWard)
            {
                this.DrawVitalLayer(ptr, start, end, hComp.Ward, this.Settings.RunicWardColor, 0);
            }

            if (hasSecondary)
            {
                this.DrawVitalLayer(ptr, start, end, secondary, healthbarConfig.ESColor, 1);
            }

            if (hasHealth)
            {
                this.DrawVitalLayer(ptr, start, end, hComp.Health, healthbarConfig.HealthbarColor, 0,
                    healthbarConfig.ShowCullStrike, this.Settings.CullingStrikeRangePerRarity[rarity]);
            }

            this.DrawBarChrome(ptr, start, end, healthbarConfig.HealthbarColor);

            var tmp = start - Vector2.UnitY;
            for (var i = 0; i < healthbarConfig.Graduations; i++)
            {
                tmp.X += healthbarConfig.GraduationsLocationStart;
                ptr.AddLine(tmp, tmp + healthbarConfig.GraduationsLocationEnd, 0xFF000000, this.graduationsThickness);
            }

            if (healthbarConfig.ShowText)
            {
                ptr.AddText(start - this.fontSize, ImGuiHelper.Color(healthbarConfig.TextColor),
                    this.healthToHumanReadable(hComp.Health.Current + hComp.Ward.Current + hComp.EnergyShield.Current));
            }

            if (hasWard && this.Settings.ShowRunicWardText)
            {
                ptr.AddText(start + new Vector2(0f, end.Y - start.Y), ImGuiHelper.Color(this.Settings.RunicWardColor),
                    $"Ward {this.healthToHumanReadable(Math.Max(0, hComp.Ward.Current))}/{this.healthToHumanReadable(Math.Max(0, hComp.Ward.Unreserved))}");
            }
        }

        private void DrawVitalLayer(
            ImDrawListPtr drawList,
            Vector2 start,
            Vector2 end,
            TEHhub.Offsets.Objects.Components.VitalStruct vital,
            Vector4 color,
            int textureIndex,
            bool showCullStrike = false,
            int cullStrikeRange = 0)
        {
            var percent = VitalPercent(vital);
            if (percent <= 0)
            {
                return;
            }

            var (texture, _, _) = this.textures.GetTexture(this.textureToValidate[textureIndex]);
            var fillEnd = new Vector2(start.X + ((end.X - start.X) * percent / 100f), end.Y);
            var drawColor = showCullStrike && percent <= cullStrikeRange
                ? 0xFFFFFFFF
                : ImGuiHelper.Color(color);
            drawList.AddImage(texture, start, fillEnd, Vector2.Zero, Vector2.One, drawColor);
        }

        private void DrawBarBackdrop(ImDrawListPtr drawList, Vector2 start, Vector2 end, Vector4 backgroundColor)
        {
            // A tiny drop-shadow and a near-black outer edge give the stacked fills a clear
            // silhouette on bright terrain without changing their size or readability.
            var shadowOffset = new Vector2(1.5f, 1.5f);
            drawList.AddRectFilled(start + shadowOffset, end + shadowOffset, ImGuiHelper.Color(new Vector4(0f, 0f, 0f, 0.58f)));
            drawList.AddRectFilled(start - Vector2.One, end + Vector2.One, ImGuiHelper.Color(new Vector4(0.015f, 0.02f, 0.03f, 0.96f)));
            drawList.AddRectFilled(start, end, ImGuiHelper.Color(backgroundColor));
        }

        private void DrawBarChrome(ImDrawListPtr drawList, Vector2 start, Vector2 end, Vector4 healthColor)
        {
            // Keep the highlight very subtle: it makes a full bar look less flat, while the
            // HP/ES/Ward layer beneath remains obvious when the top resource is depleted.
            var topHighlight = new Vector4(
                Math.Min(1f, (healthColor.X * 0.55f) + 0.28f),
                Math.Min(1f, (healthColor.Y * 0.55f) + 0.28f),
                Math.Min(1f, (healthColor.Z * 0.55f) + 0.28f),
                0.42f);
            drawList.AddLine(start + new Vector2(1f, 0.5f), new Vector2(end.X - 1f, start.Y + 0.5f), ImGuiHelper.Color(topHighlight));
            drawList.AddRectFilled(
                new Vector2(start.X + 1f, start.Y + ((end.Y - start.Y) * 0.70f)),
                end - Vector2.One,
                ImGuiHelper.Color(new Vector4(0f, 0f, 0f, 0.12f)));
            drawList.AddRect(start, end, ImGuiHelper.Color(new Vector4(0.68f, 0.76f, 0.90f, 0.72f)), 0f, ImDrawFlags.None, 1.15f);
        }

        private void UpdateOncePerDraw()
        {
            this.graduationsThickness = ImGui.GetFontSize() / 9f;
            this.fontSize = new(0f, ImGui.GetFontSize());
        }

        private static bool HasVisibleVital(TEHhub.Offsets.Objects.Components.VitalStruct vital)
        {
            return vital.Total > 0 && vital.Unreserved > 0 && vital.Current > 0;
        }

        private static int VitalPercent(TEHhub.Offsets.Objects.Components.VitalStruct vital)
        {
            if (vital.Unreserved <= 0 || vital.Current <= 0)
            {
                return 0;
            }

            return Math.Clamp((int)Math.Round(100d * vital.Current / vital.Unreserved), 0, 100);
        }

        private string healthToHumanReadable(int value)
        {
            if (value >= 100000)
            {
                return $"{(value / 1000000f):0.00}M";

            }
            else if (value >= 100)
            {
                return $"{(value / 1000f):0.00}K";
            }
            else
            {
                return $"{value}";
            }
        }

        private void DrawConfirmationPopup()
        {
            ImGui.SetNextWindowPos(new Vector2(Core.Overlay.Size.Width / 3f, Core.Overlay.Size.Height / 3f));
            if (ImGui.BeginPopup("POIConfigHealthbarDeleteConfirmation"))
            {
                ImGui.Text(this.PluginText.F("popup.delete_poi_config", "Do you want to delete group {0} POI Monster healthbar config?", this.poiMonsterConfigToDelete));
                ImGui.Separator();
                if (ImGui.Button(this.PluginText.Label("button.yes", "Yes", "HealthBarsDeletePoiYes"),
                    new Vector2(ImGui.GetContentRegionAvail().X / 2f, ImGui.GetTextLineHeight() * 2)))
                {
                    _ = this.Settings.POIMonster.Remove(poiMonsterConfigToDelete);
                    ImGui.CloseCurrentPopup();
                }

                ImGui.SameLine();
                if (ImGui.Button(this.PluginText.Label("button.no", "No", "HealthBarsDeletePoiNo"), new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetTextLineHeight() * 2)))
                {
                    ImGui.CloseCurrentPopup();
                }

                ImGui.EndPopup();
            }
        }

        private IEnumerator<Wait> OnAreaChange()
        {
            while (true)
            {
                yield return new Wait(RemoteEvents.AreaChanged);
                this.bPositions.Clear();
            }
        }
    }
}
