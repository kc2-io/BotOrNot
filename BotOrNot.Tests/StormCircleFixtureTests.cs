using BotOrNot.Core.Models;
using BotOrNot.Core.Services;
using FortniteReplayReader;
using Microsoft.Extensions.Logging.Abstractions;
using Unreal.Core.Models.Enums;

namespace BotOrNot.Tests;

[TestFixture]
public sealed class StormCircleFixtureTests
{
    private static IEnumerable<TestCaseData> Cases()
    {
        yield return new TestCaseData(
                "UnsavedReplay-2026.01.31-15.34.27.replay",
                new[] { 1, 2, 3, 4, 5, 6, 7, 8 },
                new[] { 140.011, 442.047, 632.030, 822.031, 1002.020, 1122.001, 1242.026, 1361.978 },
                6,
                new[] { 44, 23, 5, 2, 7, 4, 5, 7 })
            .SetName("BattleRoyale_UsesRecordedAbsolutePhases");

        yield return new TestCaseData(
                "Reload_PunchBerryDuo_Owner_Elim_5_Team_Elim_1_Place_1.replay",
                new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 },
                new[] { 80.072, 201.054, 311.039, 406.049, 501.043, 621.059, 741.064, 836.049, 916.049 },
                0,
                new[] { 15, 15, 16, 11, 7, 8, 1, 0, 3 })
            .SetName("Reload_UsesRecordedAbsolutePhases");
    }

    [TestCaseSource(nameof(Cases))]
    public void ParserAndResolver_UseCommonReplayClockAndExpectedPhases(
        string replayFile,
        int[] expectedPhases,
        double[] expectedReplayTimes,
        int expectedUnknownFinishes,
        int[] expectedFinishCountsByPhase)
    {
        var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestData", replayFile);
        var replay = new ReplayReader(NullLogger.Instance, ParseMode.Normal).ReadReplay(path);
        var zones = replay.MapData.SafeZones.ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(zones.Select(zone => zone.CurrentPhase), Is.EqualTo(expectedPhases));
            Assert.That(zones.Select(zone => Math.Round(zone.ReplayTimeSeconds!.Value, 3)),
                Is.EqualTo(expectedReplayTimes));
            Assert.That(zones.Select(zone => zone.PhaseCount), Is.All.EqualTo(13));
            Assert.That(zones.Select(zone => zone.ChannelIndex).Distinct().Count(), Is.EqualTo(1));
            Assert.That(zones.Select(zone => zone.ActorGuid), Is.All.Not.Null);
            Assert.That(zones.Select(zone => zone.ActorGuid).Distinct().Count(), Is.EqualTo(1));
            Assert.That(zones[0].PhaseCountWasExported, Is.True);
            Assert.That(zones.Skip(1).Select(zone => zone.PhaseCountWasExported), Is.All.False);
        });

        var resolver = new StormCircleResolver(zones.Select(zone => new StormCircleObservation(
            zone.ReplayTimeSeconds!.Value,
            zone.CurrentPhase,
            zone.PhaseCount,
            zone.ChannelIndex,
            zone.ActorGuid)));
        var resolutions = replay.Eliminations
            .Where(elimination => !elimination.Knocked)
            .Select(elimination => resolver.Resolve(elimination.Info.StartTime / 1000d))
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(resolutions.Count(result => result.Status == StormCircleStatus.Unknown),
                Is.EqualTo(expectedUnknownFinishes));
            for (var phase = 1; phase <= expectedFinishCountsByPhase.Length; phase++)
            {
                Assert.That(resolutions.Count(result => result.CircleNumber == phase),
                    Is.EqualTo(expectedFinishCountsByPhase[phase - 1]),
                    $"Unexpected finish count for recorded phase {phase}.");
            }
        });
    }
}
