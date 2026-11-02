# Script khoi dong nhanh he thong AURA Demo
Write-Host "Dang khoi dong cac container Docker..." -ForegroundColor Cyan
docker start aura-postgres-1 aura-s3-reporting-postgres-1 aura-s3-rabbitmq-1 aura-s3-nifi-1 aura-ai77-539ab735c32d401c

Write-Host "Dang khoi dong cac backend services (.NET)..." -ForegroundColor Cyan
$env:GOOGLE_APPLICATION_CREDENTIALS = "$env:USERPROFILE\.aura\credentials\aura-dev-service-account.json"
$repoRoot = (Resolve-Path "$PSScriptRoot\..\..").Path
Start-Process dotnet -ArgumentList "run --launch-profile http" -WorkingDirectory "$repoRoot\src\services\AURA.Identity.Api" -WindowStyle Hidden
Start-Process dotnet -ArgumentList "run --launch-profile http" -WorkingDirectory "$repoRoot\src\services\AURA.Profile.Api" -WindowStyle Hidden
Start-Process dotnet -ArgumentList "run --launch-profile http" -WorkingDirectory "$repoRoot\src\gateway\AURA.ApiGateway" -WindowStyle Hidden

Write-Host "Dang khoi dong Web Frontend..." -ForegroundColor Cyan
Start-Process npm -ArgumentList "run dev" -WorkingDirectory "$repoRoot\src\web\aura-web" -WindowStyle Hidden

Write-Host ""
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "HE THONG AURA DA KHOI DONG THANH CONG!" -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "1. Web AURA:           http://localhost:5173" -ForegroundColor Yellow
Write-Host "   (Tai khoan: Google cua ban)" -ForegroundColor Gray
Write-Host "2. Apache NiFi:        https://127.0.0.1:8543/nifi" -ForegroundColor Yellow
Write-Host "   (aura-s3-admin / C0JbotAsPE-jQySsqgHY-xK1hefZXa5SM1bq0auqAeKpEwz-)" -ForegroundColor Gray
Write-Host "3. RabbitMQ:           http://localhost:15372" -ForegroundColor Yellow
Write-Host "   (aura_dev / 20fa035cbaa24341b75d05dad081935a)" -ForegroundColor Gray
Write-Host "==========================================================" -ForegroundColor Green
