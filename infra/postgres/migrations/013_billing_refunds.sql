BEGIN;
-- Preserve history. Existing duplicates require investigation, never automatic deletion.
DO $$ BEGIN
 IF EXISTS(SELECT 1 FROM billing.credit_ledger_entries
   WHERE reservation_id IS NOT NULL AND entry_type='ADJUSTMENT' AND metadata->>'operation' IN ('REFUND','BATCH_REFUND')
   GROUP BY reservation_id HAVING count(*)>1) THEN
  RAISE EXCEPTION 'Duplicate historical refunds require audited reconciliation before migration013';
 END IF;
END $$;
CREATE UNIQUE INDEX IF NOT EXISTS uq_billing_full_refund_per_capture
 ON billing.credit_ledger_entries(reservation_id)
 WHERE reservation_id IS NOT NULL AND entry_type='ADJUSTMENT' AND metadata->>'operation' IN ('REFUND','BATCH_REFUND');
ALTER TABLE billing.credit_reservations ADD COLUMN IF NOT EXISTS reconciliation_attempts integer NOT NULL DEFAULT 0;
ALTER TABLE billing.credit_reservations ADD COLUMN IF NOT EXISTS reconciliation_next_at timestamptz;
ALTER TABLE billing.credit_reservations ADD COLUMN IF NOT EXISTS reconciliation_last_reason text;
GRANT UPDATE(reconciliation_attempts,reconciliation_next_at,reconciliation_last_reason) ON billing.credit_reservations TO aura_billing_app;
REVOKE ALL ON SCHEMA profile FROM aura_billing_app;
REVOKE ALL ON ALL TABLES IN SCHEMA profile FROM aura_billing_app;
REVOKE ALL ON SCHEMA profile FROM aura_billing_migrator;
REVOKE ALL ON ALL TABLES IN SCHEMA profile FROM aura_billing_migrator;
DO $$ BEGIN
 IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conrelid='billing.credit_ledger_entries'::regclass AND conname='ck_refund_authority') THEN
 ALTER TABLE billing.credit_ledger_entries ADD CONSTRAINT ck_refund_authority CHECK (
  entry_type<>'ADJUSTMENT' OR COALESCE(metadata->>'operation' NOT IN ('REFUND','BATCH_REFUND'),true)
  OR COALESCE((metadata->>'refund_authority'='SYSTEM_ADMIN' AND NULLIF(metadata->>'approved_by','') IS NOT NULL)
   OR (metadata->>'refund_authority'='ANALYSIS_TERMINAL' AND NULLIF(metadata->>'terminal_evidence','') IS NOT NULL),false)
 ) NOT VALID;
 END IF;
END $$;
COMMIT;
