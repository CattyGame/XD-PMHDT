BEGIN;
CREATE TABLE IF NOT EXISTS identity.doctor_capability_audit (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(), actor_account_id uuid NOT NULL,
 target_account_id uuid NOT NULL, approved boolean NOT NULL,
 reason text NOT NULL CHECK (length(reason) BETWEEN 1 AND 1000), created_at timestamptz NOT NULL DEFAULT now()
);
GRANT SELECT,INSERT ON identity.doctor_capability_audit TO aura_identity_app;
COMMIT;
