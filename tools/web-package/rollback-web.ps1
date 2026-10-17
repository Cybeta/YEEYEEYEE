param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$InstallRoot = "",
    [string]$DataDir = "",
    [string]$RestoreData = "",
    [int]$Port = 0,
    [string]$HealthUrl = "",
    [int]$HealthTimeoutSeconds = 60,
    [switch]$ConfirmDataLoss,
    [switch]$ForceIncompatibleData
)

# Rolls the program back to an installed version. Program rollback and data
# restore are separate operations on purpose:
#   - program rollback keeps the current data;
#   - data restore returns to the backup taken before an upgrade and therefore
#     loses every write made after that backup.
#
# ASCII-only on purpose: PowerShell 5 reads a BOM-less UTF-8 file as ANSI.

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'web-common.ps1')

if ([string]::IsNullOrWhiteSpace($InstallRoot)) { $InstallRoot = $PSScriptRoot }
$InstallRoot = [System.IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
if ([string]::IsNullOrWhiteSpace($DataDir)) { $DataDir = Get-DefaultDataDir $InstallRoot }
$DataDir = [System.IO.Path]::GetFullPath($DataDir).TrimEnd('\')

$currentVersion = Get-CurrentWebVersion $InstallRoot
$currentDir = Join-Path (Join-Path $InstallRoot 'versions') $currentVersion
$targetDir = Join-Path (Join-Path $InstallRoot 'versions') $Version
if (-not (Test-Path -LiteralPath $targetDir -PathType Container)) { throw "target version is not installed: $Version" }
if (-not (Test-Path -LiteralPath (Join-Path $targetDir 'YEEYEEYEE.Web.dll'))) { throw "target version has no YEEYEEYEE.Web.dll: $Version" }

$currentManifest = $null
$targetManifest = $null
try { $currentManifest = Read-WebManifest $currentDir } catch { }
try { $targetManifest = Read-WebManifest $targetDir } catch { }
$currentFormat = if ($currentManifest -and $null -ne $currentManifest.dataFormat) { [int]$currentManifest.dataFormat } else { $null }
$targetFormat = if ($targetManifest -and $null -ne $targetManifest.dataFormat) { [int]$targetManifest.dataFormat } else { $null }
$sameDataFormat = ($null -ne $currentFormat -and $null -ne $targetFormat -and $currentFormat -eq $targetFormat)

Write-Host ("rollback " + $currentVersion + " -> " + $Version)

$restore = $null
$restoreRecord = $null
if (-not [string]::IsNullOrWhiteSpace($RestoreData)) {
    $restore = [System.IO.Path]::GetFullPath($RestoreData).TrimEnd('\')
    if (-not (Test-Path -LiteralPath $restore -PathType Container)) { throw "data backup not found: $restore" }
    if (-not $ConfirmDataLoss) {
        throw 'refusing to restore data without -ConfirmDataLoss: the backup is older than the current data'
    }
    # Everything below runs before the service is stopped and before any data is
    # moved or replaced. An empty, unmarked, interrupted or altered backup stops
    # the command here, so the running service and its data stay untouched.
    $restoreRecord = Assert-WebBackupComplete $restore
    Write-Host ("backup verified: " + $restoreRecord.fileCount + " files, " + $restoreRecord.totalBytes + " bytes")
    Write-Host ("backup source version: " + $restoreRecord.sourceVersion + ", dataFormat: " + $restoreRecord.dataFormat)
    Write-Host ("backup completed at: " + $restoreRecord.completedAt)
    Write-Host ''
    Write-Host 'WARNING: restoring a data backup returns to the moment the backup was taken.'
    Write-Host 'Chat history, jobs, uploads and project writes made after that moment will not be served.'
    Write-Host 'The current data directory is moved aside first, so it can still be inspected.'
    Write-Host ''
}

if ($null -eq $restore -and -not $sameDataFormat) {
    if (-not $ForceIncompatibleData) {
        throw ('data format differs or is unknown (current ' + $currentFormat + ', target ' + $targetFormat + '). ' +
            'A program-only rollback could start an old version against migrated data. ' +
            'Restore the matching data backup with -RestoreData, or pass -ForceIncompatibleData if you are certain no migration occurred.')
    }
    Write-Host 'WARNING: forcing a program rollback although the data format differs or is unknown.'
}

$null = Stop-WebService $InstallRoot $DataDir
Write-Host 'web service stopped'

if ($null -ne $restore) {
    $backupsDir = Join-Path $InstallRoot 'backups'
    New-Item -ItemType Directory -Force -Path $backupsDir | Out-Null
    $aside = Join-Path $backupsDir ('data-before-rollback-' + (Get-Date).ToString('yyyyMMddHHmmss'))
    Write-Host ("moving current data aside: " + $aside)
    Move-Item -LiteralPath $DataDir -Destination $aside
    New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
    Copy-WebBackupData $restore $DataDir $restoreRecord
    Write-Host ("data restored from " + $restore + " (verified against the completion record)")
    Write-Host ("writes made after that backup are preserved only in " + $aside)
}

Set-CurrentWebVersion $InstallRoot $Version
$health = if (-not [string]::IsNullOrWhiteSpace($HealthUrl)) { $HealthUrl }
          elseif ($Port -gt 0) { "http://127.0.0.1:$Port/health" }
          else { 'http://localhost:5000/health' }
$log = Join-Path (Join-Path $InstallRoot 'logs') ("web-" + $Version + "-" + (Get-Date).ToString('yyyyMMddHHmmss') + '.log')

try {
    $null = Start-WebBackground $InstallRoot $DataDir $Version $Port $log
} catch {
    Write-Host ('start failed: ' + $_.Exception.Message)
    Write-FilteredLogTail $log 40
    Write-FilteredLogTail ($log + '.err') 40
    throw ('rollback target did not start; the service is stopped. Log: ' + $log)
}
if (-not (Wait-WebHealth $health $HealthTimeoutSeconds)) {
    $null = Stop-WebService $InstallRoot $DataDir
    Write-FilteredLogTail $log 40
    Write-FilteredLogTail ($log + '.err') 40
    throw ('rollback target did not become healthy; the service is stopped. Log: ' + $log)
}

Write-Host ("rollback complete: " + $Version)
Write-Host ("healthy: " + $health)
if ($null -ne $restore) { Write-Host 'data was restored from backup; confirm login, projects, chat and attachments.' }
