BEGIN;
CREATE TABLE IF NOT EXISTS profile.clinic_invitations (
    membership_id uuid PRIMARY KEY REFERENCES profile.memberships(membership_id),
    token_hash bytea NOT NULL CHECK (octet_length(token_hash)=32),
    issued_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL,
    consumed_at timestamptz,
    CHECK (expires_at > issued_at)
);
GRANT SELECT,INSERT,UPDATE ON profile.clinic_invitations TO aura_profile_app;
COMMIT;
