# Data Visualization (DV v2) Code Audit & Architecture Baseline

## 1. Current Implementation Inventory & Entry Point
- **Host Window:** TEHhub/Ui/DataVisualization.cs (DataVisualization.DataVisualizationRenderCoRoutine, priority UiRenderPriority.CoreWindows).
- **Central Hub:** TEHhub/Core.cs (RemoteObjectsToImGuiCollapsingHeader(), CacheImGui(), States, Process, CurrentAreaLoadedFiles, GHSettings).
- **Reflection Contract:** TEHhub/RemoteObjects/RemoteObjectBase.cs (ToImGui(), GetToImGuiMethods(), RemoteObjectPropertyDetail).
- **Activation:** Toggled via Settings (F12) -> Tools -> Data Visualization (DV) checkbox (Core.GHSettings.ShowDataVisualization).
- **UI Helpers:** TEHhub/Utils/ImGuiHelper.cs (IntPtrToImGui, DisplayTextAndCopyOnClick, StatsWidget, DisplayFloatWithInfinitySupport, EnumComboBox).

---

## 2. Capability Matrix & Coverage

| Capability | Source File & Class | Hierarchy Depth | Live Read Semantics | Special Features / Actions |
| :--- | :--- | :---: | :--- | :--- |
| **Settings Inspector** | DataVisualization.cs | Root | In-memory fields | Dumps all Core.GHSettings values live |
| **GGPK Caches** | GgpkAddresses.cs | Root / 1 | In-memory cache | Click-to-copy GGPK address strings |
| **Process & Statics** | DataVisualization.cs | Root / 1 | Cached at attach | Click-to-copy static AOB addresses |
| **GameStates Root** | GameStates.cs | Root / 1 | Cached state pointers | State addresses & CurrentState enum |
| **InGameState** | InGameState.cs | Depth 1 | Cached pointer | Click-to-copy UiRoot address |
| **AreaInstance** | AreaInstance.cs | Depth 2 | Cached per area/frame | Monster Level, Area Hash, Terrain info |
| **Map Modifiers** | AreaInstance.cs | Depth 3 | Cached mod vector | Click-to-copy modifier display name/values |
| **Environment Info** | AreaInstance.cs | Depth 3 | Cached environment list | Click-to-copy environment keys |
| **Awake Entities** | AreaInstance.cs | Depth 3 | Per-frame collection | Filter by ID, Path, Rarity; Dump Models |
| **Sleeping Entities** | AreaInstance.cs | Depth 3 | On-demand scan | Button "Scan Sleeping Entities" (one-shot) |
| **Entity Properties** | Entity.cs | Depth 4 | Cached per frame | Path, ID, IsValid, Zone, Subtype, State |
| **Expedition Runes** | Entity.cs | Depth 4 | Real-time state | Golden crown slot, Anchor rune, Proliferation |
| **Explosive Range** | Entity.cs | Depth 4 | Real-time calculation | World radius, bomb coverage & closest delta |
| **Entity Components**| Entity.cs | Depth 5 | Lazy instantiation | Button "Load##<Comp>" for uncached comps |
| **Life (HP/MP/ES)** | Life.cs | Depth 6 | Cached per frame | Health, Mana, ES, Ward, Divinity, Spirit |
| **Buffs / Debuffs** | Buffs.cs | Depth 6 | Cached per frame | Buff definitions, TimeLeft, Charges, FlaskSlot |
| **Actor (Skills/CD)**| Actor.cs | Depth 6 | Cached per frame | Animation ID, Cooldowns, Gem link/socket info |
| **Entity Stats** | Stats.cs | Depth 6 | Cached snapshot | Items stats / Buffs stats (Click-to-copy) |
| **Position / Render**| Render.cs, Positioned.cs | Depth 6 | Cached per frame | GridPos, WorldPos, TerrainHeight, Bounds, Reaction |
| **ServerData & Gold**| ServerData.cs | Depth 3 | Real-time on-demand | Real-time Current Gold readout & diagnostics |
| **Inventories** | ServerData.cs, Inventory.cs | Depth 3 / 4 | Cached on change | Inventory combo selector, 2D slot grid, items |
| **UI Tree & Atlas** | ImportantUiElements.cs | Depth 2 / 3 | Cached UI pointers | Atlas Maps biome & "Copy layout scan" |
| **UiElement Debug** | UiElementBase.cs | Depth 3+ | Lazy slot materialization| Checkbox "Show" (yellow box), Button "Explore" |
| **WorldData / Matrix**| WorldData.cs | Depth 2 | Volatile snapshot | 4x4 WorldToScreen matrix display, AreaDetails |
| **Preload Dumps** | LoadedFiles.cs | Root | In-memory loaded files | "Save" preload dump, Multi-term search |

