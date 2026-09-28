using System.Text.Json;
using BotOrNot.Core.Services;

namespace BotOrNot.Tests;

[TestFixture]
public sealed class ReplaySummaryProfileTests
{
    [TestCase(0, 0)]
    [TestCase(38, 0)]
    [TestCase(42, 20)]
    [TestCase(43, 0)]
    public void UnvalidatedRelease_RetainsNormalDecode(int major, int minor)
        => Assert.That(SummaryReplayReader.SupportsRelease(major, minor), Is.False);

    [Test]
    public async Task PrivateF1_SummaryPreservesEliminationCreditWhenAvailable()
    {
        var path = Environment.GetEnvironmentVariable("BOTORNOT_F1_REPLAY");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            Assert.Ignore("Private F1 replay is optional; set BOTORNOT_F1_REPLAY for local validation.");
        await AssertParity(path!);
    }

    [TestCase("Blitz_ForbiddenFruit_CalmSambucusBRSquad_Owner_Elim_1_Team_Elim_3_Place_3.replay")]
    [TestCase("Blitz_ForbiddenFruitNoBuildBRSquad_Owner_Elim_1_Team_Elim_12_Place_1.replay")]
    [TestCase("Reload_PunchBerryDuo_Owner_Elim_5_Team_Elim_1_Place_1.replay")]
    [TestCase("UnsavedReplay-2026.01.31-15.34.27.replay")]
    [TestCase("UnsavedReplay-2026.06.10-06.22.43.replay")]
    public async Task SummaryProfile_PreservesCompleteLibraryProjection(string fileName)
    {
        var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestData", fileName);
        await AssertParity(path);
    }

    private static async Task AssertParity(string path)
    {
        var service = new ReplayService();
        var full = ReplaySummaryFactory.Create(await service.LoadReplayAsync(path), new FileInfo(path));
        var summary = await service.LoadSummaryAsync(path);

        // Includes nullable analysis state, every opponent tuple, and computed UI properties.
        Assert.That(JsonSerializer.Serialize(summary), Is.EqualTo(JsonSerializer.Serialize(full)));
    }
}
