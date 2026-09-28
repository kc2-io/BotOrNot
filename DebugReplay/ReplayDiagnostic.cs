using System.Text.Json;
using BotOrNot.Core.Services;
using FortniteReplayReader;
using Microsoft.Extensions.Logging;
using Unreal.Core.Models.Enums;

internal static class ReplayDiagnostic
{
    public static async Task RunAsync(string replayPath, string? outputPath)
    {
        var logger = new AggregateLogger();
        var reader = new ReplayReader(logger, ParseMode.Normal);
        var readTask = Task.Run(() => reader.ReadReplay(replayPath));
        var completed = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(90)));

        object report;
        if (completed != readTask)
        {
            report = new
            {
                File = Path.GetFileName(replayPath),
                ParseMode = ParseMode.Normal.ToString(),
                Completed = false,
                Error = "Parser timeout after 90 seconds"
            };
        }
        else
        {
            try
            {
                var replay = await readTask;
                var players = replay.PlayerData?.ToArray() ?? [];
                var application = await new ReplayService().LoadReplayAsync(replayPath);
                report = new
                {
                    File = Path.GetFileName(replayPath),
                    ParseMode = ParseMode.Normal.ToString(),
                    Completed = true,
                    Header = new
                    {
                        replay.Info.LengthInMs,
                        replay.Header.Branch,
                        replay.Header.Changelist,
                        replay.Header.EngineNetworkVersion,
                        replay.Header.GameNetworkProtocolVersion
                    },
                    GameData = new
                    {
                        replay.GameData?.CurrentPlaylist,
                        RecorderIdPresent = replay.GameData?.RecorderId is not null,
                        replay.GameData?.MaxPlayers,
                        replay.GameData?.WinningTeam
                    },
                    PlayerCount = players.Length,
                    OwnerFlags = players.Count(player => player.IsReplayOwner),
                    MissingFields = new
                    {
                        PlayerId = players.Count(player => player.PlayerId is null),
                        Level = players.Count(player => player.Level is null),
                        Kills = players.Count(player => player.Kills is null),
                        Platform = players.Count(player => string.IsNullOrEmpty(player.Platform))
                    },
                    Application = new
                    {
                        OwnerPresent = !string.IsNullOrWhiteSpace(application.OwnerName),
                        application.OwnerKills,
                        OwnerEliminations = application.OwnerEliminations.Count,
                        application.Metadata
                    },
                    Diagnostics = logger.Entries
                };
            }
            catch (Exception exception)
            {
                report = new
                {
                    File = Path.GetFileName(replayPath),
                    ParseMode = ParseMode.Normal.ToString(),
                    Completed = false,
                    Error = exception.GetType().Name,
                    exception.Message,
                    Diagnostics = logger.Entries
                };
            }
        }

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        if (string.IsNullOrWhiteSpace(outputPath))
            Console.WriteLine(json);
        else
            await File.WriteAllTextAsync(outputPath, json);
    }

    private sealed class AggregateLogger : ILogger
    {
        public Dictionary<string, int> Entries { get; } = new(StringComparer.Ordinal);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var template = (state as IEnumerable<KeyValuePair<string, object?>>)?
                .FirstOrDefault(pair => pair.Key == "{OriginalFormat}").Value?.ToString()
                ?? formatter(state, exception);
            Entries[template] = Entries.GetValueOrDefault(template) + 1;
        }
    }
}
