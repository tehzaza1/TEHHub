# Expedition Planner — implementation handoff

## Objective

Create a standalone `ExpeditionPlanner` plugin for PoE 2. It must not add code, settings, or rendering to `Radar`.

The first deliverable is a read-only visual aid: show Expedition explosives, connector poles, and fuse links that the game has already placed. The later planner recommends a route; it never clicks, places, or detonates for the player.

## Final target

The completed plugin is a dependable manual decision aid for both small and large Expeditions:

1. Before detonation, it identifies every placed explosive, fuse chain, connector, reward marker, and active remnant visible to the game.
2. It draws the explosion network clearly enough for the player to understand which rewards and remnants each chain can reach.
3. When PoE2 placement limits and blast radius have been verified, it evaluates the available choices and highlights the best manual placement route for the user's configured priorities.
4. It explains why a route is preferred and marks missing or uncertain evidence instead of inventing an answer.
5. It remains stable across area changes, supports any valid number of explosives/chains, and adds negligible work outside an active Expedition.

The end product does **not** control the game. The player retains every placement and detonation action. A recommendation is hidden whenever the necessary PoE2 evidence cannot be read or validated.

## Grand Expedition strategy model

The route scorer must be **stateful**. A Grand Expedition route is not just a collection of independent target values:

- The golden slot's rune proliferates to every subsequent remnant in the chain.
- Other runes apply only to their current remnant.
- Duplicate runes do not stack, so taking the same rune again has no incremental value.
- The value of a rune depends on where it is taken: an early proliferating rune has more future value than the same rune at the final remnant.

Therefore, evaluate a candidate route in order. Its state contains the selected/proliferated rune set, current chain position, consumed explosives, affected rewards, and any dangerous modifiers. The transition score for a target is its direct reward plus the projected value it adds to later reachable remnants; a duplicate contributes zero unless the game evidence proves an additional effect.

Use the following **editable default priority seeds** from the 0.5 Grand Expedition guide, not as immutable game truth:

| Initial priority | Rune |
| --- | --- |
| Highest | Opulent (golden) |
| Strong purple | Power, Death, Bond, Oath |
| Good purple | Time, Rebirth |
| Low individual value | Other purple / blue runes |

Keep those values in the external per-user settings catalog, include the source/patch label, and allow an update to change or remove every default. Combat danger is a separate input: the source describes loot priorities but does not provide a reliable build-danger catalog.

Large encounters must be treated as routine: the guide documents a 22-explosive run. The search may be bounded for performance, but must report when it uses a heuristic rather than silently truncating an optimal search.

## Confirmed live evidence

Captured in a live Expedition encounter on 2026-09-13 using Debug TEHhub 1.6.6:

| Meaning | Exact entity path | Model / icon evidence |
| --- | --- | --- |
| Start control | `Metadata/MiscellaneousObjects/Expedition/ExpeditionDetonator` | `plunger.ao`, `ExpeditionDetonator` |
| Placed main explosive | `Metadata/MiscellaneousObjects/Expedition/ExpeditionExplosive` | `explosive.ao`, `ExpeditionDynamite` |
| Connector / branch point | `Metadata/MiscellaneousObjects/Expedition/ExpeditionConnectorPole` | `explosivesmall.ao`, `ExpeditionRegularMonster` |
| Fuse segment | `Metadata/MiscellaneousObjects/Expedition/ExpeditionExplosiveFuse` | `wire.ao`, `Beam`, `LimitedLifespan` |
| Reward / encounter marker | `Metadata/MiscellaneousObjects/Expedition/ExpeditionMarker` | chest, elite, and monster marker models |
| Active remnant | `Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter` | `Expedition2RemnantActive` |

The placed-explosive capture added one `ExpeditionExplosive`, two `ExpeditionConnectorPole`, and three `ExpeditionExplosiveFuse` entities. All exposed reliable grid/world positions. This confirms the visual phase can use normal entity components and does not require an unknown UI offset.

PoE1's old `ExpeditionIcons` planner used a dedicated `ExpeditionDetonatorElement.Info` wrapper for placed count/positions and raw pathfinding/map-stat data. TEHhub does not yet have compatible PoE2 wrappers for those inputs. Do not copy those offsets or assume PoE1 placement rules.

