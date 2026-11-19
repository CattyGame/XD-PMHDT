BEGIN;
SET LOCAL ROLE aura_billing_migrator;

CREATE TABLE IF NOT EXISTS billing.catalog_publications (
    actor_account_id uuid NOT NULL,
    idempotency_key uuid NOT NULL,
    request_hash char(64) NOT NULL,
    price_version_id uuid NOT NULL REFERENCES billing.package_price_versions(id),
    PRIMARY KEY (actor_account_id, idempotency_key)
);
GRANT SELECT, INSERT ON billing.catalog_publications TO aura_billing_app;

CREATE INDEX IF NOT EXISTS ix_billing_order_history
    ON billing.orders (tenant_id, billing_owner_type, billing_owner_id, environment, created_at DESC, id DESC);
CREATE INDEX IF NOT EXISTS ix_billing_ledger_usage
    ON billing.credit_ledger_entries (wallet_id, tenant_id, created_at DESC, id DESC);
CREATE INDEX IF NOT EXISTS ix_billing_payments_success_period
    ON billing.payments (updated_at, order_id) WHERE status = 'SUCCESS';

RESET ROLE;
SET LOCAL ROLE aura_notification_migrator;

CREATE TABLE IF NOT EXISTS notification.push_templates (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    event_type varchar(40) NOT NULL UNIQUE,
    title varchar(100) NOT NULL,
    body varchar(250) NOT NULL,
    enabled boolean NOT NULL DEFAULT true,
    version integer NOT NULL DEFAULT 1 CHECK (version > 0)
);

INSERT INTO notification.push_templates (event_type, title, body)
SELECT event_type, '{{app_name}}', 'Có thông báo mới. Đăng nhập để xem chi tiết.'
FROM unnest(ARRAY['ANALYSIS_COMPLETED','ANALYSIS_FAILED','PAYMENT_SUCCESS','HIGH_RISK_WARNING','CREDIT_LOW']) AS event_type
ON CONFLICT (event_type) DO NOTHING;

GRANT SELECT ON notification.push_templates TO aura_notification_app;
GRANT UPDATE (title, body, enabled, version) ON notification.push_templates TO aura_notification_app;
COMMIT;
