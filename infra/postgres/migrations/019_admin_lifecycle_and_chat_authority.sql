BEGIN;

DO $$
BEGIN
    IF current_database() <> 'aura_dev' THEN
        RAISE EXCEPTION 'Run this migration only on aura_dev';
    END IF;
END
$$;

-- 1. Identity account version, display_name, and append-only lifecycle audit
ALTER TABLE identity.accounts
    ADD COLUMN IF NOT EXISTS display_name VARCHAR(100) NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS version INT NOT NULL DEFAULT 1 CHECK (version >= 1);

CREATE TABLE IF NOT EXISTS identity.account_lifecycle_audit (
    audit_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    actor_account_id UUID NOT NULL,
    target_account_id UUID NOT NULL REFERENCES identity.accounts(account_id) ON DELETE RESTRICT,
    action VARCHAR(64) NOT NULL,
    previous_status VARCHAR(32) NULL,
    new_status VARCHAR(32) NULL,
    previous_roles TEXT[] NULL,
    new_roles TEXT[] NULL,
    version INT NOT NULL CHECK (version >= 1),
    reason TEXT NOT NULL CHECK (length(btrim(reason)) BETWEEN 1 AND 1000),
    correlation_id VARCHAR(64) NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_account_lifecycle_audit_target
    ON identity.account_lifecycle_audit(target_account_id, created_at DESC);

GRANT SELECT, INSERT ON identity.account_lifecycle_audit TO aura_identity_app;
REVOKE UPDATE, DELETE ON identity.account_lifecycle_audit FROM aura_identity_app;

-- Preserve the original M6 audit shape separately from M1's owner outbox payloads.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns
               WHERE table_schema='profile' AND table_name='audit_events' AND column_name='source_service')
       AND to_regclass('profile.clinic_lifecycle_audit_events') IS NULL THEN
        ALTER TABLE profile.audit_events RENAME TO clinic_lifecycle_audit_events;
    END IF;
END $$;

-- 2. Profile clinic version and append-only lifecycle history
ALTER TABLE profile.clinics
    ADD COLUMN IF NOT EXISTS version INT NOT NULL DEFAULT 1 CHECK (version >= 1);

CREATE TABLE IF NOT EXISTS profile.clinic_lifecycle_audit_events (
    event_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    source_service VARCHAR(64) NOT NULL,
    actor_account_id UUID NULL,
    action VARCHAR(64) NOT NULL,
    resource_type VARCHAR(64) NOT NULL,
    resource_id_hash VARCHAR(64) NOT NULL,
    correlation_id VARCHAR(64) NOT NULL,
    outcome VARCHAR(16) NOT NULL CHECK (outcome IN ('ALLOW', 'DENY', 'FAIL')),
    occurred_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_profile_audit_events_occurred
    ON profile.clinic_lifecycle_audit_events(occurred_at DESC, event_id DESC);

GRANT SELECT, INSERT ON profile.clinic_lifecycle_audit_events TO aura_profile_app;
REVOKE UPDATE, DELETE ON profile.clinic_lifecycle_audit_events FROM aura_profile_app;

COMMIT;
