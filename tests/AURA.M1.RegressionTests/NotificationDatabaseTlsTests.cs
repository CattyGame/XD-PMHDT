using AURA.Notification.Api;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace AURA.M1.RegressionTests;

public class NotificationDatabaseTlsTests
{
    [Theory]
    [InlineData(true, SslMode.VerifyFull)]
    [InlineData(false, SslMode.Disable)]
    public void Database_tls_verifies_the_server_and_retains_connection_limits(bool tls, SslMode expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NotificationDatabase:Database"] = "fixture", ["NotificationDatabase:Username"] = "fixture",
            ["NotificationDatabase:Password"] = "fixture;quoted", ["NotificationDatabase:UseTls"] = tls.ToString(),
            ["NotificationDatabase:RootCertificate"] = "/run/aura-tls/ca.crt"
        }).Build();
        var actual = NotificationDatabaseConfiguration.Build(config);
        Assert.Equal(expected, actual.SslMode);
        Assert.Equal(16, actual.MaxPoolSize);
        Assert.Equal("fixture;quoted", actual.Password);
        if (tls) Assert.Equal("/run/aura-tls/ca.crt", actual.RootCertificate);
    }
}
