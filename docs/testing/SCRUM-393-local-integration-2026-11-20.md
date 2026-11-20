# SCRUM-393 integration verification — 2026-11-20

Branch `fix/SCRUM-393-admin-portal`; develop integration base `a0e5997`, merge `369a053`.

## Changes after pulling team code

Portal lifecycle callers now reuse `api/adminManagement.ts` from develop. Clinic status changes include the displayed `expectedVersion` and require a newer matching receipt. The existing `/admin` management page remains available; `/admin/:section` serves the portal without an ambiguous optional root route.

Corrected the shared audit reader, both consumers and fixtures to the actual Profile `AuditEventDto`: sourceService/resourceType/resourceIdHash/correlationId/occurredAt/outcome. Raw target/actor IDs and free-text reasons are not substituted for those fields. The portal identifies the current audit scope as clinic management.

Restored all three analytics gateway routes while retaining the newly merged consultation and lifecycle routes; removed duplicate lifecycle route definitions during merge resolution. Kept the current shared authorized-request signature including extra headers.

Analytics total uses the backend's UNIQUE_ANALYSIS denominator, which excludes duplicates. Duplicate uploads are displayed as an independent diagnostic count. Date controls use complete local calendar days and an exclusive upper boundary, including the default range.

## Live environment and execution

Vite 127.0.0.1:5173, gateway 5000, Firebase development project, local `aura_dev` DB. Created a dedicated development SuperAdmin as requested; no credential or token is committed. Updated local Identity/Profile/Gateway from current source, applied existing migrations 014/019 and missing prerequisite clinic columns/audit-table DDL from migration 010 with scoped grants.

- SuperAdmin normal UI login and role bootstrap succeeded.
- GET admin accounts and clinics: HTTP 200, actual DTOs parsed.
- Admin analytics: HTTP 200. Live dashboard rendered total=6, active=1, completed=3, failed=2, duplicate=1 independently; unknown measurements=6. No invented measurements/revenue.
- Clinic fixture remains ACTIVE. UI update used expectedVersion=1 and returned version=2. A second API update advanced to version=3; submission from the stale UI was rejected with 409 and old clinic data was cleared. Reload is available.
- Live audit rendered the two corresponding CLINIC_STATUS_UPDATE rows with ALLOW and hashed resource identifiers.
- Doctor account accessing admin accounts: HTTP 403. Doctor activity summary after gateway route update: HTTP 200 with total=1.
- Local catalog, notification-template and AI-config endpoints return 404. Their UI displays an unavailable/error state; these are not passing integration checks.

Live UI screenshot was saved outside the repository in the task workspace. No account credentials or local environment files are committed.

## Verification

Final full Vitest run: 56 files / 543 tests passed, zero skipped (113.47 seconds). Final production build passed. Targeted portal/shared-admin regression suite: 3 files / 15 tests passed after the DTO and denominator corrections. Playwright mock suite: 8 passed, including mobile catalog version-conflict recovery. Lint/build passed with existing repository warnings; Gateway build passed.

## Remaining dependencies

Jira at verification: SCRUM-384 Done; SCRUM-383 In Progress; SCRUM-390 and SCRUM-392 In Review. M6 lifecycle implementation is present in develop. M1 catalog/history/usage/templates and M4 config require their backend integration before live acceptance can be completed. Keep SCRUM-393 In Progress; mock fixtures do not establish backend acceptance.