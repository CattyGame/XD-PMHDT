[CmdletBinding()]
param(
    [string]$BackupDir = 'backups',
    [string]$PostgresHost = '127.0.0.1', [int]$PostgresPort = 5432,
    [Parameter(Mandatory)][string]$Database, [string]$User = 'postgres',
    [string]$Password = $env:PGPASSWORD, [string]$Container = '',
    [ValidateSet('Local','AzureBlob')][string]$StorageProvider = 'Local',
    [string]$AssetRoot = '', [string]$EncryptionKey = $env:AURA_BACKUP_KEY,
    [ValidateRange(1,3650)][int]$RetentionDays = 30
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Backup.Common.ps1')
$backupRoot = [IO.Path]::GetFullPath($BackupDir)
[IO.Directory]::CreateDirectory($backupRoot) | Out-Null
$stage = Join-Path $backupRoot ('.stage-' + [Guid]::NewGuid().ToString('N'))
$package = Join-Path $stage 'payload'
$zip = Join-Path $stage 'payload.zip'
$savedPassword = $env:PGPASSWORD
$keys = Get-BackupKeys $EncryptionKey
$started = [DateTimeOffset]::UtcNow
$remoteDump = '/tmp/aura-backup-' + [Guid]::NewGuid().ToString('N') + '.dump'
try {
    [IO.Directory]::CreateDirectory($package) | Out-Null
    $dump = Join-Path $package 'database.dump'
    $env:PGPASSWORD = $Password
    if ($Container) {
        Invoke-BackupProcess 'docker' @('exec','--env','PGPASSWORD',$Container,'pg_dump','-U',$User,'-d',$Database,'-Fc','-f',$remoteDump)
        Invoke-BackupProcess 'docker' @('cp',"${Container}:$remoteDump",$dump)
    } else {
        if (!(Get-Command pg_dump -ErrorAction SilentlyContinue)) { throw 'pg_dump required; pass an explicit Container if using Docker.' }
        Invoke-BackupProcess 'pg_dump' @('-h',$PostgresHost,'-p',"$PostgresPort",'-U',$User,'-d',$Database,'-Fc','-f',$dump)
    }
    if ((Get-Item -LiteralPath $dump).Length -eq 0) { throw 'Empty database dump.' }
    $assets = Join-Path $package 'assets'
    [IO.Directory]::CreateDirectory($assets) | Out-Null
    if ($StorageProvider -eq 'AzureBlob') {
        $tool = Join-Path $PSScriptRoot '../../tools/AURA.PrivateStorage.Backup'
        Invoke-BackupProcess 'dotnet' @('run','--project',$tool,'--','export',$assets)
    } else {
        if (!$AssetRoot -or !(Test-Path -LiteralPath $AssetRoot -PathType Container)) { throw 'An existing private AssetRoot is required; no simulated inventory.' }
        $sourceRoot = [IO.Path]::GetFullPath($AssetRoot)
        if ($backupRoot.StartsWith($sourceRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or $backupRoot -eq $sourceRoot) { throw 'Backup directory cannot be inside AssetRoot.' }
        if ((Get-Item -LiteralPath $sourceRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'AssetRoot cannot be a symbolic link.' }
        $objects = @(foreach ($file in Get-ChildItem -LiteralPath $sourceRoot -Recurse -Force) {
            if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Asset links are not supported.' }
            if ($file.PSIsContainer) { continue }
            $relative = [IO.Path]::GetRelativePath($sourceRoot,$file.FullName)
            $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
            $target = Join-Path $assets $relative
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $target
            if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $hash) { throw 'Asset changed during backup.' }
            @{ path=$relative; length=$file.Length; sha256=$hash }
        })
        @{ provider='Local'; objects=$objects } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $package 'assets.manifest.json') -Encoding utf8
    }
    @{ formatVersion=1; database=$Database; storageProvider=$StorageProvider; startedAt=$started.ToString('O');
        completedAt=[DateTimeOffset]::UtcNow.ToString('O'); dumpSha256=(Get-FileHash -LiteralPath $dump -Algorithm SHA256).Hash;
        requiresProvisionedDatabaseRoles=$true } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $package 'manifest.json') -Encoding utf8
    [IO.Compression.ZipFile]::CreateFromDirectory($package,$zip)
    $output = Join-Path $backupRoot ('aura_backup_' + [DateTime]::UtcNow.ToString('yyyyMMdd_HHmmss') + '_' + [Guid]::NewGuid().ToString('N') + '.enc')
    Write-EncryptedBackup $zip $output $keys
    foreach ($old in Get-ChildItem -LiteralPath $backupRoot -Filter 'aura_backup_*.enc' -File | Where-Object LastWriteTimeUtc -LT ([DateTime]::UtcNow.AddDays(-$RetentionDays))) {
        Remove-Item -LiteralPath $old.FullName
        if (Test-Path -LiteralPath "$($old.FullName).hmac") { Remove-Item -LiteralPath "$($old.FullName).hmac" }
    }
    Write-Output "BACKUP_COMPLETE: $output"
} finally {
    if ($Container) { & docker exec $Container rm -f $remoteDump 2>$null | Out-Null }
    Remove-BackupStaging $stage $backupRoot
    [Security.Cryptography.CryptographicOperations]::ZeroMemory($keys.Enc)
    [Security.Cryptography.CryptographicOperations]::ZeroMemory($keys.Mac)
    $env:PGPASSWORD = $savedPassword
}