---

## 3. Current Navigation Tree & Click-Depth Table

`	ext
Data Visualization (Window)
├── Settings (Root)
├── GGPK String / Object Cache (Depth 1)
├── Game Process -> Static Addresses (Depth 2)
├── CurrentAreaLoadedFiles (Depth 1)
└── States (GameStates)
    └── InGameStateObject (Depth 1)
        ├── CurrentWorldInstance -> AreaDetails (Depth 3)
        ├── GameUi -> Atlas Maps (Depth 3)
        └── CurrentAreaInstance (Depth 2)
            ├── Area / Map Modifiers (Depth 3)
            ├── ServerDataObject [Gold: 142,500] (Depth 3)
            └── Player (LocalPlayer) (Depth 3)
                └── Components (Depth 4)
                    ├── Life -> Health / Mana / ES (Depth 6 - 7 clicks)
                    ├── Buffs -> Status Effect (Depth 6 - 7 clicks)
                    ├── Actor -> Active Skills / Cooldowns (Depth 6 - 7 clicks)
                    ├── Stats -> Items / Buffs Stats (Depth 6 - 7 clicks)
                    └── Render -> Grid / World Position (Depth 5 - 6 clicks)
`

| Destination Target | Navigation Path from DV Window | Clicks Required |
| :--- | :--- | :---: |
| **Preloaded Files** | CurrentAreaLoadedFiles | **1** |
| **Static Addresses** | Game Process -> Static Addresses | **2** |
| **Area Details** | States -> InGameStateObject -> CurrentWorldInstance -> AreaDetails | **3** |
| **Current Gold** | States -> InGameStateObject -> CurrentAreaInstance -> ServerDataObject | **3** |
| **Map Modifiers** | States -> InGameStateObject -> CurrentAreaInstance -> Area / Map Modifiers | **3** |
| **Player Position** | States -> InGameState -> CurrentAreaInstance -> Player -> Components -> Render | **5** |
| **Player Life (HP)** | States -> InGameState -> CurrentAreaInstance -> Player -> Components -> Life -> Health | **6** |
| **Player Buffs** | States -> InGameState -> CurrentAreaInstance -> Player -> Components -> Buffs -> Status Effect | **6** |
| **Player Skills** | States -> InGameState -> CurrentAreaInstance -> Player -> Components -> Actor -> Active Skills | **6** |
| **Player Stats** | States -> InGameState -> CurrentAreaInstance -> Player -> Components -> Stats | **5** |

---

## 4. Navigation Pain Points Identified in Source

1. **Excessive Click Depth (5 to 7 Clicks):** Inspecting everyday player components (Life, Buffs, Actor, Stats, Gold) requires unfolding many single-child nodes.
2. **Tree Collapse on Area Transitions:** Remote object addresses re-point upon changing zones, resetting ImGui node IDs and forcing repeated manual expansion.
3. **Isolated Search Functions:** Existing search bars (LoadedFiles and EntitiesWidget) only filter within their own isolated sub-trees. There is no global search.
4. **Coupled Navigation & Inspection:** Viewing an expanded component pushes all other items hundreds of pixels downward in a single shared vertical scroll region.
5. **No Bookmarking / History:** Zero shortcuts for frequent inspection targets (LocalPlayer.Life, ServerData, AreaMods).

---

## 5. Refresh & Memory-Read Performance Constraints

- **Cached Reads:** Most ToImGui() methods only format properties already cached by frame coroutines (AreaInstance.UpdateData, etc.).
- **Lazy Pointers:** Uncached components (NPC, StateMachine) and Sleeping Entities are read **strictly on user button clicks**.
- **CRITICAL SEARCH RULE:** DV v2 Global Search MUST index **metadata / type names / static registration paths only**. It must **never** eagerly traverse live pointers or instantiate unmaterialized components during search indexing.

---

## 6. Reusable Rendering Contract

DV v2 preserves 100% of existing debug features with zero inspector rewrites:

`	ext
[ DV v2 Navigation Shell: Search / Favorites / Tree ]
                       │
                       ▼ (Select Target Node)
[ Right Inspector Pane: targetRemoteObject.ToImGui() ]
`

