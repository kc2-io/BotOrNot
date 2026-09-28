using System.Globalization;
using BotOrNot.Core.Models;

namespace BotOrNot.Core.Services;

public enum SquadProjectionStatus
{
    ConfirmedSolo,
    Complete,
    Partial,
    Unavailable
}

public sealed class SquadMemberSummary
{
    public string StableId { get; init; } = "";
    public string? Name { get; init; }
    public int? Kills { get; init; }
    public int? Level { get; init; }
    public string? Platform { get; init; }
    public int? Placement { get; init; }
    public int? ObservedSquadSize { get; init; }
    public bool? IsBot { get; init; }
    public bool CanOpenFortniteTracker => IsBot is not true && !string.IsNullOrWhiteSpace(Name);
}

/// <summary>A conservative, display-ready view of the recorder's observed squad.</summary>
public sealed class SquadProjection
{
    public SquadProjectionStatus Status { get; init; }
    public int? ExpectedTeamSize { get; init; }
    /// <summary>
    /// Minimum observed team size based on distinct, stable identities whose membership is not
    /// contradictory. Anonymous and ambiguous records do not inflate this count.
    /// </summary>
    public int? ObservedTeamSize { get; init; }
    public int? TeamKills { get; init; }
    public bool HasConflictingTeamKills { get; init; }
    public List<SquadMemberSummary> Teammates { get; init; } = new();

