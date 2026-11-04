BEGIN;
ALTER TABLE analysis.analysis_requests ADD COLUMN IF NOT EXISTS clinic_context_id uuid NULL;
ALTER TABLE analysis.batches ADD COLUMN IF NOT EXISTS admission_complete boolean NOT NULL DEFAULT true;
-- A durable, unique batch completion obligation; transaction boundaries never depend on Redis.
CREATE TABLE IF NOT EXISTS analysis.batch_completion_events (
    batch_id uuid PRIMARY KEY REFERENCES analysis.batches(id),
    event_id uuid NOT NULL UNIQUE,
    completed_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE OR REPLACE FUNCTION analysis.ensure_batch_completion(target_tenant uuid,target_batch uuid)
RETURNS boolean LANGUAGE plpgsql AS $$
DECLARE
    admitted boolean;
    total_count integer;
    complete_count integer;
    failed_count integer;
    request_id uuid;
    image_id_value uuid;
    patient_id_value uuid;
    correlation text;
    public_status text;
    inserted uuid;
BEGIN
    SELECT admission_complete INTO admitted FROM analysis.batches
      WHERE tenant_id=target_tenant AND id=target_batch FOR UPDATE;
    IF NOT FOUND OR NOT admitted THEN RETURN false; END IF;
    PERFORM analysis.recompute_batch_progress(target_tenant,target_batch);
    SELECT count(*)::integer,
      count(*) FILTER(WHERE processing_status IN ('COMPLETED','DUPLICATE'))::integer,
      count(*) FILTER(WHERE processing_status IN ('FAILED','LOW_QUALITY','REJECTED'))::integer
      INTO total_count,complete_count,failed_count
      FROM analysis.images WHERE tenant_id=target_tenant AND batch_id=target_batch;
    IF total_count=0 OR total_count<>complete_count+failed_count THEN RETURN false; END IF;
    SELECT i.analysis_request_id,i.id,r.patient_id,r.correlation_id
      INTO request_id,image_id_value,patient_id_value,correlation
      FROM analysis.images i JOIN analysis.analysis_requests r ON r.tenant_id=i.tenant_id AND r.id=i.analysis_request_id
      WHERE i.tenant_id=target_tenant AND i.batch_id=target_batch ORDER BY i.id LIMIT 1;
    public_status:=CASE WHEN failed_count=0 THEN 'COMPLETED' WHEN complete_count=0 THEN 'FAILED' ELSE 'PARTIAL_FAILED' END;
    INSERT INTO analysis.batch_completion_events(batch_id,event_id) VALUES(target_batch,target_batch)
      ON CONFLICT(batch_id) DO NOTHING RETURNING event_id INTO inserted;
    IF inserted IS NULL THEN RETURN false; END IF;
    INSERT INTO analysis.outbox_events(id,tenant_id,analysis_request_id,image_id,event_type,schema_version,payload,occurred_at)
    VALUES(target_batch,target_tenant,request_id,image_id_value,'batch.completed','0.7',
      jsonb_build_object('eventId',target_batch,'eventType','batch.completed','schemaVersion','0.7',
        'occurredAt',to_char(clock_timestamp() AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS"Z"'),
        'tenantId',target_tenant,'correlationId',correlation,'analysisId',target_batch,
        'data',jsonb_build_object('batchId',target_batch,'patientId',patient_id_value,'batchStatus',public_status,
          'totalImages',total_count,'completedImages',complete_count,'failedImages',failed_count)),clock_timestamp());
    RETURN true;
END $$;
DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname='aura_analysis_app') THEN
        GRANT SELECT, INSERT ON analysis.batch_completion_events TO aura_analysis_app;
        GRANT EXECUTE ON FUNCTION analysis.ensure_batch_completion(uuid,uuid) TO aura_analysis_app;
        -- Job/image triggers execute this invoker function under the runtime role.
        GRANT EXECUTE ON FUNCTION analysis.recompute_batch_progress(uuid,uuid) TO aura_analysis_app;
    END IF;
END $$;
COMMIT;
