using AURA.Notification.ConsultationChecks;

await AuthorityChecks.RunAsync();
RuleChecks.Run();

var runDatabase = args.Contains("--database", StringComparer.OrdinalIgnoreCase);
var rawConnection = Environment.GetEnvironmentVariable("AURA_NOTIFICATION_TEST_CONNECTION")
    ?? Environment.GetEnvironmentVariable("AURA_TEST_POSTGRES");

if (runDatabase && string.IsNullOrWhiteSpace(rawConnection))
{
    throw new InvalidOperationException(
        "Database checks (--database) require AURA_NOTIFICATION_TEST_CONNECTION or AURA_TEST_POSTGRES.");
}

if (string.IsNullOrWhiteSpace(rawConnection))
{
    Console.WriteLine("SKIP: PostgreSQL checks need an isolated AURA_NOTIFICATION_TEST_CONNECTION.");
}
else
{
    await RepositoryChecks.RunAsync(rawConnection);
    await CrossServiceSocketAndFcmChecks.RunAsync(rawConnection);
}
