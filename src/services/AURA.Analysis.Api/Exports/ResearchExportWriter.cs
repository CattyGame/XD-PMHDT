using AURA.Analysis.Api.Campaign;

namespace AURA.Analysis.Api.Exports;

public static class ResearchExportWriter
{
    public static string WriteCsv(IReadOnlyList<ResearchExportRow> rows)
    {
        var lines = new List<string>(rows.Count + 1)
        {
            CampaignCsvWriter.Row("caseCode", "riskLevel", "measurementAvailable", "avr")
        };
        lines.AddRange(rows.Select(row => CampaignCsvWriter.Row(
            row.CaseCode, row.RiskLevel, row.MeasurementAvailable, row.Avr)));
        return string.Join("\r\n", lines) + "\r\n";
    }
}
