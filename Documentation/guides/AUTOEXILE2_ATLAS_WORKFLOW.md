# AutoExile2 MapFarm: Atlas launch design

## Scope and current evidence

This design covers MapFarm's path from a crafted Waystone in the hideout to entering its map. `AutoExile2` owns the Atlas planner, target catalog, settings, and UI actions. `Atlas2` is a reference implementation for map names, content decoding, biome metadata, and shortest-path routing; AutoExile2 does not require the Atlas2 plugin to be enabled or loaded.

In `bot-actions-20260924-055305-831.jsonl`, all 8/8 Exalted Orb actions were confirmed. The bot then closed Stash, clicked the personal Map Device, observed Atlas, and changed from `FindEligibleAtlasNode` to `AccessibleAtlasNodeNotFound` in about 36 ms. That log does not record the Atlas node states, map IDs, or rectangles, so it cannot establish why every candidate was rejected.

`ImportantUiElements.UpdateAtlasMapData` refreshes Atlas topology every 20 frames while refreshing materialized child UI positions every frame. `AtlasInsertPanelOpener` currently proceeds when `AtlasMaps.Count > 0`, then rejects immediately if its first selection pass finds no node that is simultaneously accessible, classified as a known normal map, visible, and geometrically usable. An existing nonempty snapshot does not establish that the node state has settled after the Map Device opens.

## First implementation: two farming modes

- **General maps:** Choose a random currently accessible map that accepts the prepared Waystone. Choose once per run, not on every tick. The player configures which Tablet to apply for the desired content; Tablet availability and insertion need separate verification before Traverse.
- **Deadly Map Boss:** The player selects one or more named targets from a catalog owned by AutoExile2 and adapted from Atlas2's known map data. Locate targets in the observed graph and compute graph distance from each *live* `AccessibleNow` node to the target. Select the currently accessible node with the fewest remaining graph hops, even when the target itself is far off-screen or currently inaccessible. The target may be selected only after it becomes `AccessibleNow`. Replan after every completed map. If several selected targets are reachable, prefer the fewest remaining maps. Persist target identity by internal MapId where available so UI language changes do not invalidate it. When none of the selected targets is known in the loaded Atlas, report that no route is currently known.

Hardcore/Grand Deli preparation and Expedition/Ocean routing are outside this first implementation. Their rules will be designed separately after the two modes above work in the live Atlas.

The Atlas2 map groups and `tokens.json` are the starting references for AutoExile2's catalog. Localized names that refer to the same MapId should appear as one player option. AutoExile2's planner uses `Core`'s `AtlasMaps` snapshot directly and does not call Atlas2 runtime objects or render code.

## Workflow

1. **Atlas readiness.** After Map Device interaction, wait for Atlas visibility and at least one fresh Atlas topology refresh. A core snapshot revision or refresh timestamp should make freshness explicit. Require a second consistent snapshot before selection, within a bounded readiness timeout. Keep the game input idle while waiting.
2. **Candidate diagnosis.** Count map nodes by state, known/unknown map ID, normal/unique classification, visible UI, valid rectangle, and inside/outside the Atlas viewport. On timeout, log these counts and a bounded sample of node index, map ID, raw status, state, UI visibility, and rectangle. Emit this once per Atlas attempt, not every frame. Use a distinct decision for no accessible node, unknown classification, off-screen candidates, and unreadable UI.
3. **Node choice.** Ask AutoExile2's Atlas planner for the next node under the selected farming mode. The planner returns target identity, route, next accessible node, and a diagnostic when no route is known. The clicker verifies that this node's current UI element matches the planned address and that its rectangle is inside the visible Atlas viewport. Re-read state, ID, address, visibility, and rectangle immediately before input. A changed candidate returns to planning without clicking. An off-screen planned node needs a bounded navigation step or an explicit stop reason; screen-center distance must never override the selected farming goal.
4. **Node click and panel confirmation.** Issue at most one click for the selected node. Retry only when the input system proves no click was sent. Confirm the unique Waystone/Tablet insertion panel through its live UI structure. A sent click without confirmation ends with a diagnostic; it must never trigger a blind second click.
5. **Waystone insertion.** Take exactly one of the already crafted Waystones from inventory. Verify the insertion slot is empty, the item is still in the expected inventory slot, and the UI is clear of a held currency. Move it once, then verify that the panel contains a matching Waystone and that the inventory slot changed. Treat an uncertain transfer as a stop condition. Keep the remaining crafted Waystones in inventory for following maps.
6. **Traverse and portal.** Capture portal entity IDs through `PortalEntryHandler.CapturePortalSnapshot` before pressing Traverse. Verify the panel and Waystone again, issue Traverse once, and latch that input. Afterward pass the snapshot to `BeginAfterTraverse`; let `PortalEntryHandler` wait for a new targetable portal and verify the area transition. Never repeat Traverse just because a portal is late.
7. **Next run.** When the map ends and the character returns to the hideout, use the next prepared Waystone. Refill/craft only when the prepared inventory batch is exhausted or invalidated.

## State and ownership

`WaveFarmMode` owns the sequence and area transitions. An AutoExile2 Atlas planner owns target selection and graph routing, using the shared `Core.GameUi.AtlasMaps` data. AutoExile2 settings own the farming profile, target names, and Tablet policy. `AtlasInsertPanelOpener` owns Atlas readiness, live click verification, and panel confirmation. A separate insertion/Traverse component should own the selected Waystone and one-shot Traverse latch. `PortalEntryHandler` retains sole ownership of portal discovery and entry. Each component exposes a terminal result with a reason; `WaveFarmMode` advances only after a verified success.

