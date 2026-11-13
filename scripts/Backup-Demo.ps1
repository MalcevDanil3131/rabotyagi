#requires -Version 7.2
[CmdletBinding()]
param([string]$OutputDirectory, [securestring]$Password, [string]$PasswordFile)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $PSScriptRoot 'DatabaseBackup.psm1') -Force
$state = Get-Content -LiteralPath (Join-Path $repo '.tools/demo-runtime/state.json') -Raw | ConvertFrom-Json
if ($state.Database -cnotmatch '^rabotyagi_demo_[a-z0-9_]+$') { throw 'Unexpected demo database.' }
$arguments = @{} + $PSBoundParameters
$arguments.Database = $state.Database
$arguments.UploadsPath = Join-Path $repo "rabotyagi/.demo/$($state.Database)/uploads"
$statusPath = Join-Path $repo '.tools/backup-runtime/last-run.json'
try {
    $result = New-DatabaseBackup @arguments
} catch {
    if (Test-Path -LiteralPath (Split-Path -Parent $statusPath)) {
        @{ Status = 'Failed'; FinishedUtc = [DateTimeOffset]::UtcNow.ToString('o'); Error = 'Backup failed; no completed backup was published for this run.' } |
            ConvertTo-Json | Set-Content -LiteralPath $statusPath -Encoding utf8
    }
    throw
}
# Logging errors must not be reported as failure to create an already published archive.
@{ Status = 'Succeeded'; FinishedUtc = [DateTimeOffset]::UtcNow.ToString('o'); Backup = $result } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $statusPath -Encoding utf8
$result
