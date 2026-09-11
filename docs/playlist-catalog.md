# Playlist catalog maintenance

\`BotOrNot.Core/Data/PlaylistMappings.json\` is embedded in the application. The application
does not fetch playlists over the network. The catalog retains historical replay IDs even when
they no longer appear in current source snapshots.

Run the generator against the recorded review inputs without changing files:

\`\`\`powershell
python scripts/generate_playlist_mappings.py \`
  --community ..\\issue-review-evidence\\playlist-api.json \`
  --epic ..\\issue-review-evidence\\epic-content.json \`
  --observation-date 2026-09-11
\`\`\`

After reviewing the generated report, write it atomically:

\`\`\`powershell
python scripts/generate_playlist_mappings.py \`
  --community path\\to\\playlist-api.json \`
  --epic path\\to\\epic-content.json \`
  --observation-date YYYY-MM-DD \`
  --write
\`\`\`

\`--download --write --observation-date YYYY-MM-DD\` downloads the two documented sources for
maintainer use. It rejects non-success responses, malformed or empty data before replacing
either output. Do not commit downloaded snapshots. Add a reviewed entry to
\`scripts/playlist-overrides.json\` only when source inference cannot safely express the label,
for example a verified rotating map. Overrides take precedence over source data.
\`--include-new\` is deliberately required before source-derived IDs absent from the historical
catalog can enter a proposed update.

The weekly GitHub workflow runs the same generator, tests its deterministic rules, and opens or
updates one \`automation/playlist-catalog\` pull request only when the catalog changes. It does
not publish a release. After a maintainer reviews and merges that PR, use the normal \`v*\` tag
release process in \`.github/workflows/release.yml\`.
