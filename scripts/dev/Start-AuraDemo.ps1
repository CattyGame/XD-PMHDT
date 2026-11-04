# Script khoi dong toan bo he thong AURA
$rootDir = (Resolve-Path "$PSScriptRoot\..\..").Path

Write-Host "Dang khoi dong cac container Docker..." -ForegroundColor Cyan
docker start aura-postgres-1 aura-redis-1 aura-azurite-1 aura-ai-core-1 aura-s3-reporting-postgres-1 aura-s3-rabbitmq-1 aura-s3-nifi-1 aura-ai77-539ab735c32d401c aura-rabbitmq-amqp-forwarder

Write-Host "Dang khoi dong cac backend services (.NET)..." -ForegroundColor Cyan
$env:GOOGLE_APPLICATION_CREDENTIALS = "$env:USERPROFILE\.aura\credentials\aura-dev-service-account.json"

function Start-AuraService($dir, $cmd) {
    Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
        CommandLine = "cmd.exe /c cd /d `"$dir`" && $cmd"
    } | Out-Null
}

Start-AuraService "$rootDir\src\services\AURA.Identity.Api" "dotnet run --launch-profile http"
Start-AuraService "$rootDir\src\services\AURA.Profile.Api" "dotnet run --launch-profile http"
Start-AuraService "$rootDir\src\services\AURA.Billing.Api" "dotnet run --launch-profile http"
Start-AuraService "$rootDir\src\services\AURA.Analysis.Api" "dotnet run --launch-profile http"
Start-AuraService "$rootDir\src\gateway\AURA.ApiGateway" "dotnet run --launch-profile http"

Write-Host "Dang khoi dong Web Frontend..." -ForegroundColor Cyan
Start-AuraService "$rootDir\src\web\aura-web" "npm run dev"

Write-Host ""
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "HE THONG AURA DA KHOI DONG THANH CONG!" -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "1. Web AURA (Frontend):    http://localhost:5173" -ForegroundColor Yellow
Write-Host "2. API Gateway:            http://localhost:5000" -ForegroundColor Yellow
Write-Host "3. Analysis API:           http://localhost:5001" -ForegroundColor Yellow
Write-Host "4. Billing API:            http://localhost:5002" -ForegroundColor Yellow
Write-Host "5. Identity API:           http://127.0.0.1:5004" -ForegroundColor Yellow
Write-Host "6. Profile API:            http://127.0.0.1:5005" -ForegroundColor Yellow
Write-Host "7. AI Core (FastAPI):      http://127.0.0.1:8000" -ForegroundColor Yellow
Write-Host "8. Apache NiFi:            https://127.0.0.1:8543/nifi" -ForegroundColor Yellow
Write-Host "9. RabbitMQ Management:    http://localhost:15372" -ForegroundColor Yellow
Write-Host "==========================================================" -ForegroundColor Green
