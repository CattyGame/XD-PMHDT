-- SCRUM-385: source-linked alerts, append-only feedback and protected export snapshots.
BEGIN;

CREATE TABLE IF NOT EXISTS analysis.alerts (
    id uuid PRIMARY KEY,
    tenant_id uuid NOT NULL,
    clinic_id uuid NOT NULL,
    analysis_id uuid NOT NULL,
    result_id uuid NOT NULL,
    review_version integer NULL,
    rule_version integer NOT NULL CHECK (rule_version > 0),
    kind varchar(32) NOT NULL CHECK (kind IN ('CLINICIAN_REVIEW_HIGH','RESEARCH_NOTICE')),
    state varchar(16) NOT NULL CHECK (state IN ('OPEN','ACKNOWLEDGED','CLOSED')),
    state_version integer NOT NULL DEFAULT 1 CHECK (state_version > 0),
    reason varchar(1000) NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT fk_alert_analysis FOREIGN KEY (tenant_id, analysis_id)
        REFERENCES analysis.images (tenant_id, id),
    CONSTRAINT fk_alert_result FOREIGN KEY (tenant_id, analysis_id, result_id)
        REFERENCES analysis.analysis_results (tenant_id, image_id, id)
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_alert_source
    ON analysis.alerts (tenant_id, analysis_id, result_id, COALESCE(review_version, 0), rule_version, kind);
CREATE INDEX IF NOT EXISTS ix_alerts_clinic_active
    ON analysis.alerts (tenant_id, clinic_id, state, updated_at DESC, id DESC);

CREATE TABLE IF NOT EXISTS analysis.alert_history (
    id uuid PRIMARY KEY,
    tenant_id uuid NOT NULL,
    alert_id uuid NOT NULL REFERENCES analysis.alerts(id),
    actor_id uuid NOT NULL,
    from_state varchar(16) NULL,
    to_state varchar(16) NOT NULL,
    state_version integer NOT NULL CHECK (state_version > 0),
    reason varchar(1000) NULL,
    correlation_id varchar(64) NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_alert_history_alert ON analysis.alert_history (tenant_id, alert_id, state_version);

CREATE TABLE IF NOT EXISTS analysis.feedback (
    id uuid PRIMARY KEY,
    tenant_id uuid NOT NULL,
    clinic_id uuid NOT NULL,
    analysis_id uuid NOT NULL,
    result_id uuid NOT NULL,
    model_version varchar(100) NOT NULL,
    review_version integer NOT NULL CHECK (review_version >= 0),
    doctor_id uuid NOT NULL,
    kind varchar(32) NOT NULL CHECK (kind IN ('SEGMENTATION','VESSEL_TYPE','CALIBER','QUALITY','OTHER')),
    comment varchar(4000) NULL,
    correlation_id varchar(64) NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT fk_feedback_analysis FOREIGN KEY (tenant_id, analysis_id)
        REFERENCES analysis.images (tenant_id, id),
    CONSTRAINT fk_feedback_result FOREIGN KEY (tenant_id, analysis_id, result_id)
        REFERENCES analysis.analysis_results (tenant_id, image_id, id)
);
CREATE INDEX IF NOT EXISTS ix_feedback_analysis ON analysis.feedback (tenant_id, analysis_id, created_at DESC);
ALTER TABLE analysis.feedback DROP CONSTRAINT IF EXISTS feedback_review_version_check;
ALTER TABLE analysis.feedback ADD CONSTRAINT ck_feedback_review_version CHECK (review_version >= 0);

CREATE TABLE IF NOT EXISTS analysis.export_snapshots (
    id uuid PRIMARY KEY,
    tenant_id uuid NOT NULL,
    clinic_id uuid NOT NULL,
    actor_id uuid NOT NULL,
    kind varchar(24) NOT NULL CHECK (kind IN ('CAMPAIGN_REPORT','RESEARCH_EXPORT')),
    campaign_id uuid NULL,
    format varchar(8) NOT NULL CHECK (format IN ('CSV','PDF')),
    object_key text NOT NULL,
    content_type varchar(128) NOT NULL,
    sha256 char(64) NOT NULL CHECK (sha256 ~ '^[0-9a-f]{64}$'),
    fingerprint char(64) NOT NULL CHECK (fingerprint ~ '^[0-9a-f]{64}$'),
    idempotency_key varchar(128) NOT NULL,
    patient_scope uuid[] NOT NULL,
    snapshot jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL,
    CONSTRAINT uq_export_idempotency UNIQUE (tenant_id, actor_id, kind, idempotency_key)
);
CREATE INDEX IF NOT EXISTS ix_export_snapshots_scope
    ON analysis.export_snapshots (tenant_id, clinic_id, created_at DESC);

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'aura_analysis_app') THEN
        GRANT SELECT, INSERT ON analysis.alerts TO aura_analysis_app;
        GRANT UPDATE (state, state_version, reason, updated_at) ON analysis.alerts TO aura_analysis_app;
        GRANT SELECT, INSERT ON analysis.alert_history TO aura_analysis_app;
        REVOKE UPDATE, DELETE ON analysis.alert_history FROM aura_analysis_app;
        GRANT SELECT, INSERT ON analysis.feedback TO aura_analysis_app;
        REVOKE UPDATE, DELETE ON analysis.feedback FROM aura_analysis_app;
        GRANT SELECT, INSERT ON analysis.export_snapshots TO aura_analysis_app;
        REVOKE UPDATE, DELETE ON analysis.export_snapshots FROM aura_analysis_app;
    END IF;
END
$$;

COMMIT;
