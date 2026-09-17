#if DEBUG
namespace TEHhub.Ui;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Collections.Concurrent;
using TEHhub.Plugin;
using TEHhub.RemoteObjects.UiElement;

internal static class ToolHub
{
    private static DateTime next;
    private static ToolHubSnapshot? snapshot;
    private static readonly ConcurrentDictionary<string, bool> WindowRequests = new();
    internal static ToolHubSnapshot? Snapshot => Volatile.Read(ref snapshot);
    internal static bool RequestWindow(string name, bool visible)
    {
        var field = Core.GHSettings.GetType().GetField(name);
        if (!name.StartsWith("Show", StringComparison.Ordinal) || field?.FieldType != typeof(bool)) return false;
        WindowRequests[name] = visible;
        return true;
    }

    internal static void Tick()
    {
        foreach (var request in WindowRequests)
            if (WindowRequests.TryRemove(request.Key, out var visible))
                Core.GHSettings.GetType().GetField(request.Key)!.SetValue(Core.GHSettings, visible);
        if (DateTime.UtcNow < next) return;
        next = DateTime.UtcNow.AddSeconds(1);
        try
        {
            var ui = Core.States.InGameStateObject.GameUi;
            var panels = new Dictionary<string, Dictionary<string, string>>();
            foreach (var property in ui.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length != 0 || !typeof(UiElementBase).IsAssignableFrom(property.PropertyType)) continue;
                if (property.GetValue(ui) is not UiElementBase panel) continue;
                panels[property.Name] = new() { ["address"] = panel.Address.ToString("X"),
                    ["visible"] = panel.IsVisible.ToString(), ["children"] = panel.TotalChildrens.ToString() };
            }
            var windows = Core.GHSettings.GetType().GetFields().Where(f => f.FieldType == typeof(bool) &&
                f.Name.StartsWith("Show", StringComparison.Ordinal))
                .ToDictionary(f => f.Name, f => (bool)f.GetValue(Core.GHSettings)!);
            PluginContainer[] containers;
            lock (PManager.Plugins) containers = PManager.Plugins.ToArray();
            var plugins = containers.Select(p => new PluginDiagnostic(p.Name, p.Metadata.Enable,
                p.Plugin.GetType().Assembly.GetName().Version?.ToString() ?? "unknown")).ToArray();
            var area = Core.States.InGameStateObject.CurrentAreaInstance;
            ToolDiagnostics.Publish("element-finder", ElementFinder.GetToolStatus());
            ToolDiagnostics.Publish("game-ui-explorer", GameUiExplorer.GetToolStatus());
            ToolDiagnostics.Publish("primary-root-research", PrimaryRootResearch.GetToolStatus());
            ToolDiagnostics.Publish("data-visualization", new Dictionary<string, string> {
                ["awakeEntities"] = area.AwakeEntities.Count.ToString(),
                ["sleepingEntities"] = area.SleepingEntities.Count.ToString(),
                ["playerAddress"] = area.Player.Address.ToString("X"), ["gameUiAddress"] = ui.Address.ToString("X") });
            Volatile.Write(ref snapshot, new(DateTime.UtcNow, Environment.ProcessId,
                typeof(Core).Assembly.GetName().Version?.ToString(3) ?? "unknown",
                Core.States.GameCurrentState.ToString(), area.AreaHash, windows, panels, plugins));
        }
        catch (Exception ex) { ToolDiagnostics.Publish("tool-hub", new Dictionary<string, string> { ["error"] = ex.Message }); }
    }
}

internal sealed record PluginDiagnostic(string Name, bool Enabled, string Version);
internal sealed record ToolHubSnapshot(DateTime UpdatedUtc, int ProcessId, string Version, string GameState,
    string AreaHash, Dictionary<string, bool> Windows, Dictionary<string, Dictionary<string, string>> UiPanels,
    PluginDiagnostic[] Plugins);

internal sealed record ToolApiCatalog(string BaseUrl, string[] Endpoints, string[] PublishedTools);

#endif
