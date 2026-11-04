[CmdletBinding()]
param(
    [string]$EnvFile = ".env",
    [string]$ComposeProject = "aura",
    [switch]$PlanOnly,
    [switch]$ConfigureRuntimeLogins
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path

# Run all service migrations, then transfer ownership and reapply restrictions.
$migrations = @(
    "infra/postgres/migrations/001_notification_devices.sql",
    "infra/postgres/migrations/002_service_database_roles.sql",
    "infra/postgres/migrations/002_identity_and_profile.sql",
    "infra/postgres/migrations/003_doctor_assignments.sql",
    "infra/postgres/migrations/003_billing_poc_packages.sql",
    "infra/postgres/migrations/004_billing_business_schema.sql",
    "infra/postgres/migrations/analysis/001_analysis_core.sql",
    "infra/postgres/migrations/analysis/002_upload_recovery.sql",
    "infra/postgres/migrations/analysis/002_outbox_publisher.sql",
    "infra/postgres/migrations/analysis/003_outbox_claim_generation.sql",
    "infra/postgres/migrations/analysis/004_batch_processing.sql",
    "infra/postgres/migrations/analysis/005_doctor_review_concurrency.sql",
    "infra/postgres/migrations/analysis/006_screening_campaigns.sql",
    "infra/postgres/migrations/analysis/007_batch_engine_progress.sql",
    "infra/postgres/migrations/analysis/008_doctor_review_icdr.sql",
    "infra/postgres/migrations/analysis/007_worker_fencing_and_lease.sql",
    "infra/postgres/migrations/analysis/008_worker_consistency.sql",
    "infra/postgres/migrations/analysis/009_upload_sessions.sql",
    "infra/postgres/migrations/analysis/010_report_snapshots.sql",
    "infra/postgres/migrations/005_camera_grants.sql",
    "infra/postgres/migrations/005_notification_inbox.sql",
    "infra/postgres/migrations/006_billing_settlement_inbox.sql",
    "infra/postgres/migrations/008_billing_payments.sql",
    "infra/postgres/migrations/009_billing_clinic_and_reconciliation.sql",
    "infra/postgres/migrations/010_billing_clinic_campaign_packages.sql",
    "infra/postgres/migrations/010_clinic_lifecycle_and_memberships.sql",
    "infra/postgres/migrations/011_clinic_patients_and_assignments.sql",
    "infra/postgres/migrations/012_clinic_invitations.sql",
    "infra/postgres/migrations/013_billing_refunds.sql",
    "infra/postgres/migrations/014_identity_doctor_capability_audit.sql",
    "infra/postgres/migrations/002_service_database_roles.sql",
    "infra/postgres/migrations/015_runtime_boundary_hardening.sql"
)

foreach ($migration in $migrations) {
    if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot $migration) -PathType Leaf)) {
        throw "Migration not found: $migration"
    }
}

if ($PlanOnly) {
    $migrations
    return
}

if ([System.IO.Path]::IsPathRooted($EnvFile)) {
    $envPath = $EnvFile
}
else {
    $envPath = Join-Path $repositoryRoot $EnvFile
}

if (-not (Test-Path -LiteralPath $envPath -PathType Leaf)) {
    throw "Environment file not found. Copy .env.example to .env and configure it first."
}

Get-Command docker -ErrorAction Stop | Out-Null
$composeArgs = @(
    "compose", "-p", $ComposeProject, "--env-file", $envPath,
    "-f", (Join-Path $repositoryRoot "compose.yaml")
)
$psqlArgs = @("psql", "-X", "-v", "ON_ERROR_STOP=1", "-U", "aura_dev", "-d", "aura_dev")

# Never print the resolved Compose configuration; it contains credentials.
& docker @composeArgs config -q
if ($LASTEXITCODE -ne 0) { throw "Compose configuration is invalid. No migration was run." }

$oldEncoding = $OutputEncoding
try {
    $OutputEncoding = New-Object System.Text.UTF8Encoding($false)
    foreach ($migration in $migrations) {
        Write-Host "Applying: $migration"
        Get-Content -LiteralPath (Join-Path $repositoryRoot $migration) -Raw -Encoding UTF8 |
            & docker @composeArgs exec -T postgres @psqlArgs
        if ($LASTEXITCODE -ne 0) {
            throw "Migration failed: $migration. Stop and inspect the error; do not delete volumes."
        }
    }
}
finally {
    $OutputEncoding = $oldEncoding
}

if ($ConfigureRuntimeLogins) {
    foreach ($service in @("identity", "profile", "analysis", "billing", "notification")) {
        $role = "aura_${service}_app"
        Write-Host "Set the PostgreSQL password for $role (enter it twice)."
        # psql encrypts the password before sending it to the server.
        # Do not put plaintext passwords in SQL, command arguments or this script.
        & docker @composeArgs exec postgres @psqlArgs -c "\password $role"
        if ($LASTEXITCODE -ne 0) { throw "Password setup failed for $role." }
        & docker @composeArgs exec -T postgres @psqlArgs -c "ALTER ROLE $role LOGIN;"
        if ($LASTEXITCODE -ne 0) { throw "Could not enable login for $role." }
    }
}

Write-Host "PASS: All service migrations and ownership/grants were applied."
if (-not $ConfigureRuntimeLogins) {
    Write-Host "Existing passwords were preserved. New runtime roles still need passwords and LOGIN."
}
