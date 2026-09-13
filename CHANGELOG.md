# TEHhub Changelog

Version format: MAJOR.MINOR.PATCH (x.x.x). Major versions change compatibility, minor versions add features, patch versions fix bugs or make small improvements. Every completed change updates this file; related edits delivered together share one version.

## 1.8.13 — 2026-09-14

- Extended the Debug Rune Station snapshot with a bounded listener map: up to 24 StateMachine listener nodes now report their pointers and both known station-owner candidate relationships (`-0xA0` and `-0x98`).
- Compatibility: Diagnostic-only; public Release builds continue to compile it out.
- Validation: `dotnet build TEHhub/TEHhub.csproj -c Debug --no-restore` succeeded with 0 warnings and 0 errors.
## 1.8.12 — 2026-09-14

- Extended the Debug Rune Station snapshot with a bounded one-hop pointer scan from the verified Station and Anchor Holder structures.
- Reports only nested references that resolve exactly into the verified Rune DAT range; traversal is capped at 96 source pointers and 0x200 bytes per target.
- Compatibility: Diagnostic-only; public Release builds continue to compile it out.
- Validation: `dotnet build TEHhub/TEHhub.csproj -c Debug --no-restore` succeeded with 0 warnings and 0 errors.
## 1.8.11 — 2026-09-14

- Extended the Debug-only Expedition Rune Station inspector to scan bounded Station and Anchor Holder memory for direct pointers into the verified Rune DAT table.
- The snapshot now reports the anchor row, holder, table base, resolved row stride, and every directly verifiable rune reference before reporting vector headers. This separates true rune references from arbitrary pointer-shaped memory.
- Compatibility: Diagnostic-only; public Release builds continue to compile it out.
- Validation: `dotnet build TEHhub/TEHhub.csproj -c Debug --no-restore` succeeded with 0 warnings and 0 errors.
## 1.8.10 — 2026-09-13

- Reworked the Debug-only Expedition Rune Station vector inspector after its previous live scan returned no rows:
  - Added a manual **Refresh station memory** action that captures one bounded snapshot instead of probing every ImGui frame.
  - Reports all sane, non-empty `std::vector` headers in the station range, exact byte sizes, compatible candidate strides, and a capped raw-byte preview without dereferencing unknown values.
  - Reports resolver/read failures in the panel rather than silently discarding them, so a verified slot-entry layout can be derived from live evidence.
- Compatibility: No runtime decoder or gameplay recommendation changed. The diagnostic UI is compiled out of public Release builds.
- Validation: `dotnet build TEHhub/TEHhub.csproj -c Debug --no-restore` succeeded with 0 warnings and 0 errors.
## 1.8.9 — 2026-09-13

- Added Expedition Remnant and Monolith inspection across core TEHhub Entity views:
  - **StateMachine Component Details**:
    - Added `RuneStationDetails` struct and `StateMachine.TryGetRuneStationDetails(out RuneStationDetails)` to resolve authoritative socket counts, golden crown slot indices (`station + 0x40`), anchor rune names (`station + 0x28` / `0x30`), anchor slot indices (`station + 0x3C`), and proliferation status (`IsAnchorInGoldenSlot`).
    - Extended `TryGetRuneStationSocketCount` to automatically leverage dual listener station candidate offsets (`sub - 0xA0` and `sub - 0x98`).
    - Added colored Expedition Rune Station summary inside `StateMachine.ToImGui()`.
  - **Core Entity ImGui View**:
    - Added dedicated color-coded `[Expedition Monolith / Remnant]` section in `Entity.ToImGui()` displaying authoritative sockets, golden slot index, recipe anchor rune, and whether the rune proliferates or is local-only.
  - **AreaInstance EntitiesWidget & JSON Dump**:
    - Enhanced entity tree node labels in `EntitiesWidget` (`Awake Entities` and `Sleeping Entities`) to display `[Expedition Xs | ★ Gold #Y: Rune (Proliferates)]` or local status directly in the list.
    - Added `ExpeditionMonolith` structured object into entity JSON dump payloads when using `dump##{entity.Key}`.
    - Updated on-screen world labels to include Expedition socket, golden slot, and anchor rune info when filtering by path.
  - **OffsetHelper Inspector**:
    - Displayed Expedition Monolith summary inside pinned entity cards.
