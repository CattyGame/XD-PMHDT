using AURA.Analysis.Api.Campaign;
using Xunit;

namespace AURA.Analysis.Api.Tests;

public sealed class CampaignCsvWriterTests
{
    [Theory]
    [InlineData("=HYPERLINK(\"https://evil.invalid\")", "\"'=HYPERLINK(\"\"https://evil.invalid\"\")\"")]
    [InlineData(" +SUM(1,2)", "\"' +SUM(1,2)\"")]
    [InlineData("\t@cmd", "\"'\t@cmd\"")]
    [InlineData("-2", "\"'-2\"")]
    public void Formula_like_cells_are_prefixed_and_csv_encoded(string input, string expected)
    {
        Assert.Equal(expected, CampaignCsvWriter.EscapeCell(input));
    }

    [Fact]
    public void Csv_row_quotes_delimiters_and_quotes_in_all_cells()
    {
        Assert.Equal("\"campaign\",\"A, B\",\"x\"\"y\"", CampaignCsvWriter.Row("campaign", "A, B", "x\"y"));
    }
}
