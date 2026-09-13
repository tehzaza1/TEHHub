#!/usr/bin/env python3
"""Build an offline Expedition rune-and-Runeshape-recipe catalog from local data.

This is a maintainer tool. TEHhub never loads this research artifact at runtime.
"""
from __future__ import annotations

import argparse
import json
from dataclasses import asdict, dataclass
from datetime import UTC, datetime
from pathlib import Path


@dataclass(frozen=True)
class RecipeReference:
    id: str
    size: int
    reward_name: str | None
    reward_count: int
    description: str
    min_level: int
    max_level: int


@dataclass(frozen=True)
class RuneCatalogEntry:
    name: str
    recipes: list[RecipeReference]


def load_recipes(path: Path) -> tuple[list[str], dict[str, list[RecipeReference]]]:
    source = json.loads(path.read_text(encoding="utf-8"))
    names = list(source["runes"].values())
    references: dict[str, list[RecipeReference]] = {name: [] for name in names}
    for recipe in source["recipes"]:
        reference = RecipeReference(
            id=recipe["id"], size=recipe["size"],
            reward_name=(recipe.get("reward") or {}).get("name"),
            reward_count=recipe["rewardCount"], description=recipe.get("description") or "",
            min_level=recipe["minLevel"], max_level=recipe["maxLevel"],
        )
        for rune in set(recipe["runes"]):
            references.setdefault(rune, []).append(reference)
    return names, references


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--recipes", type=Path, default=Path("Plugins/LootValue/expedition2_recipes.json"))
    parser.add_argument("--output", type=Path, default=Path("Plugins/ExpeditionPlanner/Research/rune-catalog.generated.json"))
    parser.add_argument("--rune", action="append", dest="runes", help="Limit output to one exact rune name; repeatable.")
    args = parser.parse_args()

    names, recipes = load_recipes(args.recipes)
    selected = args.runes or names
    unknown = sorted(set(selected) - set(names))
    if unknown:
        parser.error(f"Unknown rune names: {', '.join(unknown)}")

    document = {
        "schema_version": 2,
        "generated_at_utc": datetime.now(UTC).isoformat(),
        "source_recipes": str(args.recipes).replace("\\", "/"),
        "entries": [asdict(RuneCatalogEntry(rune, recipes[rune])) for rune in selected],
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(document, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"Wrote {len(selected)} entries to {args.output}.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())