namespace myFarming
{
    using System;
    using System.IO;
    using System.Text.Encodings.Web;
    using System.Text.Json;
    using TEHhub.Plugin;

    public sealed class SessionStore
    {
        private readonly string filePath;
        public FarmSession Current { get; private set; } = new();

        public SessionStore(string configDir)
        {
            this.filePath = Path.Combine(configDir, "session.json");
            this.Load();
        }

        public void Load()
        {
            if (File.Exists(this.filePath))
            {
                try
                {
                    var json = File.ReadAllText(this.filePath);
                    this.Current = JsonSerializer.Deserialize<FarmSession>(json) ?? new FarmSession();
                }
                catch (Exception ex)
                {
                    PluginLog.Error("myFarming", $"Failed to load session from {this.filePath}: {ex.Message}");
                    this.Current = new FarmSession();
                }
            }
            else
            {
                this.Current = new FarmSession();
            }
        }

        public void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(this.filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(this.Current, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });
                File.WriteAllText(this.filePath, json);
            }
            catch (Exception ex)
            {
                PluginLog.Error("myFarming", $"Failed to save session to {this.filePath}: {ex.Message}");
            }
        }

        public void Reset()
        {
            int nextId = (this.Current?.SessionId ?? 0) + 1;
            this.Current = new FarmSession
            {
                SessionId = nextId,
                StartedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };
            this.Save();
        }
    }
}
