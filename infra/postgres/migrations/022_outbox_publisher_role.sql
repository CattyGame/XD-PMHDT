-- Fresh installs previously skipped the conditional publisher grants because this role did not exist.
BEGIN;
DO $$ BEGIN
    IF current_database() <> 'aura_dev' THEN RAISE EXCEPTION 'Run only on aura_dev'; END IF;
    IF NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname='aura_analysis_publisher') THEN
        CREATE ROLE aura_analysis_publisher NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
    END IF;
END $$;
GRANT CONNECT ON DATABASE aura_dev TO aura_analysis_publisher;
GRANT USAGE ON SCHEMA analysis TO aura_analysis_publisher;
REVOKE ALL ON analysis.outbox_events FROM aura_analysis_publisher;
GRANT SELECT ON analysis.outbox_events,analysis.v_outbox_status TO aura_analysis_publisher;
GRANT UPDATE(published_at,delivery_attempts,next_publish_at,last_error_code,locked_until,
             needs_attention_at,last_error_at,claim_generation)
    ON analysis.outbox_events TO aura_analysis_publisher;
COMMIT;
