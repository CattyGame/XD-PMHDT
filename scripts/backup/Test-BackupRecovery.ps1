[CmdletBinding()]
param([Parameter(Mandatory)][string]$Container, [string]$User = 'postgres', [string]$Password = $env:PGPASSWORD,
      [string]$TemplateDatabase, [string]$Output,
      [ValidateRange(1,86400)][int]$MaxRtoSeconds = 720)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Backup.Common.ps1')
$root = Join-Path ([IO.Path]::GetTempPath()) ('.stage-aura-backup-test-' + [Guid]::NewGuid().ToString('N'))
$database = 'aura_backup_test_' + [Guid]::NewGuid().ToString('N')
$oldKey = $env:AURA_BACKUP_KEY
$oldPassword = $env:PGPASSWORD
$freshDatabase = $database + '_fresh'
$evidence = [ordered]@{ status='FAIL'; startedAt=[DateTimeOffset]::UtcNow.ToString('O');
    scope='Isolated PostgreSQL snapshot and local private binary assets; no shared volume deletion';
    sourceCommit=(& git -C (Join-Path $PSScriptRoot '../..') rev-parse HEAD); targetRtoSeconds=$MaxRtoSeconds }
function Assert-Fails([scriptblock]$Operation) {
    $failed = $false
    try { & $Operation } catch { $failed = $true }
    if (!$failed) { throw 'Expected operation to fail.' }
}
function Get-RecoveryDigest([string]$TargetDatabase = $database) {
    $tables = & docker exec --env PGPASSWORD $Container psql -X -U $User -d $TargetDatabase -At -v ON_ERROR_STOP=1 -c "SELECT quote_ident(schemaname)||'.'||quote_ident(tablename) FROM pg_tables WHERE schemaname NOT IN ('pg_catalog','information_schema') ORDER BY schemaname,tablename"
    if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate recovery tables.' }
    foreach ($table in $tables) {
        $digest = & docker exec --env PGPASSWORD $Container psql -X -U $User -d $TargetDatabase -At -v ON_ERROR_STOP=1 -c "SELECT count(*)::text || '|' || md5(coalesce(string_agg(row_to_json(t)::text,chr(10) ORDER BY row_to_json(t)::text),'')) FROM $table t"
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
    $protectedAckAt = [DateTimeOffset]::UtcNow
    $before = @(Get-RecoveryDigest)
    & "$PSScriptRoot/Backup-AuraDatabase.ps1" -Database $database -Container $Container -User $User -Password $Password -AssetRoot $assets -BackupDir (Join-Path $root 'backups')
    $archive = (Get-ChildItem -LiteralPath (Join-Path $root 'backups') -Filter '*.enc').FullName
    $backupCompletedAt = [DateTimeOffset]::UtcNow
    $arguments = @{EncryptedBackupFile=$archive; Database=$database; Container=$Container; User=$User; Password=$Password; AssetRoot=$restore; ConfirmStorageTarget=$restore; ConfirmTarget="docker:${Container}/$database"; MaxRtoSeconds=$MaxRtoSeconds}
    Invoke-BackupProcess docker @('exec','--env','PGPASSWORD',$Container,'psql','-U',$User,'-d',$database,'-v','ON_ERROR_STOP=1','-c','INSERT INTO recovery_probe VALUES(2,''acknowledged after snapshot; intentionally unprotected'');')
    $unprotectedAckAt = [DateTimeOffset]::UtcNow
    $drTimer = [Diagnostics.Stopwatch]::StartNew()
    Invoke-BackupProcess docker @('exec','--env','PGPASSWORD',$Container,'createdb','-U',$User,$freshDatabase)
    $fresh = $arguments.Clone(); $fresh.Database=$freshDatabase; $fresh.ConfirmTarget="docker:${Container}/$freshDatabase"
    & "$PSScriptRoot/Restore-AuraDatabase.ps1" @fresh
    if (Compare-Object $before @(Get-RecoveryDigest $freshDatabase)) { throw 'Fresh database differs from the protected snapshot.' }
    foreach ($file in Get-ChildItem -LiteralPath $assets -File) {
        if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath (Join-Path $restore $file.Name)).Hash) { throw 'Fresh private asset roundtrip failed.' }
    }
    $drTimer.Stop()
    if ($drTimer.Elapsed.TotalSeconds -ge $MaxRtoSeconds) { throw 'Fresh DR including target creation and verification exceeded RTO.' }
    $evidence.freshRestoreSeconds=$drTimer.Elapsed.TotalSeconds
    $drTimer.Restart()
    Invoke-BackupProcess docker @('exec','--env','PGPASSWORD',$Container,'psql','-U',$User,'-d',$database,'-c','DELETE FROM recovery_probe;')
    & "$PSScriptRoot/Restore-AuraDatabase.ps1" @arguments
    $rows = & docker exec --env PGPASSWORD $Container psql -U $User -d $database -tAc 'SELECT payload FROM recovery_probe WHERE id=1'
    if ($LASTEXITCODE -ne 0 -or $rows.Trim() -ne 'before backup') { throw 'Database roundtrip failed.' }
    if (Compare-Object $before @(Get-RecoveryDigest)) { throw 'Restored database table contents differ from the backup source.' }
    foreach ($file in Get-ChildItem -LiteralPath $assets -File) {
        if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath (Join-Path $restore $file.Name)).Hash) { throw 'Private asset roundtrip failed.' }
    }
    $drTimer.Stop()
    if ($drTimer.Elapsed.TotalSeconds -ge $MaxRtoSeconds) { throw 'Populated DR including verification exceeded RTO.' }
    $evidence.populatedRestoreSeconds=$drTimer.Elapsed.TotalSeconds
    $lost = & docker exec --env PGPASSWORD $Container psql -X -U $User -d $database -At -v ON_ERROR_STOP=1 -c 'SELECT count(*) FROM recovery_probe WHERE id=2'
    if ($LASTEXITCODE -ne 0 -or $lost.Trim() -ne '0') { throw 'Post-snapshot RPO probe was not observed correctly.' }
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
    $evidence.status='PASS'
    $evidence.tableFingerprints=$before
    $evidence.assetCount=3
    $evidence.protectedSnapshotRowsLost=0
    $evidence.unprotectedAcknowledgedRowsLost=1
    $evidence.protectedAckAt=$protectedAckAt.ToString('O')
    $evidence.backupCompletedAt=$backupCompletedAt.ToString('O')
    $evidence.lastUnprotectedAckAt=$unprotectedAckAt.ToString('O')
    $evidence.acknowledgementExposureSeconds=($unprotectedAckAt-$protectedAckAt).TotalSeconds
    $evidence.rpoInterpretation='Protected snapshot restored exactly; post-backup writes are not protected without WAL/PITR. Backup age is not zero-loss RPO.'
    $evidence.negativeControls=@('wrong_key','tamper','wrong_db_target','missing_database')
    Write-Output "PASS: PostgreSQL roundtrip verified across $($before.Count) tables + 3 private binary assets; old backup key retained after rotation; wrong key, tamper, target and pg_restore failure rejected."
} finally {
    $evidence.completedAt=[DateTimeOffset]::UtcNow.ToString('O')
    if ($Output) {
        $fullOutput=[IO.Path]::GetFullPath($Output)
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($fullOutput)) | Out-Null
        $evidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $fullOutput -Encoding utf8
    }
    & docker exec --env PGPASSWORD $Container dropdb -U $User --if-exists --force $database 2>$null | Out-Null
    & docker exec --env PGPASSWORD $Container dropdb -U $User --if-exists --force $freshDatabase 2>$null | Out-Null
    $env:AURA_BACKUP_KEY = $oldKey
    $env:PGPASSWORD = $oldPassword
    Remove-BackupStaging $root ([IO.Path]::GetTempPath())
}
