# TEHhub Changelog

Version format: MAJOR.MINOR.PATCH (x.x.x). Major versions change compatibility, minor versions add features, patch versions fix bugs or make small improvements. Every completed change updates this file; related edits delivered together share one version.

## 1.8.84 — 2026-09-16 [Plugin Update — NinjaPricer]

- **`NinjaPricer` Normal Map Runeshape & Monolith Full Detection Support**:
  - **Awake + Sleeping Entities Dual Scanning**: `GetActiveMonoliths()` now processes both awake entities and distant sleeping entities so monoliths in large normal maps are discovered across the entire map immediately.
  - **Flexible Entity Path Matching**: Broadened candidate path matching to include all standard Expedition encounter path variants (`Expedition2Encounter`, `ExpeditionEncounter`, etc.).
  - **Dynamic Station Offset Scanning**: Added `[sub - 0x120 .. sub - 0x60]` fallback scan in `TryReadMonolith` to reliably resolve the monolith station structure across all map formats and memory configurations.
- Compatibility: Fully compatible with all TEHhub plugins.
- Validation: Built `TEHhub.sln` in Release configuration with 0 warnings and 0 errors; verified and deployed to game directory.

## 1.8.83 — 2026-09-16 [Plugin Update — myFarming]

- **`myFarming` Restored Total Session Divine / Currency Count in HUD**:
  - **Total Session Currency Display**: Line 2 now explicitly renders the total accumulated session loot/divine count (`Total: +{totalProfit}`) alongside hourly profit rate (`({rateProfit}/h)`) and completed map count (`[{maps} maps]`).
  - **Graceful Currency & Gold Icon Fallbacks**: Added text fallback tags (`Div`, `Ex`, `c`, `Gold`) so currency amounts never break layout if image textures fail to load.
- Compatibility: Fully compatible with all TEHhub plugins.
- Validation: Built `TEHhub.sln` in Release configuration with 0 warnings and 0 errors; verified and deployed to game directory.

## 1.8.82 — 2026-09-16 [Plugin Update — NinjaPricer, SDK / Core Framework Update]

- **`NinjaPricer` Streamlined Network-Bubble Live Coverage & Permanent Memory Retention**:
  - Simplified monolith tracking architecture to a direct two-phase model:
    - **Within Network Range (`AwakeEntities`)**: Continuously updates live monolith status and checks live explosive coverage (`CalculateCoverage`). If a bomb is placed, it is instantly marked covered (`coveredMonolithIds`). If picked up/removed while in range, it updates to un-covered immediately.
    - **Outside Network Range**: Retains the last known coverage state permanently in cache so distant monoliths never lose their green status or disappear when moving far away.
    - **Completion & Transition Cleanup**: Completed/looted monoliths (`ActivatedState >= 7`) are removed from tracking; area transitions clear all cached state.
- **`ExpeditionMechanics` Architecture Simplification**:
  - Removed complex distance-pruning heuristics and static explosive tracking in favor of clean live/sleeping entity scanning directly paired with coverage calculation math.
- Compatibility: Fully compatible with all TEHhub plugins.
- Validation: Built `TEHhub.sln` in Release configuration with 0 warnings and 0 errors; verified and deployed to game directory.

- **`NinjaPricer` Persistent Spatial Monolith Registry**:
  - Implemented `trackedMonoliths` persistent spatial registry: Monoliths discovered across the entire zone remain tracked and visible in both the Runeshape Combination UI window and Large Map (`Tab`) world markers, preventing distant monoliths from vanishing when moving away.
  - Decoupled coverage evaluation from player proximity: Evaluates coverage directly against static monolith world coordinates and all placed explosive locations across the zone.
- **`ExpeditionMechanics` World-Coordinate Coverage Overload**:
  - Added direct `Vector3` coverage calculation overload: `CalculateCoverage(Vector3 targetPos, AreaInstance? area, float? customRadiusWorld)` evaluates 2D blast radius independently of volatile entity pointers.
  - Refined explosive pruning radius to arm's reach (`< 250 W`) to eliminate false-positive bomb un-tracking during movement.
- Compatibility: Fully compatible with all TEHhub plugins.
- Validation: Built `TEHhub.sln` in Release configuration with 0 warnings and 0 errors; verified and deployed to game directory.

## 1.8.80 — 2026-09-16 [Plugin Update — NinjaPricer, SDK / Core Framework Update]

- **`ExpeditionMechanics` Distant Explosive Persistence & Proximity Pruning**:
  - Fixed `TrackedExplosives` synchronization in `ExpeditionMechanics.cs`: Distant explosives beyond the local wake bubble are now persistently retained across the entire zone rather than being wiped when the player moves away.
  - Placed explosives are now only pruned when the player is within proximity (`< 1200 W`) and the explosive entity is genuinely missing (undone/deleted by the player).
- **`NinjaPricer` Monolith Coverage Persistence**:
  - Enhanced `MonolithData` and `NinjaPricerCore.cs` with `coveredMonolithIds` (unique entity ID tracking) alongside entity address tracking.
  - Sticky coverage persistence ensures covered Expedition Remnant monoliths remain green even when the player travels far across the map.
  - Undone bombs in proximity properly un-green the monolith in real time, and completed monoliths (`ActivatedState >= 7` or looted) cleanly disappear.
- Compatibility: Fully compatible with all TEHhub plugins.
- Validation: Built `TEHhub.sln` in Release configuration with 0 warnings and 0 errors; verified and deployed to game directory.

## 1.8.79 — 2026-09-16 [Plugin Update — myFarming]

- **`myFarming` Multi-Layer Hybrid Gold Tracking Engine**:
  - **Ground Gold Auto-Loot Tracking**: Monitors dropped gold piles (`Metadata/Items/Currency/Gold*`, `CoinPile*`, `Stack.Count`) and registers live auto-pickup gain as the player runs over gold in combat maps.
  - **Dynamic UI Gold Reading**: Scans `GameUi.RightPanel` (inventory) for gold counter text and calibrates exact player gold balance.
  - **Adaptive Memory Offset Auto-Calibration**: Replaced static candidate locking with dynamic baseline delta matching across `PlayerServerData` to automatically locate and bind the true live gold memory offset without false positives.
  - **Persistent Line 3 HUD Indicator**: Map gold counter is now continuously visible on Line 3 whenever `ShowGold` is enabled (starting from `+0` and updating in real-time as gold is looted).
- Compatibility: Fully compatible with all TEHhub plugins.
- Validation: Built `TEHhub.sln` in Release configuration with 0 warnings and 0 errors; verified and deployed to game directory.

## 1.8.78 — 2026-09-16 [Plugin Update — myFarming & NinjaPricer, SDK / Core Framework Update]

