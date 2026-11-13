#requires -Version 7.2
[CmdletBinding()]
param([Parameter(Mandatory)][string]$BackupPath, [Parameter(Mandatory)][string]$Database,
    [securestring]$Password, [string]$PasswordFile)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'DatabaseBackup.psm1') -Force
Restore-DatabaseBackup @PSBoundParameters
