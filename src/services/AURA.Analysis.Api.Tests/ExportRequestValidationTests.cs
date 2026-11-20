using AURA.Analysis.Api.Exports;
using AURA.Analysis.Api.Upload;
using System.Text.Json;
using Xunit;

namespace AURA.Analysis.Api.Tests;

public sealed class ExportRequestValidationTests
{
    [Fact]
    public void Accepts_csv_research_export_with_purpose_and_clinic_scope()
    {
        var request = new ResearchExportRequest(Guid.NewGuid(), null, "retinal screening study", "CSV", false);

        ResearchExportRequestValidator.Validate(request);
    }

    [Fact]
    public void Rejects_research_export_without_purpose()
    {
        var request = new ResearchExportRequest(Guid.NewGuid(), null, "  ", "CSV", false);

        var failure = Assert.Throws<UploadFailure>(() => ResearchExportRequestValidator.Validate(request));

        Assert.Equal(400, failure.Status);
        Assert.Equal("ERR_VALIDATION", failure.Code);
    }

    [Fact]
    public void Requires_includeMasks_field_to_be_explicit_in_request_json()
    {
        var json = "{\"clinicContextId\":\"11111111-1111-4111-8111-111111111111\",\"campaignId\":null,\"purpose\":\"study\",\"format\":\"CSV\"}";

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ResearchExportRequest>(json));
    }
}
