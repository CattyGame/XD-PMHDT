-- SCRUM-367: extend the existing canonical batch row and keep progress durable.
-- Redis is a rebuildable cache; PostgreSQL image states remain authoritative.

BEGIN;

ALTER TABLE analysis.batches
    ADD COLUMN IF NOT EXISTS clinic_id uuid NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_attribute
        WHERE attrelid = 'analysis.batches'::regclass
          AND attname = 'total_items' AND NOT attisdropped
    ) THEN
        ALTER TABLE analysis.batches
            ADD COLUMN total_items integer GENERATED ALWAYS AS (total_images) STORED;
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM pg_attribute
        WHERE attrelid = 'analysis.batches'::regclass
          AND attname = 'completed_items' AND NOT attisdropped
    ) THEN
        ALTER TABLE analysis.batches
            ADD COLUMN completed_items integer GENERATED ALWAYS AS (completed_images) STORED;
    END IF;
END
$$;

CREATE OR REPLACE FUNCTION analysis.recompute_batch_progress(target_tenant uuid, target_batch uuid)
RETURNS void
LANGUAGE plpgsql
AS $$
DECLARE
    completed_count integer;
    failed_count integer;
    active_count integer;
BEGIN
    IF target_batch IS NULL THEN RETURN; END IF;

    SELECT count(*) FILTER (WHERE i.processing_status IN ('COMPLETED', 'DUPLICATE')),
           count(*) FILTER (WHERE i.processing_status IN ('LOW_QUALITY', 'REJECTED')
               OR (i.processing_status = 'FAILED' AND NOT COALESCE(j.retryable AND j.status IN ('FAILED', 'RETRY_READY'), false))),
           count(*) FILTER (WHERE i.processing_status IN ('PENDING', 'QUEUED', 'PROCESSING', 'RETRY_READY')
               OR (i.processing_status = 'FAILED' AND COALESCE(j.retryable AND j.status IN ('FAILED', 'RETRY_READY'), false)))
      INTO completed_count, failed_count, active_count
      FROM analysis.images i
      LEFT JOIN LATERAL (
          SELECT x.status, x.retryable
            FROM analysis.jobs x
           WHERE x.tenant_id = i.tenant_id AND x.image_id = i.id
           ORDER BY x.attempt_no DESC
           LIMIT 1
      ) j ON true
     WHERE i.tenant_id = target_tenant AND i.batch_id = target_batch;

    UPDATE analysis.batches
       SET completed_images = completed_count,
           failed_images = failed_count,
           status = CASE
               WHEN active_count > 0 THEN 'IN_PROGRESS'
               WHEN failed_count = 0 THEN 'COMPLETED'
               WHEN completed_count = 0 THEN 'FAILED'
               ELSE 'PARTIAL_COMPLETED'
           END,
           updated_at = clock_timestamp()
     WHERE tenant_id = target_tenant AND id = target_batch;
    RETURN;
END;
$$;

CREATE OR REPLACE FUNCTION analysis.refresh_batch_progress_from_image()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    PERFORM analysis.recompute_batch_progress(NEW.tenant_id, NEW.batch_id);
    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION analysis.refresh_batch_progress_from_job()
RETURNS trigger
LANGUAGE plpgsql
AS $$
DECLARE
    target_batch uuid;
BEGIN
    SELECT batch_id INTO target_batch FROM analysis.images
     WHERE tenant_id = NEW.tenant_id AND id = NEW.image_id;
    PERFORM analysis.recompute_batch_progress(NEW.tenant_id, target_batch);
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_images_refresh_batch_progress ON analysis.images;
CREATE TRIGGER trg_images_refresh_batch_progress
AFTER UPDATE OF processing_status ON analysis.images
FOR EACH ROW
WHEN (OLD.processing_status IS DISTINCT FROM NEW.processing_status)
EXECUTE FUNCTION analysis.refresh_batch_progress_from_image();

DROP TRIGGER IF EXISTS trg_jobs_refresh_batch_progress ON analysis.jobs;
CREATE TRIGGER trg_jobs_refresh_batch_progress
AFTER UPDATE OF status, retryable, attempt_no ON analysis.jobs
FOR EACH ROW
EXECUTE FUNCTION analysis.refresh_batch_progress_from_job();

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'aura_analysis_app') THEN
        GRANT SELECT, UPDATE ON analysis.batches TO aura_analysis_app;
    END IF;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'aura_analysis_migrator') THEN
        GRANT ALL PRIVILEGES ON analysis.batches TO aura_analysis_migrator;
    END IF;
END
$$;

COMMIT;