- **`myFarming` HUD Overlay Redesign & Daily Rolling Sessions**:
  - **Modern Compact 3-Line HUD**: Redesigned overlay into an ultra-clean 3-line floating display matching reference aesthetic:
    - **Line 1 (Kill Badges)**: Rounded colored badges with counts: `◻ White (normal) 🟦 Blue (magic) 🟨 Yellow (rare) 🟧 Orange (unique) (total)`.
    - **Line 2 (Daily Session & Profit & Gold)**: `Session: {time} | Profit: {profitVal} [currency icon]/h [{totalMaps} maps] | {sessionGold} [gold icon]`.
    - **Line 3 (Current Map / Status & Gold)**: `{mapName} ({time}) | +{lootVal} [currency icon] | +{mapGold} [gold icon]`.
  - **Daily Rolling Sessions**: Replaced static numbered sessions with automatic daily rolling sessions stored as `sessions/session_YYYY-MM-DD.json`. Daily sessions automatically advance on midnight date rollover.
  - **Gold Tracking & k/m Formatting**: Added real-time Player Gold memory tracking (`GoldTracker`) with clean compact scaling units (`1k`, `10k`, `100k`, `1.0m`, `10.0m`, `100.0m`) and custom high-resolution `gold.png` coin icon.
  - **Smart Panel Hiding**: Automatically suppresses HUD overlay when blocking UI panels (Stash, Inventory, Passives, Vendors, etc.) are open while keeping the HUD visible over Large Map (`Tab`).
  - **Background Opacity & Borderless Lock Mode**: Added opacity slider (0% to 100%) and complete header/close `[X]` removal in locked mode for a sleek borderless HUD.
  - **Optimized 0.5s Scan Rate**: Tuned background item/gold/kill update tick to a smooth and lightweight 500ms interval.
- **`NinjaPricer` & `ExpeditionMechanics` Monolith Undone-Bomb Un-Green Fix**:
  - Fixed `TrackedExplosives` caching in `ExpeditionMechanics.cs` to synchronize and immediately purge bombs undone/deleted by the player.
  - Fixed `NinjaPricerCore.cs` pre-detonation coverage logic so removing an explosive immediately turns the monolith un-green in real-time.
- Compatibility: Fully compatible with all TEHhub plugins.
- Validation: Built `TEHhub.sln` in Release configuration with 0 warnings and 0 errors; verified and deployed to game directory.

## 1.8.77 — 2026-09-16 [Plugin Update — NinjaPricer, SDK / Core Framework Update]

- **`State` & `Entity` Placed Explosives Retention Across Distances**:
  - Added `Metadata/MiscellaneousObjects/Expedition/ExpeditionExplosive` and `Metadata/MiscellaneousObjects/Expedition/ExpeditionDynamite` to `SpecialMiscObjPaths` (group 100) so placed explosive bombs are never culled or lost when moving far away.
  - Automatically ensures missing required special misc paths on existing settings deserialization.
- **`NinjaPricer` Clean Direct Entity Iteration**:
  - Replaced persistent dictionary caching in `GetActiveMonoliths` with clean, direct `AwakeEntities` enumeration.
  - Eliminates duplicate rows naturally without requiring spatial clustering or heuristics.
  - Integrated `ExpeditionMechanics.HasPlacedExplosives` for persistent explosive coverage detection across the entire area.
- Compatibility: Fully compatible with all TEHhub plugins.
- Validation: Built `TEHhub.sln` in Release configuration with 0 warnings and 0 errors; deployed to target directory.

## 1.8.76 — 2026-09-16 [Plugin Update — NinjaPricer]

- **`NinjaPricer` Runeshape Duplicate Row Deduplication & Premature Green Highlight Fix**:
  - **Runeshape Monolith Spatial Deduplication**: Resolved duplicate entity handles and duplicate rows in the Runeshape window by introducing multi-pass spatial proximity clustering (< 100 World Units). Each physical monolith station in the world now displays exactly once in the list.
  - **Zero-Explosive Uncoverage & False-Positive Green Elimination**: Guaranteed that monoliths never highlight green before explosives are actually placed. All coverage flags and cached addresses are strictly cleared when zero bombs are on the ground pre-detonation.
  - **Accurate Sticky Detonation State**: Green highlight is only preserved post-detonation while monsters are spawning and rewards are collected.
- Compatibility: Fully compatible with all TEHhub plugins.
- Validation: Built `TEHhub.sln` in Release configuration with 0 warnings and 0 errors; deployed to target directory.

## 1.8.75 — 2026-09-16 [SDK / Core Framework Update]

- **`ExpeditionMechanics` Correct Placed Explosive Entity Filtering**:
  - Fixed `IsExplosiveEntity` in `ExpeditionMechanics.cs` to explicitly exclude `ExpeditionExplosiveFuse` (fuse wire), `ExpeditionDetonator`, `ExpeditionConnectorPole`, and placement indicators from being mistakenly classified as placed explosive bombs.
  - Previously, the fuse wire entity connected to the detonator was matched as an explosive, causing monoliths and remnants near the detonator to turn green before any bomb was placed.
  - Blast radius and green highlight coverage calculations now strictly and accurately originate only from real placed explosive bombs.
- Compatibility: Fully compatible with all TEHhub plugins.
- Validation: Built `TEHhub.sln` in Release configuration with 0 warnings and 0 errors; fully deployed and verified all synchronization.

## 1.8.74 — 2026-09-16 [Plugin Update — NinjaPricer]

- **`NinjaPricer` Global Price Hide When Hovering Over Any Item / Slot / Loot**:
  - Upgraded hover-hide behavior: when pointing the mouse over ANY item (in inventory, stash, or on the ground), ALL price overlay badges hide completely across the screen so tooltips and item affixes are 100% visible and unobscured.
  - Added comprehensive tracking of all visible item slot rectangles (`cachedAllItemRects`) to immediately trigger the hide even on unpriced items.
  - All price badges instantly reappear as soon as the mouse is moved away from items.
- Compatibility: Fully compatible with all TEHhub plugins.
- Validation: Built `TEHhub.sln` in Release configuration with 0 warnings and 0 errors; fully deployed and verified all synchronization.

## 1.8.73 — 2026-09-16 [Plugin Update — NinjaPricer]

- **`NinjaPricer` Runeshape Window Title & Localization Asset Synchronization**:
  - Replaced dynamic title with direct `Runeshape###RuneshapeWindow` in `ImGui.Begin` to guarantee the title displays strictly as `"Runeshape"` across all window states.
  - Fully synchronized all 11 localization JSON dictionaries (`th-TH.json`, `en-US.json`, etc.) and plugin binary assets to `C:\Games\Hy-v Tool\DXPEOE\TEHhub`.
- Compatibility: Fully compatible with all TEHhub plugins.
- Validation: Built `TEHhub.sln` in Release configuration with 0 warnings and 0 errors; fully deployed and verified all localization and binary timestamps.

## 1.8.72 — 2026-09-16 [Plugin Update — NinjaPricer]

- **`NinjaPricer` Runeshape Window Title Simplification**:
  - Simplified the Runeshape window title to just `"Runeshape"` across all localizations and headers as requested.
- Compatibility: Fully compatible with all TEHhub plugins.
- Validation: Built `TEHhub.sln` in Release configuration with 0 warnings and 0 errors; synced directly to installation directory.

## 1.8.71 — 2026-09-16 [Plugin Update — NinjaPricer]

- **`NinjaPricer` Hide Slot & Ground Prices on Mouse Hover (Enabled by Default)**:
  - **Hide on Hover for Inventory & Stash Slots**:
    - Added `HideSlotPriceOnHover` setting (enabled by default) and checkbox in settings.
    - When hovering the mouse cursor over an item slot in Inventory or Stash, its price tag overlay automatically hides so the in-game item tooltip and stats are crystal clear and unobscured.
    - Added mouse bounding-box check for ground loot price tags to hide when hovered as well.
- Compatibility: Fully compatible with all TEHhub plugins.
- Validation: Audited all scaling and hover detection code; built `TEHhub.sln` in Release configuration with 0 warnings and 0 errors; synced directly to installation directory.

## 1.8.70 — 2026-09-16 [Plugin Update — NinjaPricer]

