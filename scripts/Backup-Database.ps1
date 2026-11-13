#requires -Version 7.2
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Database, [string]$OutputDirectory, [string]$UploadsPath,
    [switch]$DatabaseOnly, [securestring]$Password, [string]$PasswordFile)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'DatabaseBackup.psm1') -Force
New-DatabaseBackup @PSBoundParameters
