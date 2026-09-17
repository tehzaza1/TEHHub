# GEMINI.md

This file provides guidance to Gemini / Antigravity when working with code in this repository.

## What this is

TEHHub is a Windows x64 .NET 10 overlay for Path of Exile 2. It attaches to the running game process, reads game state out of its memory, and renders an ImGui overlay (via `ClickableTransparentOverlay`). Functionality is delivered through dynamically-loaded plugins.

## Build & Run

- **Target:** `net10.0-windows`, `win-x64`, `PlatformTarget=x64` (solution platform is shown as `Any CPU` but output is always x64). Requires the **.NET 10 SDK** (Desktop Runtime alone is not enough).
- **Always build the whole solution**, not individual projects. `TEHhub.Launcher` and each plugin have `AfterTargets="Build"` MSBuild `Copy` steps that stage their DLLs/assets into the TEHhub output folder. Building just `TEHhub` produces an app with no launcher and no plugins.

```powershell
dotnet build TEHhub.sln -c Release       # production build
dotnet build TEHhub.sln -c Debug         # development / debugging
```

- **Handling build errors from locked files (`MSB3021`/`MSB3027`, "being used by another process"):** These happen because TEHhub is running and holds the output DLLs. This is expected — do **not** try to kill TEHhub, and do **not** explain that or why it happened. The compile itself still succeeded (no `error CS`); only the copy-to-output step failed. Just check for real `error CS` lines and tell the user whether they need to rebuild the solution themselves.
- **Output / run dir:** `TEHhub\bin\<Config>\net10.0-windows\win-x64\`. Launch `TEHhub.Launcher.exe` from there (it locates and starts `TEHhub.exe`). Run with the same privilege level as the game — if the game is elevated, run elevated, or the overlay won't attach.
- **Deployment:** When compiling `TEHhub.sln -c Release` successfully with 0 Errors / 0 Warnings, execute the release deploy script to sync binaries to the live runtime directory:
  ```powershell
  powershell -ExecutionPolicy Bypass -File tools\Deploy-Release.ps1
  ```
  Target live deployment path: `C:\Games\Hy-v Tool\DXPEOE\TEHhub`
- `NuGet.config` (repo root) adds the `veldrid-prereleases` myget feed required by `ClickableTransparentOverlay`.

## Architecture

**Three core projects + plugins:**
- `TEHhub` — The overlay executable and all core runtime services (`GameOverlay`, `Core`, `RemoteObjects`, `PManager`).
- `TEHhub.Offsets` — Native struct layouts (`Objects/`, `Components/`) and the signature `Pattern[]` (`StaticOffsetsPatterns.cs`) used to locate game data in memory. Has no dependency on `TEHhub`.
- `TEHhub.Launcher` — Thin wrapper that finds/updates/launches TEHhub; copied into TEHhub's output.
- `Plugins/*` — Each is a separate class library referencing `TEHhub` with `<Private>false</Private>` (do not copy TEHhub's DLLs), staged into `...\win-x64\Plugins\<ProjectName>\`.

**`Core` (static, `Core.cs`)** is the central hub. It exposes everything plugins read: `Core.Process` (the attached `GameProcess`), `Core.States` (game state tree), `Core.CurrentAreaLoadedFiles`, settings, and caches. It owns startup coroutines that wire each `RemoteObject`'s `Address` to a resolved static address.

**Coroutine event loop (not async/await for the hot path).** The whole app runs on `Coroutine` (Daniel Cronqvist's lib) driven from `GameOverlay.Render()`, which ticks the handler and raises events each frame in order: `PerFrameDataUpdate` → `PostPerFrameDataUpdate` → `OnRender` → `OnPostRender`. Code subscribes by `yield return new Wait(SomeEvent)` in an `IEnumerator<Wait>` coroutine. Events live in `CoroutineEvents/` (`GameHelperEvents` = lifecycle/render, `RemoteEvents`, `HybridEvents`). Render and event dispatch wrap every handler in try/catch — a throwing coroutine logs but doesn't crash the frame.

**Memory reading model.** `GameProcess` continuously finds the game process and resolves the signature patterns into `StaticAddresses`, firing `OnStaticAddressFound`. `RemoteObjects/` types derive from `RemoteObjectBase`: you set their `.Address` and they read/parse memory lazily (re-reading only when the address changes, unless `forceUpdate`). `RemoteObjects/Components/` mirrors the game's entity-component layout (`Actor`, `Life`, `Positioned`, `Render`, etc.), with matching byte layouts in `TEHhub.Offsets/Objects/Components/`. Actual reads go through `Utils/SafeMemoryHandle` and the source-generated `Utils/NativeProcessMemory` interop layer.

**Settings/config.** `Core.GHSettings` and plugin settings are JSON loaded via `Utils/JsonHelper` (Newtonsoft). At runtime, config is written next to the exe: `configs\core_settings.json`, `configs\plugins.json`, and `Plugins\<Name>\config\`. These are gitignored runtime data.

## Plugins

To author a plugin, copy `Plugins/SamplePluginTemplate/Changeme`. A plugin is exactly **one `sealed` class deriving from `PCore<TSettings>`** (where `TSettings : IPSettings, new()`) — `PManager` rejects assemblies that don't have exactly one. Override `OnEnable(bool isGameOpened)`, `OnDisable`, `DrawSettings`, `DrawUI`, `SaveSettings`.

- `PManager` loads each plugin into its **own collectible `AssemblyLoadContext`** so it can be hot-unloaded (DEBUG builds support reload; unload runs repeated GC to release the ALC).
- The plugin DLL filename **must match its directory name** (`PluginName*.dll` in `Plugins\PluginName\`), or it won't be discovered.
- `DrawUI` is called every frame for enabled plugins via the `OnRender` coroutine (wrapped in profiling + try/catch). Read game data through `Core.*`.

## Localization

Overlay text is translated through keyed JSON resources. There are **two parallel systems** — pick the one that owns the text:

- **`TEHhub.Localization.OverlayLocalization`** (static) — **core/overlay** text (settings window, core popups). Resources live in `TEHhub/Localization/<lang>.json` and are staged to the output root.
- **`TEHhub.Localization.PluginLocalization`** (instance) — **plugin** text. Every `PCore<TSettings>` exposes it as `protected PluginLocalization PluginText`. Each plugin owns its resources under `Plugins/<Name>/Localization/<lang>.json`, staged next to the plugin DLL by `Plugins/Directory.Build.targets`. A `static` helper (see AutoHotKeyTrigger's `AhkText`) is the pattern when a static class needs `PluginText` it can't reach through `this`.

Both expose the same four functions:
- `T(key, fallback)` — plain text.
- `F(key, fallback, args...)` — `string.Format` with the current culture.
- `Label(key, fallback, id)` — appends a hidden `##id` so the ImGui ID stays stable across languages while the visible text changes.
- `Title(key, fallback, id)` — appends `###id` for windows / tabs / collapsing headers.

`fallback` is the English string, returned only when the key is missing from both the current language and `en-US.json`.

## Patch-Day Memory Offset Discovery

When PoE2 patches, the app breaks until offsets are refreshed. Use the Heuristic Memory Probing technique:

1. **Step 1: Base Pattern Recovery (AOB Signature)**
   - Scan for unchanging reference strings in the `.exe` file or module memory:
     - `"Unable to get InGameState"` ➔ Calculate RIP-relative displacement to find the Game States base.
     - `"Mods.dat"` ➔ File Root base.
     - `"Got Instance Details from login server"` ➔ `AreaChangeCounter`.
   - Turn the assembly opcodes around the calling point into an AOB pattern and place it in `TEHhub.Offsets/StaticOffsetsPatterns.cs`.

2. **Step 2: Heuristic AreaInstance & Player Chasing**
   - From `InGameState`, scan all pointers in range `0x000` - `0x500` (stepping by 8 bytes).
   - Within each valid pointer, inspect sub-pointers in range `0x000` - `0x800` for an entity whose `Details->name` starts with `"Metadata/Characters/"`:
     - Matched sub-pointer ➔ `LocalPlayerPtr` (e.g., `0x5D0`).
     - Storing offset within InGameState ➔ `AreaInstanceData` (e.g., `0x290`).

3. **Step 3: Heuristic Vital Pools (HP / MP / ES Discovery)**
   - Navigate to player `"Life"` component.
   - Scan for 4-byte integer pairs `(Total, Current)` meeting `0 < Current <= Total <= 50000`:
     - First pair ➔ `Health (HP)` (e.g., `0x1DC` / `0x1E0` in VitalStruct Health).
     - Second pair ➔ `Mana (MP)` (e.g., `0x234`).
     - Third pair ➔ `Energy Shield (ES)` (e.g., `0x274`).

4. **Step 4: Area Level Discovery**
   - Within `AreaInstance`, scan for a 1-byte integer matching the current zone level (1 - 100) ➔ Retrieves offset for `CurrentAreaLevel` (e.g., `0x0BC`).

## Mandatory Global Rules

1. **Mandatory Git Commit Policy (CRITICAL / สำคัญมาก):**
   - ทุกครั้งที่มีการแก้โค้ด / ปรับแต่ง / แก้บักเสร็จเรียบร้อย: **ต้องทำ `git commit` ทันทีเสมอ**
   - ห้ามปล่อยให้โค้ดที่ทำงานได้แล้วค้างอยู่ในสถานะ Uncommitted ใน Working Tree เด็ดขาด
   - Stage ไฟล์ทั้งหมด (`git add .`) และใส่ Commit Message ให้ชัดเจน บ่งบอกว่าแก้อะไรและทำไม
2. **Strict Scope Isolation (Conservative Editing):**
   - แก้เฉพาะจุดที่เกี่ยวข้องกับงานที่ได้รับมอบหมายเท่านั้น
   - ห้ามแตะต้องหรือ Refactor ส่วนที่ทำงานถูกต้องอยู่แล้วโดยไม่จำเป็น หรือถ้าผู้ใช้ไม่ได้สั่งให้แก้
3. **Communication & Language Policy:**
   - สื่อสารกับผู้ใช้เป็น **ภาษาไทย (Thai)** เสมอ
   - เขียนโค้ด ชื่อตัวแปร ฟังก์ชัน คอมเมนต์ในโค้ดเป็น **ภาษาอังกฤษ (English)** ทั้งหมด
4. **Versioning & Changelog Policy:**
   - **ห้าม Bump Version ใน `.csproj` และห้ามแก้ `CHANGELOG.md` เด็ดขาด** ระหว่างการพัฒนาหรือแก้บักทั่วไป
   - ให้ทำ Version bump และเขียน Changelog เฉพาะเมื่อผู้ใช้มีคำสั่งชัดเจนให้ทำ Release เท่านั้น
