param(
    [Parameter(Mandatory = $true)][string]$InstallRoot,
    [string]$DataDir = "",
    [string]$LegacyDataDir = "",
    [string]$Version = "",
    [switch]$StopLegacy,
    [switch]$Start,
    [int]$Port = 0,
    [int]$HealthTimeoutSeconds = 60,
    [switch]$Force
)

# Installs this package into an explicit version directory and creates (or
# reuses) a fixed data directory. It also migrates an existing installation
# whose data lives beside the program.
#
# ASCII-only on purpose: PowerShell 5 reads a BOM-less UTF-8 file as ANSI.

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'web-common.ps1')

$InstallRoot = [System.IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
if ([string]::IsNullOrWhiteSpace($DataDir)) { $DataDir = Get-DefaultDataDir $InstallRoot }
$DataDir = [System.IO.Path]::GetFullPath($DataDir).TrimEnd('\')

$manifest = Assert-WebPackage $PSScriptRoot
$packageVersion = [string]$manifest.version
if (-not [string]::IsNullOrWhiteSpace($Version) -and $Version -ne $packageVersion) {
    throw "package version is $packageVersion, not the requested $Version"
}
$Version = $packageVersion

$versionsDir = Join-Path $InstallRoot 'versions'
$versionDir = Join-Path $versionsDir $Version
$backupsDir = Join-Path $InstallRoot 'backups'
$logsDir = Join-Path $InstallRoot 'logs'
foreach ($directory in @($InstallRoot, $versionsDir, $DataDir, $backupsDir, $logsDir)) {
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
}

Write-Host ("installing YEEYEEYEE.Web " + $Version)
Write-Host ("install root " + $InstallRoot)
Write-Host ("data         " + $DataDir)

# 1. Program files live in versions/<version>. The files at the install root are
#    stable launchers, never part of a version.
$stamp = (Get-Date).ToString('yyyyMMddHHmmss')
if (Test-Path -LiteralPath $versionDir) {
    if (-not $Force) { throw "version $Version is already installed; pass -Force to replace it" }
    $aside = $versionDir + '.replaced-' + $stamp
    Write-Host ("moving existing version aside: " + $aside)
    Move-Item -LiteralPath $versionDir -Destination $aside
}
New-Item -ItemType Directory -Force -Path $versionDir | Out-Null
Get-ChildItem -LiteralPath $PSScriptRoot -Force | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $versionDir -Recurse -Force
}
$null = Assert-WebPackage $versionDir

foreach ($script in @('start-web.ps1', 'upgrade-web.ps1', 'rollback-web.ps1', 'install-web.ps1', 'web-common.ps1', 'README-WEB.txt')) {
    $source = Join-Path $PSScriptRoot $script
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination (Join-Path $InstallRoot $script) -Force }
}

