BEGIN;
DO $$ BEGIN
    IF current_database() <> 'aura_dev' THEN RAISE EXCEPTION 'Run this migration only on aura_dev'; END IF;
END $$;
SET LOCAL ROLE aura_billing_migrator;

-- The verified purchaser receives clinic payment notifications; a clinic ID is never an account ID.
ALTER TABLE billing.orders ADD COLUMN IF NOT EXISTS notification_recipient_account_id uuid;
UPDATE billing.orders SET notification_recipient_account_id=billing_owner_id
WHERE billing_owner_type='ACCOUNT' AND notification_recipient_account_id IS NULL;

CREATE TABLE IF NOT EXISTS billing.payment_event_outbox (
    event_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    order_id uuid NOT NULL UNIQUE REFERENCES billing.orders(id),
    tenant_id uuid NOT NULL,
    recipient_account_id uuid,
    schema_version text NOT NULL DEFAULT '1.0' CHECK (schema_version='1.0'),
    state text NOT NULL DEFAULT 'PENDING' CHECK (state IN ('PENDING','IN_FLIGHT','SENT','HOLD')),
    attempts integer NOT NULL DEFAULT 0 CHECK (attempts>=0),
    available_at timestamptz NOT NULL DEFAULT now(),
    lease_token uuid,
    lease_expires_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    dispatched_at timestamptz,
    last_error text
);
CREATE INDEX IF NOT EXISTS ix_payment_event_outbox_due ON billing.payment_event_outbox(available_at)
WHERE state IN ('PENDING','IN_FLIGHT');
GRANT SELECT,INSERT,UPDATE ON billing.payment_event_outbox TO aura_billing_app;
COMMIT;
