#!/usr/bin/env python3
"""Build an offline Expedition rune catalog from PoE2DB pages and local recipes.

This is a maintainer tool. TEHhub never imports pages at runtime.
"""
from __future__ import annotations

import argparse
import html
import json
import re
import sys
import time
from dataclasses import asdict, dataclass
from datetime import UTC, datetime
from html.parser import HTMLParser
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

USER_AGENT = "TEHhub-ExpeditionCatalogResearch/1.0 (maintainer import; offline catalog build)"
DEFAULT_BASE_URL = "https://poe2db.tw/us/{slug}_Rune"
MAX_RETRIES = 2
RETRY_DELAY_SECONDS = 1.0
REQUEST_TIMEOUT_SECONDS = 15
REQUEST_INTERVAL_SECONDS = 1.0


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
    source_url: str
    fetched_at_utc: str | None
    effects: list[str]
    danger_tags: list[str]
    recipes: list[RecipeReference]
    source_status: str
    parse_notes: list[str]


class TextExtractor(HTMLParser):
    """Keeps visible text and metadata without depending on PoE2DB's page layout."""

    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self._skip_depth = 0
        self._chunks: list[str] = []
        self.description: str | None = None
        self.title: str | None = None

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        attributes = dict(attrs)
        if tag in {"script", "style", "noscript", "svg"}:
            self._skip_depth += 1
        if tag == "meta":
            key = (attributes.get("property") or attributes.get("name") or "").lower()
            content = attributes.get("content")
            if content and key in {"description", "og:description"} and not self.description:
                self.description = content.strip()
            if content and key == "og:title" and not self.title:
                self.title = content.strip()
        if tag in {"br", "p", "li", "tr", "h1", "h2", "h3", "h4", "div"}:
            self._chunks.append("\n")

    def handle_endtag(self, tag: str) -> None:
        if tag in {"script", "style", "noscript", "svg"} and self._skip_depth:
            self._skip_depth -= 1
        if tag in {"p", "li", "tr", "h1", "h2", "h3", "h4", "div"}:
            self._chunks.append("\n")

    def handle_data(self, data: str) -> None:
        if not self._skip_depth:
            self._chunks.append(data)

    def lines(self) -> list[str]:
        text = html.unescape("".join(self._chunks)).replace("\xa0", " ")
        return [line.strip() for line in re.split(r"[\r\n]+", text) if line.strip()]


def slug_for(name: str) -> str:
    return re.sub(r"[^A-Za-z0-9]+", "_", name).strip("_")


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


def fetch(url: str) -> tuple[str | None, str | None]:
    last_error: str | None = None
    for attempt in range(MAX_RETRIES + 1):
        try:
            request = Request(url, headers={"User-Agent": USER_AGENT, "Accept": "text/html"})
            with urlopen(request, timeout=REQUEST_TIMEOUT_SECONDS) as response:
                return response.read().decode(response.headers.get_content_charset() or "utf-8", errors="replace"), None
        except (HTTPError, URLError, TimeoutError) as error:
            last_error = f"{type(error).__name__}: {error}"
            if attempt < MAX_RETRIES:
                time.sleep(RETRY_DELAY_SECONDS * (attempt + 1))
    return None, last_error


def extract_effects(page: str) -> tuple[list[str], list[str]]:
    parser = TextExtractor()
    parser.feed(page)
    # Keep only the compact mechanics section before the recipe list.  Recipe rewards and
    # page navigation must never become rune effects.
    lines = parser.lines()
    try:
        end = lines.index("Runeshape Combinations")
    except ValueError:
        end = len(lines)
    mechanics = [re.sub(r"[{}<>]", "", line).strip() for line in lines[:end]]
    candidates: list[str] = []
    for index, line in enumerate(mechanics):
        if line.endswith(":") and index + 1 < len(mechanics):
            candidates.append(line + " " + mechanics[index + 1])
        candidates.append(line)
    seen: set[str] = set()
    effects: list[str] = []
    pattern = re.compile(r"\b(monsters?|enemies|players?)\b|\b(?:cannot|can not|gain|have|are|is|inflicts)\b", re.I)
    for candidate in candidates:
        normalized = re.sub(r"\s+", " ", candidate or "").strip(" -•")
        if normalized.endswith(":"):
            continue
        if 8 <= len(normalized) <= 300 and pattern.search(normalized) and normalized not in seen:
            seen.add(normalized)
            effects.append(normalized)
    return effects[:40], (["No effect-like text found; retain as unverified."] if not effects else [])


def derive_danger_tags(effects: list[str]) -> list[str]:
    text = "\n".join(effects).casefold()
    tags: list[str] = []
    # Conservative: tag only phrases explicitly present in extracted page text.
    if re.search(r"(?:cannot|can not) (?:be |have )?(?:life )?leeched from|cannot leech", text):
        tags.append("leech-block")
    if "corrupted blood" in text:
        tags.append("corrupted-blood")
    return tags


def read_html(html_dir: Path, rune: str) -> tuple[str | None, str | None]:
    path = html_dir / f"{slug_for(rune)}_Rune.html"
    if not path.is_file():
        return None, f"Offline HTML missing: {path.name}"
    return path.read_text(encoding="utf-8", errors="replace"), None


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--recipes", type=Path, default=Path("Plugins/LootValue/expedition2_recipes.json"))
    parser.add_argument("--output", type=Path, default=Path("Plugins/ExpeditionPlanner/Research/rune-catalog.generated.json"))
    parser.add_argument("--html-dir", type=Path, help="Parse saved pages only; never contacts the network.")
    parser.add_argument("--base-url", default=DEFAULT_BASE_URL, help="URL template containing {slug}.")
    parser.add_argument("--rune", action="append", dest="runes", help="Limit import to one exact rune name; repeatable.")
    args = parser.parse_args()

    names, recipes = load_recipes(args.recipes)
    selected = args.runes or names
    unknown = sorted(set(selected) - set(names))
    if unknown:
        parser.error(f"Unknown rune names: {', '.join(unknown)}")

    entries: list[RuneCatalogEntry] = []
    for index, rune in enumerate(selected):
        if index and not args.html_dir:
            time.sleep(REQUEST_INTERVAL_SECONDS)
        url = args.base_url.format(slug=slug_for(rune))
        page, error = read_html(args.html_dir, rune) if args.html_dir else fetch(url)
        if page is None:
            entries.append(RuneCatalogEntry(rune, url, None, [], [], recipes[rune], "unavailable", [error or "Unknown fetch failure"]))
            continue
        effects, notes = extract_effects(page)
        entries.append(RuneCatalogEntry(
            rune, url, datetime.now(UTC).isoformat(), effects, derive_danger_tags(effects), recipes[rune],
            "parsed" if effects else "unverified", notes,
        ))

    document = {
        "schema_version": 1,
        "generated_at_utc": datetime.now(UTC).isoformat(),
        "source_recipes": str(args.recipes).replace("\\", "/"),
        "network_used": args.html_dir is None,
        "entries": [asdict(entry) for entry in entries],
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(document, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    unavailable = sum(entry.source_status != "parsed" for entry in entries)
    print(f"Wrote {len(entries)} entries to {args.output} ({unavailable} unavailable).")
    return 1 if unavailable else 0


if __name__ == "__main__":
    raise SystemExit(main())
