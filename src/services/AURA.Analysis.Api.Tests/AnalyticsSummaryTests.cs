using System.Text.Json;
using AURA.Analysis.Api.Analytics;
using AURA.Analysis.Api.Upload;
using Xunit;

namespace AURA.Analysis.Api.Tests;

public sealed class AnalyticsSummaryTests
{
    [Fact]
    public void Analytics_window_uses_utc_half_open_bounds()
    {
        var from = Utc(2026, 11, 16, 0);
        var to = Utc(2026, 11, 17, 0);
        var window = AnalyticsWindow.Create(from, to);

        Assert.True(window.Contains(from));
        Assert.True(window.Contains(Utc(2026, 11, 16, 23)));
        Assert.False(window.Contains(to));
    }

    [Fact]
    public void Analytics_window_rejects_non_utc_or_empty_ranges()
    {
        AssertValidationFailure(() => AnalyticsWindow.Create(
            new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.FromHours(7)), Utc(2026, 11, 17, 0)));
        AssertValidationFailure(() => AnalyticsWindow.Create(Utc(2026, 11, 16, 0), Utc(2026, 11, 16, 0)));
        AssertValidationFailure(() => AnalyticsWindow.Create(Utc(2026, 11, 17, 0), Utc(2026, 11, 16, 0)));
    }

    [Fact]
    public void Missing_trend_measurements_serialize_as_null()
    {
        var point = new AnalyticsTrendPoint(Guid.NewGuid(), Utc(2026, 11, 16, 0),
            null, null, null, null, 0, null, null, false, false, null, null);
        var json = JsonSerializer.Serialize(point, UploadJson.Options);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("avr").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("unavailableReason").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("vesselDensity").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("tortuosityIndex").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("modelVersion").ValueKind);
    }

    private static DateTimeOffset Utc(int year, int month, int day, int hour) =>
        new(year, month, day, hour, 0, 0, TimeSpan.Zero);

    private static void AssertValidationFailure(Action action)
    {
        var failure = Assert.Throws<UploadFailure>(action);
        Assert.Equal(400, failure.Status);
        Assert.Equal("ERR_VALIDATION", failure.Code);
    }
}
