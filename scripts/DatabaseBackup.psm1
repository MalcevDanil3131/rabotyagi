#requires -Version 7.2
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:Repo = Split-Path -Parent $PSScriptRoot

function Find-BackupTool([string]$Name, [string[]]$Candidates) {
    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    foreach ($candidate in $Candidates) { if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate } }
    throw "Required tool not found: $Name"
}

# Arguments are passed separately; no shell interpolation, passwords or binary PowerShell pipes.
function Invoke-BackupProcess {
    param([string]$Executable, [string[]]$Arguments, [string]$OutputFile, [string]$InputFile, [securestring]$Secret)
    $info = [Diagnostics.ProcessStartInfo]::new($Executable)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.RedirectStandardInput = $true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    $output = $null; $inputStream = $null
    try {
        if (!$process.Start()) { throw 'Could not start backup tool.' }
        $errorTask = $process.StandardError.ReadToEndAsync()
        if ($OutputFile) {
            $output = [IO.File]::Open($OutputFile, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
            $outputTask = $process.StandardOutput.BaseStream.CopyToAsync($output)
        } else { $outputTask = $process.StandardOutput.ReadToEndAsync() }
        if ($Secret) {
            $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secret)
            try {
                $text = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
                if ($text.Contains("`r") -or $text.Contains("`n")) { throw 'Backup passphrase must be a single line.' }
                $process.StandardInput.NewLine = "`n"
                $process.StandardInput.WriteLine($text)
                $process.StandardInput.Flush()
                $text = $null
            } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
        } elseif ($InputFile) {
            $inputStream = [IO.File]::OpenRead($InputFile)
            $copy = $inputStream.CopyToAsync($process.StandardInput.BaseStream)
            if (!$copy.Wait(300000)) { $process.Kill($true); throw 'Restore input timed out.' }
            $copy.GetAwaiter().GetResult()
        }
        $process.StandardInput.Close()
        if (!$process.WaitForExit(300000)) { $process.Kill($true); throw 'Backup tool timed out after 5 minutes.' }
        $result = $outputTask.GetAwaiter().GetResult()
        $diagnostics = $errorTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) {
            # Do not echo tool stderr: database errors can contain row data.
            $operation = [IO.Path]::GetFileName($Executable)
            foreach ($tool in @('pg_dump', 'pg_restore', 'psql', 'createdb')) { if ($Arguments -contains $tool) { $operation = $tool; break } }
            throw "Backup tool failed: $operation, exit $($process.ExitCode). No existing database was overwritten."
        }
        if (!$OutputFile) { return $result.Trim() }
    } finally {
        if ($inputStream) { $inputStream.Dispose() }; if ($output) { $output.Dispose() }
        if ($process.Id -and !$process.HasExited) { $process.Kill($true) }
        $process.Dispose()
    }
}

function Assert-BackupDatabase([string]$Database, [switch]$Restore) {
    $pattern = if ($Restore) { '^rabotyagi_demo_restore_[a-f0-9]{32}$' } else { '^rabotyagi_demo_[a-z0-9_]+$' }
    if ($Database.Length -gt 63 -or $Database -cnotmatch $pattern) {
        throw 'Database guard refused the name. Only isolated demo sources and fresh rabotyagi_demo_restore_<GUID> targets are allowed.'
    }
}

