param(
    [Parameter(Mandatory = $true)][string]$ZipPath,
    [string]$InstallRoot = "",
    [string]$DataDir = "",
    [string]$ExpectedVersion = "",
    [string]$ExpectedSha256 = "",
    [int]$Port = 0,
    [string]$HealthUrl = "",
    [int]$HealthTimeoutSeconds = 60,
    [switch]$Force,
    [switch]$SkipBackup
)

# Upgrades an existing versioned installation. Program directories are versioned;
# data lives in one fixed directory and is backed up after the service is stopped.
#
# ASCII-only on purpose: PowerShell 5 reads a BOM-less UTF-8 file as ANSI.

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'web-common.ps1')

if ([string]::IsNullOrWhiteSpace($InstallRoot)) { $InstallRoot = $PSScriptRoot }
$InstallRoot = [System.IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
if ([string]::IsNullOrWhiteSpace($DataDir)) { $DataDir = Get-DefaultDataDir $InstallRoot }
$DataDir = [System.IO.Path]::GetFullPath($DataDir).TrimEnd('\')
$ZipPath = [System.IO.Path]::GetFullPath($ZipPath)

if (-not (Test-Path -LiteralPath (Join-Path $InstallRoot 'current.txt'))) {
    throw "this is not a versioned Web installation: $InstallRoot. Use install-web.ps1 for the first migration."
}
if (-not (Test-Path -LiteralPath $ZipPath -PathType Leaf)) { throw "update zip not found: $ZipPath" }

$currentVersion = Get-CurrentWebVersion $InstallRoot
$currentDir = Join-Path (Join-Path $InstallRoot 'versions') $currentVersion
if (-not (Test-Path -LiteralPath $currentDir -PathType Container)) { throw "current version is not installed: $currentVersion" }
$currentManifest = $null
try { $currentManifest = Read-WebManifest $currentDir } catch { $currentManifest = $null }

$versionsDir = Join-Path $InstallRoot 'versions'
$backupsDir = Join-Path $InstallRoot 'backups'
$logsDir = Join-Path $InstallRoot 'logs'
$stagingRoot = Join-Path $InstallRoot 'staging'
foreach ($directory in @($versionsDir, $backupsDir, $logsDir, $stagingRoot)) {
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
}

Write-Host ("upgrading " + $InstallRoot)
Write-Host ("current version " + $currentVersion)
Write-Host ("data            " + $DataDir)

# 1. Verify the package before touching the running service.
$hash = Get-WebSha256 $ZipPath
Write-Host ("zip sha256 " + $hash)
if (-not [string]::IsNullOrWhiteSpace($ExpectedSha256) -and $hash -ne $ExpectedSha256.ToUpperInvariant()) {
    throw "zip SHA-256 mismatch: expected $($ExpectedSha256.ToUpperInvariant()), got $hash"
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
$manifest = $null
try {
    foreach ($entry in $zip.Entries) {
        $name = $entry.FullName.Replace('/', '\')
        if ([System.IO.Path]::IsPathRooted($name) -or $name -match '(^|\\)\.\.(\\|$)') {
            throw "zip contains an unsafe path: $($entry.FullName)"
        }
    }
    $manifestEntry = $zip.Entries | Where-Object { $_.FullName.Replace('\', '/') -eq 'release-manifest.json' } | Select-Object -First 1
    if (-not $manifestEntry) { throw 'zip has no release-manifest.json' }
    $reader = New-Object System.IO.StreamReader($manifestEntry.Open())
    try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
} finally {
    $zip.Dispose()
}

$version = [string]$manifest.version
if ([string]::IsNullOrWhiteSpace($version)) { throw 'release-manifest.json has no version' }
if (-not [string]::IsNullOrWhiteSpace($ExpectedVersion) -and $version -ne $ExpectedVersion) {
    throw "zip version is $version, not the expected $ExpectedVersion"
}
if ($version -eq $currentVersion) { throw "this version is already installed: $version" }
$newDataFormat = if ($null -ne $manifest.dataFormat) { [int]$manifest.dataFormat } else { $null }
$oldDataFormat = if ($currentManifest -and $null -ne $currentManifest.dataFormat) { [int]$currentManifest.dataFormat } else { $null }

$staging = Join-Path $stagingRoot ($version + '-' + (Get-Date).ToString('yyyyMMddHHmmss'))
if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
New-Item -ItemType Directory -Force -Path $staging | Out-Null
Write-Host 'extracting package'
[System.IO.Compression.ZipFile]::ExtractToDirectory($ZipPath, $staging)
$entries = @(Get-ChildItem -LiteralPath $staging -Force)
if ($entries.Count -eq 1 -and $entries[0].PSIsContainer) { $staging = $entries[0].FullName }
$null = Assert-WebPackage $staging

# 2. Explain what stopping the service does to in-flight work.
Write-Host ''
Write-Host 'The upgrade stops this Web service. In-flight work is handled as follows:'
Write-Host '  - running chat replies and generation jobs are interrupted; retry them after the upgrade'
Write-Host '  - uploads in progress fail and must be uploaded again'
Write-Host '  - writes that were not saved yet are lost; save your drafts before continuing'
Write-Host '  - open pages reconnect and fetch a fresh snapshot after the service returns;'
Write-Host '    unsaved drafts stay in the page, but review them before saving'
Write-Host ''
if (-not $Force) {
    $answer = Read-Host 'Type YES to stop the service, back up data and upgrade'
    if ($answer -ne 'YES') { throw 'upgrade cancelled' }
}

$health = if (-not [string]::IsNullOrWhiteSpace($HealthUrl)) { $HealthUrl }
          elseif ($Port -gt 0) { "http://127.0.0.1:$Port/health" }
          else { 'http://localhost:5000/health' }

$null = Stop-WebService $InstallRoot $DataDir
Write-Host 'web service stopped'

# Any failure before current.txt changes leaves the old program selected. Start it
# again so an upgrade that cannot even back up does not leave the site offline.
function Restore-RunningService([string]$reason) {
    Write-Host $reason
    try {
        $recoverLog = Join-Path $logsDir ("web-" + $currentVersion + "-" + (Get-Date).ToString('yyyyMMddHHmmss') + '.log')
        $null = Start-WebBackground $InstallRoot $DataDir $currentVersion $Port $recoverLog
        if (Wait-WebHealth $health $HealthTimeoutSeconds) {
            Write-Host ("previous version is healthy again: " + $currentVersion)
        } else {
            Write-Host ('previous version did not become healthy; log: ' + $recoverLog)
            Write-FilteredLogTail $recoverLog 40
            Write-FilteredLogTail ($recoverLog + '.err') 40
        }
    } catch {
        Write-Host ('could not restart the previous version: ' + $_.Exception.Message)
    }
    throw $reason
}

# 3. A backup is taken only after the service is stopped so SQLite WAL files are consistent.
#    The completion record is written last; a failed or interrupted copy keeps
#    only the in-progress marker and is never accepted by rollback-web.ps1.
$backup = $null
if (-not $SkipBackup) {
    try {
        $backup = Join-Path $backupsDir ('data-' + (Get-Date).ToString('yyyyMMddHHmmss'))
        Write-Host ("backing up data to " + $backup)
        Start-WebBackup $backup $currentVersion
        Copy-MergeTree $DataDir $backup
        Copy-Item -LiteralPath (Join-Path $InstallRoot 'current.txt') -Destination (Join-Path $backup 'current-version.txt') -Force
        Assert-TreeCopied $DataDir $backup -Hash
        $backupRecord = Complete-WebBackup $backup $currentVersion $oldDataFormat
        Write-Host ("backup complete: " + $backupRecord.fileCount + " files, " + $backupRecord.totalBytes + " bytes")
    } catch {
        Restore-RunningService ('backup failed, upgrade aborted: ' + $_.Exception.Message)
    }
}

# 4. Install the new program directory. Staging is on the same volume, so this is a move.
$versionDir = Join-Path $versionsDir $version
try {
    if (Test-Path -LiteralPath $versionDir) {
        $aside = $versionDir + '.replaced-' + (Get-Date).ToString('yyyyMMddHHmmss')
        Move-Item -LiteralPath $versionDir -Destination $aside
    }
    Move-Item -LiteralPath $staging -Destination $versionDir
    Set-CurrentWebVersion $InstallRoot $version
} catch {
    Set-CurrentWebVersion $InstallRoot $currentVersion
    Restore-RunningService ('could not install the new program directory: ' + $_.Exception.Message)
}
Write-Host ("installed " + $version)

$log = Join-Path $logsDir ("web-" + $version + "-" + (Get-Date).ToString('yyyyMMddHHmmss') + '.log')

# 5. Start and verify health.
$healthy = $false
try {
    $info = Start-WebBackground $InstallRoot $DataDir $version $Port $log
    $healthy = Wait-WebHealth $health $HealthTimeoutSeconds
} catch {
    Write-Host ('start failed: ' + $_.Exception.Message)
}
if ($healthy) {
    Write-Host ("healthy: " + $health)
    Write-Host ("upgraded to " + $version)
    if ($backup) { Write-Host ("data backup: " + $backup) }
    Write-Host 'previous version directories are kept under versions\.'
    Write-Host 'If a page was open during the upgrade, confirm it reconnected and save any remaining draft.'
    exit 0
}

# 6. Failure handling. Only restore the previous program automatically when both
#    manifests declare the same data format. Otherwise data may already have been
#    migrated, and starting the old program against it would be unsafe.
Write-Host 'the new version did not become healthy'
$null = Stop-WebService $InstallRoot $DataDir
Write-FilteredLogTail $log 40
Write-FilteredLogTail ($log + '.err') 40
if ($backup) { Write-Host ("data backup: " + $backup) }
Write-Host ("failed version directory: " + $versionDir)

$sameDataFormat = ($null -ne $oldDataFormat -and $null -ne $newDataFormat -and $oldDataFormat -eq $newDataFormat)
if ($sameDataFormat) {
    Write-Host ("data format is unchanged (" + $oldDataFormat + "); restoring previous program " + $currentVersion)
    Set-CurrentWebVersion $InstallRoot $currentVersion
    $oldLog = Join-Path $logsDir ("web-" + $currentVersion + "-" + (Get-Date).ToString('yyyyMMddHHmmss') + '.log')
    try {
        $null = Start-WebBackground $InstallRoot $DataDir $currentVersion $Port $oldLog
        if (Wait-WebHealth $health $HealthTimeoutSeconds) {
            Write-Host ("previous version is healthy again: " + $currentVersion)
            Write-Host 'data was not restored from backup; writes made by the failed version remain.'
            Write-Host ('inspect the failed version log: ' + $log)
            exit 1
        }
        Write-Host 'the previous version also failed to become healthy'
        Write-FilteredLogTail $oldLog 40
        Write-FilteredLogTail ($oldLog + '.err') 40
    } catch {
        Write-Host ('restarting the previous version failed: ' + $_.Exception.Message)
    }
    Write-Host 'automatic recovery failed. The service is stopped.'
    Write-Host 'manual steps:'
    Write-Host ('  1. review logs: ' + $log + ' and ' + $oldLog)
    Write-Host ('  2. start ' + $currentVersion + ' manually: powershell -ExecutionPolicy Bypass -File "' + (Join-Path $InstallRoot 'start-web.ps1') + '" -Version ' + $currentVersion)
    if ($backup) {
        Write-Host ('  3. restore data only if needed: powershell -ExecutionPolicy Bypass -File "' + (Join-Path $InstallRoot 'rollback-web.ps1') + '" -Version ' + $currentVersion + ' -RestoreData "' + $backup + '" -ConfirmDataLoss')
        Write-Host '     restoring the backup loses every write made after the backup was taken.'
    }
    exit 1
}

Write-Host 'data format changed or is unknown, so automatic program rollback was refused.'
Write-Host 'the service is stopped and no old program was started against the new data.'
Write-Host 'manual steps:'
Write-Host ('  1. review the log: ' + $log)
Write-Host ('  2. if you are certain the data was not migrated, force a program rollback:')
Write-Host ('     powershell -ExecutionPolicy Bypass -File "' + (Join-Path $InstallRoot 'rollback-web.ps1') + '" -Version ' + $currentVersion + ' -ForceIncompatibleData')
if ($backup) {
    Write-Host ('  3. to restore the pre-upgrade data as well:')
    Write-Host ('     powershell -ExecutionPolicy Bypass -File "' + (Join-Path $InstallRoot 'rollback-web.ps1') + '" -Version ' + $currentVersion + ' -RestoreData "' + $backup + '" -ConfirmDataLoss')
    Write-Host '     restoring the backup loses every write made after the backup was taken.'
}
exit 1
