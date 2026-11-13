#requires -Version 7.2
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$runId = [Guid]::NewGuid().ToString('N')
$project = 'rabotyagi-release-checks-' + $runId
$database = 'rabotyagi_deploy_checks_' + $runId
$runtime = Join-Path $repoRoot ".tools\release-checks\$runId"
$null = New-Item -ItemType Directory -Path $runtime -Force
$dockerCommand = Get-Command docker.exe -ErrorAction SilentlyContinue
$docker = if ($dockerCommand) { $dockerCommand.Source } else { Join-Path $env:LOCALAPPDATA 'Programs\DockerDesktop\resources\bin\docker.exe' }
$checks = 0
$savedEnvironment = @{}
$compose = @('compose', '--env-file', (Join-Path $runtime 'empty.env'), '--project-name', $project,
    '--project-directory', $repoRoot, '--file', (Join-Path $repoRoot 'compose.production.yaml'),
    '--file', (Join-Path $runtime 'override.yaml'))
[IO.File]::WriteAllText((Join-Path $runtime 'empty.env'), '')

function Check([bool]$Condition, [string]$Description) {
    if (-not $Condition) { throw "FAIL: $Description" }
    $script:checks++
    Write-Host "OK: $Description"
}

function Compose([string[]]$Arguments, [string]$LogName, [switch]$AllowFailure) {
    $output = & $docker @compose @Arguments 2>&1
    $composeExit = $LASTEXITCODE
    if ($LogName) { $output | Set-Content -LiteralPath (Join-Path $runtime $LogName) }
    if ($composeExit -ne 0 -and -not $AllowFailure) { throw "Isolated release Compose failed. See $runtime" }
    return $composeExit
}

function Sql([string]$Text) {
    $result = & $docker @compose exec -T postgres psql -X -U rabotyagi -d $database -t -A -v ON_ERROR_STOP=1 -c $Text
    if ($LASTEXITCODE -ne 0) { throw 'Isolated release SQL failed.' }
    return ($result -join "`n").Trim()
}

function Request([string]$Path, [string]$Method = 'GET', $Body = $null, [hashtable]$Headers = @{}) {
    $message = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($Method), "https://localhost:18443$Path")
    try {
        foreach ($name in $Headers.Keys) { $message.Headers.TryAddWithoutValidation($name, [string]$Headers[$name]) | Out-Null }
        if ($null -ne $Body) {
            $message.Content = [Net.Http.StringContent]::new(($Body | ConvertTo-Json -Compress), [Text.Encoding]::UTF8, 'application/json')
        }
        return $script:http.SendAsync($message).GetAwaiter().GetResult()
    } finally { $message.Dispose() }
}

function CheckStatus([string]$Path, [int]$Status, [string]$Description, [string]$Method = 'GET', $Body = $null, [hashtable]$Headers = @{}) {
    $response = Request $Path $Method $Body $Headers
    try { Check ([int]$response.StatusCode -eq $Status) $Description }
    finally { $response.Dispose() }
}

function ProxyLoginStatus([string]$ClientIp) {
    $output = & $docker exec $frontendId wget -S -O /dev/null --header='Host: localhost' --header='X-Forwarded-Proto: https' `
        "--header=X-Forwarded-For: $ClientIp" --header='Content-Type: application/json' `
        '--post-data={"email":"absent@checks.invalid","password":"Invalid-check-input"}' http://api:8080/api/account/login 2>&1
    $match = [regex]::Match(($output | Out-String), 'HTTP/1\.1\s+(\d{3})')
    if (-not $match.Success) { throw 'Trusted proxy check did not return an HTTP status.' }
    return [int]$match.Groups[1].Value
}

