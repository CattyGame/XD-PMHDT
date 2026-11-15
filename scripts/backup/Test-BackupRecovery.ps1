[CmdletBinding()]
param([Parameter(Mandatory)][string]$Container, [string]$User = 'postgres', [string]$Password = $env:PGPASSWORD,
      [string]$TemplateDatabase)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Backup.Common.ps1')
$root = Join-Path ([IO.Path]::GetTempPath()) ('.stage-aura-backup-test-' + [Guid]::NewGuid().ToString('N'))
$database = 'aura_backup_test_' + [Guid]::NewGuid().ToString('N')
$oldKey = $env:AURA_BACKUP_KEY
$oldPassword = $env:PGPASSWORD
function Assert-Fails([scriptblock]$Operation) {
    $failed = $false
    try { & $Operation } catch { $failed = $true }
    if (!$failed) { throw 'Expected operation to fail.' }
}
function Get-RecoveryDigest {
    $tables = & docker exec --env PGPASSWORD $Container psql -X -U $User -d $database -At -v ON_ERROR_STOP=1 -c "SELECT quote_ident(schemaname)||'.'||quote_ident(tablename) FROM pg_tables WHERE schemaname NOT IN ('pg_catalog','information_schema') ORDER BY schemaname,tablename"
    if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate recovery tables.' }
    foreach ($table in $tables) {
        $digest = & docker exec --env PGPASSWORD $Container psql -X -U $User -d $database -At -v ON_ERROR_STOP=1 -c "SELECT count(*)::text || '|' || md5(coalesce(string_agg(row_to_json(t)::text,chr(10) ORDER BY row_to_json(t)::text),'')) FROM $table t"
        if ($LASTEXITCODE -ne 0) { throw "Cannot fingerprint table: $table" }
        "$table|$digest"
    }
}
try {
    $env:PGPASSWORD = $Password
    $env:AURA_BACKUP_KEY = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    $assets = Join-Path $root 'source'
    $restore = Join-Path $root 'restored'
    [IO.Directory]::CreateDirectory($assets) | Out-Null
    foreach ($name in @('original.bin','mask.bin','report.pdf')) {
        [IO.File]::WriteAllBytes((Join-Path $assets $name),[Security.Cryptography.RandomNumberGenerator]::GetBytes(4096))
    }
    $createArgs = @('exec','--env','PGPASSWORD',$Container,'createdb','-U',$User)
    if ($TemplateDatabase) {
        if ($TemplateDatabase -notmatch '^[a-z][a-z0-9_]*$') { throw 'Invalid template database name.' }
        $createArgs += @('-T',$TemplateDatabase)
    }
    Invoke-BackupProcess docker ($createArgs + @($database))
    Invoke-BackupProcess docker @('exec','--env','PGPASSWORD',$Container,'psql','-U',$User,'-d',$database,'-v','ON_ERROR_STOP=1','-c','CREATE TABLE recovery_probe(id int PRIMARY KEY, payload text); INSERT INTO recovery_probe VALUES(1,''before backup'');')
    $before = @(Get-RecoveryDigest)
    & "$PSScriptRoot/Backup-AuraDatabase.ps1" -Database $database -Container $Container -User $User -Password $Password -AssetRoot $assets -BackupDir (Join-Path $root 'backups')
    $archive = (Get-ChildItem -LiteralPath (Join-Path $root 'backups') -Filter '*.enc').FullName
    $arguments = @{EncryptedBackupFile=$archive; Database=$database; Container=$Container; User=$User; Password=$Password; AssetRoot=$restore; ConfirmStorageTarget=$restore; ConfirmTarget="docker:${Container}/$database"}
    Invoke-BackupProcess docker @('exec','--env','PGPASSWORD',$Container,'psql','-U',$User,'-d',$database,'-c','DELETE FROM recovery_probe;')
    & "$PSScriptRoot/Restore-AuraDatabase.ps1" @arguments
    $rows = & docker exec --env PGPASSWORD $Container psql -U $User -d $database -tAc 'SELECT payload FROM recovery_probe WHERE id=1'
    if ($LASTEXITCODE -ne 0 -or $rows.Trim() -ne 'before backup') { throw 'Database roundtrip failed.' }
    if (Compare-Object $before @(Get-RecoveryDigest)) { throw 'Restored database table contents differ from the backup source.' }
    foreach ($file in Get-ChildItem -LiteralPath $assets -File) {
        if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath (Join-Path $restore $file.Name)).Hash) { throw 'Private asset roundtrip failed.' }
    }
    $wrong = $arguments.Clone(); $wrong.ConfirmTarget = 'wrong-target'
    Assert-Fails { & "$PSScriptRoot/Restore-AuraDatabase.ps1" @wrong }
    $backupKey = $env:AURA_BACKUP_KEY
    $env:AURA_BACKUP_KEY = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    Assert-Fails { & "$PSScriptRoot/Restore-AuraDatabase.ps1" @arguments }
    $env:AURA_BACKUP_KEY = $backupKey
    & "$PSScriptRoot/Restore-AuraDatabase.ps1" @arguments
    $original = [IO.File]::ReadAllBytes($archive)
    $tampered = $original.Clone(); $tampered[32] = $tampered[32] -bxor 1
    [IO.File]::WriteAllBytes($archive,$tampered)
    Assert-Fails { & "$PSScriptRoot/Restore-AuraDatabase.ps1" @arguments }
    [IO.File]::WriteAllBytes($archive,$original)
    $missing = $arguments.Clone(); $missing.Database = $database + '_missing'; $missing.ConfirmTarget = "docker:${Container}/$($missing.Database)"
    Assert-Fails { & "$PSScriptRoot/Restore-AuraDatabase.ps1" @missing }
    Write-Output "PASS: PostgreSQL roundtrip verified across $($before.Count) tables + 3 private binary assets; old backup key retained after rotation; wrong key, tamper, target and pg_restore failure rejected."
} finally {
    & docker exec --env PGPASSWORD $Container dropdb -U $User --if-exists --force $database 2>$null | Out-Null
    $env:AURA_BACKUP_KEY = $oldKey
    $env:PGPASSWORD = $oldPassword
    Remove-BackupStaging $root ([IO.Path]::GetTempPath())
}