- Compatibility: Core framework and launcher versions synchronized to 1.8.9.
- Validation: Debug and Release builds for all projects in `TEHhub.sln` succeeded with 0 Warnings and 0 Errors.

## 1.8.8 — 2026-09-13

- Fixed ExpeditionPlanner overlay visibility, 3D badge projection, and candidate reach failures:
  - **Fixed 3D Badge Screen Projection**: Used native 3D world elevation (`p.WorldPosition.Z`) instead of `TerrainHeight` (which was 0), eliminating behind-camera projection clipping and restoring 3D badges on screen.
  - **Always-Visible Route Advisor Window**: The ImGui summary card now renders whenever an Expedition encounter exists (`this.hasDetonator || this.activeTargets.Count > 0 || this.placedBombs.Count > 0`), displaying active target counts, detonator status, and route status even when analyzing placements.
  - **Player Anchor Fallback**: When the Detonator entity is out of network bubble or unplaced, the solver cleanly falls back to the player's current position or nearest target as placement anchor.
  - **Relaxed Wire Detour & Obstacle Constraints**:
    - Replaced the over-restrictive `blocked >= 4` hard wall tripwire with a realistic threshold (`blocked >= 35`), preventing minor terrain doodads and tile fringes from aborting legal wire routes.
    - Calibrated obstruction distance penalty to `-40f` (down from `-250f`), preventing valid paths from being discarded.
    - Ensured candidate generation retains target base positions and front-facing vectors.
  - **Explicit Target BaseWeights**: Populated default values for Chests (50), Elites (35), Monsters (10), Sentinels (80), and Bosses (150).
- Compatibility: Standalone ExpeditionPlanner updates; synchronized core and launcher version metadata.
- Validation: Debug and Release builds for all 15 projects in `TEHhub.sln` succeeded with 0 Warnings and 0 Errors; verified live memory entity classification on running PoE 2 client.

## 1.8.7 — 2026-09-13

- Implemented the Grand Expedition Rune System, Tier List, Golden Slot Proliferation, and Aggressive Reroll detection:
  - **Rune Tier List Calibration**:
    - **Golden**: `Opulent` (SSS-Tier, prioritizes monster rarity and double loot, massive weight 1000f, selected as earliest possible anchor).
    - **S-Tier Purple**: `Power`, `Death`, `Bond`, `Oath` (weight 400–450f, prioritized early in the explosive chain).
    - **A-Tier**: `Time` (purple) and `Rebirth` (blue rune treated as A-tier, weight 220–250f).
    - **B-Tier Purple**: `Arcane`, `Protective`, `Celestial`, `Soul`, `Vision`, `Prismatic`, `Moon`, `Rage`, etc. (weight 100–120f).
    - **C-Tier Blue**: Blue runes (weight 30–50f, valued for quantity stacking on subsequent remnants rather than individual impact).
  - **Golden Slot Proliferation & No-Stacking Enforcement**:
    - Identified that only the rune in the Remnant pillar's golden slot (`anchorPos` / `anchorIdx`) proliferates forward to all subsequent remnants.
    - Implemented strict non-stacking rule in `ExpeditionSolver`: if a rune has already proliferated into `accumulatedProliferatedRunes`, duplicate runes yield zero proliferation multiplier.
    - Proliferation multiplier actively tracks accumulated rune count, rewarding 7–8+ rune stacks at chain completion.
  - **Aggressive Remnant Reroll Advice**:
    - Detects Remnants whose golden slot holds only blue runes (no purple or gold).
    - Flags these Remnants with clear alerts (`[! REROLL RECOMMENDED: No Purple/Gold]`) in the 3D world badge and highlights them in the Route Advisor summary card.
  - **Visual & In-Game UI Styling**:
    - Color-coded badges and labels: Golden Opulent (bright gold `0xFFFFD700`), S/A/B Purple (vibrant orchid/magenta `0xFFFF55FF`), and Blue (`0xFF00D2FF`).
    - Route Advisor card displays the full active `Proliferated Rune Stack` chain (e.g. `[Opulent] -> [Bond] -> [Oath] -> [Power]`) and counts remnants needing reroll.
- Compatibility: Standalone ExpeditionPlanner updates; synchronized core and launcher version metadata.
- Validation: Debug and Release builds for all 15 projects in `TEHhub.sln` succeeded with 0 Warnings and 0 Errors.

## 1.8.6 — 2026-09-13

