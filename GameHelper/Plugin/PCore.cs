// <copyright file="PCore.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace GameHelper.Plugin
{
    using GameHelper.Localization;
    using System;
    using System.Collections.Generic;
    using System.IO;

    /// <summary>
    ///     Interface for creating plugins.
    /// </summary>
    /// <typeparam name="TSettings">plugin's setting class name.</typeparam>
    public abstract class PCore<TSettings> : IPCore
        where TSettings : IPSettings, new()
    {
        /// <summary>
        ///     Gets or sets the plugin root directory folder.
        /// </summary>
        public string DllDirectory = null!;

        /// <summary>
        ///     Gets or sets the plugin settings.
        /// </summary>
        public TSettings Settings = new();

        private PluginLocalization? pluginText;

        /// <summary>
        ///     Gets localized text from the plugin-owned Localization directory.
        /// </summary>
        protected PluginLocalization PluginText => this.pluginText ??= new PluginLocalization(this.DllDirectory);

        /// <inheritdoc />
        public virtual string GetDescription() => this.PluginText.T("plugin.description", string.Empty);

        /// <inheritdoc />
        public virtual IReadOnlyCollection<string> ConflictsWith => Array.Empty<string>();

        /// <inheritdoc />
        public virtual int ConflictPriority => 0;

        /// <inheritdoc />
        public abstract void OnDisable();

        /// <inheritdoc />
        public abstract void OnEnable(bool isGameOpened);

        /// <inheritdoc />
        public abstract void DrawSettings();

        /// <inheritdoc />
        public abstract void DrawUI();

        /// <inheritdoc />
        public abstract void SaveSettings();

        /// <inheritdoc />
        public void SetPluginDllLocation(string dllLocation)
        {
            this.DllDirectory = dllLocation;
        }

        /// <summary>
        ///     Gets the mutable configuration directory for this plugin. Plugin authors must keep
        ///     all user-created configuration and state files beneath this path, never beside the DLL.
        /// </summary>
        protected string PluginConfigDirectory
        {
            get
            {
                var pluginName = !string.IsNullOrWhiteSpace(this.DllDirectory)
                    ? new DirectoryInfo(this.DllDirectory).Name
                    : this.GetType().Assembly.GetName().Name ?? this.GetType().Name;
                return Path.Combine(AppContext.BaseDirectory, "configs", "plugins", pluginName);
            }
        }

        /// <summary>
        ///     Gets a mutable configuration path outside the plugin deployment directory.
        ///     Plugin updates can therefore replace DLLs and assets without overwriting user settings.
        ///     The first access migrates the matching legacy file from the old plugin-local config folder
        ///     (or plugin root for older mutable data files).
        /// </summary>
        /// <param name="fileName">The config file name, relative to the plugin config folder.</param>
        /// <returns>The centralized config path.</returns>
        protected string PluginConfigPath(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName) || Path.IsPathRooted(fileName))
            {
                throw new ArgumentException("A relative plugin config file name is required.", nameof(fileName));
            }

            var configDirectory = this.PluginConfigDirectory;
            var configPath = Path.Combine(configDirectory, fileName);
            var legacyConfigPath = string.IsNullOrWhiteSpace(this.DllDirectory)
                ? string.Empty
                : Path.Combine(this.DllDirectory, "config", fileName);
            var legacyRootPath = string.IsNullOrWhiteSpace(this.DllDirectory)
                ? string.Empty
                : Path.Combine(this.DllDirectory, fileName);
            var legacyPath = File.Exists(legacyConfigPath) ? legacyConfigPath : legacyRootPath;

            if (!File.Exists(configPath) && !string.IsNullOrEmpty(legacyPath) && File.Exists(legacyPath))
            {
                try
                {
                    Directory.CreateDirectory(configDirectory);
                    File.Move(legacyPath, configPath);
                }
                catch (IOException)
                {
                    return legacyPath;
                }
                catch (UnauthorizedAccessException)
                {
                    return legacyPath;
                }
            }

            return configPath;
        }
    }
}
