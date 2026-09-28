# Elimination Event Fields

Investigation of what data is available for each elimination and how BotOrNot associates it with recorded storm state using `FortniteReplayReader 3.0.5-botornot`.

## Event Type

`FortniteReplayReader.Models.Events.PlayerElimination`

## Fields

| Field | Type | Example | Description |
|---|---|---|---|
| `Time` | string | `"00:51"` | Formatted match time (MM:SS); intended for display rather than event ordering |
| `Knocked` | bool | `true` | Whether this is a knock (true) or a finish (false) |
| `GunType` | byte | `4`, `5` | Weapon category code |
| `IsSelfElimination` | bool | `false` | Self-inflicted (fall damage, storm, etc.) |
| `Distance` | float? | `1.875` | Distance between players |
| `IsValidLocation` | bool | `true` | Whether location data is populated |
| `Eliminated` | string | `5225584461D5...` | Eliminated player ID (legacy field) |
| `Eliminator` | string | `A4EBB9C31B1B...` | Eliminator player ID (legacy field) |
| `EliminatedInfo` | PlayerEliminationInfo | — | Detailed info about the eliminated player |
| `EliminatorInfo` | PlayerEliminationInfo | — | Detailed info about the eliminator |
| `Info` | EventInfo | — | Unreal engine event metadata |

## PlayerEliminationInfo Fields

Available on both `EliminatedInfo` and `EliminatorInfo`:

| Field | Type | Example | Description |
|---|---|---|---|
| `Id` | string | `5225584461D5...` | Player ID |
| `PlayerType` | PlayerTypes | `PLAYER` | Player type enum |
| `IsBot` | bool | `false` | Whether this player is a bot |
| `Location` | FVector | `X: 0, Y: 0, Z: 0` | Position on map (X, Y, Z) |
| `Rotation` | FQuat | — | Player rotation quaternion |
| `Scale` | FVector | — | Scale vector |

## Storm Circle

There is no storm phase field on an elimination event itself. BotOrNot resolves the phase from the replay's replicated `SafeZoneIndicator` observations instead of estimating it from a mode schedule.

Each safe-zone observation records:

- `CurrentPhase`, the nullable integer phase replicated by Fortnite
- `PhaseCount`, the nullable total configured phase count
- `ReplayTimeSeconds`, the replay reader's frame clock
- `ChannelIndex` and `ActorGuid`, which identify the replicated safe-zone source

An elimination's event clock is `Info.StartTime / 1000`. The resolver selects the latest valid `CurrentPhase` observation at or before that event time. An update exactly on the event boundary applies to that event. `CurrentPhase` is the recorded absolute storm phase; it is not the number of observed shrink updates and it is not the observation's list index.

Before the first replicated phase observation, the result is `Unknown`. A recorded `CurrentPhase` of `0` explicitly means `Before phase 1`. Missing phase exports remain nullable and do not masquerade as phase zero. An invalid latest phase or conflicting same-frame phases produce `Unknown` until a later valid observation. If the replay contains observations from multiple identified safe-zone actors, the resolver treats the whole association as ambiguous and returns `Unknown`.

The formatted elimination `Time` and the safe-zone world-clock fields (`StartShrinkTime` and `FinishShrinkTime`) use different clock domains and must not be compared for this association.

## Currently Used by BotOrNot

- `Time` — display fallback when the precise event clock is unavailable; never a knock-credit timeout
- `Info.StartTime` — precise event clock for display, storm association, event correlation, and observed knock/finish ordering
- `Knocked` — distinguish knocks from finishes
- `EliminatedInfo.Id` / `EliminatorInfo.Id` — player identification
- `Eliminated` / `Eliminator` — legacy fallback IDs
- `GunType` — raw event code used by the structured death-cause resolver

Owner credit follows observed knocks, explicit DBNO recovery, and reboot-counter increases. No elapsed-time threshold infers a recovery. See [death-cause coverage](death-cause-coverage.md) for evidence and limitations.

## Not Yet Used

- `IsSelfElimination` — could flag self-elims differently
- `Distance` — engagement distance
- `Location` — map position (could enable heatmaps)
- `EliminatedInfo.IsBot` / `EliminatorInfo.IsBot` — alternative bot detection source
