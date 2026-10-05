# Bot or Not?

A cross-platform desktop application that analyzes Fortnite replay files to identify which players in your matches are bots versus real players.

## Features

- **Replay Analysis**: Load and parse Fortnite `.replay` files
- **Lobby Breakdown**: See every player in your lobby - their username, level, kill count, and cause of death
- **Bot Detection**: Identifies bot players based on replay data
- **Your Eliminations**: See a breakdown of who you eliminated - how many were bots vs real players
- **Match Metadata**: View game mode, matchmaking region, match duration, and player counts
- **Platform Breakdown**: See distribution of players across PC, PlayStation, Xbox, Switch, and mobile
- **Streamer Overlay**: Show library totals and recent matches in a local browser source with automatic refresh. See the [setup guide](docs/streamer-overlay.md).

## Screenshot

![Bot or Not? screenshot](docs/screenshot.png)

Video walkthrough by [Agminate](https://youtu.be/rwa8cMH_lLM)

## Installation

### Windows

Download the latest release from the [Releases](https://github.com/ipl31/BotOrNot/releases) page:
1. Download `BotOrNot-Windows-x64.zip`
2. Extract the ZIP file
3. Run `BotOrNot.exe`

### Building from Source

Requirements:
- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)

```bash
git clone https://github.com/ipl31/BotOrNot.git
cd BotOrNot
dotnet build
dotnet run --project BotOrNot.Avalonia
```

## Usage

1. Launch the application
2. Click "Select Replay File"
3. Navigate to your Fortnite replays folder:
   - Windows: `%LOCALAPPDATA%\FortniteGame\Saved\Demos`
   - Mac: `~/Library/Application Support/Epic/FortniteGame/Saved/Demos`
4. Select a `.replay` file
5. View the analysis results

## How It Works

The application uses the [FortniteReplayReader](https://github.com/Shiqan/FortniteReplayDecompressor) library to parse Fortnite replay files. It extracts player data including:

- Player IDs and display names
- Bot status flags
- Platform information
- Elimination events
- Cosmetic loadouts (pickaxe, glider)

## Disclaimer

Data presented is not guaranteed to be accurate. Bot detection relies on information stored in replay files, which may not always be complete or correct.

## Tech Stack

- [.NET 9.0](https://dotnet.microsoft.com/)
- [Avalonia UI](https://avaloniaui.net/) - Cross-platform UI framework
- [ReactiveUI](https://www.reactiveui.net/) - MVVM framework
- [FortniteReplayReader](https://github.com/Shiqan/FortniteReplayDecompressor) - Replay parsing library

## License

MIT
