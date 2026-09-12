# TEHhub

Create a Windows release from a committed revision with `create_release.bat HEAD` (or a commit/tag). The ZIP and SHA256 checksum are written to `artifacts`; packaging uses an isolated clean Git worktree and excludes debug symbols and user configuration. `TEHhub.Launcher.exe --check` checks the core executable and .NET 10 runtime without starting the overlay. Automatic updates remain disabled until a release endpoint is configured.

TEHhub is a Windows x64 .NET overlay application with a plugin-based architecture. The main executable reads data from a running game process, renders an ImGui/ClickableTransparentOverlay UI, and loads plugins from the runtime `Plugins` directory.

TEHhub continues the work of [GameHelper2](https://github.com/Gordin/GameHelper2), originally authored by Arsenic and maintained by Gordin and community contributors. Original copyright notices and plugin author credits are retained.

This guide is written for users who want to build and run the project from Visual Studio without using command-line tools.

> **Using a release ZIP:** `TEHhub.Launcher.exe` runs without a preinstalled .NET runtime. It checks for the [Microsoft .NET 10 Runtime for Windows x64](https://dotnet.microsoft.com/download/dotnet/10.0/runtime) required by TEHhub and offers the download link when it is missing. The SDK is needed only when compiling the source code.

## Required Tools

Install these items before opening the project:

- [Visual Studio](https://visualstudio.microsoft.com/downloads/) for Windows. Visual Studio Community is enough.
- Visual Studio workload: [.NET desktop development](https://learn.microsoft.com/en-us/visualstudio/install/workload-component-id-vs-community?view=visualstudio).
- [.NET 10 SDK for Windows x64](https://dotnet.microsoft.com/en-us/download/dotnet/10.0). The Desktop Runtime alone is not enough for compiling; the SDK is required.
- The project source code. You can download it as a ZIP from the repository page, use [GitHub Desktop](https://desktop.github.com/), or use Visual Studio's `Clone a repository` screen.

If Visual Studio reports that `net10.0-windows` is not supported, update Visual Studio and install the .NET 10 SDK from the link above.

## Project Settings

- Solution file: [`TEHhub.sln`](TEHhub.sln)
- Target framework: `net10.0-windows`
- Runtime identifier: `win-x64`
- Build platform shown in Visual Studio: `Any CPU`
- Actual output architecture: x64
- Main application project: [`TEHhub/TEHhub.csproj`](TEHhub/TEHhub.csproj)
- Launcher project: [`TEHhub.Launcher/TEHhub.Launcher.csproj`](TEHhub.Launcher/TEHhub.Launcher.csproj)
- Shared runtime setting: [`Directory.Build.props`](Directory.Build.props)
- NuGet package configuration: [`NuGet.config`](NuGet.config)

The solution platform is named `Any CPU`, but the projects set `PlatformTarget` to `x64` and the repository sets `RuntimeIdentifier` to `win-x64`.

## Open The Project

1. Start Visual Studio.
2. Choose `Open a project or solution`.
3. Select [`TEHhub.sln`](TEHhub.sln) from the repository root.
4. Wait for Visual Studio to finish loading the solution.
5. If Visual Studio asks whether to restore NuGet packages, allow it.

Open the solution file, not an individual `.csproj` file. Building only the main project can skip the launcher and plugin copy steps.

## Restore Packages

Visual Studio usually restores packages automatically. If it does not:

1. In `Solution Explorer`, right-click the solution `TEHhub`.
2. Select `Restore NuGet Packages`.
3. Wait until the restore operation finishes without errors.

Keep [`NuGet.config`](NuGet.config) in the repository root. The project uses it during package restore.

## Build In Visual Studio

1. In the Visual Studio toolbar, select `Release`.
2. Keep the platform as `Any CPU`.
3. Open the top menu `Build`.
4. Select `Rebuild Solution`.
5. Wait for `Build succeeded` in the Output window.

For normal use, build `Release`. Use `Debug` only when developing or debugging the code.

## Expected Build Output

After a successful `Release` build, the runnable application is created here:

```text
TEHhub\bin\Release\net10.0-windows\win-x64\
```

The normal Visual Studio build is framework-dependent and uses the installed .NET 10 runtime.
In release ZIP files produced by `create_release.bat`, `TEHhub.Launcher.exe` is a self-contained Windows
x64 single-file application. TEHhub remains framework-dependent; the launcher detects a
missing .NET 10 runtime and offers the official download link before trying to start it. The ZIP
also contains `README_FIRST.txt` with the same requirement.

That folder should contain files such as:

```text
TEHhub.exe
TEHhub.Launcher.exe
TEHhub.dll
TEHhub.Offsets.dll
cimgui.dll
Plugins\
```

The `Plugins` folder should contain the built plugin folders:

```text
Plugins\AutoHotKeyTrigger\
Plugins\HealthBars\
Plugins\PreloadAlert\
Plugins\Radar\
```

## Run The Program

Recommended launch method:

1. In File Explorer, open:

```text
TEHhub\bin\Release\net10.0-windows\win-x64\
```

2. Double-click `TEHhub.Launcher.exe`.
3. If Windows asks for administrator permission, accept it.

You can also start `TEHhub.exe` directly from the same folder, but `TEHhub.Launcher.exe` is the intended entry point because it prepares and starts TEHhub.

Do not run `TEHhub.Launcher.exe` from `TEHhub.Launcher\bin\...` unless you know what you are doing. The launcher expects to be next to `TEHhub.exe`; the correctly copied launcher is in the `TEHhub\bin\<Configuration>\net10.0-windows\win-x64\` folder.

If the target game is running as administrator, start TEHhub as administrator too. The application manifest requests administrator privileges for the main executable.

## Solution Projects

The Visual Studio solution builds these projects:

- `TEHhub` - main overlay executable.
- `TEHhub.Offsets` - game offset/native structure definitions.
- `TEHhub.Launcher` - launcher/update wrapper copied into the main output folder.
- `AutoHotKeyTrigger` - plugin.
- `HealthBars` - plugin.
- `PreloadAlert` - plugin.
- `Radar` - plugin and plugin assets.

The solution also builds Atlas2, PlayerBuffBar, LootValue, RitualWispAlert, PickupHelper, AmanamuVoidAlert, and AutoExile2.

Additional folders such as `Plugins/SamplePluginTemplate` and `Plugins/WorldDrawing` are present in the repository but are not included in the solution build by default.

## Why Rebuild The Whole Solution

A full solution build is required because several projects have Visual Studio/MSBuild copy steps:

- `TEHhub.Launcher` copies `TEHhub.Launcher.exe` and its required DLLs into the main TEHhub output folder.
- Plugin projects copy their DLLs and assets into `TEHhub\bin\<Configuration>\net10.0-windows\win-x64\Plugins\<PluginName>\`.
- `TEHhub` copies repository documentation into the output folder when present.

If you build only `TEHhub`, the application may start without the launcher, plugin DLLs, or plugin assets.

## Runtime Configuration

Runtime settings are generated next to the executable when the program runs:

```text
configs\core_settings.json
configs\plugins.json
configs\plugins\<PluginName>\
```

These files are local runtime data and are ignored by Git. Plugin settings are stored centrally in `configs/plugins/<PluginName>/` for easier management.

## Migrating from GameHelper2

Copy the existing `configs` directory into the TEHhub installation after making a backup. If a plugin still has legacy settings in `Plugins/<PluginName>/config/`, copy those files to `configs/plugins/<PluginName>/` without overwriting newer settings. Do not copy old core assemblies over the TEHhub files.

Prebuilt plugin DLLs compiled against `GameHelper` or `GameOffsets` must be rebuilt against `TEHhub` and `TEHhub.Offsets`; the renamed assemblies and namespaces are not binary compatible. Bundled plugins are rebuilt with the solution. Keep plugin assets and author credits when porting third-party source.

## Troubleshooting

`The current .NET SDK does not support targeting .NET 10.0`

Install the [.NET 10 SDK for Windows x64](https://dotnet.microsoft.com/en-us/download/dotnet/10.0), update Visual Studio, restart Visual Studio, and reopen [`TEHhub.sln`](TEHhub.sln).

`NuGet packages did not restore`

Use `Solution Explorer > right-click TEHhub > Restore NuGet Packages`. Also check `Tools > NuGet Package Manager > Package Manager Settings > Package Sources` and make sure `nuget.org` is enabled.

`Build succeeded, but plugins are missing`

Use `Build > Rebuild Solution`, not `Build Project`. Then check the `Plugins` folder under the `TEHhub` output directory.

`Launcher says TEHhub.exe was not found`

You probably started the launcher from the wrong folder. Open `TEHhub\bin\<Configuration>\net10.0-windows\win-x64\` and run the `TEHhub.Launcher.exe` located there.

`Overlay does not attach to the game`

Run TEHhub with the same privilege level as the game. If the game is elevated, run TEHhub as administrator.

## Useful Official Links

- [Visual Studio downloads](https://visualstudio.microsoft.com/downloads/)
- [Install Visual Studio and choose workloads](https://learn.microsoft.com/en-us/visualstudio/install/install-visual-studio?view=visualstudio)
- [.NET desktop development workload](https://learn.microsoft.com/en-us/visualstudio/install/workload-component-id-vs-community?view=visualstudio)
- [.NET 10 SDK downloads](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
