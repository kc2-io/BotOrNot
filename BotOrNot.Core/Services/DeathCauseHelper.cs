using BotOrNot.Core.Models;

namespace BotOrNot.Core.Services;

public static class DeathCauseHelper
{
    private sealed record CauseMapping(string Label, DeathCauseCategory Category, bool IsResolvable = true);
    private sealed record TagMapping(string Fragment, string Label, DeathCauseCategory Category);

    private static readonly TagMapping[] SpecificTagMappings =
    [
        new("Area51Gun", "Arc Gun", DeathCauseCategory.PlayerWeapon),
        new("Gameplay.Damage.DeployableTurret.Shot", "Turret", DeathCauseCategory.PlayerWeapon)
    ];

    private static readonly Dictionary<int, CauseMapping> DeathCauses = new()
    {
        { 0, new("Storm", DeathCauseCategory.Environment) },
        { 1, new("Fall Damage", DeathCauseCategory.Environment) },
        { 2, new("Pistol", DeathCauseCategory.PlayerWeapon) },
        { 3, new("Shotgun", DeathCauseCategory.PlayerWeapon) },
        { 4, new("Rifle", DeathCauseCategory.PlayerWeapon) },
        { 5, new("SMG", DeathCauseCategory.PlayerWeapon) },
        { 6, new("Sniper", DeathCauseCategory.PlayerWeapon) },
        { 7, new("Sniper No Scope", DeathCauseCategory.PlayerWeapon) },
        { 8, new("Melee", DeathCauseCategory.PlayerWeapon) },
        { 9, new("Infinity Blade", DeathCauseCategory.PlayerWeapon) },
        { 10, new("Grenade", DeathCauseCategory.PlayerWeapon) },
        { 11, new("C4", DeathCauseCategory.PlayerWeapon) },
        { 12, new("Grenade Launcher", DeathCauseCategory.PlayerWeapon) },
        { 13, new("Rocket Launcher", DeathCauseCategory.PlayerWeapon) },
        { 14, new("Minigun", DeathCauseCategory.PlayerWeapon) },
        { 15, new("Bow", DeathCauseCategory.PlayerWeapon) },
        { 16, new("Trap", DeathCauseCategory.PlayerWeapon) },
        { 17, new("Bled Out", DeathCauseCategory.Environment) },
        { 18, new("Banhammer", DeathCauseCategory.PlayerWeapon) },
        { 19, new("Removed From Game", DeathCauseCategory.NonCombat) },
        { 20, new("Boss Melee", DeathCauseCategory.PlayerWeapon) },
        { 21, new("Boss Dive Attack", DeathCauseCategory.PlayerWeapon) },
        { 22, new("Boss Ranged", DeathCauseCategory.PlayerWeapon) },
        { 23, new("Vehicle", DeathCauseCategory.PlayerWeapon) },
        { 24, new("Shopping Cart", DeathCauseCategory.PlayerWeapon) },
        { 25, new("ATK", DeathCauseCategory.PlayerWeapon) },
        { 26, new("Quad Crasher", DeathCauseCategory.PlayerWeapon) },
        { 27, new("Biplane", DeathCauseCategory.PlayerWeapon) },
        { 28, new("Biplane Gun", DeathCauseCategory.PlayerWeapon) },
        { 29, new("LMG", DeathCauseCategory.PlayerWeapon) },
        { 30, new("Stink Bomb", DeathCauseCategory.PlayerWeapon) },
        { 31, new("Environmental", DeathCauseCategory.Environment) },
        { 32, new("Fell Out Of World", DeathCauseCategory.Environment) },
        { 33, new("Under Landscape", DeathCauseCategory.Environment) },
        { 34, new("Turret", DeathCauseCategory.PlayerWeapon) },
        { 35, new("Ship Cannon", DeathCauseCategory.PlayerWeapon) },
        { 36, new("Cube", DeathCauseCategory.Environment) },
        { 37, new("Balloon", DeathCauseCategory.Environment) },
        { 38, new("Storm Surge", DeathCauseCategory.Environment) },
        { 39, new("Lava", DeathCauseCategory.Environment) },
        { 40, new("Zombie", DeathCauseCategory.Environment) },
        { 41, new("Elite Zombie", DeathCauseCategory.Environment) },
        { 42, new("Ranged Zombie", DeathCauseCategory.Environment) },
        { 43, new("Brute", DeathCauseCategory.Environment) },
        { 44, new("Elite Brute", DeathCauseCategory.Environment) },
        { 45, new("Mega Brute", DeathCauseCategory.Environment) },
        { 46, new("Switched To Spectate", DeathCauseCategory.NonCombat) },
        { 47, new("Logged Out", DeathCauseCategory.NonCombat) },
        { 48, new("Team Switch", DeathCauseCategory.NonCombat) },
        { 49, new("Won Match", DeathCauseCategory.NonCombat) },
        { 50, new("Unspecified", DeathCauseCategory.Unknown, false) },
        { 51, new("MAX", DeathCauseCategory.Unknown, false) }
    };