Every RemoteObjectBase class already provides a self-contained ToImGui() method. DV v2 simply invokes the selected target's ToImGui() in the right inspector pane.

---

## 7. DV v2 Compatibility Contract Checklist

- [x] **MUST PRESERVE:** All 35+ ToImGui() implementations across all remote objects.
- [x] **MUST PRESERVE:** Click-to-copy for addresses and data values (ImGuiHelper).
- [x] **MUST PRESERVE:** Preload file dump saving (preload_dumps/) and multi-term filtering.
- [x] **MUST PRESERVE:** Expedition monolith rune parsing, golden slot detection, and explosive coverage math.
- [x] **MUST PRESERVE:** Dynamic component loading ("Load##Comp") and sleeping entity scanner.
- [x] **MUST PRESERVE:** UI element overlay bounding box ("Show") and bridge to GameUiExplorer ("Explore").
- [x] **MUST PRESERVE:** Atlas map layout report copying and biome inspections.
- [x] **MUST PRESERVE:** Full "Legacy Hierarchy" view tab as an absolute safety fallback.

---

## 8. Proposed DV v2 UI Architecture

`	ext
+----------------------------------------------------------------------------------------------------+
| [ < Back ] [ Forward > ] [ Search / Jump: "Gold"                     ] [ Clear ]   (DV v2) [X]     |
| Breadcrumb: States > InGameState > CurrentAreaInstance > ServerDataObject                          |
+------------------------------------+---------------------------------------------------------------+
| LEFT PANE: NAVIGATOR (~30%)        | RIGHT PANE: INSPECTOR (~70%)                                  |
| [⭐ Favorites] [🕒 Recent] [🌳 All] | Target: ServerDataObject (0x000001FA892B1000)                 |
|------------------------------------|---------------------------------------------------------------|
| ⭐ Favorites:                       | Address: 0x000001FA892B1000 [Copy]                            |
|   ⭐ LocalPlayer -> Life           | Current Gold: 142,500                                         |
|   ⭐ LocalPlayer -> Buffs          |                                                               |
|   ⭐ ServerData (Gold & Inv)       | Area / Map Modifiers (4)                                      |
|   ⭐ AreaInstance -> Modifiers     |   • Monsters deal 40% extra Fire Damage                       |
|                                    |                                                               |
| 🕒 Recent:                         | Inventories:                                                  |
|   • Player.Render                  |   [ Flask Inventory ] [ Main Inventory ]                     |
|   • AwakeEntities[1042]            |                                                               |
|                                    | Diagnostic Probe Info:                                        |
| 🌳 Hierarchy Tree (All):           |   Source: Native Memory (0x618)                               |
|   ▼ Core                           |   Last sample: 142500                                         |
|     ► Game Process                 |                                                               |
|     ► Loaded Files                 | [ ⭐ Add to Favorites ]  [ 📋 Copy Path ]  [ 🔄 Refresh ]      |
|     ▼ States                       |                                                               |
|       ▼ InGameState                |                                                               |
|         ▼ CurrentAreaInstance      |                                                               |
|           ► Player (LocalPlayer)   |                                                               |
|           ▼ ServerDataObject <───  |                                                               |
|           ► Awake Entities (184)   |                                                               |
+------------------------------------+---------------------------------------------------------------+
`

---

## 9. Implementation Roadmap (Phased Execution)

1. **Stage A (Registry & Metadata):** Define DvNode and static search registry with lambda getters (Func<RemoteObjectBase?>).
2. **Stage B (Split-Pane Shell):** Implement Left Navigator (Tabs) and Right Inspector viewport in TEHhub/Ui/DataVisualization.cs.
3. **Stage C (Search & Quick Jump):** Add fuzzy top-bar search filter jumping directly to target nodes with zero eager memory reads.
4. **Stage D (History & Breadcrumbs):** Implement Back/Forward navigation stack and clickable ancestor breadcrumbs.
5. **Stage E (Favorites & Persistence):** Add persistent JSON favorites list to State.cs.
6. **Stage F (Legacy Safety Tree):** Embed complete recursive legacy tree into the "All / Legacy" navigator tab.
7. **Stage G (Parity Verification):** Audit all 35+ components and features to ensure 100% feature parity.
8. **Stage H (Memory & Performance Validation):** Validate with MemoryReadDiagnostics that search creates zero extra memory read cycles.