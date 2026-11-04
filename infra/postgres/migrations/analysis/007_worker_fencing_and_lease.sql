BEGIN;

-- 007_worker_fencing_and_lease.sql: Add lease and fencing token columns to analysis.jobs for atomic claims and worker fencing
ALTER TABLE analysis.jobs
    ADD COLUMN IF NOT EXISTS lease_owner varchar(128) NULL,
    ADD COLUMN IF NOT EXISTS lease_expires_at timestamptz NULL,
    ADD COLUMN IF NOT EXISTS fencing_token bigint NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS call_intent_recorded boolean NOT NULL DEFAULT false;

CREATE INDEX IF NOT EXISTS ix_jobs_lease ON analysis.jobs (status, lease_expires_at);

COMMIT;