- Added autonomous Remnant pillar rune recommendation and sequential proliferation weighting:
  - **Self-Contained Runeshape Assets**: Bundled and deployed `expedition2_recipes.json` directly within `Plugins/ExpeditionPlanner/` with zero dependencies on `LootValue`.
  - **Remnant Rune Advisor**: `RemnantRuneAdvisor.cs` reads `StateMachine` station offsets (`HoleCount`, `AnchorIdx`, `AnchorPos`) on active `Expedition2Encounter` entities, evaluates available recipe combinations against area level, and recommends the optimal rune/recipe to forge into each pillar.
  - **Sequential Rune Proliferation Weighting**: Modeled Runic monster and modifier proliferation across the explosive chain. Early Remnant detonations inherit buffs to all remaining bombs (`multiplier = 1.0 + 1.2 * remainingBombs`), prioritizing high-value Remnant pillars early in the sequence.
  - **In-World & Advisor Guidance**: Displays floating rune choice cards (`★ Choice: [Rune Name]` and `Proliferates: xN bombs`) directly beneath the 3D bomb badges and lists step-by-step pillar rune selection directives in the route summary card.
  - **Settings Calibration**: Restored `MaxPlacementRangeGrid` to `90.0f` and calibrated baseline `BlastRadiusGrid` to `30.0f` (PoE 2 base explosive radius).
- Compatibility: Fully self-contained within ExpeditionPlanner plugin; synchronized core and launcher version metadata.
- Validation: Debug and Release builds for all 15 projects in `TEHhub.sln` succeeded with 0 Warnings and 0 Errors.

## 1.8.5 — 2026-09-13

- Fixed Expedition explosive wire reach failure caused by Remnant pillar and obstacle detours:
  - **Front-Facing Dynamic Candidate Generation**: When targeting solid Remnant pillars (`Expedition2Encounter`) and Sentinels, candidates are generated dynamically on the open, walkable ground on the front hemisphere facing the active anchor (detonator or previous bomb). The pillar is placed behind the bomb rather than between the anchor and the bomb, guaranteeing 100% clean line of sight and shortening wire length by 20–28 units.
  - **Exact Convex Hull Wire Detour Distance**: Implemented `ComputeCircleDetourDistance` in `LegalPlacement.cs` modeling tangent approaches, circular arc wrapping around pillars (`PillarEffectiveRadius = 18.0f`), and discrete connector peg spacing overhead.
  - **Strict Obstacle Line-of-Sight Enforcement**:
    - Lines blocked by solid walls or cliffs (`blockedCells >= 4`) are rejected immediately.
    - If a candidate requires detouring around a pillar or wall, reach limit is strictly constrained (`<= 65.0f` grid vs open-ground `85.0f`), and score is heavily penalized (`-250f`) so open-ground paths are strictly prioritized.
    - Candidate points colliding with the physical pillar base (`< 14.0f` radius) or unwalkable cells are prohibited.
  - **Enhanced Route Overlay**: Visualizes connector lines from active anchor (detonator/last placed bomb) to first recommendation, color-coding clean lines (cyan) vs obstacle-detoured lines (red warning), and displaying wire distance in the advisor card.
- Compatibility: Standalone plugin logic updated; Debug and Release builds retain identical core behaviors; synchronized core and launcher version metadata.
- Validation: Debug and Release builds for all 15 projects in `TEHhub.sln` succeeded with 0 Warnings and 0 Errors; verified detour distance formulas and front-facing candidate generator logic.

## 1.8.4 — 2026-09-13

- Added standalone `Plugins/ExpeditionPlanner`:
  - `ExpeditionPlannerCore.cs` implementing `PCore<ExpeditionPlannerSettings>`.
  - Reads awake Expedition entities directly: Detonators, Placed Explosives, Connector Poles, Fuses, Reward Markers, Remnants, and Verisium Sentinels.
  - Implemented `LegalPlacement.IsPlaceable(point, previousBomb, currentArea)` evaluating distance bounds and terrain validity.
  - Implemented stateful `ExpeditionSolver` modeling Grand Expedition route scoring across Safe, Balanced, and Greedy player profiles with rune proliferation and forbidden rune protection.
  - ImGui visual route overlay: renders numbered placement badges (`1 → 2 → 3`), blast radius indicators, connector lines, and an advisory summary card. Completely read-only with zero automated input or clicking.
  - Settings persisted cleanly to `configs/plugins/ExpeditionPlanner/settings.json`.
