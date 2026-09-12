using BotOrNot.Core.Services;
using FortniteReplayReader;
using Microsoft.Extensions.Logging.Abstractions;
using Unreal.Core;
using Unreal.Core.Contracts;
using Unreal.Core.Models;
using Unreal.Core.Models.Enums;

namespace BotOrNot.Tests;

[TestFixture]
public sealed class SummaryContentBlockTests
{
    private const string SkippedPawnPath = "/Game/Athena/PlayerPawn_Athena.PlayerPawn_Athena_C";
    private const string GameStatePath = "/Game/Athena/Athena_GameState.Athena_GameState_C";
    private const string GameStateCachePath = "Athena_GameState_C_ClassNetCache";

    [Test]
    public void SkippedContentBlock_ConsumesOnlyTheTemporaryContentBoundary()
    {
        var reader = CreateReader(SkippedPawnPath);
        var archive = Bits(false, true, true, false, true, false);
        archive.SetTempEnd(4, FBitArchiveEndIndex.CONTENT_BLOCK_PAYLOAD);

        var result = reader.ReceivedReplicatorBunch(
            new DataBunch { Archive = archive, ChIndex = 1 }, archive, TestSummaryReplayReader.ActorGuid, bHasRepLayout: false);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.True);
            Assert.That(archive.Position, Is.EqualTo(4));
            Assert.That(archive.AtEnd(), Is.True);
            Assert.That(reader.ReceivePropertiesCalls, Is.Zero);
        });

        archive.RestoreTempEnd(FBitArchiveEndIndex.CONTENT_BLOCK_PAYLOAD);
        Assert.Multiple(() =>
        {
            Assert.That(archive.Position, Is.EqualTo(4));
            Assert.That(archive.ReadBit(), Is.True, "the neighbor bit must remain unread");
            Assert.That(archive.ReadBit(), Is.False);
        });
    }

    [Test]
    public void SkippedContentBlock_DeliversQueuedExternalDataOnce()
    {
        var reader = CreateReader(SkippedPawnPath);
        reader.QueueExternalData();
        var archive = Bits(false, true, false);
        archive.SetTempEnd(3, FBitArchiveEndIndex.CONTENT_BLOCK_PAYLOAD);

        var result = reader.ReceivedReplicatorBunch(
            new DataBunch { Archive = archive, ChIndex = 1 }, archive, TestSummaryReplayReader.ActorGuid, bHasRepLayout: true);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.True);
            Assert.That(reader.ExternalDataCallbacks, Is.EqualTo(1));
            Assert.That(reader.LastExternalNetGuid, Is.EqualTo(TestSummaryReplayReader.ActorGuid));
            Assert.That(archive.AtEnd(), Is.True);
        });
    }

    [Test]
    public void SkippedContentBlock_DoesNotDeliverExternalDataWithoutRepLayout()
    {
        var reader = CreateReader(SkippedPawnPath);
        reader.QueueExternalData();
        var archive = Bits(true, false);
        archive.SetTempEnd(2, FBitArchiveEndIndex.CONTENT_BLOCK_PAYLOAD);

        var result = reader.ReceivedReplicatorBunch(
            new DataBunch { Archive = archive, ChIndex = 1 }, archive, TestSummaryReplayReader.ActorGuid, bHasRepLayout: false);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.True);
            Assert.That(reader.ExternalDataCallbacks, Is.Zero);
            Assert.That(archive.AtEnd(), Is.True);
        });
    }

    [Test]
    public void ReadableClassNetCache_FallsBackToTheBaseReplicatorPath()
    {
        var reader = CreateReader(GameStatePath, forceSkip: true);
        reader.AddReadableGameStateClassCache();
        var archive = Bits(false);

        var result = reader.ReceivedReplicatorBunch(
            new DataBunch { Archive = archive, ChIndex = 1 }, archive, TestSummaryReplayReader.ActorGuid, bHasRepLayout: true);

        Assert.That(reader.ReceivePropertiesCalls, Is.EqualTo(1));
    }

    [Test]
    public void UnvalidatedRelease_FallsBackToTheBaseReplicatorPath()
    {
        var reader = CreateReader(SkippedPawnPath, major: 43, minor: 0);
        var archive = Bits(false);

        var result = reader.ReceivedReplicatorBunch(
            new DataBunch { Archive = archive, ChIndex = 1 }, archive, TestSummaryReplayReader.ActorGuid, bHasRepLayout: true);

        Assert.That(reader.ReceivePropertiesCalls, Is.EqualTo(1));
    }

    [Test]
    public void UnknownGroup_FallsBackToTheBaseReplicatorPath()
    {
        const string unknownPath = "/Game/Test/UnseenActor.UnseenActor_C";
        var reader = CreateReader(unknownPath);
        var archive = Bits(false);

        var result = reader.ReceivedReplicatorBunch(
            new DataBunch { Archive = archive, ChIndex = 1 }, archive, TestSummaryReplayReader.ActorGuid, bHasRepLayout: true);

        Assert.That(reader.ReceivePropertiesCalls, Is.EqualTo(1));
    }

    private static TestSummaryReplayReader CreateReader(
        string groupPath, int major = 42, int minor = 0, bool forceSkip = false)
    {
        var reader = new TestSummaryReplayReader(forceSkip)
        {
            Major = major,
            Minor = minor,
        };
        reader.ConfigureActorGroup(groupPath);
        return reader;
    }

    private static NetBitReader Bits(params bool[] bits)
    {
        var bytes = new byte[(bits.Length + 7) / 8];
        for (var index = 0; index < bits.Length; index++)
        {
            if (bits[index])
                bytes[index / 8] |= (byte)(1 << (index & 7));
        }

        return new NetBitReader(bytes, bits.Length);
    }

    private sealed class TestSummaryReplayReader(bool forceSkip) : SummaryReplayReader(NullLogger.Instance)
    {
        public const uint ActorGuid = 42;

        public int ExternalDataCallbacks { get; private set; }
        public uint? LastExternalNetGuid { get; private set; }
        public int ReceivePropertiesCalls { get; private set; }

        public void ConfigureActorGroup(string groupPath)
        {
            Channels[1] = new UChannel
            {
                ChannelIndex = 1,
                Actor = new Actor { ActorNetGUID = new NetworkGUID { Value = ActorGuid } },
            };
            _netGuidCache.NetGuidToPathName[ActorGuid] = groupPath;
            _netGuidCache.AddToExportGroupMap(groupPath, new NetFieldExportGroup
            {
                PathName = groupPath,
                PathNameIndex = 1,
                NetFieldExportsLength = 0,
                NetFieldExports = [],
            });
        }

        public void QueueExternalData() => _netGuidCache.ExternalData[ActorGuid] = new ExternalData
        {
            NetGUID = ActorGuid,
            Archive = new NetBitReader(Array.Empty<byte>(), 0),
        };

        public void AddReadableGameStateClassCache() =>
            _netGuidCache.AddToExportGroupMap(GameStateCachePath, new NetFieldExportGroup
            {
                PathName = GameStateCachePath,
                PathNameIndex = 2,
                NetFieldExportsLength = 1,
                NetFieldExports = [new NetFieldExport { Handle = 0, Name = "CurrentPlaylistInfo" }],
            });

        protected override bool ShouldReadGroup(NetFieldExportGroup group) =>
            forceSkip || base.ShouldReadGroup(group);

        public override bool ReceiveProperties(
            FBitArchive archive,
            NetFieldExportGroup group,
            uint channelIndex,
            out INetFieldExportGroup? exportGroup,
            bool enablePropertyChecksum = true,
            bool netDeltaUpdate = false)
        {
            ReceivePropertiesCalls++;
            exportGroup = null;
            return true;
        }

        protected override void OnExternalDataRead(uint channelIndex, IExternalData? update)
        {
            ExternalDataCallbacks++;
            LastExternalNetGuid = update?.NetGUID;
        }
    }
}
