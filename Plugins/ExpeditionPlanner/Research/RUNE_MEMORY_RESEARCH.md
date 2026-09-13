# Expedition Rune Memory & Awake Entity Research Notes

## 1. Context & Objective
In Path of Exile 2 (Expedition 2 / Dawn of the Hunt), Remnants contain Runeshapes / Monoliths with 1 to 16 rune sockets (typically 6 sockets).
One socket is the Golden Crown socket, which proliferates its modifier to all subsequent explosives in the detonation sequence.
Our objective was to discover whether the game stores all individual socketed runes (Slot 0..N-1) in live game memory within the awake `Expedition2Encounter` entity before the player interacts with or opens the monolith UI.

---

## 2. Verified & Working Memory Layout (Authoritative)

Under `Expedition2Encounter` (`EntityCustomGroup == 103`):
- Has `StateMachine` component (`StateMachineComponentOffsets`).
- At `StateMachine + 0x20` is a `StdVector` of listener pointers.
- Walking the listener pointers resolves to `station = *(node) - 0x98` (or `sub - 0xA0`).
- Validated by checking `station + 0x10 == entity.Address` (`StationDeviceBackPtr`).

### Authoritative Offsets on `station`:
| Offset | Type | Description | Values / Notes |
|---|---|---|---|
| `+0x10` | `IntPtr` | `StationDeviceBackPtr` | Matches `entity.Address` |
| `+0x28` | `IntPtr` | `StationAnchorRefOffset` | Pointer to DAT row in `Rune.dat` (or `0` if Unique) |
| `+0x30` | `IntPtr` | `StationAnchorHolderOffset` | Pointer to table holder (`holder + 0x28` points to `tableBase`) |
| `+0x38` | `int` | `StationSocketCount` | Authoritative socket/hole count (e.g. 6) |
| `+0x3C` | `int` | `StationAnchorPosOffset` | 0-based slot index of the Anchor Rune |
| `+0x40` | `StdVector<int>` | `StationGoldenSlotsOffset` | Vector of golden crown slot indices (e.g. `[2]`) |

### Anchor Rune Name Resolution:
- `delta = rowPtr - tableBase`
- `rowStride = (delta % 0x68 == 0) ? 0x68 : 0x6C`
- `anchorIdx = delta / rowStride`
- Index maps 1:1 to the 34 canonical runes:
  `["Fire", "Cold", "Lightning", "Tempest", "Momentum", "Bloodletting", "Stone", "Adaptive", "Arcane", "Toxic", "Electrocuting", "Protective", "Cyclonic", "Vision", "Tidal", "Rebirth", "Prismatic", "Gasp", "Moon", "Celestial", "Opulent", "Rage", "Wisdom", "Sky", "Earth", "Life", "Bond", "Ward", "Soul", "Death", "Oath", "Time", "Power", "Bait"]`

---

## 3. Investigated Areas (Negative Results / Unfinished Hypotheses)

### Hypothesis A: Remaining Runes Stored as `StdVector<IntPtr>` on `station`
- **Investigation**: Scanned offsets `0x58` through `0x200` on the `station` struct for:
  - Vector headers with `TotalElements == socketCount` (e.g. 6 elements).
  - Pointers matching `tableBase + (N * rowStride)`.
- **Result**: NEGATIVE. No 6-element vector of DAT pointers exists on `station`. The only DAT pointer stored directly on `station` is the Anchor Rune (`+0x28`).

### Hypothesis B: Compact Integer Indexes (0..33)
- **Investigation**: Scanned offsets `0x00` through `0x400` for byte/int arrays containing consecutive rune IDs.
- **Result**: NEGATIVE. No compact array matching the remaining socket slots was found.

### Hypothesis C: `RuneEncounterController@80` Inventories
- **Investigation**: Paired monster entity `Metadata/Monsters/LeagueExpeditionNew/RuneEncounterController@80` has an `Inventories` component.
- **Result**: NEGATIVE. Probing vector headers in `Controller.Inventories` revealed no `Item` entities. The game does not treat socketed monolith runes as `WorldItem` or bag `Inventory` items.

### Hypothesis D: Reverse VirtualQueryEx Memory Scan
- **Investigation**: Scanned 32 MB of committed pages within 1 GiB radius of the station looking for clusters of DAT row pointers.
- **Result**: Pointers to `Rune.dat` exist across client cache/lookup tables, but are not linked in a per-station sequential array in awake memory before interaction.

---

## 4. Conclusion & True Architectural Source of Truth

In PoE 2:
1. **Server/Map State (Awake Entity)**:
   The server only sends the **Anchor Rune**, **Socket Count**, and **Golden Slot Index** to the client's awake entity.
2. **Full Recipe & Socketed Rune Data**:
   The full recipe and combination options are sent by the server and populated into the UI **when the player opens the Monolith**:
   - UI Element: `GameUi.RuneshapeCombinationsPanel` (Child `[39]` of `GameUi`, fingerprint `0x00462EF1`).
   - Each recipe row contains child text labels (e.g. `"20x Exalted Orb"`, `"1x Divine Orb"`).
3. **Pre-interaction Estimation (from Distance)**:
   Can be deduced with high probability using the `322` catalog recipes in `expedition2_recipes.json` filtered by:
   `recipe.size <= holeCount && recipe.runeIdx[anchorPos] == anchorIdx`.
