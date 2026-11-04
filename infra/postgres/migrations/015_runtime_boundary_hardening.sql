-- Reapply after the historical blanket runtime-grants migration. No historical rows are changed.
BEGIN;
CREATE OR REPLACE FUNCTION analysis.recompute_batch_progress(target_tenant uuid,target_batch uuid)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE completed_count integer; failed_count integer; active_count integer; admitted boolean;
BEGIN
    IF target_batch IS NULL THEN RETURN; END IF;
    SELECT admission_complete INTO admitted FROM analysis.batches WHERE tenant_id=target_tenant AND id=target_batch FOR UPDATE;
    IF NOT FOUND THEN RETURN; END IF;
    SELECT count(*) FILTER(WHERE i.processing_status IN ('COMPLETED','DUPLICATE')),
      count(*) FILTER(WHERE i.processing_status IN ('LOW_QUALITY','REJECTED') OR
        (i.processing_status='FAILED' AND NOT coalesce(j.retryable AND j.status IN ('FAILED','RETRY_READY'),false))),
      count(*) FILTER(WHERE i.processing_status IN ('PENDING','QUEUED','PROCESSING','RETRY_READY') OR
        (i.processing_status='FAILED' AND coalesce(j.retryable AND j.status IN ('FAILED','RETRY_READY'),false)))
      INTO completed_count,failed_count,active_count
      FROM analysis.images i LEFT JOIN LATERAL(SELECT status,retryable FROM analysis.jobs x
        WHERE x.tenant_id=i.tenant_id AND x.image_id=i.id ORDER BY attempt_no DESC LIMIT 1) j ON true
      WHERE i.tenant_id=target_tenant AND i.batch_id=target_batch;
    UPDATE analysis.batches SET total_images=completed_count+failed_count+active_count,
      completed_images=completed_count,failed_images=failed_count,
      status=CASE WHEN NOT admitted OR active_count>0 THEN 'IN_PROGRESS'
        WHEN completed_count+failed_count=0 THEN 'FAILED'
        WHEN failed_count=0 THEN 'COMPLETED' WHEN completed_count=0 THEN 'FAILED' ELSE 'PARTIAL_COMPLETED' END,
      updated_at=clock_timestamp() WHERE tenant_id=target_tenant AND id=target_batch;
END $$;
REVOKE UPDATE,DELETE ON analysis.report_snapshots,analysis.batch_completion_events FROM aura_analysis_app;
REVOKE UPDATE,DELETE ON identity.doctor_capability_audit FROM aura_identity_app;
REVOKE DELETE ON profile.clinic_invitations FROM aura_profile_app;
REVOKE ALL ON FUNCTION analysis.ensure_batch_completion(uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION analysis.ensure_batch_completion(uuid,uuid),analysis.recompute_batch_progress(uuid,uuid) TO aura_analysis_app;
GRANT UPDATE(reconciliation_attempts,reconciliation_next_at,reconciliation_last_reason) ON billing.credit_reservations TO aura_billing_app;
COMMIT;
