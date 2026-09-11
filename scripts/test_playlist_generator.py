import copy
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import generate_playlist_mappings as generator


def source(identifier, game_type="EFortGameType::BR", team=1, tags=None, rating="build", name="Mode"):
    return {
        "id": identifier, "name": name, "gameType": game_type, "maxTeamSize": team,
        "ratingType": rating, "gameplayTags": tags or [], "path": "FortniteGame/Plugins/GameFeatures/BRPlaylists"
    }


def epic(*entries):
    return {"playlistinformation": {"playlist_info": {"playlists": list(entries)}}}


def official(identifier, display_name):
    return {"playlist_name": identifier, "display_name": display_name}


class PlaylistGeneratorTests(unittest.TestCase):
    def setUp(self):
        self.catalog = {"_lastUpdated": "2026-02-03", "playlists": [
            {"playlist_name": "Playlist_Historical", "display_name": "Reload Build - Duos", "_source": "manual"},
            {"playlist_name": "Playlist_Conflict", "display_name": "Reviewed label"}
        ]}
        self.community = {"status": 200, "data": [
            source("Playlist_BR", team=1),
            source("Playlist_ZB", "EFortGameType::ZeroBuild", 2,
                   ["Athena.Playlist.NoBuildingMaterials"], rating="fun"),
            source("Playlist_Reload", "EFortGameType::BlastBerry", 3, rating="blastberry_build"),
            source("Playlist_Blitz", team=4, tags=["Athena.Playlist.Blitz"]),
            source("Playlist_Six", team=6),
            source("Playlist_Ranked", team=2, rating="ranked-br-combined_build"),
            source("Playlist_UnknownBuild", team=1, rating="fun"),
            source("Playlist_VK_Play", "EFortGameType::VKPlay", 16, ["Playlist.UGC.Play"]),
            source("Playlist_Conflict", team=1, name="Changed upstream name")
        ]}
        self.epic = epic(
            official("Playlist_BR", "Battle Royale"),
            official("Playlist_Conflict", "Official changed name"),
        )

    def test_supported_descriptors_require_evidence_and_preserve_history(self):
        catalog, report = generator.build(self.catalog, self.community, self.epic, {}, "2026-09-11", include_new=True)
        values = {item["playlist_name"]: item for item in catalog["playlists"]}
        self.assertEqual(values["Playlist_BR"]["display_name"], "BR Build Solo")
        self.assertEqual(values["Playlist_ZB"]["display_name"], "BR Zero Build Duos")
        self.assertEqual(values["Playlist_Reload"]["family"], "Reload")
        self.assertEqual(values["Playlist_Blitz"]["teamSize"], 4)
        self.assertEqual(values["Playlist_Six"]["teamSize"], 6)
        self.assertEqual(values["Playlist_Ranked"]["rankedState"], "ranked")
        self.assertNotIn("Playlist_VK_Play", values)
        self.assertNotIn("Playlist_UnknownBuild", values)
        self.assertIn("Playlist_Historical", report["preserved"])
        conflict = next(item for item in report["conflicts"] if item["playlist_name"] == "Playlist_Conflict")
        self.assertEqual(conflict["official_display_name"], "Official changed name")

    def test_override_precedence_and_casefolded_ids(self):
        overrides = {"playlist_br": {
            "playlist_name": "playlist_br", "display_name": "Curated BR", "variant": "Reviewed map"
        }}
        catalog, _ = generator.build(copy.deepcopy(self.catalog), self.community, self.epic, overrides,
                                     "2026-09-11", include_new=True)
        values = {item["playlist_name"].casefold(): item for item in catalog["playlists"]}
        self.assertEqual(values["playlist_br"]["display_name"], "Curated BR")
        self.assertEqual(values["playlist_br"]["variant"], "Reviewed map")

    def test_observation_date_and_output_stay_stable_on_later_date(self):
        first, _ = generator.build(self.catalog, self.community, self.epic, {}, "2026-09-11", include_new=True)
        second, _ = generator.build(first, self.community, self.epic, {}, "2026-09-18", include_new=True)
        self.assertEqual(generator.json_bytes(first), generator.json_bytes(second))

    def test_partial_source_never_removes_historical_records(self):
        partial = {"status": 200, "data": [source("Playlist_BR")]}
        catalog, _ = generator.build(self.catalog, partial, self.epic, {}, "2026-09-11")
        self.assertIn("Playlist_Historical", [item["playlist_name"] for item in catalog["playlists"]])

    def test_rejects_malformed_sources_and_override_types(self):
        invalid_epics = [{}, {"error": "upstream failure"}, epic(), epic(official("Playlist_X", "x"), official("playlist_x", "x"))]
        for value in invalid_epics:
            with self.assertRaises(generator.ValidationError):
                generator.normalize_sources(self.community, value)
        malformed_tags = {"status": 200, "data": [source("Playlist_X", tags="not-an-array")]}
        with self.assertRaises(generator.ValidationError):
            generator.normalize_sources(malformed_tags, self.epic)
        malformed_team = {"status": 200, "data": [source("Playlist_X", team=True)]}
        descriptor = generator.infer_descriptor(malformed_team["data"][0], "2026-09-11")
        self.assertIsNone(descriptor["teamSize"] if descriptor else None)
        with self.assertRaises(generator.ValidationError):
            generator.load_overrides_from_value({"overrides": [{"playlist_name": "Playlist_X", "teamSize": True}]})

    def test_failed_cli_validation_does_not_modify_existing_outputs(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            catalog = root / "catalog.json"
            report = root / "report.json"
            community = root / "community.json"
            bad_epic = root / "epic.json"
            overrides = root / "overrides.json"
            catalog.write_text(json.dumps(self.catalog), encoding="utf-8")
            report.write_text('{"sentinel":true}\n', encoding="utf-8")
            community.write_text(json.dumps(self.community), encoding="utf-8")
            bad_epic.write_text('{"error":"nope"}', encoding="utf-8")
            overrides.write_text('{"overrides":[]}', encoding="utf-8")
            original_catalog = catalog.read_text(encoding="utf-8")
            result = subprocess.run([
                sys.executable, str(Path(__file__).parent / "generate_playlist_mappings.py"),
                "--catalog", str(catalog), "--community", str(community), "--epic", str(bad_epic),
                "--overrides", str(overrides), "--output", str(catalog), "--report", str(report), "--write"
            ], capture_output=True, text=True)
            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(catalog.read_text(encoding="utf-8"), original_catalog)
            self.assertEqual(report.read_text(encoding="utf-8"), '{"sentinel":true}\n')

    def test_duplicate_catalog_is_rejected(self):
        catalog = {"playlists": [
            {"playlist_name": "Playlist_X", "display_name": "One"},
            {"playlist_name": "playlist_x", "display_name": "Two"},
        ]}
        with self.assertRaises(generator.ValidationError):
            generator.canonical_catalog(catalog)


if __name__ == "__main__":
    unittest.main()
