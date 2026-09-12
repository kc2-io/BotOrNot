using BotOrNot.Core.Models;
using BotOrNot.Core.Services;

namespace BotOrNot.Tests;

[TestFixture]
public sealed class OpponentProjectionTests
{
    [Test]
    public void FromReplay_ExcludesOwnerTeammatesBotsNpcsAndUnknownRelationships()
    {
        var replay = Replay(
            Player("owner", "Recorder", 4, isOwner: true),
            Player("mate", "Squad mate", 4),
            Player("opponent", "Enemy player", 5),
            Player("bot", "Bot", 5, isBot: true),
            new PlayerRow
            {
                StableId = "Wildlife",
                Id = "Wildlife",
                Name = "Wildlife",
                TeamIndexValue = 5,
                Bot = "false"
            },
            Player("unknown-team", "Unknown", null));

        var projection = OpponentProjection.FromReplay(replay);

        Assert.That(projection.Opponents.Select(opponent => opponent.StableId).ToArray(), Is.EqualTo(new[] { "opponent" }));
        Assert.That(projection.IsComplete, Is.False);
    }

    [Test]
    public void FromReplay_DeduplicatesStableIdentityWithinMatchAndNeverUsesFallbackId()
    {
        var replay = Replay(
            Player("owner", "Recorder", 4, isOwner: true),
            Player("repeat", "Earlier name", 5),
            Player("REPEAT", "Later name", 5),
            new PlayerRow
            {
                Id = "Display-name fallback",
                Name = "Display-name fallback",
                TeamIndexValue = 5,
                Bot = "false"
            });

        var projection = OpponentProjection.FromReplay(replay);

        Assert.That(projection.Opponents, Has.Count.EqualTo(1));
        Assert.That(projection.Opponents.Single().StableId, Is.EqualTo("repeat").IgnoreCase);
        Assert.That(projection.IsComplete, Is.False, "A fallback row cannot be counted as a stable opponent.");
        Assert.That(replay.Players.Last().IsNpc, Is.False, "A name-derived display ID is not authoritative NPC evidence.");
    }

    [Test]
    public void FromReplay_ConflictingTeamsForRepeatedIdentityAreUnknown()
    {
        var replay = Replay(
            Player("owner", "Recorder", 4, isOwner: true),
            Player("repeat", "Returning", 4),
            Player("REPEAT", "Returning", 5));

        var projection = OpponentProjection.FromReplay(replay);

        Assert.That(projection.Opponents, Is.Empty);
        Assert.That(projection.IsComplete, Is.False);
    }

    [Test]
    public void FromReplay_SameIdentityCountsOnlyInMatchWhereItIsAnOpponent()
    {
        var teammateMatch = Replay(
            Player("owner", "Recorder", 4, isOwner: true),
            Player("returning", "Returning player", 4));
        var opponentMatch = Replay(
            Player("owner", "Recorder", 9, isOwner: true),
            Player("returning", "Returning player", 12));

        Assert.That(OpponentProjection.FromReplay(teammateMatch).Opponents, Is.Empty);
        Assert.That(
            OpponentProjection.FromReplay(opponentMatch).Opponents.Select(opponent => opponent.StableId).ToArray(),
            Is.EqualTo(new[] { "returning" }));
    }

    [Test]
    public void FromReplay_MissingOwnerTeamMakesRelationshipsUnknown()
    {
        var replay = Replay(
            Player("owner", "Recorder", null, isOwner: true),
            Player("other", "Other", 5));
        replay.OwnerTeamIndex = null;

        var projection = OpponentProjection.FromReplay(replay);

        Assert.That(projection.Opponents, Is.Empty);
        Assert.That(projection.IsComplete, Is.False);
    }

    [Test]
    public void FromReplay_MissingOrAmbiguousOwnerFlagMakesAllRelationshipsUnknown()
    {
        var noOwner = new ReplayData
        {
            OwnerId = "guessed-owner",
            OwnerTeamIndex = 4,
            Players = [Player("other", "Other", 5)]
        };
        var twoOwners = new ReplayData
        {
            OwnerId = "owner-a",
            OwnerTeamIndex = 4,
            Players =
            [
                Player("owner-a", "A", 4, isOwner: true),
                Player("owner-b", "B", 4, isOwner: true),
                Player("other", "Other", 5)
            ]
        };

        Assert.Multiple(() =>
        {
            Assert.That(OpponentProjection.FromReplay(noOwner).Opponents, Is.Empty);
            Assert.That(OpponentProjection.FromReplay(noOwner).IsComplete, Is.False);
            Assert.That(OpponentProjection.FromReplay(twoOwners).Opponents, Is.Empty);
            Assert.That(OpponentProjection.FromReplay(twoOwners).IsComplete, Is.False);
        });
    }

    private static ReplayData Replay(params PlayerRow[] players)
    {
        var owner = players.Single(player => player.IsReplayOwner);
        return new ReplayData
        {
            OwnerId = owner.StableId,
            OwnerTeamIndex = owner.TeamIndexValue,
            Players = players.ToList()
        };
    }

    private static PlayerRow Player(
        string stableId,
        string name,
        int? team,
        bool isOwner = false,
        bool isBot = false) => new()
        {
            StableId = stableId,
            Id = stableId,
            Name = name,
            TeamIndexValue = team,
            TeamIndex = team?.ToString(),
            IsReplayOwner = isOwner,
            Bot = isBot ? "true" : "false"
        };
}
