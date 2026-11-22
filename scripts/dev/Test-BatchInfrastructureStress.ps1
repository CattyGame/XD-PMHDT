[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GatewayUrl,
    [Guid]$PatientId,
    [Guid]$ClinicContextId,
    [string]$Manifest,
    [int]$ExpectedSucceeded = -1,
    [int]$ExpectedFailed = -1,
    [string]$PostgresContainer,
    [switch]$RequireFinancialReconciliation,
    [Parameter(Mandatory)][string]$ImageDirectory,
    [Parameter(Mandatory)][string[]]$Containers,
    [string]$Output = 'artifacts/m1-live-batch.json',
    [int]$BatchCount = 100,
    [ValidateSet(1,4)][int]$ExpectedWorkers = 1,
    [switch]$RequireRealResults
)
$ErrorActionPreference = 'Stop'
$batchArgs = @('--url',$GatewayUrl,'--images',$ImageDirectory,'--output',$Output,'--count',"$BatchCount",'--containers') + $Containers
if ($PatientId -ne [Guid]::Empty) { $batchArgs += @('--patient',"$PatientId") }
if ($ClinicContextId -ne [Guid]::Empty) { $batchArgs += @('--clinic-context-id',"$ClinicContextId") }
if ($Manifest) { $batchArgs += @('--manifest',$Manifest) }
if ($ExpectedSucceeded -ge 0 -or $ExpectedFailed -ge 0) {
    if ($ExpectedSucceeded -lt 0 -or $ExpectedFailed -lt 0) { throw 'Both expected counts must be nonnegative.' }
    $batchArgs += @('--expected-succeeded',"$ExpectedSucceeded",'--expected-failed',"$ExpectedFailed")
}
if ($PostgresContainer) { $batchArgs += @('--postgres-container',$PostgresContainer) }
if ($RequireFinancialReconciliation) { $batchArgs += '--require-financial-reconciliation' }
$batchArgs += @('--expected-workers',"$ExpectedWorkers")
if ($RequireRealResults) { $batchArgs += '--require-real-results' }
& python (Join-Path $PSScriptRoot 'test_real_batch.py') @batchArgs
if ($LASTEXITCODE -ne 0) { throw 'Real image pipeline acceptance failed; inspect the evidence file.' }
