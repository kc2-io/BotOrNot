#!/usr/bin/env python3
"""
Applies upstream bug fixes to FortniteReplayDecompressor for Fortnite 41.00 replay support.

Fixes applied:
  1. Propagate NetworkReplayVersion to _cmdReader so NetFieldParser can detect build 41.00
  2. Break on bitReader.IsError in ReceivedPacket to prevent infinite loop (upstream issue #75)
  3. Use ShortComponents rotation quantization for build 41.00+ (upstream PR #77)
  4. Register the Playspace GameState path used by newer Reload replays
  5. Reconcile recorder ownership regardless of update ordering and channel reuse
  6. Apply non-null incremental player identity, bot, and level updates
"""
import sys
import os
import subprocess


PINNED_UPSTREAM_COMMIT = "2fc699e99cf8f6654f13fcc5272ea1de57d89fd7"


def patch_file(path, old, new, description):
    with open(path, 'r', encoding='utf-8-sig') as f:
        raw = f.read()

    # Normalize to LF for matching, then restore original endings on write
    has_crlf = '\r\n' in raw
    content = raw.replace('\r\n', '\n')
    old_lf = old.replace('\r\n', '\n')
    new_lf = new.replace('\r\n', '\n')

    if old_lf not in content:
        print(f'ERROR: Could not find patch target for: {description}')
        print(f'  Looking for:\n{old_lf!r}')
        sys.exit(1)

    patched = content.replace(old_lf, new_lf)

    if has_crlf:
        patched = patched.replace('\n', '\r\n')

    with open(path, 'w', encoding='utf-8', newline='') as f:
        f.write(patched)

    print(f'  OK: {description}')


