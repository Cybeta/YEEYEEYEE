param(
    [string]$Version = "",
    [string]$Runtime = "win-x64",
    [switch]$SelfContained
)

# Builds the portable zip that the in-app updater downloads from a GitHub release.
#
# ASCII-only on purpose: PowerShell 5 reads a BOM-less UTF-8 file as ANSI, so non-ASCII
# comments here would turn into mojibake (and can even break parsing).
#
# Defaults to a framework-dependent build, which matches the documented requirement
# (Windows + .NET 10). Pass -SelfContained to ship the runtime inside the zip.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'YEEYEEYEE.Desktop.Avalonia\YEEYEEYEE.Desktop.Avalonia.csproj'

if (-not (Test-Path $project)) { throw "project not found: $project" }

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$csproj = Get-Content -LiteralPath $project -Raw
    $Version = $csproj.Project.PropertyGroup.Version
    if ([string]::IsNullOrWhiteSpace($Version)) { throw "no <Version> in $project; pass -Version" }
}

$artifacts = Join-Path $root 'artifacts'
$publishDir = Join-Path $artifacts "publish-$Runtime"
$zipPath = Join-Path $artifacts ("yeeeyee-{0}-{1}.zip" -f $Version, $Runtime)

if (Test-Path $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
if (Test-Path $zipPath) { Remove-Item -LiteralPath $zipPath -Force }

$publishArgs = @(
    'publish', $project,
    '-c', 'Release',
    '-r', $Runtime,
    '-o', $publishDir,
    ('-p:Version=' + $Version),
    '-p:PublishSingleFile=false',
    '-p:DebugType=none'
)
if ($SelfContained) { $publishArgs += '--self-contained'; $publishArgs += 'true' }
else { $publishArgs += '--self-contained'; $publishArgs += 'false' }

Write-Output ("publishing " + $Version + " for " + $Runtime + " ...")
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$exe = Join-Path $publishDir 'YEEYEEYEE.Desktop.Avalonia.exe'
if (-not (Test-Path $exe)) { throw "publish output has no main executable: $exe" }

# Ship a README next to the exe so a recipient of the zip knows what this is.
$readme = @(
    'YEEYEEYEE ' + $Version,
    '',
    'Run YEEYEEYEE.Desktop.Avalonia.exe to start.',
    'Requirement: Windows with the .NET 10 runtime.',
    'The in-app updater replaces this folder, so keep it somewhere writable',
    '(do not install under Program Files).'
) -join [Environment]::NewLine
[System.IO.File]::WriteAllText((Join-Path $publishDir 'README.txt'), $readme, (New-Object System.Text.UTF8Encoding($false)))

Write-Output "zipping ..."
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -CompressionLevel Optimal

$size = [math]::Round((Get-Item -LiteralPath $zipPath).Length / 1MB, 2)
Write-Output ""
Write-Output ("zip:  " + $zipPath)
Write-Output ("size: " + $size + " MB")
Write-Output ""
Write-Output "Upload it to the GitHub release, then the in-app updater can use it:"
Write-Output ("  gh release upload v" + $Version + " '" + $zipPath + "'")