    public static DeathCauseInfo ResolveEvent(int? eventCode, int? killFeedCode, IEnumerable<string>? deathTags)
    {
        var tags = NormalizeTags(deathTags);
        var tagMappings = ResolveSpecificTags(tags);
        var eventMapping = ResolveCode(eventCode);
        var killFeedMapping = ResolveCode(killFeedCode);

        if (tagMappings.Count > 1)
        {
            return new DeathCauseInfo
            {
                DisplayName = "Unknown",
                RawEventCode = eventCode,
                RawKillFeedCode = killFeedCode,
                RawTags = tags,
                TagRole = DeathCauseTagRole.KillFeedDeathContext,
                Source = DeathCauseSource.SpecificEventTag,
                ResolutionStatus = DeathCauseResolutionStatus.Conflicting,
                Confidence = EvidenceConfidence.Medium
            };
        }

        if (tagMappings.Count == 1)
        {
            var tagMapping = tagMappings[0];
            var numericCategory = eventMapping is { IsResolvable: true }
                ? eventMapping.Category
                : killFeedMapping is { IsResolvable: true }
                    ? killFeedMapping.Category
                    : DeathCauseCategory.Unknown;
            return new DeathCauseInfo
            {
                Category = tagMapping.Category,
                DisplayName = tagMapping.Label,
                RawEventCode = eventCode,
                RawKillFeedCode = killFeedCode,
                RawTags = tags,
                TagRole = DeathCauseTagRole.KillFeedDeathContext,
                Source = DeathCauseSource.SpecificEventTag,
                ResolutionStatus = numericCategory != DeathCauseCategory.Unknown && numericCategory != tagMapping.Category
                    ? DeathCauseResolutionStatus.Conflicting
                    : DeathCauseResolutionStatus.Resolved,
                // The tag belongs to a uniquely correlated player-state frame, not the
                // event chunk itself, so keep correlation confidence explicit.
                Confidence = EvidenceConfidence.Medium
            };
        }

        if (eventMapping is { IsResolvable: true })
        {
            var conflicts = killFeedMapping is { IsResolvable: true } &&
                            !killFeedMapping.Label.Equals(eventMapping.Label, StringComparison.OrdinalIgnoreCase);
            return FromMapping(eventMapping, eventCode, killFeedCode, tags,
                DeathCauseSource.EliminationEventCode,
                conflicts ? DeathCauseResolutionStatus.Conflicting : DeathCauseResolutionStatus.Resolved,
                EvidenceConfidence.Medium, DeathCauseTagRole.KillFeedDeathContext);
        }

        if (killFeedMapping is { IsResolvable: true })
        {
            return FromMapping(killFeedMapping, eventCode, killFeedCode, tags,
                DeathCauseSource.KillFeedCode, DeathCauseResolutionStatus.Resolved,
                EvidenceConfidence.High, DeathCauseTagRole.KillFeedDeathContext);
        }

        var unknownCode = eventCode ?? killFeedCode;
        return new DeathCauseInfo
        {
            DisplayName = FormatCode(unknownCode),
            RawEventCode = eventCode,
            RawKillFeedCode = killFeedCode,
            RawTags = tags,
            TagRole = tags.Count == 0 ? DeathCauseTagRole.None : DeathCauseTagRole.KillFeedDeathContext,
            Source = DeathCauseSource.None,
            ResolutionStatus = DeathCauseResolutionStatus.Unknown,
            Confidence = unknownCode.HasValue || tags.Count > 0 ? EvidenceConfidence.Medium : EvidenceConfidence.None
        };
    }