- Fixed `HealthBars` texture path resolution: resolved `TexturesPath` against `AppContext.BaseDirectory` when relative to prevent texture load failures regardless of the launching working directory.
- Upgraded `ExpeditionOffsetScanner`: expanded UI traversal depth, region scanning limits, and added entity and component memory windows.
- Compatibility: Standalone plugin loads independently with no dependencies on Radar; diagnostic endpoints compile out of Release builds.
- Validation: Debug and Release builds for all 15 projects in `TEHhub.sln` succeeded with 0 Warnings and 0 Errors; verified live memory entity classification on running PoE 2 client.

## 1.8.3 — 2026-09-13

- Added `tools/Capture-ExpeditionScan.ps1`: interactive CLI wizard and command runner to execute the differential offset scan sequence `0 → 1 → 2 → 3 → 0` in live Expeditions and record surviving candidates.
- Added `tools/Deploy-Debug.ps1`: safe Debug deployment script syncing binaries and plugins to `C:\Games\Hy-v Tool\DXPEOE\TEHhub` while strictly preserving `configs/`, `.ini`, and local runtime logs.
- Enhanced `ExpeditionProbe.cs`: captures `Buffs` (`StatusEffects`) and `Stats` on candidate entities (including `Expedition2Encounter`) to identify Remnant modifier data sources without guessing.
- Registered `ExpeditionProbeStat` in `DiagnosticsApiJsonContext` for source-generated JSON serialization.
- Compatibility: Debug diagnostic probe enriched; Release builds cleanly compile out diagnostic endpoints; synchronized core and launcher version metadata.
- Validation: Debug and Release builds succeeded with 0 Warnings and 0 Errors; verified robocopy deployment to `C:\Games\Hy-v Tool\DXPEOE\TEHhub` preserved existing configuration.

## 1.8.2 — 2026-09-13

- Defined the ExpeditionPlanner pipeline explicitly: read current Rune/Remnant data, score it against the active player objective, filter candidate bomb points through `IsPlaceable`, then recommend only a legal ordered route.
- Defined `IsPlaceable(point, previousBomb, currentArea)` precedence: verified native data first, verified current-area calibration second, otherwise unknown. Unknown points are excluded rather than proposed.
- Clarified that the Runeshape recipe catalog describes craft relationships only; it does not identify the runes in the current encounter.
- Compatibility: documentation and synchronized version metadata only; runtime behaviour is unchanged.
- Validation: documentation reviewed; Debug and Release core and launcher builds passed without warnings or errors.
## 1.8.1 — 2026-09-13

- Refined the ExpeditionPlanner product scope: player-facing UI shows only recommended bomb locations, their order, and concise reasoning. Raw fuse/pole networks, measured ranges, and diagnostic entity state remain internal evidence and Debug tooling.
- Updated the implementation handoff so Phase 1 builds an internal entity model and renders no speculative visual output before placement constraints are verified.
- Compatibility: documentation and synchronized version metadata only; runtime behaviour is unchanged.
- Validation: documentation reviewed; Debug and Release core and launcher builds passed without warnings or errors.
## 1.8.0 — 2026-09-13

- Added a Debug-only, read-only Expedition differential scanner at `POST /api/diagnostics/expedition-offset-scan?placedBombs=N`. It narrows integer candidates by the observed placed-bomb sequence while scanning only bounded windows rooted at live InGameState and declared UI anchors.
- Added `POST /api/diagnostics/expedition-offset-scan/reset` to start an independent capture sequence. Results include root provenance, address, relative offset, value sequence, scan coverage and truncation status; every result remains an unverified candidate.
- The scanner is limited to 64 KiB, 64 regions, 48 declared UI nodes and 25 ms per on-demand render capture. It never scans process-wide memory, follows arbitrary pointers, writes game memory, or imports PoE1 offsets/constants.
- Updated the ExpeditionPlanner handoff with the live detonation observations, the explicit no-inference rules, and the reproducible native-wrapper discovery sequence for a handoff to another developer.
- Compatibility: the scanner, routes and diagnostic JSON types compile out of Release; runtime gameplay behaviour is unchanged.
- Validation: Debug and Release core and launcher builds passed without warnings or errors. Verified the Debug binary contains the scanner endpoint and Release does not.
## 1.7.1 — 2026-09-13

