# UiDump

An on-demand, read-only PoE2 UI research plugin. It follows the attached game's
`GameUi` children tree. It does not click, move the cursor, send keys, transfer
items, or alter game memory. No AutoExile2 or OH2 dependency.

## Use

1. Build `TEHhub.sln` in Release. UiDump is deployed with the other plugins.
2. Enable **UiDump** in TEHhub plugin management and open its settings.
3. Set a label such as `stash-closed`, `stash-tab-maps` or `vendor`.
4. Open the desired in-game panel, then press **F8** once. Alternatively click
   **Capture current game UI**; the default 3-second delay lets you return to the game.
5. Keep the panel/tab stable while capturing. Check the result in plugin settings.
6. Use **Open dump folder** or **Copy ZIP path**. Captures are under
   `configs/plugins/UiDump/dumps/` beneath the TEHhub application directory.
7. Send the ZIP plus a matching screenshot and your PoE2 game version.

Each ZIP contains `ui.json`, `ui-tree.txt` and a format `README.txt`. Nodes carry
root-relative index paths, parent links, addresses, visibility, geometry, bounded
text candidates, validated item references where available, and optional raw bytes.

Default scope is the visible tree, including hidden boundary nodes so omitted
subtrees remain explicit. Enable **Include hidden UI subtrees** for full-tree
research. This does not make unloaded stash tabs available: each tab must be
opened and captured separately. String candidates are not guaranteed UI labels.

Captures run in small render-thread slices. They are **not atomic snapshots**.
Changing tabs during capture can mix observations even if the root stays the same.
Process, area, root, state changes, timeouts and interrupted renders abort with
partial evidence. A cancelled capture is also saved as partial. Node/depth/queue
limits and read errors are explicitly recorded; there is no silent "complete".
Text/item candidate absence means undecoded or unavailable, not proof of an empty UI.

Suggested stash evidence: closed, normal tab, second differently named tab, empty
tab, waystone/special tab, and vendor. This separates stash identity from generic
left-panel visibility before implementing Stash SDK operations.

## Validation

`dotnet run --project Plugins/UiDump/Tests/UiDump.Tests.csproj -c Release`

Tests exercise traversal, parent paths, hidden ancestry, cycles, unreadable nodes,
read exceptions, node/depth/queue limits and cancellation using synthetic trees.
Live-game UI layouts, text candidates and special-tab item decoding still require
captured evidence from the running PoE2 client.
