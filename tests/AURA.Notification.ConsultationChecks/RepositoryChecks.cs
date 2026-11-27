using AURA.Notification.Api;
using Npgsql;

namespace AURA.Notification.ConsultationChecks;

internal static class RepositoryChecks
{
    public static async Task RunAsync()
    {
        var raw = Environment.GetEnvironmentVariable("AURA_NOTIFICATION_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("Repository checks require AURA_NOTIFICATION_TEST_CONNECTION to name an isolated test database.");

        var settings = new NpgsqlConnectionStringBuilder(raw);
        if (settings.Host is not ("localhost" or "127.0.0.1") ||
            !(settings.Database?.Contains("test", StringComparison.OrdinalIgnoreCase) == true ||
              settings.Database == "aura_dev" && settings.Port == 55432 && Environment.GetEnvironmentVariable("AURA_TEST_ALLOW_DATABASE") == "aura_dev"))
            throw new InvalidOperationException("Refusing to run consultation repository checks against a non-test database.");

        await using var source = NpgsqlDataSource.Create(raw);
        var repository = new ConsultationRepository(source);
        var patient = Guid.NewGuid();
        var doctor = Guid.NewGuid();
        var actor = patient;
        var request = NewCreate(actor, patient, doctor);
        var created = await repository.CreateConversationAsync(request, default);
        Require(created.Outcome == CreateConsultationOutcome.Created, "first create should persist a conversation");
        var replay = await repository.CreateConversationAsync(request, default);
        Require(replay.Outcome == CreateConsultationOutcome.Replay && replay.Conversation.ConversationId == created.Conversation.ConversationId,
            "create replay should return the same conversation");
        var conflict = await repository.CreateConversationAsync(request with { RequestFingerprint = new string('f', 64) }, default);
        Require(conflict.Outcome == CreateConsultationOutcome.PayloadConflict, "changed create replay should conflict");
        var reassigned = await repository.CreateConversationAsync(request with
        {
            AssignmentId = Guid.NewGuid(),
            IdempotencyKey = Guid.NewGuid().ToString("N")
        }, default);
        Require(reassigned.Outcome == CreateConsultationOutcome.Created &&
                reassigned.Conversation.ConversationId != created.Conversation.ConversationId,
            "a new assignment to the same doctor must start a new conversation binding");

        var clientId = Guid.NewGuid();
        var first = await repository.PersistMessageAsync(created.Conversation.ConversationId, patient, clientId, "nội dung test", new string('a', 64), default);
        var same = await repository.PersistMessageAsync(created.Conversation.ConversationId, patient, clientId, "nội dung test", new string('a', 64), default);
        var changed = await repository.PersistMessageAsync(created.Conversation.ConversationId, patient, clientId, "nội dung khác", new string('b', 64), default);
        Require(first.Outcome == PersistConsultationMessageOutcome.Created, "new message should persist");
        Require(same.Outcome == PersistConsultationMessageOutcome.Replay && same.Message!.MessageId == first.Message!.MessageId,
            "same payload replay should return the saved message");
        Require(same.Message!.DeliveryState is "PERSISTED" or "READ",
            "same payload replay must not expose the stored fingerprint as a delivery state");
        Require(changed.Outcome == PersistConsultationMessageOutcome.PayloadConflict, "changed payload replay should conflict");
        await using (var outboxCheck = source.CreateCommand("SELECT count(*) FROM notification.consultation_notification_outbox WHERE message_id=@message_id;"))
        {
            outboxCheck.Parameters.AddWithValue("message_id", first.Message!.MessageId);
            Require(Convert.ToInt64(await outboxCheck.ExecuteScalarAsync()) == 1,
                "a persisted message and its offline notification obligation must commit together exactly once");
        }
        await using (var messageCount = source.CreateCommand("SELECT count(*) FROM notification.consultation_messages WHERE conversation_id=@conversation_id AND client_message_id=@client_message_id;"))
        {
            messageCount.Parameters.AddWithValue("conversation_id", created.Conversation.ConversationId);
            messageCount.Parameters.AddWithValue("client_message_id", clientId);
            Require(Convert.ToInt64(await messageCount.ExecuteScalarAsync()) == 1,
                "a lost acknowledgement followed by reconnect must not create a second message");
        }

        var concurrentClientId = Guid.NewGuid();
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => repository.PersistMessageAsync(
            created.Conversation.ConversationId, patient, concurrentClientId, "concurrent", new string('f', 64), default)));
        Require(concurrent.Count(result => result.Outcome == PersistConsultationMessageOutcome.Created) == 1 &&
                concurrent.All(result => result.Message?.MessageId == concurrent[0].Message?.MessageId),
            "simultaneous duplicate sends must persist exactly one message and return its id to all callers");

