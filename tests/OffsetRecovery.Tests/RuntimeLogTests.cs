using TEHhub.Ui;

internal static class RuntimeLogTests
{
    internal static void Run(Action<bool, string> check)
    {
        RuntimeLog.Clear();
        for (var i = 0; i < 1100; i++) RuntimeLog.Write(RuntimeLogLevel.Info, "test", "entry " + i);
        var snapshot = RuntimeLog.Snapshot();
        check(snapshot.Length == 1000 && snapshot[0].Message == "entry 100" && snapshot[^1].Message == "entry 1099", "log keeps newest bounded entries");
        RuntimeLog.Write(RuntimeLogLevel.Error, "plugin", new string('x', 20000));
        check(RuntimeLog.Snapshot()[^1].Message.Length < 16100, "log bounds exception message size");
        RuntimeLog.Clear();
        check(RuntimeLog.Snapshot().Length == 0, "clear log view removes history");
    }

    internal static void Smoke()
    {
        RuntimeLog.Initialize();
        try
        {
            Console.Write("split ");
            Console.WriteLine("message");
            Console.Error.WriteLine("synthetic error");
            Console.WriteLine("[Radar] Warning: synthetic warning");
            Console.WriteLine("[LootValue] Failed to load synthetic fixture");
            TEHhub.Plugin.PluginLog.Warning("TestPlugin", "structured warning");
            var entries = RuntimeLog.Snapshot();
            if (!entries.Any(e => e.Message == "split message" && e.Level == RuntimeLogLevel.Info)
                || !entries.Any(e => e.Message == "synthetic error" && e.Level == RuntimeLogLevel.Error))
                throw new Exception("Console capture failed.");
            if (!entries.Any(e => e.Source == "Radar" && e.Level == RuntimeLogLevel.Warning)
                || !entries.Any(e => e.Source == "LootValue" && e.Level == RuntimeLogLevel.Error)
                || !entries.Any(e => e.Source == "TestPlugin" && e.Message == "structured warning" && e.Level == RuntimeLogLevel.Warning))
                throw new Exception("Plugin severity/source capture failed.");
        }
        finally { RuntimeLog.Stop(); }
        if (!Directory.GetFiles(RuntimeLog.DirectoryPath, "runtime-*.log").Any(p => File.ReadAllText(p).Contains("synthetic error")))
            throw new Exception("Background log persistence failed.");
        Console.WriteLine("PASS: console capture, severity and background file persistence.");
    }
}
