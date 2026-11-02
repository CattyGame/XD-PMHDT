using System.Globalization;

namespace AURA.Analysis.Api.Campaign;

public static class CampaignCsvWriter
{
    public static string Row(params object?[] values) => string.Join(",", values.Select(EscapeCell));

    public static string EscapeCell(object? value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        var firstVisible = text.FirstOrDefault(c => !char.IsWhiteSpace(c) && !char.IsControl(c) && c != '\uFEFF');
        if (firstVisible is '=' or '+' or '-' or '@' || text.StartsWith('\t') || text.StartsWith('\r') || text.StartsWith('\n'))
            text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
