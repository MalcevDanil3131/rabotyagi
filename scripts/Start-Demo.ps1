[CmdletBinding()]
param([switch]$NoBrowser)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$runtimeRoot = Join-Path $repoRoot '.tools\demo-runtime'
$statePath = Join-Path $runtimeRoot 'state.json'
$demoUrl = 'http://127.0.0.1:5173'
$apiUrl = 'http://127.0.0.1:5189'
$mutex = $null
$ownsMutex = $false

function Find-Executable([string]$Name, [string[]]$Candidates) {
    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    foreach ($candidate in $Candidates) { if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate } }
    throw "$Name was not found. Install it before starting the demo."
}

function Test-Docker {
    $oldPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $script:docker info --format '{{.ServerVersion}}' *> $null; return $LASTEXITCODE -eq 0 }
    finally { $ErrorActionPreference = $oldPreference }
}

function Get-DemoAccounts([string]$BaseUrl) {
    try {
        $response = Invoke-RestMethod -Uri "$BaseUrl/api/demo/accounts" -Headers @{ 'X-Rabotyagi-Demo' = '1' } -TimeoutSec 2
        foreach ($account in $response) { Write-Output $account }
    } catch { return @() }
}

function Test-Port([int]$Port) {
    return @([Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners() |
        Where-Object { $_.Port -eq $Port }).Count -gt 0
}

function Test-RecordedProcess([string]$Property, [string]$Executable, [string]$CommandFragment) {
    $record = $script:demoState.PSObject.Properties[$Property]
    if (-not $record -or -not $record.Value) { return $false }
    $process = Get-CimInstance Win32_Process -Filter "ProcessId=$([int]$record.Value)" -ErrorAction SilentlyContinue
    return $process -and $process.Name -eq $Executable -and $process.CommandLine -like "*$CommandFragment*"
}

function Save-State {
    $temporary = "$statePath.tmp"
    $script:demoState | ConvertTo-Json | Set-Content -LiteralPath $temporary -Encoding UTF8
    Move-Item -LiteralPath $temporary -Destination $statePath -Force
}

function Wait-Demo([string]$BaseUrl, [int]$ProcessId) {
    for ($attempt = 0; $attempt -lt 45; $attempt++) {
        if (-not (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) {
            throw "Demo process exited. See logs in $runtimeRoot"
        }
        $accounts = @(Get-DemoAccounts $BaseUrl)
        if ($accounts.Count -eq 6 -and @($accounts | Where-Object { $_.key -eq 'superadmin' }).Count -eq 1) { return }
        Start-Sleep -Milliseconds 500
    }
    throw "Demo did not become ready at $BaseUrl. See logs in $runtimeRoot"
}

try {
    if (-not (Test-Path -LiteralPath $statePath)) { throw 'Local demo state is missing. This launcher only resumes the separately prepared demo; it never creates or adopts an application database.' }
    $script:demoState = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    if ($demoState.Database -notmatch '^rabotyagi_demo_[a-z0-9_]+$' -or
        $demoState.Container -notmatch '^rabotyagi-demo-[a-z0-9-]+$' -or
        $demoState.Volume -cne ($demoState.Database + '_data') -or
        $demoState.User -cne 'demo_admin' -or $demoState.Port -ne 55439) {
        throw 'Unexpected demo configuration. Refusing to access any database.'
    }
    $mutex = New-Object System.Threading.Mutex($false, ('Local\RabotyagiDemo_' + $demoState.Database))
    try { $ownsMutex = $mutex.WaitOne(0) } catch [System.Threading.AbandonedMutexException] { $ownsMutex = $true }
    if (-not $ownsMutex) { throw 'Another demo launch is in progress. Please wait for its window.' }

    $script:docker = Find-Executable 'docker.exe' @(
        (Join-Path $env:LOCALAPPDATA 'Programs\DockerDesktop\resources\bin\docker.exe'),
        (Join-Path $env:ProgramFiles 'Docker\Docker\resources\bin\docker.exe'))
    $dotnet = Find-Executable 'dotnet.exe' @((Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'))
    $node = Find-Executable 'node.exe' @((Join-Path $env:ProgramFiles 'nodejs\node.exe'))
    $vitePath = Join-Path $repoRoot 'frontend\node_modules\vite\bin\vite.js'
    if (-not (Test-Path -LiteralPath $vitePath)) { throw 'Frontend dependencies are missing. Restore frontend dependencies before launching the demo.' }
    $securePassword = Import-Clixml -LiteralPath (Join-Path $runtimeRoot 'password.xml')
    $demoPassword = [Net.NetworkCredential]::new('', $securePassword).Password
    if ([string]::IsNullOrWhiteSpace($demoPassword)) { throw 'Cannot unlock the local demo database password for this Windows user.' }

    Write-Host 'Starting the isolated Rabotyagi demo...'
    if (-not (Test-Docker)) {
        Write-Host 'Starting Docker Desktop. Existing containers may resume according to their own restart policies.'
        & $docker desktop start --detach
        if ($LASTEXITCODE -ne 0) { throw 'Docker Desktop could not start.' }
        for ($attempt = 0; $attempt -lt 60 -and -not (Test-Docker); $attempt++) { Start-Sleep -Seconds 1 }
        if (-not (Test-Docker)) { throw 'Docker is not ready. Start Docker Desktop and retry.' }
    }

    # Inspect only the named demo container, without reading or displaying its credentials.
    $raw = & $docker inspect $demoState.Container --format '{{json .Config.Labels}}'
    if ($LASTEXITCODE -ne 0) { throw 'The prepared demo container was not found. No containers were created or changed.' }
    $labels = ($raw -join "`n") | ConvertFrom-Json
    $raw = & $docker inspect $demoState.Container --format '{{json .HostConfig.PortBindings}}'
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect demo port bindings.' }
    $ports = ($raw -join "`n") | ConvertFrom-Json
    $raw = & $docker inspect $demoState.Container --format '{{json .Mounts}}'
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect demo storage.' }
    $mounts = ($raw -join "`n") | ConvertFrom-Json
    $mounts = @($mounts)
    $binding = @($ports.'5432/tcp')
    if ($labels.'rabotyagi.purpose' -cne 'local-demo' -or $binding.Count -ne 1 -or
        $binding[0].HostIp -cne '127.0.0.1' -or $binding[0].HostPort -ne '55439' -or
        $mounts.Count -ne 1 -or $mounts[0].Name -cne $demoState.Volume -or
        $mounts[0].Destination -cne '/var/lib/postgresql/data') {
        throw 'Container isolation checks failed. Refusing to start the demo.'
    }
    & $docker start $demoState.Container | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Demo PostgreSQL could not start.' }
    $ready = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        & $docker exec $demoState.Container pg_isready -U demo_admin -d $demoState.Database *> $null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Seconds 1
    }
    if (-not $ready) { throw 'Demo PostgreSQL is not ready.' }

    if (Test-Port 5189) {
        if (-not (Test-RecordedProcess 'ApiPid' 'dotnet.exe' 'rabotyagi.dll')) { throw 'Port 5189 is occupied by an unrecognized process. It will not be stopped.' }
        Wait-Demo $apiUrl ([int]$demoState.ApiPid)
        Write-Host 'Demo API is already running.'
    } else {
        Write-Host 'Building demo API (database is not accessed during build)...'
        & $dotnet build (Join-Path $repoRoot 'rabotyagi\rabotyagi.csproj') --no-restore --nologo *> (Join-Path $runtimeRoot 'build.log')
        if ($LASTEXITCODE -ne 0) { throw "Build failed. See $runtimeRoot\build.log" }
        $env:ASPNETCORE_ENVIRONMENT = 'Demo'
        $env:DOTNET_ENVIRONMENT = 'Demo'
        $env:Demo__Enabled = 'true'
        $env:Demo__Port = '5189'
        $env:BootstrapAdmin__Enabled = 'false'
        $env:ASPNETCORE_URLS = $apiUrl
        $env:Logging__EventLog__LogLevel__Default = 'None'
        $env:ConnectionStrings__DemoConnection = "Host=127.0.0.1;Port=55439;Database=$($demoState.Database);Username=demo_admin;Password=$demoPassword"
        try {
            $api = Start-Process -FilePath $dotnet -ArgumentList 'bin/Debug/net10.0/rabotyagi.dll' -WorkingDirectory (Join-Path $repoRoot 'rabotyagi') -WindowStyle Hidden -PassThru `
                -RedirectStandardOutput (Join-Path $runtimeRoot 'api.log') -RedirectStandardError (Join-Path $runtimeRoot 'api-error.log')
        } finally { Remove-Item Env:ConnectionStrings__DemoConnection -ErrorAction SilentlyContinue }
        $demoState | Add-Member -MemberType NoteProperty -Name ApiPid -Value $api.Id -Force
        Save-State
        Wait-Demo $apiUrl $api.Id
    }

    if (Test-Port 5173) {
        if (-not (Test-RecordedProcess 'FrontendPid' 'node.exe' 'vite') -or
            -not (Test-RecordedProcess 'FrontendPid' 'node.exe' '--mode demo')) {
            throw 'Port 5173 is occupied by an unrecognized process. It will not be stopped.'
        }
        Wait-Demo $demoUrl ([int]$demoState.FrontendPid)
        Write-Host 'Demo frontend is already running.'
    } else {
        $env:API_PROXY_TARGET = $apiUrl
        # A nonempty '/' overrides .env.local even in Windows PowerShell 5.1.
        # apiBase trims the trailing slash, so all requests stay on this demo origin.
        $env:VITE_API_BASE_URL = '/'
        $ui = Start-Process -FilePath $node -ArgumentList 'node_modules/vite/bin/vite.js --host 127.0.0.1 --mode demo' -WorkingDirectory (Join-Path $repoRoot 'frontend') -WindowStyle Hidden -PassThru `
            -RedirectStandardOutput (Join-Path $runtimeRoot 'frontend.log') -RedirectStandardError (Join-Path $runtimeRoot 'frontend-error.log')
        $demoState | Add-Member -MemberType NoteProperty -Name FrontendPid -Value $ui.Id -Force
        Save-State
        Wait-Demo $demoUrl $ui.Id
    }
    Write-Host "Ready: $demoUrl" -ForegroundColor Green
    Write-Host "Database: $($demoState.Database) on 127.0.0.1:55439. All demo data is preserved."
    if (-not $NoBrowser) { Start-Process $demoUrl }
} catch {
    Write-Host ("Demo launch failed: " + $_.Exception.Message) -ForegroundColor Red
    Write-Host "Logs: $runtimeRoot"
    exit 1
} finally {
    if ($ownsMutex) { $mutex.ReleaseMutex() }
    if ($mutex) { $mutex.Dispose() }
}
