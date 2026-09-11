#!/usr/bin/env python3
"""Build the embedded playlist catalog from reviewed snapshots or explicit downloads.

This is maintainer tooling.  The desktop application never imports this module or fetches
playlist data at runtime.
"""
from __future__ import annotations

import argparse
import json
import os
import tempfile
import urllib.request
from datetime import date
from pathlib import Path
from typing import Any

COMMUNITY_URL = "https://fortnite-api.com/v1/playlists"
EPIC_URL = "https://fortnitecontent-website-prod07.ol.epicgames.com/content/api/pages/fortnite-game/"
SUPPORTED_FAMILIES = {"BR", "Zero Build", "Reload", "Blitz"}


class ValidationError(ValueError):
    pass


def load_json(path: Path) -> Any:
    try:
        with path.open(encoding="utf-8") as stream:
            value = json.load(stream)
    except (OSError, json.JSONDecodeError) as error:
        raise ValidationError(f"{path}: invalid JSON: {error}") from error
    if value in (None, {}, []):
        raise ValidationError(f"{path}: source must not be empty")
    return value


def download(url: str) -> Any:
    request = urllib.request.Request(url, headers={"User-Agent": "BotOrNot playlist catalog maintainer"})
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            if response.status != 200:
                raise ValidationError(f"{url}: HTTP {response.status}")
            payload = response.read()
    except OSError as error:
        raise ValidationError(f"{url}: download failed: {error}") from error
    if not payload:
        raise ValidationError(f"{url}: empty response")
    try:
        return json.loads(payload)
    except json.JSONDecodeError as error:
        raise ValidationError(f"{url}: invalid JSON: {error}") from error


def normalize_sources(community: Any, epic: Any) -> tuple[dict[str, dict[str, Any]], dict[str, Any]]:
    if (not isinstance(community, dict) or community.get("status") != 200 or
            not isinstance(community.get("data"), list) or not community["data"]):
        raise ValidationError("community source must be a successful {status: 200, data: [...]} response")
    items: dict[str, dict[str, Any]] = {}
    for item in community["data"]:
        if not isinstance(item, dict) or not isinstance(item.get("id"), str) or not item["id"].strip():
            raise ValidationError("community source contains a playlist without a non-empty id")
        key = item["id"].casefold()
        if key in items:
            raise ValidationError(f"community source has duplicate playlist id {item['id']!r}")
        items[key] = item
    if not isinstance(epic, dict) or not epic:
        raise ValidationError("Epic source must be a non-empty object")
    return items, epic


def tags(item: dict[str, Any]) -> set[str]:
    return {value.casefold() for value in item.get("gameplayTags", []) if isinstance(value, str)}


def infer_descriptor(item: dict[str, Any], observed_date: str) -> dict[str, Any] | None:
    game_type = str(item.get("gameType", "")).casefold()
    item_tags = tags(item)
    path = str(item.get("path", "")).casefold()
    # UGC, Creative and internal modes are deliberately unresolved.  Their file names must
    # never be guessed as battle-royale variants.
    if "ugc" in " ".join(item_tags) or "creative" in " ".join(item_tags) or "vkplay" in game_type:
        return None

    family = None
    if "blastberry" in game_type or "blastberry" in " ".join(item_tags):
        family = "Reload"
    elif "forbiddenfruit" in game_type or "forbiddenfruit" in path or "blitz" in " ".join(item_tags):
        family = "Blitz"
    elif game_type.endswith("::br"):
        family = "BR"
    elif "zerobuild" in game_type:
        family = "BR"
    if family not in SUPPORTED_FAMILIES:
        return None

    zero_build = (
        "zerobuild" in game_type or
        "nobuild" in path or
        "athena.playlist.nobuild" in item_tags or
        "athena.playlist.nobuildingmaterials" in item_tags or
        any(value.startswith("product.") and "zerobuild" in value for value in item_tags)
    )
    build_mode = "Zero Build" if zero_build else "Build"
    team_size = item.get("maxTeamSize")
    if not isinstance(team_size, int) or team_size < 1:
        team_size = None
    rating = str(item.get("ratingType", "")).casefold()
    ranked = "ranked" in rating or any("habanero" in value or ".comp" in value for value in item_tags)
    return {
        "family": family,
        "buildMode": build_mode,
        "teamSize": team_size,
        "rankedState": "ranked" if ranked else "unknown",
        "variant": None,
        "sources": ["fortnite-api.com/v1/playlists"],
        "observedDate": observed_date,
        "confidence": "source-derived",
    }


