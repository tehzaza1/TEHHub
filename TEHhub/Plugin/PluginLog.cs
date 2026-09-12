namespace TEHhub.Plugin;

using TEHhub.Ui;

/// <summary>Plugins report structured messages to the built-in log viewer and session log files.</summary>
public static class PluginLog
{
    public static void Info(string pluginName, string message) => RuntimeLog.Write(RuntimeLogLevel.Info, pluginName, message);
    public static void Warning(string pluginName, string message) => RuntimeLog.Write(RuntimeLogLevel.Warning, pluginName, message);
    public static void Error(string pluginName, string message) => RuntimeLog.Write(RuntimeLogLevel.Error, pluginName, message);
}
