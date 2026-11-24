-- Disposable load-test metadata only: no image files, AI results or clinician decisions are fabricated.
BEGIN;
DO $$ BEGIN
    IF current_database() !~ '^aura_m1_capacity_[a-f0-9]{8}$' THEN
        RAISE EXCEPTION 'Reader fixture requires an explicitly named disposable capacity clone';
    END IF;
END $$;

CREATE TEMP TABLE capacity_users ON COMMIT DROP AS
SELECT n, md5('aura-capacity-account-'||n)::uuid AS account_id,
       md5('aura-capacity-tenant-'||n)::uuid AS tenant_id,
       md5('aura-capacity-patient-'||n)::uuid AS patient_id,
       md5('aura-capacity-request-'||n)::uuid AS request_id
FROM generate_series(1,2000) AS n;

INSERT INTO identity.accounts(account_id,firebase_uid,account_status,profile_provisioning_status)
SELECT account_id,'synthetic-capacity-metadata-'||n,'ACTIVE','READY' FROM capacity_users
ON CONFLICT DO NOTHING;
INSERT INTO identity.account_roles(account_id,role_name)
SELECT account_id,'PATIENT' FROM capacity_users ON CONFLICT DO NOTHING;
INSERT INTO profile.tenants(tenant_id,tenant_kind,owner_principal_id)
SELECT tenant_id,'PERSONAL',account_id FROM capacity_users ON CONFLICT DO NOTHING;
INSERT INTO profile.profiles(account_id,display_name,personal_tenant_id)
SELECT account_id,'SYNTHETIC CAPACITY METADATA '||n,tenant_id FROM capacity_users ON CONFLICT DO NOTHING;
INSERT INTO profile.patients(patient_id,tenant_id,linked_account_id,owner_principal_id,status)
SELECT patient_id,tenant_id,account_id,account_id,'ACTIVE' FROM capacity_users ON CONFLICT DO NOTHING;

INSERT INTO analysis.analysis_requests(id,tenant_id,owner_id,requester_id,patient_id,idempotency_key,
    request_fingerprint,request_schema_version,accepted_response,correlation_id)
SELECT request_id,tenant_id,account_id,account_id,patient_id,md5('aura-capacity-idempotency-'||n)::uuid,
    repeat('0',64),'capacity-metadata-v1','{}'::jsonb,'synthetic-capacity-metadata-only'
FROM capacity_users ON CONFLICT DO NOTHING;

-- REJECTED rows never enter a worker or acquire scientific measurements; their assets do not exist.
INSERT INTO analysis.images(id,tenant_id,analysis_request_id,file_id,modality,eye_side,
    object_key,mime_type,size_bytes,width,height,sha256,processing_status,review_status,batch_id)
SELECT md5('aura-capacity-image-'||g)::uuid,u.tenant_id,u.request_id,
    md5('aura-capacity-file-'||g)::uuid,'FUNDUS','unknown',
    'synthetic-capacity-metadata-only/'||g,'image/png',1,1,1,repeat('0',64),'REJECTED','NOT_REQUIRED',NULL
FROM generate_series(1,10000) AS g JOIN capacity_users u ON u.n=1+(g-1)%2000
ON CONFLICT DO NOTHING;

DO $$ BEGIN
    IF (SELECT count(*) FROM identity.accounts WHERE firebase_uid LIKE 'synthetic-capacity-metadata-%')<>2000
       OR (SELECT count(*) FROM analysis.images WHERE object_key LIKE 'synthetic-capacity-metadata-only/%')<>10000
       OR EXISTS(SELECT 1 FROM analysis.images WHERE object_key LIKE 'synthetic-capacity-metadata-only/%'
                 AND (processing_status<>'REJECTED' OR batch_id IS NOT NULL)) THEN
        RAISE EXCEPTION 'Synthetic metadata population is incomplete or unexpectedly active';
    END IF;
END $$;
COMMIT;
