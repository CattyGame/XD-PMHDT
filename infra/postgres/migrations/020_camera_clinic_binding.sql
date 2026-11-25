BEGIN;
ALTER TABLE profile.camera_grants ADD COLUMN IF NOT EXISTS actor_account_id uuid;
-- Legacy personal grants identify an actor only when the patient has an unambiguous owner.
UPDATE profile.camera_grants g SET actor_account_id=p.linked_account_id
FROM profile.patients p JOIN profile.tenants t ON t.tenant_id=p.tenant_id
WHERE g.patient_id=p.patient_id AND g.tenant_id=p.tenant_id AND t.tenant_kind='PERSONAL'
  AND g.actor_account_id IS NULL AND p.linked_account_id=p.owner_principal_id;
REVOKE SELECT ON identity.accounts FROM aura_profile_app;
REVOKE USAGE ON SCHEMA identity FROM aura_profile_app;
COMMIT;
