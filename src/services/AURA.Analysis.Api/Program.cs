using AURA.Analysis.Api;
using AURA.Analysis.Api.Http;
using AURA.Analysis.Api.Outbox.Adapters;
using QuestPDF.Infrastructure;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;

var builder = WebApplication.CreateBuilder(args);

QuestPDF.Settings.License = LicenseType.Community;

builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var analysisHttpEnabled = builder.Configuration.GetValue<bool>("Analysis:EnableHttpEndpoints");
if (analysisHttpEnabled) builder.Services.AddAnalysisRuntime(builder.Configuration, builder.Environment);

builder.Services.AddOutboxPublisher(builder.Configuration);

var app = builder.Build();

app.UseExceptionHandler();
app.UseAuraPrometheusMetrics();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapHealthChecks("/health");
app.MapAuraMetrics();
app.MapAnalysisReadiness(analysisHttpEnabled);

app.MapGet("/api/v1/platform/ping", () =>
    Results.Ok(new PlatformPingResponse(
        "AURA.Analysis.Api",
        "Template API is running",
        DateTimeOffset.UtcNow)))
    .WithName("PlatformPing")
    .WithTags("Platform")
    .Produces<PlatformPingResponse>(StatusCodes.Status200OK);

if (analysisHttpEnabled)
{
    app.MapAnalysisEndpoints();
    app.MapCampaignEndpoints();
}
app.Run();

public sealed record PlatformPingResponse(
    string Service,
    string Message,
    DateTimeOffset Timestamp);
