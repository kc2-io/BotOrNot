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
    protected override bool ShouldReadGroup(NetFieldExportGroup group) => group.PathName is not
        ("/Game/Athena/PlayerPawn_Athena.PlayerPawn_Athena_C" or
         "/Script/FortniteGame.FortPickupAthena" or
         "/Game/Athena/SafeZone/SafeZoneIndicator.SafeZoneIndicator_C");
}
