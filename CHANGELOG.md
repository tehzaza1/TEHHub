# TEHhub Changelog

Version format: MAJOR.MINOR.PATCH (x.x.x). Major versions change compatibility, minor versions add features, patch versions fix bugs or make small improvements. Every completed change updates this file; related edits delivered together share one version.

## 1.3.0 — 2026-09-12

- Added frame-boundary performance capture controls and cached bottleneck snapshots with render duration percentiles, process CPU/RAM/GC, world/entity context, memory reads and ranked inclusive scopes.
- Capture runs without opening profiler windows or changing saved settings, and automatically stops after 120 seconds of render activity.
- Added a Debug-aware JSON collector and a measured-first optimization plan for entity/memory updates, Radar, Atlas and LootValue.
- Removed duplicate profiler EndFrame accounting from its UI window; the overlay owns frame completion.
- Validation: capture control/count/reset/serialization tests passed; Debug build passed. Live collection still requires starting the new Debug overlay; automatic approval review blocked its elevated launch.

## 1.2.1 — 2026-09-12

- Moved Log into the main Settings content area; the bottom navigation item selects it like General/Plugins, without a separate window.
- Background capture and file logging continue when switching pages or hiding Settings.
- Validation: Release solution build passed; live UI appearance not visually verified.

## 1.2.0 — 2026-09-12

- Added a fixed Log button below Settings navigation opening a separate searchable log window with severity filters, auto-scroll, clear view and clipboard export.
- Capture console messages and record plugin load/reload/save/DrawUI failures with plugin names. Added public PluginLog.Info/Warning/Error APIs for structured plugin reporting.
- Write bounded rotating session files asynchronously; retain recent history, expose queue/disk failures, and flush during normal shutdown. Preserve Error.log for crashes.
- Validation: Release build and bounded-history tests; synthetic console/plugin severity and real background persistence smoke check passed. Native UI appearance has not been visually verified.

## 1.1.0 — 2026-09-12

- Added local Qwen-assisted primary-root research in OffsetHelper: scan real read-only memory using existing graph validation, then review bounded candidate evidence through Ollama.
- Validate candidate IDs, decisions and probe names; reject invented IDs, extra fields, malformed or oversized responses. AI reviews never install offsets or bypass deterministic confirmation.
- Save immutable and latest AI reviews with game/report SHA256 and input evidence; support cancellation, TryFix disable and session changes. No candidates causes local abstention without inference.
- Added adversarial model-output tests and an explicit synthetic localhost smoke mode.
- Validation: Release build passed with zero warnings/errors; 193 assertions passed; actual installed Qwen returned a validated abstention for a synthetic graph missing descendant evidence. No live-game recovery success is claimed.

## 1.0.4 — 2026-09-12

- Consolidated technical guides and historical records under Documentation with a navigation index; kept runtime data, plugin licenses and agent instruction files in their required locations.
- Updated documentation references and release packaging for the new structure.
- Validation: document path/reference checks and Release solution build.

## 1.0.3 — 2026-09-12

- Removed the About section and all its displayed text from General settings, as requested.
- Validation: Release solution build.

## 1.0.2 — 2026-09-12

- Generate SHA256 through the .NET cryptography API so release creation also works when the PowerShell Get-FileHash command is unavailable.
- Validation: release packaging includes clean build, launcher check, required-file and symbol checks.

## 1.0.1 — 2026-09-12

- Fixed packaging when the repository has no root LICENSE file; preserve available plugin licenses, credits and the historical GameHelper2 changelog.
- Validation: clean Release build and standalone launcher publish passed; package creation is checked by the release workflow.

## 1.0.0 — 2026-09-12

- Established TEHhub as the successor to the GameHelper2-based project, with renamed core, offsets, launcher, solution and plugin references. Original author credits and license remain.
- Updated metadata, manifests, localization and documentation for TEHhub.
- Added a themed control center with responsive branding, navigation and plugin search, wrapped descriptions, plugin settings links and empty states.
- Retained .NET 10 support, centralized plugin configuration, offset recovery, recovery dumps and primary-root research tools.
- Rebuilt release creation around an isolated committed Git revision, standalone launcher, package manifest and SHA256 checksum; excludes PDB files and user settings.
- Added launcher --check and skipped automatic update checks until an endpoint is configured.
- Fixed local AI worker context validation and repository path boundary checking.
- Compatibility: legacy plugin DLLs must be rebuilt against TEHhub. Existing configuration migration is manual.
- Validation: Release solution build passed with zero warnings/errors; offset recovery tests passed 167 assertions. Native UI and live-game behavior still require testing in game.