- Fixed LootValue Runeshape recipe loading to use the plugin-local `expedition2_recipes.json` deployed beside `LootValue.dll`.
- Removed the machine-specific legacy fallback under `C:\Games\Hy-v Tool\DXPEOE\trade\resources`; a missing plugin-local catalog now fails visibly instead of silently using an external copy.
- Compatibility: the recipe catalog is packaged with both Debug and Release LootValue builds. Existing settings are unchanged.
- Validation: Debug and Release LootValue builds passed without warnings or errors; verified the deployed plugin output contains `expedition2_recipes.json`.
## 1.7.0 — 2026-09-13

- Added a Debug-only, on-demand Expedition placement evidence capture at `POST` and `GET /api/diagnostics/expedition-placement-probe`. Its `ExpeditionDetonatorElement.Info` is explicitly entity-observed: it records detonator, placed bombs, connector poles, fuses, placement indicators, known Expedition targets, and bounded terrain samples around indicators.
- The capture makes no claim about native PoE2 UI offsets and does not copy PoE1 placement constants. Raw walkability values are emitted only when their layout exactly matches the observed terrain grid; otherwise they remain unmapped evidence.
- Simplified the Expedition research catalog to canonical rune names and local Runeshape recipe references. Removed scraped rune-effect text and inferred danger tags so a future planner cannot make decisions from unverified effects.
- Compatibility: the placement API and diagnostic code compile out of public Release builds; runtime gameplay behaviour is unchanged.
- Validation: Python catalog regeneration and compilation passed; Debug and Release core builds passed without warnings or errors. Verified the Debug binary contains the placement endpoint while the Release binary does not.
## 1.6.11 — 2026-09-13

- Added a maintainer-only PoE2DB rune catalog importer and a generated 34-rune research catalog that links bounded effect evidence, conservative danger tags, and local Runeshape recipe references.
- Parsed effects are available for 29 runes; Tidal, Moon, Sky, Earth, and Bait remain explicitly unknown/unavailable and must never be considered safe by a planner.
- Added PoE2DB source and CC BY-NC-SA attribution guidance. The importer/catalog are research-only and excluded from runtime/plugin loading.
- Validation: Python compilation and a Bloodletting parser assertion passed; Debug and Release core builds passed without warnings/errors after the version update.

## 1.6.10 — 2026-09-13

- Added the Grand Expedition route model to the ExpeditionPlanner handoff: scoring is ordered and stateful for golden-slot proliferation, non-stacking duplicates, explosive budget, and projected downstream rune value.
- Added configurable initial priority seeds for Opulent, Power, Death, Bond, Oath, Time, and Rebirth from the supplied 0.5 strategy reference; combat-danger data remains a separate verified input.
- Compatibility: documentation and synchronized version metadata only; runtime behaviour is unchanged.
- Validation: reviewed against the cited guide; Debug and Release core builds passed without warnings/errors after the version update.

## 1.6.9 — 2026-09-13

- Expanded the ExpeditionPlanner handoff with a net-score model for reward value, beneficial rune weights, dangerous-rune penalties, uncertainty, Safe/Balanced/Greedy profiles, and per-rune `never take` rules.
- Defined the required outcome: the recommendation explains its rewards, dangers, score, and rejected alternatives; unknown rune data cannot silently be treated as safe.
- Compatibility: documentation and synchronized version metadata only; runtime behaviour is unchanged.
- Validation: documentation reviewed; Debug and Release core builds passed without warnings/errors after the version update.

## 1.6.8 — 2026-09-13

- Defined the ExpeditionPlanner end state and definition of done: a standalone, manual recommendation tool for small and large encounters with verified placement constraints, clear explanations, and no game input.
- Compatibility: documentation and synchronized version metadata only; runtime behaviour is unchanged.
- Validation: documentation reviewed; Debug and Release core builds passed without warnings/errors after the version update.

## 1.6.7 — 2026-09-13

- Added an implementation handoff for a standalone ExpeditionPlanner plugin, including verified PoE2 entity evidence, the small/large encounter design, safety boundaries, phased delivery, and validation criteria.
- The plan keeps Expedition planning separate from Radar and centralizes its future settings under `configs/plugins/ExpeditionPlanner`.
- Compatibility: documentation only; runtime behaviour is unchanged.
- Validation: reviewed against the live Debug evidence captures collected for an explosive, connector poles, and fuses; Debug and Release core builds passed without warnings/errors.

