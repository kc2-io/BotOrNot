using FortniteReplayReader;
using Microsoft.Extensions.Logging;
using Unreal.Core.Models;
using Unreal.Core.Models.Enums;

namespace BotOrNot.Core.Services;

/// <summary>
/// Library-only profile. Full match loads always use the standard Normal reader.
/// Unknown groups remain enabled so additions do not silently discard possible evidence.
/// </summary>
internal sealed class SummaryReplayReader(ILogger logger) : ReplayReader(logger, ParseMode.Normal)
{
    // Decide before any exclusion. Unvalidated releases retain the full Normal decode,
    // so fallback never combines partially skipped data with a full projection.
    internal static bool SupportsRelease(int major, int minor) => (major, minor) is
        (39, 30) or (39, 40) or (41, 0) or (41, 10) or (42, 0) or (42, 10);

    protected override bool ShouldReadGroup(NetFieldExportGroup group) =>
        !SupportsRelease(Major, Minor) || group.PathName is not
        ("/Game/Athena/PlayerPawn_Athena.PlayerPawn_Athena_C" or
         "/Script/FortniteGame.FortPickupAthena" or
         "/Game/Athena/SafeZone/SafeZoneIndicator.SafeZoneIndicator_C");
}
