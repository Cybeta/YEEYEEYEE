# Shared helpers for the Windows Web package scripts.
#
# ASCII-only on purpose: PowerShell 5 reads a BOM-less UTF-8 file as ANSI.
# These helpers never print configuration contents, tokens or keys. They only
# report paths, versions, counts and health results.

$ErrorActionPreference = 'Stop'

function Get-WebSha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Read-WebManifest([string]$Directory) {
    $path = Join-Path $Directory 'release-manifest.json'
    if (-not (Test-Path -LiteralPath $path)) { throw "release-manifest.json not found in $Directory" }
    return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
}

function Assert-WebPackage([string]$Directory) {
    $required = @(
        'YEEYEEYEE.Web.dll',
        'YEEYEEYEE.Web.runtimeconfig.json',
        'appsettings.json',
        'canvas-dist\index.html',
        'release-manifest.json'
    )
    foreach ($relative in $required) {
        $path = Join-Path $Directory $relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "package is missing $relative" }
        if ((Get-Item -LiteralPath $path).Length -le 0) { throw "package file is empty: $relative" }
    }

    $assets = Join-Path $Directory 'canvas-dist\assets'
    if (-not (Test-Path -LiteralPath $assets -PathType Container)) { throw 'package is missing canvas-dist\assets' }
    if (@(Get-ChildItem -LiteralPath $assets -File -Recurse).Count -eq 0) { throw 'canvas-dist\assets is empty' }

    $manifest = Read-WebManifest $Directory
    if ($manifest.product -ne 'yeeeyee-web') { throw "unexpected package product: $($manifest.product)" }
    if ([string]::IsNullOrWhiteSpace([string]$manifest.version)) { throw 'package manifest has no version' }

    # Optional per-file hashes let the package prove that the backend and the
    # frontend entry point are the ones that were built together.
    if ($manifest.sha256) {
        foreach ($property in $manifest.sha256.PSObject.Properties) {
            $relative = $property.Name
            $path = Join-Path $Directory $relative
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "manifest lists a missing file: $relative" }
            $actual = Get-WebSha256 $path
            if ($actual -ne ([string]$property.Value).ToUpperInvariant()) {
                throw "hash mismatch for ${relative}: expected $($property.Value), got $actual"
            }
        }
    }

    # These belong to an installation, never to a package.
    foreach ($forbidden in @('Projects', 'data', 'config', 'assets', 'web-ai-config.json', 'yeeeyee.users.db', 'yeeeyee.jobs.db', 'yeeeyee.chat.db', '.setup-token')) {
        if (Test-Path -LiteralPath (Join-Path $Directory $forbidden)) {
            throw "package must not contain installation data: $forbidden"
        }
    }
    $leaks = @(Get-ChildItem -LiteralPath $Directory -Recurse -File -Force |
        Where-Object { $_.Extension -ieq '.pdb' -or $_.Name -match '(?i)ffmpeg|ai-key|ai-config|recent-projects' })
    if ($leaks.Count -gt 0) { throw ('package contains forbidden files: ' + (($leaks | ForEach-Object FullName) -join ', ')) }

    return $manifest
}

