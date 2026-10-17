param(
    [string]$Version = "",
    [string]$Runtime = "win-x64",
    [switch]$SkipCanvasBuild
)

# Builds the Web service zip used for Windows upgrades.
#
# The zip contains program files only. Users, projects, chat databases, assets,
# generated media, configuration and encryption keys are never packaged.
#
# ASCII-only on purpose: PowerShell 5 reads a BOM-less UTF-8 file as ANSI.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$webProject = Join-Path $root 'YEEYEEYEE.Web\YEEYEEYEE.Web.csproj'
$desktopProject = Join-Path $root 'YEEYEEYEE.Desktop.Avalonia\YEEYEEYEE.Desktop.Avalonia.csproj'
$canvasDir = Join-Path $root 'YEEYEEYEE.Canvas'
$scriptsDir = Join-Path $PSScriptRoot 'web-package'

if (-not (Test-Path -LiteralPath $webProject)) { throw "project not found: $webProject" }
if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$csproj = Get-Content -LiteralPath $desktopProject -Raw
    $Version = $csproj.Project.PropertyGroup.Version
    if ([string]::IsNullOrWhiteSpace($Version)) { throw 'no <Version> in the desktop project; pass -Version' }
}

$artifacts = Join-Path $root 'artifacts'
$publishDir = Join-Path $artifacts ("publish-web-" + $Version)
$zipPath = Join-Path $artifacts ("yeeeyee-web-" + $Version + "-" + $Runtime + ".zip")
if (-not (Test-Path -LiteralPath $artifacts)) { New-Item -ItemType Directory -Force -Path $artifacts | Out-Null }

if (-not $SkipCanvasBuild) {
    Push-Location $canvasDir
    try {
        if (-not (Test-Path -LiteralPath 'node_modules')) {
            Write-Output 'installing canvas dependencies ...'
            & npm ci
            if ($LASTEXITCODE -ne 0) { throw "npm ci failed with exit code $LASTEXITCODE" }
        }
        Write-Output 'building canvas ...'
        & npm run build
        if ($LASTEXITCODE -ne 0) { throw "npm run build failed with exit code $LASTEXITCODE" }
    } finally {
        Pop-Location
    }
}
$canvasDist = Join-Path $canvasDir 'dist'
if (-not (Test-Path -LiteralPath (Join-Path $canvasDist 'index.html'))) { throw "canvas dist not found: $canvasDist" }

if (Test-Path -LiteralPath $publishDir) {
    # Only root-level names count: canvas-dist\assets is a legitimate frontend directory.
    $forbidden = @(Get-ChildItem -LiteralPath $publishDir -Force |
        Where-Object { $_.Name -in @('Projects', 'data', 'config', 'assets', 'yeeeyee.users.db', 'yeeeyee.jobs.db', 'web-ai-config.json') })
    if ($forbidden.Count -gt 0) { throw ('refusing to clear a publish directory that contains user data: ' + $publishDir) }
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }

Write-Output ("publishing web " + $Version + " for " + $Runtime + " ...")
$publishArgs = @(
    'publish', $webProject,
    '-c', 'Release',
    '-r', $Runtime,
    '-o', $publishDir,
    ('-p:Version=' + $Version),
    '-p:PublishSingleFile=false',
    '-p:DebugType=none'
)
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

# Symbols, development settings and anything that resembles installation data
# must never reach a package.
Get-ChildItem -LiteralPath $publishDir -Recurse -File -Filter '*.pdb' | Remove-Item -Force
foreach ($name in @('appsettings.Development.json', 'web-ai-config.json', 'yeeeyee.users.db', 'yeeeyee.jobs.db', 'yeeeyee.chat.db', '.setup-token')) {
    $path = Join-Path $publishDir $name
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
}
foreach ($directory in @('Projects', 'data', 'config', 'assets', 'logs', 'backups')) {
    $path = Join-Path $publishDir $directory
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
}

Write-Output 'copying canvas-dist ...'
Copy-Item -LiteralPath $canvasDist -Destination (Join-Path $publishDir 'canvas-dist') -Recurse -Force

foreach ($script in @('start-web.ps1', 'upgrade-web.ps1', 'rollback-web.ps1', 'install-web.ps1', 'web-common.ps1', 'README-WEB.txt')) {
    $source = Join-Path $scriptsDir $script
    if (-not (Test-Path -LiteralPath $source)) { throw "web package script missing: $source" }
    Copy-Item -LiteralPath $source -Destination (Join-Path $publishDir $script) -Force
}

$mainDll = Join-Path $publishDir 'YEEYEEYEE.Web.dll'
$canvasIndex = Join-Path $publishDir 'canvas-dist\index.html'
if (-not (Test-Path -LiteralPath $mainDll)) { throw "publish output has no YEEYEEYEE.Web.dll" }

$manifest = [ordered]@{
    product   = 'yeeeyee-web'
    version   = $Version
    runtime   = $Runtime
    dataFormat = 1
    builtAt   = (Get-Date).ToUniversalTime().ToString('o')
    sha256    = [ordered]@{
        'YEEYEEYEE.Web.dll'    = (Get-FileHash -LiteralPath $mainDll -Algorithm SHA256).Hash.ToUpperInvariant()
        'canvas-dist/index.html' = (Get-FileHash -LiteralPath $canvasIndex -Algorithm SHA256).Hash.ToUpperInvariant()
    }
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $publishDir 'release-manifest.json') -Encoding UTF8

# Package verification: required files, no symbols, no FFmpeg and no installation data.
$required = @('YEEYEEYEE.Web.dll', 'YEEYEEYEE.Web.runtimeconfig.json', 'appsettings.json', 'canvas-dist\index.html', 'release-manifest.json')
foreach ($relative in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishDir $relative))) { throw "package is missing $relative" }
}
$forbidden = @(Get-ChildItem -LiteralPath $publishDir -Recurse -Force |
    Where-Object {
        $rootLevel = (Split-Path -Parent $_.FullName) -eq $publishDir
        $_.Extension -ieq '.pdb' -or $_.Name -match '(?i)ffmpeg|ai-key|ai-config' -or
        ($rootLevel -and $_.Name -in @('Projects', 'data', 'config', 'assets', 'web-ai-config.json', '.setup-token')) -or
        $_.Name -match '\.(jobs|users|chat)\.db'
    })
if ($forbidden.Count -gt 0) { throw ('package contains forbidden files: ' + (($forbidden | ForEach-Object FullName) -join ', ')) }

$credentialPattern = 'sk-[A-Za-z0-9]{16,}|"ApiKey"\s*:\s*"[^"]{8,}"'
$textFiles = Get-ChildItem -LiteralPath $publishDir -Recurse -File | Where-Object { $_.Extension -in @('.json', '.config', '.txt', '.ps1') }
foreach ($file in $textFiles) {
    $matches = Select-String -LiteralPath $file.FullName -Pattern $credentialPattern -AllMatches -ErrorAction SilentlyContinue
    if ($matches) { throw ('possible credential in package: ' + $file.FullName) }
}

Write-Output 'zipping ...'
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToUpperInvariant()
$size = [math]::Round((Get-Item -LiteralPath $zipPath).Length / 1MB, 2)
Set-Content -LiteralPath ($zipPath + '.sha256') -Value ($hash + '  ' + (Split-Path -Leaf $zipPath)) -Encoding ASCII

Write-Output ''
Write-Output ('version: ' + $Version)
Write-Output ('zip:     ' + $zipPath)
Write-Output ('size:    ' + $size + ' MB')
Write-Output ('sha256:  ' + $hash)
Write-Output ('publish: ' + $publishDir)
