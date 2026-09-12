# Library summary profile

Library scans call `ReplayService.LoadSummaryAsync`; opening a match calls
`LoadReplayAsync`. Both use `ParseMode.Normal`, the same physical decoder bound,
and the same identity, event correlation, elimination credit, and summary
projection code. The summary reader alone excludes proven detail-only groups.
The default reader remains the reference for differential tests.

## Dependency contract

| Library result | Evidence that must remain available |
| --- | --- |
| File identity, order, duration | File name/path/write time and replay metadata |
| Playlist and current display name | Game-state playlist and custom `CurrentPlaylistInfo` payload, raw playlist ID |
| Owner identity/name | `RecorderPlayerState`, actor/channel mappings, channel lifecycle, incremental player state, external/private name records |
| Player/bot counts and NPC exclusion | Player creation/retirement, numeric and stable identities, bot flags, names including missing-name fallback |
| Owner placement | Player `Place`, team state, `WinningTeam` fallback |
| Authoritative kills | Nullable `KillScore`; missing is distinct from zero |
| Derived bot/player kills and analysis status | Raw elimination events and replicated victim/actor evidence, correlation uniqueness, DBNO presence/value, reboot observations, shared credit resolution |
| Opponent tuples/completeness | Stable identities and names, team membership, owner resolution, bot/NPC exclusion, retired participants |
| Displayed computed values | `PlayerKills`, `AnalysisStatusText`, `IsWin`, `BotPercent`, and aggregate/opponent projections over the complete selected collection |

Identity fields include `PlayerId`/`PlayerID`, `UniqueId`/`UniqueID`,
`PlatformUniqueNetId`, `BotUniqueId`, `bIsABot`, `bOnlySpectator`, and `TeamIndex`.
Keep incremental false/zero updates and missing-field behavior intact.

Replicated elimination evidence includes `FinisherOrDowner`, `bDBNO`,
`RebootCounter`, `DeathCause`, `DeathLocation`, and death tags, plus the replay
frame clock. Cause and location are **observation triggers** in the builder:
dropping them merely because they are absent from final summary JSON can change
correlation ambiguity and recovery inference. They are retained.

## Initial exclusion set

Only these exact group paths are excluded initially:

- `/Game/Athena/PlayerPawn_Athena.PlayerPawn_Athena_C`: movement and cosmetics.
- `/Script/FortniteGame.FortPickupAthena`: item details.
- `/Game/Athena/SafeZone/SafeZoneIndicator.SafeZoneIndicator_C`: storm details.

Actor/channel framing, schema discovery, external names, event chunks, game state,
and player state remain enabled. Unknown groups remain enabled. This is not a
playlist filter and does not reduce the selected replay count. Full match loads
retain storm circles and other detail projections.

Exclusions apply only to the validated release families 39.30, 39.40, 41.00,
41.10, 42.00, and 42.10. For other versions the policy allows every group from
the start of the read, providing the full Normal decode without a partially
optimized result or a second parse. Add a release only after fixture/corpus parity
validation; seasonal version numbers alone do not establish compatibility.

## Validation and timing

`ReplaySummaryProfileTests` compares every serialized summary property against the
full reader on committed BR/Reload fixtures. `tools/ReplayBenchmark` freezes and
checks private file hashes, creates Normal golden summaries, and emits per-file
deltas. Keep private replay files and generated identity/name reports out of Git.
Existing identity, lifecycle, ambiguity, recovery, and storm tests remain required.

Service timing is diagnostic. Five-second acceptance requires the complete
selected library, final aggregates and rendered viewport, with an empty application
cache; report process startup separately. Warm OS file cache is distinct from a
warm application cache. A faster best run alone is not acceptance evidence.
