using AURA.Notification.ConsultationChecks;

await AuthorityChecks.RunAsync();
await RuleChecks.RunAsync();

var runDatabase = args.Contains("--database", StringComparer.OrdinalIgnoreCase);
var requirePostgres = args.Contains("--require-postgres", StringComparer.Ordinal);
var rawConnection = Environment.GetEnvironmentVariable("AURA_NOTIFICATION_TEST_CONNECTION")
    ?? Environment.GetEnvironmentVariable("AURA_TEST_POSTGRES");

if ((runDatabase || requirePostgres) && string.IsNullOrWhiteSpace(rawConnection))
{
    throw new InvalidOperationException(
        "Required consultation PostgreSQL checks need AURA_NOTIFICATION_TEST_CONNECTION or AURA_TEST_POSTGRES.");
}

if (string.IsNullOrWhiteSpace(rawConnection))
{
    Console.WriteLine("SKIP: PostgreSQL checks need an isolated AURA_NOTIFICATION_TEST_CONNECTION.");
}
else
{
    await RepositoryChecks.RunAsync(rawConnection);
    if (runDatabase)
        await CrossServiceSocketAndFcmChecks.RunAsync(rawConnection);
}
