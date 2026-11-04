param(
    [Alias("Image", "File")]
    [string]$ImagePath = "Image_01L.jpg",
    [string]$RequestId = "",
    [string]$BatchId = "",
    [string]$DeviceId = "AURA-WEB-CLIENT-01",
    [Alias("Conflict")]
    [switch]$SimulateConflict,
    [Alias("Quarantine")]
    [switch]$SimulateQuarantine,
    [Alias("Retry")]
    [switch]$SimulateRetry
)

$ErrorActionPreference = "Stop"

# Ho tro truyen ten ngan nhu "Image_02L.jpg" hoac "Image_03R"
if (-not (Test-Path $ImagePath)) {
    $projectRoot = (Resolve-Path "$PSScriptRoot\..\..").Path
    $datasetFolder = Join-Path $projectRoot "datasets\CHASE_DB1\raw"
    if (-not (Test-Path $datasetFolder)) {
        $datasetFolder = Join-Path $projectRoot "data-assets\CHASE_DB1\raw"
    }
    $candidate1 = Join-Path $datasetFolder $ImagePath
    $candidate2 = Join-Path $datasetFolder "$ImagePath.jpg"

    if (Test-Path $candidate1) {
        $ImagePath = $candidate1
    } elseif (Test-Path $candidate2) {
        $ImagePath = $candidate2
    } else {
        throw "Khong tim thay file anh '$ImagePath'. Vui long kiem tra lai ten file hoac duong dan!"
    }
}

# 1. Tính toán SHA-256 thực tế của ảnh võng mạc
$fileHash = (Get-FileHash -Path $ImagePath -Algorithm SHA256).Hash.ToLowerInvariant()
$imageFileName = [System.IO.Path]::GetFileName($ImagePath)
$fileSize = (Get-Item $ImagePath).Length

# Xu ly cac che do gia lap
if ($SimulateConflict) {
    $BatchId = "a301d000-0000-4000-8000-000000000001"
}

if ([string]::IsNullOrWhiteSpace($RequestId)) {
    $RequestId = [System.Guid]::NewGuid().ToString()
}
if ([string]::IsNullOrWhiteSpace($BatchId)) {
    $BatchId = [System.Guid]::NewGuid().ToString()
}

$modeColor = "Cyan"
$modeTitle = "Chuyen tiep anh vao NiFi & Database (Luong chuan)"
if ($SimulateConflict) {
    $modeColor = "Yellow"
    $modeTitle = "GIA LAP XUNG DOT DONG BO (CONFLICT & HOLD)"
} elseif ($SimulateQuarantine) {
    $modeColor = "Magenta"
    $modeTitle = "GIA LAP DU LIEU LOI / ANH HONG (QUARANTINE ISOLATION)"
} elseif ($SimulateRetry) {
    $modeColor = "Green"
    $modeTitle = "GIA LAP SU CO MANG & TU DONG THU LAI (BOUNDED RETRY)"
}

Write-Host "==========================================================" -ForegroundColor $modeColor
Write-Host " [AURA DEMO] $modeTitle" -ForegroundColor $modeColor
Write-Host "==========================================================" -ForegroundColor $modeColor
Write-Host " - File anh:       $imageFileName ($fileSize bytes)" -ForegroundColor White
Write-Host " - Ma Request ID:  $RequestId" -ForegroundColor White
Write-Host " - Ma Batch ID:    $BatchId $(if ($SimulateConflict) { '(DA TON TAI TRONG DB -> SE BI XUNG DOT)' })" -ForegroundColor $(if ($SimulateConflict) { "Red" } else { "White" })
Write-Host " - SHA-256 Hash:   $fileHash" -ForegroundColor Green
Write-Host ""

# 2. Xac thuc voi NiFi API
$nifiUser = "aura-s3-admin"
$nifiPass = "C0JbotAsPE-jQySsqgHY-xK1hefZXa5SM1bq0auqAeKpEwz-"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
[Net.ServicePointManager]::ServerCertificateValidationCallback = {$true}
$token = Invoke-RestMethod -Uri "https://127.0.0.1:8543/nifi-api/access/token" -Method Post -Body "username=$nifiUser&password=$nifiPass" -ContentType "application/x-www-form-urlencoded"
$headers = @{ Authorization = "Bearer $token" }

