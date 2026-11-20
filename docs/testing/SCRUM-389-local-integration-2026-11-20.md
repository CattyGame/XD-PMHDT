# SCRUM-389 local integration evidence — 2026-11-20

Branch: `fix/SCRUM-389-doctor-workspace`. Integration base: `develop@a0e59971ab9471780ed0aa71b1e35bfb3610eefe` (merge `64e1086`).

## Environment and data

Vite on 127.0.0.1:5173, gateway on port 5000, Analysis on port 5001, PostgreSQL database `aura_dev`, private Azurite containers. Firebase development authentication, local DOCTOR membership and patient assignment. No credentials are recorded here.

Dedicated synthetic clinic and unlinked synthetic patient were created locally. Analysis `38900000-2026-4000-8000-000000000101` uses generated 256×256 engineering patterns, not a patient image. The result remains `isMock=true`, model `synthetic-fixture-v1`, config `scrum389-local-test`, null risk/metrics and no research block. It does not establish AI model or clinical acceptance.

The local Analysis/Gateway containers were rebuilt from the branch source. Existing Analysis feedback migration and Profile clinic-patient/assignment migration were applied to the local database. Service authentication and private storage remained enabled.

## Executed live checks

- Authenticated doctor worklist: HTTP 200 with the assigned synthetic case.
- Detail: result version 1, review version 0 and explicit mock label visible. Original and overlay rendered from protected bytes as Blob URLs; both decoded to 256×256 pixels.
- QUALITY feedback before first review: success UI, verified one persisted row bound to the exact analysis/result/model and review version 0.
- Doctor approval: success UI, review version advanced 0→1. SQL confirmed one append-only review with null corrected risk; AI risk and metrics remained empty and `is_mock` remained true.
- Stale review and stale feedback using review version 0 after approval: both HTTP 409; neither created an extra feedback/review row.
- Revoked the dedicated synthetic assignment, then submitted feedback from the open detail. Access was denied, the detail and all image elements were removed, and feedback row count remained one. Restored the assignment after the negative test.

## Automated verification

- `npm test -- --maxWorkers=2`: 55 files, 540 tests passed, zero skipped (201.05 seconds). Includes feedback on labelled synthetic results and preservation of the visible mock label.
- `npm run build`: passed.
- `npm run lint`: passed with existing repository warnings, including the viewer constant export warning.
- `npx playwright test --config playwright.m5-mock.config.ts`: 8 passed, including mobile research crop/provenance, desktop review, offline/409 and revoke cleanup. These are explicitly mocked tests.

## Remaining acceptance limits

SCRUM-385 and SCRUM-387 are both In Review in Jira. M2 feedback code is merged into develop; M4 research V2 was not merged at the time of this check. Real M4 research/provenance/geometry integration remains pending. The local synthetic case covers the legacy/null-result path and real authorization, persistence and version conflicts; it is not a substitute for that integration.

The browser screenshot service was unavailable in this environment; live checks used the accessibility tree and rendered image decoding. Mock Playwright tests generated responsive screenshots separately. Do not mark required live research integration as passed or transition the issue to Done based on this evidence.