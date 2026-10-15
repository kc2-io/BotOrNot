using System.Text;
using BotOrNot.Core.Models;

namespace BotOrNot.Core.Services;

public sealed record CsvColumnDefinition(string Header, Func<PlayerRow, string> ValueSelector);

public static class CsvExportService
{
    public static string GenerateCsv(IEnumerable<PlayerRow> rows, IReadOnlyList<CsvColumnDefinition> columns)
    {
        var sb = new StringBuilder();

        // Header row
        for (var i = 0; i < columns.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(EscapeField(columns[i].Header));
        }
        sb.AppendLine();

        // Data rows
        foreach (var row in rows)
        {
            for (var i = 0; i < columns.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(EscapeField(columns[i].ValueSelector(row)));
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static string GenerateTeammateEliminationsCsv(IEnumerable<SquadMemberSummary> teammates)
    {
        var sb = new StringBuilder();
        AppendRow(sb,
            "Teammate Id", "Teammate Name", "Teammate Kills", "Observed", "Coverage",
            "Victim Id", "Victim Name", "Bot", "Platform", "Death Cause", "Elim Time", "Storm Phase");

        foreach (var teammate in teammates)
        {
            var coverage = teammate.EliminationCoverageText ??
                           (teammate.HasEliminationDetails ? "Complete" : "Unavailable");
            if (teammate.Eliminations.Count == 0)
            {
                AppendRow(sb,
                    teammate.StableId,
                    teammate.Name ?? "",
                    teammate.Kills?.ToString() ?? "",
                    teammate.HasEliminationDetails ? "0" : "",
                    coverage,
                    "", "", "", "", "", "", "");
                continue;
            }

            foreach (var victim in teammate.Eliminations)
            {
                var platform = PlatformHelper.GetFriendlyName(victim.Platform);
                AppendRow(sb,
                    teammate.StableId,
                    teammate.Name ?? "",
                    teammate.Kills?.ToString() ?? "",
                    teammate.Eliminations.Count.ToString(),
                    coverage,
                    victim.Id,
                    victim.Name ?? "",
                    bool.TryParse(victim.Bot, out var isBot) ? isBot ? "Yes" : "No" : "Unknown",
                    platform == "Unknown" ? "-" : platform,
                    victim.DeathCause ?? "",
                    victim.ElimTime ?? "",
                    victim.StormPhaseCsvValue);
            }
        }

        return sb.ToString();
    }

    private static void AppendRow(StringBuilder builder, params string[] values)
    {
        for (var index = 0; index < values.Length; index++)
        {
            if (index > 0) builder.Append(',');
            builder.Append(EscapeField(values[index]));
        }
        builder.AppendLine();
    }

    private static string EscapeField(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return '"' + value.Replace("\"", "\"\"") + '"';
        }
        return value;
    }
}
