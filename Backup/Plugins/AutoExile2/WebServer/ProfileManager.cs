// <copyright file="ProfileManager.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.WebServer
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Text.Json;

    /// <summary>
    /// Manages multiple named build profiles for AutoExile 2.
    /// Stores individual profiles under Plugins/AutoExile2/Profiles/{Name}.json
    /// and tracks the active profile in Plugins/AutoExile2/meta.json.
    /// </summary>
    public class ProfileManager
    {
        private const string DefaultProfileName = "Default";

        private string pluginDir = string.Empty;
        private string profilesDir = string.Empty;
        private string metaPath = string.Empty;
        private string legacySettingsPath = string.Empty;
        private readonly Action<string>? logger;

        public ProfileManager(Action<string>? logger = null)
        {
            this.logger = logger;
        }

        public string ActiveProfileName { get; private set; } = DefaultProfileName;

        public event Action<string>? OnProfileSwitched;

        public void Initialize(string pluginDirectory)
        {
            this.pluginDir = pluginDirectory;
            this.profilesDir = Path.Combine(pluginDirectory, "Profiles");
            this.metaPath = Path.Combine(pluginDirectory, "meta.json");
            this.legacySettingsPath = Path.Combine(pluginDirectory, "config", "settings.txt");

            Directory.CreateDirectory(this.profilesDir);
        }

        public List<string> ListProfiles()
        {
            if (!Directory.Exists(this.profilesDir)) return new List<string> { DefaultProfileName };

            var list = Directory.GetFiles(this.profilesDir, "*.json")
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (list.Count == 0)
            {
                list.Add(DefaultProfileName);
            }

            return list;
        }

        public bool ProfileExists(string name)
        {
            var clean = Sanitize(name);
            return !string.IsNullOrWhiteSpace(clean) && File.Exists(this.PathFor(clean));
        }

        public AutoExile2Settings LoadActive(AutoExile2Settings fallback)
        {
            var activeName = this.ReadMetaActiveProfile();

            if (!string.IsNullOrWhiteSpace(activeName) && this.ProfileExists(activeName))
            {
                var loaded = this.LoadProfileFile(activeName);
                if (loaded != null)
                {
                    this.ActiveProfileName = activeName;
                    this.Log($"Loaded active profile: {activeName}");
                    return loaded;
                }
            }

            // Ensure a pristine factory Default profile always exists
            if (!this.ProfileExists(DefaultProfileName))
            {
                var pristineDefault = new AutoExile2Settings();
                this.WriteProfileFile(pristineDefault, DefaultProfileName);
                this.Log($"Created pristine clean factory profile: {DefaultProfileName}");
            }

            // If no active profile specified, check legacy settings.txt to migrate as "Custom"
            if (File.Exists(this.legacySettingsPath))
            {
                try
                {
                    var text = File.ReadAllText(this.legacySettingsPath);
                    var userSettings = JsonSerializer.Deserialize<AutoExile2Settings>(text, AutoExileJson.Options);
                    if (userSettings != null)
                    {
                        const string customProfileName = "Custom";
                        if (!this.ProfileExists(customProfileName))
                        {
                            this.WriteProfileFile(userSettings, customProfileName);
                            this.Log($"Saved existing user settings to '{customProfileName}' profile");
                        }
                        this.ActiveProfileName = customProfileName;
                        this.WriteMeta(customProfileName);
                        return userSettings;
                    }
                }
                catch (Exception ex)
                {
                    this.Log($"Legacy migration error: {ex.Message}");
                }
            }

            // Fallback: use pristine Default profile
            this.ActiveProfileName = DefaultProfileName;
            this.WriteMeta(DefaultProfileName);
            return this.LoadProfileFile(DefaultProfileName) ?? new AutoExile2Settings();
        }

        public void SaveActive(AutoExile2Settings settings)
        {
            try
            {
                this.WriteProfileFile(settings, this.ActiveProfileName);
                this.WriteMeta(this.ActiveProfileName);

                // Also sync to legacy settings.txt for GameHelper standard plugin persistence
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(this.legacySettingsPath) ?? string.Empty);
                    File.WriteAllText(this.legacySettingsPath, JsonSerializer.Serialize(settings, AutoExileJson.Options));
                }
                catch { }
            }
            catch (Exception ex)
            {
                this.Log($"Failed to save active profile '{this.ActiveProfileName}': {ex.Message}");
            }
        }

        public bool SwitchProfile(ref AutoExile2Settings currentSettings, string name)
        {
            var clean = Sanitize(name);
            if (string.IsNullOrWhiteSpace(clean)) return false;
            if (clean.Equals(this.ActiveProfileName, StringComparison.OrdinalIgnoreCase)) return true;

            if (!this.ProfileExists(clean))
            {
                this.Log($"Cannot switch — profile not found: {clean}");
                return false;
            }

            // Flush current settings first
            this.SaveActive(currentSettings);

            var loaded = this.LoadProfileFile(clean);
            if (loaded == null) return false;

            // Copy loaded values into currentSettings reference
            CopySettings(loaded, currentSettings);
            this.ActiveProfileName = clean;
            this.WriteMeta(clean);
            this.Log($"Switched to profile: {clean}");
            this.OnProfileSwitched?.Invoke(clean);
            return true;
        }

        public bool CreateProfile(AutoExile2Settings currentSettings, string name, bool switchTo)
        {
            var clean = Sanitize(name);
            if (string.IsNullOrWhiteSpace(clean)) return false;
            if (this.ProfileExists(clean))
            {
                this.Log($"Cannot create — profile already exists: {clean}");
                return false;
            }

            try
            {
                this.WriteProfileFile(currentSettings, clean);
                if (switchTo)
                {
                    this.ActiveProfileName = clean;
                    this.WriteMeta(clean);
                }

                this.Log($"Profile created: {clean}{(switchTo ? " (active)" : "")}");
                return true;
            }
            catch (Exception ex)
            {
                this.Log($"Create profile failed: {ex.Message}");
                return false;
            }
        }

        public bool RenameProfile(string from, string to)
        {
            var cleanFrom = Sanitize(from);
            var cleanTo = Sanitize(to);
            if (string.IsNullOrWhiteSpace(cleanFrom) || string.IsNullOrWhiteSpace(cleanTo)) return false;
            if (!this.ProfileExists(cleanFrom) || this.ProfileExists(cleanTo)) return false;

            try
            {
                var src = this.PathFor(cleanFrom);
                var dst = this.PathFor(cleanTo);
                File.Move(src, dst);

                if (this.ActiveProfileName.Equals(cleanFrom, StringComparison.OrdinalIgnoreCase))
                {
                    this.ActiveProfileName = cleanTo;
                    this.WriteMeta(cleanTo);
                }

                this.Log($"Renamed profile: {cleanFrom} -> {cleanTo}");
                return true;
            }
            catch (Exception ex)
            {
                this.Log($"Rename profile failed: {ex.Message}");
                return false;
            }
        }

        public bool DeleteProfile(string name)
        {
            var clean = Sanitize(name);
            if (string.IsNullOrWhiteSpace(clean)) return false;

            // Cannot delete active profile
            if (clean.Equals(this.ActiveProfileName, StringComparison.OrdinalIgnoreCase))
            {
                this.Log($"Cannot delete active profile: {clean}");
                return false;
            }

            if (!this.ProfileExists(clean)) return false;

            try
            {
                File.Delete(this.PathFor(clean));
                this.Log($"Deleted profile: {clean}");
                return true;
            }
            catch (Exception ex)
            {
                this.Log($"Delete profile failed: {ex.Message}");
                return false;
            }
        }

        public string? ExportProfile(string name)
        {
            var clean = Sanitize(name);
            if (!this.ProfileExists(clean)) return null;

            try
            {
                return File.ReadAllText(this.PathFor(clean));
            }
            catch (Exception ex)
            {
                this.Log($"Export profile failed: {ex.Message}");
                return null;
            }
        }

        public bool ImportProfile(string name, string jsonContent)
        {
            var clean = Sanitize(name);
            if (string.IsNullOrWhiteSpace(clean)) return false;

            try
            {
                // Validate json deserializes into AutoExile2Settings
                var parsed = JsonSerializer.Deserialize<AutoExile2Settings>(jsonContent, AutoExileJson.Options);
                if (parsed == null) return false;

                this.WriteProfileFile(parsed, clean);
                this.Log($"Imported profile: {clean}");
                return true;
            }
            catch (Exception ex)
            {
                this.Log($"Import profile failed: {ex.Message}");
                return false;
            }
        }

        private static void CopySettings(AutoExile2Settings src, AutoExile2Settings dst)
        {
            foreach (var field in typeof(AutoExile2Settings).GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                field.SetValue(dst, field.GetValue(src));
            }

            foreach (var property in typeof(AutoExile2Settings).GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (property.CanRead && property.CanWrite)
                {
                    property.SetValue(dst, property.GetValue(src));
                }
            }
            dst.Skills = new List<SkillSlotConfig>(src.Skills ?? new List<SkillSlotConfig>());
            dst.P1Skills = new List<SkillSlotConfig>(src.P1Skills ?? new List<SkillSlotConfig>());
            dst.P2Skills = new List<SkillSlotConfig>(src.P2Skills ?? new List<SkillSlotConfig>());
        }

        private string PathFor(string name) => Path.Combine(this.profilesDir, $"{name}.json");

        private static string Sanitize(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return string.Concat(name.Where(c => !invalid.Contains(c))).Trim();
        }

        private AutoExile2Settings? LoadProfileFile(string name)
        {
            try
            {
                var path = this.PathFor(name);
                if (!File.Exists(path)) return null;
                var text = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<AutoExile2Settings>(text, AutoExileJson.Options);
                if (loaded != null)
                {
                    loaded.Skills ??= new List<SkillSlotConfig>();
                    loaded.P1Skills ??= new List<SkillSlotConfig>();
                    loaded.P2Skills ??= new List<SkillSlotConfig>();
                }
                return loaded;
            }
            catch (Exception ex)
            {
                this.Log($"Error reading profile '{name}': {ex.Message}");
                return null;
            }
        }

        private void WriteProfileFile(AutoExile2Settings settings, string name)
        {
            Directory.CreateDirectory(this.profilesDir);
            var path = this.PathFor(name);
            var json = JsonSerializer.Serialize(settings, AutoExileJson.Options);
            File.WriteAllText(path, json);
        }

        private string ReadMetaActiveProfile()
        {
            try
            {
                if (!File.Exists(this.metaPath)) return string.Empty;
                var text = File.ReadAllText(this.metaPath);
                var meta = JsonSerializer.Deserialize<Dictionary<string, string>>(text, AutoExileJson.Options);
                if (meta != null && meta.TryGetValue("activeProfile", out var p) && !string.IsNullOrWhiteSpace(p))
                {
                    return p;
                }
            }
            catch { }

            return string.Empty;
        }

        private void WriteMeta(string activeProfile)
        {
            try
            {
                var dict = new Dictionary<string, object>
                {
                    ["activeProfile"] = activeProfile,
                    ["lastSaved"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                };
                File.WriteAllText(this.metaPath, JsonSerializer.Serialize(dict, AutoExileJson.Options));
            }
            catch { }
        }

        private void Log(string msg)
        {
            this.logger?.Invoke($"[ProfileManager] {msg}");
        }
    }
}
