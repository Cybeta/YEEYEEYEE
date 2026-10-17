param(
    [string]$InstallRoot = $PSScriptRoot,
    [string]$DataDir = "",
    [string]$Version = "",
    [int]$Port = 0,
    [string]$ProjectsDirectory = "",
    [string]$HealthUrl = "",
    [string]$LogFile = "",
    [switch]$NoWait,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$RemainingArgs
)

# Starts one version of the YEEYEEYEE Web service with an explicit, fixed data
# directory. The program directory is replaceable; the data directory is not.
#
# ASCII-only on purpose: PowerShell 5 reads a BOM-less UTF-8 file as ANSI.

$ErrorActionPreference = 'Stop'
$InstallRoot = [System.IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
if ([string]::IsNullOrWhiteSpace($DataDir)) { $DataDir = Join-Path $InstallRoot 'data' }
$DataDir = [System.IO.Path]::GetFullPath($DataDir).TrimEnd('\')

if ([string]::IsNullOrWhiteSpace($Version)) {
    $pointer = Join-Path $InstallRoot 'current.txt'
    if (-not (Test-Path -LiteralPath $pointer)) { throw "current.txt not found in $InstallRoot" }
    $Version = (Get-Content -LiteralPath $pointer -Raw).Trim()
}
if ([string]::IsNullOrWhiteSpace($Version)) { throw 'no version to start' }

$versionDir = Join-Path (Join-Path $InstallRoot 'versions') $Version
$dll = Join-Path $versionDir 'YEEYEEYEE.Web.dll'
if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) { throw "version $Version is not installed: $dll" }

$configDir = Join-Path $DataDir 'config'
$assetsDir = Join-Path $DataDir 'assets'
$projectsDir = if ([string]::IsNullOrWhiteSpace($ProjectsDirectory)) { Join-Path $DataDir 'projects' } else { [System.IO.Path]::GetFullPath($ProjectsDirectory) }
$runDir = Join-Path $DataDir 'run'
$logsDir = Join-Path $DataDir 'logs'
foreach ($directory in @($DataDir, $configDir, $assetsDir, $projectsDir, $runDir, $logsDir)) {
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
}

$pidFile = Join-Path $runDir 'web.pid'
if (Test-Path -LiteralPath $pidFile) {
    $previous = $null
    try { $previous = Get-Content -LiteralPath $pidFile -Raw | ConvertFrom-Json } catch { }
    if ($previous -and $previous.pid) {
        $process = Get-Process -Id ([int]$previous.pid) -ErrorAction SilentlyContinue
        if ($process) {
            $command = ''
            try { $command = [string](Get-CimInstance Win32_Process -Filter "ProcessId=$($process.Id)" -ErrorAction SilentlyContinue).CommandLine } catch { }
            if ($command.IndexOf($InstallRoot, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                throw ("this installation already has a running web service (pid " + $process.Id + ")")
            }
        }
    }
    Remove-Item -LiteralPath $pidFile -Force -ErrorAction SilentlyContinue
}

# Every persistent location is pinned here. Relative defaults in appsettings.json
# would otherwise resolve against the version directory and be lost on upgrade.
$env:YEEYEEYEE__CanvasDistPath = Join-Path $versionDir 'canvas-dist'
$env:YEEYEEYEE__WebProjectsDirectory = $projectsDir
$env:YEEYEEYEE__JobDatabasePath = Join-Path $DataDir 'yeeeyee.jobs.db'
$env:YEEYEEYEE__UserDatabasePath = Join-Path $DataDir 'yeeeyee.users.db'
$env:YEEYEEYEE__ChatDatabasePath = Join-Path $DataDir 'yeeeyee.chat.db'
$env:YEEYEEYEE__AssetDirectory = $assetsDir
$env:YEEYEEYEE__WebAiConfigPath = Join-Path $configDir 'web-ai-config.json'
# Standalone mode is opt-in. Only provide a default when the deployment has not
# already chosen a scene path explicitly.
if ([string]::IsNullOrWhiteSpace($env:YEEYEEYEE__WebScenePath)) {
    $env:YEEYEEYEE__WebScenePath = Join-Path $DataDir 'scene.json'
}
# This is the AES-GCM key file used when model/media secrets are saved on Windows.
# Losing or moving it makes every stored secret unreadable.
$env:YEEYEEYEE_CONFIG_HOME = $configDir

if ($Port -gt 0) {
    $env:Kestrel__Endpoints__Http__Url = "http://localhost:$Port"
    $env:ASPNETCORE_URLS = "http://localhost:$Port"
}
if ([string]::IsNullOrWhiteSpace($HealthUrl)) {
    $HealthUrl = if ($Port -gt 0) { "http://127.0.0.1:$Port/health" } else { 'http://localhost:5000/health' }
}

$dotnetArguments = @('"' + $dll + '"')
if ($RemainingArgs) {
    foreach ($argument in $RemainingArgs) {
        if ($argument -match '\s') { $dotnetArguments += ('"' + $argument + '"') }
        else { $dotnetArguments += $argument }
    }
}
$argumentLine = $dotnetArguments -join ' '

$startedAt = (Get-Date).ToString('o')
function Write-PidFile([int]$ProcessId) {
    $payload = @{
        pid         = $ProcessId
        version     = $Version
        startedAt   = $startedAt
        installRoot = $InstallRoot
        dataDir     = $DataDir
        healthUrl   = $HealthUrl
        logFile     = $LogFile
    } | ConvertTo-Json -Compress
    [System.IO.File]::WriteAllText($pidFile, $payload, (New-Object System.Text.UTF8Encoding($false)))
}

Set-Location -LiteralPath $versionDir

if ($NoWait) {
    if ([string]::IsNullOrWhiteSpace($LogFile)) {
        $LogFile = Join-Path $logsDir ("web-" + $Version + "-" + (Get-Date).ToString('yyyyMMddHHmmss') + '.log')
    }
    $errorLog = $LogFile + '.err'
    foreach ($path in @($LogFile, $errorLog)) {
        $parent = Split-Path -Parent $path
        if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
    }
    $process = Start-Process -FilePath 'dotnet' -ArgumentList $argumentLine -WorkingDirectory $versionDir `
        -RedirectStandardOutput $LogFile -RedirectStandardError $errorLog -PassThru -WindowStyle Hidden -ErrorAction Stop
    Write-PidFile $process.Id
    Write-Host ("started pid " + $process.Id + " version " + $Version)
    Write-Host ("data " + $DataDir)
    Write-Host ("log " + $LogFile)
    Write-Host ("health " + $HealthUrl)
    exit 0
}

Write-Host ("starting YEEYEEYEE.Web " + $Version)
Write-Host ("program " + $versionDir)
Write-Host ("data    " + $DataDir)
Write-Host ("health  " + $HealthUrl)

$child = Start-Process -FilePath 'dotnet' -ArgumentList $argumentLine -WorkingDirectory $versionDir -NoNewWindow -PassThru -ErrorAction Stop
Write-PidFile $child.Id
try {
    $child.WaitForExit()
    exit $child.ExitCode
}
finally {
    $current = $null
    try { $current = Get-Content -LiteralPath $pidFile -Raw | ConvertFrom-Json } catch { }
    if ($current -and [int]$current.pid -eq [int]$child.Id) {
        Remove-Item -LiteralPath $pidFile -Force -ErrorAction SilentlyContinue
    }
}
