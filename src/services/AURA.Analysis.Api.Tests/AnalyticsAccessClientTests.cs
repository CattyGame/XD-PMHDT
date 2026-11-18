using AURA.Analysis.Api.Analytics;
using AURA.Analysis.Api.Upload;
using Xunit;

namespace AURA.Analysis.Api.Tests;

public sealed class AnalyticsAccessClientTests
{
    [Fact]
    public async Task Unconfigured_profile_authority_fails_closed_with_retryable_dependency_error()
    {
        var resolver = new AnalyticsAccessUnavailableResolver();
        var request = new AnalyticsAccessRequest(Guid.NewGuid(), "ANALYTICS_READ", "CLINIC", Guid.NewGuid(), null, null);
        var error = await Assert.ThrowsAsync<UploadFailure>(
            () => resolver.ResolveAsync(request, CancellationToken.None));

        Assert.Equal(503, error.Status);
        Assert.True(error.Retryable);
    }
}
