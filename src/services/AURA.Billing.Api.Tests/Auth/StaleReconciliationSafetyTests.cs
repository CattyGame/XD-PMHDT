using System.Net;
using AURA.Billing.Api.Auth;
using AURA.Billing.Api.Models;
using Xunit;

namespace AURA.Billing.Api.Tests.Auth;

public sealed class StaleReconciliationSafetyTests
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _analysisId = Guid.NewGuid();
    private readonly Guid _eventId = Guid.NewGuid();
    private readonly Guid _reservationId = Guid.NewGuid();

    [Fact]
    public void ADR005_D4_State_Machine_Invariant_No_Capture_To_Release()
    {
        // CAPTURE must strictly have COMPLETED status, VALID_RESULT reason, and valid ResultVersion > 0
        var invalidCapture = new SettlementResolutionResponse(
            _eventId,
            _analysisId,
            _tenantId,
            Applicable: true,
            SettlementAction: "CAPTURE",
            Reason: "VALID_RESULT",
            ProcessingStatus: "FAILED", // invalid status for CAPTURE!
            ReservationId: _reservationId,
            Generation: 1,
            ResultVersion: null,
            IsMock: false);

        var ex = Assert.Throws<SettlementResolverCallException>(() =>
            AnalysisSettlementResolverClient.ValidateResolution(invalidCapture, _eventId, _analysisId, _tenantId));

        Assert.Equal("ERR_ANALYSIS_RESOLVER_INVALID_CAPTURE", ex.Code);
        Assert.False(ex.Retryable);
    }

    [Fact]
    public void ADR005_D4_SettlementResolver_Accepts_Valid_Release_With_Null_IsMock()
    {
        // Producer returns IsMock = null for normal terminal failures. Consumer must accept it.
        var validRelease = new SettlementResolutionResponse(
            _eventId,
            _analysisId,
            _tenantId,
            Applicable: true,
            SettlementAction: "RELEASE",
            Reason: "FINAL_FAILURE",
            ProcessingStatus: "FAILED",
            ReservationId: _reservationId,
            Generation: 1,
            ResultVersion: null,
            IsMock: null); // Producer normal release payload has IsMock=null

        // Should not throw ERR_ANALYSIS_RESOLVER_INVALID_RELEASE
        AnalysisSettlementResolverClient.ValidateResolution(validRelease, _eventId, _analysisId, _tenantId);
    }

    [Fact]
    public void ADR005_D4_SettlementResolver_Rejects_Release_If_ResultVersion_Present()
    {
        // A release resolution must not have a valid result version attached
        var invalidRelease = new SettlementResolutionResponse(
            _eventId,
            _analysisId,
            _tenantId,
            Applicable: true,
            SettlementAction: "RELEASE",
            Reason: "FINAL_FAILURE",
            ProcessingStatus: "FAILED",
            ReservationId: _reservationId,
            Generation: 1,
            ResultVersion: 1, // Invalid for RELEASE!
            IsMock: false);

        var ex = Assert.Throws<SettlementResolverCallException>(() =>
            AnalysisSettlementResolverClient.ValidateResolution(invalidRelease, _eventId, _analysisId, _tenantId));

        Assert.Equal("ERR_ANALYSIS_RESOLVER_INVALID_RELEASE", ex.Code);
    }

    [Fact]
    public void ADR005_D4_Fingerprint_ForRelease_Includes_ReservationId_And_Reason()
    {
        var reservationA = Guid.NewGuid();
        var reservationB = Guid.NewGuid();

        var fpA1 = ReservationFingerprint.ForRelease(reservationA, "FINAL_FAILURE");
        var fpA2 = ReservationFingerprint.ForRelease(reservationA, "LOW_QUALITY");
        var fpB = ReservationFingerprint.ForRelease(reservationB, "FINAL_FAILURE");

        // Same reservation, different reasons must produce different fingerprints
        Assert.NotEqual(fpA1, fpA2);

        // Different reservations, same reason must produce different fingerprints
        Assert.NotEqual(fpA1, fpB);

        // Case and whitespace normalized
        var fpNormalized = ReservationFingerprint.ForRelease(reservationA, "  final_failure  ");
        Assert.Equal(fpA1, fpNormalized);
    }

    [Fact]
    public void ADR005_D4_Fingerprint_Capture_And_Release_Are_Distinct()
    {
        var reservation = Guid.NewGuid();
        var captureFp = ReservationFingerprint.ForCapture(reservation);
        var releaseFp = ReservationFingerprint.ForRelease(reservation, "FINAL_FAILURE");

        Assert.NotEqual(captureFp, releaseFp);
        Assert.Equal(64, captureFp.Length);
        Assert.Equal(64, releaseFp.Length);
    }
}