- **`NinjaPricer` Independent Text & UI Scaling for Ninja vs. Expedition + Clean Header Bar**:
  - **Independent Scaling Sliders**:
    - Separated `TextScale` & `UiScale` (for Ninja dropped items, inventory, stash, and ritual shop) from `ExpeditionTextScale` & `ExpeditionUiScale` (for LargeMap badges, 3D world markers, and Runeshape overlay).
    - Added dedicated scaling sliders for Expedition right under the **Expedition** settings tab.
  - **Removed `+`, `-`, and `Mini` / `Full` Buttons from Runeshape Sort Bar**:
    - Cleaned up the Runeshape header bar to contain only the essential `Weight` and `Price` sort buttons, keeping the window compact and uncluttered.
- Compatibility: Fully compatible with all TEHhub plugins.
- Validation: Built `TEHhub.sln` in Release configuration with 0 warnings and 0 errors; synced directly to installation directory.

## 1.8.69 — 2026-09-16 [Plugin Update — NinjaPricer]

- **`NinjaPricer` Expedition Tab UI Reorganization & LargeMap Settings Clarity**:
  - **Prominent LargeMap & World Markers Section**:
    - Placed the **LargeMap & World Markers** section right at the top of the **Expedition** tab.
    - Moved the `Minimal LargeMap/World badges (Color + Price only)` toggle out of the sub-tree dropdown so it is immediately visible and easily accessible.
    - Added full Thai localization and descriptive tooltips for LargeMap minimal badges, header elements, and expanded recipe rows.
- Compatibility: Fully compatible with all TEHhub plugins.
- Validation: Built `TEHhub.sln` in Release configuration with 0 warnings and 0 errors; synced directly to installation directory.

## 1.8.68 — 2026-09-16 [Plugin Update — NinjaPricer]

- **`NinjaPricer` Expanded Recipe List Visual Toggles Fix**:
  - **Hooked `RsShowRowName` into Child Row Text Rendering**:
    - Fixed an issue where the `Reward name` checkbox under `Expanded list elements:` in settings had no effect.
    - Accurately toggles the reward item name text string on/off inside expanded monolith recipe rows while keeping quantity and price intact as configured.
- Compatibility: Fully compatible with all TEHhub plugins.
- Validation: Built `TEHhub.sln` in Release configuration with 0 warnings and 0 errors; synced directly to installation directory.

## 1.8.67 — 2026-09-16 [Plugin Update — NinjaPricer]

- **`NinjaPricer` Minimal Map/World Marker Mode & Row Isolation Fix**:
  - **Minimal Map Badges Mode (`Mini` / `Full` Toggle)**:
    - Added quick `Mini` / `Full` mode button on the Runeshape sort bar and in settings.
    - When Minimal mode is enabled, LargeMap badges and 3D world markers hide the rune sockets row and reward item icons, rendering a clean, ultra-compact chip with **only the Monolith Color Square + Currency Icon + Best Price**.
  - **Row Isolation & State Reset Fix**:
    - Wrapped each monolith row in `ImGui.PushID(mIdx)` and unique widget IDs to eliminate any cross-talk between rows when clicking headers.
    - Added `expandedMonoliths.Clear()` to `OnAreaChange` so every new area starts in a clean collapsed state.
- Compatibility: Fully compatible with all TEHhub plugins.
- Validation: Built `TEHhub.sln` in Release configuration with 0 warnings and 0 errors; packaged to standalone zip artifact.

## 1.8.66 — 2026-09-16 [Plugin Update — NinjaPricer]

- **`NinjaPricer` Dynamic Auto-Sizing & Expand/Collapse All for Runeshape Overlay**:
  - **Dynamic Content-Adaptive Window Sizing**:
    - Replaced static `availW` calculation with per-frame natural content width measurement (`maxContentW`) across sort bar controls, monolith headers, and expanded recipe rows.
    - Window smoothly expands and shrinks dynamically in both width and height when rows are expanded/collapsed or when monolith counts change.
  - **Expand All (+) & Collapse All (-) Controls**:
    - Added quick action buttons `+` and `-` directly on the sort header bar to collapse all rows into a sleek minimal box or expand all recipe details at once.
- Compatibility: Fully compatible with all TEHhub plugins.
- Validation: Built `TEHhub.sln` in Release configuration with 0 warnings and 0 errors; packaged to standalone zip artifact.

## 1.8.65 — 2026-09-16 [Plugin Update — NinjaPricer]

- **`NinjaPricer` Revert Iron Rune Filter**:
  - Removed Iron Rune recipe filter to restore full default recipe listings as intended (clarified user request regarding map icons vs. iron runes).
- Compatibility: Fully compatible with all TEHhub plugins.
- Validation: Built `TEHhub.sln` in Release configuration with 0 warnings and 0 errors.

## 1.8.64 — 2026-09-16 [Plugin Update — NinjaPricer / Radar] & [SDK / Core Framework Update]

- **`NinjaPricer` / `Radar` / Core UI Overlap Prevention & Clutter Reduction**:
  - **Auto-Hide Overlays When In-Game Rune Selection Panel Is Open**:
    - `ImportantUiElements.IsAnyLargePanelOpen` now incorporates `RuneshapeCombinationsPanel` visibility, causing Radar LargeMap drawings and full-screen map badges to suppress automatically while browsing recipes at a pillar.
    - `NinjaPricer` 3D world markers & LargeMap monolith badges now explicitly return early when `RuneshapeCombinationsPanel` is open, eliminating UI clutter and overlay stacking directly over the in-game pillar selection menu.
  - **FullHD / 1080p Compact Runeshape Window Layout**:
    - Added `RsCompactRows` (default `true`) and `RsRowScale` (slider `0.5x` - `1.0x`, default `0.85x`) settings.
    - Compacts header heights, rune socket sizes, badge dimensions, and vertical item padding so all 15+ Expedition monolith rows fit cleanly on 1080p FullHD displays without overflowing or requiring excessive scrolling.
  - **Persistent Distant Monolith & Explosive Tracking**:
    - Enhanced `ExpeditionMechanics` with area-scoped `TrackedExplosives` coordinate persistence and `cachedAreaMonoliths`, preserving green coverage highlights even when the player walks far across the map beyond the active entity wake bubble.
- Compatibility: Fully compatible with all TEHhub plugins.
- Validation: Built `TEHhub.sln` in Release configuration with 0 warnings and 0 errors.

## 1.8.63 — 2026-09-16 [Plugin Update — NinjaPricer]

- **`NinjaPricer` Expedition Monolith Sticky Coverage & Auto-Hide Lifecycle**:
  - **Persistent Detonation Coverage**: Monoliths covered by placed explosives maintain their active green highlight (`IsCoveredByExplosive = true`) even after the detonator is activated and placed explosive entities are destroyed by the game engine.
  - **Dynamic Undo/Repositioning Support**: If an explosive is picked up or moved away during the placement phase, the un-covered monolith cleanly clears its green coverage state.
  - **Auto-Hide on Completion**: Completed/looted monoliths (`IsCompleted == true`, `activated >= 7` or bypassed `activated == 8`) are immediately removed from active tracking and hidden from both 3D world markers and the Runeshape window overlay.
- Compatibility: Fully compatible with all TEHhub plugins.
- Validation: Built `TEHhub.sln` in Release configuration with 0 warnings and 0 errors; deployed cleanly to `C:\Games\Hy-v Tool\DXPEOE\TEHhub`.

## 1.8.62 — 2026-09-16 [SDK & Plugin Update]