# 2. Migrate data from an old layout (program and data in the same directory).
$migrated = @()
$legacyFiles = @(
    'yeeeyee.jobs.db', 'yeeeyee.jobs.db-wal', 'yeeeyee.jobs.db-shm',
    'yeeeyee.users.db', 'yeeeyee.users.db-wal', 'yeeeyee.users.db-shm',
    'yeeeyee.chat.db', 'yeeeyee.chat.db-wal', 'yeeeyee.chat.db-shm',
    '.setup-token', 'scene.json'
)
if (-not [string]::IsNullOrWhiteSpace($LegacyDataDir)) {
    $LegacyDataDir = [System.IO.Path]::GetFullPath($LegacyDataDir).TrimEnd('\')
    if (-not (Test-Path -LiteralPath $LegacyDataDir -PathType Container)) { throw "legacy data directory not found: $LegacyDataDir" }

    $running = @(Get-WebRunningProcesses $LegacyDataDir)
    if ($running.Count -gt 0) {
        if (-not $StopLegacy) {
            throw ('the old web service is still running (pid(s): ' + (($running | ForEach-Object ProcessId) -join ', ') +
                '). Stop it first, or pass -StopLegacy to stop only that installation.')
        }
        Write-Host 'stopping the old web service before copying data'
        foreach ($process in $running) { Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue }
        $deadline = (Get-Date).AddSeconds(30)
        while ((Get-Date) -lt $deadline -and @(Get-WebRunningProcesses $LegacyDataDir).Count -gt 0) { Start-Sleep -Milliseconds 300 }
        if (@(Get-WebRunningProcesses $LegacyDataDir).Count -gt 0) { throw 'could not stop the old web service' }
    }

    $existing = @(
        (Test-Path -LiteralPath (Join-Path $DataDir 'yeeeyee.users.db')),
        (Test-Path -LiteralPath (Join-Path $DataDir 'yeeeyee.jobs.db')),
        (Test-Path -LiteralPath (Join-Path $DataDir 'config\ai-key.bin'))
    )
    if (($existing -contains $true) -and -not $Force) {
        throw "the data directory already contains data: $DataDir (pass -Force only if you intend to merge/overwrite)"
    }

    foreach ($name in $legacyFiles) {
        $source = Join-Path $LegacyDataDir $name
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { continue }
        $target = Join-Path $DataDir $name
        if (Test-Path -LiteralPath $target) {
            if ((Get-WebSha256 $source) -ne (Get-WebSha256 $target)) {
                if (-not $Force) { throw "data file already exists with different content: $target" }
                Write-Host ("overwriting existing data file because -Force was given: " + $name)
            } else { continue }
        }
        Copy-Item -LiteralPath $source -Destination $target -Force -ErrorAction Stop
        if ((Get-WebSha256 $source) -ne (Get-WebSha256 $target)) { throw "copy verification failed: $name" }
        $migrated += $name
    }

    $legacyWebConfig = Join-Path $LegacyDataDir 'web-ai-config.json'
    if (Test-Path -LiteralPath $legacyWebConfig -PathType Leaf) {
        $target = Join-Path $DataDir 'config\web-ai-config.json'
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        if ((Test-Path -LiteralPath $target) -and ((Get-WebSha256 $legacyWebConfig) -ne (Get-WebSha256 $target))) {
            if (-not $Force) { throw "web-ai-config.json already exists with different content: $target" }
            Write-Host 'overwriting web-ai-config.json because -Force was given'
        }
        Copy-Item -LiteralPath $legacyWebConfig -Destination $target -Force -ErrorAction Stop
        if ((Get-WebSha256 $legacyWebConfig) -ne (Get-WebSha256 $target)) { throw 'copy verification failed: web-ai-config.json' }
        $migrated += 'web-ai-config.json'
    }

    $directoryMap = @(
        @{ From = 'assets'; To = 'assets' },
        @{ From = 'Projects'; To = 'projects' },
        @{ From = 'config'; To = 'config' },
        @{ From = 'sites'; To = 'config\sites' }
    )
    foreach ($mapping in $directoryMap) {
        $source = Join-Path $LegacyDataDir $mapping.From
        if (-not (Test-Path -LiteralPath $source -PathType Container)) { continue }
        $target = Join-Path $DataDir $mapping.To
        New-Item -ItemType Directory -Force -Path $target | Out-Null
        Copy-MergeTree $source $target
        Assert-TreeCopied $source $target -Hash:($mapping.From -eq 'config')
        $facts = Get-TreeFacts $source
        $migrated += ($mapping.From + ' (' + $facts.Count + ' files, ' + $facts.Bytes + ' bytes)')
    }

    # The old web-ai-config.json may already sit inside config/.
    $nestedWebConfig = Join-Path $LegacyDataDir 'config\web-ai-config.json'
    if (Test-Path -LiteralPath $nestedWebConfig -PathType Leaf) {
        $target = Join-Path $DataDir 'config\web-ai-config.json'
        if (-not (Test-Path -LiteralPath $target)) {
            Copy-Item -LiteralPath $nestedWebConfig -Destination $target -Force
            if ((Get-WebSha256 $nestedWebConfig) -ne (Get-WebSha256 $target)) { throw 'copy verification failed: config/web-ai-config.json' }
            $migrated += 'config/web-ai-config.json'
        }
    }

    Write-Host 'migrated data:'
    if ($migrated.Count -eq 0) { Write-Host '  (nothing found in the legacy directory)' }
    foreach ($item in $migrated) { Write-Host ('  ' + $item) }
    Write-Host ("the old directory is unchanged and can be kept as a recovery copy: " + $LegacyDataDir)
}

Set-CurrentWebVersion $InstallRoot $Version
Write-Host ("current version: " + $Version)

if ($Start) {
    $log = Join-Path $logsDir ("web-" + $Version + "-" + $stamp + '.log')
    Write-Host 'starting the new web service'
    $info = Start-WebBackground $InstallRoot $DataDir $Version $Port $log
    $health = if ($Port -gt 0) { "http://127.0.0.1:$Port/health" } else { 'http://localhost:5000/health' }
    if (-not (Wait-WebHealth $health $HealthTimeoutSeconds)) {
        Write-FilteredLogTail $log 40
        throw ("the new web service did not become healthy: " + $health)
    }
    Write-Host ("healthy: " + $health + " (pid " + $info.pid + ")")
}

Write-Host 'install finished.'
Write-Host 'Keep the previous installation directory until login, projects, chat, attachments and encrypted settings are verified.'
