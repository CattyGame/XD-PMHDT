[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$NiFiApiUrl,
    [Parameter(Mandatory)][string]$Container,
    [Parameter(Mandatory)][Guid]$ConnectionId,
    [string]$Output = 'artifacts/m1-nifi-restart.json'
)
$ErrorActionPreference = 'Stop'
# Use an isolated aura-test-* NiFi, a nonempty queue, and stopped downstream processors.
& python (Join-Path $PSScriptRoot 'test_nifi_persistence.py') --url $NiFiApiUrl --container $Container --connection $ConnectionId --output $Output
if ($LASTEXITCODE -ne 0) { throw 'NiFi persistence drill failed or prerequisites are missing; no acceptance is granted.' }
