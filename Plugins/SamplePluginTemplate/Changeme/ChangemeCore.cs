// <copyright file="ChangemeCore.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Changeme
{
    using System.IO;
    using System.Text.Json;
    using GameHelper.Plugin;

    /// <summary>
    /// <see cref="ChangemeCore"/> plugin.
    /// </summary>
    public sealed class ChangemeCore : PCore<ChangemeSettings>
    {
        // All user-created plugin files belong in configs/plugins/Changeme, never next to the DLL.
        private string SettingsPath => this.PluginConfigPath("settings.json");

        /// <inheritdoc/>
        public override void DrawSettings()
        {
        }

        /// <inheritdoc/>
        public override void DrawUI()
        {
        }

        /// <inheritdoc/>
        public override void OnDisable()
        {
        }

        /// <inheritdoc/>
        public override void OnEnable(bool isGameOpened)
        {
            if (File.Exists(this.SettingsPath))
            {
                this.Settings = JsonSerializer.Deserialize<ChangemeSettings>(File.ReadAllText(this.SettingsPath))
                    ?? new ChangemeSettings();
            }
        }

        /// <inheritdoc/>
        public override void SaveSettings()
        {
            Directory.CreateDirectory(this.PluginConfigDirectory);
            File.WriteAllText(this.SettingsPath, JsonSerializer.Serialize(this.Settings));
        }
    }
}
