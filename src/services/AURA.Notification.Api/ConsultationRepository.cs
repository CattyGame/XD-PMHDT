using Npgsql;
using NpgsqlTypes;

namespace AURA.Notification.Api;

public sealed class ConsultationRepository(NpgsqlDataSource dataSource)
{
    public async Task<CreateConsultationResult> CreateConversationAsync(CreateConsultationCommand request, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await using (var lockCommand = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@lock_key, 0));", connection, transaction))
        {
            lockCommand.Parameters.AddWithValue("lock_key", $"consultation:{request.ActorAccountId:D}:{request.IdempotencyKey}");
            await lockCommand.ExecuteNonQueryAsync(ct);
        }

        await using (var replay = new NpgsqlCommand("""
            SELECT i.request_fingerprint, c.conversation_id, c.analysis_id, c.clinic_context_id, c.tenant_id,
                   c.patient_id, c.patient_account_id, c.doctor_account_id, c.assignment_id,
                   c.status, c.created_at
            FROM notification.consultation_create_idempotency i
            JOIN notification.consultation_conversations c USING (conversation_id)
            WHERE i.actor_account_id=@actor_id AND i.idempotency_key=@key;
            """, connection, transaction))
        {
            replay.Parameters.AddWithValue("actor_id", request.ActorAccountId);
            replay.Parameters.AddWithValue("key", request.IdempotencyKey);
            await using var reader = await replay.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var fingerprintMatches = string.Equals(reader.GetString(0).Trim(), request.RequestFingerprint, StringComparison.Ordinal);
                var conversation = ReadConversation(reader, 1);
                await reader.DisposeAsync();
                await transaction.CommitAsync(ct);
                return new CreateConsultationResult(
                    fingerprintMatches ? CreateConsultationOutcome.Replay : CreateConsultationOutcome.PayloadConflict,
                    conversation);
            }
        }

        ConsultationConversation created;
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO notification.consultation_conversations
                (analysis_id, clinic_context_id, tenant_id, patient_id, patient_account_id, doctor_account_id, assignment_id)
            VALUES (@analysis_id, @clinic_id, @tenant_id, @patient_id, @patient_account_id, @doctor_account_id, @assignment_id)
            ON CONFLICT (clinic_context_id, analysis_id, patient_id, doctor_account_id, assignment_id) WHERE status='OPEN'
            DO UPDATE SET updated_at=now()
            RETURNING conversation_id, analysis_id, clinic_context_id, tenant_id, patient_id, patient_account_id,
                      doctor_account_id, assignment_id, status, created_at;
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("analysis_id", request.AnalysisId);
            insert.Parameters.AddWithValue("clinic_id", request.ClinicContextId);
            insert.Parameters.AddWithValue("tenant_id", request.TenantId);
            insert.Parameters.AddWithValue("patient_id", request.PatientId);
            insert.Parameters.AddWithValue("patient_account_id", request.PatientAccountId);
            insert.Parameters.AddWithValue("doctor_account_id", request.DoctorAccountId);
            insert.Parameters.AddWithValue("assignment_id", request.AssignmentId);
            await using var reader = await insert.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw new NpgsqlException("Conversation insert returned no row.");
            created = ReadConversation(reader, 0);
        }

        await using (var idempotency = new NpgsqlCommand("""
            INSERT INTO notification.consultation_create_idempotency
                (actor_account_id, idempotency_key, request_fingerprint, conversation_id)
            VALUES (@actor_id, @key, @fingerprint, @conversation_id);
            """, connection, transaction))
        {
            idempotency.Parameters.AddWithValue("actor_id", request.ActorAccountId);
            idempotency.Parameters.AddWithValue("key", request.IdempotencyKey);
            idempotency.Parameters.AddWithValue("fingerprint", request.RequestFingerprint);
            idempotency.Parameters.AddWithValue("conversation_id", created.ConversationId);
            await idempotency.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return new CreateConsultationResult(CreateConsultationOutcome.Created, created);
    }

    public async Task<ConsultationConversation?> GetConversationAsync(Guid conversationId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT conversation_id, analysis_id, clinic_context_id, tenant_id, patient_id, patient_account_id,
                   doctor_account_id, assignment_id, status, created_at
            FROM notification.consultation_conversations WHERE conversation_id=@conversation_id;
            """);
        command.Parameters.AddWithValue("conversation_id", conversationId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadConversation(reader, 0) : null;
    }

    public async Task<ConsultationConversationsPageData> ListConversationsAsync(Guid accountId, int limit, Guid? beforeId, CancellationToken ct)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        const string sql = """
            WITH anchor AS (
                SELECT updated_at, conversation_id FROM notification.consultation_conversations
                WHERE conversation_id=@before_id AND (patient_account_id=@account_id OR doctor_account_id=@account_id)
            ), page AS (
                SELECT c.conversation_id, c.analysis_id, c.clinic_context_id, c.tenant_id, c.patient_id,
                       c.patient_account_id, c.doctor_account_id, c.assignment_id, c.status, c.created_at, c.updated_at,
                       (SELECT count(*)::int FROM notification.consultation_messages m
                        LEFT JOIN notification.consultation_read_cursors r
                          ON r.conversation_id=m.conversation_id AND r.account_id=@account_id
                        WHERE m.conversation_id=c.conversation_id AND m.sender_account_id<>@account_id
                          AND (r.last_read_message_id IS NULL OR (m.sent_at,m.message_id) >
                               (SELECT rm.sent_at,rm.message_id FROM notification.consultation_messages rm
                                WHERE rm.conversation_id=r.conversation_id AND rm.message_id=r.last_read_message_id))) AS unread_count
                FROM notification.consultation_conversations c
                WHERE (c.patient_account_id=@account_id OR c.doctor_account_id=@account_id)
                  AND (@before_id IS NULL OR (c.updated_at,c.conversation_id) < (SELECT updated_at,conversation_id FROM anchor))
                ORDER BY c.updated_at DESC,c.conversation_id DESC LIMIT @take
            )
            SELECT conversation_id,analysis_id,clinic_context_id,tenant_id,patient_id,patient_account_id,doctor_account_id,
                   assignment_id,status,created_at,unread_count,updated_at FROM page;
            """;

        var rows = new List<ConsultationConversationListItem>();
        await using (var command = dataSource.CreateCommand(sql))
        {
            command.Parameters.AddWithValue("account_id", accountId);
            command.Parameters.Add("before_id", NpgsqlDbType.Uuid).Value = (object?)beforeId ?? DBNull.Value;
            command.Parameters.AddWithValue("take", limit + 1);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                rows.Add(new ConsultationConversationListItem(ReadConversation(reader, 0), reader.GetInt32(10), reader.GetFieldValue<DateTimeOffset>(11)));
        }

        var hasMore = rows.Count > limit;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        return new ConsultationConversationsPageData(rows, hasMore ? rows[^1].Conversation.ConversationId.ToString("D") : null);
    }

    public async Task<ConsultationMessagesPage> GetMessagesAsync(Guid conversationId, Guid accountId, int limit, Guid? beforeId, CancellationToken ct)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        const string sql = """
            WITH anchor AS (
                SELECT sent_at,message_id FROM notification.consultation_messages
                WHERE conversation_id=@conversation_id AND message_id=@before_id
            ), page AS (
            SELECT m.message_id,m.conversation_id,m.client_message_id,m.sender_account_id,m.body_text,m.sent_at,
                   CASE WHEN EXISTS (
                       SELECT 1 FROM notification.consultation_read_cursors r
                       JOIN notification.consultation_messages rm
                         ON rm.conversation_id=r.conversation_id AND rm.message_id=r.last_read_message_id
                       WHERE r.conversation_id=m.conversation_id
                         AND r.account_id=CASE WHEN c.patient_account_id=m.sender_account_id
                                               THEN c.doctor_account_id ELSE c.patient_account_id END
                         AND (rm.sent_at,rm.message_id) >= (m.sent_at,m.message_id)
                   ) THEN 'READ' ELSE 'PERSISTED' END AS delivery_state
                FROM notification.consultation_messages m
            JOIN notification.consultation_conversations c ON c.conversation_id=m.conversation_id
                WHERE m.conversation_id=@conversation_id
                  AND (c.patient_account_id=@account_id OR c.doctor_account_id=@account_id)
                  AND (@before_id IS NULL OR (m.sent_at,m.message_id) < (SELECT sent_at,message_id FROM anchor))
                ORDER BY m.sent_at DESC,m.message_id DESC LIMIT @take
            )
            SELECT message_id,conversation_id,client_message_id,sender_account_id,body_text,sent_at,delivery_state FROM page
            ORDER BY sent_at,message_id;
            """;
        var rows = new List<ConsultationMessage>();
        await using (var command = dataSource.CreateCommand(sql))
        {
            command.Parameters.AddWithValue("conversation_id", conversationId);
            command.Parameters.AddWithValue("account_id", accountId);
            command.Parameters.Add("before_id", NpgsqlDbType.Uuid).Value = (object?)beforeId ?? DBNull.Value;
            command.Parameters.AddWithValue("take", limit + 1);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) rows.Add(ReadMessage(reader, 0));
        }
        var hasMore = rows.Count > limit;
        if (hasMore) rows.RemoveAt(0); // earliest row is outside the newest-first page selected by SQL
        return new ConsultationMessagesPage(rows, hasMore && rows.Count > 0 ? rows[0].MessageId : null);
    }

    public async Task<PersistConsultationMessageResult> PersistMessageAsync(
        Guid conversationId, Guid senderAccountId, Guid clientMessageId, string text, string fingerprint, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@lock_key, 0));", connection, transaction))
        {
            lockCommand.Parameters.AddWithValue("lock_key", $"consultation-send:{senderAccountId:D}");
            await lockCommand.ExecuteNonQueryAsync(ct);
        }

        await using (var lookup = new NpgsqlCommand("""
            SELECT message_id,conversation_id,client_message_id,sender_account_id,body_text,sent_at,
                   payload_fingerprint,'PERSISTED'::text AS delivery_state
            FROM notification.consultation_messages
            WHERE conversation_id=@conversation_id AND sender_account_id=@sender_account_id AND client_message_id=@client_message_id;
            """, connection, transaction))
        {
            lookup.Parameters.AddWithValue("conversation_id", conversationId);
            lookup.Parameters.AddWithValue("sender_account_id", senderAccountId);
            lookup.Parameters.AddWithValue("client_message_id", clientMessageId);
            await using var reader = await lookup.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var samePayload = string.Equals(reader.GetString(6).Trim(), fingerprint, StringComparison.Ordinal);
                var replayed = ReadMessage(reader, 0, 7);
                await reader.DisposeAsync();
                await transaction.CommitAsync(ct);
                return new PersistConsultationMessageResult(
                    samePayload ? PersistConsultationMessageOutcome.Replay : PersistConsultationMessageOutcome.PayloadConflict,
                    replayed);
            }
        }

        Guid offlineRecipientAccountId;
        Guid tenantId;
        await using (var participant = new NpgsqlCommand("""
            SELECT patient_account_id, doctor_account_id, tenant_id
            FROM notification.consultation_conversations
            WHERE conversation_id=@conversation_id
              AND (patient_account_id=@sender_account_id OR doctor_account_id=@sender_account_id);
            """, connection, transaction))
        {
            participant.Parameters.AddWithValue("conversation_id", conversationId);
            participant.Parameters.AddWithValue("sender_account_id", senderAccountId);
            await using var reader = await participant.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                await transaction.CommitAsync(ct);
                return new PersistConsultationMessageResult(PersistConsultationMessageOutcome.NotParticipant, null);
            }
            var patientAccountId = reader.GetGuid(0);
            var doctorAccountId = reader.GetGuid(1);
            tenantId = reader.GetGuid(2);
            offlineRecipientAccountId = patientAccountId == senderAccountId ? doctorAccountId : patientAccountId;
        }

        await using (var quota = new NpgsqlCommand("""
            INSERT INTO notification.consultation_send_buckets(account_id,window_started_at,message_count)
            VALUES (@account_id,now(),1)
            ON CONFLICT (account_id) DO UPDATE
                SET window_started_at=CASE
                        WHEN notification.consultation_send_buckets.window_started_at <= now() - interval '1 minute'
                        THEN now() ELSE notification.consultation_send_buckets.window_started_at END,
                    message_count=CASE
                        WHEN notification.consultation_send_buckets.window_started_at <= now() - interval '1 minute'
                        THEN 1 ELSE notification.consultation_send_buckets.message_count+1 END
                WHERE notification.consultation_send_buckets.window_started_at <= now() - interval '1 minute'
                   OR notification.consultation_send_buckets.message_count < 30
            RETURNING message_count;
            """, connection, transaction))
        {
            quota.Parameters.AddWithValue("account_id", senderAccountId);
            if (await quota.ExecuteScalarAsync(ct) is null)
            {
                await transaction.CommitAsync(ct);
                return new PersistConsultationMessageResult(PersistConsultationMessageOutcome.RateLimited, null);
            }
        }

        ConsultationMessage persisted;
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO notification.consultation_messages
                (conversation_id,client_message_id,sender_account_id,body_text,payload_fingerprint)
            VALUES (@conversation_id,@client_message_id,@sender_account_id,@body_text,@fingerprint)
            RETURNING message_id,conversation_id,client_message_id,sender_account_id,body_text,sent_at;
            """, connection, transaction))
        {
            AddMessageParameters(insert, conversationId, senderAccountId, clientMessageId, text, fingerprint);
            await using var reader = await insert.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw new NpgsqlException("Message insert returned no row.");
            persisted = ReadMessage(reader, 0);
        }
        await using (var update = new NpgsqlCommand("UPDATE notification.consultation_conversations SET updated_at=now() WHERE conversation_id=@conversation_id;", connection, transaction))
        {
            update.Parameters.AddWithValue("conversation_id", conversationId);
            await update.ExecuteNonQueryAsync(ct);
        }
        await using (var outbox = new NpgsqlCommand("""
            INSERT INTO notification.consultation_notification_outbox
                (conversation_id, message_id, recipient_account_id, tenant_id)
            VALUES (@conversation_id, @message_id, @recipient_account_id, @tenant_id)
            ON CONFLICT (message_id, recipient_account_id) DO NOTHING;
            """, connection, transaction))
        {
            outbox.Parameters.AddWithValue("conversation_id", conversationId);
            outbox.Parameters.AddWithValue("message_id", persisted.MessageId);
            outbox.Parameters.AddWithValue("recipient_account_id", offlineRecipientAccountId);
            outbox.Parameters.AddWithValue("tenant_id", tenantId);
            await outbox.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return new PersistConsultationMessageResult(PersistConsultationMessageOutcome.Created, persisted);
    }

    public async Task<(AdvanceConsultationReadOutcome Outcome, ConsultationReadCursor? Cursor)> AdvanceReadCursorAsync(
        Guid conversationId, Guid accountId, Guid lastReadMessageId, CancellationToken ct)
    {
        const string membershipSql = """
            SELECT EXISTS (
                SELECT 1 FROM notification.consultation_conversations c
                JOIN notification.consultation_messages m ON m.conversation_id=c.conversation_id
                WHERE c.conversation_id=@conversation_id AND m.message_id=@message_id
                  AND (c.patient_account_id=@account_id OR c.doctor_account_id=@account_id)
            );
            """;
        await using (var validate = dataSource.CreateCommand(membershipSql))
        {
            validate.Parameters.AddWithValue("conversation_id", conversationId);
            validate.Parameters.AddWithValue("account_id", accountId);
            validate.Parameters.AddWithValue("message_id", lastReadMessageId);
            if (await validate.ExecuteScalarAsync(ct) is not true)
                return (AdvanceConsultationReadOutcome.InvalidMessage, null);
        }

        const string sql = """
            INSERT INTO notification.consultation_read_cursors(conversation_id,account_id,last_read_message_id,read_at)
            SELECT @conversation_id,@account_id,@message_id,now()
            WHERE EXISTS (
                SELECT 1 FROM notification.consultation_conversations c
                WHERE c.conversation_id=@conversation_id
                  AND (c.patient_account_id=@account_id OR c.doctor_account_id=@account_id)
            ) AND EXISTS (
                SELECT 1 FROM notification.consultation_messages m
                WHERE m.conversation_id=@conversation_id AND m.message_id=@message_id
            )
            ON CONFLICT (conversation_id,account_id) DO UPDATE
                SET last_read_message_id=EXCLUDED.last_read_message_id,read_at=EXCLUDED.read_at,
                    version=notification.consultation_read_cursors.version+1
                WHERE (SELECT ROW(sent_at,message_id) FROM notification.consultation_messages
                       WHERE conversation_id=@conversation_id AND message_id=EXCLUDED.last_read_message_id) >
                      (SELECT ROW(sent_at,message_id) FROM notification.consultation_messages
                       WHERE conversation_id=@conversation_id AND message_id=notification.consultation_read_cursors.last_read_message_id)
            RETURNING last_read_message_id,read_at,version;
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("conversation_id", conversationId);
        command.Parameters.AddWithValue("account_id", accountId);
        command.Parameters.AddWithValue("message_id", lastReadMessageId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
            return (AdvanceConsultationReadOutcome.Advanced,
                new ConsultationReadCursor(reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetInt32(2)));

        var prior = await GetReadCursorAsync(conversationId, accountId, ct);
        if (prior is not null) return (AdvanceConsultationReadOutcome.Unchanged, prior);
        return (AdvanceConsultationReadOutcome.InvalidMessage, null);
    }

    public async Task<ConsultationReadCursor?> GetReadCursorAsync(Guid conversationId, Guid accountId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("SELECT last_read_message_id,read_at,version FROM notification.consultation_read_cursors WHERE conversation_id=@conversation_id AND account_id=@account_id;");
        command.Parameters.AddWithValue("conversation_id", conversationId);
        command.Parameters.AddWithValue("account_id", accountId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new ConsultationReadCursor(reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetInt32(2))
            : null;
    }

    private static void AddMessageParameters(NpgsqlCommand command, Guid conversationId, Guid senderAccountId, Guid clientMessageId, string text, string fingerprint)
    {
        command.Parameters.AddWithValue("conversation_id", conversationId);
        command.Parameters.AddWithValue("client_message_id", clientMessageId);
        command.Parameters.AddWithValue("sender_account_id", senderAccountId);
        command.Parameters.AddWithValue("body_text", text);
        command.Parameters.AddWithValue("fingerprint", fingerprint);
    }

    private static ConsultationConversation ReadConversation(NpgsqlDataReader reader, int start) => new(
        reader.GetGuid(start), reader.GetGuid(start + 1), reader.GetGuid(start + 2), reader.GetGuid(start + 3),
        reader.GetGuid(start + 4), reader.GetGuid(start + 5), reader.GetGuid(start + 6), reader.GetGuid(start + 7),
        reader.GetString(start + 8), reader.GetFieldValue<DateTimeOffset>(start + 9));

    private static ConsultationMessage ReadMessage(NpgsqlDataReader reader, int start, int deliveryStateColumn = 6) => new(
        reader.GetGuid(start), reader.GetGuid(start + 1), reader.GetGuid(start + 2), reader.GetGuid(start + 3),
        reader.GetString(start + 4), reader.GetFieldValue<DateTimeOffset>(start + 5),
        reader.FieldCount > start + deliveryStateColumn && !reader.IsDBNull(start + deliveryStateColumn)
            ? reader.GetString(start + deliveryStateColumn)
            : "PERSISTED");
}
