# Expedition rune catalog

`import_rune_catalog.py` is a maintainer-only Python 3 tool. It never runs in TEHhub and is not loaded by a plugin. It produces a compact catalog from the local, maintained Runeshape recipe source at `Plugins/LootValue/expedition2_recipes.json`.

## Output contract

The generated JSON contains exactly one entry for every known rune:

- `name`: canonical rune name.
- `recipes`: every local Runeshape recipe containing that rune, including the result and area-level bounds.

Rune effects and inferred combat-danger tags are intentionally omitted. The planner will not make safety or reward decisions from web-scraped effect text. Those decisions require separately verified in-game evidence and editable player profiles.

## Command

```powershell
python Plugins/ExpeditionPlanner/Research/import_rune_catalog.py `
  --output Plugins/ExpeditionPlanner/Research/rune-catalog.generated.json
```

To regenerate a single entry while inspecting a recipe relationship:

```powershell
python Plugins/ExpeditionPlanner/Research/import_rune_catalog.py --rune Opulent
```

## Current generated snapshot

The catalog contains all 34 canonical runes and their local Runeshape recipe references. It has no runtime consumer and is research-only.