function Get-BackupContext {
    $state = Get-Content -LiteralPath (Join-Path $script:Repo '.tools/demo-runtime/state.json') -Raw | ConvertFrom-Json
    if ($state.Database -cnotmatch '^rabotyagi_demo_[a-z0-9_]+$' -or $state.Container -cnotmatch '^rabotyagi-demo-[a-z0-9-]+$' -or
        $state.Port -ne 55439 -or $state.User -cne 'demo_admin' -or $state.Volume -cne ($state.Database + '_data')) {
        throw 'Unexpected demo runtime state. Refusing all database operations.'
    }
    $docker = Find-BackupTool 'docker.exe' @("$env:LOCALAPPDATA/Programs/DockerDesktop/resources/bin/docker.exe", "$env:ProgramFiles/Docker/Docker/resources/bin/docker.exe")
    $gpg = Find-BackupTool 'gpg.exe' @("$env:ProgramFiles/Git/usr/bin/gpg.exe")
    $label = Invoke-BackupProcess $docker @('inspect', $state.Container, '--format', '{{index .Config.Labels "rabotyagi.purpose"}}')
    $ports = Invoke-BackupProcess $docker @('inspect', $state.Container, '--format', '{{json .HostConfig.PortBindings}}') | ConvertFrom-Json
    $mounts = @(Invoke-BackupProcess $docker @('inspect', $state.Container, '--format', '{{json .Mounts}}') | ConvertFrom-Json)
    $binding = @($ports.'5432/tcp')
    if ($label -cne 'local-demo' -or $binding.Count -ne 1 -or $binding[0].HostIp -cne '127.0.0.1' -or $binding[0].HostPort -ne '55439' -or
        $mounts.Count -ne 1 -or $mounts[0].Name -cne $state.Volume -or $mounts[0].Destination -cne '/var/lib/postgresql/data') {
        throw 'Demo container isolation checks failed.'
    }
    # Never start Docker or containers automatically, including other containers with restart policies.
    return [pscustomobject]@{ Docker = $docker; Gpg = $gpg; Container = $state.Container; User = $state.User }
}

function Invoke-BackupSql($Context, [string]$Database, [string]$Sql) {
    return Invoke-BackupProcess $Context.Docker @('exec', $Context.Container, 'psql', '--no-password', '--no-psqlrc', '-U', $Context.User,
        '-d', $Database, '-A', '-t', '-v', 'ON_ERROR_STOP=1', '-c', $Sql)
}

function Assert-NoReparsePoint([string]$Path) {
    $item = Get-Item -LiteralPath $Path -ErrorAction SilentlyContinue
    while ($item) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Symlinks/junctions are not allowed in backup paths.' }
        if ($item -is [IO.FileInfo]) { $item = $item.Directory } else { $item = $item.Parent }
    }
}

