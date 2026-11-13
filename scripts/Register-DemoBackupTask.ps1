#requires -Version 7.2
[CmdletBinding(SupportsShouldProcess)]
param([string]$At = '03:00', [string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) '.backups'),
    [string]$PasswordFile = (Join-Path (Split-Path -Parent $PSScriptRoot) '.tools/backup-runtime/passphrase.xml'))
$ErrorActionPreference = 'Stop'
if ($At -notmatch '^([01][0-9]|2[0-3]):[0-5][0-9]$') { throw 'At must be HH:mm in the Windows local timezone.' }
$repo = Split-Path -Parent $PSScriptRoot
$state = Get-Content -LiteralPath (Join-Path $repo '.tools/demo-runtime/state.json') -Raw | ConvertFrom-Json
if ($state.Database -cnotmatch '^rabotyagi_demo_[a-z0-9_]+$') { throw 'This task supports the isolated demo database only.' }
if (!(Test-Path -LiteralPath $PasswordFile -PathType Leaf)) { throw 'Initialize a protected backup passphrase file first.' }
$secret = Import-Clixml -LiteralPath $PasswordFile
if ($secret -isnot [securestring] -or $secret.Length -lt 20) { throw 'Invalid protected passphrase file.' }
$exe = [Environment]::ProcessPath
if ([IO.Path]::GetFileName($exe) -ine 'pwsh.exe') { throw 'Run this script using PowerShell 7.' }
$scriptPath = Join-Path $PSScriptRoot 'Backup-Demo.ps1'
$outputPath = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($OutputDirectory))
if ($outputPath -eq [IO.Path]::GetPathRoot($outputPath)) { throw 'Use a dedicated backup subdirectory, not a drive root.' }
$arguments = '-NoLogo -NoProfile -File "' + $scriptPath + '" -OutputDirectory "' + $outputPath + '" -PasswordFile "' + [IO.Path]::GetFullPath($PasswordFile) + '"'
$taskName = 'RabotyagiDemoBackup'
if ($PSCmdlet.ShouldProcess("$taskName at $At for $($state.Database)", 'Register daily backup for the current logged-in Windows user')) {
    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) { throw 'Task already exists; it will not be overwritten.' }
    $action = New-ScheduledTaskAction -Execute $exe -Argument $arguments -WorkingDirectory $repo
    $trigger = New-ScheduledTaskTrigger -Daily -At $At
    $principal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 1)
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings | Out-Null
    Write-Output "Registered $taskName at $At Windows local time. Docker must already be running; no containers are started by this job."
}
