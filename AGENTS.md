# Project version and changelog policy

TEHhub starts at 1.0.0 and uses MAJOR.MINOR.PATCH (x.x.x). For every completed change, including small fixes and documentation changes, update CHANGELOG.md and advance the appropriate version. Group related edits delivered together into one version. MAJOR is for incompatible changes, MINOR for compatible new features, PATCH for fixes and small improvements. Keep core and launcher versions synchronized; assembly/file versions use x.x.x.0. Changelog entries must include date, concrete changes, compatibility notes when relevant, and validation actually performed. Do not claim live-game testing unless performed.

# Sub-agent implementation and debugging policy

For substantial implementation work and bug audits, use separate sub-agents when independent work is available: assign one agent to code implementation and another to debugging, tests, Release/Debug separation, and regression review. The primary agent coordinates scope, reviews their results, integrates changes, and owns version/changelog updates. Do not have agents edit the same files concurrently; define file ownership before delegation. Reuse available agents when new-agent limits are reached.

Choose the least expensive suitable model and reasoning effort for each assignment. Send only necessary context, request concise evidence, and avoid duplicate investigations. Use additional agents only for concrete independent tasks; handle trivial changes directly when delegation would waste tokens. User authorization for this delegation persists across tasks in this project.

User installations use Debug with diagnostics and local APIs. Public Release packages must compile out diagnostic API listeners and unnecessary Debug tooling, rather than merely leaving them disabled. Verify both configurations after changes affecting this boundary. Ordinary bounded application error logs remain available in Release.

# PoE 2 Controller Mode UI & Memory Architecture Reference

## Overview

In Path of Exile 2, the UI hierarchy in **Controller / Gamepad Mode** differs drastically from Keyboard/Mouse Mode.
This document records the exact discovery methodology, UI tree layout, and key fingerprints for the **Atlas Map** and **Atlas Passive Skill Tree**.

---

## 1. Key Gameplay Node Signatures

When scanning live memory (`GameUi`), node container sizes act as definitive fingerprints:
- **Character Passive Skill Tree**: `~1,373 nodes` (`root.17.2.2.0` in Gamepad mode)
- **Atlas Map Nodes**: `751 nodes` (PoE2 0.5.x Endgame Maps, `root.17.2.3.0.0[6]`)
- **Atlas Passive Skill Tree**: `510 nodes` total (`root.17.2.3.4` has 9 children; child 0 has 277 cluster roots that branch into 510 nodes)

---

## 2. Controller Mode UI Hierarchy

Under `GameUi` (`InGameState.GameUi` / `GamepadUiRootStructPtr -> 0x340`):

```
GameUi (Root)
└── Child 17 (Gamepad UI Root)
    └── Child 2
        ├── Child 2 -> [0] Character Passive Skill Tree (1,373 nodes)
        └── Child 3 -> WorldMap Panel Container (Size: 2560 x 1463.5)
            ├── Child 0 -> World / Act / Atlas Map Container
            │   └── Child 0 -> Tabs Container
            │       ├── Child 0 -> Act 1
            │       ├── Child 1 -> Act 2
            │       ├── Child 2 -> Act 3
            │       ├── Child 3 -> Act 4
            │       ├── Child 4 -> Act 5
            │       ├── Child 5 -> Interlude
            │       └── Child 6 -> Atlas Map (751 map nodes, fp 0x542EF3)
            └── Child 4 -> Atlas Passive Skill Tree Panel
                └── Child 0 -> 277 Cluster Roots (summing to 510 total passive nodes)
```

---

## 3. The "Ghost Visibility" Behavior in Controller Mode

### The Problem
When the player is viewing the Atlas Map and presses the controller button to view the **Atlas Passive Skill Tree**:
1. The game sets `root.17.2.3.4` (Atlas Passive) to:
   - `IsVisible = True` (`Flags` bit 0x800 set, e.g. `0x562EF1`)
   - `TotalChildrens = 9` (child 0 has 277 cluster roots)
2. **HOWEVER**, the underlying Atlas Map container (`root.17.2.3.0.0[6]`, 751 nodes) **REMAINS `IsVisible = True`** in memory underneath!
3. Overlays checking only `atlasUi.IsVisible` will continue rendering map badges, routes, and text right on top of the passive skill circles.

### When Atlas Passive is Closed
1. `root.17.2.3.4` becomes:
   - `IsVisible = False` (`Flags = 0x45626F1`, bit 0x800 cleared)
   - `TotalChildrens = 0`
   - `Size = (0, 0)`

---

## 4. Robust Discovery Algorithm (Parent Climbing)

Do **NOT** rely purely on fixed absolute paths (e.g. `{ 17, 2, 3, 4 }`) across different resolutions or game updates if they shift.
Instead, use **Parent-Climbing** starting from the known Atlas Map node container:

```csharp
// atlasAddr is the 751-node Atlas Map container
IntPtr pTabs = Read<UiElementBaseOffset>(atlasAddr).ParentPtr;          // root.17.2.3.0.0
IntPtr pMapRoot = Read<UiElementBaseOffset>(pTabs).ParentPtr;           // root.17.2.3.0
IntPtr pWorldMap = Read<UiElementBaseOffset>(pMapRoot).ParentPtr;       // root.17.2.3

// Child 4 under WorldMap is the Atlas Passive Tree panel
IntPtr passivePanel = GetChildAddress(pWorldMap, 4);

bool isAtlasPassiveOpen = passivePanel != IntPtr.Zero
    && (Read<UiElementBaseOffset>(passivePanel).Flags & 0x800) != 0
    && GetChildCount(passivePanel) > 0;
```

If `isAtlasPassiveOpen == true`, hide the Atlas2 overlay immediately (`return;`).
