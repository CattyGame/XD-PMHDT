Set-StrictMode -Version Latest

function Get-BackupKeys([string]$EncryptionKey) {
    try { $master = [Convert]::FromBase64String($EncryptionKey) } catch { throw 'AURA_BACKUP_KEY must be Base64.' }
    if ($master.Length -ne 32) { throw 'AURA_BACKUP_KEY must contain 32 random bytes.' }
    try {
        return @{
            Enc = [Security.Cryptography.SHA256]::HashData([byte[]]($master + [Text.Encoding]::UTF8.GetBytes('AURA-backup-encryption-v1')))
            Mac = [Security.Cryptography.SHA256]::HashData([byte[]]($master + [Text.Encoding]::UTF8.GetBytes('AURA-backup-authentication-v1')))
        }
    } finally { [Security.Cryptography.CryptographicOperations]::ZeroMemory($master) }
}

function Invoke-BackupProcess([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command failed (exit $LASTEXITCODE)." }
}

function Remove-BackupStaging([string]$Path, [string]$Parent) {
    $resolved = [IO.Path]::GetFullPath($Path)
    $allowed = [IO.Path]::GetFullPath($Parent).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (!$resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase) -or ![IO.Path]::GetFileName($resolved).StartsWith('.stage-')) {
        throw 'Refusing staging deletion outside declared backup directory.'
    }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Force -Recurse }
}

function Write-EncryptedBackup([string]$Zip, [string]$Output, $Keys) {
    $aes = [Security.Cryptography.Aes]::Create()
    $aes.Key = $Keys.Enc
    $aes.GenerateIV()
    $source = [IO.File]::OpenRead($Zip)
    $target = [IO.File]::Open($Output, [IO.FileMode]::CreateNew)
    try {
        $target.Write([Text.Encoding]::ASCII.GetBytes('AURABK01'))
        $target.Write($aes.IV)
        $crypto = [Security.Cryptography.CryptoStream]::new($target, $aes.CreateEncryptor(), [Security.Cryptography.CryptoStreamMode]::Write, $true)
        try { $source.CopyTo($crypto); $crypto.FlushFinalBlock() } finally { $crypto.Dispose() }
    } finally { $source.Dispose(); $target.Dispose(); $aes.Dispose() }
    $hmac = [Security.Cryptography.HMACSHA256]::new($Keys.Mac)
    $input = [IO.File]::OpenRead($Output)
    try { [IO.File]::WriteAllText("$Output.hmac", [Convert]::ToHexString($hmac.ComputeHash($input))) }
    finally { $input.Dispose(); $hmac.Dispose() }
}

function Read-EncryptedBackup([string]$InputFile, [string]$Zip, $Keys) {
    if (!(Test-Path -LiteralPath "$InputFile.hmac" -PathType Leaf)) { throw 'Authenticated backup tag is required.' }
    $source = [IO.File]::OpenRead($InputFile)
    $hmac = [Security.Cryptography.HMACSHA256]::new($Keys.Mac)
    try {
        $actual = $hmac.ComputeHash($source)
        $expected = [Convert]::FromHexString([IO.File]::ReadAllText("$InputFile.hmac").Trim())
        if (![Security.Cryptography.CryptographicOperations]::FixedTimeEquals($actual, $expected)) { throw 'Backup authentication failed.' }
        $source.Position = 0
        $header = [byte[]]::new(8); $source.ReadExactly($header)
        if ([Text.Encoding]::ASCII.GetString($header) -ne 'AURABK01') { throw 'Unsupported backup format.' }
        $iv = [byte[]]::new(16); $source.ReadExactly($iv)
        $aes = [Security.Cryptography.Aes]::Create(); $aes.Key = $Keys.Enc; $aes.IV = $iv
        $crypto = [Security.Cryptography.CryptoStream]::new($source, $aes.CreateDecryptor(), [Security.Cryptography.CryptoStreamMode]::Read, $true)
        $output = [IO.File]::Create($Zip)
        try { $crypto.CopyTo($output) } finally { $output.Dispose(); $crypto.Dispose(); $aes.Dispose() }
    } finally { $source.Dispose(); $hmac.Dispose() }
}
