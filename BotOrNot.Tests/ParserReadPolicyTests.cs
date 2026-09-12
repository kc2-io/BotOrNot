using FortniteReplayReader.Models.NetFieldExports;
using NUnit.Framework;
using Unreal.Core;
using Unreal.Core.Contracts;
using Unreal.Core.Models;
using Unreal.Core.Models.Enums;

namespace BotOrNot.Tests;

public sealed class ParserReadPolicyTests
{
    private const string PlayerStatePath = "/Script/FortniteGame.FortPlayerStateAthena";

    [Test]
    public void RejectedGroupConsumesNetDeltaFieldsWithoutIgnoringChannel()
    {
        var reader = new FilteringReplayReader(readGroup: false, rejectedField: null);
        var group = CreateGroup();
        var archive = CreatePropertiesArchive(1, 2);

        var result = reader.ReceiveProperties(
            archive, group, 1, out var exportGroup,
            enablePropertyChecksum: false, netDeltaUpdate: true);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.True);
            Assert.That(archive.AtEnd(), Is.True);
            Assert.That(archive.IsError, Is.False);
            Assert.That(exportGroup, Is.Null);
            Assert.That(reader.LastExport, Is.Null);
            Assert.That(reader.IsIgnoringGroup(1, group.PathName), Is.False);
        });
    }

    [Test]
    public void RejectedFieldIsSkippedBeforeFollowingFieldIsDecoded()
    {
        var reader = new FilteringReplayReader(readGroup: true, rejectedField: "bIsABot");
        var group = CreateThreeFieldGroup();
        var archive = CreatePropertiesArchive(1, 2, 3);

        var result = reader.ReceiveProperties(
            archive, group, 1, out var exportGroup, enablePropertyChecksum: false);

        var parsed = exportGroup as FortPlayerState;
        Assert.Multiple(() =>
        {
            Assert.That(result, Is.True);
            Assert.That(archive.AtEnd(), Is.True);
            Assert.That(archive.IsError, Is.False);
            Assert.That(parsed, Is.Not.Null);
            Assert.That(parsed!.bDBNO, Is.True);
            Assert.That(parsed.bIsABot, Is.Null);
            Assert.That(parsed.bOnlySpectator, Is.True);
            Assert.That(reader.LastExport, Is.SameAs(parsed));
            Assert.That(group.NetFieldExports[1]!.Incompatible, Is.False);
        });
    }

    [Test]
    public void FullySkippedGroupDoesNotBlockRequiredGroupOnSameChannel()
    {
        const string pawnPath = "/Game/Athena/PlayerPawn_Athena.PlayerPawn_Athena_C";
        var reader = new FilteringReplayReader(
            readGroup: true, rejectedField: null, rejectedGroup: pawnPath);
        var skippedGroup = CreateGroup(pawnPath, "PlayerState");
        var requiredGroup = CreateGroup(PlayerStatePath, "bIsABot");

        var skippedResult = reader.ReceiveProperties(
            CreatePropertiesArchive(1), skippedGroup, 1, out var skippedExport,
            enablePropertyChecksum: false);
        var requiredResult = reader.ReceiveProperties(
            CreatePropertiesArchive(1), requiredGroup, 1, out var requiredExport,
            enablePropertyChecksum: false);

        Assert.Multiple(() =>
        {
            Assert.That(skippedResult, Is.True);
            Assert.That(skippedExport, Is.Null);
            Assert.That(reader.IsIgnoringGroup(1, pawnPath), Is.False);
            Assert.That(requiredResult, Is.True);
            Assert.That(requiredExport, Is.TypeOf<FortPlayerState>());
            Assert.That(((FortPlayerState)requiredExport!).bIsABot, Is.True);
        });
    }

    [Test]
    public void NullFieldIsSkippedBeforeFollowingRequiredField()
    {
        var reader = new FilteringReplayReader(readGroup: true, rejectedField: null);
        var group = new NetFieldExportGroup
        {
            PathName = PlayerStatePath,
            NetFieldExportsLength = 2,
            NetFieldExports =
            [
                null,
                new NetFieldExport { Handle = 1, Name = "bIsABot" },
            ],
        };
        var archive = CreatePropertiesArchive(1, 2);

        var result = reader.ReceiveProperties(
            archive, group, 1, out var exportGroup, enablePropertyChecksum: false);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.True);
            Assert.That(archive.AtEnd(), Is.True);
            Assert.That(archive.IsError, Is.False);
            Assert.That(exportGroup, Is.TypeOf<FortPlayerState>());
            Assert.That(((FortPlayerState)exportGroup!).bIsABot, Is.True);
        });
    }

    [Test]
    public void UnknownFieldDoesNotBlockFollowingRequiredField()
    {
        var reader = new FilteringReplayReader(readGroup: true, rejectedField: null);
        var unknown = new NetFieldExport { Handle = 0, Name = "UnknownField" };
        var group = new NetFieldExportGroup
        {
            PathName = PlayerStatePath,
            NetFieldExportsLength = 2,
            NetFieldExports =
            [
                unknown,
                new NetFieldExport { Handle = 1, Name = "bIsABot" },
            ],
        };
        var archive = CreatePropertiesArchive(1, 2);

        var result = reader.ReceiveProperties(
            archive, group, 1, out var exportGroup, enablePropertyChecksum: false);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.True);
            Assert.That(archive.AtEnd(), Is.True);
            Assert.That(archive.IsError, Is.False);
            Assert.That(unknown.Incompatible, Is.True);
            Assert.That(exportGroup, Is.TypeOf<FortPlayerState>());
            Assert.That(((FortPlayerState)exportGroup!).bIsABot, Is.True);
        });
    }

    [Test]
    public void RejectedFieldWithOutOfBoundsLengthFailsWithoutAdvancingPastArchive()
    {
        var reader = new FilteringReplayReader(readGroup: true, rejectedField: "bDBNO");
        var group = CreateGroup(PlayerStatePath, "bDBNO");
        var archive = CreateOutOfBoundsPropertiesArchive();

        var result = reader.ReceiveProperties(
            archive, group, 1, out var exportGroup, enablePropertyChecksum: false);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.False);
            Assert.That(exportGroup, Is.Null);
            Assert.That(archive.Position, Is.LessThanOrEqualTo(archive.LastBit));
        });
    }

    [Test]
    public void AllowAllPolicyPreservesExportForEmptyUpdate()
    {
        var reader = new FilteringReplayReader(readGroup: true, rejectedField: null);
        var group = CreateGroup();
        var archive = new NetBitReader(new byte[] { 0 }, 8);

        var result = reader.ReceiveProperties(
            archive, group, 1, out var exportGroup, enablePropertyChecksum: false);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.True);
            Assert.That(archive.AtEnd(), Is.True);
            Assert.That(archive.IsError, Is.False);
            Assert.That(exportGroup, Is.TypeOf<FortPlayerState>());
            Assert.That(reader.LastExport, Is.Null);
        });
    }

    [Test]
    public void AllowedCustomPlaylistFieldIsDecodedAndResolved()
    {
        const string gameStatePath = "/Game/Athena/Athena_GameState.Athena_GameState_C";
        const string classCachePath = "Athena_GameState_C_ClassNetCache";
        const string playlistPath = "/Game/Athena/Playlists/Playlist_DefaultSolo.Playlist_DefaultSolo";
        const uint actorGuid = 42;
        const uint playlistGuid = 7;
        var reader = new FilteringReplayReader(readGroup: true, rejectedField: "OtherField");
        reader.ConfigureCustomPlaylist(
            actorGuid, playlistGuid, playlistPath, gameStatePath, classCachePath);
        var archive = CreateCustomPlaylistArchive(playlistGuid);
        var bunch = new DataBunch { Archive = archive, ChIndex = 1 };

        var result = reader.ReceivedReplicatorBunch(
            bunch, archive, actorGuid, bHasRepLayout: false);

        var playlist = reader.LastExport as PlaylistInfo;
        Assert.Multiple(() =>
        {
            Assert.That(result, Is.True);
            Assert.That(archive.AtEnd(), Is.True);
            Assert.That(archive.IsError, Is.False);
            Assert.That(playlist, Is.Not.Null);
            Assert.That(playlist!.Name, Is.EqualTo(playlistPath));
        });
    }

    private static NetFieldExportGroup CreateGroup() => new()
    {
        PathName = PlayerStatePath,
        NetFieldExportsLength = 2,
        NetFieldExports =
        [
            new NetFieldExport { Handle = 0, Name = "bDBNO" },
            new NetFieldExport { Handle = 1, Name = "bIsABot" },
        ],
    };

    private static NetFieldExportGroup CreateThreeFieldGroup() => new()
    {
        PathName = PlayerStatePath,
        NetFieldExportsLength = 3,
        NetFieldExports =
        [
            new NetFieldExport { Handle = 0, Name = "bDBNO" },
            new NetFieldExport { Handle = 1, Name = "bIsABot" },
            new NetFieldExport { Handle = 2, Name = "bOnlySpectator" },
        ],
    };

    private static NetFieldExportGroup CreateGroup(string path, string field) => new()
    {
        PathName = path,
        NetFieldExportsLength = 1,
        NetFieldExports = [new NetFieldExport { Handle = 0, Name = field }],
    };

    private static NetBitReader CreatePropertiesArchive(params uint[] handles)
    {
        var bits = new List<bool>();
        foreach (var handle in handles)
        {
            WritePacked(bits, handle);
            WritePacked(bits, 1);
            bits.Add(true);
        }
        WritePacked(bits, 0);

        var bytes = new byte[(bits.Count + 7) / 8];
        for (var index = 0; index < bits.Count; index++)
        {
            if (bits[index])
                bytes[index / 8] |= (byte)(1 << (index & 7));
        }

        return new NetBitReader(bytes, bits.Count);
    }

    private static NetBitReader CreateOutOfBoundsPropertiesArchive()
    {
        var bits = new List<bool>();
        WritePacked(bits, 1);
        WritePacked(bits, 64);
        bits.Add(true);

        var bytes = new byte[(bits.Count + 7) / 8];
        for (var index = 0; index < bits.Count; index++)
        {
            if (bits[index])
                bytes[index / 8] |= (byte)(1 << (index & 7));
        }
        return new NetBitReader(bytes, bits.Count);
    }

    private static NetBitReader CreateCustomPlaylistArchive(uint playlistGuid)
    {
        var payload = new List<bool>
        {
            false, // PlaylistInfo header bit for pre-fast-array-delta versions.
        };
        WritePacked(payload, playlistGuid);
        payload.AddRange(Enumerable.Repeat(false, 31));

        var bits = new List<bool>
        {
            false, // Class-net-cache field handle 0 of 2.
        };
        WritePacked(bits, (uint)payload.Count);
        bits.AddRange(payload);

        var bytes = new byte[(bits.Count + 7) / 8];
        for (var index = 0; index < bits.Count; index++)
        {
            if (bits[index])
                bytes[index / 8] |= (byte)(1 << (index & 7));
        }
        return new NetBitReader(bytes, bits.Count)
        {
            EngineNetworkVersion = EngineNetworkVersionHistory.HISTORY_INITIAL,
        };
    }

    private static void WritePacked(List<bool> bits, uint value)
    {
        do
        {
            var chunk = (byte)((value & 0x7f) << 1);
            value >>= 7;
            if (value != 0)
                chunk |= 1;
            for (var bit = 0; bit < 8; bit++)
                bits.Add((chunk & (1 << bit)) != 0);
        } while (value != 0);
    }

    private sealed class FilteringReplayReader(
        bool readGroup,
        string? rejectedField,
        string? rejectedGroup = null)
        : FortniteReplayReader.ReplayReader(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FortniteReplayReader.ReplayReader>.Instance,
            ParseMode.Minimal)
    {
        public INetFieldExportGroup? LastExport { get; private set; }

        public bool IsIgnoringGroup(uint channelIndex, string group) =>
            Channels[channelIndex]!.IsIgnoringGroup(group);

        public void ConfigureCustomPlaylist(
            uint actorGuid,
            uint playlistGuid,
            string playlistPath,
            string gameStatePath,
            string classCachePath)
        {
            _netGuidCache.NetGuidToPathName[actorGuid] = gameStatePath;
            _netGuidCache.NetGuidToPathName[playlistGuid] = playlistPath;
            _netGuidCache.AddToExportGroupMap(gameStatePath, new NetFieldExportGroup
            {
                PathName = gameStatePath,
                PathNameIndex = 1,
                NetFieldExportsLength = 0,
                NetFieldExports = [],
            });
            _netGuidCache.AddToExportGroupMap(classCachePath, new NetFieldExportGroup
            {
                PathName = classCachePath,
                PathNameIndex = 2,
                NetFieldExportsLength = 2,
                NetFieldExports =
                [
                    new NetFieldExport { Handle = 0, Name = "CurrentPlaylistInfo" },
                    null,
                ],
            });
        }

        protected override bool ShouldReadGroup(NetFieldExportGroup group) =>
            readGroup && !string.Equals(group.PathName, rejectedGroup, StringComparison.Ordinal);

        protected override bool ShouldReadField(NetFieldExportGroup group, NetFieldExport field) =>
            !string.Equals(field.Name, rejectedField, StringComparison.Ordinal);

        protected override void OnExportRead(uint channelIndex, INetFieldExportGroup? exportGroup) =>
            LastExport = exportGroup;

        public void InitializeChannel() => Channels[1] = new UChannel { ChannelIndex = 1 };

        // Field initializers run after the base constructor. Set the channel lazily on first use.
        public override bool ReceiveProperties(
            FBitArchive archive,
            NetFieldExportGroup group,
            uint channelIndex,
            out INetFieldExportGroup? exportGroup,
            bool enablePropertyChecksum = true,
            bool netDeltaUpdate = false)
        {
            InitializeChannel();
            return base.ReceiveProperties(
                archive, group, channelIndex, out exportGroup, enablePropertyChecksum, netDeltaUpdate);
        }
    }
}
