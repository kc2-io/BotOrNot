using FortniteReplayReader;
using FortniteReplayReader.Models;
using FortniteReplayReader.Models.NetFieldExports;
using Unreal.Core.Models;

namespace BotOrNot.Tests;

[TestFixture]
public sealed class FortniteReplayBuilderTests
{
    [Test]
    public void RecorderBeforePlayer_MarksExactlyOneOwner()
    {
        var builder = new FortniteReplayBuilder();
        builder.UpdateGameState(GameStateWithRecorder(101));
        builder.AddActorChannel(7, 101);
        builder.UpdatePlayerState(7, Player(1, "owner"));

        var replay = builder.Build(new FortniteReplay());

        Assert.That(replay.PlayerData.Count(player => player.IsReplayOwner), Is.EqualTo(1));
        Assert.That(replay.PlayerData.Single(player => player.IsReplayOwner).PlayerId, Is.EqualTo("owner"));
    }

    [Test]
    public void PlayerBeforeRecorder_MarksExactlyOneOwner()
    {
        var builder = new FortniteReplayBuilder();
        builder.AddActorChannel(7, 101);
        builder.UpdatePlayerState(7, Player(1, "owner"));
        builder.UpdateGameState(GameStateWithRecorder(101));

        var replay = builder.Build(new FortniteReplay());

        Assert.That(replay.PlayerData.Single(player => player.IsReplayOwner).PlayerId, Is.EqualTo("owner"));
    }

    [Test]
    public void ActorMappingAfterRecorderAndPlayer_MarksOwner()
    {
        var builder = new FortniteReplayBuilder();
        builder.UpdateGameState(GameStateWithRecorder(101));
        builder.UpdatePlayerState(7, Player(1, "owner"));
        builder.AddActorChannel(7, 101);

        Assert.That(builder.Build(new FortniteReplay()).PlayerData.Single().IsReplayOwner, Is.True);
    }

    [Test]
    public void RecorderIdWithoutActorMapping_DoesNotMatchNumericPlayerId()
    {
        var builder = new FortniteReplayBuilder();
        builder.UpdateGameState(GameStateWithRecorder(42));
        builder.UpdatePlayerState(7, Player(42, "same-number-different-namespace"));

        Assert.That(builder.Build(new FortniteReplay()).PlayerData.Any(player => player.IsReplayOwner), Is.False);
    }

    [Test]
    public void ChannelReuse_PreservesDepartedOwnerWithoutTaggingNewOccupant()
    {
        var builder = new FortniteReplayBuilder();
        builder.UpdateGameState(GameStateWithRecorder(101));
        builder.AddActorChannel(7, 101);
        builder.UpdatePlayerState(7, Player(1, "owner"));
        builder.RemoveChannel(7);
        builder.AddActorChannel(7, 202);
        builder.UpdatePlayerState(7, Player(2, "new-occupant"));

        var players = builder.Build(new FortniteReplay()).PlayerData.ToList();

        Assert.That(players, Has.Count.EqualTo(2));
        Assert.That(players.Single(player => player.IsReplayOwner).PlayerId, Is.EqualTo("owner"));
        Assert.That(players.Single(player => player.PlayerId == "new-occupant").IsReplayOwner, Is.False);
    }

    [Test]
    public void IncrementalPlayerUpdates_PreserveMissingAndApplyFalseAndZero()
    {
        var builder = new FortniteReplayBuilder();
        builder.UpdatePlayerState(7, new FortPlayerState
        {
            PlayerID = 1,
            UniqueId = "player-one",
            bIsABot = true,
            Level = 12,
            SeasonLevelUIDisplay = 34
        });
        builder.UpdatePlayerState(7, new FortPlayerState
        {
            bIsABot = false,
            Level = 0,
            SeasonLevelUIDisplay = 0
        });
        builder.UpdatePlayerState(7, new FortPlayerState());

        var player = builder.Build(new FortniteReplay()).PlayerData.Single();

        Assert.That(player.PlayerId, Is.EqualTo("player-one"));
        Assert.That(player.IsBot, Is.False);
        Assert.That(player.Level, Is.Zero);
        Assert.That(player.SeasonLevelUIDisplay, Is.Zero);
    }

