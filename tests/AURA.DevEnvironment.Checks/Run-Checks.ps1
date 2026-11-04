$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$setup = Join-Path $repositoryRoot "scripts/dev/Set-AuraDevEnvironment.ps1"
$initialize = Join-Path $repositoryRoot "scripts/dev/Initialize-AuraDevDatabase.ps1"
$temporary = Join-Path ([System.IO.Path]::GetTempPath()) ("aura-env-check-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $temporary | Out-Null
$credential = Join-Path $temporary "service-account.json"
$key = Join-Path $temporary "token-key.txt"
$variables = @(
    "AURA_DOCKER_NETWORK", "FIREBASE_PROJECT_ID", "FIREBASE_SERVICE_ACCOUNT_PATH",
    "NOTIFICATION_ENCRYPTION_KEY_ID", "NOTIFICATION_ENCRYPTION_KEY",
    "IDENTITY_DB_PASSWORD", "PROFILE_DB_PASSWORD", "ANALYSIS_DB_PASSWORD",
    "BILLING_DB_PASSWORD", "NOTIFICATION_DB_PASSWORD",
    "IDENTITY_SERVICE_CLIENT_SECRET", "BILLING_SERVICE_CLIENT_SECRET", "PROFILE_SERVICE_CLIENT_SECRET", "ANALYSIS_SERVICE_CLIENT_SECRET", "ANALYSIS_WORKER_CLIENT_SECRET"
)
$original = @{}
foreach ($name in $variables) { $original[$name] = [Environment]::GetEnvironmentVariable($name, "Process") }
$previousExitCode = $global:LASTEXITCODE
$previousState = Get-Variable AuraM1EnvironmentCheckState -Scope Global -ErrorAction SilentlyContinue
$previousStateValue = if ($previousState) { $previousState.Value } else { $null }
$global:AuraM1EnvironmentCheckState = @{}
$global:AuraM1EnvironmentCheckState.passed = 0
$global:AuraM1EnvironmentCheckState.password = 'test-only-password"with-quote'
$global:AuraM1EnvironmentCheckState.emptyAt = -1
$global:AuraM1EnvironmentCheckState.promptCount = 0

# Exercise script input without using any real credentials or interactive prompts.
function Read-Host {
    param([string]$Prompt, [switch]$AsSecureString)
    $global:AuraM1EnvironmentCheckState.promptCount++
    if ($global:AuraM1EnvironmentCheckState.promptCount -eq $global:AuraM1EnvironmentCheckState.emptyAt) { return (New-Object System.Security.SecureString) }
    return (ConvertTo-SecureString $global:AuraM1EnvironmentCheckState.password -AsPlainText -Force)
}

function Assert-Check {
    param([bool]$Condition, [string]$Name)
    if (-not $Condition) { throw "FAIL: $Name" }
    $global:AuraM1EnvironmentCheckState.passed++
    Write-Host "PASS: $Name"
}

function Assert-SetupRejected {
    param([hashtable]$Arguments, [string]$Name)
    $before = [Environment]::GetEnvironmentVariable("FIREBASE_PROJECT_ID", "Process")
    $failed = $false
    try { & $setup @Arguments }
    catch { $failed = $true }
    Assert-Check $failed $Name
    Assert-Check ([Environment]::GetEnvironmentVariable("FIREBASE_PROJECT_ID", "Process") -eq $before) ($Name + " preserves existing configuration")
}

try {
    # Deliberately not a signing credential: this fixture only tests configuration validation.
    [IO.File]::WriteAllText($credential, '{"type":"service_account","project_id":"aura-dev-97716"}')
    [IO.File]::WriteAllText($key, [Convert]::ToBase64String((New-Object byte[] 32)))
    $serviceAuthDirectory = Join-Path $temporary "service-auth"
    $clientsDirectory = Join-Path $serviceAuthDirectory "clients"
    New-Item -ItemType Directory -Path $clientsDirectory -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $clientsDirectory "identity-api.json"), '{"clientId":"identity-api","clientSecret":"test-identity-client-secret"}')
    [IO.File]::WriteAllText((Join-Path $clientsDirectory "billing-api.json"), '{"clientId":"billing-api","clientSecret":"test-billing-client-secret"}')
    [IO.File]::WriteAllText((Join-Path $clientsDirectory "profile-api.json"), '{"clientId":"profile-api","clientSecret":"test-profile-client-secret"}')
    [IO.File]::WriteAllText((Join-Path $clientsDirectory "analysis-api.json"), '{"clientId":"analysis-api","clientSecret":"test-analysis-client-secret"}')
    $arguments = @{ CredentialPath = $credential; NotificationKeyPath = $key; ServiceAuthDirectory = $serviceAuthDirectory }

    & $setup @arguments -DockerNetwork "existing-aura-network" -NotificationKeyId "existing-key-id"
    Assert-Check ($global:AuraM1EnvironmentCheckState.promptCount -eq 5) "All five runtime database accounts are prompted"
    Assert-Check ($env:AURA_DOCKER_NETWORK -eq "existing-aura-network") "Existing network can be retained"
    Assert-Check ($env:NOTIFICATION_ENCRYPTION_KEY_ID -eq "existing-key-id") "Existing notification key ID can be retained"
    Assert-Check ($env:FIREBASE_SERVICE_ACCOUNT_PATH -eq $credential.Replace('\', '/')) "Credential path is normalized for Docker"
    foreach ($name in @("IDENTITY_DB_PASSWORD", "PROFILE_DB_PASSWORD", "ANALYSIS_DB_PASSWORD", "BILLING_DB_PASSWORD")) {
        Assert-Check ([Environment]::GetEnvironmentVariable($name) -eq $global:AuraM1EnvironmentCheckState.password.Replace('"', '""')) ($name + " supports quoted Npgsql passwords")
    }
    Assert-Check ($env:NOTIFICATION_DB_PASSWORD -eq $global:AuraM1EnvironmentCheckState.password) "Notification separate password field remains unchanged"
    Assert-Check ($env:IDENTITY_SERVICE_CLIENT_SECRET -eq "test-identity-client-secret") "Identity service secret is loaded from its client credentials file"
    Assert-Check ($env:BILLING_SERVICE_CLIENT_SECRET -eq "test-billing-client-secret") "Billing service secret is loaded from its client credentials file"
    Assert-Check ($env:PROFILE_SERVICE_CLIENT_SECRET -eq "test-profile-client-secret") "Profile service secret is loaded from its own client credentials file"

    Assert-Check ($env:ANALYSIS_SERVICE_CLIENT_SECRET -eq "test-analysis-client-secret") "Analysis service secret is loaded from its own client credentials file"

    [IO.File]::WriteAllText($credential, '{"type":"service_account","project_id":"wrong-project"}')
    Assert-SetupRejected $arguments "Mismatched Firebase project is rejected"
    [IO.File]::WriteAllText($credential, '{"type":"service_account","project_id":"aura-dev-97716"}')
    [IO.File]::WriteAllText($key, "invalid-base64")
    Assert-SetupRejected $arguments "Invalid Base64 is rejected"
    [IO.File]::WriteAllText($key, [Convert]::ToBase64String((New-Object byte[] 16)))
    Assert-SetupRejected $arguments "Wrong encryption-key length is rejected"
    [IO.File]::WriteAllText($key, [Convert]::ToBase64String((New-Object byte[] 32)))
    Assert-SetupRejected @{ CredentialPath = (Join-Path $temporary "absent.json"); NotificationKeyPath = $key } "Missing credential file is rejected"
    $global:AuraM1EnvironmentCheckState.emptyAt = $global:AuraM1EnvironmentCheckState.promptCount + 3
    Assert-SetupRejected $arguments "Empty runtime password is rejected before environment mutation"
    $global:AuraM1EnvironmentCheckState.emptyAt = -1

    $plan = @(& $initialize -PlanOnly)
    Assert-Check ($plan.Count -eq 33) "Migration plan includes batch progress, ICDR reviews, every service, Billing settlement inbox and final grants pass"
    Assert-Check ($plan[0] -eq "infra/postgres/migrations/001_notification_devices.sql") "Notification schema is bootstrapped first"
    Assert-Check ($plan[2] -eq "infra/postgres/migrations/002_identity_and_profile.sql") "Identity/Profile migration is included"
    Assert-Check ($plan[3] -eq "infra/postgres/migrations/003_doctor_assignments.sql") "Doctor assignments follow Profile schema"
    Assert-Check ($plan[4] -eq "infra/postgres/migrations/003_billing_poc_packages.sql") "Legacy Billing PoC migration is included"
    Assert-Check ($plan[5] -eq "infra/postgres/migrations/004_billing_business_schema.sql") "Billing business migration follows Billing PoC schema"
    Assert-Check ($plan[7] -eq "infra/postgres/migrations/analysis/002_upload_recovery.sql") "Upload recovery follows Analysis core and precedes final grants"
    Assert-Check ($plan[10] -eq "infra/postgres/migrations/analysis/004_batch_processing.sql") "Batch processing follows upload recovery"
    Assert-Check ($plan[11] -eq "infra/postgres/migrations/analysis/005_doctor_review_concurrency.sql") "Doctor review concurrency follows Analysis core"
    Assert-Check ($plan[12] -eq "infra/postgres/migrations/analysis/006_screening_campaigns.sql") "Screening campaigns follows doctor review concurrency"
    Assert-Check ($plan[13] -eq "infra/postgres/migrations/analysis/007_batch_engine_progress.sql") "Batch progress cache reconstruction follows campaign schema"
    Assert-Check ($plan[14] -eq "infra/postgres/migrations/analysis/008_doctor_review_icdr.sql") "Doctor-approved ICDR grade follows the review schema"
    Assert-Check ($plan[19] -eq "infra/postgres/migrations/005_camera_grants.sql") "Camera delegation grants follow Analysis core"
    Assert-Check ($plan[20] -eq "infra/postgres/migrations/005_notification_inbox.sql") "Notification inbox follows camera grants"
    Assert-Check ($plan[21] -eq "infra/postgres/migrations/006_billing_settlement_inbox.sql") "Billing settlement inbox follows notification inbox"
    Assert-Check ($plan[22] -eq "infra/postgres/migrations/008_billing_payments.sql") "Billing payments follows settlement inbox"
    Assert-Check ($plan[23] -eq "infra/postgres/migrations/009_billing_clinic_and_reconciliation.sql") "Billing clinic packages and reconciliation follow payments"
    Assert-Check ($plan[25] -eq "infra/postgres/migrations/010_clinic_lifecycle_and_memberships.sql") "Clinic lifecycle and memberships follow billing clinic reconciliation"
    Assert-Check ($plan[26] -eq "infra/postgres/migrations/011_clinic_patients_and_assignments.sql") "Clinic patients and doctor assignments extension follow clinic lifecycle"
    Assert-Check ($plan[-3] -eq "infra/postgres/migrations/002_service_database_roles.sql" -and $plan[-2] -eq "infra/postgres/migrations/015_runtime_boundary_hardening.sql" -and $plan[-1] -eq "infra/postgres/migrations/016_billing_event_compatibility.sql") "Final blanket grants are followed by ADR005 append-only and runtime boundary hardening"

    $global:AuraM1EnvironmentCheckState.dockerCalls = New-Object System.Collections.Generic.List[string]
    $global:AuraM1EnvironmentCheckState.failOnCall = -1
    function docker {
        [CmdletBinding(PositionalBinding = $false)]
        param(
            [Parameter(ValueFromPipeline = $true)]$InputObject,
            [Parameter(ValueFromRemainingArguments = $true)][object[]]$DockerArguments
        )
        process { }
        end {
            $global:AuraM1EnvironmentCheckState.dockerCalls.Add(($DockerArguments -join " "))
            $global:LASTEXITCODE = if ($global:AuraM1EnvironmentCheckState.dockerCalls.Count -eq $global:AuraM1EnvironmentCheckState.failOnCall) { 1 } else { 0 }
        }
    }
    $originalEncoding = $OutputEncoding
    & $initialize -EnvFile ".env.example"
    Assert-Check ($global:AuraM1EnvironmentCheckState.dockerCalls.Count -eq 34) "Migration runner invokes config validation and all thirty-three migrations"
    Assert-Check ($OutputEncoding -eq $originalEncoding) "Migration runner restores pipeline encoding"
    Assert-Check (@($global:AuraM1EnvironmentCheckState.dockerCalls | Where-Object { $_ -like '*ALTER ROLE*' }).Count -eq 0) "Default migration run preserves existing passwords/logins"
    $global:AuraM1EnvironmentCheckState.dockerCalls.Clear()
    $global:AuraM1EnvironmentCheckState.failOnCall = 4
    $failed = $false
    try { & $initialize -EnvFile ".env.example" }
    catch { $failed = $true }
    Assert-Check ($failed -and $global:AuraM1EnvironmentCheckState.dockerCalls.Count -eq 4) "A failed migration stops later migrations"
    Assert-Check ($OutputEncoding -eq $originalEncoding) "Failed migration also restores pipeline encoding"
    $global:AuraM1EnvironmentCheckState.dockerCalls.Clear()
    $global:AuraM1EnvironmentCheckState.failOnCall = 1
    $failed = $false
    try { & $initialize -EnvFile ".env.example" }
    catch { $failed = $true }
    Assert-Check ($failed -and $global:AuraM1EnvironmentCheckState.dockerCalls.Count -eq 1) "Invalid Compose configuration runs no migration"

    Write-Host "PASS: $($global:AuraM1EnvironmentCheckState.passed) development-configuration checks. Docker/HTTP/database runtime were not exercised by this suite."
}
finally {
    $global:LASTEXITCODE = $previousExitCode
    foreach ($name in $variables) { [Environment]::SetEnvironmentVariable($name, $original[$name], "Process") }
    Remove-Item -LiteralPath $temporary -Recurse -Force
    if ($previousState) {
        $global:AuraM1EnvironmentCheckState = $previousStateValue
    }
    else {
        Remove-Variable AuraM1EnvironmentCheckState -Scope Global
    }
}
