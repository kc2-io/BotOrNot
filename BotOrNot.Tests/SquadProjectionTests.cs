using BotOrNot.Core.Models;
using BotOrNot.Core.Services;

namespace BotOrNot.Tests;

[TestFixture]
public sealed class SquadProjectionTests
{
    [Test]
    public void FromReplay_CompleteDuoProjectsNullableMemberStatsAndOneTeamTotal()
    {
        var replay = Replay(
            "Playlist_DefaultDuo",
            Player("owner-id", "Recorder", 3, isOwner: true, kills: "0", teamKills: "5", squadSize: 2),
            Player("mate-id", "Teammate", 3, kills: "0", teamKills: "5", squadSize: 2,
                level: "15", platform: "Xbox", placement: "2"));

        var projection = SquadProjection.FromReplay(replay);

        Assert.Multiple(() =>
        {
            Assert.That(projection.Status, Is.EqualTo(SquadProjectionStatus.Complete));
            Assert.That(projection.ExpectedTeamSize, Is.EqualTo(2));
            Assert.That(projection.ObservedTeamSize, Is.EqualTo(2));
            Assert.That(projection.TeamKills, Is.EqualTo(5));
            Assert.That(projection.HasConflictingTeamKills, Is.False);
            Assert.That(projection.Teammates, Has.Count.EqualTo(1));
        });
        var teammate = projection.Teammates.Single();
        Assert.Multiple(() =>
        {
            Assert.That(teammate.StableId, Is.EqualTo("mate-id"));
            Assert.That(teammate.Name, Is.EqualTo("Teammate"));
            Assert.That(teammate.Kills, Is.Zero);
            Assert.That(teammate.Level, Is.EqualTo(15));
            Assert.That(teammate.Platform, Is.EqualTo("Xbox"));
            Assert.That(teammate.Placement, Is.EqualTo(2));
            Assert.That(teammate.ObservedSquadSize, Is.EqualTo(projection.ObservedTeamSize));
            Assert.That(teammate.IsBot, Is.False);
        });
    }

    [Test]
    public void FromReplay_ConfirmedSoloRequiresCatalogFormatWithoutObservedTeammate()
    {
        var confirmedSolo = Replay(
            "Playlist_DefaultSolo",
            Player("owner-id", "Recorder", 7, isOwner: true, squadSize: 1));
        var contradictorySolo = Replay(
            "Playlist_DefaultSolo",
            Player("owner-id", "Recorder", 7, isOwner: true, squadSize: 2),
            Player("mate-id", "Observed teammate", 7, squadSize: 2));
        var unknownFormat = Replay(
            "Playlist_UncataloguedSolo",
            Player("owner-id", "Recorder", 7, isOwner: true, squadSize: 1));
        var unknownFormatWithTeammate = Replay(
            "Playlist_UncataloguedDuo",
            Player("owner-id", "Recorder", 7, isOwner: true, squadSize: 2),
            Player("mate-id", "Observed teammate", 7, squadSize: 2));

        Assert.Multiple(() =>
        {
            Assert.That(SquadProjection.FromReplay(confirmedSolo).Status,
                Is.EqualTo(SquadProjectionStatus.ConfirmedSolo));
            Assert.That(SquadProjection.FromReplay(contradictorySolo).Status,
                Is.EqualTo(SquadProjectionStatus.Partial));
            Assert.That(SquadProjection.FromReplay(contradictorySolo).Teammates, Has.Count.EqualTo(1));
            Assert.That(SquadProjection.FromReplay(unknownFormat).Status,
                Is.EqualTo(SquadProjectionStatus.Unavailable));
            Assert.That(SquadProjection.FromReplay(unknownFormatWithTeammate).Status,
                Is.EqualTo(SquadProjectionStatus.Partial));
            Assert.That(SquadProjection.FromReplay(unknownFormatWithTeammate).Teammates,
                Has.Count.EqualTo(1));
        });
    }

