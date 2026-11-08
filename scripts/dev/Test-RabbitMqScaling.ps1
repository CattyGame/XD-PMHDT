[CmdletBinding()]
param(
    [string]$RabbitMqHost = '127.0.0.1', [int]$ManagementPort = 15372,
    [string]$Vhost = '/aura-s3', [string]$User = 's3-operator',
    [string]$Password = $env:RABBITMQ_OPERATOR_PASSWORD, [int]$ExpectedConsumers = 4
)
$ErrorActionPreference = 'Stop'
if (!$Password -or $ExpectedConsumers -lt 1) { throw 'Explicit credentials and a positive expected consumer count are required.' }
if ($RabbitMqHost -notin @('127.0.0.1','localhost')) { throw 'Management HTTP is restricted to loopback; use an authenticated TLS tunnel.' }
$auth = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("${User}:$Password"))
$queue = Invoke-RestMethod -Uri "http://${RabbitMqHost}:${ManagementPort}/api/queues/$([Uri]::EscapeDataString($Vhost))/aura.s3.analysis.jobs" -Headers @{Authorization="Basic $auth"}
if ($queue.type -ne 'quorum' -or $queue.consumers -ne $ExpectedConsumers) { throw 'Queue type or live consumer count does not match.' }
$channels = @($queue.consumer_details | ForEach-Object { $_.channel_details.connection_name } | Sort-Object -Unique)
if ($channels.Count -ne $ExpectedConsumers -or @($queue.consumer_details | Where-Object { $_.prefetch_count -lt 1 -or $_.ack_required -ne $true }).Count) { throw 'Each worker must use its own connection, bounded prefetch and acknowledgements.' }
Write-Output "PASS: $ExpectedConsumers live consumers on a quorum queue. Throughput/fairness requires Test-BatchInfrastructureStress.ps1; no synthetic jobs were published."
