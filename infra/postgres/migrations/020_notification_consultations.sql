-- SCRUM-391: durable consultation chats owned by Notification.
BEGIN;

DO $$
BEGIN
    IF current_database() <> 'aura_dev' THEN
        RAISE EXCEPTION 'Run this migration only on aura_dev';
    END IF;
END
$$;

SET LOCAL ROLE aura_notification_migrator;

ALTER TABLE notification.inbox_items
    DROP CONSTRAINT IF EXISTS ck_notification_inbox_items_type;
ALTER TABLE notification.inbox_items
    ADD CONSTRAINT ck_notification_inbox_items_type
    CHECK (type IN ('ANALYSIS_COMPLETED', 'ANALYSIS_FAILED', 'PAYMENT_SUCCESS', 'CONSULTATION_MESSAGE'));

CREATE TABLE IF NOT EXISTS notification.consultation_conversations (
    conversation_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    analysis_id uuid NOT NULL,
    clinic_context_id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    patient_id uuid NOT NULL,
    patient_account_id uuid NOT NULL,
    doctor_account_id uuid NOT NULL,
    assignment_id uuid NOT NULL,
    status varchar(12) NOT NULL DEFAULT 'OPEN'
        CHECK (status IN ('OPEN', 'CLOSED')),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_consultation_conversation_participants
        CHECK (patient_account_id <> doctor_account_id)
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_notification_consultation_open_scope
    ON notification.consultation_conversations
       (clinic_context_id, analysis_id, patient_id, doctor_account_id, assignment_id)
    WHERE status = 'OPEN';

CREATE INDEX IF NOT EXISTS ix_notification_consultation_patient_recent
    ON notification.consultation_conversations (patient_account_id, updated_at DESC, conversation_id DESC);
CREATE INDEX IF NOT EXISTS ix_notification_consultation_doctor_recent
    ON notification.consultation_conversations (doctor_account_id, updated_at DESC, conversation_id DESC);

CREATE TABLE IF NOT EXISTS notification.consultation_messages (
    message_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    conversation_id uuid NOT NULL REFERENCES notification.consultation_conversations(conversation_id) ON DELETE CASCADE,
    client_message_id uuid NOT NULL,
    sender_account_id uuid NOT NULL,
    body_text text NOT NULL,
    payload_fingerprint char(64) NOT NULL,
    sent_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT uq_notification_consultation_message_client
        UNIQUE (conversation_id, sender_account_id, client_message_id),
    CONSTRAINT uq_notification_consultation_message_order
        UNIQUE (conversation_id, sent_at, message_id),
    CONSTRAINT uq_notification_consultation_message_reference
        UNIQUE (conversation_id, message_id),
    CONSTRAINT ck_notification_consultation_message_text
        CHECK (char_length(body_text) BETWEEN 1 AND 4000 AND octet_length(body_text) <= 16384)
);

CREATE INDEX IF NOT EXISTS ix_notification_consultation_messages_page
    ON notification.consultation_messages (conversation_id, sent_at, message_id);
CREATE INDEX IF NOT EXISTS ix_notification_consultation_messages_sender
    ON notification.consultation_messages (sender_account_id, sent_at DESC);

CREATE TABLE IF NOT EXISTS notification.consultation_read_cursors (
    conversation_id uuid NOT NULL REFERENCES notification.consultation_conversations(conversation_id) ON DELETE CASCADE,
    account_id uuid NOT NULL,
    last_read_message_id uuid NOT NULL,
    read_at timestamptz NOT NULL DEFAULT now(),
    version integer NOT NULL DEFAULT 1 CHECK (version >= 1),
    PRIMARY KEY (conversation_id, account_id),
    CONSTRAINT fk_consultation_read_cursor_message
        FOREIGN KEY (conversation_id, last_read_message_id)
        REFERENCES notification.consultation_messages (conversation_id, message_id)
        ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS notification.consultation_create_idempotency (
    actor_account_id uuid NOT NULL,
    idempotency_key varchar(128) NOT NULL,
    request_fingerprint char(64) NOT NULL,
    conversation_id uuid NOT NULL REFERENCES notification.consultation_conversations(conversation_id) ON DELETE CASCADE,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (actor_account_id, idempotency_key),
    CONSTRAINT ck_consultation_idempotency_key
        CHECK (length(btrim(idempotency_key)) BETWEEN 1 AND 128)
);

CREATE TABLE IF NOT EXISTS notification.consultation_send_buckets (
    account_id uuid NOT NULL,
    window_started_at timestamptz NOT NULL,
    message_count smallint NOT NULL DEFAULT 1 CHECK (message_count BETWEEN 1 AND 30),
    PRIMARY KEY (account_id)
);

CREATE TABLE IF NOT EXISTS notification.consultation_notification_outbox (
    outbox_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    conversation_id uuid NOT NULL REFERENCES notification.consultation_conversations(conversation_id) ON DELETE CASCADE,
    message_id uuid NOT NULL,
    recipient_account_id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    status varchar(16) NOT NULL DEFAULT 'PENDING'
        CHECK (status IN ('PENDING', 'IN_FLIGHT', 'COMPLETED', 'CANCELED', 'DEAD_LETTER')),
    attempts integer NOT NULL DEFAULT 0 CHECK (attempts >= 0),
    next_attempt_at timestamptz NOT NULL DEFAULT now(),
    lease_token uuid,
    lease_expires_at timestamptz,
    last_error_code varchar(64),
    created_at timestamptz NOT NULL DEFAULT now(),
    completed_at timestamptz,
    CONSTRAINT uq_consultation_notification_message_recipient UNIQUE (message_id, recipient_account_id),
    CONSTRAINT fk_consultation_notification_outbox_message
        FOREIGN KEY (conversation_id, message_id)
        REFERENCES notification.consultation_messages (conversation_id, message_id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS ix_consultation_notification_outbox_due
    ON notification.consultation_notification_outbox (next_attempt_at, created_at)
    WHERE status IN ('PENDING', 'IN_FLIGHT');

COMMENT ON TABLE notification.consultation_conversations IS
    'Notification-owned durable patient/assigned-doctor consultation conversations (SCRUM-391).';
COMMENT ON TABLE notification.consultation_messages IS
    'Durable consultation text; payload_fingerprint detects changed clientMessageId replays.';

GRANT SELECT, INSERT ON notification.consultation_conversations TO aura_notification_app;
GRANT UPDATE (updated_at) ON notification.consultation_conversations TO aura_notification_app;
GRANT SELECT, INSERT ON notification.consultation_messages TO aura_notification_app;
GRANT SELECT, INSERT ON notification.consultation_read_cursors TO aura_notification_app;
GRANT UPDATE (last_read_message_id, read_at, version) ON notification.consultation_read_cursors TO aura_notification_app;
GRANT SELECT, INSERT ON notification.consultation_create_idempotency TO aura_notification_app;
GRANT SELECT, INSERT ON notification.consultation_send_buckets TO aura_notification_app;
GRANT UPDATE (window_started_at, message_count) ON notification.consultation_send_buckets TO aura_notification_app;
GRANT SELECT, INSERT ON notification.consultation_notification_outbox TO aura_notification_app;
GRANT UPDATE (status, attempts, next_attempt_at, lease_token, lease_expires_at, last_error_code, completed_at)
    ON notification.consultation_notification_outbox TO aura_notification_app;

COMMIT;