    [TestCase("Playlist_DefaultDuo", 1)]
    [TestCase("Playlist_DefaultTrio", 2)]
    [TestCase("Playlist_DefaultSquad", 3)]
    public void FromReplay_KnownTeamFormatIsCompleteWhenAllMembersAreObserved(
        string playlist,
        int observedTeammates)
    {
        var players = new List<PlayerRow>
        {
            Player("owner-id", "Recorder", 4, isOwner: true, squadSize: observedTeammates + 1)
        };
        players.AddRange(Enumerable.Range(0, observedTeammates)
            .Select(index => Player($"mate-{index}", $"Teammate {index}", 4,
                squadSize: observedTeammates + 1)));
        var replay = Replay(playlist, players.ToArray());

        var projection = SquadProjection.FromReplay(replay);

        Assert.That(projection.Status, Is.EqualTo(SquadProjectionStatus.Complete));
        Assert.That(projection.Teammates, Has.Count.EqualTo(observedTeammates));
    }

    [Test]
    public void FromReplay_MissingExpectedMemberIsPartialRatherThanSolo()
    {
        var replay = Replay(
            "Playlist_DefaultTrio",
            Player("owner-id", "Recorder", 4, isOwner: true, squadSize: 2),
            Player("mate-id", "Teammate", 4, squadSize: 2));

        var projection = SquadProjection.FromReplay(replay);

        Assert.That(projection.Status, Is.EqualTo(SquadProjectionStatus.Partial));
        Assert.That(projection.ExpectedTeamSize, Is.EqualTo(3));
        Assert.That(projection.ObservedTeamSize, Is.EqualTo(2));
    }

    [Test]
    public void FromReplay_DeduplicatesStableIdsButKeepsDistinctPeopleWithSameName()
    {
        var replay = Replay(
            "Playlist_DefaultTrio",
            Player("owner-id", "Recorder", 4, isOwner: true, squadSize: 3),
            Player("mate-a", "Same name", 4, kills: "2", squadSize: 3),
            Player("MATE-A", "Same name", 4, kills: "3", squadSize: 3),
            Player("mate-b", "Same name", 4, squadSize: 3));

        var projection = SquadProjection.FromReplay(replay);

        Assert.That(projection.Status, Is.EqualTo(SquadProjectionStatus.Complete));
        Assert.That(projection.Teammates, Has.Count.EqualTo(2));
        Assert.That(projection.Teammates.Select(teammate => teammate.StableId),
            Is.EquivalentTo(new[] { "mate-a", "mate-b" }).IgnoreCase);
        Assert.That(projection.Teammates.Single(teammate =>
            teammate.StableId.Equals("mate-a", StringComparison.OrdinalIgnoreCase)).Kills, Is.Null);
    }

    [Test]
    public void FromReplay_ConflictingMembershipIsPartialAndExcluded()
    {
        var replay = Replay(
            "Playlist_DefaultDuo",
            Player("owner-id", "Recorder", 4, isOwner: true, squadSize: 2),
            Player("moving-id", "Moving player", 4, squadSize: 2),
            Player("MOVING-ID", "Moving player", 8, squadSize: 1));

        var projection = SquadProjection.FromReplay(replay);

        Assert.That(projection.Status, Is.EqualTo(SquadProjectionStatus.Partial));
        Assert.That(projection.Teammates, Is.Empty);
    }

    [Test]
    public void FromReplay_UnresolvedMembershipMakesPerMemberObservedSizeUnavailable()
    {
        var replay = Replay(
            "Playlist_DefaultTrio",
            Player("owner-id", "Recorder", 4, isOwner: true),
            Player("known-mate", "Known teammate", 4),
            Player("moving-id", "Moving player", 4),
            Player("MOVING-ID", "Moving player", 8));

        var projection = SquadProjection.FromReplay(replay);

        Assert.That(projection.ObservedTeamSize, Is.EqualTo(2));
        Assert.That(projection.Teammates.Single().ObservedSquadSize, Is.Null);
        Assert.That(projection.Status, Is.EqualTo(SquadProjectionStatus.Partial));
    }

