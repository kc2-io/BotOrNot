# Playlist catalog maintenance

BotOrNot.Core/Data/PlaylistMappings.json is embedded in the application. The application never
fetches playlists at runtime. Historical replay IDs remain in the catalog even when they are
absent from current source snapshots.

Validate saved, reproducible source inputs without changing files:

~~~powershell
python scripts/generate_playlist_mappings.py --community ..\issue-review-evidence\playlist-api.json --epic ..\issue-review-evidence\epic-content.json --observation-date 2026-09-11
~~~

The dry run prints whether the catalog would change and counts for additions, conflicts, and
unresolved entries. Review docs/playlist-catalog-report.json before writing.

~~~powershell
python scripts/generate_playlist_mappings.py --community path\to\playlist-api.json --epic path\to\epic-content.json --observation-date YYYY-MM-DD --include-new --write
~~~

--download obtains the two documented source snapshots for maintainer use. Invalid HTTP, empty,
malformed, or schema-invalid sources leave both outputs unchanged. Do not commit downloaded
snapshots. Add a reviewed entry to scripts/playlist-overrides.json when source evidence cannot
safely express a label, such as a verified rotating map; overrides take precedence. --include-new
is required before a source-derived ID absent from the historical catalog can enter a proposed
update.

The weekly workflow validates generated output itself and opens or updates one
automation/playlist-catalog pull request only when the catalog changes. It does not publish a
release. After reviewing and merging that PR, use the normal v* tag release process in
.github/workflows/release.yml.