## 1.6.6 — 2026-09-13

- Added a Debug-only, on-demand Expedition UI probe. It walks only declared Game UI children with limits of 4,096 nodes, depth 12, 512 children per node, and 150 ms; output contains visible nodes and their ancestor paths, flags, local bounds, and sane string IDs.
- Added `POST` and `GET /api/diagnostics/expedition-ui-probe`; the endpoint is excluded from Release builds.
- Broadened the Debug Expedition entity probe to include placement-related paths.
- Compatibility: this collects evidence only. It does not automate placement or install inferred offsets.
- Validation: Debug and Release core builds passed without warnings/errors. Live-game capture is pending.

## 1.6.5 — 2026-09-13

- Added a Debug-only, on-demand Expedition2 evidence probe. It captures bounded, read-only candidate entity data during active encounters: paths, model/minimap identifiers, grid/world positions, mods and component names.
- Added `POST` and `GET /api/diagnostics/expedition-probe` on the loopback Debug API. The probe runs on the render thread only when requested; Release compiles it and its endpoint out.
- This is research instrumentation, not an Expedition planner or automated placement system. Live Expedition2 evidence is still required before adding placement/radius overlays.
- Validation: Debug and Release core builds passed without warnings/errors. Live probe capture remains pending.

## 1.6.4 — 2026-09-13

- Fixed reversed couch co-op selection semantics: selecting Player 1's name now confirms Player 2 as the follower; selecting Player 2's own name cannot reverse the pair. An empty leader selection continues to auto-detect the pair.
- Renamed the web control and messages to identify the selected character as the leader/head, with an in-app explanation of the Player 1 → Player 2 relationship.
- Validation: AutoExile2 Debug and Release builds passed without warnings/errors; player-picker JavaScript syntax and regression checks passed. Live couch co-op testing remains pending.

## 1.6.3 — 2026-09-13

- Fixed AutoEx co-op player name discovery: the picker includes Player 1, direct Player 2 and awake player names with matching details, and retains names when detail records are incomplete. A lone local player is not automatically selected as the follow target.
- Player components retry initially empty/unreadable names at most once per 500 ms until resolved, instead of caching an empty name permanently. Successful names remain cached; XP/level refreshes continue during name-read failures.
- Validation: AutoEx/core Debug and Release builds passed without warnings/errors; JavaScript syntax and player-picker regression checks passed. No live co-op testing or deployment performed.

## 1.6.2 — 2026-09-13

- Fixed price-cache restart recovery: fetched provider name aliases are now saved/restored separately from verified metadata mappings. Cache schema 7 refetches older incomplete caches; existing user settings remain compatible.
- Release packaging excludes API-dependent monitor scripts and verifies that Debug API types are absent and both LootValue mapping files exactly match source. Launcher validation now waits for the GUI process and checks its actual exit code.
- Added project policy for separate code/debug sub-agents, file ownership, efficient models and Debug/Release validation.
- Validation: Debug suite passed 214 assertions; Release solution build passed without warnings/errors; release script syntax passed. An offline probe verified save/reload/alias resolution and rejection of schema-6 caches. Live-game price validation remains pending.

## 1.6.1 — 2026-09-13

- Removed the unused runtime metadata mapping writer; mapping files are loaded read-only. LootValue builds now deploy both verified mapping files alongside the plugin DLL.
- Inspection of the user's live metadata file found 1,882 keys; all 836 verified aliases were still present with unchanged names. The missing-price cause is not established by this inspection.
- Validation: Release solution build and launcher package checks recorded in the release build artifacts. Live price testing remains pending.

## 1.6.0 — 2026-09-13

- Added a central Debug tool API catalog, cached application/UI/plugin state, tool diagnostics, runtime logs and render-thread tool window requests. Added a bounded diagnostic publishing contract for plugins and usage documentation.
- Compiled out the HTTP listener, tool hub and publisher from Release; ordinary in-program logs remain available. Core and launcher versions are synchronized at 1.6.0.
- Added Currency Exchange list discovery using validated cached/fixed paths and a bounded fallback search, with scan diagnostics exposed to Debug tooling.
- Validation: Release core/LootValue and launcher builds passed without warnings/errors; Debug recovery/diagnostics suite passed 214 assertions. Live Exchange overlay validation and deployment remain pending.

