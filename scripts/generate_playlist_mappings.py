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


def normalize_sources(community: Any, epic: Any) -> tuple[dict[str, dict[str, Any]], dict[str, dict[str, Any]]]:
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
        if "gameplayTags" in item and (
                not isinstance(item["gameplayTags"], list) or
                not all(isinstance(tag, str) for tag in item["gameplayTags"])):
            raise ValidationError(f"community playlist {item['id']!r} has malformed gameplayTags")
        for field in ("gameType", "path", "ratingType", "name"):
            if field in item and not isinstance(item[field], str):
                raise ValidationError(f"community playlist {item['id']!r} has malformed {field}")
        if "maxTeamSize" in item and item["maxTeamSize"] is not None and type(item["maxTeamSize"]) is not int:
            raise ValidationError(f"community playlist {item['id']!r} has malformed maxTeamSize")
        items[key] = item
    try:
        playlists = epic["playlistinformation"]["playlist_info"]["playlists"]
    except (KeyError, TypeError) as error:
        raise ValidationError("Epic source must contain playlistinformation.playlist_info.playlists") from error
    if not isinstance(playlists, list) or not playlists:
        raise ValidationError("Epic playlist list must be non-empty")
    official: dict[str, dict[str, Any]] = {}
    for item in playlists:
        if not isinstance(item, dict) or not isinstance(item.get("playlist_name"), str) or not item["playlist_name"].strip():
            raise ValidationError("Epic source contains a playlist without a non-empty playlist_name")
        key = item["playlist_name"].casefold()
        if key in official:
            raise ValidationError(f"Epic source has duplicate playlist id {item['playlist_name']!r}")
        if "display_name" in item and not isinstance(item["display_name"], str):
            raise ValidationError(f"Epic playlist {item['playlist_name']!r} has malformed display_name")
        official[key] = item
    return items, official


def tags(item: dict[str, Any]) -> set[str]:
    return {value.casefold() for value in item.get("gameplayTags", [])}


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

    zero_build_evidence = (
        "zerobuild" in game_type or
        "nobuild" in path or
        "athena.playlist.nobuild" in item_tags or
        "athena.playlist.nobuildingmaterials" in item_tags or
        any(value.startswith("product.") and "zerobuild" in value for value in item_tags)
    )
    rating = str(item.get("ratingType", "")).casefold()
    build_evidence = (
        any(value.startswith("product.") and value.endswith(".build") and "zero" not in value
            for value in item_tags) or
        "athena.quests.nobuild.exclude" in item_tags or
        (rating.endswith("build") and "zero" not in rating and "nobuild" not in rating)
    )
    if zero_build_evidence == build_evidence:
        return None
    build_mode = "Zero Build" if zero_build_evidence else "Build"
    team_size = item.get("maxTeamSize")
    if type(team_size) is not int or team_size < 1:
        team_size = None
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


def with_stable_observation_date(record: dict[str, Any], descriptor: dict[str, Any],
                                 observation_date: str) -> dict[str, Any]:
    """Only date a descriptor when its semantic evidence changes."""
    fields = ("family", "buildMode", "teamSize", "rankedState", "variant", "sources", "confidence")
    if all(record.get(field) == descriptor.get(field) for field in fields) and record.get("observedDate"):
        descriptor["observedDate"] = record["observedDate"]
    else:
        descriptor["observedDate"] = observation_date
    return descriptor


def load_overrides(path: Path | None) -> dict[str, dict[str, Any]]:
    if path is None:
        return {}
    raw = load_json(path)
    return load_overrides_from_value(raw)


