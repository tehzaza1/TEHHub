# Local tool API (Debug only)

Debug starts the loopback listener at `http://localhost:9877`. Release compiles out the listener, tool hub and public diagnostic publisher; it never starts this API. Normal application logs remain available in both builds.

`GET /api/tools` lists routes and currently published diagnostic IDs.

| Route | Purpose |
| --- | --- |
| GET /api/tools/state | Process ID, version, game state, area, tool window flags, known UI panels and loaded plugin versions/enabled flags |
| GET /api/tools/diagnostics | Cached diagnostic reports from tools and plugins |
| GET /api/tools/diagnostics/{id} | One published report; 404 means no report has been published |
| GET /api/tools/logs | Bounded runtime log, including plugin warnings and errors |
| POST /api/tools/windows/{ShowSetting}?visible=true | Queue a tool window change for the render thread |

Existing diagnostics routes cover memory, offsets, performance, map capture and skill research; discover their exact paths through the catalog. These routes inspect the running program and cannot open a game panel or change game inputs.

The tool hub publishes state at most once per second from the render thread. Always check `updatedUtc`, `processId` and `version`: a paused render loop leaves stale snapshots, and another running instance may own the listener. HTTP requests do not read game memory through the tool hub. On-demand skill research uses its existing render-thread request queue.

Built-in reports include data visualization, element finder, UI explorer, primary-root research and offset try-fix. LootValue publishes `loot-value-exchange` after scanning the exchange list. Loaded/enabled status alone does not establish plugin compatibility or correctness; inspect logs and detailed reports too.

Plugins can publish copied string dictionaries through `TEHhub.Ui.ToolDiagnostics.Publish(id, values)` inside `#if DEBUG`. Limits are 256 IDs, 64 fields per report, 100 characters per key and 2,000 per value. Compile calls out of Release because the publisher type is absent there. Publishing does not perform memory reads.

Example: `Invoke-RestMethod http://localhost:9877/api/tools/state`
