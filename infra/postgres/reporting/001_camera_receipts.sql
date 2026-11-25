-- Reporting owns receipt metadata only. Never apply this to a service business database.
BEGIN;
DO $$ BEGIN
    IF current_database() <> 'aura_reporting' THEN RAISE EXCEPTION 'Run only on aura_reporting'; END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='aura_reporting_owner') THEN
        CREATE ROLE aura_reporting_owner NOLOGIN;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='aura_reporting_writer') THEN
        CREATE ROLE aura_reporting_writer NOLOGIN;
    END IF;
END $$;
CREATE SCHEMA IF NOT EXISTS reporting AUTHORIZATION aura_reporting_owner;
REVOKE ALL ON SCHEMA reporting FROM PUBLIC;
CREATE TABLE IF NOT EXISTS reporting.camera_ingestion_receipts (
    request_id uuid PRIMARY KEY,
    device_id text NOT NULL,
    image_sha256 text NOT NULL CHECK(image_sha256 ~ '^[a-f0-9]{64}$'),
    batch_id uuid NOT NULL,
    image_count integer NOT NULL CHECK(image_count=1),
    source_accepted_at timestamptz NOT NULL,
    correlation_id text NOT NULL,
    receipt_version integer NOT NULL DEFAULT 1 CHECK(receipt_version=1),
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
ALTER TABLE reporting.camera_ingestion_receipts ADD COLUMN IF NOT EXISTS receipt_version integer NOT NULL DEFAULT 1 CHECK(receipt_version=1);
ALTER TABLE reporting.camera_ingestion_receipts OWNER TO aura_reporting_owner;
CREATE OR REPLACE PROCEDURE reporting.record_camera_receipt(
    request uuid, device text, image_hash text, batch uuid, images integer, accepted timestamptz, correlation text)
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
    IF request IS NULL OR request='00000000-0000-0000-0000-000000000000'::uuid
       OR batch IS NULL OR batch='00000000-0000-0000-0000-000000000000'::uuid
       OR device IS NULL OR device !~ '^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$'
       OR image_hash IS NULL OR image_hash !~ '^[a-f0-9]{64}$'
       OR images IS DISTINCT FROM 1 OR accepted IS NULL
       OR correlation IS NULL OR correlation !~ '^[A-Za-z0-9._-]{1,64}$' THEN
        RAISE EXCEPTION 'Invalid receipt metadata' USING ERRCODE='22023';
    END IF;
    INSERT INTO reporting.camera_ingestion_receipts(request_id,device_id,image_sha256,batch_id,image_count,source_accepted_at,correlation_id)
    VALUES(request,device,image_hash,batch,images,accepted,correlation) ON CONFLICT(request_id) DO NOTHING;
    IF NOT EXISTS(SELECT 1 FROM reporting.camera_ingestion_receipts r WHERE r.request_id=request
        AND (r.device_id,r.image_sha256,r.batch_id,r.image_count,r.source_accepted_at,r.correlation_id,r.receipt_version)
            IS NOT DISTINCT FROM (device,image_hash,batch,images,accepted,correlation,1)) THEN
        RAISE EXCEPTION 'Receipt conflicts with committed event' USING ERRCODE='23505';
    END IF;
END $$;
ALTER PROCEDURE reporting.record_camera_receipt(uuid,text,text,uuid,integer,timestamptz,text) OWNER TO aura_reporting_owner;
REVOKE ALL ON TABLE reporting.camera_ingestion_receipts FROM PUBLIC,aura_reporting_writer;
REVOKE ALL ON PROCEDURE reporting.record_camera_receipt(uuid,text,text,uuid,integer,timestamptz,text) FROM PUBLIC;
GRANT USAGE ON SCHEMA reporting TO aura_reporting_writer;
GRANT EXECUTE ON PROCEDURE reporting.record_camera_receipt(uuid,text,text,uuid,integer,timestamptz,text) TO aura_reporting_writer;
COMMIT;
