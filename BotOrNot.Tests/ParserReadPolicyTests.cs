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

    [Test]
    public void ReusablePropertyBufferResetsAfterLargerCustomPayload()
    {
        const string gameStatePath = "/Game/Athena/Athena_GameState.Athena_GameState_C";
        const string classCachePath = "Athena_GameState_C_ClassNetCache";
        const string playlistPath = "/Game/Athena/Playlists/Playlist_DefaultSolo.Playlist_DefaultSolo";
        const uint actorGuid = 42;
        const uint playlistGuid = 7;
        var reader = new FilteringReplayReader(readGroup: true, rejectedField: null);
        reader.ConfigureCustomPlaylist(
            actorGuid, playlistGuid, playlistPath, gameStatePath, classCachePath);

        Assert.That(reader.ReceivedReplicatorBunch(
            new DataBunch { Archive = CreateCustomPlaylistArchive(playlistGuid), ChIndex = 1 },
            CreateCustomPlaylistArchive(playlistGuid), actorGuid, bHasRepLayout: false), Is.True);

        var group = CreateGroup(PlayerStatePath, "bIsABot");
        var result = reader.ReceiveProperties(
            CreatePropertiesArchive(1), group, 1, out var exportGroup,
            enablePropertyChecksum: false);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.True);
            Assert.That(exportGroup, Is.TypeOf<FortPlayerState>());
            Assert.That(((FortPlayerState)exportGroup!).bIsABot, Is.True);
        });
    }

    [Test]
    public void ReusablePacketBufferOwnsInputAndResetsReaderState()
    {
        var reader = new PacketProbeReader(useReusableBuffers: true, dirtyFirstRead: true);
        var firstPacket = CreateRawPacket([true, false, true, false, false, true, false, true]);
        var originalFirstByte = firstPacket[0];

        reader.ReceivedRawPacket(firstPacket);
        firstPacket[0] ^= 0xff;
        Assert.That(reader.ReadCapturedByte(), Is.EqualTo(originalFirstByte));

        reader.ReceivedRawPacket(CreateRawPacket([false, true, false]));

        Assert.Multiple(() =>
        {
            Assert.That(reader.StartStates, Has.Count.EqualTo(2));
            Assert.That(reader.StartStates[1].Position, Is.Zero);
            Assert.That(reader.StartStates[1].MarkPosition, Is.Zero);
            Assert.That(reader.StartStates[1].LastBit, Is.EqualTo(3));
            Assert.That(reader.StartStates[1].IsError, Is.False);
        });
    }

    [Test]
    public void ReusablePacketBufferDoesNotInvalidateRetainedPartialBunch()
    {
        var reader = new PartialBunchProbeReader();
        bool[] initial = [true, false, true, true, false, false, true, false];
        bool[] final = [false, true, true, false, true];

        reader.ReceivedRawPacket(CreatePartialPacket(initial, initial: true, final: false));
        reader.ReceivedRawPacket(CreatePartialPacket(final, initial: false, final: true));

        Assert.Multiple(() =>
        {
            Assert.That(reader.CompletedBits, Is.EqualTo(initial.Concat(final)));
            Assert.That(reader.CompletedArchiveError, Is.False);
        });
    }

    [Test]
    public void ReusablePacketBufferAvoidsPerFillAllocation()
    {
        var packet = CreateRawPacket([true, false, true, false, true, false, true, false]);
        var reusable = new PacketProbeReader(useReusableBuffers: true);
        var copying = new PacketProbeReader(useReusableBuffers: false);
        reusable.ReceivedRawPacket(packet);
        copying.ReceivedRawPacket(packet);

        var beforeReusable = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
            reusable.ReceivedRawPacket(packet);
        var reusableBytes = GC.GetAllocatedBytesForCurrentThread() - beforeReusable;

        var beforeCopying = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
            copying.ReceivedRawPacket(packet);
        var copyingBytes = GC.GetAllocatedBytesForCurrentThread() - beforeCopying;

        Assert.That(reusableBytes, Is.LessThan(copyingBytes));
    }

    [Test]
    public void ReusableBunchResetsAllMutableHeaderStateBetweenPackets()
    {
        var reader = new BunchProbeReader(useReusableBunches: true, dirtyFirstBunch: true);
        reader.ReceivedPacket(CreateBunchArchive(
            CreateNonPartialBunchBits(channelIndex: 4, open: true, close: true,
                dormant: true, paused: true, reliable: true)));
        reader.ReceivedPacket(CreateBunchArchive(
            CreateNonPartialBunchBits(channelIndex: 9)));

        Assert.Multiple(() =>
        {
            Assert.That(reader.BunchReferences, Has.Count.EqualTo(2));
            Assert.That(reader.BunchReferences[1], Is.SameAs(reader.BunchReferences[0]));
            Assert.That(reader.Headers[0].ChIndex, Is.EqualTo(4));
            Assert.That(reader.Headers[0].bOpen, Is.True);
            Assert.That(reader.Headers[0].bClose, Is.True);
            Assert.That(reader.Headers[0].bDormant, Is.True);
            Assert.That(reader.Headers[0].bIsReplicationPaused, Is.True);
            Assert.That(reader.Headers[0].bReliable, Is.True);
            Assert.That(reader.Headers[1].ChIndex, Is.EqualTo(9));
            Assert.That(reader.Headers[1].PacketId, Is.EqualTo(2));
            Assert.That(reader.Headers[1].AllOptionalFlagsClear, Is.True);
            Assert.That(reader.Headers[1].CloseReason, Is.EqualTo(ChannelCloseReason.Destroyed));
            Assert.That(reader.Headers[1].ChType, Is.EqualTo(ChannelType.None));
            Assert.That(reader.Headers[1].ChName, Is.EqualTo(ChannelName.None));
            Assert.That(reader.Headers[1].ChSequence, Is.Zero);
        });
    }

    [Test]
    public void DefaultBunchPathKeepsDistinctObjects()
    {
        var reader = new BunchProbeReader(useReusableBunches: false);
        reader.ReceivedPacket(CreateBunchArchive(CreateNonPartialBunchBits(channelIndex: 4)));
        reader.ReceivedPacket(CreateBunchArchive(CreateNonPartialBunchBits(channelIndex: 9)));

        Assert.That(reader.BunchReferences[1], Is.Not.SameAs(reader.BunchReferences[0]));
    }

    [Test]
    public void ReusableBunchDoesNotOverwriteOuterBunchDuringNestedPacket()
    {
        var reader = new BunchProbeReader(useReusableBunches: true, nestOnFirstBunch: true);
        reader.ReceivedPacket(CreateBunchArchive(CreateNonPartialBunchBits(channelIndex: 4)));

        Assert.Multiple(() =>
        {
            Assert.That(reader.BunchReferences, Has.Count.EqualTo(2));
            Assert.That(reader.BunchReferences[1], Is.Not.SameAs(reader.BunchReferences[0]));
            Assert.That(reader.BunchReferences[0].ChIndex, Is.EqualTo(4));
            Assert.That(reader.BunchReferences[1].ChIndex, Is.EqualTo(9));
        });
    }

    [Test]
    public void ReusableBunchAvoidsPerPacketObjectAllocation()
    {
        var bits = CreateNonPartialBunchBits(channelIndex: 4);
        var archive = CreateBunchArchive(bits);
        var reusable = new BunchProbeReader(useReusableBunches: true);
        var copying = new BunchProbeReader(useReusableBunches: false);
        reusable.ReceivedPacket(archive);
        archive.Reset();
        copying.ReceivedPacket(archive);

        var beforeReusable = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
        {
            archive.Reset();
            reusable.ReceivedPacket(archive);
        }
        var reusableBytes = GC.GetAllocatedBytesForCurrentThread() - beforeReusable;

        var beforeCopying = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
        {
            archive.Reset();
            copying.ReceivedPacket(archive);
        }
        var copyingBytes = GC.GetAllocatedBytesForCurrentThread() - beforeCopying;

        Assert.That(reusableBytes, Is.LessThan(copyingBytes));
    }

    [Test]
    public void FastSerializedIntegerMatchesOriginalForEveryPowerAndBitOffset()
    {
        var slow = new SerializedIntProbeReader(useFastSerializedIntegers: false);
        var fast = new SerializedIntProbeReader(useFastSerializedIntegers: true);
        var random = new Random(0x5e71a1);

        for (var bitCount = 0; bitCount <= 30; bitCount++)
        {
            var maxValue = 1 << bitCount;
            var maxResult = (uint)maxValue - 1;
            uint[] values =
            [
                0,
                maxResult,
                maxResult == 0 ? 0u : 1u,
                maxResult == 0 ? 0u : (uint)random.NextInt64(maxValue),
            ];

            for (var offset = 0; offset <= 7; offset++)
            {
                foreach (var value in values.Distinct())
                {
                    foreach (var trailingBits in new[] { 0, 5 })
                    {
                        var bits = Enumerable.Range(0, offset)
                            .Select(index => (index & 1) == 0)
                            .ToList();
                        for (var bit = 0; bit < bitCount; bit++)
                            bits.Add((value & (1u << bit)) != 0);
                        bits.AddRange(Enumerable.Repeat(true, trailingBits));

                        AssertSerializedIntegerParity(
                            slow, fast, CreateRawPacket(bits), offset, maxValue,
                            startInError: false,
                            $"power={maxValue}, offset={offset}, value={value}, trailing={trailingBits}");
                    }
                }
            }
        }
    }

    [Test]
    public void FastSerializedIntegerFallsBackForTruncationErrorAndNonPowerRanges()
    {
        var slow = new SerializedIntProbeReader(useFastSerializedIntegers: false);
        var fast = new SerializedIntProbeReader(useFastSerializedIntegers: true);

        for (var bitCount = 1; bitCount <= 30; bitCount++)
        {
            var maxValue = 1 << bitCount;
            for (var offset = 0; offset <= 7; offset++)
            {
                var truncated = Enumerable.Range(0, offset + bitCount - 1)
                    .Select(index => (index & 1) == 0);
                AssertSerializedIntegerParity(
                    slow, fast, CreateRawPacket(truncated), offset, maxValue,
                    startInError: false, $"truncated power={maxValue}, offset={offset}");

                var complete = Enumerable.Range(0, offset + bitCount)
                    .Select(index => (index & 1) != 0);
                AssertSerializedIntegerParity(
                    slow, fast, CreateRawPacket(complete), offset, maxValue,
                    startInError: true, $"pre-error power={maxValue}, offset={offset}");
            }
        }

        int[] nonPowers = [0, -1, 3, 5, 7, 10, 255, 16_383, 16_385, 1_000_000];
        foreach (var maxValue in nonPowers)
        {
            for (var offset = 0; offset <= 7; offset++)
            {
                var bits = Enumerable.Range(0, offset + 40)
                    .Select(index => index % 3 == 0);
                AssertSerializedIntegerParity(
                    slow, fast, CreateRawPacket(bits), offset, maxValue,
                    startInError: false, $"nonpower={maxValue}, offset={offset}");
            }
        }
    }

    [Test]
    public void FastSerializedIntegerDoesNotAllocateAfterWarmup()
    {
        var reader = new SerializedIntProbeReader(useFastSerializedIntegers: true);
        var packet = CreateRawPacket(Enumerable.Range(0, 19).Select(index => index % 3 == 0));
        for (var i = 0; i < 100; i++)
            reader.Read(packet, offset: 5, maxValue: 16_384, startInError: false);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
            reader.Read(packet, offset: 5, maxValue: 16_384, startInError: false);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.That(allocated, Is.Zero);
    }

    private static void AssertSerializedIntegerParity(
        SerializedIntProbeReader slow,
        SerializedIntProbeReader fast,
        byte[] packet,
        int offset,
        int maxValue,
        bool startInError,
        string context)
    {
        var expected = slow.Read(packet, offset, maxValue, startInError);
        var actual = fast.Read(packet, offset, maxValue, startInError);
        Assert.That(actual, Is.EqualTo(expected), context);
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

    private static byte[] CreateRawPacket(IEnumerable<bool> payload)
    {
        var bits = payload.ToList();
        bits.Add(true); // Packet termination marker.
        return PackBits(bits);
    }

    private static byte[] CreatePartialPacket(bool[] payload, bool initial, bool final)
    {
        var bits = new List<bool>
        {
            false, // Legacy ack dummy.
            false, // bControl.
            false, // bIsReplicationPaused.
            false, // bReliable.
        };
        WriteSerializedInt(bits, 1, 10_240); // Channel index.
        bits.Add(false); // bHasPackageMapExports.
        bits.Add(false); // bHasMustBeMappedGUIDs.
        bits.Add(true);  // bPartial.
        bits.Add(initial);
        bits.Add(final);
        WriteSerializedInt(bits, 0, (uint)ChannelType.MAX);
        WriteSerializedInt(bits, (uint)payload.Length, 16_384);
        bits.AddRange(payload);
        return CreateRawPacket(bits);
    }

    private static List<bool> CreateNonPartialBunchBits(
        uint channelIndex, bool open = false, bool close = false,
        bool dormant = false, bool paused = false, bool reliable = false)
    {
        var bits = new List<bool>();
        bits.Add(false); // Legacy ack dummy.
        bits.Add(open || close);
        if (open || close)
        {
            bits.Add(open);
            bits.Add(close);
        }
        if (close)
            bits.Add(dormant);
        bits.Add(paused);
        bits.Add(reliable);
        WriteSerializedInt(bits, channelIndex, 10_240);
        bits.Add(false); // bHasPackageMapExports.
        bits.Add(false); // bHasMustBeMappedGUIDs.
        bits.Add(false); // bPartial.
        WriteSerializedInt(bits, 0, (uint)ChannelType.MAX);
        WriteSerializedInt(bits, 0, 16_384); // Empty bunch data.
        return bits;
    }

    private static NetBitReader CreateBunchArchive(List<bool> bits) =>
        new(PackBits(bits), bits.Count)
        {
            EngineNetworkVersion = EngineNetworkVersionHistory.HISTORY_INITIAL,
        };

    private static byte[] PackBits(IReadOnlyList<bool> bits)
    {
        var bytes = new byte[(bits.Count + 7) / 8];
        for (var index = 0; index < bits.Count; index++)
        {
            if (bits[index])
                bytes[index / 8] |= (byte)(1 << (index & 7));
        }
        return bytes;
    }

    private static void WriteSerializedInt(List<bool> bits, uint value, uint maxValue)
    {
        uint encoded = 0;
        for (uint mask = 1; encoded + mask < maxValue; mask *= 2)
        {
            var set = (value & mask) != 0;
            bits.Add(set);
            if (set)
                encoded |= mask;
        }
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

        protected override bool UseReusableBuffers => true;

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

    private sealed class PacketProbeReader(bool useReusableBuffers, bool dirtyFirstRead = false)
        : FortniteReplayReader.ReplayReader(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FortniteReplayReader.ReplayReader>.Instance,
            ParseMode.Minimal)
    {
        private FBitArchive? _captured;

        public List<(int Position, int MarkPosition, int LastBit, bool IsError)> StartStates { get; } = [];

        protected override bool UseReusableBuffers => useReusableBuffers;

        public override void ReceivedPacket(FBitArchive bitReader)
        {
            StartStates.Add((bitReader.Position, bitReader.MarkPosition,
                bitReader.Position + bitReader.GetBitsLeft(), bitReader.IsError));
            _captured = bitReader;
            if (dirtyFirstRead && StartStates.Count == 1)
            {
                bitReader.ReadBit();
                bitReader.Mark();
                bitReader.SetTempEnd(1, FBitArchiveEndIndex.BUNCH);
                bitReader.SetError();
            }
        }

        public byte ReadCapturedByte()
        {
            _captured!.Reset();
            return _captured.ReadByte();
        }
    }

    private sealed class PartialBunchProbeReader()
        : FortniteReplayReader.ReplayReader(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FortniteReplayReader.ReplayReader>.Instance,
            ParseMode.Minimal)
    {
        public bool[]? CompletedBits { get; private set; }
        public bool CompletedArchiveError { get; private set; }

        protected override bool UseReusableBuffers => true;
        protected override bool UseReusableBunches => true;

        public override bool ReceivedSequencedBunch(DataBunch bunch)
        {
            var bits = new List<bool>();
            while (!bunch.Archive.AtEnd())
                bits.Add(bunch.Archive.ReadBit());
            CompletedBits = bits.ToArray();
            CompletedArchiveError = bunch.Archive.IsError;
            return false;
        }
    }

    private sealed class BunchProbeReader(
        bool useReusableBunches, bool dirtyFirstBunch = false, bool nestOnFirstBunch = false)
        : FortniteReplayReader.ReplayReader(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FortniteReplayReader.ReplayReader>.Instance,
            ParseMode.Minimal)
    {
        public List<DataBunch> BunchReferences { get; } = [];
        public List<BunchHeaderSnapshot> Headers { get; } = [];

        protected override bool UseReusableBunches => useReusableBunches;

        public override void ReceivedRawBunch(DataBunch bunch)
        {
            BunchReferences.Add(bunch);
            Headers.Add(new BunchHeaderSnapshot(bunch));
            if (dirtyFirstBunch && BunchReferences.Count == 1)
            {
                bunch.bIgnoreRPCs = true;
                bunch.bHasPartialCustomExportsFinalBit = true;
                bunch.bHasPackageMapExports = true;
                bunch.bHasMustBeMappedGUIDs = true;
                bunch.bPartial = true;
                bunch.bPartialInitial = true;
                bunch.bPartialFinal = true;
                bunch.ChSequence = 99;
            }
            if (nestOnFirstBunch && BunchReferences.Count == 1)
                ReceivedPacket(CreateBunchArchive(CreateNonPartialBunchBits(channelIndex: 9)));
        }
    }

    private sealed record BunchHeaderSnapshot
    {
        public BunchHeaderSnapshot(DataBunch bunch)
        {
            PacketId = bunch.PacketId;
            ChIndex = bunch.ChIndex;
#pragma warning disable CS0618 // Verify that reuse clears every observable DataBunch field.
            ChType = bunch.ChType;
#pragma warning restore CS0618
            ChName = bunch.ChName;
            ChSequence = bunch.ChSequence;
            bOpen = bunch.bOpen;
            bClose = bunch.bClose;
            bDormant = bunch.bDormant;
            bIsReplicationPaused = bunch.bIsReplicationPaused;
            bReliable = bunch.bReliable;
            CloseReason = bunch.CloseReason;
            AllOptionalFlagsClear = !bunch.bOpen && !bunch.bClose && !bunch.bDormant
                && !bunch.bIsReplicationPaused && !bunch.bReliable && !bunch.bPartial
                && !bunch.bPartialInitial && !bunch.bHasPartialCustomExportsFinalBit
                && !bunch.bPartialFinal && !bunch.bHasPackageMapExports
                && !bunch.bHasMustBeMappedGUIDs && !bunch.bIgnoreRPCs;
        }

        public int PacketId { get; }
        public uint ChIndex { get; }
        public ChannelType ChType { get; }
        public ChannelName ChName { get; }
        public int ChSequence { get; }
        public bool bOpen { get; }
        public bool bClose { get; }
        public bool bDormant { get; }
        public bool bIsReplicationPaused { get; }
        public bool bReliable { get; }
        public ChannelCloseReason CloseReason { get; }
        public bool AllOptionalFlagsClear { get; }
    }

    private sealed class SerializedIntProbeReader(bool useFastSerializedIntegers)
        : FortniteReplayReader.ReplayReader(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FortniteReplayReader.ReplayReader>.Instance,
            ParseMode.Minimal)
    {
        private int _offset;
        private int _maxValue;
        private bool _startInError;
        private bool _completed;
        private SerializedIntResult _result;

        protected override bool UseReusableBuffers => true;
        protected override bool UseFastSerializedIntegers => useFastSerializedIntegers;

        public SerializedIntResult Read(
            byte[] packet, int offset, int maxValue, bool startInError)
        {
            _offset = offset;
            _maxValue = maxValue;
            _startInError = startInError;
            _completed = false;
            ReceivedRawPacket(packet);
            if (!_completed)
                throw new InvalidOperationException("Serialized integer probe did not complete.");
            return _result;
        }

        public override void ReceivedPacket(FBitArchive bitReader)
        {
            bitReader.SkipBits(_offset);
            if (_startInError)
                bitReader.SetError();
            var value = bitReader.ReadSerializedInt(_maxValue);
            _result = new SerializedIntResult(
                value, bitReader.Position, bitReader.GetBitsLeft(),
                bitReader.IsError, bitReader.AtEnd());
            _completed = true;
        }
    }

    private readonly record struct SerializedIntResult(
        uint Value, int Position, int BitsLeft, bool IsError, bool AtEnd);
}
