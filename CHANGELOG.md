# TEHhub Changelog

Version format: MAJOR.MINOR.PATCH (x.x.x). Major versions change compatibility, minor versions add features, patch versions fix bugs or make small improvements. Every completed change updates this file; related edits delivered together share one version.

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
