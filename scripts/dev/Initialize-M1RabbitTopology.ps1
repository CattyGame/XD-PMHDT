[CmdletBinding()]
param([Parameter(Mandatory)][string]$Container)
$ErrorActionPreference = 'Stop'
# Users and their credentials are provisioned separately; this import only changes named AURA topology/permissions.
$source = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../infra/rabbitmq/m1.topology.json'))
$target = '/tmp/aura-m1-topology-' + [Guid]::NewGuid().ToString('N') + '.json'
try {
    & docker cp $source "${Container}:$target"
    if ($LASTEXITCODE -ne 0) { throw 'Topology copy failed.' }
    & docker exec $Container rabbitmqctl import_definitions $target
    if ($LASTEXITCODE -ne 0) { throw 'Topology import failed; provision the named users first.' }
    $ready = $false
    for ($i=0; $i -lt 30; $i++) {
        $bindings = & docker exec $Container rabbitmqctl list_bindings -p /aura-s3 source_name destination_name routing_key --quiet
        if ($LASTEXITCODE -eq 0 -and ($bindings -match 'aura.s3.events\s+aura.s3.notification.events\s+billing.payment.success')) { $ready = $true; break }
        Start-Sleep -Seconds 1
    }
    if (!$ready) { throw 'Payment notification binding was not provisioned.' }
} finally { & docker exec $Container rm -f $target 2>$null | Out-Null }
