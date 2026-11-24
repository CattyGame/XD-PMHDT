using System.Net;
using System.Text;
using Aura.Worker.Analysis.Configuration;
using Aura.Worker.Analysis.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Aura.Worker.Analysis.Tests.Services;

public class AiHttpClientStreamingTests
{
    [Fact]
    public async Task Large_annotations_are_read_without_buffering_the_entire_response()
    {
        var annotation = new string('A', 8 * 1024 * 1024);
        using var http = new HttpClient(new ResponseHandler(HttpStatusCode.OK,
            "{\"status\":\"completed\",\"risk_available\":false,\"metrics\":{\"av_ratio\":null}," +
            "\"segmentation\":{\"overlay_base64\":\"" + annotation + "\"}}"));

        var result = await Client(http).AnalyzeAsync(new AiAnalyzeRequest(), default);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(annotation, result.Response!.Segmentation.OverlayBase64);
        Assert.Null(result.Response.Metrics.AvRatio);
        Assert.False(result.Response.RiskAvailable);
    }

    [Theory]
    [InlineData(500, true, "ERR_AI_FAILED")]
    [InlineData(403, false, "ERR_UNAUTHORIZED")]
    [InlineData(422, false, "ERR_IMAGE_LOW_QUALITY")]
    public async Task Error_bodies_are_not_buffered_or_copied_into_patient_visible_errors(
        int status, bool transient, string? code)
    {
        using var http = new HttpClient(new ResponseHandler((HttpStatusCode)status, "private-image-payload"));

        var result = await Client(http).AnalyzeAsync(new AiAnalyzeRequest(), default);

        Assert.False(result.IsSuccess);
        Assert.Equal(transient, result.IsTransientError);
        Assert.Equal(code, result.ErrorCode);
        Assert.Equal(status == 422, result.IsLowQuality);
        Assert.DoesNotContain("private-image-payload", result.ErrorMessage);
    }

    private static AiHttpClient Client(HttpClient http)
    {
        var tokens = new Mock<IServiceTokenProvider>();
        tokens.Setup(p => p.GetTokenAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-service-token");
        return new AiHttpClient(http, Options.Create(new AiClientOptions()), tokens.Object,
            NullLogger<AiHttpClient>.Instance);
    }

    private sealed class ResponseHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new UnbufferedContent(body) });
    }

    // A network response can be streamed; prebuffering it duplicates large native-image annotations.
    private sealed class UnbufferedContent(string body) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new InvalidOperationException("Full response buffering is forbidden.");
        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(body), writable: false));
    }
}