def load_overrides_from_value(raw: Any) -> dict[str, dict[str, Any]]:
    values = raw.get("overrides") if isinstance(raw, dict) else None
    if not isinstance(values, list):
        raise ValidationError("override file must contain an overrides array")
    result: dict[str, dict[str, Any]] = {}
    for value in values:
        if not isinstance(value, dict) or not isinstance(value.get("playlist_name"), str) or not value["playlist_name"].strip():
            raise ValidationError("every override needs playlist_name")
        for field in ("display_name", "family", "buildMode", "rankedState", "variant", "confidence"):
            if field in value and (not isinstance(value[field], str) or not value[field].strip()):
                raise ValidationError(f"override {value['playlist_name']!r} has malformed {field}")
        if "teamSize" in value and (type(value["teamSize"]) is not int or value["teamSize"] < 1):
            raise ValidationError(f"override {value['playlist_name']!r} has malformed teamSize")
        allowed = {"playlist_name", "display_name", "family", "buildMode", "teamSize",
                   "rankedState", "variant", "sources", "observedDate", "confidence"}
        unexpected = set(value) - allowed
        if unexpected:
            raise ValidationError(f"override {value['playlist_name']!r} has unsupported fields {sorted(unexpected)}")
        key = value["playlist_name"].casefold()
        if key in result:
            raise ValidationError(f"override duplicate {value['playlist_name']!r}")
        result[key] = value
    return result


def canonical_catalog(existing: Any) -> list[dict[str, Any]]:
    if not isinstance(existing, dict) or not isinstance(existing.get("playlists"), list) or not existing["playlists"]:
        raise ValidationError("catalog must contain a playlists array")
    seen: set[str] = set()
    records = []
    for record in existing["playlists"]:
        if (not isinstance(record, dict) or not isinstance(record.get("playlist_name"), str) or
                not record["playlist_name"].strip() or not isinstance(record.get("display_name"), str) or
                not record["display_name"].strip()):
            raise ValidationError("every catalog record needs playlist_name and display_name")
        key = record["playlist_name"].casefold()
        if key in seen:
            raise ValidationError(f"catalog duplicate ignoring case: {record['playlist_name']!r}")
        seen.add(key)
        if "family" in record and record["family"] not in SUPPORTED_FAMILIES:
            raise ValidationError(f"catalog record {record['playlist_name']!r} has malformed family")
        if "buildMode" in record and record["buildMode"] not in ("Build", "Zero Build"):
            raise ValidationError(f"catalog record {record['playlist_name']!r} has malformed buildMode")
        if "teamSize" in record and (type(record["teamSize"]) is not int or record["teamSize"] < 1):
            raise ValidationError(f"catalog record {record['playlist_name']!r} has malformed teamSize")
        if "rankedState" in record and record["rankedState"] not in ("ranked", "unknown"):
            raise ValidationError(f"catalog record {record['playlist_name']!r} has malformed rankedState")
        if "variant" in record and record["variant"] is not None and (
                not isinstance(record["variant"], str) or not record["variant"].strip()):
            raise ValidationError(f"catalog record {record['playlist_name']!r} has malformed variant")
        if "sources" in record and (not isinstance(record["sources"], list) or not record["sources"] or
                not all(isinstance(source, str) and source.strip() for source in record["sources"])):
            raise ValidationError(f"catalog record {record['playlist_name']!r} has malformed sources")
        if "observedDate" in record:
            if not isinstance(record["observedDate"], str):
                raise ValidationError(f"catalog record {record['playlist_name']!r} has malformed observedDate")
            try:
                date.fromisoformat(record["observedDate"])
            except ValueError as error:
                raise ValidationError(f"catalog record {record['playlist_name']!r} has malformed observedDate") from error
        if "confidence" in record and record["confidence"] not in ("source-derived", "curated-override"):
            raise ValidationError(f"catalog record {record['playlist_name']!r} has malformed confidence")
        records.append(dict(record))
    return records


