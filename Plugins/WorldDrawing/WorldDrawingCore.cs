// <copyright file="WorldDrawingCore.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace WorldDrawing
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Numerics;
    using System.Text.Json;
    using Coroutine;
    using GameHelper;
    using GameHelper.CoroutineEvents;
    using GameHelper.Plugin;
    using GameHelper.RemoteEnums;
    using GameHelper.RemoteEnums.Entity;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.Utils;
    using ImGuiNET;


    /// <summary>
    /// <see cref="WorldDrawingCore"/> plugin.
    /// </summary>
    public sealed class WorldDrawingCore : PCore<WorldDrawingSettings>
    {
        private string SettingPathname => Path.Join(this.DllDirectory, "config", "settings.txt");
        private ActiveCoroutine onAreaChangeCoroutine;

        /// <inheritdoc/>
        public override void DrawSettings()
        {
        }

        /// <inheritdoc/>
        public override void DrawUI()
        {
            if (Core.States.InGameStateObject.GameUi.SkillTreeNodesUiElements.Count > 0)
            {
                return;
            }
        }

        /// <inheritdoc/>
        public override void OnDisable()
        {
            this.onAreaChangeCoroutine?.Cancel();
            this.onAreaChangeCoroutine = null;
        }

        /// <inheritdoc/>
        public override void OnEnable(bool isGameOpened)
        {
            if (File.Exists(this.SettingPathname))
            {
                var content = File.ReadAllText(this.SettingPathname);
                this.Settings = JsonSerializer.Deserialize(
                    content,
                    WorldDrawingJsonContext.Default.WorldDrawingSettings) ?? new WorldDrawingSettings();
            }

            this.onAreaChangeCoroutine = CoroutineHandler.Start(this.onAreaChange());
        }

        /// <inheritdoc/>
        public override void SaveSettings()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(this.SettingPathname));
            var settingsData = JsonSerializer.Serialize(
                this.Settings,
                WorldDrawingJsonContext.Default.WorldDrawingSettings);
            File.WriteAllText(this.SettingPathname, settingsData);
        }

        private IEnumerable<Wait> onAreaChange()
        {
            while (true)
            {
                yield return new Wait(RemoteEvents.AreaChanged);
                this.ClearAll();
            }
        }

        private void ClearAll()
        {
        }
    }
}
