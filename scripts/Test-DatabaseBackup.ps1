#requires -Version 7.2
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$state = Get-Content -LiteralPath (Join-Path $repo '.tools/demo-runtime/state.json') -Raw | ConvertFrom-Json
if ($state.Database -cnotmatch '^rabotyagi_demo_[a-z0-9_]+$' -or $state.User -cne 'demo_admin' -or $state.Port -ne 55439) { throw 'Unexpected demo configuration.' }
$password = [Net.NetworkCredential]::new('', (Import-Clixml -LiteralPath (Join-Path $repo '.tools/demo-runtime/password.xml'))).Password
$env:WORKFLOW_CHECKS_CONNECTION = "Host=127.0.0.1;Port=55439;Database=postgres;Username=demo_admin;Password=$password"
$env:BACKUP_CHECKS_PWSH = [Environment]::ProcessPath
try {
    & dotnet build (Join-Path $repo 'tests/WorkflowChecks') --no-restore -clp:ErrorsOnly
    if ($LASTEXITCODE -ne 0) { throw 'Could not build backup checks.' }
    & dotnet (Join-Path $repo 'tests/WorkflowChecks/bin/Debug/net10.0/WorkflowChecks.dll') --backup-only
    if ($LASTEXITCODE -ne 0) { throw 'Backup/restore verification failed.' }
} finally {
    Remove-Item Env:WORKFLOW_CHECKS_CONNECTION -ErrorAction SilentlyContinue
    Remove-Item Env:BACKUP_CHECKS_PWSH -ErrorAction SilentlyContinue
    $password = $null
}
