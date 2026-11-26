-- SCRUM-418: forward-only repair for installations where 015 was already applied.
-- Keep append-only audit records intact; remove only runtime mutation privileges.
BEGIN;

SET LOCAL ROLE aura_identity_migrator;
REVOKE UPDATE, DELETE
    ON identity.doctor_capability_audit, identity.account_lifecycle_audit
    FROM aura_identity_app;

RESET ROLE;
SET LOCAL ROLE aura_profile_migrator;
REVOKE UPDATE, DELETE
    ON profile.clinic_lifecycle_audit_events, profile.clinic_audit_logs
    FROM aura_profile_app;

COMMIT;
