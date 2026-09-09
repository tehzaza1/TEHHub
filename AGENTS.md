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