- **SDK / Core Framework: Authoritative Expedition Mechanics & Proximity Detection API**:
  - **`ExpeditionMechanics` Helper**:
    - Centralized authoritative PoE 2 Expedition constants: Regular Expedition (5 base explosives, 300 W / 3.0m blast radius, 1,000 W / 10.0m reach) and Grand Expedition (15 base explosives, 380 W / 3.8m blast radius, 1,200 W / 12.0m reach).
    - Added automatic Grand Expedition zone identification via Area ID (`ExpeditionLogBook_*`, `ExpeditionSubArea_*`) and Map Name in addition to active map modifiers.
    - Strictly enforced PoE engine flooring rule (`Math.Floor` for explosive counts).
    - Robust dynamic parsing of active area/map modifiers: extracts percentage values directly from UI modifier text when `Values.Value0` is `NaN` (e.g. `42% increased number of Expedition Explosives` correctly adds +42% to scale 15 base bombs to 21).
    - Exposed `area.ExpeditionConfig` on `AreaInstance`.
  - **`Entity` Proximity & Coverage API**:
    - Added `DistanceWorldFrom(Entity other)` and `Distance3DWorldFrom(Entity other)` for horizontal 2D and 3D World Unit calculations.
    - Added `IsExpeditionEncounter` property to identify Remnant monolith entities.
    - Added `IsInRangeOfExplosive(Entity explosive, float? customRadiusWorld)` and `GetExpeditionExplosiveCoverage()` to check whether placed explosives cover the entity.
    - Added `ExpeditionExplosiveCoverage` struct tracking coverage status, covering explosive ID, exact distance, and closest explosive.
- **Core UI & Data Visualization**:
  - **Entities Tab Restoration**: Restored standard `entity.ToImGui()` rendering in `DataVisualization` -> `Entities` tab, eliminating dummy `"No custom ImGui widget implemented for ..."` entries and providing full entity header, Remnant rune/explosive coverage, and clean collapsible `Components` tree with `[Load]` buttons.
  - **Area Modifiers Display**: Separated `Active Area / Map Modifiers` into its own distinct collapsible header in `DataVisualization` -> `Player & Area`.
- **Plugins Update**:
  - **`myFarming`**: Removed kill tracking accumulation from Session Summary; strictly scoped monster kill counters (`W: M: R: U:`) to the active combat map only.
  - **`NinjaPricer`**: Added green border highlights on 3D world monolith markers and Runeshape window header rows when a placed explosive covers the monolith.
- Compatibility: Fully compatible with all TEHhub plugins.
- Validation: Built `TEHhub.sln` in Release configuration with 0 warnings and 0 errors; verified clean builds.

## 1.8.61 — 2026-09-16 [SDK / Core Framework Update]

- **Core Framework: Fix ImGui Duplicate ID Conflict in GGPK Caches**:
  - Added unique identifier parameter and `PushID`/`PopID` boundary to `GgpkAddresses.ToImGui()`.
  - Resolved `Programmer error: 2 visible items with conflicting ID` warning when viewing both `GGPK String Data Cache` and `GGPK Object Cache` in Memory diagnostics.
- Compatibility: Fully compatible with all TEHhub plugins.
- Validation: Built `TEHhub.sln` in Release configuration with 0 warnings and 0 errors; passed test suites.

## 1.8.60 — 2026-09-16 [SDK / Core Framework Update]

- **Core Framework: Data Visualization 2.0 (12-Tab Inspector Suite)**:
  - Completely redesigned `DataVisualization` from a legacy vertical list into an intuitive 12-tab inspector suite.
  - **`Area & Vitals`**: Current world area metadata, level, hash, active area/map modifiers, player HP/MP/ES progress bars, and coordinates.
  - **`Buffs`**: Interactive, searchable table of all active player buffs (charges, stage, time left, one-click copy).
  - **`Entities`**: Filterable Awake and Sleeping entity lists by ID/Path/Rarity with one-click JSON dump and sleeping scanner.
  - **`Inventories`**: ComboBox selector for all player inventories with item grid details and modifiers.
  - **`Flasks & Charms`**: Dedicated 5-slot flask inspector and charms overview.
  - **`Memory`**: Base address, static addresses, GGPK caches, and memory settings.
  - **`UI Explorer`**: GameUi hierarchy inspector with controller mode status and visibility flags.
  - **`Components`**: Deep component inspector for local player and mouse-hovered entity.
  - **`Render`**: Resolution, black-bar cull sizes, camera 4x4 matrix, and interactive WorldToScreen coordinate tester.
  - **`Terrain`**: Tile metadata, walkable grid bytes, heightmap, and Tgt landmarks.
  - **`Events`**: Core lifecycle ticks and memory diagnostic status.
  - **`Skills & Timing`**: Player actor active skills, usability flags, use stages, and cast types.
- Compatibility: Fully compatible with all TEHhub plugins.
- Validation: Built `TEHhub.sln` in Release configuration with 0 warnings and 0 errors; passed test suites.

## 1.8.59 — 2026-09-16 [SDK / Core Framework Update]

- **Core Framework: Map & Area Modifiers SDK**:
  - Added new `AreaMod` data structure in `TEHhub.RemoteObjects.States.InGameStateObjects` containing `RawName`, `DisplayName`, `Values (Value0, Value1)`, and `ModRecordPtr`.
  - Implemented memory parsing for active zone/map modifiers in `ServerData` and exposed via `Core.States.InGameStateObject.CurrentAreaInstance.AreaMods` and `AreaModNames`.
  - Added helper methods in `AreaInstance`: `HasMod(string modNameFragment)` and `TryGetMod(string modNameFragment, out AreaMod? mod)` for convenient consumption across all plugins.
  - Added interactive "Area / Map Modifiers" tree nodes in both `ServerData` and `AreaInstance` ImGui widgets and DataVisualization, with one-click copy and adaptive memory probing.
- Compatibility: Fully compatible with all TEHhub plugins.
- Validation: Built `TEHhub.sln` in Release configuration with 0 warnings and 0 errors; passed test suites.

## 1.8.58 — 2026-09-16 [SDK / Core Framework Update]

- **Core Framework: Centralized Master Switch for Inactive Overlay Hiding**:
  - Moved overlay background/inactive visibility checking into Core (`PManager.DrawPluginUiRenderCoroutine`).
  - Added global setting `HideOverlaysWhenGameInactive` (enabled by default) and configurable in Settings under Miscellaneous Config.
  - When the user switches away from the game, plugin UI rendering (`DrawUI()`) is automatically bypassed across all plugins at the core level, saving CPU and GPU render cycles while preserving background data processing (`PerFrameDataUpdate`).
- Compatibility: Fully compatible with all TEHhub plugins.
- Validation: Built `TEHhub.sln` in Release configuration with 0 warnings and 0 errors.

## 1.8.57 — 2026-09-16 [SDK / Core Framework Update]

- **Core Framework: Robust Foreground Window & Focus Handling**:
  - Updated `GameProcess.UpdateIsForeground()` to treat both the game process (`PathOfExile2.exe`) and the TEHhub process (`Environment.ProcessId`) as foreground.
  - Clicking on any overlay UI element, settings panel, or plugin widget no longer causes the overlay to falsely detect a background/alt-tab state and disappear.
  - Alt-Tabbing away to external applications (e.g. browser, Discord, desktop) continues to reliably hide all background-sensitive overlays as intended.
  - Reduced monitor loop polling interval from 1.0s to 0.2s for instantaneous focus and window bounds tracking.
- Compatibility: Fully compatible with all TEHhub plugins.
- Validation: Built `TEHhub.sln` in Release configuration with 0 warnings and 0 errors.

## 1.8.56 — 2026-09-16 [Plugin Update — myFarming]

