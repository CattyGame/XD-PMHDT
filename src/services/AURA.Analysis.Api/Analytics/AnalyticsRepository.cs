using Npgsql;
using NpgsqlTypes;

namespace AURA.Analysis.Api.Analytics;

public sealed record AnalyticsDataScope(
    Guid? TenantId,
    Guid? ClinicId,
    Guid? PatientId,
    IReadOnlyCollection<Guid>? AuthorizedPatientIds = null,
    Guid? DoctorId = null);

public sealed record AnalyticsSummaryRow(
    long Total,
    long Active,
    long Completed,
    long Failed,
    long LowQuality,
    long Rejected,
    long Duplicate,
    long RiskLow,
    long RiskModerate,
    long RiskHigh,
    long RiskUnavailable,
    long RiskUnreviewed,
    long MeasurementAvailable,
    long MeasurementUnavailable,
    long MeasurementUnknown);

public sealed record AnalyticsTrendRow(
    Guid AnalysisId,
    DateTimeOffset CreatedAt,
    string? ModelVersion,
    string? ConfigVersion,
    string? ThresholdVersion,
    string? MeasurementUnits,
    int ReviewVersion,
    decimal? Avr,
    string? UnavailableReason,
    bool AvrAvailable,
    decimal? VesselDensity,
    decimal? TortuosityIndex);

public interface IAnalyticsRepository
{
    Task<AnalyticsSummaryRow> ReadSummaryAsync(AnalyticsDataScope scope, AnalyticsWindow window, CancellationToken ct);
    Task<IReadOnlyList<AnalyticsTrendRow>> ReadPatientTrendsAsync(
        Guid tenantId, Guid clinicId, Guid patientId, AnalyticsWindow window, int limit, CancellationToken ct);
}

public sealed class PostgresAnalyticsRepository(NpgsqlDataSource dataSource) : IAnalyticsRepository
{
    private const string ScopeSql = """
        FROM analysis.images i
        JOIN analysis.analysis_requests r ON r.tenant_id=i.tenant_id AND r.id=i.analysis_request_id
        LEFT JOIN LATERAL (
            SELECT x.risk_assessment, x.metrics
            FROM analysis.analysis_results x
            WHERE x.tenant_id=i.tenant_id AND x.image_id=i.id
            ORDER BY x.version DESC LIMIT 1
        ) result ON true
        LEFT JOIN LATERAL (
            SELECT d.corrected_risk_level, d.analysis_result_id, d.version, d.doctor_id
            FROM analysis.doctor_reviews d
            WHERE d.tenant_id=i.tenant_id AND d.image_id=i.id
            ORDER BY d.version DESC LIMIT 1
        ) review ON true
        LEFT JOIN analysis.analysis_results reviewed_result
          ON reviewed_result.tenant_id=i.tenant_id AND reviewed_result.id=review.analysis_result_id
        WHERE ($1::uuid IS NULL OR i.tenant_id=$1)
          AND ($2::uuid IS NULL OR r.owner_id=$2)
          AND ($3::uuid IS NULL OR r.patient_id=$3)
          AND ($4::uuid[] IS NULL OR r.patient_id=ANY($4))
          AND ($5::uuid IS NULL OR review.doctor_id=$5)
          AND i.created_at >= $6 AND i.created_at < $7
        """;

