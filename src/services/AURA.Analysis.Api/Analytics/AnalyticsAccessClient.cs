using AURA.Analysis.Api.Upload;

namespace AURA.Analysis.Api.Analytics;

public sealed record AnalyticsAccessRequest(
    Guid ActorId,
    string Operation,
    string ResourceScope,
    Guid? ClinicId,
    Guid? PatientId,
    string? CorrelationId);

public sealed record AnalyticsAccessGrant(
    Guid? TenantId,
    Guid? ClinicId,
    Guid? PatientId,
    IReadOnlyCollection<Guid>? AuthorizedPatientIds,
    long PermissionVersion);

public interface IAnalyticsAccessResolver
{
    Task<AnalyticsAccessGrant> ResolveAsync(AnalyticsAccessRequest request, CancellationToken ct);
}

/// <summary>
/// Safe local runtime until the versioned Profile ANALYTICS_READ wire contract is available.
/// Never infers authority from identity roles or client-supplied clinic selectors.
/// </summary>
public sealed class AnalyticsAccessUnavailableResolver : IAnalyticsAccessResolver
{
    public Task<AnalyticsAccessGrant> ResolveAsync(AnalyticsAccessRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromException<AnalyticsAccessGrant>(UploadFailure.Dependency("Profile analytics authority"));
    }
}
