[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GatewayUrl,
    [Parameter(Mandatory)][Guid]$PatientId,
    [Parameter(Mandatory)][string]$ImageDirectory,
    [Parameter(Mandatory)][string[]]$Containers,
    [string]$Output = 'artifacts/m1-live-batch.json',
    [int]$BatchCount = 100
)
$ErrorActionPreference = 'Stop'
& python (Join-Path $PSScriptRoot 'test_real_batch.py') --url $GatewayUrl --patient $PatientId --images $ImageDirectory --output $Output --count $BatchCount --containers @Containers
if ($LASTEXITCODE -ne 0) { throw 'Real image pipeline acceptance failed; inspect the evidence file.' }
