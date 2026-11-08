[CmdletBinding()]
param([string]$Image = 'redis:7-alpine')
$ErrorActionPreference = 'Stop'
$name = 'aura-test-redis-' + [Guid]::NewGuid().ToString('N')
$password = 'm1,pass=' + [Guid]::NewGuid().ToString('N') + '$:!'
$config = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../infra/redis/redis.conf'))
function Run([string[]]$Arguments) {
    $result = & docker @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'Redis runtime Docker command failed.' }
    return $result
}
try {
    Run @('run','-d','--name',$name,'--mount',"type=bind,source=$config,target=/usr/local/etc/redis/redis.conf,readonly",'--env',"REDISCLI_AUTH=$password",'--health-cmd','redis-cli ping','--health-interval','1s','--health-retries','20',$Image,'redis-server','/usr/local/etc/redis/redis.conf','--requirepass',$password) | Out-Null
    $healthy = $false
    for ($i=0; $i -lt 30; $i++) {
        if ((Run @('inspect','--format','{{.State.Health.Status}}',$name)) -eq 'healthy') { $healthy = $true; break }
        Start-Sleep -Seconds 1
    }
    if (!$healthy) { throw 'Custom-password Redis did not become healthy.' }
    $denied = Run @('exec','--env','REDISCLI_AUTH=',$name,'redis-cli','GET','aura:test')
    if ($denied -notmatch 'NOAUTH') { throw 'Unauthenticated Redis access was not rejected.' }
    if ((Run @('exec',$name,'redis-cli','SET','analysis:batch:m1-test','persisted','EX','120')) -ne 'OK') { throw 'Authenticated write failed.' }
    Start-Sleep -Seconds 2
    Run @('restart',$name) | Out-Null
    $value = $null
    for ($i=0; $i -lt 20; $i++) {
        try { $value=Run @('exec',$name,'redis-cli','GET','analysis:batch:m1-test'); if ($value -eq 'persisted') { break } } catch { }
        Start-Sleep -Seconds 1
    }
    if ($value -ne 'persisted') { throw 'Redis cache value did not survive restart.' }
    Write-Output 'PASS: nondefault password with connection-string delimiters, authenticated healthcheck, NOAUTH rejection and actual AOF restart.'
} finally { & docker rm -f -v $name 2>$null | Out-Null }