def display_name(descriptor: dict[str, Any]) -> str:
    family = descriptor["family"]
    build = descriptor["buildMode"]
    team_size = descriptor["teamSize"]
    team = {1: "Solo", 2: "Duos", 3: "Trios", 4: "Squads", 6: "Six-stack"}.get(team_size)
    if not team:
        raise ValidationError("new source-derived mappings require an explicit supported maxTeamSize")
    # Do not append “Pubs”: source absence of a ranked tag is not evidence of public matchmaking.
    ranked = " Ranked" if descriptor["rankedState"] == "ranked" else ""
    return f"{family} {build} {team}{ranked}"


def label_agrees(record: dict[str, Any], descriptor: dict[str, Any]) -> bool:
    """Do not turn source hints into structured facts when curated history disagrees."""
    label = record["display_name"].casefold()
    if descriptor["family"].casefold() not in label and not (
            descriptor["family"] == "BR" and "battle royale" in label):
        return False
    zero_build = "zero build" in label
    if (descriptor["buildMode"] == "Zero Build") != zero_build:
        return False
    if (descriptor["rankedState"] == "ranked") != ("ranked" in label):
        return False
    team_words = {1: "solo", 2: "duo", 3: "trio", 4: "squad", 6: "six"}
    word = team_words.get(descriptor["teamSize"])
    return word is None or word in label


def load_overrides(path: Path | None) -> dict[str, dict[str, Any]]:
    if path is None:
        return {}
    raw = load_json(path)
    values = raw.get("overrides") if isinstance(raw, dict) else None
    if not isinstance(values, list):
        raise ValidationError("override file must contain an overrides array")
    result: dict[str, dict[str, Any]] = {}
    for value in values:
        if not isinstance(value, dict) or not isinstance(value.get("playlist_name"), str):
            raise ValidationError("every override needs playlist_name")
        key = value["playlist_name"].casefold()
        if key in result:
            raise ValidationError(f"override duplicate {value['playlist_name']!r}")
        result[key] = value
    return result


def canonical_catalog(existing: Any) -> list[dict[str, Any]]:
    if not isinstance(existing, dict) or not isinstance(existing.get("playlists"), list):
        raise ValidationError("catalog must contain a playlists array")
    seen: set[str] = set()
    records = []
    for record in existing["playlists"]:
        if not isinstance(record, dict) or not isinstance(record.get("playlist_name"), str) or not isinstance(record.get("display_name"), str):
            raise ValidationError("every catalog record needs playlist_name and display_name")
        key = record["playlist_name"].casefold()
        if key in seen:
            raise ValidationError(f"catalog duplicate ignoring case: {record['playlist_name']!r}")
        seen.add(key)
        records.append(dict(record))
    return records


