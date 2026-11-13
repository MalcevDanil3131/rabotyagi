#requires -Version 7.2
param([Parameter(Mandatory)][string]$SourceDatabase, [Parameter(Mandatory)][string]$RestoreDatabase,
    [Parameter(Mandatory)][string]$FailureDatabase, [Parameter(Mandatory)][string]$SourceUploads,
    [Parameter(Mandatory)][string]$OutputDirectory, [Parameter(Mandatory)][string]$ResultPath)
$ErrorActionPreference = 'Stop'
if ($SourceDatabase -cnotmatch '^rabotyagi_demo_backupcheck_[a-f0-9]{32}$') { throw 'These mutation/failure tests require a fresh backupcheck database.' }
$repo = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $repo 'scripts/DatabaseBackup.psm1') -Force
$module = Get-Module DatabaseBackup
$checkCount = 0
function Check([bool]$Success, [string]$Message) {
    if (!$Success) { throw "FAIL: $Message" }
    $script:checkCount++
    Write-Output "OK: $Message"
}
function MustFail([scriptblock]$Action, [string]$Message, [string]$Expected = '') {
    $failed = $false
    try { $null = & $Action } catch {
        if ($Expected -and $_.Exception.Message -notmatch $Expected) { throw }
        $failed = $true
    }
    Check $failed $Message
}
$secret = ConvertTo-SecureString ([Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))) -AsPlainText -Force
$wrong = ConvertTo-SecureString ([Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))) -AsPlainText -Force
MustFail { New-DatabaseBackup -Database rabotyagi_db -Password $secret } 'primary source name is refused before connection' 'Database guard'
MustFail { Restore-DatabaseBackup -Database rabotyagi_db -BackupPath missing -Password $secret } 'primary restore target is refused before connection' 'Database guard'
MustFail { Restore-DatabaseBackup -Database $SourceDatabase -BackupPath missing -Password $secret } 'source database cannot be a restore target' 'Database guard'
$secretDir = & $module { param($path) New-PrivateBackupDirectory $path } (Split-Path -Parent $ResultPath)
$secretFile = Join-Path $secretDir 'passphrase.xml'
$secret | Export-Clixml -LiteralPath $secretFile
MustFail { New-DatabaseBackup -Database $SourceDatabase -Password $secret -OutputDirectory $repo } 'arbitrary existing output directories are not modified' 'Output directory'
MustFail { New-DatabaseBackup -Database $SourceDatabase -Password $secret -OutputDirectory $OutputDirectory } 'photos cannot be silently omitted' 'references photos'
$backup = New-DatabaseBackup -Database $SourceDatabase -PasswordFile $secretFile -UploadsPath $SourceUploads -OutputDirectory $OutputDirectory
Check ((Test-Path -LiteralPath $backup.BackupPath) -and $backup.PhotoCount -gt 0) 'encrypted backup and photo bundle created'
Check ((Get-FileHash -LiteralPath $backup.BackupPath -Algorithm SHA256).Hash -ceq $backup.Sha256) 'encrypted backup checksum matches'
Check (!(Get-ChildItem -LiteralPath $OutputDirectory -File | Where-Object { $_.Extension -in @('.dump', '.zip') })) 'no plaintext archive is published'
MustFail { Restore-DatabaseBackup -Database $FailureDatabase -BackupPath $backup.BackupPath -Password $wrong } 'wrong passphrase fails before database creation' 'gpg.exe'
$corrupt = Join-Path $secretDir 'corrupt.zip.gpg'
$bytes = [IO.File]::ReadAllBytes($backup.BackupPath)
$bytes[[int]($bytes.Length / 2)] = $bytes[[int]($bytes.Length / 2)] -bxor 1
[IO.File]::WriteAllBytes($corrupt, $bytes)
MustFail { Restore-DatabaseBackup -Database $FailureDatabase -BackupPath $corrupt -Password $secret } 'tampered ciphertext fails before database creation' 'gpg.exe'
$restored = Restore-DatabaseBackup -Database $RestoreDatabase -BackupPath $backup.BackupPath -PasswordFile $secretFile
Check ($restored.Database -ceq $RestoreDatabase -and $restored.PhotosIncluded) 'restored only to a new database with photos'
MustFail { Restore-DatabaseBackup -Database $RestoreDatabase -BackupPath $backup.BackupPath -Password $secret } 'existing target is never overwritten' 'already exists'
$context = & $module { Get-BackupContext }
$failedTargetCount = & $module { param($ctx,$dbName) Invoke-BackupSql $ctx 'postgres' "SELECT count(*) FROM pg_database WHERE datname='$dbName'" } $context $FailureDatabase
Check ($failedTargetCount -ceq '0') 'failed decryption never creates the target database'
$null = & $module { param($ctx,$dbName)
    Invoke-BackupSql $ctx $dbName ('CREATE TABLE "BackupRestoreFailure" ("Id" integer PRIMARY KEY CHECK (current_database() = ''' + $dbName + ''')); INSERT INTO "BackupRestoreFailure" VALUES (1);')
} $context $SourceDatabase
try {
    $fault = New-DatabaseBackup -Database $SourceDatabase -Password $secret -UploadsPath $SourceUploads -OutputDirectory (Join-Path $secretDir 'fault-archives')
    MustFail { Restore-DatabaseBackup -Database $FailureDatabase -BackupPath $fault.BackupPath -Password $secret } 'SQL restore failure is reported' 'pg_restore'
    $tables = & $module { param($ctx,$dbName) Invoke-BackupSql $ctx $dbName "SELECT count(*) FROM pg_tables WHERE schemaname='public'" } $context $FailureDatabase
    Check ($tables -ceq '0') 'failed SQL restore rolls back all application tables in its new target'
} finally {
    $null = & $module { param($ctx,$dbName) Invoke-BackupSql $ctx $dbName 'DROP TABLE "BackupRestoreFailure"' } $context $SourceDatabase
}
$photos = @(Get-ChildItem -LiteralPath $SourceUploads -File)
foreach ($photo in $photos) {
    Check ((Get-FileHash -LiteralPath $photo.FullName).Hash -ceq (Get-FileHash -LiteralPath (Join-Path $restored.UploadsPath $photo.Name)).Hash) 'restored photo bytes match source'
}
$stageRoot = Join-Path $repo '.tools/backup-runtime/staging'
Check (@(Get-ChildItem -LiteralPath $stageRoot -Force).Count -eq 0) 'plaintext staging files are cleaned after successes and failures'
@{ Backup = $backup; Restore = $restored; Checks = $checkCount; PassphraseFile = $secretFile } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ResultPath -Encoding utf8
