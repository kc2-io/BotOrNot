using System.Reactive.Threading.Tasks;
using BotOrNot.Avalonia.Services;
using BotOrNot.Avalonia.ViewModels;
using BotOrNot.Core.Models;
using BotOrNot.Core.Services;

namespace BotOrNot.Tests;

[TestFixture]
public class MainWindowViewModelNpcTests
{
    private const int ExpectedNpcCountForReplayWithNpcs = 15;
    private string _settingsPath = null!;

    [SetUp]
    public void SetUp() =>
        _settingsPath = Path.Combine(Path.GetTempPath(), $"botornot-npc-replay-{Guid.NewGuid():N}.json");

    [TearDown]
    public void TearDown() => File.Delete(_settingsPath);

    private static string ReplayWithNpcs => Path.Combine(
        TestContext.CurrentContext.TestDirectory,
        "TestData",
        "UnsavedReplay-2026.06.10-06.22.43.replay");

    private static string ReplayWithoutNpcs => Path.Combine(
        TestContext.CurrentContext.TestDirectory,
        "TestData",
        "Reload_PunchBerryDuo_Owner_Elim_5_Team_Elim_1_Place_1.replay");

    [Test]
    public async Task LoadReplay_PartitionsNpcsCorrectly()
    {
        var viewModel = CreateViewModel();
        await viewModel.LoadReplayCommand.Execute(ReplayWithNpcs).ToTask();

        Assert.That(viewModel.HasData, Is.True);
        Assert.That(viewModel.Players, Is.All.Matches<PlayerRow>(p => !p.IsNpc), "Players grid should not contain NPCs");
        Assert.That(viewModel.Npcs, Is.All.Matches<PlayerRow>(p => p.IsNpc), "NPCs grid should contain only NPCs");
        Assert.That(viewModel.Npcs.Count, Is.EqualTo(ExpectedNpcCountForReplayWithNpcs), "Unexpected NPC count");
        Assert.That(viewModel.Players.Count + viewModel.Npcs.Count, Is.EqualTo(115), "Players + NPCs should equal total replay players");
        Assert.That(viewModel.NpcsSeenHeader, Is.EqualTo($"NPCs Seen ({viewModel.Npcs.Count})"));
    }

    [Test]
    public async Task LoadReplay_WithoutNpcs_HidesNpcsSection()
    {
        var viewModel = CreateViewModel();
        await viewModel.LoadReplayCommand.Execute(ReplayWithoutNpcs).ToTask();

        Assert.That(viewModel.HasData, Is.True);
        Assert.That(viewModel.Npcs, Is.Empty);
        Assert.That(viewModel.HasNpcs, Is.False);
    }

    [Test]
    public async Task Filter_AppliesToNpcs()
    {
        var viewModel = CreateViewModel();
        await viewModel.LoadReplayCommand.Execute(ReplayWithNpcs).ToTask();

        var firstNpcName = viewModel.Npcs.First().Name;
        Assume.That(firstNpcName, Is.Not.Null.And.Not.Empty);

        viewModel.FilterText = firstNpcName;

        Assert.That(viewModel.Npcs, Is.All.Matches<PlayerRow>(p =>
            (p.Name?.Contains(firstNpcName, StringComparison.OrdinalIgnoreCase) ?? false)));
        Assert.That(viewModel.Npcs.Any(p => p.Name == firstNpcName), Is.True);
    }

    [Test]
    public async Task Filter_MatchingNoNpcs_KeepsNpcsSectionVisible()
    {
        var viewModel = CreateViewModel();
        await viewModel.LoadReplayCommand.Execute(ReplayWithNpcs).ToTask();

        viewModel.FilterText = "zzzznomatch";

        Assert.That(viewModel.Npcs, Is.Empty, "Filtered NPCs should be empty");
        Assert.That(viewModel.HasNpcs, Is.True, "NPCs section should remain visible because the replay contains NPCs");
    }

    [Test]
    public async Task OwnerEliminations_FromRealReplay_ExcludeNpcs()
    {
        var viewModel = CreateViewModel();
        await viewModel.LoadReplayCommand.Execute(ReplayWithNpcs).ToTask();

        Assert.That(viewModel.OwnerEliminations, Is.All.Matches<PlayerRow>(p => !p.IsNpc), "Owner eliminations should not contain NPCs");
    }

    [Test]
    public async Task HeaderCounts_FromRealReplay_ExcludeNpcs()
    {
        var viewModel = CreateViewModel();
        await viewModel.LoadReplayCommand.Execute(ReplayWithNpcs).ToTask();

        Assert.That(viewModel.PlayersSeenHeader, Does.Contain($"Players Seen ({viewModel.Players.Count})"));
        Assert.That(viewModel.NpcsSeenHeader, Does.Contain($"NPCs Seen ({viewModel.Npcs.Count})"));
        Assert.That(viewModel.PlayersSeenHeader, Does.Not.Contain("NPCs Seen"));
    }

    [Test]
    public async Task Npcs_HaveZeroSquadSize()
    {
        var viewModel = CreateViewModel();
        await viewModel.LoadReplayCommand.Execute(ReplayWithNpcs).ToTask();

        Assert.That(viewModel.Npcs, Is.Not.Empty);
        Assert.That(viewModel.Npcs, Is.All.Matches<PlayerRow>(p => p.SquadSize == 0), "NPCs should not contribute to squad size");
    }

    [Test]
    public async Task OwnerKills_AgreesWithNonNpcEliminations()
    {
        var service = new ReplayService();
        var data = await service.LoadReplayAsync(ReplayWithNpcs);

        Assert.That(data.OwnerKills, Is.Not.Null, "This replay must report an authoritative OwnerKills for the comparison to be meaningful");

        var nonNpcElimCount = data.OwnerEliminations.Count(p => !p.IsNpc);
        Assert.That(data.OwnerKills.Value, Is.EqualTo(nonNpcElimCount),
            "Fortnite's authoritative kill count should match the non-NPC elimination count; if it includes NPCs, the header and grid will disagree.");
    }

    private MainWindowViewModel CreateViewModel() => new(
        themeService: new ThemeService(new SettingsService(_settingsPath)));
}
