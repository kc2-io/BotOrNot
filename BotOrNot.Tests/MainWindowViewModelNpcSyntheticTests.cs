using System.Reactive.Threading.Tasks;
using BotOrNot.Avalonia.Services;
using BotOrNot.Avalonia.ViewModels;
using BotOrNot.Core.Models;
using BotOrNot.Core.Services;

namespace BotOrNot.Tests;

[TestFixture]
public class MainWindowViewModelNpcSyntheticTests
{
    private string _settingsPath = null!;

    [SetUp]
    public void SetUp() =>
        _settingsPath = Path.Combine(Path.GetTempPath(), $"botornot-npc-synthetic-{Guid.NewGuid():N}.json");

    [TearDown]
    public void TearDown() => File.Delete(_settingsPath);

    private static PlayerRow Human(string name, string platform = "WIN", string? teamIndex = null)
        => new()
        {
            StableId = $"id-{name}",
            Id = $"id-{name}",
            Name = name,
            Platform = platform,
            TeamIndex = teamIndex,
            Bot = "false"
        };

    private static PlayerRow Bot(string name, string? teamIndex = null)
        => new()
        {
            StableId = $"id-{name}",
            Id = $"id-{name}",
            Name = name,
            TeamIndex = teamIndex,
            Bot = "true"
        };

    private static PlayerRow Npc(string name)
        => new()
        {
            Id = name,
            Name = name,
            Bot = "true"
        };

    private static ReplayData BuildData(List<PlayerRow> players, List<PlayerRow>? ownerEliminations = null)
        => new()
        {
            Players = players,
            OwnerEliminations = ownerEliminations ?? new(),
            OwnerName = "Owner",
            OwnerKills = ownerEliminations?.Count(player => !player.IsNpc),
            Metadata = new ReplayMetadata { FileName = "test.replay" }
        };

    [Test]
    public async Task Npcs_KilledByOwner_MarkedInNpcGrid()
    {
        var wolf = Npc("Wolf");
        var boss = Npc("Boss");
        var data = BuildData(
            new List<PlayerRow> { Human("Alice"), wolf, boss },
            new List<PlayerRow> { Human("Victim"), Npc("wolf") });

        var viewModel = CreateViewModel(new FixedReplayService(data));
        await viewModel.LoadReplayCommand.Execute("test.replay").ToTask();

        Assert.That(viewModel.Npcs, Has.Count.EqualTo(2));
        var npcByName = viewModel.Npcs.ToDictionary(n => n.Name!);
        Assert.That(npcByName["Wolf"].KilledByOwner, Is.True);
        Assert.That(npcByName["Boss"].KilledByOwner, Is.False);
    }

    [Test]
    public async Task Partitioning_SeparatesPlayersAndNpcs()
    {
        var data = BuildData(new List<PlayerRow>
        {
            Human("Alice"),
            Bot("Bot1"),
            Npc("Wolf"),
            Npc("Boss")
        });

        var viewModel = CreateViewModel(new FixedReplayService(data));
        await viewModel.LoadReplayCommand.Execute("test.replay").ToTask();

        Assert.That(viewModel.Players, Has.Count.EqualTo(2));
        Assert.That(viewModel.Npcs, Has.Count.EqualTo(2));
        Assert.That(viewModel.Players, Is.All.Matches<PlayerRow>(p => !p.IsNpc));
        Assert.That(viewModel.Npcs, Is.All.Matches<PlayerRow>(p => p.IsNpc));
    }

    [Test]
    public async Task OwnerEliminations_ExcludeNpcs()
    {
        var data = BuildData(new List<PlayerRow>
        {
            Human("Alice"),
            Npc("Wolf")
        }, new List<PlayerRow>
        {
            Human("Victim"),
            Npc("Wolf")
        });

        var viewModel = CreateViewModel(new FixedReplayService(data));
        await viewModel.LoadReplayCommand.Execute("test.replay").ToTask();

        Assert.That(viewModel.OwnerEliminations, Has.Count.EqualTo(1));
        Assert.That(viewModel.OwnerEliminations[0].Name, Is.EqualTo("Victim"));
        Assert.That(viewModel.OwnerKillsHeader, Is.EqualTo("Owner's Eliminations (1) - 1 Players, 0 Bots"));
        Assert.That(viewModel.ElimsSummary, Is.EqualTo("1 Elims (0 Bots)"));
    }

