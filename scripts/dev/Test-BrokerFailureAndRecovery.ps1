[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if (!$env:AURA_TEST_POSTGRES -or !$env:AURA_TEST_RABBITMQ_PASSWORD) { throw 'Isolated migrated PostgreSQL and RabbitMQ credentials are required. This gate never purges shared queues.' }
& dotnet test (Join-Path $PSScriptRoot '../../tests/AURA.M1.RegressionTests') --filter 'FullyQualifiedName~BillingTests|FullyQualifiedName~RabbitTransportTests|FullyQualifiedName~NotificationTests'
if ($LASTEXITCODE -ne 0) { throw 'Payment persistence, retry or broker checks failed.' }
Write-Output 'Regression gate only: PostgreSQL rollback/replay, dispatcher restart/retry and AMQP confirm/permissions. Full clinical pipeline outage acceptance remains a separate live drill.'
