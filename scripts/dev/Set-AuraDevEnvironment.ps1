[CmdletBinding()]
param(
    [string]$DockerNetwork = "aura_network",
    [string]$FirebaseProjectId = "aura-dev-97716",
    [string]$CredentialPath = "",
    [string]$NotificationKeyPath = "",
    [string]$NotificationKeyId = "local-v1",
    [string]$ServiceAuthDirectory = ""
)

$ErrorActionPreference = "Stop"

if ($NotificationKeyId -notmatch '^[A-Za-z0-9._-]{1,64}$') {
    throw "NotificationKeyId must contain 1-64 letters, digits, dots, underscores or hyphens."
}

if ([string]::IsNullOrWhiteSpace($DockerNetwork)) {
    throw "DockerNetwork must not be empty."
}

if ([string]::IsNullOrWhiteSpace($FirebaseProjectId)) {
    throw "FirebaseProjectId must not be empty."
}

if ([string]::IsNullOrWhiteSpace($CredentialPath)) {
    $CredentialPath = Join-Path $env:USERPROFILE `
        ".aura\credentials\aura-dev-service-account.json"
}

if ([string]::IsNullOrWhiteSpace($NotificationKeyPath)) {
    $NotificationKeyPath = Join-Path $env:USERPROFILE `
        ".aura\credentials\notification-token-key-v1.txt"
}
if ([string]::IsNullOrWhiteSpace($ServiceAuthDirectory)) {
    $ServiceAuthDirectory = Join-Path $env:USERPROFILE `
        ".aura\service-auth\local-v1"
}

foreach ($path in @($CredentialPath, $NotificationKeyPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "File not found: $path"
    }
}

$CredentialPath = (Resolve-Path -LiteralPath $CredentialPath).Path
$NotificationKeyPath = (
    Resolve-Path -LiteralPath $NotificationKeyPath
).Path

$identityClientPath = Join-Path $ServiceAuthDirectory "clients\identity-api.json"
$billingClientPath = Join-Path $ServiceAuthDirectory "clients\billing-api.json"
$profileClientPath = Join-Path $ServiceAuthDirectory "clients\profile-api.json"
$analysisClientPath = Join-Path $ServiceAuthDirectory "clients\analysis-api.json"
$workerClientPath = Join-Path $ServiceAuthDirectory "clients\analysis-worker.json"

foreach ($clientPath in @($identityClientPath, $billingClientPath, $profileClientPath, $analysisClientPath)) {
    if (-not (Test-Path -LiteralPath $clientPath -PathType Leaf)) {
        throw "Service client credentials file not found."
    }
}

$identityClient = Get-Content -LiteralPath $identityClientPath -Raw -Encoding UTF8 | ConvertFrom-Json
$billingClient = Get-Content -LiteralPath $billingClientPath -Raw -Encoding UTF8 | ConvertFrom-Json
$profileClient = Get-Content -LiteralPath $profileClientPath -Raw -Encoding UTF8 | ConvertFrom-Json

if ($identityClient.clientId -ne "identity-api" -or [string]::IsNullOrWhiteSpace([string]$identityClient.clientSecret)) {
    throw "Invalid Identity service client credentials."
}
if ($billingClient.clientId -ne "billing-api" -or [string]::IsNullOrWhiteSpace([string]$billingClient.clientSecret)) {
    throw "Invalid Billing service client credentials."
}

if ($profileClient.clientId -ne "profile-api" -or [string]::IsNullOrWhiteSpace([string]$profileClient.clientSecret)) {
    throw "Invalid Profile service client credentials."
}

$analysisClient = Get-Content -LiteralPath $analysisClientPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($analysisClient.clientId -ne "analysis-api" -or [string]::IsNullOrWhiteSpace([string]$analysisClient.clientSecret)) {
    throw "Invalid Analysis service client credentials."
}
$analysisServiceClientSecret = [string]$analysisClient.clientSecret
$identityServiceClientSecret = [string]$identityClient.clientSecret
$billingServiceClientSecret = [string]$billingClient.clientSecret
$profileServiceClientSecret = [string]$profileClient.clientSecret
$workerServiceClientSecret = $null
if(Test-Path -LiteralPath $workerClientPath -PathType Leaf) {
    $workerClient=Get-Content -LiteralPath $workerClientPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if($workerClient.clientId -ne 'analysis-worker' -or [string]::IsNullOrWhiteSpace([string]$workerClient.clientSecret)) {throw 'Invalid Worker service client credentials.'}
    $workerServiceClientSecret=[string]$workerClient.clientSecret
}

# Validate project without printing credential contents.
$credential = Get-Content -LiteralPath $CredentialPath -Raw |
    ConvertFrom-Json

if (
    $credential.type -ne "service_account" -or
    $credential.project_id -ne $FirebaseProjectId
) {
    throw "Service account does not match the selected Firebase project."
}

Remove-Variable credential

$keyText = (
    Get-Content -LiteralPath $NotificationKeyPath -Raw
).Trim()

try {
    $keyBytes = [Convert]::FromBase64String($keyText)
}
catch {
    throw "Notification encryption key is not valid Base64."
}

try {
    if ($keyBytes.Length -ne 32) {
        throw "Notification encryption key must contain exactly 32 bytes."
    }
}
finally {
    [Array]::Clear($keyBytes, 0, $keyBytes.Length)
}

function Read-AuraPassword {
    param([string]$Prompt)

    $secret = Read-Host $Prompt -AsSecureString

    try {
        if ($secret.Length -eq 0) {
            throw "Password must not be empty."
        }

        return (
            New-Object System.Net.NetworkCredential("", $secret)
        ).Password
    }
    finally {
        $secret.Dispose()
    }
}

try {
    $identityPassword = Read-AuraPassword "Mat khau aura_identity_app"
    $profilePassword = Read-AuraPassword "Mat khau aura_profile_app"
    $analysisPassword = Read-AuraPassword "Mat khau aura_analysis_app"

    $billingPassword = Read-AuraPassword `
        "Mat khau aura_billing_app"

    $notificationPassword = Read-AuraPassword `
        "Mat khau aura_notification_app"

    $values = @{
        AURA_DOCKER_NETWORK = $DockerNetwork
        FIREBASE_PROJECT_ID = $FirebaseProjectId
        FIREBASE_SERVICE_ACCOUNT_PATH = $CredentialPath.Replace("\", "/")
        NOTIFICATION_ENCRYPTION_KEY_ID = $NotificationKeyId
        NOTIFICATION_ENCRYPTION_KEY = $keyText
        NOTIFICATION_DB_PASSWORD = $notificationPassword
        IDENTITY_SERVICE_CLIENT_SECRET = $identityServiceClientSecret
        BILLING_SERVICE_CLIENT_SECRET = $billingServiceClientSecret
        PROFILE_SERVICE_CLIENT_SECRET = $profileServiceClientSecret
        ANALYSIS_SERVICE_CLIENT_SECRET = $analysisServiceClientSecret
        ANALYSIS_WORKER_CLIENT_SECRET = $workerServiceClientSecret

        # Npgsql connection strings quote these values in compose.yaml.
        IDENTITY_DB_PASSWORD = $identityPassword.Replace('"', '""')
        PROFILE_DB_PASSWORD = $profilePassword.Replace('"', '""')
        ANALYSIS_DB_PASSWORD = $analysisPassword.Replace('"', '""')
        BILLING_DB_PASSWORD = $billingPassword.Replace('"', '""')
    }

    foreach ($name in $values.Keys) {
        [Environment]::SetEnvironmentVariable(
            $name,
            $values[$name],
            "Process"
        )
    }

    Write-Host "PASS: AURA configuration loaded into this PowerShell process."
    Write-Host "Database passwords were loaded, not created or verified. Keep this window open."
}
finally {
    $identityPassword = $null
    $profilePassword = $null
    $analysisPassword = $null
    $billingPassword = $null
    $notificationPassword = $null
    $identityServiceClientSecret = $null
    $billingServiceClientSecret = $null
    $profileServiceClientSecret = $null
    $analysisServiceClientSecret = $null
    $workerServiceClientSecret = $null
    $workerClient = $null
    $analysisClient = $null
    $identityClient = $null
    $billingClient = $null
    $profileClient = $null
    $keyText = $null
    $values = $null
}