    public static DeathCauseInfo ResolveLegacy(string? deathCauseValue, IEnumerable<string>? deathTags)
    {
        var tags = NormalizeTags(deathTags);
        var tagMappings = ResolveSpecificTags(tags);
        int? code = int.TryParse(deathCauseValue, out var parsedCode) ? parsedCode : null;
        var codeMapping = ResolveCode(code);

        if (tagMappings.Count > 1)
        {
            return new DeathCauseInfo
            {
                DisplayName = "Unknown",
                RawKillFeedCode = code,
                RawTags = tags,
                TagRole = DeathCauseTagRole.LegacyPlayerSnapshot,
                Source = DeathCauseSource.LegacyPlayerSnapshotTag,
                ResolutionStatus = DeathCauseResolutionStatus.Conflicting,
                Confidence = EvidenceConfidence.Low
            };
        }

        if (tagMappings.Count == 1)
        {
            var tagMapping = tagMappings[0];
            return new DeathCauseInfo
            {
                Category = tagMapping.Category,
                DisplayName = tagMapping.Label,
                RawKillFeedCode = code,
                RawTags = tags,
                TagRole = DeathCauseTagRole.LegacyPlayerSnapshot,
                Source = DeathCauseSource.LegacyPlayerSnapshotTag,
                ResolutionStatus = codeMapping is { IsResolvable: true } && codeMapping.Category != tagMapping.Category
                    ? DeathCauseResolutionStatus.Conflicting
                    : DeathCauseResolutionStatus.Resolved,
                Confidence = EvidenceConfidence.Low
            };
        }

        if (codeMapping is { IsResolvable: true })
        {
            return FromMapping(codeMapping, null, code, tags,
                DeathCauseSource.LegacyPlayerSnapshot, DeathCauseResolutionStatus.Resolved,
                EvidenceConfidence.Low, DeathCauseTagRole.LegacyPlayerSnapshot);
        }

        return new DeathCauseInfo
        {
            DisplayName = code.HasValue ? FormatCode(code) :
                string.IsNullOrWhiteSpace(deathCauseValue) ? "Unknown" : deathCauseValue,
            RawKillFeedCode = code,
            RawTags = tags,
            TagRole = tags.Count == 0 ? DeathCauseTagRole.None : DeathCauseTagRole.LegacyPlayerSnapshot,
            Source = string.IsNullOrWhiteSpace(deathCauseValue) && tags.Count == 0
                ? DeathCauseSource.None
                : DeathCauseSource.LegacyPlayerSnapshot,
            ResolutionStatus = DeathCauseResolutionStatus.Unknown,
            Confidence = string.IsNullOrWhiteSpace(deathCauseValue) && tags.Count == 0
                ? EvidenceConfidence.None
                : EvidenceConfidence.Low
        };
    }

    public static string GetDisplayName(string? deathCauseValue) => ResolveLegacy(deathCauseValue, null).DisplayName;
    public static string GetDisplayName(string? deathCauseValue, IEnumerable<string>? deathTags) =>
        ResolveLegacy(deathCauseValue, deathTags).DisplayName;

    private static DeathCauseInfo FromMapping(
        CauseMapping mapping, int? eventCode, int? killFeedCode, IReadOnlyList<string> tags,
        DeathCauseSource source, DeathCauseResolutionStatus status, EvidenceConfidence confidence,
        DeathCauseTagRole tagRole) => new()
    {
        Category = mapping.Category,
        DisplayName = $"{mapping.Label} ({(source is DeathCauseSource.KillFeedCode or DeathCauseSource.LegacyPlayerSnapshot ? killFeedCode : eventCode ?? killFeedCode)})",
        RawEventCode = eventCode,
        RawKillFeedCode = killFeedCode,
        RawTags = tags,
        TagRole = tags.Count == 0 ? DeathCauseTagRole.None : tagRole,
        Source = source,
        ResolutionStatus = status,
        Confidence = confidence
    };

    private static CauseMapping? ResolveCode(int? code) =>
        code.HasValue && DeathCauses.TryGetValue(code.Value, out var mapping) ? mapping : null;

    private static string FormatCode(int? code)
    {
        if (!code.HasValue) return "Unknown";
        return DeathCauses.TryGetValue(code.Value, out var mapping)
            ? $"{mapping.Label} ({code.Value})"
            : $"Unknown ({code.Value})";
    }

    private static IReadOnlyList<TagMapping> ResolveSpecificTags(IReadOnlyList<string> tags)
    {
        return SpecificTagMappings.Where(mapping => tags.Any(tag => TagMatches(tag, mapping.Fragment)))
            .DistinctBy(mapping => mapping.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool TagMatches(string tag, string mapping)
    {
        if (mapping.Contains('.'))
            return tag.Equals(mapping, StringComparison.OrdinalIgnoreCase);

        return tag.StartsWith("Item.Weapon.", StringComparison.OrdinalIgnoreCase) &&
               tag.Split('.').Any(segment => segment.Equals(mapping, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> NormalizeTags(IEnumerable<string>? tags)
    {
        if (tags == null) return Array.Empty<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<string>();
        foreach (var tag in tags)
        {
            if (string.IsNullOrWhiteSpace(tag)) continue;
            var trimmed = tag.Trim();
            if (seen.Add(trimmed)) normalized.Add(trimmed);
        }
        return normalized;
    }
}