    [Test]
    public void SafeZonePartialExports_PreservePhaseAndExposeFieldPresence()
    {
        var builder = new FortniteReplayBuilder();
        builder.UpdateSafeZones(7, new SafeZoneIndicator
        {
            SafeZoneStartShrinkTime = 300,
            SafeZoneFinishShrinkTime = 480,
            CurrentPhase = 1,
            PhaseCount = 13
        }, 100);
        builder.UpdateSafeZones(7, new SafeZoneIndicator
        {
            SafeZoneStartShrinkTime = 570,
            SafeZoneFinishShrinkTime = 670,
            CurrentPhase = 2
        }, 200);

        var observations = builder.Build(new FortniteReplay()).MapData.SafeZones.ToList();

        Assert.Multiple(() =>
        {
            Assert.That(observations, Has.Count.EqualTo(2));
            Assert.That(observations[1].CurrentPhase, Is.EqualTo(2));
            Assert.That(observations[1].PhaseCount, Is.EqualTo(13));
            Assert.That(observations[1].CurrentPhaseWasExported, Is.True);
            Assert.That(observations[1].PhaseCountWasExported, Is.False);
            Assert.That(observations[1].ReplayTimeSeconds, Is.EqualTo(200));
            Assert.That(observations[1].ChannelIndex, Is.EqualTo(7));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SafeZoneChannelReuse_DoesNotCarryPreviousActorPhase(bool movePreviousActor)
    {
        var builder = new FortniteReplayBuilder();
        builder.AddActorChannel(7, 101);
        builder.UpdateSafeZones(7, new SafeZoneIndicator { CurrentPhase = 6, PhaseCount = 13 }, 100);
        if (movePreviousActor)
            builder.AddActorChannel(8, 101);
        builder.AddActorChannel(7, 202);
        builder.UpdateSafeZones(7, new SafeZoneIndicator
        {
            SafeZoneStartShrinkTime = 700,
            SafeZoneFinishShrinkTime = 800
        }, 200);

        var latest = builder.Build(new FortniteReplay()).MapData.SafeZones.Last();

        Assert.Multiple(() =>
        {
            Assert.That(latest.CurrentPhase, Is.Null);
            Assert.That(latest.PhaseCount, Is.Null);
            Assert.That(latest.StartShrinkTime, Is.EqualTo(700));
        });
    }

    [Test]
    public void RecordedTeamSize_OnlyUsesExplicitGameStateTeamSize()
    {
        var builder = new FortniteReplayBuilder();
        builder.UpdateGameState(new GameState
        {
            ActiveTeamNums =
            [
                new NetworkGUID { Value = 1 },
                new NetworkGUID { Value = 2 },
                new NetworkGUID { Value = 3 }
            ]
        });

        var fallbackOnly = builder.Build(new FortniteReplay()).GameData;
        Assert.Multiple(() =>
        {
            Assert.That(fallbackOnly.TeamSize, Is.EqualTo(3));
            Assert.That(fallbackOnly.RecordedTeamSize, Is.Null);
        });

        var explicitBuilder = new FortniteReplayBuilder();
        explicitBuilder.UpdateGameState(new GameState { TeamSize = 4 });
        Assert.That(explicitBuilder.Build(new FortniteReplay()).GameData.RecordedTeamSize, Is.EqualTo(4));
    }

    private static GameState GameStateWithRecorder(uint actorId) => new()
    {
        RecorderPlayerState = new ActorGuid { Value = actorId }
    };

    private static FortPlayerState Player(int id, string accountId) => new()
    {
        PlayerID = id,
        UniqueId = accountId,
        bIsABot = false
    };
}
