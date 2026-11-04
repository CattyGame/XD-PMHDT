BEGIN;

DO $$
BEGIN
    IF current_database() <> 'aura_dev' THEN
        RAISE EXCEPTION 'Run this migration only on aura_dev';
    END IF;
END
$$;

-- SCRUM-365: Clinic Campaign Packages (5,000 credits) & Entitlements
SET LOCAL ROLE aura_billing_migrator;

-- 1. Insert 5000-credit Clinic Campaign Package in legacy billing_poc catalog
INSERT INTO billing_poc.service_packages (id, name, price, credits, updated_at)
VALUES (5, 'Gói Chiến dịch 5.000 ảnh', 65000000.00, 5000, now())
ON CONFLICT (id) DO UPDATE
SET name = EXCLUDED.name, price = EXCLUDED.price, credits = EXCLUDED.credits, updated_at = now();

-- 2. Insert ACTIVE package price version for orders snapshot validation
INSERT INTO billing.package_price_versions
    (package_id, version_number, display_name, price_amount, currency_code, credits, status, effective_from)
VALUES
    (5, 2, 'Gói Chiến dịch 5.000 ảnh', 65000000.00, 'VND', 5000, 'ACTIVE', now())
ON CONFLICT (package_id, version_number) DO NOTHING;

-- Authorization is resolved through Profile API; Billing receives no Profile schema access.
COMMIT;
