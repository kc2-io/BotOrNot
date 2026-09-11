# Playlist mapping evidence

Issue #53 adds six raw playlist IDs to the embedded display-name table. The mappings use only dimensions supported by the captured source fields; no map name or public/unranked claim is inferred.

Sources:

- Community snapshot: [Fortnite-API playlist endpoint](https://fortnite-api.com/v1/playlists), captured in `work/issue-review-evidence/playlist-api.json`.
- Official content snapshot: [Epic Fortnite content endpoint](https://fortnitecontent-website-prod07.ol.epicgames.com/content/api/pages/fortnite-game/), captured in `work/epic-content.json`; the playlist collection is `playlistinformation.playlist_info.playlists`.

| ID | Supported fields | Display name |
| --- | --- | --- |
| `Playlist_ForbiddenFruitOldNoBuildBRSolo` | `gameType=ForbiddenFruit`; `ratingType=nobuild`; `maxTeamSize=1`; ForbiddenFruit/NoBuild/Solo tags | Blitz Zero Build - Solo |
| `Playlist_MatchMistSolo` | `gameType=BlastBerry`; `ratingType=blastberry_build`; `maxTeamSize=1`; BlastBerry/Build/MatchMist/Solo tags | Reload Build - Solo |
| `Playlist_MatchMistDuo` | `gameType=BlastBerry`; `ratingType=blastberry_build`; `maxTeamSize=2`; BlastBerry/Build/MatchMist/Duo tags | Reload Build - Duos |
| `Playlist_MatchMistSquad` | `gameType=BlastBerry`; `ratingType=blastberry_build`; `maxTeamSize=4`; BlastBerry/Build/MatchMist/Squad tags | Reload Build - Squads |
| `Playlist_NoBuildBR_Habanero_Solo` | `gameType=ZeroBuild`; `ratingType=ranked-br-combined`; `maxTeamSize=1`; ZeroBuild/Habanero/NoBuildBR.Comp/Solo tags | Ranked Zero Build - Solo |
| `Playlist_Trios` | `gameType=BR`; `ratingType=fun`; `maxTeamSize=3`; Default/Trios tags | BR Build - Trios |

The `path`, `name`, and `description` fields corroborate the family/team interpretation. The source does not establish a human-facing map for MatchMist, and non-ranked entries do not prove a “Pubs” classification, so those dimensions are intentionally omitted.