    [Test]
    public void FromReplay_ConflictingMembershipCannotSupplyTeamTotal()
    {
        var replay = Replay(
            "Playlist_DefaultDuo",
            Player("owner-id", "Recorder", 4, isOwner: true),
            Player("moving-id", "Moving player", 4, teamKills: "6"),
            Player("MOVING-ID", "Moving player", 8, teamKills: "6"));

        var projection = SquadProjection.FromReplay(replay);

        Assert.That(projection.TeamKills, Is.Null);
        Assert.That(projection.Teammates, Is.Empty);
        Assert.That(projection.Status, Is.EqualTo(SquadProjectionStatus.Partial));
    }

    [Test]
    public void FromReplay_DuplicateOwnerIdentityOnEnemyTeamCannotSupplyTeamTotal()
    {
        var replay = Replay(
            "Playlist_DefaultDuo",
            Player("owner-id", "Recorder", 4, isOwner: true, teamKills: "6"),
            Player("OWNER-ID", "Recorder duplicate", 8, teamKills: "6"));

        var projection = SquadProjection.FromReplay(replay);

        Assert.That(projection.TeamKills, Is.Null);
        Assert.That(projection.ObservedTeamSize, Is.Zero);
        Assert.That(projection.Status, Is.EqualTo(SquadProjectionStatus.Partial));
    }

    [Test]
    public void FromReplay_RepeatedAnonymousRowsDoNotInflateObservedTeamSize()
    {
        var anonymous = Player("", "Anonymous teammate", 4, squadSize: 2);
        anonymous.Id = "fallback-one";
        var repeatedAnonymous = Player("", "Anonymous teammate", 4, squadSize: 2);
        repeatedAnonymous.Id = "fallback-two";
        var replay = Replay(
            "Playlist_DefaultDuo",
            Player("owner-id", "Recorder", 4, isOwner: true, squadSize: 2),
            anonymous,
            repeatedAnonymous);

        var projection = SquadProjection.FromReplay(replay);

        Assert.That(projection.ObservedTeamSize, Is.EqualTo(1));
        Assert.That(projection.Teammates, Is.Empty);
        Assert.That(projection.Status, Is.EqualTo(SquadProjectionStatus.Partial));
    }

    [Test]
    public void FromReplay_IncludesActualBotTeammateAndExcludesNpc()
    {
        var replay = Replay(
            "Playlist_DefaultDuo",
            Player("owner-id", "Recorder", 4, isOwner: true, squadSize: 2),
            Player("bot-id", "Bot teammate", 4, isBot: true, squadSize: 2),
            Player("Wildlife", "Wildlife", 4, squadSize: 3));

        var projection = SquadProjection.FromReplay(replay);

        Assert.That(projection.Status, Is.EqualTo(SquadProjectionStatus.Complete));
        Assert.That(projection.Teammates, Has.Count.EqualTo(1));
        Assert.That(projection.Teammates.Single().IsBot, Is.True);
    }

    [Test]
    public void FromReplay_UnknownMemberStatsRemainNullAndTeamKillConflictsDoNotSum()
    {
        var replay = Replay(
            "Playlist_DefaultDuo",
            Player("owner-id", "Recorder", 4, isOwner: true, teamKills: "4", squadSize: 2),
            Player("mate-id", "unknown", 4, kills: "unknown", teamKills: "5", squadSize: 0,
                level: "unknown", platform: "unknown", placement: "unknown", bot: "unknown"));

        var projection = SquadProjection.FromReplay(replay);
        var teammate = projection.Teammates.Single();

        Assert.Multiple(() =>
        {
            Assert.That(projection.TeamKills, Is.Null);
            Assert.That(projection.HasConflictingTeamKills, Is.True);
            Assert.That(teammate.Name, Is.Null);
            Assert.That(teammate.Kills, Is.Null);
            Assert.That(teammate.Level, Is.Null);
            Assert.That(teammate.Platform, Is.Null);
            Assert.That(teammate.Placement, Is.Null);
            Assert.That(teammate.ObservedSquadSize, Is.EqualTo(2));
            Assert.That(teammate.IsBot, Is.Null);
        });
    }

