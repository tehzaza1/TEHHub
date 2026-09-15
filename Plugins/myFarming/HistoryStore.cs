namespace myFarming
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Encodings.Web;
    using System.Text.Json;
    using TEHhub.Plugin;

    public sealed class HistoryStore
    {
        private readonly string filePath;
        public List<MapRun> Runs { get; private set; } = new();

        public HistoryStore(string configDirectory)
        {
            this.filePath = Path.Combine(configDirectory, "history.json");
            this.Load();
        }

        public void AddRun(MapRun run)
        {
            this.Runs.Insert(0, run);
            // Cap history to 500 runs
            if (this.Runs.Count > 500)
            {
                this.Runs.RemoveRange(500, this.Runs.Count - 500);
            }
            this.Save();
        }

        public void DeleteRun(string id)
        {
            this.Runs.RemoveAll(r => r.Id == id);
            this.Save();
        }

        public void ClearSession(int sessionId)
        {
            this.Runs.RemoveAll(r => r.SessionId == sessionId);
            this.Save();
        }

        public void ClearAll()
        {
            this.Runs.Clear();
            this.Save();
        }

        public void Load()
        {
            if (!File.Exists(this.filePath)) return;

            try
            {
                var json = File.ReadAllText(this.filePath);
                var list = JsonSerializer.Deserialize<List<MapRun>>(json);
                if (list != null)
                {
                    this.Runs = list;
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error("myFarming", $"Failed to load history: {ex.Message}");
            }
        }

        public void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(this.filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(this.Runs, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });
                File.WriteAllText(this.filePath, json);
            }
            catch (Exception ex)
            {
                PluginLog.Error("myFarming", $"Failed to save history: {ex.Message}");
            }
        }
    }
}
