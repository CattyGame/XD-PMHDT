BEGIN;
ALTER TABLE billing.settlement_inbox DROP CONSTRAINT IF EXISTS ck_settlement_inbox_schema;
ALTER TABLE billing.settlement_inbox ADD CONSTRAINT ck_settlement_inbox_schema CHECK(schema_version IN ('0.7','0.8'));
COMMIT;