    public static SquadProjection FromReplay(ReplayData replay)
    {
        var expectedTeamSize = PlaylistHelper.GetKnownMaxTeamSize(replay.Metadata.Playlist);
        var owners = replay.Players.Where(player => player.IsReplayOwner).Take(2).ToList();
        var owner = owners.Count == 1 ? owners[0] : null;
        var hasAuthoritativeOwner = owner != null &&
                                    !string.IsNullOrWhiteSpace(replay.OwnerId) &&
                                    string.Equals(owner.StableId, replay.OwnerId, StringComparison.OrdinalIgnoreCase) &&
                                    replay.OwnerTeamIndex.HasValue &&
                                    owner.TeamIndexValue == replay.OwnerTeamIndex &&
                                    !owner.HasConflictingTeamIndex;

        if (!hasAuthoritativeOwner)
        {
            return new SquadProjection
            {
                Status = SquadProjectionStatus.Unavailable,
                ExpectedTeamSize = expectedTeamSize
            };
        }

        var participants = replay.Players.Where(player => !player.IsNpc).ToList();
        var ambiguousStableIds = participants
            .Where(player => !string.IsNullOrWhiteSpace(player.StableId))
            .GroupBy(player => player.StableId!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Any(player => player.HasConflictingTeamIndex) ||
                            group.Where(player => player.TeamIndexValue.HasValue)
                                .Select(player => player.TeamIndexValue)
                                .Distinct()
                                .Take(2)
                                .Count() > 1 ||
                            group.Select(player => ParticipantClassifier.Classify(replay, player)).Distinct().Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var sameTeamParticipants = participants
            .Where(player => !player.HasConflictingTeamIndex &&
                             player.TeamIndexValue == replay.OwnerTeamIndex)
            .ToList();
        var observedTeamSize = sameTeamParticipants
            .Where(player => !string.IsNullOrWhiteSpace(player.StableId))
            .Where(player => !ambiguousStableIds.Contains(player.StableId!))
            .Select(player => player.StableId!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        var teammateGroups = participants
            .Where(player => !string.IsNullOrWhiteSpace(player.StableId) &&
                             !ambiguousStableIds.Contains(player.StableId) &&
                             ParticipantClassifier.Classify(replay, player) == ParticipantRelationship.Teammate)
            .GroupBy(player => player.StableId!, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var hasAnonymousTeammate = sameTeamParticipants.Any(player =>
            !player.IsReplayOwner && string.IsNullOrWhiteSpace(player.StableId));
        var hasAmbiguousTeammate = participants.Any(player =>
            !string.IsNullOrWhiteSpace(player.StableId) &&
            ambiguousStableIds.Contains(player.StableId) &&
            player.TeamIndexValue == replay.OwnerTeamIndex);
        int? memberObservedTeamSize = hasAnonymousTeammate || hasAmbiguousTeammate
            ? null
            : observedTeamSize;

        var teammates = teammateGroups
            .Select(group => CreateMember(group, memberObservedTeamSize))
            .OrderBy(member => member.Name ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(member => member.Name ?? "", StringComparer.Ordinal)
            .ThenBy(member => member.StableId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(member => member.StableId, StringComparer.Ordinal)
            .ToList();

        var teamKillValues = sameTeamParticipants
            .Where(player => string.IsNullOrWhiteSpace(player.StableId) ||
                             !ambiguousStableIds.Contains(player.StableId))
            .Select(player => ParseNonNegativeInt(player.TeamKills))
            .OfType<int>()
            .Distinct()
            .Take(2)
            .ToList();
        var hasConflictingTeamKills = teamKillValues.Count > 1;
        int? teamKills = teamKillValues.Count == 1 ? teamKillValues[0] : null;

        var definitiveTeamSize = 1 + teammates.Count;

        SquadProjectionStatus status;
        if (expectedTeamSize == 1 &&
            observedTeamSize == 1 &&
            definitiveTeamSize == 1 &&
            !hasAnonymousTeammate &&
            !hasAmbiguousTeammate)
        {
            status = SquadProjectionStatus.ConfirmedSolo;
        }
        else if (!expectedTeamSize.HasValue)
        {
            status = teammates.Count > 0
                ? SquadProjectionStatus.Partial
                : SquadProjectionStatus.Unavailable;
        }
        else if (expectedTeamSize > 1 &&
                 definitiveTeamSize == expectedTeamSize &&
                 observedTeamSize == expectedTeamSize &&
                 !hasAnonymousTeammate &&
                 !hasAmbiguousTeammate)
        {
            status = SquadProjectionStatus.Complete;
        }
        else
        {
            status = SquadProjectionStatus.Partial;
        }

        return new SquadProjection
        {
            Status = status,
            ExpectedTeamSize = expectedTeamSize,
            ObservedTeamSize = observedTeamSize,
            TeamKills = teamKills,
            HasConflictingTeamKills = hasConflictingTeamKills,
            Teammates = teammates
        };
    }

    private static SquadMemberSummary CreateMember(
        IGrouping<string, PlayerRow> group,
        int? observedTeamSize)
    {
        return new SquadMemberSummary
        {
            StableId = group.Key,
            Name = SelectDisplayName(group.Select(player => player.Name)),
            Kills = SelectUniqueInt(group.Select(player => player.Kills)),
            Level = SelectUniqueInt(group.Select(player => player.Level)),
            Platform = SelectUniqueText(group.Select(player => player.Platform)),
            Placement = SelectUniqueInt(group.Select(player => player.Placement)),
            ObservedSquadSize = observedTeamSize,
            IsBot = SelectUniqueBool(group.Select(player => player.Bot))
        };
    }

    private static string? SelectDisplayName(IEnumerable<string?> values)
    {
        return values
            .Where(value => !string.IsNullOrWhiteSpace(value) &&
                            !string.Equals(value, "unknown", StringComparison.OrdinalIgnoreCase))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value, StringComparer.Ordinal)
            .LastOrDefault();
    }

    private static int? SelectUniqueInt(IEnumerable<string?> values)
    {
        var parsed = values.Select(ParseNonNegativeInt).OfType<int>().Distinct().Take(2).ToList();
        return parsed.Count == 1 ? parsed[0] : null;
    }

    private static string? SelectUniqueText(IEnumerable<string?> values)
    {
        var observed = values
            .Where(value => !string.IsNullOrWhiteSpace(value) &&
                            !string.Equals(value, "unknown", StringComparison.OrdinalIgnoreCase))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToList();
        return observed.Count == 1 ? observed[0] : null;
    }

    private static bool? SelectUniqueBool(IEnumerable<string?> values)
    {
        var observed = values
            .Select(value => bool.TryParse(value, out var parsed) ? parsed : (bool?)null)
            .OfType<bool>()
            .Distinct()
            .Take(2)
            .ToList();
        return observed.Count == 1 ? observed[0] : null;
    }

    private static int? ParseNonNegativeInt(string? value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
               parsed >= 0
            ? parsed
            : null;
    }
}