    [Test]
    public void FromReplay_MissingOwnerOrSentinelOwnerTeamIsUnavailable()
    {
        var noOwner = new ReplayData
        {
            Metadata = new ReplayMetadata { Playlist = "Playlist_DefaultSquad" },
            Players = [Player("other-id", "Other", 5)]
        };
        var sentinelOwnerTeam = Replay(
            "Playlist_DefaultDuo",
            Player("owner-id", "Recorder", null, isOwner: true));

        Assert.Multiple(() =>
        {
            Assert.That(SquadProjection.FromReplay(noOwner).Status,
                Is.EqualTo(SquadProjectionStatus.Unavailable));
            Assert.That(SquadProjection.FromReplay(sentinelOwnerTeam).Status,
                Is.EqualTo(SquadProjectionStatus.Unavailable));
        });
    }

    [Test]
    public void FromReplay_AmbiguousOwnerIsUnavailableEvenForKnownSolo()
    {
        var firstOwner = Player("owner-id", "Recorder", 4, isOwner: true);
        var secondOwner = Player("other-id", "Other recorder", 8, isOwner: true);
        var replay = new ReplayData
        {
            OwnerId = firstOwner.StableId,
            OwnerTeamIndex = firstOwner.TeamIndexValue,
            Metadata = new ReplayMetadata { Playlist = "Playlist_DefaultSolo" },
            Players = [firstOwner, secondOwner]
        };

        var projection = SquadProjection.FromReplay(replay);

        Assert.That(projection.Status, Is.EqualTo(SquadProjectionStatus.Unavailable));
        Assert.That(projection.ExpectedTeamSize, Is.EqualTo(1));
    }

    [TestCase("Blitz_ForbiddenFruit_CalmSambucusBRSquad_Owner_Elim_1_Team_Elim_3_Place_3.replay", 4, 3)]
    [TestCase("Reload_PunchBerryDuo_Owner_Elim_5_Team_Elim_1_Place_1.replay", 2, 1)]
    public async Task FromReplay_RealTeamFixturesMatchExpectedMembership(
        string replayFileName,
        int expectedTeamSize,
        int expectedTeammates)
    {
        var replayPath = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestData", replayFileName);
        var replay = await new ReplayService().LoadReplayAsync(replayPath);

        var projection = SquadProjection.FromReplay(replay);

        Assert.Multiple(() =>
        {
            Assert.That(projection.Status, Is.EqualTo(SquadProjectionStatus.Complete));
            Assert.That(projection.ExpectedTeamSize, Is.EqualTo(expectedTeamSize));
            Assert.That(projection.ObservedTeamSize, Is.EqualTo(expectedTeamSize));
            Assert.That(projection.Teammates, Has.Count.EqualTo(expectedTeammates));
            Assert.That(projection.Teammates.Any(teammate =>
                teammate.StableId.Equals(replay.OwnerId, StringComparison.OrdinalIgnoreCase)), Is.False);
        });
    }

    private static ReplayData Replay(string playlist, params PlayerRow[] players)
    {
        var owner = players.Single(player => player.IsReplayOwner);
        return new ReplayData
        {
            OwnerId = owner.StableId,
            OwnerTeamIndex = owner.TeamIndexValue,
            Metadata = new ReplayMetadata { Playlist = playlist },
            Players = players.ToList()
        };
    }

    private static PlayerRow Player(
        string stableId,
        string name,
        int? team,
        bool isOwner = false,
        bool isBot = false,
        string? kills = null,
        string? teamKills = null,
        int squadSize = 0,
        string? level = null,
        string? platform = null,
        string? placement = null,
        string? bot = null) => new()
        {
            StableId = stableId,
            Id = stableId,
            Name = name,
            TeamIndexValue = team,
            TeamIndex = team?.ToString(),
            IsReplayOwner = isOwner,
            Bot = bot ?? isBot.ToString(),
            Kills = kills,
            TeamKills = teamKills,
            SquadSize = squadSize,
            Level = level,
            Platform = platform,
            Placement = placement
        };
}
