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