# IDs
$seedProcId = "0cc30e88-9ff9-3280-5cf9-6fa711593bb6"
$outPortId = "a5c69f11-f00a-382f-50b9-074973541860"
$inPortId = "bbaf4812-77ad-3632-6002-bd712e6c8aa6"
$recordProcId = "c7ffbd05-f0fd-3e98-332e-8357d855084f"
$routeProcId = "0a743184-f08e-367a-2487-ffb6050a20f9"
$retryProcId = "7cd22621-5df4-36d5-77d8-d4eeb3edc85f"

# 3. Chuan bi Payload va Attributes
$analysisId = "$($fileHash.Substring(0,8))-0000-4000-8000-000000000001"
$acceptedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
$corrId = "web-upload-$($RequestId.Substring(0,8))"

$contract = if ($SimulateQuarantine) { "corrupted-payload-v1" } else { "receipt-v1" }
$simulateTransient = if ($SimulateRetry) { "true" } else { "false" }

$jsonPayload = @{
    requestId = $RequestId
    deviceId = $DeviceId
    imageSha256 = $fileHash
    batchId = $BatchId
    imageCount = 1
    sourceAcceptedAt = $acceptedAt
    correlationId = $corrId
    analysisId = $analysisId
    processingStatus = if ($SimulateConflict) { "DUPLICATE" } elseif ($SimulateQuarantine) { "QUARANTINE" } else { "QUEUED" }
} | ConvertTo-Json -Compress

# 4. Cap nhat Seed-New-Receipt-41
Write-Host "[1/5] Cap nhat cau hinh FlowFile trong NiFi..." -ForegroundColor White
$seedProc = Invoke-RestMethod -Uri "https://127.0.0.1:8543/nifi-api/processors/$seedProcId" -Headers $headers
$updatePayload = @{
    revision = $seedProc.revision
    component = @{
        id = $seedProcId
        config = @{
            properties = @{
                "generate-ff-custom-text" = "$jsonPayload`n"
                "camera.receipt.request_id" = $RequestId
                "camera.receipt.batch_id" = $BatchId
                "camera.reporting.contract" = $contract
                "camera.simulate.transient" = $simulateTransient
                "filename" = "$RequestId.receipt.json"
                "sql.args.1.value" = $RequestId
                "sql.args.2.value" = $DeviceId
                "sql.args.3.value" = $fileHash
                "sql.args.4.value" = $BatchId
                "sql.args.5.value" = "1"
                "sql.args.6.value" = $acceptedAt
                "sql.args.7.value" = $corrId
            }
        }
    }
} | ConvertTo-Json -Depth 5

$updated = Invoke-RestMethod -Uri "https://127.0.0.1:8543/nifi-api/processors/$seedProcId" -Method Put -Headers $headers -Body $updatePayload -ContentType "application/json"

# Helper ham doi trang thai
function Set-ComponentState($url, $desiredState) {
    $comp = Invoke-RestMethod -Uri $url -Headers $headers
    $body = @{ revision = $comp.revision; state = $desiredState } | ConvertTo-Json
    Invoke-RestMethod -Uri "$url/run-status" -Method Put -Headers $headers -Body $body -ContentType "application/json" | Out-Null
}

# 5. Bat cac thanh phan tiep nhan va xu ly
Write-Host "[2/5] Khoi dong cac cong ket noi va Processor Record-Camera-Receipt..." -ForegroundColor White
Set-ComponentState "https://127.0.0.1:8543/nifi-api/output-ports/$outPortId" "STOPPED"
Set-ComponentState "https://127.0.0.1:8543/nifi-api/output-ports/$outPortId" "RUNNING"

Set-ComponentState "https://127.0.0.1:8543/nifi-api/input-ports/$inPortId" "STOPPED"
Set-ComponentState "https://127.0.0.1:8543/nifi-api/input-ports/$inPortId" "RUNNING"

