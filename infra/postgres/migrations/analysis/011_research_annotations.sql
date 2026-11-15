-- Optional A/V native overlays and 224px DR Grad-CAM crops; existing annotations stay unchanged.
BEGIN;
ALTER TABLE analysis.annotation_refs DROP CONSTRAINT IF EXISTS ck_annotation_type;
ALTER TABLE analysis.annotation_refs ADD CONSTRAINT ck_annotation_type
    CHECK (type IN ('MASK', 'OVERLAY', 'ANNOTATED', 'HEATMAP'));
COMMIT;