- **myFarming: Dynamic Proportional Currency Icons**:
  - Increased currency icon sizes (Divine, Exalted, Chaos) to dynamically scale with line height and text scale (1.4x line height), making currency badges noticeably larger, clearer, and vertically centered with currency values.
- Compatibility: Fully compatible with all TEHhub plugins.
- Validation: Built `TEHhub.sln` in Release configuration with 0 warnings and 0 errors.

## 1.8.55 — 2026-09-16 [Plugin Update — NinjaPricer]

- **Runeshape: Unified Sort Button (Combined Icon + Text)**:
  - Replaced the separate ImageButton and TextButton layout with a single unified, responsive button widget (`DrawSortButton`) embedding the high-resolution Weight/Price icon alongside the label.
  - Removed unsupported emoji characters from all 11 localization files (`en-US`, `th-TH`, etc.) that were displaying as broken `?` symbols.
- **Runeshape: Monolith Rows Default to Closed / Collapsed**:
  - Monolith recipe rows now default to closed / collapsed when viewing the Runeshape window, keeping the view clean and compact until explicitly clicked to expand.
  - Expanding or collapsing a monolith continues to be completely isolated per-monolith.
- Compatibility: Fully compatible with all TEHhub plugins.
- Validation: Built `TEHhub.sln` in Release configuration with 0 warnings and 0 errors.

## 1.8.54 — 2026-09-16 [Plugin Update — myFarming]

- **myFarming: Dedicated Session State Persistence (`session.json`)**:
  - Decoupled active multi-map farming session data from `settings.json` into its own dedicated `configs/plugins/myFarming/session.json` storage managed by `SessionStore`.
  - Closing, restarting, or resetting UI settings in TEHhub will never wipe or reset active farming session progress (maps, time, loot, kills).
  - Session statistics can be independently inspected, backed up, or managed without touching plugin configuration.
- Compatibility: Fully compatible with all TEHhub plugins.
- Validation: Built `TEHhub.sln` in Release configuration with 0 warnings and 0 errors.

## 1.8.53 — 2026-09-16 [Plugin Update — myFarming & NinjaPricer]

- **NinjaPricer / Runeshape: Weight & Price Icons**:
  - Added dedicated crisp high-resolution UI textures for Weight (`resources/runeshape/ui/Weight.png`) and Price (`resources/runeshape/ui/Price.png`).
  - Integrated Weight and Price icons into the Runeshape sort toggle buttons, in-world 3D monolith badges, window headers, and recipe rows.
- **myFarming: Precise Monster Kill Tracker**:
  - Rewrote monster kill tracking to require observing monsters in an active **Alive** state (`Health.Current > 0`) before transitioning to death, eliminating overcounting (e.g. killing 1 counting as 4).
  - Filtered out friendly summons, minions, pets, and allies (`EntityStates.MonsterFriendly`, `Positioned.IsFriendly`, minion paths).
  - Removed flawed despawn radius guessing (`dist < 120f`) and pre-existing dead corpse counting.
- **myFarming: Overlay UI De-duplication & Button Cleanup**:
  - Removed manual "Finish Map" and "Resume / Pause" buttons from the overlay HUD, as map transitions and pause states are handled automatically.
  - Eliminated duplicate kills/loot lines when running a single map session.
  - Formatted looted items cleanly with bullet points, removing misleading leading chaos icons on non-chaos items.
- Compatibility: Fully compatible with all TEHhub plugins.
- Validation: Built `TEHhub.sln` in Release configuration with 0 warnings and 0 errors.

## 1.8.52 — 2026-09-16 [Plugin Update — NinjaPricer]

- **Runeshape: Synchronized Price & Weight Sorting Across World Markers & UI Window**:
  - Monoliths and recipes now strictly sort according to the active sort mode (`💰 Price` vs `⚖ Weight`) across all layers:
    - **In-World & Map Badges (`#1, #2...`)**: Ordered globally by the selected sort mode before badge colors and ranks are assigned.
    - **Runeshape Window Rows**: Monoliths are ordered matching the active sort mode.
    - **Recipe List Offers**: Recipes inside each monolith are sorted by Price DESC (with Weight tie-breaker) in Price mode, or by Weight DESC (with Price tie-breaker) in Weight mode.
- **Runeshape Window: Per-Entity Monolith Collapse Isolation**:
  - Fixed an issue where expanding or collapsing a monolith would cause other monoliths to open/close simultaneously or carry over unwanted collapse state between maps.
  - Replaced shared color-palette collapse keys with isolated per-entity address tracking (`collapsedMonoliths`), ensuring clicking one monolith only affects that specific monolith.
- **Runeshape Window: Cleaned up UI**:
  - Removed the redundant "All Recipes Catalog" and search tree from the bottom of the active Runeshape window to keep the window fast, compact, and strictly focused on active map monoliths.
- Compatibility: Fully compatible with all TEHhub plugins.
- Validation: Built `TEHhub.sln` in Release configuration with 0 warnings and 0 errors.

## 1.8.51 — 2026-09-16 [Plugin Update — myFarming]

- **Resource Centralization**:
  - Centralized shared resources into root `resources/` folder: `resources/currency/poe2/` (Chaos, Divine, Exalted, etc.), `resources/radar/icons.png` (monster & entity radar spritesheet), and `resources/runeshape/expedition2_recipes.json` (Runeshape recipes).
  - Removed deprecated `ExpeditionPlanner` plugin and cleaned up solution project files and `InternalsVisibleTo` attributes.
- **myFarming: Currency Icon Rendering**:
  - Added inline currency icon textures (Exalted, Divine, Chaos) rendered using `ImGui.Image` next to Loot values, Profit/Hour rates, and looted items list rows.
- **myFarming: Multi-Map Session Retention & Persistence**:
  - Active farming session totals (completed map count, total duration, accumulated profit, profit rate, and monster kills) now persist across maps and hideout roundtrips until the player explicitly clicks "New Session" / "Reset Session" (`จำ session ล่าสุดไว้ตลอดจนกว่าจะกดปิดเอง`).
  - Added one-click "New Session" button directly into the HUD overlay header and the General settings tab.
- **myFarming: Monster Kill Rarity Breakdown**:
  - Fixed monster rarity tracking to read `ObjectMagicProperties.Rarity` instead of uninitialized mod properties.
  - Added colored breakdown for White/Normal (`W`), Magic (`M`), Rare (`R`), and Unique (`U`) kills in both the HUD overlay and settings summary.
- **myFarming: Foreground Window Handling**:
  - Overlay respects game window and TEHhub launcher window focus to prevent unwanted hiding while interacting with overlay controls.
- Compatibility: Fully compatible with all TEHhub plugins.
- Validation: Built `TEHhub.sln` in Release configuration with 0 warnings and 0 errors.

## 1.8.50 — 2026-09-16 [Plugin Update — NinjaPricer & myFarming]

- **NinjaPricer: Runeshape Window Layout & Auto-Resize**:
  - Fixed an issue where the monolith combination weight (e.g. `+120`, `+80`) and prices on the right side of monolith header rows were cut off / clipped.
  - Calculated exact required row width (`badges + runes + price + weight + padding`) and applied it to the header layout button so `AlwaysAutoResize` calculates the full window width properly.
- **myFarming: Game Focus Awareness**:
  - Added `Hide When Game Not Focused` option (`HideWhenGameNotFocused`) to hide the HUD overlay when the game is minimized or backgrounded.
- Compatibility: Fully compatible with all TEHhub plugins.
- Validation: Built `TEHhub.sln` in Release configuration with 0 warnings and 0 errors.

## 1.8.49 — 2026-09-16 [Plugin Update — myFarming]

