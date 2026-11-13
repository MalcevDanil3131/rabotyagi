#requires -Version 7.2
[CmdletBinding()]
param([switch]$Rebuild)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$baseUrl = 'http://127.0.0.1:3001'
$checks = 0
$command = Get-Command docker.exe -ErrorAction SilentlyContinue
$docker = if ($command) { $command.Source } else { Join-Path $env:LOCALAPPDATA 'Programs\DockerDesktop\resources\bin\docker.exe' }

function Check([bool]$Condition, [string]$Description) {
    if (-not $Condition) { throw "FAIL: $Description" }
    $script:checks++
    Write-Host "OK: $Description"
}

function Get-DemoContainer([string]$Service) {
    $ids = @(& $docker ps -aq --filter label=com.docker.compose.project=rabotyagi-demo --filter "label=com.docker.compose.service=$Service")
    if ($LASTEXITCODE -ne 0 -or $ids.Count -ne 1) { throw "Expected one Docker demo $Service container." }
    $container = (& $docker inspect $ids[0] | ConvertFrom-Json)[0]
    Check ($container.Config.Labels.'rabotyagi.purpose' -ceq 'docker-demo') "$Service has the isolated demo label"
    return $container
}

function Get-DatabaseFingerprint([string]$ContainerId) {
    $sql = 'SELECT ''Orders:'' || count(*) || '':'' || md5(coalesce(string_agg(row_to_json(o)::text, '''' ORDER BY "Id"),'''')) FROM "Orders" o UNION ALL SELECT ''History:'' || count(*) || '':'' || md5(coalesce(string_agg(row_to_json(h)::text, '''' ORDER BY "Id"),'''')) FROM "OrderHistoryEvents" h'
    $result = & $docker exec $ContainerId psql -X -U demo_admin -d rabotyagi_demo_docker -A -t -v ON_ERROR_STOP=1 -c $sql
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read isolated demo fingerprint.' }
    return ($result -join "`n")
}

function Get-Response([string]$Path, $Session = $null, [hashtable]$Headers = @{}) {
    return Invoke-WebRequest -Uri "$baseUrl$Path" -WebSession $Session -Headers $Headers -SkipHttpErrorCheck -TimeoutSec 10
}

# Inspect labels before running SQL, stopping, or rebuilding anything.
$postgres = Get-DemoContainer 'postgres'
$frontend = Get-DemoContainer 'frontend'
$api = Get-DemoContainer 'api'
Check ($postgres.Config.Env -contains 'POSTGRES_DB=rabotyagi_demo_docker') 'database is the dedicated Docker demo'
Check (@($postgres.HostConfig.PortBindings.PSObject.Properties).Count -eq 0) 'PostgreSQL has no host port'
Check (@($api.HostConfig.PortBindings.PSObject.Properties).Count -eq 0) 'API has no host port'
$port = @($frontend.HostConfig.PortBindings.'80/tcp')
Check ($port.Count -eq 1 -and $port[0].HostIp -ceq '127.0.0.1' -and $port[0].HostPort -ceq '3001') 'only frontend is published on loopback port 3001'
Check ($api.HostConfig.NetworkMode -ceq "container:$($frontend.Id)") 'API shares the frontend loopback namespace'
Check ($api.Config.User -ne 'root' -and $api.Config.User -ne '0') 'API runs as a non-root user'
Check (@($postgres.Mounts | Where-Object Name -eq 'rabotyagi-demo_postgres_data').Count -eq 1) 'PostgreSQL uses its dedicated volume'
Check (@($api.Mounts | Where-Object Name -eq 'rabotyagi-demo_demo_files').Count -eq 1) 'photos and cookie keys use a persistent dedicated volume'
Check ($frontend.State.Health.Status -ceq 'healthy') 'readiness includes the running API'
$before = Get-DatabaseFingerprint $postgres.Id

$homePage = Get-Response '/'
Check ($homePage.StatusCode -eq 200 -and $homePage.Content.Contains('<div id="root">')) 'built React entry point is served'
$asset = [regex]::Match($homePage.Content, 'src="(/assets/[^" ]+\.js)"').Groups[1].Value
Check ($asset -and (Get-Response $asset).StatusCode -eq 200) 'compiled JavaScript is served'
Check ((Get-Response '/api/account').StatusCode -eq 401) 'anonymous account request is denied through Nginx'
Check ((Get-Response '/api/demo/accounts').StatusCode -eq 404) 'demo header is still required'
$demoHeaders = @{ 'X-Rabotyagi-Demo' = '1'; Origin = $baseUrl }
$accounts = (Get-Response '/api/demo/accounts' -Headers $demoHeaders).Content | ConvertFrom-Json
Check ($accounts.Count -eq 6) 'six demo accounts are available through Nginx'
Check ((Get-Response '/api/demo/accounts' -Headers @{ 'X-Rabotyagi-Demo' = '1'; Origin = 'https://evil.example' }).StatusCode -eq 404) 'foreign origin cannot use quick login'
Check ((Get-Response '/api/demo/accounts' -Headers @{ 'X-Rabotyagi-Demo' = '1'; Host = 'evil.example'; 'X-Forwarded-For' = '127.0.0.1' }).StatusCode -eq 404) 'foreign host cannot bypass the demo guard with forwarded headers'

