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
        var client = new AiHttpClient(http, Options.Create(new AiClientOptions { TimeoutSeconds = 1 }), tokens.Object,
            NullLogger<AiHttpClient>.Instance);
        var result = await client.AnalyzeAsync(new AiAnalyzeRequest { RequestId = Guid.NewGuid(), ImageBase64 = "AQ==" },
            CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.False(result.IsLowQuality);
        Assert.Equal(transient, result.IsTransientError);
    }

    [Fact]
    public async Task Busy_waits_for_retry_after_within_one_call_and_preserves_request_identity()
    {
        var tokens = new Mock<IServiceTokenProvider>();
        tokens.Setup(p => p.GetTokenAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-service-token");
        var handler = new BusyThenReject();
        using var http = new HttpClient(handler);
        var client = new AiHttpClient(http, Options.Create(new AiClientOptions { TimeoutSeconds = 1 }), tokens.Object,
            NullLogger<AiHttpClient>.Instance);
        var result = await client.AnalyzeAsync(new AiAnalyzeRequest { RequestId = Guid.NewGuid(), ImageBase64 = "AQ==" }, default);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Equal(handler.Bodies[0], handler.Bodies[1]);
        Assert.Equal("ERR_UNAUTHORIZED", result.ErrorCode);
        Assert.False(result.IsTransientError);
    }

    private sealed class BusyThenReject : HttpMessageHandler
    {
        public readonly List<string> Bodies = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            var response = new HttpResponseMessage(Bodies.Count == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.Unauthorized)
                { Content = new StringContent("{}") };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));
            return response;
        }
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
