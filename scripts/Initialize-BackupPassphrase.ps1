#requires -Version 7.2
[CmdletBinding()]
param([string]$Path = (Join-Path (Split-Path -Parent $PSScriptRoot) '.tools/backup-runtime/passphrase.xml'))
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $Path) { throw 'Passphrase file already exists; it will not be overwritten.' }
Import-Module (Join-Path $PSScriptRoot 'DatabaseBackup.psm1') -Force
$secret = Read-Host 'Backup passphrase (20+ characters; save separately in your password manager)' -AsSecureString
if ($secret.Length -lt 20 -or $secret.Length -gt 512) { throw 'Use 20 to 512 characters.' }
$confirmation = Read-Host 'Repeat backup passphrase' -AsSecureString
if ([Net.NetworkCredential]::new('', $secret).Password -cne [Net.NetworkCredential]::new('', $confirmation).Password) { throw 'Passphrases do not match.' }
$module = Get-Module DatabaseBackup
$parent = & $module { param($parentPath) New-PrivateBackupDirectory $parentPath } (Split-Path -Parent ([IO.Path]::GetFullPath($Path)))
$temporary = Join-Path $parent ([guid]::NewGuid().ToString('N') + '.xml.partial')
try {
    $secret | Export-Clixml -LiteralPath $temporary
    [IO.File]::Move($temporary, [IO.Path]::GetFullPath($Path))
} finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
Write-Output 'Saved a Windows DPAPI-protected passphrase file for this Windows account. Keep the original passphrase separately for recovery on another computer.'
