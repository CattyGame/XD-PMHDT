# Script dung toan bo he thong AURA
Write-Host "Dang dung cac tien trinh host (.NET, Node)..." -ForegroundColor Cyan
Stop-Process -Name "AURA.ApiGateway", "AURA.Identity.Api", "AURA.Profile.Api", "AURA.Analysis.Api", "AURA.Billing.Api", "dotnet", "node" -Force -ErrorAction SilentlyContinue

Write-Host "Dang dung cac container Docker..." -ForegroundColor Cyan
docker stop aura-postgres-1 aura-redis-1 aura-azurite-1 aura-ai-core-1 aura-s3-reporting-postgres-1 aura-s3-rabbitmq-1 aura-s3-nifi-1 aura-ai77-539ab735c32d401c aura-rabbitmq-amqp-forwarder aura-pg87s-c4fdb65507bd41d9 aura-identity81-7ed83e5b3fdc44de aura-lab51-ac28e39b2b59-rabbitmq

Write-Host ""
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "TOAN BO HE THONG AURA DA DUOC TAT AN TOAN!" -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
