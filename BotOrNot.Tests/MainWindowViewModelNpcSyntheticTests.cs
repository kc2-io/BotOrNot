using System.Reactive.Threading.Tasks;
using BotOrNot.Avalonia.ViewModels;
using BotOrNot.Core.Models;
using BotOrNot.Core.Services;

namespace BotOrNot.Tests;

public class FakeReplayService : IReplayService
{
    private readonly ReplayData _data;

    public FakeReplayService(ReplayData data)
    {
        _data = data;
    }

    public Task<ReplayData> LoadReplayAsync(string path, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_data);
    }
}

[TestFixture]
public class MainWindowViewModelNpcSyntheticTests
{
    private static PlayerRow Human(string name, string platform = "WIN", string? teamIndex = null)
        => new()
        {
            Id = $"id-{name}",
            Name = name,
            Platform = platform,
            TeamIndex = teamIndex,
            Bot = "false"
        };

    private static PlayerRow Bot(string name, string? teamIndex = null)
        => new()
        {
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
            Metadata = new ReplayMetadata { FileName = "test.replay" }
        };

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

        var viewModel = new MainWindowViewModel(new FakeReplayService(data));
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

        var viewModel = new MainWindowViewModel(new FakeReplayService(data));
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

        var viewModel = new MainWindowViewModel(new FakeReplayService(data));
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
        var viewModel = new MainWindowViewModel(service);

        await viewModel.LoadReplayCommand.Execute("with.replay").ToTask();
        Assert.That(viewModel.HasNpcs, Is.True);

        await viewModel.LoadReplayCommand.Execute("without.replay").ToTask();
        Assert.That(viewModel.HasNpcs, Is.False);
        Assert.That(viewModel.Npcs, Is.Empty);
        Assert.That(viewModel.NpcsSeenHeader, Is.EqualTo("NPCs Seen (0)"));
    }

    [Test]
    public async Task FailedReload_KeepsPreviousState()
    {
        var withNpcs = BuildData(new List<PlayerRow> { Human("Alice"), Npc("Wolf") });
        var service = new ThrowingAfterFirstReplayService(withNpcs, new InvalidOperationException("parse failed"));
        var viewModel = new MainWindowViewModel(service);

        await viewModel.LoadReplayCommand.Execute("good.replay").ToTask();
        var previousPlayersSeenHeader = viewModel.PlayersSeenHeader;
        var previousHasNpcs = viewModel.HasNpcs;
        var previousNpcCount = viewModel.Npcs.Count;
        var previousPlayerCount = viewModel.Players.Count;

        await viewModel.LoadReplayCommand.Execute("bad.replay").ToTask();

        Assert.That(viewModel.ErrorMessage, Is.Not.Null);
        Assert.That(viewModel.HasData, Is.True);
        Assert.That(viewModel.Players.Count, Is.EqualTo(previousPlayerCount));
        Assert.That(viewModel.Npcs.Count, Is.EqualTo(previousNpcCount));
        Assert.That(viewModel.HasNpcs, Is.EqualTo(previousHasNpcs));
        Assert.That(viewModel.PlayersSeenHeader, Is.EqualTo(previousPlayersSeenHeader));
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
