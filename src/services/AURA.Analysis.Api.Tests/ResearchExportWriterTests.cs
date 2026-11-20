using AURA.Analysis.Api.Exports;
using Xunit;

namespace AURA.Analysis.Api.Tests;

public sealed class ResearchExportWriterTests
{
    [Fact]
    public void Csv_uses_only_allowlisted_metrics_and_neutralizes_formula_cells()
    {
        var csv = ResearchExportWriter.WriteCsv([
            new ResearchExportRow("=1+1", "HIGH", true, .42m)
        ]);

        Assert.Equal("\"caseCode\",\"riskLevel\",\"measurementAvailable\",\"avr\"\r\n\"'=1+1\",\"HIGH\",\"True\",\"0.42\"\r\n", csv);
        Assert.DoesNotContain("patientId", csv, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("resultId", csv, StringComparison.OrdinalIgnoreCase);
    }
}
