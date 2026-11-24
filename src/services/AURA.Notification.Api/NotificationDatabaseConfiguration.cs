using Npgsql;

namespace AURA.Notification.Api;

public static class NotificationDatabaseConfiguration
{
    public static NpgsqlConnectionStringBuilder Build(IConfiguration configuration) => new()
    {
        Host = configuration["NotificationDatabase:Host"] ?? "127.0.0.1",
        Port = configuration.GetValue("NotificationDatabase:Port", 5432),
        Database = Required(configuration, "Database"),
        Username = Required(configuration, "Username"),
        Password = Required(configuration, "Password"),
        Timeout = 5,
        CommandTimeout = 10,
        MaxPoolSize = configuration.GetValue("NotificationDatabase:MaximumPoolSize", 16),
        IncludeErrorDetail = false,
        SslMode = configuration.GetValue("NotificationDatabase:UseTls", false) ? SslMode.VerifyFull : SslMode.Disable,
        RootCertificate = configuration["NotificationDatabase:RootCertificate"]
    };

    private static string Required(IConfiguration configuration, string key)
        => !string.IsNullOrWhiteSpace(configuration["NotificationDatabase:" + key])
            ? configuration["NotificationDatabase:" + key]!
            : throw new InvalidOperationException("Missing required configuration: NotificationDatabase:" + key);
}
