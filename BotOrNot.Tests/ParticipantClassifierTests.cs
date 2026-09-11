using BotOrNot.Core.Models;
using BotOrNot.Core.Services;

namespace BotOrNot.Tests;

[TestFixture]
public sealed class ParticipantClassifierTests
{
    [TestCase("1", 1)]
    [TestCase("102", 102)]
    [TestCase(" 7 ", 7)]
    [TestCase(null, null)]
    [TestCase("", null)]
    [TestCase("unknown", null)]
    [TestCase("0", null)]
    [TestCase("-1", null)]
    public void NormalizeTeamIndex_AcceptsOnlyPositiveNumericValues(string? value, int? expected)
    {
        Assert.That(ParticipantClassifier.NormalizeTeamIndex(value), Is.EqualTo(expected));
    }

    [Test]
    public void Classify_UsesOwnerFlagEvenWhenStableIdentityAndTeamAreMissing()
    {
        var participant = new PlayerRow { IsReplayOwner = true };
        var replay = new ReplayData { Players = [participant] };

        Assert.That(
            ParticipantClassifier.Classify(replay, participant),
            Is.EqualTo(ParticipantRelationship.Owner));
    }

    [Test]
    public void Classify_UsesStableOwnerIdentityForDuplicateRows()
    {
        var owner = new PlayerRow { StableId = "account-id", IsReplayOwner = true };
        var replay = new ReplayData { OwnerId = "account-id", Players = [owner] };
        var participant = new PlayerRow { StableId = "ACCOUNT-ID" };

        Assert.That(
            ParticipantClassifier.Classify(replay, participant),
            Is.EqualTo(ParticipantRelationship.Owner));
    }

    [Test]
    public void Classify_RejectsOwnerFieldsThatDoNotMatchFlaggedRow()
    {
        var owner = new PlayerRow
        {
            StableId = "actual-owner",
            TeamIndexValue = 4,
            IsReplayOwner = true
        };
        var replay = new ReplayData
        {
            OwnerId = "guessed-owner",
            OwnerTeamIndex = 4,
            Players = [owner]
        };

        Assert.That(
            ParticipantClassifier.Classify(replay, new PlayerRow { StableId = "other", TeamIndexValue = 5 }),
            Is.EqualTo(ParticipantRelationship.Unknown));
    }

    [TestCase(4, 4, ParticipantRelationship.Teammate)]
    [TestCase(4, 5, ParticipantRelationship.Opponent)]
    [TestCase(null, 5, ParticipantRelationship.Unknown)]
    [TestCase(4, null, ParticipantRelationship.Unknown)]
    public void Classify_RequiresBothValidatedTeams(
        int? ownerTeam,
        int? participantTeam,
        ParticipantRelationship expected)
    {
        var owner = new PlayerRow { TeamIndexValue = ownerTeam, IsReplayOwner = true };
        var replay = new ReplayData { OwnerTeamIndex = ownerTeam, Players = [owner] };
        var participant = new PlayerRow { TeamIndexValue = participantTeam };

        Assert.That(ParticipantClassifier.Classify(replay, participant), Is.EqualTo(expected));
    }
}
