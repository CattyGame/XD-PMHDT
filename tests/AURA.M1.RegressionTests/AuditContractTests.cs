using System.Text.Json;
using AURA.Common.Audit;
using Xunit;

namespace AURA.M1.RegressionTests;

public class AuditContractTests
{
    [Fact]
    public void Event_contains_only_typed_metadata_and_hashes_untrusted_correlation()
    {
        var audit = AuditEvent.Create(AuditSource.Analysis, Guid.NewGuid(), AuditAction.ImageRead,
            AuditResource.Image, Guid.NewGuid().ToString(), "Bearer private-token?patient_name=secret");
        var json = JsonSerializer.Serialize(audit, AuditJson.Options);
        Assert.DoesNotContain("private-token", json);
        Assert.DoesNotContain("patient_name", json);
        Assert.Equal(64, audit.CorrelationId.Length);
        Assert.Equal(audit.CorrelationId, AuditEvent.Correlation("Bearer private-token?patient_name=secret"));
        AuditEvent.Validate(audit);
        Assert.Throws<ArgumentException>(() => AuditEvent.Validate(audit with { ResourceId = "patient-name.jpg?token=secret" }));
        Assert.Throws<ArgumentException>(() => AuditEvent.Validate(audit with { Action = (AuditAction)999 }));
        Assert.Throws<ArgumentException>(() => AuditEvent.Validate(audit with { ActorAccountId = Guid.Empty }));
    }

    [Fact]
    public void Wire_rejects_extra_properties_and_numeric_enums_instead_of_silently_persisting_them()
    {
        var audit = AuditEvent.Create(AuditSource.Billing, Guid.NewGuid(), AuditAction.PriceChanged,
            AuditResource.Package, "1", null);
        var json = JsonSerializer.Serialize(audit, AuditJson.Options);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AuditEvent>(json[..^1] + ",\"token\":\"secret\"}", AuditJson.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AuditEvent>(json.Replace("\"PriceChanged\"", "2"), AuditJson.Options));
        Assert.Throws<ArgumentException>(() => AuditEvent.Validate(audit with { CorrelationId = "Bearer token" }));
        Assert.Throws<ArgumentException>(() => AuditEvent.Validate(audit with { ResourceVersion = 0 }));
    }
}