$client = $null
foreach ($account in $accounts) {
    $session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
    $login = Invoke-WebRequest -Uri "$baseUrl/api/demo/login/$($account.key)" -Method Post -Headers $demoHeaders -WebSession $session -SkipHttpErrorCheck -TimeoutSec 10
    Check ($login.StatusCode -eq 200) "quick login works for $($account.key)"
    $profile = (Get-Response '/api/account' $session).Content | ConvertFrom-Json
    Check ($profile.id -ceq $account.id -and $profile.roles -contains $account.role) "server session has the expected $($account.key) identity and role"
    $page = (Get-Response '/api/order/page?page=1&pageSize=20&scope=all' $session).Content | ConvertFrom-Json
    Check ($null -ne $page.items -and $page.totalCount -gt 0) "$($account.key) can read the actual order API"
    if ($account.key -ceq 'client') { $client = $session }
}

$orders = (Get-Response '/api/order/page?page=1&pageSize=20&scope=all' $client).Content | ConvertFrom-Json
Check (@($orders.items | Where-Object priceStatus -eq 'proposed').Count -gt 0) 'demo includes a price waiting for client approval'
Check (@($orders.items | Where-Object priceStatus -eq 'accepted').Count -gt 0) 'demo includes agreed prices'
$reported = $orders.items | Where-Object { $_.photos.Count -gt 0 } | Select-Object -First 1
Check ($null -ne $reported) 'demo includes a real photo report'
$historyPath = "/api/order/$($reported.id)/history"
$history = (Get-Response $historyPath $client).Content
Check (($history | ConvertFrom-Json).Count -gt 0) 'order history is returned from PostgreSQL'
$photoPath = $reported.photos[0].url
Check ($photoPath.StartsWith('/api/order/')) 'photo URL stays on the same origin'
$photo = Get-Response $photoPath $client
Check ($photo.StatusCode -eq 200 -and $photo.Headers.'Content-Type'[0] -in @('image/png', 'image/jpeg', 'image/webp')) 'authorized report photo is served through Nginx'
Check ((Get-Response $photoPath).StatusCode -eq 401) 'anonymous report photo is denied'

try {
    & $docker stop --time 10 $api.Id | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not stop the isolated demo API.' }
    Check ((Get-Response '/healthz').StatusCode -eq 200) 'frontend remains reachable while API is stopped'
    & $docker exec $frontend.Id sh -c $frontend.Config.Healthcheck.Test[1] *> $null
    Check ($LASTEXITCODE -ne 0) 'readiness probe detects an unavailable API'
} finally {
    & $docker start $api.Id | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not resume the isolated demo API.' }
}

if ($Rebuild) {
    & powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot 'scripts\Start-DockerDemo.ps1') -NoBrowser
    if ($LASTEXITCODE -ne 0) { throw 'One-button demo update failed.' }
    $updatedApi = Get-DemoContainer 'api'
    $updatedFrontend = Get-DemoContainer 'frontend'
    Check ($updatedApi.Id -cne $api.Id -and $updatedFrontend.Id -cne $frontend.Id) 'one-button update recreates both application containers'
    Check ((Get-DemoContainer 'postgres').Id -ceq $postgres.Id) 'one-button update preserves the running database container'
}

$ready = $false
for ($attempt = 0; $attempt -lt 45; $attempt++) {
    try {
        if ((Get-Response '/api/account' $client).StatusCode -eq 200) { $ready = $true; break }
    } catch { }
    Start-Sleep -Seconds 1
}
Check $ready 'existing client cookie remains valid after restart or update'
Check ((Get-DatabaseFingerprint $postgres.Id) -ceq $before) 'order and history fingerprints are identical after restart or update'
Check ((Get-Response $historyPath $client).Content -ceq $history) 'history survives restart or update'
Check ((Get-Response $photoPath $client).StatusCode -eq 200) 'photo remains accessible after restart or update'
Write-Output "PASS: $checks Docker demo checks. Existing application databases were not accessed."
