BEGIN;
CREATE TABLE IF NOT EXISTS analysis.report_snapshots (
 id uuid PRIMARY KEY,
 tenant_id uuid NOT NULL,
 batch_id uuid NOT NULL,
 analysis_id uuid NULL,
 requester_id uuid NOT NULL,
 clinic_id uuid NULL,
 format varchar(8) NOT NULL CHECK(format IN ('pdf','csv','zip')),
 object_key text NOT NULL,
 content_type text NOT NULL,
 snapshot jsonb NOT NULL,
 snapshot_sha256 char(64) NOT NULL,
 created_at timestamptz NOT NULL DEFAULT now(),
 expires_at timestamptz NOT NULL,
 FOREIGN KEY(tenant_id,batch_id) REFERENCES analysis.batches(tenant_id,id)
);
GRANT SELECT,INSERT ON analysis.report_snapshots TO aura_analysis_app;
REVOKE UPDATE,DELETE ON analysis.report_snapshots FROM aura_analysis_app;
COMMIT;
