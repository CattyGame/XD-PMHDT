[CmdletBinding()]
param([string]$OutputDirectory = 'artifacts/m1-isolated-acceptance')
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputRoot) { throw 'Use a new output directory to avoid mixing evidence from different runs.' }
[IO.Directory]::CreateDirectory($outputRoot) | Out-Null
$suffix = [Guid]::NewGuid().ToString('N')
$postgres = "aura-test-m1-pg-$suffix"
$rabbit = "aura-test-m1-rabbit-$suffix"
$blob = "aura-test-m1-blob-$suffix"
$owned = [Collections.Generic.List[string]]::new()
$envNames = @('POSTGRES_PASSWORD','RABBITMQ_DEFAULT_PASS','AURA_TEST_POSTGRES','AURA_TEST_RABBITMQ_PASSWORD','AURA_TEST_RABBITMQ_PORT','AURA_TEST_AZURITE_CONNECTION','AURA_SCRUM345_TEST_CONNECTION_STRING','AURA_SCRUM370_TEST_CONNECTION_STRING','ADR005_TEST_POSTGRES')
$saved = @{}
foreach ($key in $envNames) { $saved[$key] = [Environment]::GetEnvironmentVariable($key,'Process') }
$evidence = [ordered]@{status='RUNNING'; startedAt=[DateTimeOffset]::UtcNow.ToString('O'); commit=(& git -C $repo rev-parse HEAD); scope='Isolated infrastructure and repository regression; not clinical/browser end-to-end acceptance'; migrations=@(); tests=@()}
$inputFiles = @(& git -C $repo diff --name-only) + @('scripts/dev/Test-M1IsolatedAcceptance.ps1','tests/AURA.M1.RegressionTests/MetricsTests.cs','infra/rabbitmq/enabled_plugins','infra/monitoring/prometheus/alert.rules.test.yml')
$evidence.workspaceChanges = @(foreach ($file in $inputFiles | Sort-Object -Unique) {
    if (Test-Path -LiteralPath (Join-Path $repo $file) -PathType Leaf) {
        @{path=$file; sha256=(Get-FileHash -LiteralPath (Join-Path $repo $file)).Hash}
    }
})
function Invoke-TestDocker([string[]]$Arguments) {
    $result = & docker @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Docker command failed: $($Arguments[0])" }
    return $result
}
function Port([string]$Name,[string]$Internal) {
    return [int]((Invoke-TestDocker @('port',$Name,$Internal)) -split ':')[-1]
}
function Wait-Ready([string]$Name,[string[]]$Command) {
    for ($attempt=0; $attempt -lt 90; $attempt++) {
        if ((Invoke-TestDocker @('inspect','--format','{{.State.Running}}',$Name)) -ne 'true') { throw "Container stopped before readiness: $Name" }
        & docker exec $Name @Command *> $null
        if ($LASTEXITCODE -eq 0) { return }
        Start-Sleep -Seconds 1
    }
    throw "Container did not become ready: $Name"
}
function Suite([string]$Name,[string]$Project) {
    $resultDir = Join-Path $outputRoot $Name
    & dotnet test (Join-Path $repo $Project) --logger 'trx;LogFileName=results.trx' --results-directory $resultDir *> (Join-Path $outputRoot "$Name.log")
    if ($LASTEXITCODE -ne 0) { throw "Suite failed: $Name; inspect its log." }
    [xml]$trx = Get-Content -LiteralPath (Join-Path $resultDir 'results.trx') -Raw
    $c = $trx.TestRun.ResultSummary.Counters
    if ([int]$c.total -le 0 -or [int]$c.passed -ne [int]$c.total -or [int]$c.notExecuted -ne 0) { throw "Suite $Name did not execute and pass every test." }
    $evidence.tests += @{name=$Name; total=[int]$c.total; passed=[int]$c.passed; skipped=[int]$c.notExecuted}
}
try {
    $env:POSTGRES_PASSWORD = [Guid]::NewGuid().ToString('N')
    $env:RABBITMQ_DEFAULT_PASS = [Guid]::NewGuid().ToString('N')
    Invoke-TestDocker @('run','-d','--name',$postgres,'-p','127.0.0.1::5432','--env','POSTGRES_PASSWORD','--env','POSTGRES_USER=aura_dev','--env','POSTGRES_DB=aura_dev','postgres:17-alpine') | Out-Null
    $owned.Add($postgres)
    $plugins = Join-Path $repo 'infra/rabbitmq/enabled_plugins'
    Invoke-TestDocker @('run','-d','--name',$rabbit,'-p','127.0.0.1::5672','-p','127.0.0.1::15692','--mount',"type=bind,source=$plugins,target=/etc/rabbitmq/enabled_plugins,readonly",'--env','RABBITMQ_DEFAULT_PASS','rabbitmq:4-management') | Out-Null
    $owned.Add($rabbit)
    Invoke-TestDocker @('run','-d','--name',$blob,'-p','127.0.0.1::10000','mcr.microsoft.com/azure-storage/azurite:3.33.0','azurite-blob','--blobHost','0.0.0.0','--skipApiVersionCheck') | Out-Null
    $owned.Add($blob)
    Wait-Ready $postgres @('pg_isready','-U','aura_dev','-d','aura_dev')
    Wait-Ready $rabbit @('rabbitmq-diagnostics','-q','ping')
    foreach ($file in (& "$PSScriptRoot/Initialize-AuraDevDatabase.ps1" -PlanOnly)) {
        Get-Content -LiteralPath (Join-Path $repo $file) -Raw -Encoding utf8 | & docker exec -i $postgres psql -X -v ON_ERROR_STOP=1 -U aura_dev -d aura_dev *>> (Join-Path $outputRoot 'migrations.log')
        if ($LASTEXITCODE -ne 0) { throw "Migration failed: $file" }
        $evidence.migrations += @{path=$file; sha256=(Get-FileHash -LiteralPath (Join-Path $repo $file)).Hash}
    }
    $topology = Get-Content -LiteralPath (Join-Path $repo 'infra/rabbitmq/m1.topology.json') -Raw | ConvertFrom-Json
    foreach ($user in $topology.permissions.user | Sort-Object -Unique) {
        Invoke-TestDocker @('exec',$rabbit,'rabbitmqctl','add_user',$user,$env:RABBITMQ_DEFAULT_PASS) | Out-Null
    }
    & "$PSScriptRoot/Initialize-M1RabbitTopology.ps1" -Container $rabbit *> (Join-Path $outputRoot 'topology.log')
    Invoke-TestDocker @('exec',$rabbit,'rabbitmqctl','set_permissions','-p','/aura-s3','guest','.*','.*','.*') | Out-Null
    $env:AURA_TEST_POSTGRES = "Host=127.0.0.1;Port=$(Port $postgres '5432/tcp');Database=aura_dev;Username=aura_dev;Password=$env:POSTGRES_PASSWORD;Include Error Detail=true"
    $env:AURA_TEST_RABBITMQ_PORT = "$(Port $rabbit '5672/tcp')"
    $env:AURA_TEST_RABBITMQ_PASSWORD = $env:RABBITMQ_DEFAULT_PASS
    # Azurite's public emulator key is not a deployment credential.
    $env:AURA_TEST_AZURITE_CONNECTION = "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:$(Port $blob '10000/tcp')/devstoreaccount1;"
    Suite 'm1' 'tests/AURA.M1.RegressionTests'
    $metrics = Invoke-RestMethod -Uri "http://127.0.0.1:$(Port $rabbit '15692/tcp')/metrics/per-object" -TimeoutSec 20
    if ($metrics -notmatch 'rabbitmq_queue_messages\{[^\r\n]*queue="aura.s3.analysis.jobs"') { throw 'Queue-level Prometheus metrics are missing.' }
    $metrics | Set-Content -LiteralPath (Join-Path $outputRoot 'rabbitmq.prom') -Encoding utf8
    $env:AURA_SCRUM345_TEST_CONNECTION_STRING = $env:AURA_TEST_POSTGRES
    $env:ADR005_TEST_POSTGRES = $env:AURA_TEST_POSTGRES
    Suite 'analysis' 'src/services/AURA.Analysis.Api.Tests'
    Suite 'worker' 'tests/Aura.Worker.Analysis.Tests'
    Invoke-TestDocker @('exec',$postgres,'createdb','-U','aura_dev','-T','aura_dev','aura_billing_test') | Out-Null
    $env:AURA_TEST_POSTGRES = $env:AURA_TEST_POSTGRES.Replace('Database=aura_dev;','Database=aura_billing_test;')
    $env:AURA_SCRUM370_TEST_CONNECTION_STRING = $env:AURA_TEST_POSTGRES
    Suite 'billing' 'src/services/AURA.Billing.Api.Tests'
    foreach ($migration in $evidence.migrations) {
        Get-Content -LiteralPath (Join-Path $repo $migration.path) -Raw -Encoding utf8 | & docker exec -i $postgres psql -X -v ON_ERROR_STOP=1 -U aura_dev -d aura_dev *>> (Join-Path $outputRoot 'migrations-reapply.log')
        if ($LASTEXITCODE -ne 0) { throw "Migration reapply failed against populated fixture: $($migration.path)" }
    }
    $evidence.populatedMigrationReapply = 'PASS'
    & "$PSScriptRoot/../backup/Test-BackupRecovery.ps1" -Container $postgres -User aura_dev -Password $env:POSTGRES_PASSWORD -TemplateDatabase aura_dev *> (Join-Path $outputRoot 'backup-recovery.log')
    & "$PSScriptRoot/Test-RedisRuntime.ps1" *> (Join-Path $outputRoot 'redis.log')
    & dotnet run --project (Join-Path $repo 'tests/AURA.BackupRecovery.Checks') *> (Join-Path $outputRoot 'jwt-rotation.log')
    if ($LASTEXITCODE -ne 0) { throw 'JWT rotation checks failed.' }
    $evidence.status = 'PASS'
} catch {
    $evidence.status = 'FAIL'
    $evidence.error = $_.Exception.Message
    throw
} finally {
    foreach ($name in $owned) { & docker rm -f -v $name *> $null }
    foreach ($key in $envNames) { [Environment]::SetEnvironmentVariable($key,$saved[$key],'Process') }
    $evidence.completedAt = [DateTimeOffset]::UtcNow.ToString('O')
    $evidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $outputRoot 'summary.json') -Encoding utf8
}
