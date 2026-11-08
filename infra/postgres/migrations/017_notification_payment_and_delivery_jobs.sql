-- ====================================================================
-- AURA Sprint 5 — Notification Payment & Durable Delivery Jobs (SCRUM-364 & SCRUM-347)
-- Supports PAYMENT_SUCCESS inbox items and durable FCM delivery jobs.
-- ====================================================================

BEGIN;

DO $$
BEGIN
    IF current_database() <> 'aura_dev' THEN
        RAISE EXCEPTION 'Run this migration only on aura_dev';
    END IF;
END
$$;

SET LOCAL ROLE aura_notification_migrator;

-- 1. Extend notification.inbox_items for payment notifications
ALTER TABLE notification.inbox_items
    DROP CONSTRAINT IF EXISTS inbox_items_type_check,
    DROP CONSTRAINT IF EXISTS ck_notification_inbox_items_type;

ALTER TABLE notification.inbox_items
    ADD CONSTRAINT ck_notification_inbox_items_type
    CHECK (type IN ('ANALYSIS_COMPLETED', 'ANALYSIS_FAILED', 'PAYMENT_SUCCESS'));

-- Allow analysis_id to be nullable for non-analysis events (such as billing payments)
ALTER TABLE notification.inbox_items
    ALTER COLUMN analysis_id DROP NOT NULL;

-- Add order_id column for payment tracing
ALTER TABLE notification.inbox_items
    ADD COLUMN IF NOT EXISTS order_id uuid NULL;

-- 2. Create durable notification.delivery_jobs table (SCRUM-364)
CREATE TABLE IF NOT EXISTS notification.delivery_jobs (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    inbox_item_id uuid REFERENCES notification.inbox_items(id) ON DELETE CASCADE,
    account_id uuid NOT NULL,
    device_id uuid NOT NULL,
    fcm_token_hash bytea NOT NULL,
    status varchar(24) NOT NULL DEFAULT 'PENDING'
        CHECK (status IN ('PENDING', 'IN_FLIGHT', 'COMPLETED', 'FAILED', 'DEAD_LETTER')),
    attempts integer NOT NULL DEFAULT 0,
    max_attempts integer NOT NULL DEFAULT 5,
    next_attempt_at timestamptz NOT NULL DEFAULT now(),
    lease_token uuid,
    lease_expires_at timestamptz,
    last_error text,
    created_at timestamptz NOT NULL DEFAULT now(),
    completed_at timestamptz,
    CONSTRAINT uq_notification_delivery_job_device UNIQUE (inbox_item_id, device_id)
);

CREATE INDEX IF NOT EXISTS ix_notification_delivery_jobs_pending
    ON notification.delivery_jobs (status, next_attempt_at)
    WHERE status IN ('PENDING', 'IN_FLIGHT');

CREATE INDEX IF NOT EXISTS ix_notification_delivery_jobs_account
    ON notification.delivery_jobs (account_id, created_at DESC);

COMMENT ON TABLE notification.delivery_jobs IS
    'Durable delivery jobs for FCM Web Push with lease fencing, exponential backoff, and dead-letter routing (SCRUM-364).';

-- Grant permissions to runtime role
GRANT SELECT, INSERT, UPDATE, DELETE ON notification.delivery_jobs TO aura_notification_app;
GRANT SELECT, INSERT, UPDATE ON notification.inbox_items TO aura_notification_app;

COMMIT;