def build(existing: Any, community: Any, epic: Any, overrides: dict[str, dict[str, Any]],
          observed_date: str, include_new: bool = False) -> tuple[dict[str, Any], dict[str, Any]]:
    records = canonical_catalog(existing)
    sources, official = normalize_sources(community, epic)
    by_id = {record["playlist_name"].casefold(): record for record in records}
    report: dict[str, list[Any]] = {key: [] for key in ("added", "changed", "preserved", "unresolved", "conflicts")}
    official_coverage: dict[str, list[Any]] = {
        "matched": [], "historicalMissing": [], "officialOnly": [], "labelConflicts": []
    }

    for key, record in by_id.items():
        source = sources.get(key)
        official_record = official.get(key)
        if official_record:
            official_coverage["matched"].append(record["playlist_name"])
            official_label = official_record.get("display_name")
            if official_label and official_label != record["display_name"]:
                official_coverage["labelConflicts"].append({
                    "playlist_name": record["playlist_name"],
                    "official_display_name": official_label,
                    "preserved_display_name": record["display_name"],
                })
        else:
            official_coverage["historicalMissing"].append(record["playlist_name"])
        override = overrides.get(key)
        before = json.dumps(record, sort_keys=True, separators=(",", ":"))
        descriptor = infer_descriptor(source, observed_date) if source else None
        if descriptor:
            descriptor = with_stable_observation_date(record, descriptor, observed_date)
        if override:
            record.update({key: value for key, value in override.items() if key != "playlist_name"})
            record["_source"] = "curated-override"
        elif descriptor and label_agrees(record, descriptor):
            # Labels in the existing catalog are reviewed historical data.  Preserve them even
            # when upstream names differ, and make that disagreement visible to maintainers.
            record.update(descriptor)
            official_label = official_record.get("display_name") if official_record else None
            if source.get("name") and source["name"] != record["display_name"]:
                report["conflicts"].append({"playlist_name": record["playlist_name"], "community_name": source["name"],
                                            "official_display_name": official_label,
                                            "preserved_display_name": record["display_name"]})
        elif descriptor:
            if record.get("confidence") == "source-derived":
                for field in ("family", "buildMode", "teamSize", "rankedState", "variant",
                              "sources", "observedDate", "confidence"):
                    record.pop(field, None)
            report["conflicts"].append({"playlist_name": record["playlist_name"],
                                        "source_descriptor": descriptor,
                                        "official_display_name": official_record.get("display_name") if official_record else None,
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

    for key, record in official.items():
        if key not in by_id:
            official_coverage["officialOnly"].append({
                "playlist_name": record["playlist_name"],
                "official_display_name": record.get("display_name"),
            })

    canonical_catalog({"playlists": records})
    records.sort(key=lambda value: (value["playlist_name"].casefold(), value["playlist_name"]))
    for values in report.values():
        values.sort(key=lambda value: (value if isinstance(value, str) else value["playlist_name"]).casefold())
    for values in official_coverage.values():
        values.sort(key=lambda value: (value if isinstance(value, str) else value["playlist_name"]).casefold())
    catalog = {
        "_comment": "Generated by scripts/generate_playlist_mappings.py; historical labels are retained until reviewed.",
        "schemaVersion": 1,
        "_lastUpdated": existing.get("_lastUpdated", observed_date),
        "playlists": records,
    }
    return catalog, {"schemaVersion": 1, "observedDate": observed_date, **report,
                     "officialCoverage": official_coverage}


def json_bytes(value: Any) -> bytes:
    return (json.dumps(value, indent=2, ensure_ascii=False, sort_keys=False) + "\n").encode("utf-8")


def atomic_write(path: Path, payload: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(dir=path.parent, delete=False) as stream:
        stream.write(payload)
        temporary = Path(stream.name)
    os.replace(temporary, path)


def file_bytes(path: Path) -> bytes | None:
    return path.read_bytes() if path.exists() else None


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
    parser.add_argument("--refresh-report", action="store_true", help="rewrite the report even if the catalog is unchanged")
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
    catalog_changed = file_bytes(args.output) != catalog_payload
    if args.write:
        # The report is the review companion for a specific catalog change.  Leaving it intact
        # when the catalog is byte-identical prevents date-only weekly churn.
        if catalog_changed:
            atomic_write(args.output, catalog_payload)
            atomic_write(args.report, report_payload)
        elif args.refresh_report or not args.report.exists():
            atomic_write(args.report, report_payload)
    else:
        print(json.dumps({
            "catalogEntries": len(catalog["playlists"]),
            "catalogWouldChange": catalog_changed,
            "added": len(report["added"]),
            "changed": len(report["changed"]),
            "unresolved": len(report["unresolved"]),
            "conflicts": len(report["conflicts"]),
            "report": str(args.report),
            "candidateReport": report,
        }, indent=2))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except ValidationError as error:
        raise SystemExit(f"playlist generator: {error}")
