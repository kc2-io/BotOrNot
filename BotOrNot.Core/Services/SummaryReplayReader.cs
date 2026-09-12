using FortniteReplayReader;
using Microsoft.Extensions.Logging;
using Unreal.Core;
using Unreal.Core.Models;
using Unreal.Core.Models.Enums;

namespace BotOrNot.Core.Services;

/// <summary>
/// Library-only profile. Full match loads always use the standard Normal reader.
/// Unknown groups remain enabled so additions do not silently discard possible evidence.
/// </summary>
// The parser explicitly supports a null logger. Unlike NullLogger, this also avoids
// allocating params arrays and boxed values for disabled messages in its hot loops.
internal class SummaryReplayReader(ILogger? logger = null) : ReplayReader(logger!, ParseMode.Normal)
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

    public override bool ReceivedReplicatorBunch(
        DataBunch bunch, FBitArchive archive, uint? repObject, bool bHasRepLayout)
    {
        var group = _netGuidCache.GetNetFieldExportGroup(repObject);
        if (group is null || ShouldReadGroup(group) ||
            bunch.ChIndex >= Channels.Length || Channels[bunch.ChIndex] is not { } channel ||
            channel.IsIgnoringGroup(group.PathName) || !_netFieldParser.WillReadType(group.PathName))
            return base.ReceivedReplicatorBunch(bunch, archive, repObject, bHasRepLayout);

        // A future parser may attach meaningful RPC/custom data to one of these groups.
        // Keep the framed field path whenever Normal would decode that class cache.
        if (_netGuidCache.TryGetClassNetCache(group.PathName, out var classCache,
                archive.EngineNetworkVersion >= EngineNetworkVersionHistory.HISTORY_CLASSNETCACHE_FULLNAME) &&
            _netFieldParser.WillReadClassNetCache(classCache.PathName))
            return base.ReceivedReplicatorBunch(bunch, archive, repObject, bHasRepLayout);

        // ProcessBunch has already read actor lifecycle/schema data and bounded this archive
        // to one CONTENT_BLOCK_PAYLOAD. Only opaque detail data remains, never a neighboring
        // actor block. External names are delivered independently of detail-property decoding.
        var remaining = archive.GetBitsLeft();
        if (archive.IsError || remaining < 0 || !archive.CanRead(remaining))
            return false;
        if (bHasRepLayout && !ReceiveExternalData(group, bunch.ChIndex))
            return false;
        archive.SkipBits(remaining);
        return !archive.IsError;
    }
}
