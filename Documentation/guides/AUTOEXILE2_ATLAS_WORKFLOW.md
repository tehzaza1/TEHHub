# AutoExile2 MapFarm: Atlas launch design

## Scope and current evidence

This design covers MapFarm's path from a crafted Waystone in the hideout to entering its map. It concerns `AutoExile2`, not the separate `Atlas2` overlay plugin.

In `bot-actions-20260924-055305-831.jsonl`, all 8/8 Exalted Orb actions were confirmed. The bot then closed Stash, clicked the personal Map Device, observed Atlas, and changed from `FindEligibleAtlasNode` to `AccessibleAtlasNodeNotFound` in about 36 ms. That log does not record the Atlas node states, map IDs, or rectangles, so it cannot establish why every candidate was rejected.

`ImportantUiElements.UpdateAtlasMapData` refreshes Atlas topology every 20 frames while refreshing materialized child UI positions every frame. `AtlasInsertPanelOpener` currently proceeds when `AtlasMaps.Count > 0`, then rejects immediately if its first selection pass finds no node that is simultaneously accessible, classified as a known normal map, visible, and geometrically usable. An existing nonempty snapshot does not establish that the node state has settled after the Map Device opens.

## Workflow

1. **Atlas readiness.** After Map Device interaction, wait for Atlas visibility and at least one fresh Atlas topology refresh. A core snapshot revision or refresh timestamp should make freshness explicit. Require a second consistent snapshot before selection, within a bounded readiness timeout. Keep the game input idle while waiting.
2. **Candidate diagnosis.** Count map nodes by state, known/unknown map ID, normal/unique classification, visible UI, valid rectangle, and inside/outside the Atlas viewport. On timeout, log these counts and a bounded sample of node index, map ID, raw status, state, UI visibility, and rectangle. Emit this once per Atlas attempt, not every frame. Use a distinct decision for no accessible node, unknown classification, off-screen candidates, and unreadable UI.
3. **Node choice.** Select from accessible, known normal nodes whose current UI element still matches the snapshot address and whose click rectangle is within the visible Atlas viewport. Prefer the candidate nearest the viewport center; break ties by stable index. Re-read its state, ID, address, visibility, and rectangle immediately before input. A changed candidate returns to selection without clicking. Do not guess that an unknown map ID is normal.
4. **Node click and panel confirmation.** Issue at most one click for the selected node. Retry only when the input system proves no click was sent. Confirm the unique Waystone/Tablet insertion panel through its live UI structure. A sent click without confirmation ends with a diagnostic; it must never trigger a blind second click.
5. **Waystone insertion.** Take exactly one of the already crafted Waystones from inventory. Verify the insertion slot is empty, the item is still in the expected inventory slot, and the UI is clear of a held currency. Move it once, then verify that the panel contains a matching Waystone and that the inventory slot changed. Treat an uncertain transfer as a stop condition. Keep the remaining crafted Waystones in inventory for following maps.
6. **Traverse and portal.** Capture portal entity IDs through `PortalEntryHandler.CapturePortalSnapshot` before pressing Traverse. Verify the panel and Waystone again, issue Traverse once, and latch that input. Afterward pass the snapshot to `BeginAfterTraverse`; let `PortalEntryHandler` wait for a new targetable portal and verify the area transition. Never repeat Traverse just because a portal is late.
7. **Next run.** When the map ends and the character returns to the hideout, use the next prepared Waystone. Refill/craft only when the prepared inventory batch is exhausted or invalidated.

## State and ownership

`WaveFarmMode` owns the sequence and area transitions. `AtlasInsertPanelOpener` owns Atlas readiness, node selection, and panel confirmation. A separate insertion/Traverse component should own the selected Waystone and one-shot Traverse latch. `PortalEntryHandler` retains sole ownership of portal discovery and entry. Each component exposes a terminal result with a reason; `WaveFarmMode` advances only after a verified success.

Reset on area change, bot stop, or user pause must release movement/input and clear the active interaction. A panel disappearing, foreground loss, chat opening, stale UI address, or ambiguous panel halts the current action safely. All waits need explicit deadlines; none should cause the hideout flow to fetch another Waystone while a prepared batch still exists.

## Implementation order

1. Add Atlas snapshot freshness and one-shot candidate diagnostics. This reveals the cause of the current `AccessibleAtlasNodeNotFound` before changing selection behavior.
2. Add bounded readiness and deterministic live candidate selection; retain the single-click rule.
3. Add verified Waystone insertion and one-shot Traverse, then connect the existing portal handler.
4. Connect successful map return to the remaining prepared Waystones.

The current log proves the failure point, but a live Atlas snapshot is needed to decide whether the immediate rejection comes from delayed status data, map classification, off-screen UI, or rectangle validation.