        _ = await repository.PersistMessageAsync(created.Conversation.ConversationId, doctor, Guid.NewGuid(), "b", new string('c', 64), default);
        var page = await repository.GetMessagesAsync(created.Conversation.ConversationId, patient, 1, null, default);
        Require(page.Items.Count == 1 && page.NextBeforeId is not null, "history should return a stable paged cursor");
        var next = await repository.GetMessagesAsync(created.Conversation.ConversationId, patient, 1, page.NextBeforeId, default);
        Require(next.Items.Count == 1 && next.Items[0].MessageId != page.Items[0].MessageId, "history cursor should not repeat messages");
        Require((await repository.GetMessagesAsync(created.Conversation.ConversationId, Guid.NewGuid(), 20, null, default)).Items.Count == 0,
            "non-participant must not read messages");

        var read = await repository.AdvanceReadCursorAsync(created.Conversation.ConversationId, patient, first.Message!.MessageId, default);
        var advance = await repository.AdvanceReadCursorAsync(created.Conversation.ConversationId, patient,
            concurrent[0].Message!.MessageId, default);
        var backwards = await repository.AdvanceReadCursorAsync(created.Conversation.ConversationId, patient, first.Message.MessageId, default);
        var foreign = await repository.AdvanceReadCursorAsync(created.Conversation.ConversationId, Guid.NewGuid(), first.Message.MessageId, default);
        Require(read.Outcome == AdvanceConsultationReadOutcome.Advanced, "read cursor should advance to a conversation message");
        Require(advance.Outcome == AdvanceConsultationReadOutcome.Advanced && advance.Cursor!.Version == 2,
            "read cursor version should increase on a forward advance");
        Require(backwards.Outcome == AdvanceConsultationReadOutcome.Unchanged &&
                backwards.Cursor!.LastReadMessageId == concurrent[0].Message!.MessageId,
            "read cursor should not move backwards");
        Require(foreign.Outcome == AdvanceConsultationReadOutcome.InvalidMessage, "non-participant read cursor should be rejected");

        var historyPatient = Guid.NewGuid();
        var history = await repository.CreateConversationAsync(NewCreate(historyPatient, historyPatient, Guid.NewGuid()), default);
        for (var index = 0; index < 21; index++)
            await repository.PersistMessageAsync(history.Conversation.ConversationId, historyPatient, Guid.NewGuid(),
                $"history {index}", new string('c', 64), default);
        var historyPage = await repository.GetMessagesAsync(history.Conversation.ConversationId, historyPatient, 20, null, default);
        Require(historyPage.Items.Count == 20 && historyPage.NextBeforeId is not null,
            "conversation history beyond twenty messages must provide a continuation cursor");
        var historyRemainder = await repository.GetMessagesAsync(history.Conversation.ConversationId, historyPatient,
            20, historyPage.NextBeforeId, default);
        Require(historyRemainder.Items.Count == 1
                && !historyPage.Items.Any(item => item.MessageId == historyRemainder.Items[0].MessageId),
            "reconnect history cursor must return the remaining message without duplicates");
        var otherConversation = await repository.CreateConversationAsync(NewCreate(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), default);
        var otherPatient = otherConversation.Conversation.PatientAccountId;
        var foreignConversationMessage = await repository.PersistMessageAsync(otherConversation.Conversation.ConversationId, otherPatient,
            Guid.NewGuid(), "foreign", new string('e', 64), default);
        var foreignCursor = await repository.AdvanceReadCursorAsync(created.Conversation.ConversationId, patient,
            foreignConversationMessage.Message!.MessageId, default);
        Require(foreignCursor.Outcome == AdvanceConsultationReadOutcome.InvalidMessage,
            "a message ID from another conversation must not be accepted as a read cursor");

        var quotaPatient = Guid.NewGuid();
        var quotaConversation = await repository.CreateConversationAsync(NewCreate(quotaPatient, quotaPatient, Guid.NewGuid()), default);
        var quotaResults = await Task.WhenAll(Enumerable.Range(0, 31).Select(_ => repository.PersistMessageAsync(
            quotaConversation.Conversation.ConversationId, quotaPatient, Guid.NewGuid(), "q", new string('e', 64), default)));
        var accepted = quotaResults.Count(result => result.Outcome == PersistConsultationMessageOutcome.Created);
        Require(accepted == 30, "only 30 sends in an account minute bucket should be accepted");

        await using var cleanup = source.CreateCommand("""
            DELETE FROM notification.consultation_conversations WHERE conversation_id = ANY(@conversation_ids);
            DELETE FROM notification.consultation_send_buckets WHERE account_id = ANY(@account_ids);
            """);
        cleanup.Parameters.AddWithValue("conversation_ids", new[]
        {
            created.Conversation.ConversationId,
            reassigned.Conversation.ConversationId,
            otherConversation.Conversation.ConversationId,
            quotaConversation.Conversation.ConversationId
        });
        cleanup.Parameters.AddWithValue("account_ids", new[] { patient, doctor, otherPatient, quotaPatient, historyPatient });
        await cleanup.ExecuteNonQueryAsync();
        Console.WriteLine("PASS: PostgreSQL consultation persistence, lost-ack replay, >20-message reconnect history, pagination, read cursor, account boundary, and rate limit.");
    }

    private static CreateConsultationCommand NewCreate(Guid actor, Guid patient, Guid doctor) => new(
        actor, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), patient, doctor, Guid.NewGuid(),
        Guid.NewGuid().ToString("N"), new string('d', 64));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
