[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EncryptedBackupFile,
    [Parameter(Mandatory)][string]$Database,
    [Parameter(Mandatory)][string]$ConfirmTarget,
    [Parameter(Mandatory)][string]$ConfirmStorageTarget,
    [string]$PostgresHost = '127.0.0.1', [int]$PostgresPort = 5432,
    [string]$User = 'postgres', [string]$Password = $env:PGPASSWORD, [string]$Container = '',
    [string]$AssetRoot = '', [string]$EncryptionKey = $env:AURA_BACKUP_KEY,
    [int]$MaxRtoSeconds = 1800, [int]$MaxRpoSeconds = 3600
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Backup.Common.ps1')
$target = if ($Container) { "docker:${Container}/$Database" } else { "tcp:${PostgresHost}:${PostgresPort}/$Database" }
if ($ConfirmTarget -cne $target -or $Database -in @('postgres','template0','template1')) { throw 'Restore target must be explicitly confirmed.' }
$archive = [IO.Path]::GetFullPath($EncryptedBackupFile)
$parent = [IO.Path]::GetDirectoryName($archive)
$stage = Join-Path $parent ('.stage-' + [Guid]::NewGuid().ToString('N'))
$keys = Get-BackupKeys $EncryptionKey
[IO.Directory]::CreateDirectory($stage) | Out-Null
$remoteDump = '/tmp/aura-restore-' + [Guid]::NewGuid().ToString('N') + '.dump'
$savedPassword = $env:PGPASSWORD
$timer = [Diagnostics.Stopwatch]::StartNew()
try {
    $zip = Join-Path $stage 'payload.zip'
    Read-EncryptedBackup $archive $zip $keys
    $package = Join-Path $stage 'payload'
    [IO.Compression.ZipFile]::ExtractToDirectory($zip,$package)
    $manifest = Get-Content -LiteralPath (Join-Path $package 'manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.formatVersion -ne 1) { throw 'Unsupported manifest version.' }
    $rpo = ([DateTimeOffset]::UtcNow - [DateTimeOffset]::Parse($manifest.startedAt)).TotalSeconds
    if ($rpo -lt -300 -or $rpo -gt $MaxRpoSeconds) { throw 'Backup age exceeds the requested RPO or has an invalid timestamp.' }
    $dump = Join-Path $package 'database.dump'
    if ((Get-FileHash -LiteralPath $dump -Algorithm SHA256).Hash -ne $manifest.dumpSha256) { throw 'Database checksum mismatch.' }
    $inventory = Get-Content -LiteralPath (Join-Path $package 'assets.manifest.json') -Raw | ConvertFrom-Json
    $assets = Join-Path $package 'assets'
    if ($inventory.provider -eq 'Local') {
        if (!$AssetRoot -or $ConfirmStorageTarget -cne [IO.Path]::GetFullPath($AssetRoot)) { throw 'Local restore storage target must be confirmed.' }
        $destinationRoot = [IO.Path]::GetFullPath($AssetRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        foreach ($item in $inventory.objects) {
            foreach ($base in @($assets,$destinationRoot)) {
                $resolved = [IO.Path]::GetFullPath((Join-Path $base $item.path))
                if (!$resolved.StartsWith([IO.Path]::GetFullPath($base).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid storage manifest path.' }
            }
            $source = Join-Path $assets $item.path
            if ((Get-Item -Force -LiteralPath $source).Length -ne $item.length -or (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $item.sha256) { throw 'Asset checksum mismatch.' }
            $ancestor = Join-Path $destinationRoot $item.path
            while ($ancestor) {
                if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -Force -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Restore target contains a symbolic link.' }
                $ancestor = [IO.Path]::GetDirectoryName($ancestor)
            }
        }
    } elseif ($inventory.provider -eq 'AzureBlob') {
        Invoke-BackupProcess 'dotnet' @('run','--project',(Join-Path $PSScriptRoot '../../tools/AURA.PrivateStorage.Backup'),'--','verify',$assets,$ConfirmStorageTarget)
    } else { throw 'Unknown storage provider.' }
    $env:PGPASSWORD = $Password
    $restoreArgs = @('-U',$User,'-d',$Database,'--clean','--if-exists','--exit-on-error','--single-transaction')
    if ($Container) {
        Invoke-BackupProcess 'docker' @('cp',$dump,"${Container}:$remoteDump")
        Invoke-BackupProcess 'docker' (@('exec','--env','PGPASSWORD',$Container,'pg_restore') + $restoreArgs + @($remoteDump))
    } else {
        if (!(Get-Command pg_restore -ErrorAction SilentlyContinue)) { throw 'pg_restore required; specify Container explicitly for Docker.' }
        Invoke-BackupProcess 'pg_restore' (@('-h',$PostgresHost,'-p',"$PostgresPort") + $restoreArgs + @($dump))
    }
    if ($inventory.provider -eq 'AzureBlob') {
        Invoke-BackupProcess 'dotnet' @('run','--project',(Join-Path $PSScriptRoot '../../tools/AURA.PrivateStorage.Backup'),'--','restore',$assets,$ConfirmStorageTarget)
    } else {
        foreach ($item in $inventory.objects) {
            $destination = Join-Path $destinationRoot $item.path
            # Refuse symlink ancestors rather than writing outside the confirmed root.
            $ancestor = [IO.Path]::GetDirectoryName($destination)
            while ($ancestor) {
                if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -Force -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Restore target contains a symbolic link.' }
                $ancestor = [IO.Path]::GetDirectoryName($ancestor)
            }
            if ((Test-Path -LiteralPath $destination) -and ((Get-Item -Force -LiteralPath $destination).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Restore target is a symbolic link.' }
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
            Copy-Item -LiteralPath (Join-Path $assets $item.path) -Destination $destination -Force
            if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $item.sha256) { throw 'Restored asset checksum mismatch.' }
        }
    }
    $timer.Stop()
    if ($timer.Elapsed.TotalSeconds -gt $MaxRtoSeconds) { throw 'Restore completed but exceeded the requested RTO.' }
    Write-Output "RESTORE_COMPLETE: verified DB and $(@($inventory.objects).Count) private assets; RTO=$($timer.Elapsed.TotalSeconds)s; backup age/RPO=$rpo s."
} finally {
    if ($Container) { & docker exec $Container rm -f $remoteDump 2>$null | Out-Null }
    Remove-BackupStaging $stage $parent
    [Security.Cryptography.CryptographicOperations]::ZeroMemory($keys.Enc)
    [Security.Cryptography.CryptographicOperations]::ZeroMemory($keys.Mac)
    $env:PGPASSWORD = $savedPassword
}