Reset on area change, bot stop, or user pause must release movement/input and clear the active interaction. A panel disappearing, foreground loss, chat opening, stale UI address, or ambiguous panel halts the current action safely. All waits need explicit deadlines; none should cause the hideout flow to fetch another Waystone while a prepared batch still exists.

## Atlas observation cache and external scan

AutoExile2 should retain Atlas nodes observed during the current TEH/game process session, including map ID, grid position, connections, content, and the latest known status. Key a node by its Atlas grid position and verify its MapId when merging observations. Retain first/last observation times and the observed UI addresses so a live scan can tell whether those addresses stay stable. Treat UI addresses and child indices as temporary: every action must locate the current node again and recheck its state and visible rectangle. The cache may contain nodes that are no longer loaded in the UI, so cached status alone cannot authorize a click or prove current accessibility. Start a fresh cache when the attached game process changes; no disk persistence is required for the first version.

For live diagnostics, provide a read-only scanner/API as a separate process outside TEH. It may reuse TEH DLLs or offset definitions but must attach to the game independently. A triggered scan should report the process/build identity, Atlas visibility/readiness, scan time, node identity and topology, status, content, and observed UI address. Repeated scans while the player closes/reopens or moves the Atlas panel will establish whether UI addresses are stable; until then, they remain diagnostic evidence only. This external tool is separate from AutoExile2's runtime cache and must not be a dependency for farming.

Live captures on 2026-09-24 before and after panning the Atlas found 550 and 654 Atlas node UI elements, respectively. The first capture's 512-node safety cap limited the comparison to 512 nodes; all 512 matched by grid position and MapId, with unchanged UI address, child index, and raw status. Their rectangles all moved, confirming the panel had moved between captures. The second capture recorded all 654 nodes. This supports merging observations by grid and MapId across pans in this game session. It does not establish UI address stability after reopening the Atlas, changing area, restarting the game, or updating PoE2; actions still re-resolve live UI.

A farther pan exposed 2,158 nodes. The complete capture matched all 654 nodes from the second capture by grid and MapId, with no collisions at the same grid position. Their UI addresses and raw statuses stayed the same, but **all 654 child indices changed** and all rectangles moved. Thus child index is unsuitable as a persisted identity even when the UI address happens to remain stable. The complete scan added 1,504 nodes to the observed set.

The first external capture of these nodes showed no connections because the standalone scanner rejected the real 3,468-edge vector above its old 2,048-edge cap. A corrected capture decoded all 3,468 undirected edges: 2,154 nodes have adjacency, four are isolated, and every edge endpoint matches a captured grid position. This validates the graph data for route planning in the observed region. The standalone scanner now has a 100,000 node/edge ceiling with bounded byte reads and explicit partial-capture diagnostics.

After this live comparison, the Atlas map catalog should be a versioned JSON file read on plugin startup and merged with new observations without deleting old nodes. It retains topology and last-observed content/status; an old status is historical data, not a current click authorization. Writes should be atomic and deferred from the render callback. Keep separate observation times so older nodes can be recognized as stale.

The runtime Atlas graph is created from live observations at `Plugins/AutoExile2/Data/atlas-observations.json` beside the deployed plugin. `configs/plugins/AutoExile2` is reserved for settings.

The durable graph assigns one stable file-local numeric ID per observed node, keyed internally by grid position plus MapId. Each entry includes its in-game display name, map type (including unique maps), biome, validated neighbor IDs, and compact target metadata where available. Repeated map names at different grid positions have separate IDs. New nodes receive new IDs; existing IDs are never renumbered or removed on a later partial scan. UI address, child index, and rectangle stay out of this file. Completion is a timestamped last observation and must be refreshed from the live Atlas before acting on a route node.

Timestamped `lastObservedState: accessibleNow` may guide which distant frontier to pan toward when its node is outside the current UI snapshot. It remains a hint: after the node becomes observable, verify its current state is `AccessibleNow` before any click. A changed state invalidates the candidate and triggers replanning. The same live verification applies to a target boss node.

The route start comes from the live Atlas current-location marker, not the fixed center of the Atlas viewport. Core identifies a visible marker by fingerprint `0x502EF3` and exposes its resolved `MapNodeIndex` through `AtlasMarkers`. Resolve that index against the current `AtlasMaps` list to get the start node's grid position and MapId, then map it to the durable node ID. The marker/index is never persisted because UI child indices changed across the far-pan capture. If no marker resolves, report that the route start is unknown and use only a separately verified accessible frontier.

For a route node outside the viewport, calibrate the current grid-to-screen direction from visible nodes, then drag the Atlas with the left mouse button in the opposite direction to bring that node toward the clickable area. Use bounded drags, release input, refresh live node positions, and recalculate after each step. Do not use saved rectangles, a fixed grid-axis sign, or a long blind drag; Atlas zoom and UI transforms can alter the screen-space direction and distance.

## Implementation order

1. Add Atlas snapshot freshness and one-shot candidate diagnostics. This reveals the cause of the current `AccessibleAtlasNodeNotFound` before changing selection behavior.
2. Adapt Atlas2's graph routing and relevant target catalog into AutoExile2's own planner. Add General and player-selected Deadly Map Boss policies. Keep Atlas2 as a code/data reference, without a plugin-to-plugin runtime dependency.
3. Add bounded readiness and live verification of the planned node; retain the single-click rule.
4. Add configurable, verified Tablet and Waystone insertion and one-shot Traverse, then connect the existing portal handler.
5. Connect successful map return to the remaining prepared Waystones.

The current log proves the failure point, but a live Atlas snapshot is needed to decide whether the immediate rejection comes from delayed status data, map classification, off-screen UI, or rectangle validation.