- **Game Pause & Escape Menu Detection**:
  - Added support for `GameStateTypes.EscapeState` (Solo Play true pause) and loading screen states.
  - When the player presses ESC to pause the game in solo play, `myFarming` automatically enters paused state (`⏸ PAUSED`), freezes the map duration timer, and prevents elapsed paused time from inflating map duration when unpausing.
- Compatibility: Fully compatible with all TEHhub plugins.
- Validation: Built `TEHhub.sln` in Release configuration with 0 warnings and 0 errors.

## 1.8.48 — 2026-09-16 [New Plugin — myFarming & Plugin Update — NinjaPricer]

- **New Plugin: `myFarming` (Ported from FarmCounter)**:
  - Added native C# farming session & backpack loot tracker plugin for TEHhub.
  - **Live Backpack Loot Diffing**: Snapshots the player's `MainInventory1` backpack upon entering maps with a stability gate (waits for 2 consecutive stable reads to avoid false loot from lazy inventory loading) and computes added loot quantities.
  - **Hideout Roundtrips**: Automatically pauses when portaling to town or hideout and resumes tracking when returning to the same map instance, preserving carryover loot.
  - **Auto-Pricing & Conversion**: Integrates with price cache (from NinjaPricer / poe2scout / poe.ninja) with fallback currency prices and supports user-defined custom price overrides.
  - **Map & Session Timers**: Displays map duration and session duration with real-time Profit / Hour rate calculation.
  - **Kill Tracking**: Tracks killed monsters categorized by rarity (Normal, Magic, Rare, Unique).
  - **Floating HUD Overlay**: Configurable in-game HUD with movable/lockable position, font scale, profit rate, and collapsible looted items list.
  - **Statistics & History**: Records completed runs to JSON history file with all-time summaries and run-by-run details.
- **NinjaPricer: Runeshape UI & Hotkey Fixes**:
  - **Runeshape Window Layout**: Removed the triangle arrow `▶` in front of each monolith row and moved the rune sockets forward directly adjacent to the colored monolith badge.
  - **Hotkey Capture Fix**: Fixed the `Clear` button in hotkey settings rows so it properly resets the hotkey binding to None and persists to settings immediately.
  - **Hide When Game Not Focused**: Moved the `HideWhenUnfocused` and hold-to-hide hotkey check before the Runeshape monolith world markers and overlay window so they properly hide when the game is unfocused.
- Compatibility: New Plugin (`myFarming`), TEHhub SDK (`InternalsVisibleTo`).
- Validation: Built `TEHhub.sln` in Release configuration with 0 warnings and 0 errors.

## 1.8.47 — 2026-09-15 [Plugin Update — NinjaPricer]

