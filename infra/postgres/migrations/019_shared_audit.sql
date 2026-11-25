BEGIN;
DO $$
DECLARE owner_schema text; app_role text; migrator text;
BEGIN
  IF current_database() <> 'aura_dev' THEN RAISE EXCEPTION 'Run only on aura_dev'; END IF;
  FOREACH owner_schema IN ARRAY ARRAY['identity','profile','analysis','billing','notification'] LOOP
    app_role := 'aura_' || owner_schema || '_app';
    migrator := 'aura_' || owner_schema || '_migrator';
    EXECUTE format('CREATE TABLE IF NOT EXISTS %I.audit_events (
      event_id uuid PRIMARY KEY, occurred_at timestamptz NOT NULL,
      payload jsonb NOT NULL CHECK(jsonb_typeof(payload)=''object''),
      payload_sha256 varchar(64) NOT NULL CHECK(payload_sha256 ~ ''^[0-9a-f]{64}$''),
      recorded_at timestamptz NOT NULL DEFAULT clock_timestamp())', owner_schema);
    EXECUTE format('CREATE TABLE IF NOT EXISTS %I.audit_outbox (
      event_id uuid PRIMARY KEY REFERENCES %I.audit_events(event_id) ON DELETE CASCADE,
      attempts integer NOT NULL DEFAULT 0 CHECK(attempts>=0),
      next_attempt_at timestamptz NOT NULL DEFAULT now(),
      lease_id uuid, lease_until timestamptz, delivered_at timestamptz)', owner_schema, owner_schema);
    EXECUTE format('CREATE INDEX IF NOT EXISTS audit_outbox_pending ON %I.audit_outbox(next_attempt_at,event_id) WHERE delivered_at IS NULL', owner_schema);
    EXECUTE format('CREATE INDEX IF NOT EXISTS audit_events_retention ON %I.audit_events(occurred_at)', owner_schema);
    EXECUTE format('ALTER TABLE %I.audit_events OWNER TO %I', owner_schema, migrator);
    EXECUTE format('ALTER TABLE %I.audit_outbox OWNER TO %I', owner_schema, migrator);
    EXECUTE format('REVOKE ALL ON %I.audit_events,%I.audit_outbox FROM %I', owner_schema, owner_schema, app_role);
    EXECUTE format('GRANT SELECT,INSERT ON %I.audit_events TO %I', owner_schema, app_role);
    EXECUTE format('GRANT SELECT,INSERT(event_id),UPDATE(attempts,next_attempt_at,lease_id,lease_until,delivered_at) ON %I.audit_outbox TO %I', owner_schema, app_role);
    -- Runtime cannot delete arbitrary audit records; the definer enforces the course's 180-day policy.
    EXECUTE format('CREATE OR REPLACE FUNCTION %I.purge_expired_audit() RETURNS integer
      LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $fn$
      DECLARE removed integer;
      BEGIN
        WITH expired AS (SELECT e.event_id FROM %I.audit_events e JOIN %I.audit_outbox o USING(event_id)
          WHERE e.occurred_at<clock_timestamp()-interval ''180 days'' AND o.delivered_at IS NOT NULL
          ORDER BY e.occurred_at LIMIT 1000), deleted AS (
          DELETE FROM %I.audit_events e USING expired x WHERE e.event_id=x.event_id RETURNING e.event_id)
        SELECT count(*) INTO removed FROM deleted;
        RETURN removed;
      END $fn$', owner_schema, owner_schema, owner_schema, owner_schema);
    EXECUTE format('ALTER FUNCTION %I.purge_expired_audit() OWNER TO %I', owner_schema, migrator);
    EXECUTE format('REVOKE ALL ON FUNCTION %I.purge_expired_audit() FROM PUBLIC', owner_schema);
    EXECUTE format('GRANT EXECUTE ON FUNCTION %I.purge_expired_audit() TO %I', owner_schema, app_role);
  END LOOP;
END $$;

CREATE TABLE IF NOT EXISTS profile.audit_inbox (
  source varchar(16) NOT NULL CHECK(source IN ('Identity','Profile','Analysis','Billing','Notification')),
  event_id uuid NOT NULL, payload_sha256 varchar(64) NOT NULL CHECK(payload_sha256 ~ '^[0-9a-f]{64}$'),
  received_at timestamptz NOT NULL DEFAULT clock_timestamp(), PRIMARY KEY(source,event_id)
);
CREATE TABLE IF NOT EXISTS profile.audit_projection (
  source varchar(16) NOT NULL, event_id uuid NOT NULL, occurred_at timestamptz NOT NULL,
  payload jsonb NOT NULL, PRIMARY KEY(source,event_id),
  FOREIGN KEY(source,event_id) REFERENCES profile.audit_inbox(source,event_id)
);
CREATE INDEX IF NOT EXISTS audit_projection_page ON profile.audit_projection(occurred_at DESC,event_id DESC,source DESC);
ALTER TABLE profile.audit_inbox OWNER TO aura_profile_migrator;
ALTER TABLE profile.audit_projection OWNER TO aura_profile_migrator;
REVOKE ALL ON profile.audit_inbox,profile.audit_projection FROM aura_profile_app;
GRANT SELECT,INSERT ON profile.audit_inbox,profile.audit_projection TO aura_profile_app;

CREATE OR REPLACE FUNCTION profile.purge_expired_audit_projection() RETURNS integer
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE removed integer;
BEGIN
  WITH expired AS (SELECT source,event_id FROM profile.audit_projection
    WHERE occurred_at<clock_timestamp()-interval '180 days' ORDER BY occurred_at LIMIT 1000), deleted AS (
    DELETE FROM profile.audit_projection p USING expired x WHERE p.source=x.source AND p.event_id=x.event_id RETURNING p.event_id)
  SELECT count(*) INTO removed FROM deleted;
  RETURN removed;
END $$;
ALTER FUNCTION profile.purge_expired_audit_projection() OWNER TO aura_profile_migrator;
REVOKE ALL ON FUNCTION profile.purge_expired_audit_projection() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION profile.purge_expired_audit_projection() TO aura_profile_app;
COMMENT ON TABLE profile.audit_inbox IS 'Opaque source/event/hash tombstones survive projection retention to prevent replay resurrection; no clinical payload.';
COMMENT ON TABLE profile.audit_projection IS 'Metadata only; 180-day course policy, not a legal compliance claim.';
COMMIT;