## Plugin boundary and storage

- Source directory: `Plugins/ExpeditionPlanner/`
- Assembly/project name: `ExpeditionPlanner`
- Configuration: use `PCore<TSettings>.PluginConfigPath("settings.json")`. It resolves to `TEHhub/configs/plugins/ExpeditionPlanner/settings.json` at runtime.
- Build output: use the ordinary plugin copy target to `TEHhub/bin/<configuration>/net10.0-windows/win-x64/Plugins/ExpeditionPlanner/`.
- Plugin loader discovers a DLL in its own directory; no core registry or Radar change is needed.
- Keep all mutable data in the configuration directory. Do not write beside the deployed DLL.

## Phase 1 — confirmed-object overlay

Implement this first.

1. Add the independent project, `ExpeditionPlannerCore : PCore<ExpeditionPlannerSettings>`, and its settings class.
2. In `DrawUI`, inspect only awake entities with the exact paths above. Use existing `Render` positions, `Animated.ModelPath`, and `MinimapIcon` components. Never read arbitrary bytes or unverified offsets.
3. Render three distinct, configurable visuals:
   - explosive: high-contrast circle/label;
   - connector pole: smaller marker;
   - fuse: line or endpoint marker when the two endpoints can be determined confidently.
4. Add a compact diagnostic row: `detonators`, `explosives`, `poles`, `fuses`, `markers`, `remnants`, plus the current area hash.
5. Reset all transient state on area hash/address change and when disabled. Do not retain entities across areas.
6. Put a strict cap on per-frame work. Use reusable lists/dictionaries and update an entity snapshot at a modest interval (for example 100–200 ms); rendering reads the snapshot only.

Acceptance: after placing one bomb and returning to normal mode, the plugin reports at least `explosives=1`, identifies its grid position, and does not require Radar. It must not draw entities after leaving the area.

## Phase 2 — connection graph and large encounters

Large Expeditions must be dynamic; never hard-code bomb, fuse, pole, or marker counts.

1. Build a graph from the live entities per encounter/area.
2. Initially draw only verified relationships. A fuse's own position alone does not prove both endpoints, so use a conservative distance/collinearity rule and label uncertain links as uncertain rather than presenting them as facts.
3. Group disconnected chains separately. The display should make separate detonation branches obvious.
4. Test with one small and one large Expedition. Capture data both before and after a placement, then preserve only sanitized diagnostics required to reproduce grouping failures.

Acceptance: all placed explosives and all detected fuse entities appear in the UI for both encounter sizes; no count limit or fixed map layout exists in code.

## Phase 3 — determine placement constraints

Do not implement advice until each input is evidenced in PoE2.

1. Use the Debug-only `POST /api/diagnostics/expedition-ui-probe` before placement and after exactly one placement to locate a stable UI panel and any count/placement state. The existing broad capture is bounded but child paths may shift, so compare structure and values across multiple captures instead of accepting one path.
2. Add a typed UI wrapper only after a stable, repeatable field is confirmed across encounters.
3. Investigate PoE2 equivalents of allowed tiles, placement range, and blast radius. Prefer documented/high-level game structures already exposed by TEHhub. If raw pathfinding or map-stat offsets are needed, add them only through the offset verification/recovery pipeline.
4. Record confidence for each input. Missing or contradictory evidence must disable the corresponding recommendation.

Acceptance: the plugin can state where its bomb count, legal placement zone, and blast radius come from, with a reproducible live capture for each.

## Phase 4 — recommendation only

### Decision model

The planner chooses the manual placement route with the highest **net score**, not simply the route that reaches the most icons.

For every possible blast/chain, calculate:

```
net score = reward value + desired-remnant value - dangerous-remnant penalty - uncertainty penalty
```

- **Reward value:** each confirmed marker class has a configurable base weight. Valuable reward types can receive a high positive weight; ordinary monster markers can receive a low weight.
- **Desired remnant value:** beneficial rune/remnant effects receive positive weights selected by the user.
- **Dangerous remnant penalty:** build-breaking effects receive large negative weights. Examples are immunity to the player's main damage type, immunity to a required ailment, extreme speed/damage modifiers, or any user-marked forbidden rune. A route whose total score is positive but touches a forbidden remnant is displayed as unsafe, not as the default recommendation.
- **Uncertainty penalty:** if the plugin cannot decode a remnant effect, prove a fuse endpoint, or verify a placement constraint, it reduces confidence and avoids presenting that route as certain.