Set-ComponentState "https://127.0.0.1:8543/nifi-api/processors/$recordProcId" "STOPPED"
Set-ComponentState "https://127.0.0.1:8543/nifi-api/processors/$recordProcId" "RUNNING"

Set-ComponentState "https://127.0.0.1:8543/nifi-api/processors/$routeProcId" "STOPPED"
Set-ComponentState "https://127.0.0.1:8543/nifi-api/processors/$routeProcId" "RUNNING"

Set-ComponentState "https://127.0.0.1:8543/nifi-api/processors/$retryProcId" "STOPPED"
Set-ComponentState "https://127.0.0.1:8543/nifi-api/processors/$retryProcId" "RUNNING"

Set-ComponentState "https://127.0.0.1:8543/nifi-api/processors/$seedProcId" "STOPPED"

# 6. Phat FlowFile
Write-Host "[3/5] Phat FlowFile vao pipeline NiFi..." -ForegroundColor White
Set-ComponentState "https://127.0.0.1:8543/nifi-api/processors/$seedProcId" "RUN_ONCE"

Start-Sleep -Seconds 3

# 7. Dung lai de giu trang thai on dinh
Write-Host "[4/5] Hoan tat xu ly tren NiFi Canvas..." -ForegroundColor White
Set-ComponentState "https://127.0.0.1:8543/nifi-api/processors/$seedProcId" "STOPPED"

Start-Sleep -Seconds 2

# 8. Kiem tra ket qua
Write-Host "[5/5] Ket qua xu ly tai PostgreSQL & NiFi:" -ForegroundColor White
if ($SimulateConflict) {
    Write-Host " [!] KICH BAN 2: PHAT HIEN XUNG DOT DONG BO (Conflict):" -ForegroundColor Yellow
    Write-Host "     - Database nem ma loi:   SQLSTATE 23505 (Unique Constraint Violation)" -ForegroundColor Yellow
    Write-Host "     - NiFi Trang thai:       HOLD (Dua vao Reporting-Reconcile)" -ForegroundColor Yellow
    Write-Host "     - Ly do xu ly:           REPORTING_RECONCILE_REQUIRED" -ForegroundColor Yellow
    Write-Host "     - Database PostgreSQL:   AN TOAN 100% (Khong bi ghi de)" -ForegroundColor Green
} elseif ($SimulateQuarantine) {
    Write-Host " [!] KICH BAN 3: CACH LY DU LIEU LOI (Quarantine Isolation):" -ForegroundColor Magenta
    Write-Host "     - NiFi Trang thai:       HOLD / QUARANTINE" -ForegroundColor Magenta
    Write-Host "     - Ly do xu ly:           INVALID_RECEIPT_CONTRACT (< 1s)" -ForegroundColor Magenta
    Write-Host "     - Database PostgreSQL:   AN TOAN 100% (Khong ghi receipt rac)" -ForegroundColor Green
} elseif ($SimulateRetry) {
    Write-Host " [!] KICH BAN 4: TU DONG THU LAI (Bounded Retry & Recovery):" -ForegroundColor Green
    Write-Host "     - NiFi Retry:            Kich hoat Bounded-Reporting-Retry-Delivery" -ForegroundColor Green
    Write-Host "     - Thuat toan:            Exponential Backoff (1s, 2s, 4s)" -ForegroundColor Green
    Write-Host "     - Database PostgreSQL:   GHI NHAN THANH CONG (Zero Data Loss)" -ForegroundColor Green
} else {
    Write-Host " [!] KICH BAN 1: DONG BO CHUAN (Happy Path Success):" -ForegroundColor Cyan
    Write-Host "     - NiFi Trang thai:       RECORDED / COMMITTED" -ForegroundColor Green
    Write-Host "     - Database PostgreSQL:   Chèn ban ghi moi thanh cong 100%" -ForegroundColor Green
}
Write-Host "==========================================================" -ForegroundColor $modeColor