Add-Type -TypeDefinition @'
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
public static class ReleaseCheckTls {
    public static string LastError = "";
    public static HttpClientHandler CreateHandler(string caPath) {
        var root = X509CertificateLoader.LoadCertificateFromFile(caPath);
        var handler = new HttpClientHandler();
        handler.AllowAutoRedirect = false;
        handler.ServerCertificateCustomValidationCallback = (request, certificate, originalChain, errors) => {
            if (certificate == null || (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return false;
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(root);
            if (originalChain != null) {
                foreach (var element in originalChain.ChainElements)
                    chain.ChainPolicy.ExtraStore.Add(element.Certificate);
            }
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            var valid = chain.Build(certificate);
            LastError = string.Join(", ", System.Array.ConvertAll(chain.ChainStatus, item => item.Status.ToString()));
            return valid;
        };
        return handler;
    }
}
'@

$settings = @{
    SITE_DOMAIN = 'localhost'
    ACME_EMAIL = 'release-checks@example.invalid'
    RABOTYAGI_RELEASE = "checks-$runId"
    RABOTYAGI_DB_NAME = $database
    RABOTYAGI_DB_USER = 'rabotyagi'
    RABOTYAGI_DB_PASSWORD = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    RABOTYAGI_PROXY_SUBNET = '172.29.231.0/24'
    RABOTYAGI_PROXY_IP = '172.29.231.10'
    BOOTSTRAP_ADMIN_EMAIL = 'owner@release-checks.invalid'
    BOOTSTRAP_ADMIN_PASSWORD = 'Aa1!' + [Guid]::NewGuid().ToString('N')
}
# !override is needed to replace the public port list, keeping the test on loopback.
$override = @'
services:
  frontend:
    ports: !override
      - "127.0.0.1:18080:80"
      - "127.0.0.1:18443:443"
  api:
    environment:
      RequestSecurity__LoginPermitLimit: "2"
      RequestSecurity__LoginWindowSeconds: "600"
'@
[IO.File]::WriteAllText((Join-Path $runtime 'override.yaml'), $override)
$script:http = $null
try {
    foreach ($name in $settings.Keys) {
        $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $settings[$name], 'Process')
    }
    Check ((Compose -Arguments @('config', '--quiet')) -eq 0) 'production Compose accepts the isolated test configuration'
    Write-Host 'Building release images with the production frontend target...'
    $null = Compose -Arguments @('build', 'api', 'frontend') -LogName 'build.log'
    $null = Compose -Arguments @('up', '--detach', '--wait', 'postgres') -LogName 'postgres.log'
    Check ((Compose -Arguments @('run', '--rm', '--no-deps', 'api') -LogName 'before-migration.log' -AllowFailure) -ne 0) 'API refuses to start before the explicit migration step'
    Check ((Sql "SELECT count(*) FROM pg_tables WHERE schemaname='public'") -ceq '0') 'ordinary API startup does not create application tables'
    $null = Compose -Arguments @('run', '--rm', 'migrate') -LogName 'migrate.log'
    $migrationCount = Sql 'SELECT count(*) FROM "__EFMigrationsHistory"'
    Check ([int]$migrationCount -gt 0) 'one-shot migrator applies the schema and exits'
    $null = Compose -Arguments @('run', '--rm', 'migrate') -LogName 'migrate-repeat.log'
    Check ((Sql 'SELECT count(*) FROM "__EFMigrationsHistory"') -ceq $migrationCount) 'repeating the migration step preserves migration history'
    $null = Compose -Arguments @('run', '--rm', 'bootstrap') -LogName 'bootstrap.log'
    Check ((Sql 'SELECT count(*) FROM "AspNetUsers"') -ceq '1') 'one-shot bootstrap creates exactly one initial administrator'
    Check ((Compose -Arguments @('run', '--rm', 'bootstrap') -LogName 'bootstrap-repeat.log' -AllowFailure) -ne 0) 'repeating bootstrap reports the existing account conflict'
    Check ((Sql 'SELECT count(*) FROM "AspNetUsers"') -ceq '1') 'bootstrap conflict preserves the existing account'

    $null = Compose -Arguments @('up', '--detach', '--wait', '--wait-timeout', '180', 'api', 'frontend') -LogName 'start.log'
    $frontendId = (& $docker @compose ps -q frontend).Trim()
    $apiId = (& $docker @compose ps -q api).Trim()
    $frontend = (& $docker inspect $frontendId | ConvertFrom-Json)[0]
    $api = (& $docker inspect $apiId | ConvertFrom-Json)[0]
    Check ($frontend.Config.Labels.'com.docker.compose.project' -ceq $project -and $api.Config.Labels.'com.docker.compose.project' -ceq $project) 'application containers belong to this isolated check project'
    Check (@($api.HostConfig.PortBindings.PSObject.Properties).Count -eq 0) 'API has no public port'
    $postgresId = (& $docker @compose ps -q postgres).Trim()
    $postgres = (& $docker inspect $postgresId | ConvertFrom-Json)[0]
    Check (@($postgres.HostConfig.PortBindings.PSObject.Properties).Count -eq 0) 'PostgreSQL has no public port'
    Check ($frontend.State.Health.Status -ceq 'healthy') 'release readiness includes the running API and PostgreSQL'
    $caPath = Join-Path $runtime 'local-root.crt'
    & $docker cp "${frontendId}:/data/caddy/pki/authorities/local/root.crt" $caPath
    if ($LASTEXITCODE -ne 0) { throw 'Local test CA was not generated.' }
    $script:http = [Net.Http.HttpClient]::new([ReleaseCheckTls]::CreateHandler($caPath))
    $script:http.Timeout = [TimeSpan]::FromSeconds(15)
    CheckStatus '/' 200 'React frontend is served over certificate-validated HTTPS'
    $response = $script:http.GetAsync('http://localhost:18080/').GetAwaiter().GetResult()
    try { Check ([int]$response.StatusCode -eq 308 -and $response.Headers.Location.Scheme -ceq 'https') 'HTTP entry point redirects to HTTPS' }
    finally { $response.Dispose() }
    CheckStatus '/api/account' 401 'HTTPS reverse proxy reaches the protected API'
    CheckStatus '/api/demo/accounts' 404 'quick demo login is absent in Production' -Headers @{'X-Rabotyagi-Demo'='1'}
    $response = Request '/swagger/index.html'
    try { Check (-not $response.Content.ReadAsStringAsync().GetAwaiter().GetResult().Contains('SwaggerUIBundle')) 'Swagger UI is absent in the public release' }
    finally { $response.Dispose() }
    $response = Request '/api/account/login' 'POST' @{email=$settings.BOOTSTRAP_ADMIN_EMAIL;password=$settings.BOOTSTRAP_ADMIN_PASSWORD} @{Origin='https://localhost:18443'}
    try {
        Check ([int]$response.StatusCode -eq 200) 'initial administrator can sign in through the HTTPS proxy with the real browser origin'
        $cookie = [string]::Join(';', $response.Headers.GetValues('Set-Cookie'))
        Check ($cookie -match '(?i)secure' -and $cookie -match '(?i)httponly' -and $cookie -match '(?i)samesite=lax') 'release session uses Secure, HttpOnly and SameSite=Lax'
    } finally { $response.Dispose() }
    $response = Request '/api/account'
    try {
        $profile = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
        Check ($profile.roles -contains 'SuperAdmin') 'the authenticated session retains the SuperAdmin role'
    } finally { $response.Dispose() }
    $invalidLogin = @{email='absent@checks.invalid';password='Invalid-check-input'}
    CheckStatus '/api/account/login' 403 'foreign browser origin cannot submit a login' 'POST' $invalidLogin @{Origin='https://evil.invalid'}
    CheckStatus '/api/account/login' 400 'a failed login consumes the remaining peer budget' 'POST' $invalidLogin @{'X-Forwarded-For'='198.51.100.1'}
    CheckStatus '/api/account/login' 429 'spoofing forwarded client IP at the public proxy cannot bypass the login limit' 'POST' $invalidLogin @{'X-Forwarded-For'='198.51.100.2'}

    Check ((ProxyLoginStatus '198.51.100.10') -eq 400) 'trusted proxy can forward the first client IP'
    Check ((ProxyLoginStatus '198.51.100.10') -eq 400) 'first client has its own login budget'
    Check ((ProxyLoginStatus '198.51.100.10') -eq 429) 'first client is limited after its own budget is exhausted'
    Check ((ProxyLoginStatus '198.51.100.11') -eq 400) 'a second client does not share the first client login budget'
    $untrustedRequest = @'
exec 3<>/dev/tcp/api/8080
printf '%s\r\n' 'POST /api/account/login HTTP/1.1' 'Host: localhost:18443' 'Origin: https://localhost:18443' 'X-Forwarded-Proto: https' 'X-Forwarded-For: 198.51.100.12' 'Content-Length: 0' 'Connection: close' '' >&3
head -n 1 <&3
'@
    $untrustedStatus = & $docker exec $postgresId bash -c $untrustedRequest
    Check (($untrustedStatus | Out-String) -match 'HTTP/1\.1 403') 'an untrusted internal peer cannot assert the HTTPS scheme with forwarded headers'

    $null = Compose -Arguments @('restart', 'api') -LogName 'restart.log'
    $ready = $false
    for ($attempt = 0; $attempt -lt 45; $attempt++) {
        try {
            $response = Request '/api/account'
            try {
                if ([int]$response.StatusCode -eq 200) { $ready = $true; break }
            } finally { $response.Dispose() }
        } catch { }
        Start-Sleep -Seconds 1
    }
    Check $ready 'persisted keys preserve the HTTPS session after an API restart'
    Check ((Sql 'SELECT count(*) FROM "AspNetUsers"') -ceq '1') 'normal restarts preserve administrator records'
    Write-Host "PASS: $checks production deployment checks. Artifacts: $runtime"
} catch {
    Write-Host "TLS check diagnostics: $([ReleaseCheckTls]::LastError)"
    throw
} finally {
    if ($script:http) { $script:http.Dispose() }
    # Removes only this run's containers and networks. Persistent volumes are retained.
    $null = Compose -Arguments @('down') -LogName 'stop.log' -AllowFailure
    foreach ($name in $savedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
    }
}
