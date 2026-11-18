# W6-M6-01 Verification Report (Admin Identity/Profile Lifecycle & Consultation Chat Authority)

- **Jira Task**: `[W6-M6-01]` (`SCRUM-395`) — Admin Identity/Profile lifecycle và authority cho Chat
- **Role**: M6 (Security, Auth, Identity & Profile)
- **Branch**: `feature/W6-M6-01-admin-lifecycle-and-chat-authority`
- **Verification Date**: `2026-11-18`

---

## 1. Scope Implemented

### A. PostgreSQL Migrations & Runtime Boundary Hardening
- `infra/postgres/migrations/019_admin_lifecycle_and_chat_authority.sql`:
  - `identity.accounts`: added `display_name VARCHAR(100) NOT NULL DEFAULT ''` and `version INT NOT NULL DEFAULT 1 CHECK (version >= 1)`.
  - `identity.account_lifecycle_audit`: append-only table (`GRANT SELECT, INSERT`, `REVOKE UPDATE, DELETE` from `aura_identity_app`) capturing actor, target, action, status/role transitions, `version`, mandatory `reason`, `correlation_id`, and `created_at`.
  - `profile.clinics`: added `version INT NOT NULL DEFAULT 1 CHECK (version >= 1)`.
  - `profile.audit_events`: append-only table (`GRANT SELECT, INSERT`, `REVOKE UPDATE, DELETE` from `aura_profile_app`) storing non-PHI audit entries (`source_service`, `actor_account_id`, `action`, `resource_type`, SHA-256 `resource_id_hash`, `correlation_id`, `outcome`, `occurred_at`).
- `infra/postgres/migrations/015_runtime_boundary_hardening.sql`:
  - Explicitly `REVOKE UPDATE, DELETE` on `identity.account_lifecycle_audit` from `aura_identity_app`, and on `profile.audit_events` and `profile.clinic_audit_logs` from `aura_profile_app`.
- `scripts/dev/Initialize-AuraDevDatabase.ps1`:
  - Added `infra/postgres/migrations/019_admin_lifecycle_and_chat_authority.sql` into the canonical ordered migration plan immediately after `014_identity_doctor_capability_audit.sql` (37 total migrations).

### B. Identity Service (`AURA.Identity.Api`)
- `src/services/AURA.Identity.Api/Models/IdentityModels.cs`:
  - Added `AllowedAccountStatuses` (`ACTIVE`, `DISABLED`), `AllowedGlobalRoles` (`PATIENT`, `DOCTOR`, `CLINIC_ADMIN`, `STAFF`, `SYSTEM_ADMIN`), `AdminAccountDto`, `AdminAccountPageResponse`, `UpdateAccountStatusRequest`, `UpdateAccountRolesRequest`.
- `src/services/AURA.Identity.Api/IdentityRepository.cs`:
  - Added `ListAccountsAdminAsync` (keyset cursor pagination, status filter, case-insensitive search on `display_name` / `firebase_uid` / UUID `account_id`).
  - Added `UpdateAccountStatusAdminAsync` and `UpdateAccountRolesAdminAsync` enforcing:
    - Mandatory `reason` (`1..1000` chars) and `expectedVersion >= 1` (`400 ERR_VALIDATION`),
    - Optimistic locking with row lock `FOR UPDATE` (`409 ERR_VERSION_CONFLICT`),
    - Self-disable block (`400 ERR_SELF_DISABLE_FORBIDDEN`) and self-demote block (`400 ERR_SELF_DEMOTE_FORBIDDEN`),
    - Sole active `SYSTEM_ADMIN` protection (`400 ERR_LAST_SYSTEM_ADMIN`),
    - Separation of `DOCTOR` capability from general `PATCH /roles` (`400 ERR_DOCTOR_CAPABILITY_SEPARATED` — must use `PUT /api/v1/admin/accounts/{accountId}/doctor-capability`),
    - Atomic version increment and append-only audit write to `identity.account_lifecycle_audit`.
  - Updated `SetDoctorCapabilityAsync` to bump `identity.accounts.version` and write `identity.account_lifecycle_audit`, and updated `CreateAccountAsync` / bootstrap to persist `display_name`.
