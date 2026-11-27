using System.Text.Json.Serialization;
using AURA.Analysis.Api.Upload;

namespace AURA.Analysis.Api.Analytics;

public sealed record AnalyticsWindow
{
    private AnalyticsWindow(DateTimeOffset from, DateTimeOffset to)
    {
        From = from;
        To = to;
    }

    public DateTimeOffset From { get; }
    public DateTimeOffset To { get; }

    public static AnalyticsWindow Create(DateTimeOffset from, DateTimeOffset to)
    {
        if (from.Offset != TimeSpan.Zero || to.Offset != TimeSpan.Zero || from >= to)
            throw UploadFailure.Validation(new ErrorDetail("window", "must_be_utc_and_from_before_to"));
        return new AnalyticsWindow(from, to);
    }

    public bool Contains(DateTimeOffset instant) => instant >= From && instant < To;
}

public sealed record AnalyticsProcessingCounts(
    long Active,
    long Completed,
    long Failed,
    long LowQuality,
    long Rejected,
    long Duplicate);

public sealed record AnalyticsRiskCounts(
    long Low,
    long Moderate,
    long High,
    long Unavailable,
    long Unreviewed);

public sealed record AnalyticsSummary(
    string Unit,
    DateTimeOffset From,
    DateTimeOffset To,
    long Total,
    AnalyticsProcessingCounts Processing,
    string RiskSource,
    AnalyticsRiskCounts ReviewRisk,
    long MeasurementAvailable,
    long MeasurementUnavailable,
    long MeasurementUnknown,
    DateTimeOffset AsOf,
    AnalyticsRevenueSummary Revenue);

public sealed record AnalyticsRevenueGroup(string Currency, string Environment, long PaidOrders, decimal Amount);

public sealed record AnalyticsRevenueSummary(
    string Status,
    IReadOnlyList<AnalyticsRevenueGroup> Groups,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? UnavailableReason);

public sealed record AnalyticsTrendPoint(
    Guid AnalysisId,
    DateTimeOffset CreatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ModelVersion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ConfigVersion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ThresholdVersion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? MeasurementUnits,
    int ReviewVersion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] decimal? Avr,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? UnavailableReason,
    bool AvrAvailable,
    bool CanConnectToPrevious,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] decimal? VesselDensity,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] decimal? TortuosityIndex);

public sealed record PatientAnalyticsTrends(
    Guid PatientId,
    IReadOnlyList<AnalyticsTrendPoint> Items,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? NextCursor);
