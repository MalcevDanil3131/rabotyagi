@echo off
setlocal
set "RABOTYAGI_BACKUP_ROOT=%~dp0"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "$runtime=Get-Command pwsh.exe -ErrorAction SilentlyContinue; $exe=if($runtime){$runtime.Source}else{$null}; if(-not $exe){foreach($candidate in @((Join-Path $env:ProgramFiles 'PowerShell\7\pwsh.exe'),(Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\native\powershell\pwsh.exe'))){if(Test-Path -LiteralPath $candidate){$exe=$candidate;break}}}; if(-not $exe){Write-Error 'PowerShell 7 is required';exit 1}; & $exe -NoLogo -NoProfile -File (Join-Path $env:RABOTYAGI_BACKUP_ROOT 'scripts\Test-DatabaseBackup.ps1'); exit $LASTEXITCODE"
if errorlevel 1 echo Backup checks failed. See the message above.
pause