def build(existing: Any, community: Any, epic: Any, overrides: dict[str, dict[str, Any]],
          observed_date: str, include_new: bool = False) -> tuple[dict[str, Any], dict[str, Any]]:
    records = canonical_catalog(existing)
    sources, _ = normalize_sources(community, epic)
    by_id = {record["playlist_name"].casefold(): record for record in records}
    report: dict[str, list[Any]] = {key: [] for key in ("added", "changed", "preserved", "unresolved", "conflicts")}

    for key, record in by_id.items():
        source = sources.get(key)
        override = overrides.get(key)
        before = json.dumps(record, sort_keys=True, separators=(",", ":"))
        descriptor = infer_descriptor(source, observed_date) if source else None
        if override:
            record.update({key: value for key, value in override.items() if key != "playlist_name"})
            record["_source"] = "curated-override"
        elif descriptor and label_agrees(record, descriptor):
            # Labels in the existing catalog are reviewed historical data.  Preserve them even
            # when upstream names differ, and make that disagreement visible to maintainers.
            record.update(descriptor)
            if source.get("name") and source["name"] != record["display_name"]:
                report["conflicts"].append({"playlist_name": record["playlist_name"], "upstream_name": source["name"],
                                            "preserved_display_name": record["display_name"]})
        elif descriptor:
            if record.get("confidence") == "source-derived":
                for field in ("family", "buildMode", "teamSize", "rankedState", "variant",
                              "sources", "observedDate", "confidence"):
                    record.pop(field, None)
            report["conflicts"].append({"playlist_name": record["playlist_name"],
                                        "source_descriptor": descriptor,
                                        "preserved_display_name": record["display_name"]})
            report["preserved"].append(record["playlist_name"])
        else:
            report["preserved"].append(record["playlist_name"])
        if json.dumps(record, sort_keys=True, separators=(",", ":")) != before:
            report["changed"].append(record["playlist_name"])

    for key, override in overrides.items():
        if key not in by_id:
            record = dict(override)
            if not isinstance(record.get("display_name"), str):
                raise ValidationError(f"new override {record.get('playlist_name')!r} needs display_name")
            record["_source"] = "curated-override"
            records.append(record)
            by_id[key] = record
            report["added"].append(record["playlist_name"])

    for key, source in sources.items():
        if key in by_id:
            continue
        descriptor = infer_descriptor(source, observed_date)
        if not descriptor:
            report["unresolved"].append({"playlist_name": source["id"], "reason": "unsupported-or-insufficient-evidence"})
            continue
        if not include_new:
            report["unresolved"].append({"playlist_name": source["id"], "reason": "new-source-entry-requires-review"})
            continue
        try:
            label = display_name(descriptor)
        except ValidationError:
            report["unresolved"].append({"playlist_name": source["id"], "reason": "unsupported-team-size"})
            continue
        record = {"playlist_name": source["id"], "display_name": label, **descriptor, "_source": "source-derived"}
        records.append(record)
        report["added"].append(record["playlist_name"])

    records.sort(key=lambda value: (value["playlist_name"].casefold(), value["playlist_name"]))
    for values in report.values():
        values.sort(key=lambda value: (value if isinstance(value, str) else value["playlist_name"]).casefold())
    catalog = {
        "_comment": "Generated by scripts/generate_playlist_mappings.py; historical labels are retained until reviewed.",
        "schemaVersion": 1,
        "_lastUpdated": existing.get("_lastUpdated", observed_date),
        "playlists": records,
    }
    return catalog, {"schemaVersion": 1, "observedDate": observed_date, **report}


def json_bytes(value: Any) -> bytes:
    return (json.dumps(value, indent=2, ensure_ascii=False, sort_keys=False) + "\n").encode("utf-8")


def atomic_write(path: Path, payload: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(dir=path.parent, delete=False) as stream:
        stream.write(payload)
        temporary = Path(stream.name)
    os.replace(temporary, path)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--catalog", type=Path, default=Path("BotOrNot.Core/Data/PlaylistMappings.json"))
    parser.add_argument("--community", type=Path)
    parser.add_argument("--epic", type=Path)
    parser.add_argument("--overrides", type=Path, default=Path("scripts/playlist-overrides.json"))
    parser.add_argument("--observation-date", default=None)
    parser.add_argument("--output", type=Path, default=Path("BotOrNot.Core/Data/PlaylistMappings.json"))
    parser.add_argument("--report", type=Path, default=Path("docs/playlist-catalog-report.json"))
    parser.add_argument("--download", action="store_true", help="download explicit source snapshots before validation")
    parser.add_argument("--include-new", action="store_true", help="include source-derived entries not already in the catalog")
    parser.add_argument("--write", action="store_true", help="atomically replace output and report after validation")
    args = parser.parse_args()
    if args.download:
        community, epic = download(COMMUNITY_URL), download(EPIC_URL)
    else:
        if args.community is None or args.epic is None:
            parser.error("--community and --epic are required unless --download is used")
        community, epic = load_json(args.community), load_json(args.epic)
    observed_date = args.observation_date or date.today().isoformat()
    try:
        date.fromisoformat(observed_date)
    except ValueError as error:
        raise ValidationError("--observation-date must be ISO YYYY-MM-DD") from error
    catalog, report = build(load_json(args.catalog), community, epic, load_overrides(args.overrides),
                            observed_date, args.include_new)
    catalog_payload, report_payload = json_bytes(catalog), json_bytes(report)
    if args.write:
        atomic_write(args.output, catalog_payload)
        atomic_write(args.report, report_payload)
    else:
        print(f"Validated {len(catalog['playlists'])} catalog entries; would write {args.output} and {args.report}.")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except ValidationError as error:
        raise SystemExit(f"playlist generator: {error}")