- **Fix Ground Price Tag Position**:
  - Price labels on dropped items now anchor to `Render.WorldPosition.Z` (the entity's label/healthbar height) instead of `TerrainHeight` (floor level).
  - This makes the price tag stay locked to the same floating position as the game's item name label, instead of drifting above/below it based on camera angle and terrain slope.
- Compatibility: Plugin update (NinjaPricer).
- Validation: Built `TEHhub.sln` in Release configuration with 0 warnings and 0 errors.

## 1.8.46 — 2026-09-15 [Plugin Update — NinjaPricer]

- **Runeshape Window: Sort Mode Toggle**:
  - Added `⚖ Weight` / `💰 Price` toggle buttons directly inside the Runeshape overlay window.
  - Active sort mode button is highlighted (green = Weight, blue = Price); inactive button is dimmed.
  - Clicking either button immediately switches `RsPrioritizeWeight` and saves settings — no need to open the settings panel.
- Compatibility: Plugin update (NinjaPricer).
- Validation: Built `TEHhub.sln` in Release configuration with 0 warnings and 0 errors.

## 1.8.45 — 2026-09-15 [Plugin Update — NinjaPricer]

- **New Expedition Settings Tab**:
  - Consolidated all Runeshape/Monolith-related settings into a new dedicated **Expedition** tab in NinjaPricer settings, replacing the scattered controls in the Overlay Toggles tab.
  - Tab sections: Runeshape & Monolith Settings (Enable toggle, priority weight, scale/color/range sliders) and Rune Weights.
- **Rune Weights Profile Editor**:
  - Added per-rune weight editor with named profiles (default: "Default").
  - Sliders grouped by rarity (Rare runes: Opulent, Power, Bond, Sky, Death, Soul, Earth, Time, Life, Ward, Oath; Common runes: all others).
  - Reset button restores all weights to the built-in default table; Zero button sets all to 0.
  - Profile weights are saved to settings and persisted across sessions.
  - Changing any slider immediately recalculates `ComboWeight` for all cached recipes.
- **`MonolithData.ActivatedState` Field**:
  - Added `ActivatedState` (int) to `MonolithData` to track the current StateMachine `activated` value for each monolith.
- **State 3 Documentation Corrected**:
  - Fixed comment in `NinjaRuneshapeHelper.cs`: state `3` means *detonator button pressed / detonation sequence initiated*, not "connected with explosive bomb, waiting to detonate".
- Compatibility: Plugin update (NinjaPricer).
- Validation: Built `TEHhub.sln` in Release configuration with 0 warnings and 0 errors.

## 1.8.44 — 2026-09-15 [Plugin Update — NinjaPricer]

- **Separate UI Size & Text Size Scaling**:
  - Added dedicated UI size slider (`UiScale`, range 0.5x to 2.5x) independent from Text size (`TextScale`).
  - `UiScale` cleanly controls graphic elements, including Runeshape recipe row item icons, propagating rune icons, socket sizes, and world marker chip padding/icons.
  - `TextScale` independently scales typography, ImGui font scale, and label sizes without distorting icon layouts.
  - Added localization keys (`settings.ui_size`) across supported locales.
- **Runeshape Window Auto-Hide**:
  - Gated the Runeshape recipe calculation and window display to show exclusively when active Runeshape monoliths are present in the current area (`activeMonoliths.Count > 0`).
  - Automatically hides the window completely when not in an area containing Runeshape monoliths or when all monoliths are completed.
- Compatibility: Plugin update (NinjaPricer).
- Validation: Built `TEHhub.sln` in Release configuration with 0 warnings and 0 errors; generated release zip package via `Create-Release.ps1`.

## 1.8.43 — 2026-09-15 [Plugin Update — NinjaPricer]

- **Fix Runeshape Monolith Completion Check via StateMachine**:
  - Replaced the flawed `MinimapIcon` check (which erroneously marked all monoliths as completed upon detonating) with precise `StateMachine` `activated` state checking.
  - Monoliths are considered completed (`IsCompleted = true`) only when `activated == 7` (reward clicked and collected) or `activated == 8` (monolith was bypassed/missed during detonation chain and expired).
  - Monoliths remain active while recipes are unselected (`activated == 1`), selected (`activated == 2`), or detonated awaiting loot pickup (`activated == 6`).
- Compatibility: Plugin update (NinjaPricer).
- Validation: Built `TEHhub.sln` in Release configuration with 0 warnings and 0 errors.

## 1.8.42 — 2026-09-15 [Plugin Update — NinjaPricer]

- **Runeshape Weight Prioritization**: Added toggleable setting `RsPrioritizeWeight` (enabled by default) to prioritize combinations with the highest rune weight (`ComboWeight`) over price when determining the best offer and ordering recipes/monoliths.
  - Monolith world markers and window collapsing headers now show the highest weight (+) offer, breaking ties with highest Chaos value.
  - Runeshape window collapsing headers sort monoliths with highest weight offer first.
  - Expanded recipe offer rows order by weight descending first, then price.
  - Added localization strings (`settings.runeshape_prioritize_weight`) in `th-TH.json` and `en-US.json`.
- Compatibility: Plugin update (NinjaPricer).
- Validation: Built `TEHhub.sln` in Release configuration with 0 warnings and 0 errors.

## 1.8.41 — 2026-09-15 [SDK / Core Framework Update]

- **PoE 2 Spirit Pool Support**: Fixed `LifeOffset.Spirit` type at offset `0x380` from `StdVector SpiritPtr` to `VitalStruct Spirit`. Exposed `Spirit` property in `TEHhub.RemoteObjects.Components.Life` with ImGui visualization and per-frame update, allowing plugins and core systems to inspect player Spirit (Total, Reserved, Current, Unreserved).
- **PoE 1 Legacy Leftovers Cleanup**: Audited component offsets for deprecated PoE 1 structures (Hellscape/Crucible mod vectors and dead Vaal soul structures).
- **Chest Component Safety Guard**: Added null pointer validation in `Chest.UpdateData` to prevent null dereference when reading `ChestsDataPtr`.
- Compatibility: SDK / Core Framework update.
- Validation: Built `TEHhub.sln` in Debug and Release configurations with 0 warnings and 0 errors.

## 1.8.40 — 2026-09-15 [Plugin Update — NinjaPricer]

- **Multi-Golden Socket & Propagating Rune Icons**: Added support for monoliths with multiple golden sockets (2+ slots); recipe rows now render the exact rune icons slotted into each golden socket with layered golden glow auras and slot-specific tooltips.
- **Runeshape Window Text Scaling**: Integrated `ImGui.SetWindowFontScale` with `TextScale` setting so the font size slider properly scales all text, headers, sockets, and icons in the Runeshape window.
- **Clean Monolith Badges**: Removed index number text (`#1`, `#2`...) from header color badges to provide clean, compact color indicators matching the world map.
- **Recipe Runes Tooltip**: Added hover tooltip on recipe rows displaying the complete list of rune ingredients needed.
- **Valuable Drop Alerts**: Integrated expensive item drop notification system (volume-scaled WAV sound alert via WinMM, 3-layer vertical ground light beams, off-screen direction indicator chips, and animated floating card banners).
- **Manual League Selector**: Added dropdown combo to select league manually or automatically from poe.ninja.
- **Plugin Localization**: Added full Localization support for NinjaPricer across 11 languages with complete Thai (`th-TH.json`) and English (`en-US.json`).
- **Plugin Resources**: Relocated all icons and recipe data into `Plugins/NinjaPricer/resources/` with local resolution.
- **LootValue Decommissioning**: Removed obsolete `LootValue` plugin.
- Compatibility: Plugin update (NinjaPricer).
- Validation: Solution built in Debug and Release with 0 warnings and 0 errors.

## 1.8.39 — 2026-09-14

- Fixed TargetableOffsets memory layout for PoE 2: corrected IsTargetable from legacy 0x51 to 0x69 (+0x18 offset displacement), IsHighlightable to 0x6A, and IsTargettedByPlayer to 0x6B based on live process memory probing.
- Simplified Targetable component UpdateData to directly read the validated IsTargetable flag, removing obsolete PoE 1 legacy quest/dialogue conditions that previously caused Targetable.IsTargetable to evaluate to false across all entities.
- Compatibility: Core entity component model bugfix for PoE 2.
- Validation: Verified live memory across 400+ entities in PoE 2 process (PID 30932), confirming active chests/monoliths evaluate to IsTargetable = true while opened chests, dead monsters, and background controllers evaluate to false. Compiled Release with 0 errors and 0 warnings.

## 1.8.38 — 2026-09-14

- Added State (offset 0x010) and IsHide property to MinimapIcon entity component and MinimapIconOffsets: enables plugins to detect hidden/completed/dismissed minimap icon states directly from core memory.
- Enabled per-frame refresh for MinimapIcon to track dynamic State changes in real time while preserving once-only cached decoding of the dat-row icon name.
- Updated NinjaRuneshapeHelper and Runeshape completion detection to leverage MinimapIcon.IsHide alongside Targetable.IsTargetable.
- Compatibility: Core entity component model enhancement.
- Validation: Debug and Release builds of TEHhub, TEHhub.Offsets, TEHhub.Launcher, and NinjaPricer succeeded with 0 warnings and 0 errors.

## 1.8.37 — 2026-09-14

- Implemented dynamic 321-recipe container discovery with persistent index caching for RuneshapeCombinationsPanel in ImportantUiElements: fast-paths against cached index (default 40), performs O(1) direct subpath [3, 2, 1, 0] check for 321 children, and scans GameUi children to auto-recover when game patches shift indices.
- Raised RuneshapeCombinationsUi MaxChildren to 512 and MaxRows to 400 to support inspecting all 321 recipes.
- Compatibility: Core UI resolver improvement.
- Validation: Debug and Release builds succeeded with 0 warnings and 0 errors.

## 1.8.36 — 2026-09-14

- Updated RuneshapeCombinationsPanel child path in ImportantUiElements from index 39 to index 40, matching the updated PoE 2 GameUi vector layout ([40][3][2][1][0]).
- Compatibility: Core UI resolver fix.
- Validation: Debug and Release builds succeeded with 0 warnings and 0 errors.

## 1.8.35 — 2026-09-14

- Replaced hardcoded "Scan Sleeping Entities for 'Abyss'" with general "Scan Sleeping Entities" button in AreaInstance debug UI: scans all sleeping entities without filtering to Abyss, populating the full sleeping entity inventory for live filtering by Id, Path, and Rarity.
- Optimized entity path filtering in EntitiesWidget to use ordinal case-insensitive matching without per-entity string allocations.
- Added 1,000-element viewport rendering cap for massive entity lists in EntitiesWidget to maintain frame performance.
- Compatibility: Core debug UI improvement.
- Validation: Debug and Release builds succeeded with 0 warnings and 0 errors.

## 1.8.34 — 2026-09-14

- Fixed Offset Helper UI root truncation: updated VerifyProbe and FindOffsetRecoveries to use MaxUiRoots (16) when evaluating UiElementBaseOffset, allowing RuneshapeCombinationsPanel and subsequent UI panels to be verified and displayed under probe roots.
- Added CleanUpData reset for RuneshapeCombinationsPanel.Address to ensure proper lifecycle cleanup.
- Compatibility: Core diagnostics and UI element management improvement.
- Validation: Debug and Release builds succeeded with 0 warnings and 0 errors.

## 1.8.33 — 2026-09-14

- Fixed Runeshape panel static-chain validation: its UI parent is an intermediate container, so the resolver now validates a non-zero live parent instead of requiring the GameUi manager address.
- Compatibility: Core UI resolver fix.
- Validation: Debug and Release core builds plus Debug and Release launcher builds succeeded with 0 warnings and 0 errors.

## 1.8.32 — 2026-09-14

- Replaced the generic Runeshape panel lookup with its verified static child-vector chain: GameUi children +0x10, index 39, then Self and Parent validation before publishing the root.
- Compatibility: Core UI resolver fix.
- Validation: Debug and Release core builds plus Debug and Release launcher builds succeeded with 0 warnings and 0 errors; installed Debug v1.8.32 started and Local Diagnostics API reported the expected build.

## 1.8.31 — 2026-09-14

- Raised Offset Helper's UI-root verification cap from 6 to 12 independently of entity root limits, allowing RuneshapeCombinationsPanel to appear in UiElementBaseOffset verification.
- Compatibility: Debug diagnostic coverage improvement.
- Validation: Debug and Release core builds plus Debug and Release launcher builds succeeded with 0 warnings and 0 errors.

## 1.8.30 — 2026-09-14

- Registered RuneshapeCombinationsPanel with Offset Helper's UI root inventory and panel-verification latch, so it appears in the same diagnostic UI list as LargeMap, MiniMap, and Gemcutting panels.
- Compatibility: Core diagnostic/UI API addition.
- Validation: Debug and Release core builds plus Debug and Release launcher builds succeeded with 0 warnings and 0 errors.

## 1.8.29 — 2026-09-14

- Added ImportantUiElements.RuneshapeCombinationsPanel as a first-class core UI root. It resolves the observed GameUi child 39 and appears in Offset Helper static-address verification alongside map and panel roots.
- Compatibility: Core UI API addition; consumers can use RuneshapeCombinationsUi to enumerate its recipe rows and rune slots.
- Validation: Debug and Release core builds plus Debug and Release launcher builds succeeded with 0 warnings and 0 errors.

## 1.8.28 — 2026-09-14

- Added shared RuneshapeCombinationsUi and its UI layout offset definition. The wrapper resolves a visible recipe panel through live child relationships and exposes recipe rows and square rune-slot controls without relying on a session-specific UI path.
- Compatibility: Core UI API addition; consumers must validate the resolved panel before using its slots. No existing plugin behavior changes.
- Validation: Debug and Release core builds plus Debug and Release launcher builds succeeded with 0 warnings and 0 errors.

## 1.8.27 — 2026-09-14

- Added bounded Debug-only detection of six-socket record vectors across verified Rune Station roots, reporting stride and capped payload for native layout identification.
- Compatibility: Diagnostic-only; public Release builds compile it out.
- Validation: Debug and Release core builds plus Debug and Release launcher builds succeeded with 0 warnings and 0 errors; live capture pending deployment.

## 1.8.26 — 2026-09-14

- Added a bounded Debug-only search across verified Rune Station, listener, and paired-controller roots for vector layouts containing exactly the live socket count of valid Rune catalog indexes.
- Compatibility: Diagnostic-only; public Release builds compile it out.
- Validation: Debug and Release core builds plus Debug and Release launcher builds succeeded with 0 warnings and 0 errors; live Debug API capture pending deployment.
## 1.8.25 — 2026-09-14

- Added a bounded Debug-only scan for compact non-zero values within the verified Rune catalog range in Rune Station and listener-sidecar structures, to test index-based rather than pointer-based rune storage.
- Compatibility: Diagnostic-only; public Release builds compile it out.
- Validation: Debug and Release core builds plus Debug and Release launcher builds succeeded with 0 warnings and 0 errors; live Debug API capture pending deployment.
## 1.8.24 — 2026-09-14

- Disabled execution of the experimental local reverse Rune-pointer scan after its first live invocation stalled the Debug diagnostic request. The scanner remains isolated from runtime planning pending a worker-based design.
- Compatibility: Diagnostic-only safety fix; public Release builds remain unaffected.
- Validation: Debug and Release core builds plus Debug and Release launcher builds succeeded with 0 warnings and 0 errors; live probe recovery test pending deployment.
## 1.8.23 — 2026-09-14

- Added a bounded Debug-only reverse-pointer scan around the paired RuneEncounterController. It searches committed readable local heap chunks for exact references to verified Rune DAT rows, exposing otherwise disconnected runtime holders without a process-wide scan.
- Compatibility: Diagnostic-only; public Release builds compile this interop and scan out.
- Validation: Debug and Release core builds plus Debug and Release launcher builds succeeded with 0 warnings and 0 errors; live Debug API capture pending deployment.
## 1.8.22 — 2026-09-14

- Extended the Debug Rune Station probe with capped raw previews and nested vector-header inspection for up to eight verified controller-inventory records, so their native container layout can be identified before further traversal.
- Compatibility: Diagnostic-only; public Release builds compile it out.
- Validation: Debug and Release core builds plus Debug and Release launcher builds succeeded with 0 warnings and 0 errors; live Debug API capture pending deployment.
## 1.8.21 — 2026-09-14

- Extended the Debug Rune Station probe to follow at most 24 unique records from verified RuneEncounterController.Inventories vectors by one bounded pointer hop, reporting only exact references into the verified Rune DAT table.
- Compatibility: Diagnostic-only; public Release builds compile it out.
- Validation: Debug and Release core builds plus Debug and Release launcher builds succeeded with 0 warnings and 0 errors; live Debug API capture pending deployment.
## 1.8.20 — 2026-09-14

- Extended the Debug-only Inventories capture to classify tiny pointer-aligned vectors as possible inventory item records and report only entries that resolve through the normal Entity reader.
- Compatibility: Exploratory diagnostics only; the planner does not consume these values and Release compiles them out.
- Validation: Debug and Release core builds plus Debug and Release launcher builds succeeded with 0 warnings and 0 errors; live Debug API capture pending deployment.
## 1.8.19 — 2026-09-14

- Extended the Debug Rune Station probe with a bounded inspection of the paired RuneEncounterController.Inventories component, reporting only plausible vector headers and capped raw payloads.
- Compatibility: Diagnostic-only; public Release builds compile this inspection out.
- Validation: Debug and Release core builds plus Debug and Release launcher builds succeeded with 0 warnings and 0 errors; live Debug API capture pending deployment.
## 1.8.18 — 2026-09-14

- Fixed the Debug Rune Station controller scan so its bounded one-hop pointer budget applies independently to each controller component root.
- Compatibility: Diagnostic-only; public Release builds compile it out.
- Validation: Debug and Release core builds plus Debug and Release launcher builds succeeded with 0 warnings and 0 errors.

## 1.8.17 — 2026-09-14

- Extended the Debug Rune Station probe to locate the paired `RuneEncounterController` at the same grid position and include its live component roots in the bounded Rune DAT reference scan.
- Compatibility: Diagnostic-only; public Release builds continue to compile it out.
- Validation: Debug and Release core builds plus Debug and Release launcher builds succeeded with 0 warnings and 0 errors.
## 1.8.16 — 2026-09-14

- Extended the Debug Rune Station probe to include non-station listener sidecars as bounded Rune DAT scan roots. This allows the diagnostic to inspect runtime controller structures that are not valid game Entities.
- Compatibility: Diagnostic-only; public Release builds continue to compile it out.
- Validation: Debug and Release core builds plus Debug and Release launcher builds succeeded with 0 warnings and 0 errors.
## 1.8.15 — 2026-09-14

- Added `POST /api/diagnostics/rune-station-probe?entityId={id}` and `GET /api/diagnostics/rune-station-probe` to the Debug loopback API.
- The endpoint queues a bounded Rune Station diagnostic on the render thread for one awake entity, returning its path and full diagnostic JSON without requiring an inspector screenshot. It accepts no game input and retains one pending request at a time.
- Compatibility: Endpoint, capture queue, and JSON schema are compiled out of public Release builds.
- Validation: `dotnet build TEHhub/TEHhub.csproj -c Debug --no-restore` succeeded with 0 warnings and 0 errors.
## 1.8.14 — 2026-09-14

- Extended the Debug Rune Station listener map to resolve non-station listener owners through the normal Entity reader and report their validated entity ID, metadata path, and component names.
- Compatibility: Diagnostic-only; public Release builds continue to compile it out.
- Validation: `dotnet build TEHhub/TEHhub.csproj -c Debug --no-restore` succeeded with 0 warnings and 0 errors.
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