def main():
    if len(sys.argv) != 2:
        print(f'Usage: {sys.argv[0]} <repo-root>')
        sys.exit(1)

    repo = sys.argv[1]
    try:
        actual_commit = subprocess.check_output(
            ["git", "-C", repo, "rev-parse", "HEAD"], text=True
        ).strip()
    except (OSError, subprocess.CalledProcessError) as exc:
        print(f"ERROR: Could not verify upstream checkout: {exc}")
        sys.exit(1)

    if actual_commit != PINNED_UPSTREAM_COMMIT:
        print("ERROR: Refusing to patch an unexpected upstream revision")
        print(f"  Expected: {PINNED_UPSTREAM_COMMIT}")
        print(f"  Actual:   {actual_commit}")
        sys.exit(1)

    replay_reader = os.path.join(repo, 'src', 'Unreal.Core', 'ReplayReader.cs')
    net_field_parser = os.path.join(repo, 'src', 'Unreal.Core', 'NetFieldParser.cs')
    game_state = os.path.join(repo, 'src', 'FortniteReplayReader', 'Models', 'NetFieldExports', 'GameState.cs')
    replay_builder = os.path.join(repo, 'src', 'FortniteReplayReader', 'FortniteReplayBuilder.cs')
    player_data = os.path.join(repo, 'src', 'FortniteReplayReader', 'Models', 'PlayerData.cs')

    print('Applying FortniteReplayDecompressor patches...')

    # Fix 1 — propagate NetworkReplayVersion to _cmdReader in ReadReplayHeader
    # Without this _cmdReader (and thus NetFieldParser) can't detect build 41.00
    patch_file(
        replay_reader,
        '        _cmdReader.ReplayHeaderFlags = header.Flags;\n    }',
        '        _cmdReader.ReplayHeaderFlags = header.Flags;\n'
        '        _cmdReader.NetworkReplayVersion = archive.NetworkReplayVersion;\n'
        '    }',
        'NetworkReplayVersion propagated to _cmdReader (enables 41.00 detection)'
    )

    # Fix 2 — break on IsError in ReceivedPacket to prevent infinite loop (upstream issue #75)
    # When bunchDataBits > remaining bits, SetTempEnd sets IsError=true without advancing
    # Position, causing all subsequent ReadBit calls to return false without advancing,
    # so AtEnd() never becomes true and the while loop spins forever.
    patch_file(
        replay_reader,
        '                bunch.Archive = bitReader;\n'
        '            }\n'
        '\n'
        '            bunchIndex++;',
        '                bunch.Archive = bitReader;\n'
        '            }\n'
        '\n'
        '            if (bitReader.IsError)\n'
        '            {\n'
        '                _logger?.LogWarning("ReceivedPacket: bunch data bits overflows remaining packet bits, aborting packet {}", packetIndex);\n'
        '                break;\n'
        '            }\n'
        '\n'
        '            bunchIndex++;',
        'Break on IsError to prevent infinite loop in ReceivedPacket (upstream issue #75)'
    )

    # Fix 3 — detect Fortnite 41.00 RepMovement rotation format change (upstream PR #77)
    # Build 41.00 widened pawn RepMovement rotation from ByteComponents to ShortComponents.
    # Detected via NetworkReplayVersion.Changelist >= 54618515 or Branch contains "+Release-41."
    patch_file(
        net_field_parser,
        '            RepLayoutCmdType.RepMovement => netFieldInfo.MovementAttribute != null ? netBitReader.SerializeRepMovement(\n'
        '                locationQuantizationLevel: netFieldInfo.MovementAttribute.LocationQuantizationLevel,\n'
        '                rotationQuantizationLevel: netFieldInfo.MovementAttribute.RotationQuantizationLevel,\n'
        '                velocityQuantizationLevel: netFieldInfo.MovementAttribute.VelocityQuantizationLevel) : netBitReader.SerializeRepMovement(),',
        '            RepLayoutCmdType.RepMovement => netFieldInfo.MovementAttribute != null ? netBitReader.SerializeRepMovement(\n'
        '                locationQuantizationLevel: netFieldInfo.MovementAttribute.LocationQuantizationLevel,\n'
        '                rotationQuantizationLevel: netFieldInfo.MovementAttribute.RotationQuantizationLevel,\n'
        '                velocityQuantizationLevel: netFieldInfo.MovementAttribute.VelocityQuantizationLevel)\n'
        '                // Fortnite 41.00 widened default-path RepMovement rotation Byte->Short\n'
        '                : netBitReader.SerializeRepMovement(\n'
        '                    rotationQuantizationLevel: (netBitReader.NetworkReplayVersion != null\n'
        '                        && (netBitReader.NetworkReplayVersion.Changelist >= 54618515u\n'
        '                            || (netBitReader.NetworkReplayVersion.Branch?.Contains("+Release-41.") ?? false)))\n'
        '                        ? RotatorQuantization.ShortComponents : RotatorQuantization.ByteComponents),',
        'RepMovement rotation ShortComponents detection for Fortnite 41.00 (upstream PR #77)'
    )

    # Fix 4 — register the GameState alias from PR #58. This is separate from the
    # recorder ordering defect, but it is a valid path alias for affected Reload files.
    patch_file(
        game_state,
        '    [NetFieldExport("RealMatchStartTime", RepLayoutCmdType.PropertyDouble)]\n'
        '    public double RealMatchStartTime { get; set; }\n'
        '\n'
        '}',
        '    [NetFieldExport("RealMatchStartTime", RepLayoutCmdType.PropertyDouble)]\n'
        '    public double RealMatchStartTime { get; set; }\n'
        '\n'
        '}\n'
        '\n'
        '[NetFieldExportClassNetCache("Playspace_GameState_C_ClassNetCache", minimalParseMode: ParseMode.Minimal)]\n'
        'public class PlayspaceGameStateCache : GameStateCache\n'
        '{\n'
        '}\n'
        '\n'
        '[NetFieldExportGroup("/Game/Athena/Playspace_GameState.Playspace_GameState_C", minimalParseMode: ParseMode.Minimal)]\n'
        'public class PlayspaceGameState : GameState\n'
        '{\n'
        '}',
        'Playspace GameState alias for newer Reload replays (PR #58)'
    )

    # Fix 5 — maintain a bidirectional actor/channel map, preserve player history on
    # channel reuse, and reconcile the recorder after every relevant ordering event.
    patch_file(
        replay_builder,
        '    private readonly HashSet<uint> _onlySpectatingPlayers = new();\n'
        '    private readonly Dictionary<uint, PlayerData> _players = new();',
        '    private readonly HashSet<uint> _onlySpectatingPlayers = new();\n'
        '    private readonly Dictionary<uint, PlayerData> _players = new();\n'
        '    private readonly List<PlayerData> _retiredPlayers = new();',
        'Retain completed players across channel reuse'
    )
    patch_file(
        replay_builder,
        '''    public void AddActorChannel(uint channelIndex, uint guid)
    {
        _actorToChannel[guid] = channelIndex;
        _channelToActor[channelIndex] = guid;
    }

    public void RemoveChannel(uint channelIndex)
    {
        _weapons.Remove(channelIndex);
        _unknownWeapons.Remove(channelIndex);
    }''',
        '''    public void AddActorChannel(uint channelIndex, uint guid)
    {
        // Channels and actor GUIDs may both be reused. Keep the two indexes in sync so
        // an old RecorderId mapping can never point at a different channel occupant.
        if (_channelToActor.TryGetValue(channelIndex, out var previousActor) && previousActor != guid &&
            _actorToChannel.TryGetValue(previousActor, out var previousActorChannel) &&
            previousActorChannel == channelIndex)
        {
            _actorToChannel.Remove(previousActor);
        }

        if (_actorToChannel.TryGetValue(guid, out var previousChannel) && previousChannel != channelIndex &&
            _channelToActor.TryGetValue(previousChannel, out var previousChannelActor) &&
            previousChannelActor == guid)
        {
            _channelToActor.Remove(previousChannel);
        }

        _actorToChannel[guid] = channelIndex;
        _channelToActor[channelIndex] = guid;
        ReconcileReplayOwner();
    }

    public void RemoveChannel(uint channelIndex)
    {
        _weapons.Remove(channelIndex);
        _unknownWeapons.Remove(channelIndex);

        // Preserve completed player history while allowing a later actor to reuse the
        // numeric channel without inheriting the departed player's model or owner flag.
        if (_players.Remove(channelIndex, out var playerData))
        {
            _retiredPlayers.Add(playerData);
        }
        _onlySpectatingPlayers.Remove(channelIndex);

        if (_channelToActor.Remove(channelIndex, out var actorId) &&
            _actorToChannel.TryGetValue(actorId, out var actorChannel) &&
            actorChannel == channelIndex)
        {
            _actorToChannel.Remove(actorId);
        }
    }''',
        'Recorder-safe actor/channel lifecycle'
    )
    patch_file(
        replay_builder,
        '    {\n'
        '        UpdateTeamData();\n'
        '        replay.GameData = GameData;',
        '    {\n'
        '        ReconcileReplayOwner();\n'
        '        UpdateTeamData();\n'
        '        replay.GameData = GameData;',
        'Final recorder reconciliation'
    )
    patch_file(
        replay_builder,
        '        replay.PlayerData = _players.Values;',
        '        replay.PlayerData = _retiredPlayers.Concat(_players.Values).ToArray();',
        'Include retained players in final replay'
    )
    patch_file(
        replay_builder,
        '        GameData.RecorderId ??= state.RecorderPlayerState?.Value;\n'
        '    }',
        '        GameData.RecorderId ??= state.RecorderPlayerState?.Value;\n'
        '        ReconcileReplayOwner();\n'
        '    }',
        'Reconcile owner when RecorderId arrives'
    )
    patch_file(
        replay_builder,
        '    public void UpdateTeamData()\n'
        '    {\n'
        '        foreach (var playerData in _players.Values)',
        '    public void UpdateTeamData()\n'
        '    {\n'
        '        foreach (var playerData in _retiredPlayers.Concat(_players.Values))',
        'Include retained players in team data'
    )
    patch_file(
        replay_builder,
        '''        if (isNewPlayer)
        {
            playerData = new PlayerData(state);

            if (_channelToActor.TryGetValue(channelIndex, out var actorId) && actorId == GameData.RecorderId)
            {
                playerData.IsReplayOwner = true;
            }

            _players[channelIndex] = playerData;
        }

        if (state.RebootCounter > 0''',
        '''        if (isNewPlayer)
        {
            playerData = new PlayerData(state);
            _players[channelIndex] = playerData;
        }
        else
        {
            playerData.Update(state);
        }

        ReconcileReplayOwner();

        if (state.RebootCounter > 0''',
        'Incremental player update and ordering-safe owner reconciliation'
    )
    patch_file(
        replay_builder,
        '    public void UpdateKillFeed(uint channelIndex, PlayerData data, FortPlayerState state)\n'
        '    {',
        '''    /// <summary>
    /// Resolves the authoritative recorder actor GUID through the current actor/channel map.
    /// If the mapping is unavailable, an already resolved owner remains stable; no weaker
    /// player ID, account ID, or elimination heuristic is used.
    /// </summary>
    private void ReconcileReplayOwner()
    {
        if (GameData.RecorderId is not uint recorderActorId ||
            !_actorToChannel.TryGetValue(recorderActorId, out var recorderChannel) ||
            !_channelToActor.TryGetValue(recorderChannel, out var mappedActorId) ||
            mappedActorId != recorderActorId ||
            !_players.TryGetValue(recorderChannel, out var recorderPlayer))
        {
            return;
        }

        foreach (var player in _retiredPlayers.Concat(_players.Values))
        {
            player.IsReplayOwner = ReferenceEquals(player, recorderPlayer);
        }
    }

    public void UpdateKillFeed(uint channelIndex, PlayerData data, FortPlayerState state)
    {''',
        'Authoritative recorder reconciliation helper'
    )

    # Fix 6 — constructor and later exports use the same null-aware merge policy.
    patch_file(
        player_data,
        '''    public PlayerData(FortPlayerState playerState)
    {
        Id = playerState.PlayerId is null ? playerState.PlayerID : (int?)playerState.PlayerId;
        EpicId = playerState.UniqueId ?? playerState.UniqueID;
        BotId = playerState.BotUniqueId;
        IsBot = playerState.bIsABot == true;
        PlayerNameCustomOverride = playerState.PlayerNameCustomOverride?.Text;
        IsGameSessionOwner = playerState.bIsGameSessionOwner;
        PlayerNumber = playerState.WorldPlayerId is not null ? (int?)playerState.WorldPlayerId : null;
        StreamerModeName = playerState.StreamerModeName?.Text;
        IsPartyLeader = playerState.PartyOwnerUniqueId == playerState.UniqueId || playerState.PartyOwnerUniqueId == playerState.UniqueID;
        TeamIndex = playerState.TeamIndex;
        Level = playerState.Level;
        SeasonLevelUIDisplay = playerState.SeasonLevelUIDisplay;
        PlatformUniqueNetId = playerState.PlatformUniqueNetId;
        Platform = playerState.Platform;
        HasFinishedLoading = playerState.bHasFinishedLoading;
        HasStartedPlaying = playerState.bHasStartedPlaying;
        IsUsingAnonymousMode = playerState.bUsingAnonymousMode;
        IsUsingStreamerMode = playerState.bUsingStreamerMode;

        Cosmetics = new Cosmetics()
        {
            CharacterBodyType = playerState.CharacterBodyType,
            HeroType = playerState.HeroType?.Name,
            CharacterGender = playerState.CharacterGender
        };
    }''',
        '''    public PlayerData(FortPlayerState playerState)
    {
        Cosmetics = new Cosmetics()
        {
        };

        Update(playerState);
    }

    /// <summary>
    /// Applies an incremental player-state export. Missing fields do not erase values from
    /// earlier exports, while explicit false and zero values remain meaningful updates.
    /// </summary>
    public void Update(FortPlayerState playerState)
    {
        if (playerState.PlayerId is not null)
            Id = (int?)playerState.PlayerId;
        else if (playerState.PlayerID is not null)
            Id = playerState.PlayerID;

        if (!string.IsNullOrEmpty(playerState.UniqueId))
            EpicId = playerState.UniqueId;
        else if (!string.IsNullOrEmpty(playerState.UniqueID))
            EpicId = playerState.UniqueID;

        if (!string.IsNullOrEmpty(playerState.BotUniqueId))
            BotId = playerState.BotUniqueId;
        if (playerState.bIsABot is not null)
            IsBot = playerState.bIsABot.Value;
        if (playerState.PlayerNameCustomOverride is not null)
            PlayerNameCustomOverride = playerState.PlayerNameCustomOverride.Text;
        if (playerState.bIsGameSessionOwner is not null)
            IsGameSessionOwner = playerState.bIsGameSessionOwner;
        if (playerState.WorldPlayerId is not null)
            PlayerNumber = playerState.WorldPlayerId;
        if (playerState.StreamerModeName is not null)
            StreamerModeName = playerState.StreamerModeName.Text;
        if (!string.IsNullOrEmpty(playerState.PartyOwnerUniqueId))
        {
            var effectivePlayerId = !string.IsNullOrEmpty(playerState.UniqueId)
                ? playerState.UniqueId
                : !string.IsNullOrEmpty(playerState.UniqueID)
                    ? playerState.UniqueID
                    : EpicId;
            IsPartyLeader = playerState.PartyOwnerUniqueId == effectivePlayerId;
        }
        if (playerState.TeamIndex is not null)
            TeamIndex = playerState.TeamIndex;
        if (playerState.Level is not null)
            Level = playerState.Level;
        if (playerState.SeasonLevelUIDisplay is not null)
            SeasonLevelUIDisplay = playerState.SeasonLevelUIDisplay;
        if (!string.IsNullOrEmpty(playerState.PlatformUniqueNetId))
            PlatformUniqueNetId = playerState.PlatformUniqueNetId;
        if (!string.IsNullOrEmpty(playerState.Platform))
            Platform = playerState.Platform;
        if (playerState.bHasFinishedLoading is not null)
            HasFinishedLoading = playerState.bHasFinishedLoading;
        if (playerState.bHasStartedPlaying is not null)
            HasStartedPlaying = playerState.bHasStartedPlaying;
        if (playerState.bUsingAnonymousMode is not null)
            IsUsingAnonymousMode = playerState.bUsingAnonymousMode;
        if (playerState.bUsingStreamerMode is not null)
            IsUsingStreamerMode = playerState.bUsingStreamerMode;

        if (playerState.CharacterBodyType is not null)
            Cosmetics.CharacterBodyType = playerState.CharacterBodyType;
        if (playerState.HeroType is not null)
            Cosmetics.HeroType = playerState.HeroType.Name;
        if (playerState.CharacterGender is not null)
            Cosmetics.CharacterGender = playerState.CharacterGender;
    }''',
        'Null-aware incremental PlayerData updates'
    )

    print('All patches applied successfully.')


if __name__ == '__main__':
    main()