function New-PrivateBackupDirectory([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    $parent = Split-Path -Parent $full
    if (Test-Path -LiteralPath $parent) { Assert-NoReparsePoint $parent }
    if (!(Test-Path -LiteralPath $full)) { $null = New-Item -ItemType Directory -Path $full -Force }
    Assert-NoReparsePoint $full
    # Read only the normal DACL; a fresh security descriptor makes Set-Acl attempt
    # SACL/owner updates that require SeSecurityPrivilege on ordinary Windows accounts.
    $acl = Get-Acl -LiteralPath $full
    $acl.SetAccessRuleProtection($true, $false)
    $owner = [Security.Principal.WindowsIdentity]::GetCurrent().User
    foreach ($oldRule in @($acl.Access)) { $acl.RemoveAccessRuleSpecific($oldRule) }
    foreach ($sid in @($owner, [Security.Principal.SecurityIdentifier]::new('S-1-5-18'))) {
        $rule = [Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
        $acl.AddAccessRule($rule)
    }
    [IO.FileSystemAclExtensions]::SetAccessControl([IO.DirectoryInfo]::new($full), $acl)
    return $full
}

function New-BackupStage {
    $root = New-PrivateBackupDirectory (Join-Path $script:Repo '.tools/backup-runtime/staging')
    $path = Join-Path $root ([guid]::NewGuid().ToString('N'))
    try { return New-PrivateBackupDirectory $path }
    catch { Remove-BackupStage $path; throw }
}

function Get-BackupOutputDirectory([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    $marker = Join-Path $full '.rabotyagi-backup-directory'
    if (Test-Path -LiteralPath $full) {
        Assert-NoReparsePoint $full
        if (!(Test-Path -LiteralPath $marker -PathType Leaf) -or (Get-Content -LiteralPath $marker -Raw).Trim() -cne 'rabotyagi-encrypted-backups-v1') {
            throw 'Output directory must be new or previously initialized by this backup tool. Existing arbitrary folders are not modified.'
        }
    }
    $full = New-PrivateBackupDirectory $full
    'rabotyagi-encrypted-backups-v1' | Set-Content -LiteralPath $marker -Encoding ascii
    return $full
}

function Remove-BackupStage([string]$Stage) {
    $root = [IO.Path]::GetFullPath((Join-Path $script:Repo '.tools/backup-runtime/staging')) + [IO.Path]::DirectorySeparatorChar
    $target = [IO.Path]::GetFullPath($Stage)
    if (!$target.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) -or (Split-Path -Leaf $target) -cnotmatch '^[a-f0-9]{32}$') {
        throw 'Refusing staging cleanup outside the verified job directory.'
    }
    if (Test-Path -LiteralPath $target) { Assert-NoReparsePoint $target; Remove-Item -LiteralPath $target -Recurse -Force }
}

function Get-BackupPassphrase([securestring]$Password, [string]$PasswordFile) {
    if ($Password -and $PasswordFile) { throw 'Use Password or PasswordFile, not both.' }
    if ($PasswordFile) { $Password = Import-Clixml -LiteralPath $PasswordFile }
    if (!$Password) { $Password = Read-Host 'Backup passphrase (store it separately for recovery on another computer)' -AsSecureString }
    if ($Password -isnot [securestring] -or $Password.Length -lt 20 -or $Password.Length -gt 512) { throw 'Use a backup passphrase of 20 to 512 characters.' }
    return $Password
}

function Invoke-BackupGpg($Context, [string]$Stage, [string]$InputPath, [string]$OutputPath, [securestring]$Password, [switch]$Decrypt) {
    $gpgDirectory = New-PrivateBackupDirectory (Join-Path $Stage 'gpg')
    # Git's MSYS GPG requires /c/... paths when launched by a native .NET process.
    $gitGpg = $Context.Gpg.Replace('\', '/').EndsWith('/Git/usr/bin/gpg.exe', [StringComparison]::OrdinalIgnoreCase)
    function GpgPath([string]$Path) {
        $full = [IO.Path]::GetFullPath($Path).Replace('\', '/')
        if (!$gitGpg) { return $full }
        if ($full -notmatch '^[a-zA-Z]:/') { throw 'Git GPG backups require a local drive path.' }
        return '/' + $full.Substring(0, 1).ToLowerInvariant() + '/' + $full.Substring(3)
    }
    $arguments = @('--no-options', '--homedir', (GpgPath $gpgDirectory), '--batch', '--no-tty', '--no-autostart', '--no-symkey-cache',
        '--pinentry-mode', 'loopback', '--passphrase-fd', '0', '--output', (GpgPath $OutputPath))
    if ($Decrypt) { $arguments += @('--decrypt', (GpgPath $InputPath)) }
    else { $arguments += @('--symmetric', '--cipher-algo', 'AES256', '--compress-algo', 'none', '--force-mdc', '--s2k-count', '65011712', (GpgPath $InputPath)) }
    $null = Invoke-BackupProcess -Executable $Context.Gpg -Arguments $arguments -Secret $Password
}

function Get-PhotoState($Context, [string]$Database) {
    $sql = 'SELECT coalesce(json_agg(x ORDER BY x."FileName")::text,''[]'') FROM (SELECT "Id", "OrderId", "FileName" FROM "OrderPhotos") x'
    return Invoke-BackupSql $Context $Database $sql
}

function New-DatabaseBackup {
    param([Parameter(Mandatory)][string]$Database, [string]$OutputDirectory = (Join-Path $script:Repo '.backups'),
        [string]$UploadsPath, [switch]$DatabaseOnly, [securestring]$Password, [string]$PasswordFile)
    Assert-BackupDatabase $Database
    if ($DatabaseOnly -and $UploadsPath) { throw 'UploadsPath cannot be combined with DatabaseOnly.' }
    $context = Get-BackupContext
    $Password = Get-BackupPassphrase $Password $PasswordFile
    $output = Get-BackupOutputDirectory $OutputDirectory
    $stage = New-BackupStage
    $partial = $null
    try {
        $payload = New-PrivateBackupDirectory (Join-Path $stage 'payload')
        $before = Get-PhotoState $context $Database
        $photos = @($before | ConvertFrom-Json)
        if ($photos.Count -and !$UploadsPath -and !$DatabaseOnly) { throw 'Database references photos. Supply UploadsPath, or explicitly request DatabaseOnly.' }
        $dump = Join-Path $payload 'database.dump'
        $null = Invoke-BackupProcess -Executable $context.Docker -Arguments @('exec', $context.Container, 'pg_dump', '--no-password', '-U', $context.User,
            '--format=custom', '--no-owner', '--no-acl', $Database) -OutputFile $dump
        $null = Invoke-BackupProcess -Executable $context.Docker -Arguments @('exec', '-i', $context.Container, 'pg_restore', '--list') -InputFile $dump
        $files = @()
        if ($UploadsPath) {
            $source = [IO.Path]::GetFullPath($UploadsPath)
            Assert-NoReparsePoint $source
            $photoDir = New-PrivateBackupDirectory (Join-Path $payload 'uploads')
            foreach ($file in @($photos | ForEach-Object { $_.FileName } | Sort-Object -Unique)) {
                if ($file -cnotmatch '^[a-fA-F0-9-]{32,36}\.(png|jpg|jpeg|webp)$') { throw 'Unsafe photo filename in database.' }
                $from = Join-Path $source $file
                Assert-NoReparsePoint $from
                Copy-Item -LiteralPath $from -Destination (Join-Path $photoDir $file) -ErrorAction Stop
                $files += @{ Name = $file; Sha256 = (Get-FileHash -LiteralPath (Join-Path $photoDir $file) -Algorithm SHA256).Hash }
            }
            if ((Get-PhotoState $context $Database) -cne $before) { throw 'Photo references changed during backup. Retry when report writes are paused.' }
        }
        $manifest = @{ Format = 'rabotyagi-backup-v1'; CreatedUtc = [DateTimeOffset]::UtcNow.ToString('o'); SourceDatabase = $Database
            DatabaseSha256 = (Get-FileHash -LiteralPath $dump -Algorithm SHA256).Hash; UploadsIncluded = [bool]$UploadsPath; Photos = @($files)
            PgDumpVersion = (Invoke-BackupProcess $context.Docker @('exec', $context.Container, 'pg_dump', '--version')) }
        $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $payload 'manifest.json') -Encoding utf8
        $zip = Join-Path $stage 'bundle.zip'
        [IO.Compression.ZipFile]::CreateFromDirectory($payload, $zip)
        $name = $Database + '_' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '_' + [guid]::NewGuid().ToString('N') + '.zip.gpg'
        $final = Join-Path $output $name
        $partial = $final + '.partial'
        Invoke-BackupGpg $context $stage $zip $partial $Password
        [IO.File]::Move($partial, $final)
        $partial = $null
        return [pscustomobject]@{ BackupPath = $final; Sha256 = (Get-FileHash -LiteralPath $final -Algorithm SHA256).Hash
            Database = $Database; PhotosIncluded = [bool]$UploadsPath; PhotoCount = $files.Count; CreatedUtc = $manifest.CreatedUtc }
    } finally {
        if ($partial -and (Test-Path -LiteralPath $partial)) { Remove-Item -LiteralPath $partial -Force }
        Remove-BackupStage $stage
    }
}

function Expand-BackupBundle([string]$Zip, [string]$Destination) {
    $archive = [IO.Compression.ZipFile]::OpenRead($Zip)
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    try {
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName
            if (!$seen.Add($name) -or $name -cnotmatch '^(database\.dump|manifest\.json|uploads/|uploads/[a-fA-F0-9-]{32,36}\.(png|jpg|jpeg|webp))$') {
                throw 'Unexpected or unsafe file in backup bundle.'
            }
            if ($name -ceq 'uploads/') { continue }
            $path = Join-Path $Destination $name
            $null = [IO.Directory]::CreateDirectory((Split-Path -Parent $path))
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $path, $false)
        }
    } finally { $archive.Dispose() }
}

function Restore-DatabaseBackup {
    param([Parameter(Mandatory)][string]$BackupPath, [Parameter(Mandatory)][string]$Database,
        [securestring]$Password, [string]$PasswordFile)
    Assert-BackupDatabase $Database -Restore
    $context = Get-BackupContext
    if ((Invoke-BackupSql $context 'postgres' "SELECT count(*) FROM pg_database WHERE datname='$Database'") -ne '0') {
        throw 'Target database already exists. Existing databases are never overwritten, even if empty.'
    }
    $restoreRoot = [IO.Path]::GetFullPath((Join-Path $script:Repo "rabotyagi/.demo/$Database"))
    if (Test-Path -LiteralPath $restoreRoot) { throw 'Target photo/key directory already exists. Choose a fresh restore database name.' }
    Assert-NoReparsePoint (Split-Path -Parent $restoreRoot)
    $Password = Get-BackupPassphrase $Password $PasswordFile
    $stage = New-BackupStage
    try {
        $zip = Join-Path $stage 'bundle.zip'
        Invoke-BackupGpg $context $stage ([IO.Path]::GetFullPath($BackupPath)) $zip $Password -Decrypt
        $payload = New-PrivateBackupDirectory (Join-Path $stage 'payload')
        Expand-BackupBundle $zip $payload
        $manifest = Get-Content -LiteralPath (Join-Path $payload 'manifest.json') -Raw | ConvertFrom-Json
        if ($manifest.Format -cne 'rabotyagi-backup-v1') { throw 'Unsupported backup format.' }
        Assert-BackupDatabase $manifest.SourceDatabase
        $dump = Join-Path $payload 'database.dump'
        if ((Get-FileHash -LiteralPath $dump -Algorithm SHA256).Hash -cne $manifest.DatabaseSha256) { throw 'Database archive checksum mismatch.' }
        $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($photo in $manifest.Photos) {
            if ($photo.Name -cnotmatch '^[a-fA-F0-9-]{32,36}\.(png|jpg|jpeg|webp)$' -or !$expected.Add($photo.Name) -or
                (Get-FileHash -LiteralPath (Join-Path $payload "uploads/$($photo.Name)") -Algorithm SHA256).Hash -cne $photo.Sha256) { throw 'Photo checksum or filename mismatch.' }
        }
        $actualFiles = [string[]]@()
        if (Test-Path -LiteralPath (Join-Path $payload 'uploads')) {
            $actualFiles = [string[]]@(Get-ChildItem -LiteralPath (Join-Path $payload 'uploads') -File | Select-Object -ExpandProperty Name)
        }
        if (!$expected.SetEquals($actualFiles) -or (!$manifest.UploadsIncluded -and $expected.Count -gt 0)) { throw 'Unexpected photo files or inconsistent manifest.' }
        $null = Invoke-BackupProcess -Executable $context.Docker -Arguments @('exec', '-i', $context.Container, 'pg_restore', '--list') -InputFile $dump
        # No --create/--clean: pg_restore must never use the original name stored in its archive.
        $null = Invoke-BackupProcess $context.Docker @('exec', $context.Container, 'createdb', '--no-password', '-U', $context.User, '--template=template0', $Database)
        $null = Invoke-BackupProcess -Executable $context.Docker -Arguments @('exec', '-i', $context.Container, 'pg_restore', '--no-password', '-U', $context.User,
            '--dbname', $Database, '--single-transaction', '--exit-on-error', '--no-owner', '--no-acl', '--no-tablespaces') -InputFile $dump
        if ($manifest.UploadsIncluded) {
            $restoredPhotos = @(Get-PhotoState $context $Database | ConvertFrom-Json)
            if (!$expected.SetEquals([string[]]@($restoredPhotos | ForEach-Object { $_.FileName }))) { throw 'Restored photo references differ from the backup manifest. Restore is incomplete.' }
            $null = New-PrivateBackupDirectory $restoreRoot
            $destination = Join-Path $restoreRoot 'uploads'
            if (Test-Path -LiteralPath (Join-Path $payload 'uploads')) { Move-Item -LiteralPath (Join-Path $payload 'uploads') -Destination $destination }
            else { $null = New-PrivateBackupDirectory $destination }
        }
        return [pscustomobject]@{ Database = $Database; SourceDatabase = $manifest.SourceDatabase; PhotosIncluded = $manifest.UploadsIncluded
            UploadsPath = $(if ($manifest.UploadsIncluded) { Join-Path $restoreRoot 'uploads' } else { $null }); BackupCreatedUtc = $manifest.CreatedUtc }
    } finally { Remove-BackupStage $stage }
}

Export-ModuleMember -Function New-DatabaseBackup, Restore-DatabaseBackup
