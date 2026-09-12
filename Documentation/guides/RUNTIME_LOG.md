# Runtime log viewer

Click **Log** at the bottom of the Settings sidebar to open a separate window. It stays open when Settings closes. Close it with the window X.

The viewer shows timestamps, source names, Info/Warning/Error filters, text search, auto-scroll, clear view and copy filtered logs. Clear view does not delete saved files. The in-memory history retains the latest 1,000 entries and bounds each message to 16,000 characters.

Session files are written asynchronously beside the running executable in `logs/runtime-*.log`. Each session rotates through three files of approximately 5 MiB each. Startup keeps the newest nine previous files before creating the current session files. Queue overflow and disk write errors appear in the viewer; logging cannot guarantee delivery when the bounded queue is full or the process is terminated abruptly.

Existing Console output is captured. Console.Error is Error; Console.Out is normally Info. Recognizable English warning/failure markers in legacy console text infer severity, and a leading `[PluginName]` supplies the source. For exact levels and names, plugins should use the public API:

```csharp
using TEHhub.Plugin;

PluginLog.Info("MyPlugin", "Loaded settings.");
PluginLog.Warning("MyPlugin", "Optional data is unavailable.");
PluginLog.Error("MyPlugin", "Unable to read the required component.");
```

Plugin load, reload, settings-save and DrawUI exceptions caught by PManager are recorded with their plugin names. Render exceptions and unhandled managed exceptions are also recorded; the existing Error.log crash fallback remains. A failure that is silently handled without any message is not automatically detected by console capture. Memory read failures still use Memory Diagnostics/OffsetHelper; absence of an error log is not proof that every offset is correct.

Validation:

```
dotnet run --project tests/OffsetRecovery.Tests/OffsetRecovery.Tests.csproj -c Release -- --log-smoke
```

This checks real console capture, inferred plugin severity, structured plugin warnings and background file persistence with synthetic messages. It does not attach to the game.
