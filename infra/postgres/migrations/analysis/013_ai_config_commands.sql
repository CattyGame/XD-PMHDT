-- Durable admin AI runtime config command journal and deterministic audit reconciliation (F3 / SCRUM-392).
BEGIN;

CREATE TABLE IF NOT EXISTS analysis.ai_config_commands (
    command_id uuid PRIMARY KEY,
    idempotency_key varchar(128) NOT NULL UNIQUE,
    action varchar(32) NOT NULL CHECK (action IN ('ACTIVATE_MANIFEST', 'ROLLBACK_MANIFEST')),
    actor_account_id uuid NOT NULL,
    expected_version integer NOT NULL CHECK (expected_version >= 1),
    artifact_manifest_id uuid NULL,
    threshold_config_sha256 char(64) NULL CHECK (threshold_config_sha256 IS NULL OR threshold_config_sha256 ~ '^[0-9a-f]{64}$'),
    reason varchar(1000) NOT NULL,
    payload_fingerprint char(64) NOT NULL CHECK (payload_fingerprint ~ '^[0-9a-f]{64}$'),
    correlation_id varchar(128) NOT NULL,
    status varchar(16) NOT NULL CHECK (status IN ('PENDING', 'COMMITTED', 'FAILED')),
    committed_version integer NULL CHECK (committed_version IS NULL OR committed_version >= 1),
    committed_manifest_id uuid NULL,
    committed_sha256 char(64) NULL CHECK (committed_sha256 IS NULL OR committed_sha256 ~ '^[0-9a-f]{64}$'),
    committed_activated_at timestamptz NULL,
    failure_code varchar(64) NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_ai_config_commands_pending
    ON analysis.ai_config_commands (status, created_at ASC)
    WHERE status = 'PENDING';

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'aura_analysis_app') THEN
        GRANT SELECT, INSERT ON analysis.ai_config_commands TO aura_analysis_app;
        GRANT UPDATE (status, committed_version, committed_manifest_id, committed_sha256, committed_activated_at, failure_code, updated_at)
            ON analysis.ai_config_commands TO aura_analysis_app;
        REVOKE DELETE ON analysis.ai_config_commands FROM aura_analysis_app;
    END IF;
END
$$;

COMMIT;
