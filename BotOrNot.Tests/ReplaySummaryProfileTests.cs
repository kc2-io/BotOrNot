using System.Text.Json;
using BotOrNot.Core.Services;

namespace BotOrNot.Tests;

[TestFixture]
public sealed class ReplaySummaryProfileTests
{
    [TestCase("Blitz_ForbiddenFruit_CalmSambucusBRSquad_Owner_Elim_1_Team_Elim_3_Place_3.replay")]
    [TestCase("Blitz_ForbiddenFruitNoBuildBRSquad_Owner_Elim_1_Team_Elim_12_Place_1.replay")]
    [TestCase("Reload_PunchBerryDuo_Owner_Elim_5_Team_Elim_1_Place_1.replay")]
    [TestCase("UnsavedReplay-2026.01.31-15.34.27.replay")]
    [TestCase("UnsavedReplay-2026.06.10-06.22.43.replay")]
    public async Task SummaryProfile_PreservesCompleteLibraryProjection(string fileName)
    {
        var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestData", fileName);
        var service = new ReplayService();
        var full = ReplaySummaryFactory.Create(await service.LoadReplayAsync(path), new FileInfo(path));
        var summary = await service.LoadSummaryAsync(path);

        // Includes nullable analysis state, every opponent tuple, and computed UI properties.
        Assert.That(JsonSerializer.Serialize(summary), Is.EqualTo(JsonSerializer.Serialize(full)));
    }
}
