using System.Text.Json;
using Aura.Worker.Analysis.Data;
using Aura.Worker.Analysis.Data.Entities;
using Aura.Worker.Analysis.Processing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Aura.Worker.Analysis.Tests;

public class CanonicalOutboxTests
{
    [Fact]
    public void Completed_factory_uses_same_persisted_id_and_contract_version_as_envelope()
    {
        var row = OutboxEventFactory.CreateCompletedEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "regression", "model", "config", "threshold");
        using var payload = JsonDocument.Parse(row.PayloadJson);
        Assert.Equal(payload.RootElement.GetProperty("eventId").GetGuid(), row.OutboxId);
        Assert.Equal(payload.RootElement.GetProperty("schemaVersion").GetString(), row.SchemaVersion);
        var mapped = AURA.Analysis.Api.Outbox.OutboxMessageMapper.Map(new(
            row.OutboxId,row.TenantId,row.EventType,row.SchemaVersion,row.PayloadJson,
            new DateTimeOffset(row.OccurredAt),0),new());
        Assert.True(mapped.Ok,mapped.ErrorCode);
    }

    [Fact]
    public void Ef_outbox_model_contains_no_legacy_status_or_available_columns()
    {
        using var db = new WorkerDbContext(new DbContextOptionsBuilder<WorkerDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=not_connected;Username=unused").Options);
        var entity = db.Model.FindEntityType(typeof(OutboxRecord))!;
        Assert.Null(entity.FindProperty(nameof(OutboxRecord.Status)));
        Assert.Null(entity.FindProperty(nameof(OutboxRecord.AvailableAt)));
    }

    [Fact]
    public void Annotation_metadata_is_derived_from_actual_png_bytes()
    {
        var bytes=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jkioAAAAASUVORK5CYII=");
        var metadata=AnnotationMetadata.FromPng(Guid.NewGuid(),"MASK","test.png",bytes);
        Assert.Equal(bytes.Length,metadata.SizeBytes);
        Assert.Equal(1,metadata.Width);Assert.Equal(1,metadata.Height);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(),metadata.Sha256);
        Assert.Throws<InvalidDataException>(()=>AnnotationMetadata.FromPng(Guid.NewGuid(),"MASK","bad",[1,2,3]));
    }
}
