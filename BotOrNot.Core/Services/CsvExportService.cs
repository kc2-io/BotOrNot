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

    private static string EscapeField(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return '"' + value.Replace("\"", "\"\"") + '"';
        }
        return value;
    }
}
