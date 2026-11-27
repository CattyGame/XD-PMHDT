using AURA.Analysis.Api.Alerts;
using AURA.Analysis.Api.Analytics;
using AURA.Analysis.Api.Upload;
using Npgsql;
using Xunit;

namespace AURA.Analysis.Api.Tests;

public sealed class AlertCrossClinicIntegrationTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("AURA_SCRUM345_TEST_CONNECTION_STRING");

    [PostgresFact]
    public async Task Alert_replay_never_duplicates_alerts_and_enforces_two_clinic_four_role_isolation()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
            throw new InvalidOperationException("AURA_SCRUM345_TEST_CONNECTION_STRING is required.");

        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);

        var tenantA = Guid.NewGuid();
        var clinicA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var clinicB = Guid.NewGuid();

        var patientA1 = Guid.NewGuid();
        var patientA2 = Guid.NewGuid();
        var patientB = Guid.NewGuid();

        var batchA = Guid.NewGuid();
        var batchB = Guid.NewGuid();
        var reqA = Guid.NewGuid();
        var reqB = Guid.NewGuid();
        var analysisA1 = Guid.NewGuid();
        var analysisA2 = Guid.NewGuid();
        var analysisB = Guid.NewGuid();
        var jobA1 = Guid.NewGuid();
        var jobA2 = Guid.NewGuid();
        var jobB = Guid.NewGuid();
        var resultA1 = Guid.NewGuid();
        var resultA2 = Guid.NewGuid();
        var resultB = Guid.NewGuid();

        var adminA = Guid.NewGuid();
        var doctorA1 = Guid.NewGuid();
        var doctorA2Unassigned = Guid.NewGuid();
        var staffA = Guid.NewGuid();
        var patientAccountA = Guid.NewGuid();
        var adminB = Guid.NewGuid();
        var doctorB = Guid.NewGuid();

        try
        {
            await SeedCompletedHighRiskAnalysisAsync(dataSource, tenantA, clinicA, adminA, patientA1, reqA, batchA, analysisA1, jobA1, resultA1, addHighReview: true);
            await SeedCompletedHighRiskAnalysisAsync(dataSource, tenantA, clinicA, adminA, patientA2, Guid.NewGuid(), Guid.NewGuid(), analysisA2, jobA2, resultA2, addHighReview: true);
            await SeedCompletedHighRiskAnalysisAsync(dataSource, tenantB, clinicB, adminB, patientB, reqB, batchB, analysisB, jobB, resultB, addHighReview: true);

            var access = new MatrixAnalyticsAccessResolver(
                tenantA, clinicA, tenantB, clinicB,
                adminA, doctorA1, doctorA2Unassigned, staffA, patientAccountA,
                adminB, doctorB, patientA1);
            var repository = new PostgresAlertRepository(dataSource);
            var service = new AlertService(repository, access);

            // Positive control 1: ClinicAdmin A lists Clinic A alerts (sequential + concurrent replay must not multiply alerts)
            var sequential1 = await service.ListAsync(adminA, clinicA, 20, null, "w6-m6-04-alert-seq1", CancellationToken.None);
            var sequential2 = await service.ListAsync(adminA, clinicA, 20, null, "w6-m6-04-alert-seq2", CancellationToken.None);
            var concurrent = await Task.WhenAll(Enumerable.Range(0, 5).Select(i =>
                service.ListAsync(adminA, clinicA, 20, null, $"w6-m6-04-alert-conc-{i}", CancellationToken.None)));

            Assert.Equal(2, sequential1.Items.Count);
            Assert.Equal(sequential1.Items.Select(x => x.AlertId), sequential2.Items.Select(x => x.AlertId));
            foreach (var page in concurrent)
            {
                Assert.Equal(sequential1.Items.Select(x => x.AlertId), page.Items.Select(x => x.AlertId));
            }

            // Assert DB has exactly 1 alert row and 1 OPEN creation history row per analysis
            Assert.Equal(1L, await ScalarLongAsync(dataSource, "SELECT count(*) FROM analysis.alerts WHERE tenant_id=$1 AND analysis_id=$2", tenantA, analysisA1));
            Assert.Equal(1L, await ScalarLongAsync(dataSource,
                "SELECT count(*) FROM analysis.alert_history h JOIN analysis.alerts a ON a.id=h.alert_id WHERE a.tenant_id=$1 AND a.analysis_id=$2 AND h.from_state IS NULL AND h.to_state='OPEN'",
                tenantA, analysisA1));

            // Positive control 2: Assigned DoctorA1 sees ONLY assigned patientA1's alert, never patientA2 or Clinic B
            var doctorPage = await service.ListAsync(doctorA1, clinicA, 20, null, "w6-m6-04-alert-doc", CancellationToken.None);
            var docAlert = Assert.Single(doctorPage.Items);
            Assert.Equal(analysisA1, docAlert.AnalysisId);
            Assert.Equal("OPEN", docAlert.Status);
            Assert.Equal(1, docAlert.Version);

            // Positive control 3: ClinicAdmin B sees ONLY Clinic B alert
            var adminBPage = await service.ListAsync(adminB, clinicB, 20, null, "w6-m6-04-alert-b", CancellationToken.None);
            var bAlert = Assert.Single(adminBPage.Items);
            Assert.Equal(analysisB, bAlert.AnalysisId);

            // Negative controls: Cross-clinic, Staff, Patient, Unassigned Doctor all fail closed
            foreach (var deniedActor in new[] { adminB, doctorB, doctorA2Unassigned, staffA, patientAccountA })
            {
                var ex = await Assert.ThrowsAsync<UploadFailure>(() =>
                    service.ListAsync(deniedActor, clinicA, 20, null, "w6-m6-04-alert-deny", CancellationToken.None));
                Assert.True(ex.Status is 403 or 404, $"Expected 403/404 for denied actor, got {ex.Status}");
            }

            // Cross-clinic PATCH probe on Clinic A alert using Clinic B context -> 404 anti-probing
            var crossClinicPatch = await Assert.ThrowsAsync<UploadFailure>(() => service.ChangeStateAsync(
                adminB, clinicB, docAlert.AlertId, new AlertStateChangeRequest("ACKNOWLEDGED", 1, "Cross-clinic probe"),
                "w6-m6-04-cross-patch", CancellationToken.None));
            Assert.Equal(404, crossClinicPatch.Status);

            // Version-bound state transition & stale conflict
            var acked = await service.ChangeStateAsync(
                doctorA1, clinicA, docAlert.AlertId, new AlertStateChangeRequest("ACKNOWLEDGED", 1, "Reviewed by assigned doctor"),
                "w6-m6-04-ack", CancellationToken.None);
            Assert.Equal("ACKNOWLEDGED", acked.Status);
            Assert.Equal(2, acked.Version);

            var staleReplay = await Assert.ThrowsAsync<UploadFailure>(() => service.ChangeStateAsync(
                doctorA1, clinicA, docAlert.AlertId, new AlertStateChangeRequest("ACKNOWLEDGED", 1, "Stale replay"),
                "w6-m6-04-stale", CancellationToken.None));
            Assert.Equal(409, staleReplay.Status);

            var closed = await service.ChangeStateAsync(
                adminA, clinicA, docAlert.AlertId, new AlertStateChangeRequest("CLOSED", 2, "Closed after follow-up"),
                "w6-m6-04-close", CancellationToken.None);
            Assert.Equal("CLOSED", closed.Status);
            Assert.Equal(3, closed.Version);
        }
        finally
        {
            await ExecuteAsync(dataSource, "DELETE FROM analysis.alert_history WHERE tenant_id = ANY($1);", new[] { tenantA, tenantB });
            await ExecuteAsync(dataSource, "DELETE FROM analysis.alerts WHERE tenant_id = ANY($1);", new[] { tenantA, tenantB });
            await ExecuteAsync(dataSource, "DELETE FROM analysis.doctor_reviews WHERE tenant_id = ANY($1);", new[] { tenantA, tenantB });
            await ExecuteAsync(dataSource, "DELETE FROM analysis.analysis_results WHERE tenant_id = ANY($1);", new[] { tenantA, tenantB });
            await ExecuteAsync(dataSource, "DELETE FROM analysis.jobs WHERE tenant_id = ANY($1);", new[] { tenantA, tenantB });
            await ExecuteAsync(dataSource, "DELETE FROM analysis.images WHERE tenant_id = ANY($1);", new[] { tenantA, tenantB });
            await ExecuteAsync(dataSource, "DELETE FROM analysis.batches WHERE tenant_id = ANY($1);", new[] { tenantA, tenantB });
            await ExecuteAsync(dataSource, "DELETE FROM analysis.analysis_requests WHERE tenant_id = ANY($1);", new[] { tenantA, tenantB });
        }
    }

    private static async Task SeedCompletedHighRiskAnalysisAsync(
        NpgsqlDataSource dataSource,
        Guid tenantId,
        Guid clinicId,
        Guid ownerId,
        Guid patientId,
        Guid requestId,
        Guid batchId,
        Guid analysisId,
        Guid jobId,
        Guid resultId,
        bool addHighReview)
    {
        var accepted = System.Text.Json.JsonSerializer.Serialize(new
        {
            batchId,
            items = new[] { new { analysisId, fileName = "w6m604-alert.png", processingStatus = "COMPLETED" } },
            createdAt = DateTime.UtcNow,
            statusUrl = $"/api/v1/batches/{batchId:D}"
        });

        await ExecuteAsync(dataSource, """
            INSERT INTO analysis.analysis_requests
                (id, tenant_id, owner_id, requester_id, patient_id, idempotency_key, request_fingerprint,
                 request_schema_version, accepted_response, correlation_id)
            VALUES ($1, $2, $3, $3, $4, $5, repeat('a', 64), '0.9.0', $6::jsonb, $7);
            """, requestId, tenantId, ownerId, patientId, Guid.NewGuid(), accepted, $"w6m604-{analysisId:N}");

        await ExecuteAsync(dataSource, """
            INSERT INTO analysis.batches (id, tenant_id, owner_id, requester_id, patient_id, clinic_id, total_images, completed_images, status)
            VALUES ($1, $2, $3, $3, $4, $5, 1, 1, 'COMPLETED');
            """, batchId, tenantId, ownerId, patientId, clinicId);

        await ExecuteAsync(dataSource, """
            INSERT INTO analysis.images
                (id, tenant_id, analysis_request_id, batch_id, file_id, modality, eye_side, object_key, mime_type,
                 size_bytes, width, height, sha256, processing_status, review_status)
            VALUES ($1, $2, $3, $4, $5, 'FUNDUS', 'left', $6, 'image/png', 1024, 64, 64, repeat('b', 64), 'COMPLETED', 'REVIEWED');
            """, analysisId, tenantId, requestId, batchId, Guid.NewGuid(), $"w6m604/{analysisId:N}/raw.png");

        await ExecuteAsync(dataSource, """
            INSERT INTO analysis.jobs (id, tenant_id, image_id, message_id, attempt_no, status, correlation_id)
            VALUES ($1, $2, $3, $4, 1, 'SUCCEEDED', $5);
            """, jobId, tenantId, analysisId, Guid.NewGuid(), $"w6m604-job-{analysisId:N}");

        await ExecuteAsync(dataSource, """
            INSERT INTO analysis.analysis_results
                (id, tenant_id, image_id, job_id, version, result_schema_version, risk_assessment, metrics, warnings,
                 is_mock, model_version, config_version, threshold_version, processing_time_ms, limitations)
            VALUES ($1, $2, $3, $4, 1, '0.9.0', '{"riskLevel":"LOW"}'::jsonb, '{}'::jsonb, '[]'::jsonb,
                    false, 'frangi-v0.2', 'v0.2', 'v0.2', 12, 'integration test fixture');
            """, resultId, tenantId, analysisId, jobId);

        if (addHighReview)
        {
            await ExecuteAsync(dataSource, """
                INSERT INTO analysis.doctor_reviews
                    (id, tenant_id, image_id, analysis_result_id, doctor_id, version, base_version, is_approved, corrected_risk_level, reason)
                VALUES ($1, $2, $3, $4, $5, 1, 0, false, 'HIGH', 'W6-M6-04 clinical alert fixture');
                """, Guid.NewGuid(), tenantId, analysisId, resultId, ownerId);
        }
    }

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql, params object[] values)
    {
        await using var command = dataSource.CreateCommand(sql);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarLongAsync(NpgsqlDataSource dataSource, string sql, params object[] values)
    {
        await using var command = dataSource.CreateCommand(sql);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private sealed class MatrixAnalyticsAccessResolver(
        Guid tenantA, Guid clinicA, Guid tenantB, Guid clinicB,
        Guid adminA, Guid doctorA1, Guid doctorA2Unassigned, Guid staffA, Guid patientAccountA,
        Guid adminB, Guid doctorB, Guid assignedPatientA1) : IAnalyticsAccessResolver
    {
        public Task<AnalyticsAccessGrant> ResolveAsync(AnalyticsAccessRequest request, CancellationToken ct)
        {
            if (request.ClinicId == clinicA)
            {
                if (request.ActorId == adminA && request.ResourceScope == "CLINIC")
                    return Task.FromResult(new AnalyticsAccessGrant(tenantA, clinicA, null, null, 1));
                if (request.ActorId == doctorA1 && request.ResourceScope == "DOCTOR_ASSIGNMENT")
                    return Task.FromResult(new AnalyticsAccessGrant(tenantA, clinicA, null, [assignedPatientA1], 1));
                if (request.ActorId == doctorA1 && request.ResourceScope == "PATIENT" && request.PatientId == assignedPatientA1)
                    return Task.FromResult(new AnalyticsAccessGrant(tenantA, clinicA, assignedPatientA1, [assignedPatientA1], 1));
                if (request.ActorId == doctorA1 && request.ResourceScope == "CLINIC")
                    return Task.FromException<AnalyticsAccessGrant>(RoleDenied());
                if (request.ActorId == doctorA2Unassigned && request.ResourceScope == "CLINIC")
                    return Task.FromException<AnalyticsAccessGrant>(RoleDenied());
                if (request.ActorId == doctorA2Unassigned && request.ResourceScope == "DOCTOR_ASSIGNMENT")
                    return Task.FromException<AnalyticsAccessGrant>(new UploadFailure(403, "ERR_FORBIDDEN", "Unassigned doctor"));
                if (request.ActorId == staffA || request.ActorId == patientAccountA || request.ActorId == adminB || request.ActorId == doctorB)
                    return Task.FromException<AnalyticsAccessGrant>(new UploadFailure(403, "ERR_FORBIDDEN", "Denied"));
            }

            if (request.ClinicId == clinicB)
            {
                if (request.ActorId == adminB && request.ResourceScope == "CLINIC")
                    return Task.FromResult(new AnalyticsAccessGrant(tenantB, clinicB, null, null, 1));
                if (request.ActorId == doctorB && request.ResourceScope == "DOCTOR_ASSIGNMENT")
                    return Task.FromResult(new AnalyticsAccessGrant(tenantB, clinicB, null, null, 1));
            }

            return Task.FromException<AnalyticsAccessGrant>(new UploadFailure(403, "ERR_FORBIDDEN", "Denied"));
        }

        private static UploadFailure RoleDenied() =>
            new(403, "ERR_FORBIDDEN", "Role requires assignment scope", details: [new("authority", "ROLE_DENIED")]);
    }
}
