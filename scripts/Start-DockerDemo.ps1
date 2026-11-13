[CmdletBinding()]
param([switch]$NoBrowser)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$runtimeRoot = Join-Path $repoRoot '.tools\docker-demo'
$passwordPath = Join-Path $runtimeRoot 'password.xml'
$composePath = Join-Path $repoRoot 'compose.demo.yaml'
$demoUrl = 'http://127.0.0.1:3001'
$mutex = New-Object System.Threading.Mutex($false, 'Local\RabotyagiDockerDemo')
$ownsMutex = $false
$previousPassword = [Environment]::GetEnvironmentVariable('RABOTYAGI_DEMO_DB_PASSWORD', 'Process')

function Find-Docker {
    $command = Get-Command docker.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    foreach ($candidate in @(
        (Join-Path $env:LOCALAPPDATA 'Programs\DockerDesktop\resources\bin\docker.exe'),
        (Join-Path $env:ProgramFiles 'Docker\Docker\resources\bin\docker.exe'))) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    throw 'Docker Desktop was not found. Install it before starting the demo.'
}

function Test-Docker {
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $script:docker info --format '{{.ServerVersion}}' *> $null
        return $LASTEXITCODE -eq 0
    } finally { $ErrorActionPreference = $previousPreference }
}

function Invoke-DemoCompose([string[]]$Arguments) {
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $script:docker @script:composeArguments @Arguments
        $composeExit = $LASTEXITCODE
    } finally { $ErrorActionPreference = $previousPreference }
    if ($composeExit -ne 0) { throw 'Demo Compose failed. See the Docker output above.' }
}

try {
    try { $ownsMutex = $mutex.WaitOne(0) }
    catch [System.Threading.AbandonedMutexException] { $ownsMutex = $true }
    if (-not $ownsMutex) { throw 'Another demo launch is in progress. Please wait for it to finish.' }

    $script:docker = Find-Docker
    if (-not (Test-Docker)) {
        Write-Host 'Starting Docker Desktop. Existing containers may resume according to their restart policies.'
        & $docker desktop start --detach
        if ($LASTEXITCODE -ne 0) { throw 'Docker Desktop could not start.' }
        for ($attempt = 0; $attempt -lt 60 -and -not (Test-Docker); $attempt++) { Start-Sleep -Seconds 1 }
        if (-not (Test-Docker)) { throw 'Docker is not ready. Start Docker Desktop and retry.' }
    }

    $null = New-Item -ItemType Directory -Path $runtimeRoot -Force
    $emptyEnvPath = Join-Path $runtimeRoot 'empty.env'
    [IO.File]::WriteAllText($emptyEnvPath, '')
    # An explicit file/project prevents the application's .env and Compose project from being used.
    $script:composeArguments = @('compose', '--env-file', $emptyEnvPath, '--project-name', 'rabotyagi-demo',
        '--project-directory', $repoRoot, '--file', $composePath)

    if (-not (Test-Path -LiteralPath $passwordPath -PathType Leaf)) {
        $existingVolumes = @(& $docker volume ls --filter name=^rabotyagi-demo_postgres_data$ --format '{{.Name}}')
        if ($LASTEXITCODE -ne 0) { throw 'Could not inspect demo storage.' }
        if ($existingVolumes -contains 'rabotyagi-demo_postgres_data') { throw 'The demo database already exists but its local password file is missing. Restore .tools/docker-demo/password.xml before starting.' }
        $random = [Security.Cryptography.RandomNumberGenerator]::Create()
        try {
            $bytes = New-Object byte[] 32
            $random.GetBytes($bytes)
            $secret = ConvertTo-SecureString ([Convert]::ToBase64String($bytes)) -AsPlainText -Force
            $temporary = "$passwordPath.tmp"
            $secret | Export-Clixml -LiteralPath $temporary
            Move-Item -LiteralPath $temporary -Destination $passwordPath
        } finally { $random.Dispose() }
    }
    $secret = Import-Clixml -LiteralPath $passwordPath
    $env:RABOTYAGI_DEMO_DB_PASSWORD = [Net.NetworkCredential]::new('', $secret).Password
    if ([string]::IsNullOrWhiteSpace($env:RABOTYAGI_DEMO_DB_PASSWORD)) { throw 'Cannot unlock the demo password for this Windows user.' }

    Invoke-DemoCompose -Arguments @('config', '--quiet')
    Write-Host 'Building the separate Docker demo. The first build may take several minutes...'
    $buildLog = Join-Path $runtimeRoot 'build.log'
    # Docker writes ordinary progress to stderr. Windows PowerShell 5.1 must judge
    # native commands by their exit code instead of treating that progress as a failure.
    $ErrorActionPreference = 'Continue'
    try {
        & $docker @composeArguments build *> $buildLog
        $buildExit = $LASTEXITCODE
    } finally { $ErrorActionPreference = 'Stop' }
    if ($buildExit -ne 0) {
        Get-Content -LiteralPath $buildLog -Tail 20 | Write-Host
        throw "Demo build failed. Full log: $buildLog"
    }

    # Recreate both namespace-sharing services together so a frontend update cannot leave
    # Kestrel in the old network namespace. Database and file volumes remain persistent.
    Write-Host 'Starting the current demo images...'
    Invoke-DemoCompose -Arguments @('up', '--detach', '--force-recreate', '--wait', '--wait-timeout', '180', 'frontend', 'api')
    $accounts = Invoke-RestMethod -Uri "$demoUrl/api/demo/accounts" -Headers @{ 'X-Rabotyagi-Demo' = '1' } -TimeoutSec 10
    if ($accounts.Count -ne 6) { throw 'Demo readiness check did not return six demo accounts.' }
    Write-Host "Ready: $demoUrl" -ForegroundColor Green
    Write-Host 'Docker Desktop: rabotyagi-demo > frontend > port 3001.'
    Write-Host 'Demo data is saved in the separate rabotyagi-demo volumes.'
    if (-not $NoBrowser) { Start-Process $demoUrl }
} catch {
    Write-Host ("Demo launch failed: " + $_.Exception.Message) -ForegroundColor Red
    exit 1
} finally {
    [Environment]::SetEnvironmentVariable('RABOTYAGI_DEMO_DB_PASSWORD', $previousPassword, 'Process')
    if ($ownsMutex) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
