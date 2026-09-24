# Repository guidance

This file combines the applicable repository guidance from `CLAUDE.md` with the user's agent-role preference below. Where `CLAUDE.md` uses retired project names, use the current TEHHub names and commands in this file. The old statement that the repository has no automated tests is stale: current tests include `tools/TEHhub.OffsetDoctor.Tests` and `tests/AutoExile2.Web.Tests`.



## What this is

TEHHub is a Windows x64 .NET 10 overlay for Path of Exile 2. It attaches to the running game process, reads game state from process memory, and renders an ImGui overlay through `ClickableTransparentOverlay`. Functionality is delivered through dynamically loaded plugins.

## Build and run

- Target `net10.0-windows`, `win-x64`, and `PlatformTarget=x64`. The solution platform may display as Any CPU while output remains x64. A .NET 10 SDK is required; the desktop runtime alone is insufficient.
- Always build the whole current solution, not just the core project. The launcher and plugin projects stage their DLLs and assets during the build. Use `dotnet build TEHhub.sln -c Release` or `dotnet build TEHhub.sln -c Debug`.
- If output files are locked and MSBuild reports `MSB3021` or `MSB3027`, do not terminate the running overlay. Check for actual compiler errors (`error CS`) separately from failed copy/staging steps, then report whether the change still needs a rebuild to take effect.
- The runtime output is `TEHhub\bin\<Configuration>\net10.0-windows\win-x64\`. Launch `TEHhub.Launcher.exe` from there. Run it with the same privilege level as the game so it can attach when the game is elevated.
- Root `NuGet.config` includes the `veldrid-prereleases` MyGet feed required by `ClickableTransparentOverlay`.
- Only projects listed in `TEHhub.sln` are part of the main build. `Plugins/Atlas2/Atlas2.sln` is a separate solution even though Atlas2 is also listed in the main solution. `Plugins/SamplePluginTemplate` and `Plugins/WorldDrawing` are in the repository but are not listed in `TEHhub.sln`.

## Architecture

- `TEHhub` contains the overlay executable and core runtime services; `TEHhub.Offsets` contains native layouts under `Objects/` and signature patterns and has no dependency on the overlay; `TEHhub.Launcher` finds, updates, and starts the overlay. Each `Plugins/*` project is a separate library referencing `TEHhub` with `<Private>false</Private>` so it does not copy the core DLLs. Plugin build targets stage the plugin DLL and assets under `win-x64\Plugins\<ProjectName>\`.
- The static `Core` class (`TEHhub/Core.cs`) is the hub exposed to plugins. It provides the attached `Core.Process`, game state tree, `Core.CurrentAreaLoadedFiles`, settings, and caches. Startup coroutines connect remote-object addresses to resolved static addresses.
- The hot path uses the Coroutine event loop driven by `TEHhubOverlay.Render()`. Per-frame events run in order: `PerFrameDataUpdate`, `PostPerFrameDataUpdate`, `OnRender`, and `OnPostRender`. Subscribe through existing coroutine patterns and events in `CoroutineEvents/` (`TEHhubEvents`, `RemoteEvents`, `HybridEvents`). Render and event dispatch catch handler exceptions so one handler does not crash the frame.
- `GameProcess` continuously locates the game process and resolves signature patterns into static addresses, raising `OnStaticAddressFound`. Types in `RemoteObjects/` derive from `RemoteObjectBase`; set their `.Address` and let them read lazily when that address changes, unless an explicit refresh is needed. `RemoteObjects/Components/` mirrors game component structures, with matching byte layouts in `TEHhub.Offsets/Objects/Components/`. Reads go through `Utils/SafeMemoryHandle` and the source-generated `Utils/NativeProcessMemory` interop layer. Keep layouts aligned and validate read results before acting on them.
- Core settings and plugin settings are JSON loaded through `Utils/JsonHelper` (Newtonsoft). Runtime configuration lives beside the executable in `configs\core_settings.json`, `configs\plugins.json`, and `Plugins\<Name>\config\`; these are gitignored runtime data.

## Plugins

- Use `Plugins/SamplePluginTemplate/Changeme` as the starting pattern. A plugin assembly must expose exactly one sealed `PCore<TSettings>` implementation (`TSettings : IPSettings, new()`); `PManager` rejects assemblies that do not. Follow the template lifecycle methods: `OnEnable(bool isGameOpened)`, `OnDisable`, `DrawSettings`, `DrawUI`, and `SaveSettings`.
- `PManager` loads each plugin into its own collectible `AssemblyLoadContext` so it can unload the plugin. Debug builds support reload; unloading uses repeated garbage collection to release the context.
- The plugin DLL filename must match its directory name (`PluginName*.dll` under `Plugins\PluginName\`) for discovery.
- `DrawUI` runs every frame for enabled plugins on the `OnRender` coroutine and is wrapped in profiling and exception handling. Read game state through `Core.*` and keep per-frame work bounded.

## Localization

There are two keyed JSON localization systems; use the one that owns the text:

- `TEHhub.Localization.OverlayLocalization` is static and owns core/overlay strings such as settings and popups. Resources are in `TEHhub/Localization/<lang>.json` and are staged at the output root by `TEHhub.csproj`.
- `TEHhub.Localization.PluginLocalization` is an instance service exposed as `PluginText` by `PCore<TSettings>`. Plugin resources belong in `Plugins/<Name>/Localization/<lang>.json` and are staged next to the plugin DLL by `Plugins/Directory.Build.targets`. A static helper, such as the AutoHotKeyTrigger localization helper, is the pattern when a static class cannot access `PluginText` directly.
- Use `T(key, fallback)` for plain text, `F(key, fallback, args...)` for `string.Format` using the current culture, `Label(key, fallback, id)` to append `##id` so visible text can change without changing its ImGui ID, and `Title(key, fallback, id)` to append `###id` for windows, tabs, and collapsing headers.
- Fallback strings are English and must match `en-US.json`; they are used only when a key is missing in both the selected language and English resources. The selected language comes from `Core.GHSettings.UiLanguage` in Settings → General. Supported languages are `en-US`, `fr-FR`, `de-DE`, `es-ES`, `ja-JP`, `ko-KR`, `pt-BR`, `ru-RU`, `th-TH`, `zh-CN`, and `zh-Hant`. `en-US.json` is the baseline; other language files may be partial and missing keys fall back to English.
- Use lower-case dotted keys: `settings.<section>.<name>` for settings and `<feature>.<name>` elsewhere. A plugin's `plugin.description` key supplies its plugin-manager description through `PCore.GetDescription()`.
- Do not reintroduce the removed `OverlayLocalization.L(english, german)` / `IsGerman` bilingual shim. The current authoring guide is `TEHhub/Localization/README.md`.

## Memory offset and patch-day recovery

PoE2 updates can invalidate both signature patterns and structure field offsets; these are separate recovery tasks:

1. Recover signature patterns in `TEHhub.Offsets/StaticOffsetsPatterns.cs` (the current list has six patterns). The `^` token marks `BytesToSkip`, as implemented in `TEHhub.Offsets/Pattern.cs`. Pattern recovery uses Ghidra and the old external `OffsetRecoveryFramework.java` workflow. The path recorded in the legacy guidance, `C:\Users\Gordin\GameHelper2-Offset-Recovery-Framework`, is unverified and may not exist on this machine; confirm any external checkout before relying on it.
2. Recover native structure field offsets separately from live memory with the in-code diagnostics/scanners. These include offsets under `TEHhub.Offsets/Objects/` such as `AreaInstanceOffsets.cs`, `Actor` component layouts, and `InGameStateOffset.cs`; the old pattern script does not update them. Scanners use known signatures and live data, with diagnostic views such as DataVisualization as evidence.

The legacy `patch-day-offset-recovery` and `ghidra-mcp-setup` references point to external Claude memory entries, not repository files. Treat them as unavailable unless they are present in the current environment.

## Conventions

- Follow the repository C# style: `// <copyright>` headers, namespace-scoped `using` directives, XML documentation for public APIs, and English identifiers/comments. Nullable reference types are enabled; warning 1591 for missing public documentation is suppressed.
- Comments referring to `F-0xx` or audit findings preserve security/correctness reasoning. Keep that reasoning when editing nearby code, including the no-`Environment.Exit` rule in `TEHhub/Program.cs` and the plugin unload lifecycle in `PManager`.

## Agent roles agreed with the user

- The primary Codex agent uses Sol at medium reasoning for review, diagnosis, and debugging. It may use Sol high when the work needs it, but must tell the user before moving to high.
- Delegate substantive code writing to Luna at extra-high or maximum reasoning. The primary agent writes code only for genuinely small fixes.
- Keep changes in local Git. The user handles pushing; do not create a pull request for this workflow.