function Get-WebRunningProcesses([string]$InstallRoot) {
    $root = [System.IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
    $found = @()
    try {
        $processes = Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" -ErrorAction Stop
        foreach ($process in $processes) {
            $command = [string]$process.CommandLine
            if ($command.IndexOf($root, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
                $command.IndexOf('YEEYEEYEE.Web.dll', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $found += $process
            }
        }
    } catch {
        # CIM can be unavailable in restricted sessions; the pid file remains the fallback.
    }
    return $found
}

function Read-WebPidFile([string]$DataDir) {
    $path = Join-Path $DataDir 'run\web.pid'
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    try { return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json }
    catch { return $null }
}

function Stop-WebService([string]$InstallRoot, [string]$DataDir) {
    $stopped = @()
    $processes = @(Get-WebRunningProcesses $InstallRoot)

    # A pid file is useful for reporting even when CIM is unavailable, but only
    # a process whose command line points at this installation may be stopped.
    $info = Read-WebPidFile $DataDir
    if ($info -and $info.pid) {
        $process = Get-Process -Id ([int]$info.pid) -ErrorAction SilentlyContinue
        if ($process -and -not ($processes | Where-Object { $_.ProcessId -eq $process.Id })) {
            $cim = Get-CimInstance Win32_Process -Filter "ProcessId=$($process.Id)" -ErrorAction SilentlyContinue
            if ($cim -and ([string]$cim.CommandLine).IndexOf([System.IO.Path]::GetFullPath($InstallRoot), [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $processes += $cim
            }
        }
    }

    if ($processes.Count -eq 0) {
        Remove-Item -LiteralPath (Join-Path $DataDir 'run\web.pid') -Force -ErrorAction SilentlyContinue
        return @()
    }

    foreach ($process in $processes) {
        Write-Host ("stopping web service pid " + $process.ProcessId)
        Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
        $stopped += $process.ProcessId
    }

    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline -and @(Get-WebRunningProcesses $InstallRoot).Count -gt 0) {
        Start-Sleep -Milliseconds 300
    }
    $left = @(Get-WebRunningProcesses $InstallRoot)
    if ($left.Count -gt 0) {
        throw ('could not stop this installation''s web service (pid(s): ' + (($left | ForEach-Object ProcessId) -join ', ') + ')')
    }
    Remove-Item -LiteralPath (Join-Path $DataDir 'run\web.pid') -Force -ErrorAction SilentlyContinue
    return $stopped
}

function Copy-MergeTree([string]$Source, [string]$Target) {
    if (-not (Test-Path -LiteralPath $Target)) { New-Item -ItemType Directory -Force -Path $Target | Out-Null }
    foreach ($entry in Get-ChildItem -LiteralPath $Source -Force) {
        $destination = Join-Path $Target $entry.Name
        if ($entry.PSIsContainer) { Copy-MergeTree $entry.FullName $destination }
        else { Copy-Item -LiteralPath $entry.FullName -Destination $destination -Force -ErrorAction Stop }
    }
}

function Get-TreeFacts([string]$Directory) {
    $files = @(Get-ChildItem -LiteralPath $Directory -Recurse -File -Force)
    $bytes = 0
    foreach ($file in $files) { $bytes += $file.Length }
    return [pscustomobject]@{ Count = $files.Count; Bytes = $bytes }
}

function Assert-TreeCopied([string]$Source, [string]$Target, [switch]$Hash) {
    # The target may already contain other (newer) files, so only prove that every
    # source file arrived intact. Extra files in the target are not a copy failure.
    $sourceRoot = $Source.TrimEnd('\')
    $targetRoot = $Target.TrimEnd('\')
    foreach ($file in Get-ChildItem -LiteralPath $Source -Recurse -File -Force) {
        $relative = $file.FullName.Substring($sourceRoot.Length).TrimStart('\')
        $copy = Join-Path $Target $relative
        if (-not (Test-Path -LiteralPath $copy -PathType Leaf)) { throw "file was not copied: $relative" }
        if ((Get-Item -LiteralPath $copy).Length -ne $file.Length) { throw "file size differs after copy: $relative" }
        if ($Hash -and (Get-WebSha256 $copy) -ne (Get-WebSha256 $file.FullName)) {
            throw "file content differs after copy: $relative"
        }
    }
}

# Backup state: a backup directory is only restorable when it carries a
# completion record written after every file was copied and verified. The
# in-progress marker makes a failed or interrupted backup recognisable; its
# mere presence never makes a directory restorable. Neither record contains
# configuration contents, tokens or keys.
function Write-WebBackupRecord([string]$Path, $Value) {
    $json = $Value | ConvertTo-Json -Depth 6
    $temporary = $Path + '.tmp'
    [System.IO.File]::WriteAllText($temporary, $json, (New-Object System.Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $temporary -Destination $Path -Force
}

function Test-WebBackupMarkerFile([System.IO.FileInfo]$File, [string]$Root) {
    if (([System.IO.Path]::GetFullPath((Split-Path -Parent $File.FullName))).TrimEnd('\') -ne $Root) { return $false }
    return ($File.Name -eq 'backup-state.json' -or $File.Name -eq 'backup-complete.json')
}

function Get-WebBackupDataFiles([string]$Directory) {
    $root = [System.IO.Path]::GetFullPath($Directory).TrimEnd('\')
    $entries = New-Object System.Collections.ArrayList
    foreach ($file in Get-ChildItem -LiteralPath $Directory -Recurse -File -Force) {
        if (Test-WebBackupMarkerFile $file $root) { continue }
        $relative = $file.FullName.Substring($root.Length).TrimStart('\')
        $null = $entries.Add([pscustomobject]@{
            path   = $relative
            bytes  = [long]$file.Length
            sha256 = (Get-WebSha256 $file.FullName)
        })
    }
    return $entries.ToArray()
}

function Start-WebBackup([string]$Directory, [string]$SourceVersion) {
    New-Item -ItemType Directory -Force -Path $Directory | Out-Null
    $state = [ordered]@{
        product       = 'yeeeyee-web-backup'
        state         = 'in-progress'
        sourceVersion = $SourceVersion
        startedAt     = (Get-Date).ToUniversalTime().ToString('o')
    }
    Write-WebBackupRecord (Join-Path $Directory 'backup-state.json') $state
}

function Complete-WebBackup([string]$Directory, [string]$SourceVersion, $DataFormat) {
    $files = @(Get-WebBackupDataFiles $Directory)
    if ($files.Count -eq 0) { throw "backup contains no data files: $Directory" }
    $bytes = [long]0
    foreach ($file in $files) { $bytes += [long]$file.bytes }
    $record = [ordered]@{
        product       = 'yeeeyee-web-backup'
        state         = 'complete'
        sourceVersion = $SourceVersion
        dataFormat    = $DataFormat
        completedAt   = (Get-Date).ToUniversalTime().ToString('o')
        fileCount     = $files.Count
        totalBytes    = $bytes
        files         = @($files)
    }
    Write-WebBackupRecord (Join-Path $Directory 'backup-complete.json') $record
    Remove-Item -LiteralPath (Join-Path $Directory 'backup-state.json') -Force -ErrorAction Stop
    return [pscustomobject]$record
}

function Assert-WebBackupComplete([string]$Directory) {
    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) {
        throw "data backup not found: $Directory"
    }
    $completePath = Join-Path $Directory 'backup-complete.json'
    $statePath = Join-Path $Directory 'backup-state.json'
    # An in-progress marker always wins: a directory that is being written to
    # must never be offered as restorable, even if an older record is present.
    if (Test-Path -LiteralPath $statePath -PathType Leaf) {
        throw "backup did not finish (an upgrade was interrupted or failed): $Directory"
    }
    if (-not (Test-Path -LiteralPath $completePath -PathType Leaf)) {
        if (@(Get-ChildItem -LiteralPath $Directory -Recurse -File -Force).Count -eq 0) {
            throw "backup directory is empty: $Directory"
        }
        throw "backup has no completion record; unmarked backups are not treated as complete: $Directory"
    }

    $record = $null
    try { $record = Get-Content -LiteralPath $completePath -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { throw "backup completion record cannot be read: $completePath" }
    if ($null -eq $record -or $record.product -ne 'yeeeyee-web-backup' -or $record.state -ne 'complete') {
        throw "backup completion record is not valid: $completePath"
    }
    $recordedFiles = @($record.files)
    if ($recordedFiles.Count -eq 0) { throw "backup completion record lists no files: $completePath" }

    $actual = @{}
    foreach ($file in @(Get-WebBackupDataFiles $Directory)) { $actual[[string]$file.path] = $file }
    if ($actual.Count -eq 0) { throw "backup directory is empty: $Directory" }

    $seen = @{}
    $bytes = [long]0
    foreach ($entry in $recordedFiles) {
        $relative = [string]$entry.path
        if ([string]::IsNullOrWhiteSpace($relative)) { throw 'backup completion record contains an empty path' }
        if ([System.IO.Path]::IsPathRooted($relative) -or $relative -match '(^|\\)\.\.(\\|$)') {
            throw "backup completion record contains an unsafe path: $relative"
        }
        if (-not $actual.ContainsKey($relative)) { throw "backup file is missing: $relative" }
        $file = $actual[$relative]
        if ([long]$entry.bytes -ne [long]$file.bytes) {
            throw ("backup file size differs: $relative (recorded " + [long]$entry.bytes + ', actual ' + [long]$file.bytes + ')')
        }
        if (([string]$entry.sha256).ToUpperInvariant() -ne [string]$file.sha256) {
            throw "backup file hash differs: $relative"
        }
        $seen[$relative] = $true
        $bytes += [long]$file.bytes
    }
    foreach ($relative in @($actual.Keys)) {
        if (-not $seen.ContainsKey($relative)) {
            throw "backup contains a file that is not in the completion record: $relative"
        }
    }
    if ([long]$record.fileCount -ne $seen.Count) {
        throw ("backup completion record counts " + [long]$record.fileCount + ' files, found ' + $seen.Count)
    }
    if ([long]$record.totalBytes -ne $bytes) {
        throw ("backup completion record totals " + [long]$record.totalBytes + ' bytes, found ' + $bytes)
    }
    return $record
}

function Copy-WebBackupData([string]$BackupDirectory, [string]$TargetDirectory, $Record) {
    # Copy exactly the files named by the completion record and verify each
    # copy against the recorded size and hash. Marker files never enter data\.
    foreach ($entry in @($Record.files)) {
        $relative = [string]$entry.path
        $source = Join-Path $BackupDirectory $relative
        $target = Join-Path $TargetDirectory $relative
        $parent = Split-Path -Parent $target
        if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
        Copy-Item -LiteralPath $source -Destination $target -Force -ErrorAction Stop
        if ((Get-Item -LiteralPath $target).Length -ne [long]$entry.bytes) {
            throw "restored file size differs: $relative"
        }
        if ((Get-WebSha256 $target) -ne ([string]$entry.sha256).ToUpperInvariant()) {
            throw "restored file hash differs: $relative"
        }
    }
}

function Start-WebBackground([string]$InstallRoot, [string]$DataDir, [string]$Version, [int]$Port, [string]$LogFile) {
    $startScript = Join-Path $InstallRoot 'start-web.ps1'
    if (-not (Test-Path -LiteralPath $startScript)) { throw "start-web.ps1 not found in $InstallRoot" }
    $arguments = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $startScript,
        '-InstallRoot', $InstallRoot,
        '-DataDir', $DataDir,
        '-Version', $Version,
        '-NoWait'
    )
    if ($Port -gt 0) { $arguments += @('-Port', [string]$Port) }
    if (-not [string]::IsNullOrWhiteSpace($LogFile)) { $arguments += @('-LogFile', $LogFile) }
    $quoted = @($arguments | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '
    $launcher = Start-Process -FilePath 'powershell.exe' -ArgumentList $quoted -PassThru -WindowStyle Hidden -ErrorAction Stop

    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline) {
        $info = Read-WebPidFile $DataDir
        if ($info -and $info.pid) {
            $process = Get-Process -Id ([int]$info.pid) -ErrorAction SilentlyContinue
            if ($process) { return $info }
        }
        if ($launcher.HasExited) { break }
        Start-Sleep -Milliseconds 300
    }
    throw 'the web service did not start (no pid file appeared)'
}

function Wait-WebHealth([string]$HealthUrl, [int]$TimeoutSeconds) {
    $deadline = (Get-Date).AddSeconds([Math]::Max(5, $TimeoutSeconds))
    do {
        try {
            $response = Invoke-WebRequest -Uri $HealthUrl -UseBasicParsing -TimeoutSec 3
            if ($response.StatusCode -eq 200 -and $response.Content -match '"status"\s*:\s*"ok"') { return $true }
        } catch { }
        Start-Sleep -Milliseconds 700
    } while ((Get-Date) -lt $deadline)
    return $false
}

function Write-FilteredLogTail([string]$LogFile, [int]$Lines) {
    if ([string]::IsNullOrWhiteSpace($LogFile) -or -not (Test-Path -LiteralPath $LogFile)) { return }
    Write-Host 'recent log lines (lines that may contain credentials are omitted):'
    # The script stays ASCII-only, so the Chinese words for token/password/key
    # are built from code points instead of literal characters.
    $tokenWord = [string][char]0x4EE4 + [string][char]0x724C   # token
    $passwordWord = [string][char]0x5BC6 + [string][char]0x7801 # password
    $keyWord = [string][char]0x5BC6 + [string][char]0x94A5      # secret key
    $secret = '(?i)(token|password|secret|api[\s_-]?key|authorization|bearer|' +
        $tokenWord + '|' + $passwordWord + '|' + $keyWord + ')'
    # Application logs are UTF-8 without BOM; Windows PowerShell 5 would read them
    # as ANSI and fail to match Chinese credential labels.
    Get-Content -LiteralPath $LogFile -Tail $Lines -Encoding UTF8 |
        Where-Object { $_ -notmatch $secret } |
        ForEach-Object { Write-Host ('  ' + $_) }
}

function Get-DefaultDataDir([string]$InstallRoot) {
    return [System.IO.Path]::GetFullPath((Join-Path $InstallRoot 'data'))
}

function Get-CurrentWebVersion([string]$InstallRoot) {
    $pointer = Join-Path $InstallRoot 'current.txt'
    if (-not (Test-Path -LiteralPath $pointer)) { throw "current.txt not found; this is not a versioned Web installation: $InstallRoot" }
    $version = (Get-Content -LiteralPath $pointer -Raw).Trim()
    if ([string]::IsNullOrWhiteSpace($version)) { throw 'current.txt is empty' }
    return $version
}

function Set-CurrentWebVersion([string]$InstallRoot, [string]$Version) {
    $pointer = Join-Path $InstallRoot 'current.txt'
    $temporary = $pointer + '.tmp'
    [System.IO.File]::WriteAllText($temporary, $Version, (New-Object System.Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $temporary -Destination $pointer -Force
}