    public async Task<AnalyticsSummaryRow> ReadSummaryAsync(AnalyticsDataScope scope, AnalyticsWindow window, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand($$"""
            WITH scoped AS (
                SELECT i.processing_status,
                       (review.version IS NOT NULL) AS has_review,
                       COALESCE(review.corrected_risk_level,
                           reviewed_result.risk_assessment->>'riskLevel',
                           reviewed_result.risk_assessment->>'risk_level') AS reviewed_risk,
                       CASE lower(COALESCE(result.metrics #>> '{research,avr,measurement_available}',''))
                           WHEN 'true' THEN true WHEN 'false' THEN false ELSE NULL END AS avr_available
                {{ScopeSql}}
            )
            SELECT count(*) FILTER (WHERE processing_status <> 'DUPLICATE') AS total,
                   count(*) FILTER (WHERE processing_status IN ('PENDING','QUEUED','PROCESSING','RETRY_READY')) AS active,
                   count(*) FILTER (WHERE processing_status='COMPLETED') AS completed,
                   count(*) FILTER (WHERE processing_status='FAILED') AS failed,
                   count(*) FILTER (WHERE processing_status='LOW_QUALITY') AS low_quality,
                   count(*) FILTER (WHERE processing_status='REJECTED') AS rejected,
                   count(*) FILTER (WHERE processing_status='DUPLICATE') AS duplicate,
                   count(*) FILTER (WHERE processing_status <> 'DUPLICATE' AND has_review AND reviewed_risk='LOW') AS risk_low,
                   count(*) FILTER (WHERE processing_status <> 'DUPLICATE' AND has_review AND reviewed_risk='MODERATE') AS risk_moderate,
                   count(*) FILTER (WHERE processing_status <> 'DUPLICATE' AND has_review AND reviewed_risk='HIGH') AS risk_high,
                   count(*) FILTER (WHERE processing_status <> 'DUPLICATE' AND has_review AND reviewed_risk IS NULL) AS risk_unavailable,
                   count(*) FILTER (WHERE processing_status <> 'DUPLICATE' AND NOT has_review) AS risk_unreviewed,
                   count(*) FILTER (WHERE processing_status <> 'DUPLICATE' AND avr_available IS TRUE) AS measurement_available,
                   count(*) FILTER (WHERE processing_status <> 'DUPLICATE' AND avr_available IS FALSE) AS measurement_unavailable,
                   count(*) FILTER (WHERE processing_status <> 'DUPLICATE' AND avr_available IS NULL) AS measurement_unknown
            FROM scoped;
            """);
        AddScopeParameters(command, scope, window);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Analytics aggregate returned no row.");
        return new(
            reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
            reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7),
            reader.GetInt64(8), reader.GetInt64(9), reader.GetInt64(10), reader.GetInt64(11),
            reader.GetInt64(12), reader.GetInt64(13), reader.GetInt64(14));
    }

    public async Task<IReadOnlyList<AnalyticsTrendRow>> ReadPatientTrendsAsync(
        Guid tenantId, Guid clinicId, Guid patientId, AnalyticsWindow window, int limit, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT i.id, i.created_at,
                   COALESCE(result.metrics #>> '{research,avr,model_version}',result.model_version),
                   result.config_version, result.threshold_version,
                   CASE WHEN result.metrics #>> '{research,avr,avr}' IS NOT NULL THEN 'ratio'
                        WHEN result.metrics #>> '{research,avr,crae_px}' IS NOT NULL THEN 'native_px'
                        ELSE NULL END AS measurement_units,
                   COALESCE(review.version,0),
                   NULLIF(result.metrics #>> '{research,avr,avr}','')::numeric AS avr,
                   NULLIF(result.metrics #>> '{research,avr,unavailable_reason}','') AS unavailable_reason,
                   COALESCE(lower(result.metrics #>> '{research,avr,measurement_available}')='true',false) AS avr_available,
                   NULLIF(result.metrics->>'vesselDensity','')::numeric AS vessel_density,
                   NULLIF(result.metrics->>'tortuosityIndex','')::numeric AS tortuosity_index
            FROM analysis.images i
            JOIN analysis.analysis_requests r ON r.tenant_id=i.tenant_id AND r.id=i.analysis_request_id
            LEFT JOIN LATERAL (
                SELECT x.model_version,x.config_version,x.threshold_version,x.metrics
                FROM analysis.analysis_results x
                WHERE x.tenant_id=i.tenant_id AND x.image_id=i.id
                ORDER BY x.version DESC LIMIT 1
            ) result ON true
            LEFT JOIN LATERAL (
                SELECT d.version FROM analysis.doctor_reviews d
                WHERE d.tenant_id=i.tenant_id AND d.image_id=i.id
                ORDER BY d.version DESC LIMIT 1
            ) review ON true
            WHERE i.tenant_id=$1 AND r.owner_id=$2 AND r.patient_id=$3
              AND i.processing_status='COMPLETED' AND result.metrics IS NOT NULL
              AND i.processing_status <> 'DUPLICATE'
              AND i.created_at >= $4 AND i.created_at < $5
            ORDER BY i.created_at,i.id LIMIT $6;
            """);
        command.Parameters.AddWithValue(tenantId);
        command.Parameters.AddWithValue(clinicId);
        command.Parameters.AddWithValue(patientId);
        command.Parameters.AddWithValue(window.From);
        command.Parameters.AddWithValue(window.To);
        command.Parameters.AddWithValue(limit);
        var rows = new List<AnalyticsTrendRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new(reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1), NullableString(reader, 2),
                NullableString(reader, 3), NullableString(reader, 4), NullableString(reader, 5), reader.GetInt32(6),
                NullableDecimal(reader, 7), NullableString(reader, 8), reader.GetBoolean(9),
                NullableDecimal(reader, 10), NullableDecimal(reader, 11)));
        }
        return rows;
    }

    private static void AddScopeParameters(NpgsqlCommand command, AnalyticsDataScope scope, AnalyticsWindow window)
    {
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = scope.TenantId is { } tenant ? tenant : DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = scope.ClinicId is { } clinic ? clinic : DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = scope.PatientId is { } patient ? patient : DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid, Value = scope.AuthorizedPatientIds is { } ids ? ids.ToArray() : DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = scope.DoctorId is { } doctor ? doctor : DBNull.Value });
        command.Parameters.AddWithValue(window.From);
        command.Parameters.AddWithValue(window.To);
    }

    private static string? NullableString(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static decimal? NullableDecimal(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);
}