- `src/services/AURA.Identity.Api/Program.cs`:
  - Added `AuthorizeSystemAdminAsync` guard (`401`/`403`) and mapped:
    - `GET /api/v1/admin/accounts`
    - `PATCH /api/v1/admin/accounts/{accountId:guid}/status`
    - `PATCH /api/v1/admin/accounts/{accountId:guid}/roles`
    - Updated `PUT /api/v1/admin/accounts/{accountId:guid}/doctor-capability` to forward `correlationId`.

### C. Profile Service (`AURA.Profile.Api`) & Consultation Chat Authority
- `src/services/AURA.Profile.Api/Models/ProfileModels.cs`:
  - Added `Version` to `ClinicDto`, optional `ExpectedVersion` to `UpdateClinicStatusRequest`, `AdminClinicListResponse`, `AuditEventDto`, `AuditEventPageResponse`, `ConsultationActions`, `ConsultationDoctorDto`, `ConsultationDoctorsResponse`, `ConsultationAccessResolveRequest`, `ConsultationAccessResolveResponse`.
- `src/services/AURA.Profile.Api/ProfileRepository.cs`:
  - Added `UpdateClinicStatusWithVersionAsync` (atomic version check `409 ERR_VERSION_CONFLICT`, `profile.clinic_audit_logs` + `profile.audit_events` writes via `HashResourceId` SHA-256), `ListClinicsAdminAsync`, `RecordAuditEventAsync`, `ListAuditEventsAdminAsync`.
- `src/services/AURA.Profile.Api/ProfileRepository.Security.cs`:
  - Extended `ResolvePatientAccessCoreAsync` to evaluate `CONSULTATION_CREATE`, `CONSULTATION_JOIN`, `CONSULTATION_READ`, `CONSULTATION_SEND` fail-closed (checking `!isPatientParticipant && role is null` before `WRONG_CLINIC` anti-probing check, requiring `clinic.status == ACTIVE`, active `DOCTOR` membership + global `DOCTOR` capability + active `doctor_assignments`, or linked active `PATIENT`).
  - Added `ResolveConsultationAccessAsync` verifying live `IAccountAuthority`, live `IAnalysisBindingAuthority`, active clinic status, active doctor assignments, active clinic `DOCTOR` memberships, and global `DOCTOR` capability; returns `Revoked = true, Code = "ACCESS_REVOKED"` on revocation/suspension/disabled state and populates `EligibleDoctors`.
- `src/services/AURA.Profile.Api/Program.cs`:
  - Mapped `POST /internal/v1/profile/consultation-access/resolve` protected by Service JWT (`audience: aura-profile`, `scope: profile:resolve`) with fail-closed `503 ERR_DEPENDENCY_UNAVAILABLE`.
  - Mapped `GET /api/v1/admin/clinics` and `GET /api/v1/admin/audit-events` (`SYSTEM_ADMIN` only, cursor-paginated, non-PHI).
  - Updated `PATCH /api/v1/admin/clinics/{clinicId:guid}/status` to require `Reason` (`1..1000`) and enforce optional `ExpectedVersion` (`409 ERR_VERSION_CONFLICT`).
- `src/services/AURA.Profile.Api/ProfilePublicMembershipEndpoints.cs`:
  - Mapped `GET /api/v1/analyses/{analysisId:guid}/consultation-doctors?clinicContextId={clinicId}` and `GET /api/v1/clinics/{clinicId:guid}/analyses/{analysisId:guid}/consultation-doctors` with anti-probing (`403 ERR_FORBIDDEN` for non-members/revoked actors, `404 ERR_NOT_FOUND` for missing/wrong-clinic analysis once clinic access or patient linkage passes, `503 ERR_DEPENDENCY_UNAVAILABLE` on upstream outages).

### D. Gateway, OpenAPI Contracts & Automated Test Suites
- `src/gateway/AURA.ApiGateway/appsettings.json`:
  - Added Gateway YARP routes: `profile-admin-clinics`, `profile-admin-audit`, `identity-admin-accounts`, `identity-admin-account-status`, `identity-admin-account-roles`, `profile-consultation-doctors` (`Order: -5`), `profile-clinic-consultation-doctors` (`Order: -5`).
- `contracts/identity-openapi.yaml` & `contracts/profile-openapi.yaml`:
  - Added full OpenAPI 3.0.3 definitions for all new Admin and Consultation authority endpoints and schemas.