    [Test]
    public async Task HeaderCounts_ExcludeNpcs()
    {
        var data = BuildData(new List<PlayerRow>
        {
            Human("Alice", platform: "WIN"),
            Human("Bob", platform: "PS5"),
            Bot("Bot1"),
            Npc("Wolf"),
            Npc("Boss")
        });

        var viewModel = CreateViewModel(new FixedReplayService(data));
        await viewModel.LoadReplayCommand.Execute("test.replay").ToTask();

        Assert.That(viewModel.PlayersSeenHeader, Is.EqualTo("Players Seen (3) - 2 Players, 1 Bots | 1 PC, 1 PlayStation"));
        Assert.That(viewModel.NpcsSeenHeader, Is.EqualTo("NPCs Seen (2)"));
    }

    [Test]
    public async Task Reload_FromReplayWithNpcsToReplayWithoutNpcs_HidesNpcsSection()
    {
        var withNpcs = BuildData(new List<PlayerRow> { Human("Alice"), Npc("Wolf") });
        var withoutNpcs = BuildData(new List<PlayerRow> { Human("Bob") });

        var service = new ToggleReplayService(withNpcs, withoutNpcs);
        var viewModel = CreateViewModel(service);

        await viewModel.LoadReplayCommand.Execute("with.replay").ToTask();
        Assert.That(viewModel.HasNpcs, Is.True);

        await viewModel.LoadReplayCommand.Execute("without.replay").ToTask();
        Assert.That(viewModel.HasNpcs, Is.False);
        Assert.That(viewModel.Npcs, Is.Empty);
        Assert.That(viewModel.NpcsSeenHeader, Is.EqualTo("NPCs Seen (0)"));
    }

    [Test]
    public async Task FailedReload_ClearsPreviousState()
    {
        var withNpcs = BuildData(new List<PlayerRow> { Human("Alice"), Npc("Wolf") });
        var service = new ThrowingAfterFirstReplayService(withNpcs, new InvalidOperationException("parse failed"));
        var viewModel = CreateViewModel(service);

        await viewModel.LoadReplayCommand.Execute("good.replay").ToTask();
        await viewModel.LoadReplayCommand.Execute("bad.replay").ToTask();

        Assert.That(viewModel.ErrorMessage, Is.Not.Null);
        Assert.That(viewModel.HasData, Is.False);
        Assert.That(viewModel.Players, Is.Empty);
        Assert.That(viewModel.Npcs, Is.Empty);
        Assert.That(viewModel.HasNpcs, Is.False);
        Assert.That(viewModel.PlayersSeenHeader, Is.EqualTo("Players Seen"));
        Assert.That(viewModel.NpcsSeenHeader, Is.EqualTo("NPCs Seen"));
    }

    [Test]
    public void NpcClassification_RequiresParserBotEvidenceWhenStableIdentityIsMissing()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Npc("Wolf").IsNpc, Is.True);
            Assert.That(Bot("BattleRoyaleBot").IsNpc, Is.False,
                "An AI player with a stable account ID must remain a player.");
            Assert.That(new PlayerRow { Id = "Anonymous", Name = "Anonymous", Bot = "false" }.IsNpc, Is.False,
                "A name-derived display ID alone must not classify a human as an NPC.");
        });
    }

    private MainWindowViewModel CreateViewModel(IReplayService replayService) => new(
        replayService: replayService,
        themeService: new ThemeService(new SettingsService(_settingsPath)));

    private sealed class FixedReplayService(ReplayData data) : IReplayService
    {
        public Task<ReplayData> LoadReplayAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(data);
    }

    private class ToggleReplayService : IReplayService
    {
        private readonly ReplayData _first;
        private readonly ReplayData _second;
        private bool _toggled;

        public ToggleReplayService(ReplayData first, ReplayData second)
        {
            _first = first;
            _second = second;
        }

        public Task<ReplayData> LoadReplayAsync(string path, CancellationToken cancellationToken = default)
        {
            var data = _toggled ? _second : _first;
            _toggled = !_toggled;
            return Task.FromResult(data);
        }
    }

    private class ThrowingAfterFirstReplayService : IReplayService
    {
        private readonly ReplayData _first;
        private readonly Exception _exception;
        private bool _thrown;

        public ThrowingAfterFirstReplayService(ReplayData first, Exception exception)
        {
            _first = first;
            _exception = exception;
        }

        public Task<ReplayData> LoadReplayAsync(string path, CancellationToken cancellationToken = default)
        {
            if (_thrown)
                throw _exception;

            _thrown = true;
            return Task.FromResult(_first);
        }
    }
}
