import copy
import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import generate_playlist_mappings as generator


def source(identifier, game_type="EFortGameType::BR", team=1, tags=None, rating="fun", name="Mode"):
    return {
        "id": identifier, "name": name, "gameType": game_type, "maxTeamSize": team,
        "ratingType": rating, "gameplayTags": tags or [], "path": "FortniteGame/Plugins/GameFeatures/BRPlaylists"
    }


class PlaylistGeneratorTests(unittest.TestCase):
    def setUp(self):
        self.catalog = {"_lastUpdated": "2026-02-03", "playlists": [
            {"playlist_name": "Playlist_Historical", "display_name": "Reload Build - Duos", "_source": "manual"},
            {"playlist_name": "Playlist_Conflict", "display_name": "Reviewed label"}
        ]}
        self.community = {"status": 200, "data": [
            source("Playlist_BR", team=1),
            source("Playlist_ZB", "EFortGameType::ZeroBuild", 2, ["Athena.Playlist.NoBuildingMaterials"]),
            source("Playlist_Reload", "EFortGameType::BlastBerry", 3),
            source("Playlist_Blitz", team=4, tags=["Athena.Playlist.Blitz"]),
            source("Playlist_Six", team=6),
            source("Playlist_Ranked", team=2, rating="ranked-br-combined"),
            source("Playlist_VK_Play", "EFortGameType::VKPlay", 16, ["Playlist.UGC.Play"]),
            source("Playlist_Conflict", team=1, name="Changed upstream name")
        ]}
        self.epic = {"playlistinformation": {"_type": "FortPlaylistInfo"}}

    def test_source_descriptors_and_unknowns(self):
        catalog, report = generator.build(self.catalog, self.community, self.epic, {}, "2026-09-11", include_new=True)
        values = {item["playlist_name"]: item for item in catalog["playlists"]}
        self.assertEqual(values["Playlist_BR"]["display_name"], "BR Build Solo")
        self.assertEqual(values["Playlist_ZB"]["display_name"], "BR Zero Build Duos")
        self.assertEqual(values["Playlist_Reload"]["family"], "Reload")
        self.assertEqual(values["Playlist_Blitz"]["teamSize"], 4)
        self.assertEqual(values["Playlist_Six"]["teamSize"], 6)
        self.assertEqual(values["Playlist_Ranked"]["rankedState"], "ranked")
        self.assertEqual(values["Playlist_Ranked"]["display_name"], "BR Build Duos Ranked")
        self.assertNotIn("Playlist_VK_Play", values)
        self.assertIn({"playlist_name": "Playlist_VK_Play", "reason": "unsupported-or-insufficient-evidence"},
                      report["unresolved"])
        self.assertIn("Playlist_Historical", report["preserved"])
        self.assertEqual(values["Playlist_Conflict"]["display_name"], "Reviewed label")
        self.assertEqual(report["conflicts"][0]["playlist_name"], "Playlist_Conflict")

    def test_override_wins_and_output_is_deterministic(self):
        overrides = {"playlist_br": {
            "playlist_name": "playlist_br", "display_name": "Curated BR", "variant": "Reviewed map"
        }}
        first = generator.build(copy.deepcopy(self.catalog), self.community, self.epic, overrides, "2026-09-11", include_new=True)
        second = generator.build(copy.deepcopy(self.catalog), self.community, self.epic, overrides, "2026-09-11", include_new=True)
        self.assertEqual(generator.json_bytes(first[0]), generator.json_bytes(second[0]))
        values = {item["playlist_name"].casefold(): item for item in first[0]["playlists"]}
        self.assertEqual(values["playlist_br"]["display_name"], "Curated BR")
        self.assertEqual(values["playlist_br"]["variant"], "Reviewed map")

    def test_partial_sources_do_not_remove_historical_records(self):
        partial = {"status": 200, "data": [source("Playlist_BR")]}
        catalog, _ = generator.build(self.catalog, partial, self.epic, {}, "2026-09-11")
        self.assertIn("Playlist_Historical", [item["playlist_name"] for item in catalog["playlists"]])

    def test_validation_rejects_malformed_empty_and_duplicates(self):
        with self.assertRaises(generator.ValidationError):
            generator.normalize_sources({"status": 500, "data": []}, self.epic)
        with self.assertRaises(generator.ValidationError):
            generator.normalize_sources({"status": 200, "data": []}, self.epic)
        duplicate = {"status": 200, "data": [source("Playlist_X"), source("playlist_x")]}
        with self.assertRaises(generator.ValidationError):
            generator.normalize_sources(duplicate, self.epic)
        with tempfile.TemporaryDirectory() as directory:
            broken = Path(directory) / "broken.json"
            broken.write_text("{", encoding="utf-8")
            with self.assertRaises(generator.ValidationError):
                generator.load_json(broken)


if __name__ == "__main__":
    unittest.main()
