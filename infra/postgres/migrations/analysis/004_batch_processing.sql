-- SCRUM-343 / W4-M2-02: Batch Processing Database Migration
-- Supports batch metadata, status tracking, and child image references

BEGIN;

CREATE TABLE IF NOT EXISTS analysis.batches (
    id                      uuid         NOT NULL,
    tenant_id               uuid         NOT NULL,
    owner_id                uuid         NOT NULL,
    requester_id            uuid         NOT NULL,
    patient_id              uuid         NOT NULL,
    clinic_id               uuid         NULL,
    total_images            integer      NOT NULL DEFAULT 0,
    completed_images        integer      NOT NULL DEFAULT 0,
    failed_images           integer      NOT NULL DEFAULT 0,
    total_items             integer      GENERATED ALWAYS AS (total_images) STORED,
    completed_items         integer      GENERATED ALWAYS AS (completed_images) STORED,
    status                  varchar(32)  NOT NULL DEFAULT 'IN_PROGRESS',
    created_at              timestamptz  NOT NULL DEFAULT now(),
    updated_at              timestamptz  NOT NULL DEFAULT now(),
    CONSTRAINT pk_batches PRIMARY KEY (id),
    CONSTRAINT uq_batches_tenant_id UNIQUE (tenant_id, id),
    CONSTRAINT ck_batches_total_images CHECK (total_images >= 0 AND total_images <= 100),
    CONSTRAINT ck_batches_completed_images CHECK (completed_images >= 0),
    CONSTRAINT ck_batches_failed_images CHECK (failed_images >= 0),
    CONSTRAINT ck_batches_status CHECK (status IN ('IN_PROGRESS', 'COMPLETED', 'PARTIAL_COMPLETED', 'FAILED'))
);

CREATE INDEX IF NOT EXISTS ix_batches_patient ON analysis.batches (tenant_id, patient_id, created_at);
CREATE INDEX IF NOT EXISTS ix_batches_status  ON analysis.batches (tenant_id, status);

-- Link analysis.images to analysis.batches
ALTER TABLE analysis.images
    ADD COLUMN IF NOT EXISTS batch_id uuid NULL;

CREATE INDEX IF NOT EXISTS ix_images_batch ON analysis.images (tenant_id, batch_id);

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_images_batch') THEN
        ALTER TABLE analysis.images
            ADD CONSTRAINT fk_images_batch FOREIGN KEY (tenant_id, batch_id)
            REFERENCES analysis.batches (tenant_id, id);
    END IF;
END $$;

-- Permissions
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'aura_analysis_app') THEN
        GRANT SELECT, INSERT, UPDATE ON analysis.batches TO aura_analysis_app;
        GRANT SELECT, UPDATE (batch_id) ON analysis.images TO aura_analysis_app;
    END IF;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'aura_analysis_migrator') THEN
        GRANT ALL PRIVILEGES ON analysis.batches TO aura_analysis_migrator;
    END IF;
END $$;

COMMIT;
