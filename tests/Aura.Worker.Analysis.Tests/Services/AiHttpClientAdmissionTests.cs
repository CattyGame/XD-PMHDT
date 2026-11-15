using System.Net;
using Aura.Worker.Analysis.Configuration;
using Aura.Worker.Analysis.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Aura.Worker.Analysis.Tests.Services;

public class AiHttpClientAdmissionTests
{
    [Theory]
    [InlineData(429, true)]
    [InlineData(503, true)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    public async Task BusyIsRetryable_AuthenticationErrorsRemainTerminal(int status, bool transient)
    {
        var tokens = new Mock<IServiceTokenProvider>();
        tokens.Setup(p => p.GetTokenAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-service-token");
        using var http = new HttpClient(new StatusHandler((HttpStatusCode)status));
        var client = new AiHttpClient(http, Options.Create(new AiClientOptions()), tokens.Object,
            NullLogger<AiHttpClient>.Instance);
        var result = await client.AnalyzeAsync(new AiAnalyzeRequest { RequestId = Guid.NewGuid(), ImageBase64 = "AQ==" },
            CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.False(result.IsLowQuality);
        Assert.Equal(transient, result.IsTransientError);
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
        }
    }
}
