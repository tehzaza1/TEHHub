# Expedition rune catalog importer

`import_rune_catalog.py` is a maintainer-only Python 3 tool. It never runs in TEHhub and is not loaded by a plugin. It links the 34 rune names and 322 Runeshape recipes already maintained in `Plugins/LootValue/expedition2_recipes.json` with evidence collected from individual PoE2DB Rune pages.

## Output contract

The generated JSON contains one entry per rune:

- `source_url`: exact page used as evidence.
- `effects`: bounded visible, effect-like text extracted from that page; raw HTML is never embedded.
- `danger_tags`: conservative derived tags. At present the importer emits only `leech-block` when the text explicitly says it cannot be leeched from, and `corrupted-blood` when that phrase is explicitly present.
- `recipes`: every local Runeshape recipe containing that rune, including result and area-level bounds.
- `source_status` and `parse_notes`: make missing, blocked, or ambiguous pages visible instead of treating them as safe.

A planner must treat an unavailable or unverified entry as **unknown**, not safe. Weights and build-specific exclusions belong in a later editable planner profile, not in this evidence catalog.

## Commands

Online collection is an explicit maintainer action. It makes at most three requests per rune (one initial attempt plus two retries), waits 1 then 2 seconds between retries, and has a 15-second request timeout:

```powershell
python Plugins/ExpeditionPlanner/Research/import_rune_catalog.py `
  --output Plugins/ExpeditionPlanner/Research/rune-catalog.generated.json
```

For repeatable/offline parsing, save each page as `<Rune>_Rune.html` (for example `Bloodletting_Rune.html`) and run:

```powershell
python Plugins/ExpeditionPlanner/Research/import_rune_catalog.py `
  --html-dir C:\temp\poe2db-runes `
  --output Plugins/ExpeditionPlanner/Research/rune-catalog.generated.json
```

To investigate one page without requesting all 34:

```powershell
python Plugins/ExpeditionPlanner/Research/import_rune_catalog.py --rune Bloodletting
```

The output is a research artifact for review. It must be validated against in-game tooltip text before being promoted to any runtime planner catalog.

## Source and attribution

The generated effect snippets are extracted from individual [PoE2DB](https://poe2db.tw/) Rune pages. PoE2DB states that its wiki content is available under CC BY-NC-SA 3.0. This repository keeps the catalog under `Research` only, records each source URL, and does not load it at runtime. Review the source licence and replace copied text with independently verified normalized tags before any public runtime distribution.

## Current generated snapshot

The 2026-09-13 import contains all 34 canonical rune entries and parsed effects for 29. `Tidal`, `Moon`, `Sky`, `Earth`, and `Bait` were unavailable or lacked parsable mechanics; consumers must treat those as unknown, never safe.
