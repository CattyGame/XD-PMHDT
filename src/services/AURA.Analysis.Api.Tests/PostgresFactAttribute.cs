using Xunit;

namespace AURA.Analysis.Api.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AURA_SCRUM345_TEST_CONNECTION_STRING")))
            Skip = "Requires AURA_SCRUM345_TEST_CONNECTION_STRING (CI integration database).";
    }
}