Start with three editable profiles:

| Profile | Behaviour |
| --- | --- |
| Safe | Rejects forbidden/dangerous remnants; prefers reliable rewards. |
| Balanced | Takes moderate risk only when the reward increase exceeds the configured penalty. |
| Greedy | Maximizes reward score but still labels dangerous effects clearly. |

The settings UI must let the user set individual reward weights, beneficial rune weights, dangerous rune penalties, and a hard **never take** toggle. Store them in `configs/plugins/ExpeditionPlanner/settings.json`.

### Delivery steps

1. Build a classified target list from verified marker models/icons and remnant data. Unknown types remain explicitly `Unknown`; never assign them a guessed value.
2. Add a remnant/rune classifier only after the actual PoE2 mod text or stable identifiers are captured. The first evidence capture showed empty `ObjectMagicProperties.ModNames` on active `Expedition2Encounter` entities, so the implementation must first investigate the UI/other confirmed component that exposes the displayed rune effect.
3. Enumerate legal candidate placements and resulting blast chains only after range, radius, and walkable-tile inputs have been verified in Phase 3.
4. Score each candidate with the model above, select the best safe option, and retain the next two alternatives for comparison.
5. For Grand Expeditions, score the ordered route with the proliferated-rune state. Prefer an early high-value proliferating rune when its projected downstream gain exceeds an immediately larger isolated reward.
6. Display the recommended point/route, affected rewards/remnants, total score, danger warnings, selected rune set, and the reason it won. Let the player choose and click manually.
7. Explain uncertainty in the overlay when constraints or link endpoints cannot be verified.
8. Keep all interaction manual. There must be no simulated input, click, placement, or detonation path.

Acceptance: recommendations disappear when their source evidence is stale, the area changes, or a required constraint cannot be verified.

## Definition of done

The project is complete when all of the following are true:

- A standalone ExpeditionPlanner DLL loads with its own centralized settings and no Radar dependency.
- It has been live-tested in at least one small and one large Expedition.
- The overlay shows the complete confirmed network before detonation and does not leave stale drawings after an area change.
- Placement recommendations are based on verified PoE2 count, range, blast-radius, and target data, with an explanation and confidence state.
- The score explains reward gains, every affected beneficial/dangerous rune, the selected profile, and why other reachable routes lost.
- Any `never take` rune makes its route unsafe; missing rune data prevents a confident recommendation rather than silently treating the rune as safe.
- Grand Expedition recommendations account for rune order, propagation, duplicates, and the available explosive budget; they disclose when a bounded heuristic was used.
- The Release package remains lean and excludes the Debug evidence endpoints/tools.
- Debug and Release builds, packaging checks, and the live validation results are recorded in `CHANGELOG.md`.

## Required validation and release rules

1. Build the new plugin in Debug and Release, then build the core in Debug and Release.
2. Verify the Release package contains the plugin DLL and required assets, but no Debug diagnostic API/tool source.
3. Confirm settings save beneath `configs/plugins/ExpeditionPlanner`.
4. Test one small and one large live Expedition only when available. Do not claim live testing without it.
5. For every delivered group of changes, bump synchronized core/launcher semantic version and add a dated `CHANGELOG.md` entry with actual validation.

## Current research tools

Debug-only, loopback-only endpoints already available:

- `POST /api/diagnostics/expedition-probe` captures bounded Expedition entity evidence on the render thread.
- `POST /api/diagnostics/expedition-ui-probe` captures a bounded visible Game UI tree for placement-screen comparison.

Both are compiled out of Release. Local capture files from the session, if still present, are under `artifacts/expedition-*.json`; treat them as temporary research data, not release content.

### Strategy reference

- [PoE 2 0.5 Grand Expedition Guide: Best Currency Farm Strategy](https://www.mmoexp.com/News/poe-2-0-5-grand-expedition-guide-best-currency-farm-strategy.html), accessed 2026-09-13. Use it only as configurable strategy input; validate live identifiers and mechanics before relying on it at runtime.