## 1.5.14 — 2026-09-13

- Re-audited the full 3,446-page cached linked catalog with two independent agents: metadata fields include all Type/ItemType rows and nearest item names; unique art pairs item headers with their own images rather than page og:image.
- Added 104 verified metadata aliases and four unique art variants (Grand Spectrum Emerald and Grip of Kulemak TokenOfPassage02/03/04). Fixed mojibake Mjölner names in both maps.
- Added independent audit tools and final reports. Validation after integration: 836/836 eligible metadata aliases covered, zero missing/conflicts; 454/454 unique art paths covered, zero missing/wrong names/loader collisions.
- Scope excludes non-unique equipment and inactive entries; 855 pages expose no parsed metadata, one catalog category returns 404. This is not proof of all hidden/unlinked game items. Debug LootValue/core and launcher builds passed; deployment/live price validation pending.

## 1.5.13 — 2026-09-13

- Fixed audit parsing to accept gem ItemType metadata instead of requiring Type. Recovered and added all 12 previously unresolved gem metadata aliases from cached detail pages.
- Added Briarpatch unique boots art separately, using the item Icon table instead of the page's gem og:image. Corrects prior unresolved classification; older audit counts remain historical.
- Validation: recovered full metadata paths documented with source URLs; Debug LootValue/core and launcher builds passed. Deployment/live price validation pending.

## 1.5.12 — 2026-09-13

- Added five missing unique item art mappings from cached PoE2DB detail pages: Grand Spectrum (Sapphire art), Grip of Kulemak, Infernoclasp, The Remembered Tales, The Wailing Wall.
- Extended conservative fill tooling to unique detail pages outside aggregate catalog; gem and skill/passive icons are excluded. Metadata mapping remains path-only; no guessed paths or prices added.
- Validation: primary item popups identify the added items as Unique; JSON parsed and Debug LootValue/core and launcher builds passed. Deployment and live price validation pending.

## 1.5.11 — 2026-09-13

- Restricted bundled pathBasenameToItemName to 720 aliases verified against metadata basenames in the audit; moved 1,131 icon/unverified aliases to a review report rather than treating them as metadata.
- Provider IDs/icon aliases now remain in a separate transient runtime dictionary and no longer merge into or save the metadata mapping file. Unique art remains separate.
- Validation: JSON filtering and Preserved Cranium metadata alias checked; Debug LootValue/core and launcher builds passed. Unverified removed aliases may require future real metadata evidence. Deployment pending.

## 1.5.10 — 2026-09-13

- Refined 950 unparsed-metadata pages against both mapping files: 516 covered, 411 without identified item art, 23 unmatched item-art records (15 after excluding DNT/Removed Skill). Added readable/source-linked unresolved report.
- Validation: cached page parsing and normalized mapping comparisons completed. Candidates are not yet verified as tradeable items; documentation/version-only update, no binary rebuild.

## 1.5.9 — 2026-09-13

- Added a readable list of all 1,761 excluded equipment metadata aliases and audit pages without parsed metadata, clarifying that unresolved metadata pages are not necessarily missing unique art mappings.
- Validation: generated list from audit/fill JSON and checked category totals sum to 1,761. Documentation/version-only change; binaries not rebuilt in this step.

## 1.5.8 — 2026-09-13

- Added 679 unambiguous metadata-basename aliases to pathBasenameToItemName from the cached PoE2DB audit. Excluded 1,761 equipment aliases from the initial fill; non-unique equipment is excluded from future fills. Unique art remains in uniqueArtMapping; all art/name entries extracted from the linked Unique_item catalog were already present, so none were duplicated.
- Added a repeatable conservative fill script and provenance report; existing mappings are preserved, unused/DNT items excluded, ambiguous aliases rejected. This does not establish full coverage of hidden or unlinked game items and does not add market prices.
- Validation: both JSON files parsed, expected addition count and Preserved Cranium alias verified; Debug LootValue/core and launcher builds passed. Deployment/live price validation pending.

## 1.5.7 — 2026-09-13

