# CLAUDE.md

## Project Overview

BotOrNot is a cross-platform desktop application that analyzes Fortnite replay files (`.replay`) to identify which players in a match are bots versus real players. It parses replay data to show lobby breakdowns, bot detection results, elimination tracking, platform distribution, and match metadata.

## Tech Stack

- **Language**: C# / .NET 10.0 (nullable enabled, implicit usings)
- **UI Framework**: Avalonia UI 11.2.2 (cross-platform desktop)
- **MVVM**: ReactiveUI
- **Replay Parsing**: FortniteReplayReader 3.0.2-botornot (patched local build; upstream v3.0.1 + issue #75 infinite-loop fix + PR #77 Fortnite 41.00 ShortComponents rotation fix)
- **Testing**: NUnit 4.2.2 with coverlet
- **CI/CD**: GitHub Actions (build on push/PR, release on tag push)

## Solution Structure

```
BotOrNot.sln
├── BotOrNot.Core/           # Core library - replay parsing, models, services
│   ├── Models/
│   │   ├── PlayerRow.cs         # Player data model (Id, Name, Bot, Platform, Kills, etc.)
│   │   └── ReplayData.cs        # Top-level replay result + ReplayMetadata
│   ├── Services/
│   │   ├── ReplayService.cs     # Main service: parses .replay files into ReplayData
│   │   ├── DeathCauseHelper.cs  # Maps numeric death cause codes to display names
│   │   ├── PlatformHelper.cs    # Maps platform codes (WIN, PS5, XBL) to friendly names
│   │   ├── PlaylistHelper.cs    # Maps playlist IDs to display names from embedded JSON
│   │   └── ReflectionUtils.cs   # Reflection helpers for reading FortniteReplayReader objects
│   └── Data/
│       └── PlaylistMappings.json  # Embedded resource: playlist name → display name
├── BotOrNot.Avalonia/       # Desktop UI application
│   ├── Views/
│   │   └── MainWindow.axaml(.cs)  # Main window with DataGrids and file picker
│   ├── ViewModels/
│   │   └── MainWindowViewModel.cs # Loads replay, computes stats, binds to UI
│   ├── Converters/
│   │   └── BotColorConverter.cs   # Bot status → colored dot (green/red/gray)
│   ├── App.axaml(.cs)            # Application entry
│   └── Program.cs               # Main entry point
├── BotOrNot.Tests/          # Unit tests
│   ├── ReplayServiceTests.cs    # Tests against real .replay files in TestData/
│   └── TestData/                # Sample replay files for testing
└── DebugReplay/             # Debug console tool for inspecting replay data
```

## Linux / Agent Dev Setup

The CI builds on Windows (PowerShell). On Linux (e.g. inside a Docker container), do
this once after cloning to build the patched NuGet packages locally:

```bash
# 1. Install .NET 10 SDK (no root required)
curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
bash /tmp/dotnet-install.sh --channel 10.0
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1   # needed — libicu not available in container

# 2. Build the patched FortniteReplayReader packages into local-packages/
bash scripts/build-local-packages.sh

# 3. Run tests
dotnet test BotOrNot.Tests/BotOrNot.Tests.csproj
```

Add the three `export` lines to `~/.bashrc` for persistence. The `build-local-packages.sh`
script only needs to run once (or when `patches/apply-patches.py` changes).

## Build & Run

```bash
# Build
dotnet build

# Run the desktop app
dotnet run --project BotOrNot.Avalonia

# Run tests
dotnet test

# Publish self-contained Windows x64 build
dotnet publish BotOrNot.Avalonia/BotOrNot.Avalonia.csproj \
  --configuration Release --runtime win-x64 --self-contained true \
  -p:PublishSingleFile=true
```

## Key Architecture Decisions

### Reflection-based replay reading
`ReflectionUtils` provides a cached reflection layer over the FortniteReplayReader library's `PlayerData` objects. This decouples the app from the library's internal property names and handles missing/null properties gracefully. Properties are looked up by multiple candidate names (e.g., `PlayerId`, `UniqueId`, `NetId`).

### Elimination credit logic
The `ReplayService` implements knock/finish tracking with a 60-second window. The owner gets credit for an elimination if:
1. They directly finish a player (no active knock exists), OR
2. They knocked the player and the finish happens within 60 seconds (regardless of who finishes)

Stale knocks (>60s) are treated as revives and don't grant credit.

### NPC filtering
NPCs (wildlife, bosses) are identified by having their Player ID equal to their Player Name. They are excluded from player counts and bot/human breakdowns but still appear in the players list.

### Winner detection
Winners are determined from `GameData.WinningTeam` and `GameData.WinningPlayerIds`. All players on the winning team get `Placement = "1"` and their death cause is set to "N/A Won Match".

## Testing

Tests run against real `.replay` files in `BotOrNot.Tests/TestData/`. Test file names encode expected values (e.g., `Owner_Elim_5_Team_Elim_1_Place_1`). Key test scenarios:
- Owner elimination count accuracy
- Knocks excluded from elimination list
- Death cause resolution
- Winning team extraction and placement
- Team kills extraction
- Playlist name mapping

## Release Process

Push a git tag matching `v*` to trigger the release workflow, which builds a self-contained Windows x64 zip and creates a GitHub release with auto-generated release notes.

## Common Development Tasks

- **Add a new death cause**: Add entry to `DeathCauseHelper.DeathCauses` dictionary
- **Add a new platform**: Add entry to `PlatformHelper.PlatformNames` dictionary
- **Add a new playlist mapping**: Update `PlaylistMappings.json` in `BotOrNot.Core/Data/`
- **Add a weapon tag fallback**: Add to `DeathCauseHelper.WeaponTagMappings`
- **Add a new column to the UI**: Add `DataGridColumn` in `MainWindow.axaml` for both grids, add property to `PlayerRow` if needed, extract from `PlayerData` in `ReplayService`
