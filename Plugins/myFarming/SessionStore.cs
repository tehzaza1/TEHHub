namespace myFarming
{
    using System;
    using System.IO;
    using System.Text.Encodings.Web;
    using System.Text.Json;
    using TEHhub.Plugin;

    public sealed class SessionStore
    {
        private readonly string configDir;
        private readonly string sessionsDir;
        public FarmSession Current { get; private set; } = new();

        public static string TodayKey => DateTime.Now.ToString("yyyy-MM-dd");

        public SessionStore(string configDir)
        {
            this.configDir = configDir;
            this.sessionsDir = Path.Combine(configDir, "sessions");
            Directory.CreateDirectory(this.sessionsDir);
            this.EnsureTodaySession();
        }

        public void EnsureTodaySession()
        {
            string today = TodayKey;
            if (this.Current != null && this.Current.DateKey == today)
            {
                return;
            }

            // If switching from another date, save the previous date's file first
            if (this.Current != null && !string.IsNullOrEmpty(this.Current.DateKey) && this.Current.DateKey != today)
            {
                this.SaveDailyFile(this.Current);
            }

            // Load today's session if it exists in sessions directory
            string todayPath = Path.Combine(this.sessionsDir, $"session_{today}.json");
            if (File.Exists(todayPath))
            {
                try
                {
                    var json = File.ReadAllText(todayPath);
                    this.Current = JsonSerializer.Deserialize<FarmSession>(json) ?? new FarmSession { DateKey = today };
                    PluginLog.Info("myFarming", $"Resumed daily session for {today} ({this.Current.TotalMaps} maps, {this.Current.TotalChaos:F1}c).");
                    return;
                }
                catch (Exception ex)
                {
                    PluginLog.Error("myFarming", $"Failed to load daily session from {todayPath}: {ex.Message}");
                }
            }

            // Check legacy session.json if it belongs to today
            string legacyPath = Path.Combine(this.configDir, "session.json");
            if (File.Exists(legacyPath))
            {
                try
                {
                    var json = File.ReadAllText(legacyPath);
                    var legacy = JsonSerializer.Deserialize<FarmSession>(json);
                    if (legacy != null && legacy.DateKey == today)
                    {
                        this.Current = legacy;
                        this.Save();
                        return;
                    }
                }
                catch
                {
                    // Fallback to new session
                }
            }

            // Create new daily session for today
            this.Current = new FarmSession
            {
                DateKey = today,
                StartedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };
            this.Save();
        }

        public void Save()
        {
            if (this.Current == null) return;
            this.SaveDailyFile(this.Current);

            // Also maintain legacy session.json for backwards compatibility
            try
            {
                string legacyPath = Path.Combine(this.configDir, "session.json");
                var json = JsonSerializer.Serialize(this.Current, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });
                File.WriteAllText(legacyPath, json);
            }
            catch (Exception ex)
            {
                PluginLog.Error("myFarming", $"Failed to save session.json: {ex.Message}");
            }
        }

        private void SaveDailyFile(FarmSession session)
        {
            try
            {
                if (string.IsNullOrEmpty(session.DateKey))
                {
                    session.DateKey = TodayKey;
                }

                string filePath = Path.Combine(this.sessionsDir, $"session_{session.DateKey}.json");
                var json = JsonSerializer.Serialize(session, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });
                File.WriteAllText(filePath, json);
            }
            catch (Exception ex)
            {
                PluginLog.Error("myFarming", $"Failed to save daily session file: {ex.Message}");
            }
        }

        public void Reset()
        {
            string today = TodayKey;
            this.Current = new FarmSession
            {
                DateKey = today,
                StartedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };
            this.Save();
        }
    }
}
