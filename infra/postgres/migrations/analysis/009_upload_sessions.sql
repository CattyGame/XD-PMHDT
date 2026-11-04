BEGIN;
ALTER TABLE analysis.batches ADD COLUMN IF NOT EXISTS admission_complete boolean NOT NULL DEFAULT true;
CREATE TABLE IF NOT EXISTS analysis.upload_batches (
 id uuid PRIMARY KEY,
 requester_id uuid NOT NULL,
 tenant_id uuid NOT NULL,
 clinic_id uuid NULL,
 fingerprint char(64) NOT NULL,
 state jsonb NOT NULL,
 created_at timestamptz NOT NULL,
 expires_at timestamptz NOT NULL,
 updated_at timestamptz NOT NULL DEFAULT now(),
 FOREIGN KEY (tenant_id,id) REFERENCES analysis.batches(tenant_id,id),
 CHECK (expires_at > created_at)
);
CREATE INDEX IF NOT EXISTS ix_upload_batches_expiry ON analysis.upload_batches(expires_at);
ALTER TABLE analysis.upload_batches ADD COLUMN IF NOT EXISTS reconciliation_attempts integer NOT NULL DEFAULT 0;
ALTER TABLE analysis.upload_batches ADD COLUMN IF NOT EXISTS reconciliation_next_at timestamptz NOT NULL DEFAULT now();
CREATE INDEX IF NOT EXISTS ix_upload_batches_reconcile ON analysis.upload_batches(reconciliation_next_at,id) WHERE state->>'status'='FINALIZING';
GRANT SELECT,INSERT,UPDATE,DELETE ON analysis.upload_batches TO aura_analysis_app;
COMMIT;
