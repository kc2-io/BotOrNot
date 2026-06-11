#!/usr/bin/env python3
"""
Applies upstream bug fixes to FortniteReplayDecompressor for Fortnite 41.00 replay support.

Fixes applied:
  1. Propagate NetworkReplayVersion to _cmdReader so NetFieldParser can detect build 41.00
  2. Break on bitReader.IsError in ReceivedPacket to prevent infinite loop (upstream issue #75)
  3. Use ShortComponents rotation quantization for build 41.00+ (upstream PR #77)
"""
import sys
import os


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
    replay_reader = os.path.join(repo, 'src', 'Unreal.Core', 'ReplayReader.cs')
    net_field_parser = os.path.join(repo, 'src', 'Unreal.Core', 'NetFieldParser.cs')

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

    print('All patches applied successfully.')


if __name__ == '__main__':
    main()