- Added a repeatable cached PoE2DB linked-item mapping audit and a report of missing/conflicting aliases; no ambiguous mappings were merged.
- Validation: audited 64 linked categories and 3,446 detail links; 2,496 pages expose item metadata, 2,814 unambiguous aliases are absent, 335 aliases conflict or are ambiguous. One category returned 404; 950 details lacked parsed item metadata. Scope is the site's linked catalog, not proof of all game items.
- Core and launcher versions synchronized; Debug builds passed. No live-game changes tested.

## 1.5.6 — 2026-09-13

- Added 88 missing icon-basename mappings from live poe.ninja Abyss and LineageSupportGems responses to the bundled mapping file.
- Added AbyssalBenchTicketJewel -> Preserved Cranium metadata alias confirmed by the user's live inspector; no fixed prices added.
- Validation: JSON parsed successfully, provider mapping extraction uses the existing normalization rules; Debug LootValue/core and launcher builds passed. Deployment pending.

## 1.5.5 — 2026-09-13

- Added missing poe.ninja LineageSupportGems exchange category (75 listings returned for Forbidden Rites at audit time). Bumped cache schema to 6 to rebuild incomplete caches.
- Validation: checked 22 exchange type candidates and 16 stash type candidates against live provider endpoints; all eight configured stash types returned data. Additional guessed exchange types returned empty and stash types returned 404, so were not added. Scout live audit was blocked by HTML/403 responses and remains inconclusive.
- Debug LootValue/core and launcher builds passed. No updated in-game price validation or deployment performed.

## 1.5.4 — 2026-09-13

- Fixed missing poe.ninja Abyss currency prices by fetching the Abyss exchange category, including Preserved Cranium.
- Bumped price-cache schema to 5 so old caches missing this category are rebuilt.
- Validation: live provider response for Forbidden Rites contains preserved-cranium in Abyss; Debug LootValue, core and launcher builds passed. Updated in-game overlay is not deployed/tested yet.

## 1.5.3 — 2026-09-13

- Fixed ground entity dumps to include the WorldItem inner item: availability, address, display/internal names, metadata/icon paths, rarity, stack count, components and mod lists.
- Existing outer entity fields are retained; the new WorldItem section is null for other entities and reports unavailable/invalid inner items explicitly.
- Validation: Debug core and launcher builds passed. Updated dump has not been validated live or deployed yet.

## 1.5.2 — 2026-09-12

- Extended UI research to read bounded UI identifiers and one level of payload pointers from visible compact controls; retains pointer addresses and read success for comparisons.
- Expanded traversal to 16,384 nodes/depth 20 with a 250 ms scheduling budget and 2,048 unique payload pointers. Captures remain on demand and report limits; a temporary capture hitch is possible.
- Validation: Debug core and launcher builds passed. Live expanded UI capture and remaining-use identification are pending. Prior 3/2/1/0 samples yielded no exact counter among captured bytes.

## 1.5.1 — 2026-09-12

- Extended on-demand Barrage research with raw 0x80-byte buff records and UI node fields/raw bytes (0x400 per node), retaining unknown fields for comparison.
- UI traversal is bounded by 4,096 nodes, depth 12 and a 100 ms scheduling budget; reports explicitly record truncation. This can cause a short frame hitch on capture and is inactive otherwise.
- Validation: Debug core build passed. Live UI capture and Barrage remaining-use identification are pending; prior component captures at observed 3/2/1/0 confirmed the visual buff disappears at zero.

## 1.5.0 — 2026-09-12

- Added on-demand player buff, active skill and cooldown research snapshots through the loopback API, copied from cached components on the render thread.
- Added Capture-Barrage.ps1 to save snapshots labeled with the remaining uses observed in game. This collects evidence; it does not expose a verified Barrage remaining-use counter.
- Added area capture controls excluding town/hideout; deployment and whole-map live validation remain pending.
- Core and launcher versions synchronized at 1.5.0. Validation: Debug core build passed; no live Barrage capture performed yet.

## 1.4.0 — 2026-09-12

- Added a small hidden background monitor for normal Release gameplay: collect 20 seconds every three minutes, stop instrumentation between rounds, and write bounded reports under logs/performance-monitor.
- Ship start/stop shortcuts and monitor script with the core build; retry while overlay/API is unavailable and prevent duplicate monitor instances.
- Added capture-status and unique capture IDs so the helper checks build/process/round identity and stops only its own capture.
- Validation: capture identity/count/reset tests and Debug/Release builds; helper syntax and background lifecycle checks. Live gameplay reports depend on running the updated deployed core.

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
