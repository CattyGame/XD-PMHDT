using AURA.Notification.ConsultationChecks;

await AuthorityChecks.RunAsync();
await RuleChecks.RunAsync();
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AURA_NOTIFICATION_TEST_CONNECTION")))
{
    if (args.Contains("--require-postgres", StringComparer.Ordinal))
        throw new InvalidOperationException("Required consultation PostgreSQL gate is missing AURA_NOTIFICATION_TEST_CONNECTION.");
    Console.WriteLine("SKIP: PostgreSQL checks need an isolated AURA_NOTIFICATION_TEST_CONNECTION.");
}
else
    await RepositoryChecks.RunAsync();
