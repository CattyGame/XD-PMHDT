-- SCRUM-368: persist ICDR only when a doctor approves the clinical review.
BEGIN;

ALTER TABLE analysis.doctor_reviews
    ADD COLUMN IF NOT EXISTS icdr_level varchar(32) NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'ck_doctor_reviews_icdr_level'
          AND conrelid = 'analysis.doctor_reviews'::regclass
    ) THEN
        ALTER TABLE analysis.doctor_reviews
            ADD CONSTRAINT ck_doctor_reviews_icdr_level
            CHECK ((icdr_level IS NULL OR icdr_level IN
                ('NO_DR', 'MILD_NPDR', 'MODERATE_NPDR', 'SEVERE_NPDR', 'PROLIFERATIVE_DR'))
                AND (is_approved OR icdr_level IS NULL));
    END IF;
END
$$;

COMMIT;