- Extended test suites in:
  - `tests/AURA.DevEnvironment.Checks/Run-Checks.ps1`
  - `tests/AURA.Profile.ClinicMembershipChecks/Program.cs`
  - `tests/AURA.Profile.AuthorizationChecks/Program.cs`
  - `tests/AURA.Profile.MultiTenantChecks/IdentityConnectionChecks.cs`
  - `tests/AURA.Profile.MultiTenantChecks/HttpChecks.cs`
  - `tests/AURA.Profile.MultiTenantChecks/DatabaseChecks.cs`

---

## 2. Executed Verification Results (Observed After Final Code Edit)

| Check / Command | Result | Scope & Exact Counts Observed |
|---|---|---|
| `dotnet build AURA.sln --configuration Release` | **PASS** | `0 Warning(s)`, `0 Error(s)` across all solution services and test projects |
| `python scripts/validate_openapi.py` | **PASS** | `PASS: OpenAPI 0.9 and campaign contracts document all 33 Analysis/Campaign routes and required review/report error schemas.` |
| YAML syntax parse on updated contracts | **PASS** | `yaml.safe_load` succeeded on `contracts/identity-openapi.yaml` and `contracts/profile-openapi.yaml` (`YAML_PARSE_OK`) |
| `dotnet test src/services/AURA.Analysis.Api.Tests/AURA.Analysis.Api.Tests.csproj --configuration Release --no-build` | **PASS** | `Passed: 416, Failed: 0, Skipped: 21, Total: 437` (the 21 skipped cases are Postgres integration tests skipped when no DB connection string is set) |
| `dotnet run --project tests/AURA.ServiceAuth.Checks/AURA.ServiceAuth.Checks.csproj --configuration Release --no-launch-profile` | **PASS** | `70/70 service-auth checks passed` (synthetic keys/credentials; no live HTTP/Firebase/DB) |
| `dotnet run --project tests/AURA.Profile.IdentityChecks/AURA.Profile.IdentityChecks.csproj --configuration Release --no-launch-profile` | **PASS** | `21/21 Profile identity checks passed` (fake HTTP delegating handler; not live Firebase) |
| `dotnet run --project tests/AURA.Profile.ClinicMembershipChecks/AURA.Profile.ClinicMembershipChecks.csproj --configuration Release --no-build --no-launch-profile` | **PASS** | `29/29 model/fixture checks passed` (includes `W6-M6-01.1` through `W6-M6-01.5`) |
| `dotnet run --project tests/AURA.Profile.AuthorizationChecks/AURA.Profile.AuthorizationChecks.csproj --configuration Release --no-build --no-launch-profile` | **PASS** | `35/35 CHECKS PASS` (includes 4 `[W6-M6-01]` consultation chat authority & revocation checks) |
| `dotnet run --project tests/AURA.Profile.MultiTenantChecks/AURA.Profile.MultiTenantChecks.csproj --configuration Release --no-build --no-launch-profile` | **PASS** | `38/38 model checks PASS` + `Authority PASS` (synthetic/model mode only) |
| `powershell -ExecutionPolicy Bypass -File tests/AURA.DevEnvironment.Checks/Run-Checks.ps1` | **PASS** | `62 development-configuration checks passed` (verified Gateway routes, mocked Docker migration ordering across 37 migrations + `019_admin_lifecycle_and_chat_authority.sql`) |
| `git diff --check` | **PASS** | Exit code `0`, zero trailing whitespace or conflict markers |

---

## 3. Not Executed Locally (Requires CI / Live Docker Runtime)

1. **`python scripts/validate_adr005_contracts.py`**: Not completed locally because `jsonschema` (`requirements-contract.txt`) is not installed in the host Python environment (`ModuleNotFoundError: No module named 'jsonschema'`).
2. **Live HTTP & PostgreSQL Integration (`AURA.Profile.MultiTenantChecks --http` / `--database`, `AURA.Profile.ClinicMembershipChecks --database`)**: Not executed because Docker Compose services and an isolated test PostgreSQL instance are not running locally (`docker ps` reported zero containers).
   - Once Docker services and migrations (`scripts/dev/Initialize-AuraDevDatabase.ps1`) are started locally or in CI (`docker-smoke`), run:
     - `dotnet run --project tests/AURA.Profile.MultiTenantChecks/AURA.Profile.MultiTenantChecks.csproj --configuration Release -- --http --database`
     - `dotnet run --project tests/AURA.Profile.ClinicMembershipChecks/AURA.Profile.ClinicMembershipChecks.csproj --configuration Release -- --database`
