using AURA.Notification.ConsultationChecks;

await AuthorityChecks.RunAsync();
RuleChecks.Run();
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AURA_NOTIFICATION_TEST_CONNECTION")))
    Console.WriteLine("SKIP: PostgreSQL checks need an isolated AURA_NOTIFICATION_TEST_CONNECTION.");
else
    await RepositoryChecks.RunAsync();
