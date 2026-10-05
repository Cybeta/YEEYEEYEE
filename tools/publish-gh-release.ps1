param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][string]$Name,
    [Parameter(Mandatory = $true)][string]$NotesPath,
    [string]$Asset = "",
    [string]$Repo = "Cybeta/YEEYEEYEE"
)

# Creates (or reuses) the GitHub release for a tag and optionally attaches a file.
#
# ASCII-only on purpose: PowerShell 5 reads a BOM-less UTF-8 file as ANSI, so non-ASCII
# characters here would turn into mojibake. The release notes themselves are read from
# a separate UTF-8 file, so they can be written in any language.
#
# The token comes from the credential git already stores for github.com and is never printed.

$ErrorActionPreference = 'Stop'

$notes = [System.IO.File]::ReadAllText($NotesPath, [System.Text.Encoding]::UTF8)

$env:GIT_TERMINAL_PROMPT = '0'
# Ask git for the stored token by redirecting a request FILE into `git credential fill`.
# A PowerShell pipeline into the native command is not reliable here: piping a string may arrive
# without the terminating blank line (git then fails with "missing protocol field"), and an array
# loses its empty entries. A file redirection always delivers the exact bytes (measured).
$requestFile = [System.IO.Path]::GetTempFileName()
[System.IO.File]::WriteAllText($requestFile, "protocol=https`nhost=github.com`n`n")
$strict = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$line = (cmd /c "git credential fill < `"$requestFile`"") 2>$null |
    Where-Object { $_ -like 'password=*' } | Select-Object -First 1
$ErrorActionPreference = $strict
Remove-Item -LiteralPath $requestFile -Force -ErrorAction SilentlyContinue
if (-not $line) { throw "no stored credential for github.com" }
$token = $line.Substring('password='.Length)
if ([string]::IsNullOrWhiteSpace($token)) { throw "stored credential has an empty token" }

$headers = @{
    Authorization = "token $token"
    Accept        = 'application/vnd.github+json'
    'User-Agent'  = 'yeeeyee-release'
}

$release = $null
try {
    $release = Invoke-RestMethod -Method Get -Uri "https://api.github.com/repos/$Repo/releases/tags/$Tag" -Headers $headers
    Write-Output ("release already exists: " + $release.html_url)
}
catch {
    $status = $null
    if ($_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode }
    if ($status -ne 404) { throw }
}

if ($null -eq $release) {
    $payload = @{
        tag_name   = $Tag
        name       = $Name
        body       = $notes
        draft      = $false
        prerelease = $false
    } | ConvertTo-Json -Depth 5 -Compress
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($payload)
    $release = Invoke-RestMethod -Method Post -Uri "https://api.github.com/repos/$Repo/releases" `
        -Headers $headers -ContentType 'application/json; charset=utf-8' -Body $bytes
    Write-Output ("release created: " + $release.html_url)
}

if ([string]::IsNullOrWhiteSpace($Asset)) { return }
if (-not (Test-Path -LiteralPath $Asset)) { throw "asset not found: $Asset" }

$assetName = [System.IO.Path]::GetFileName($Asset)
$already = @($release.assets) | Where-Object { $_ -and $_.name -eq $assetName }
if ($already) {
    Write-Output ("asset already attached: " + $assetName)
    return
}

$uploadUri = "https://uploads.github.com/repos/$Repo/releases/" + $release.id + "/assets?name=" + [uri]::EscapeDataString($assetName)
$uploaded = Invoke-RestMethod -Method Post -Uri $uploadUri -Headers $headers -ContentType 'application/zip' -InFile $Asset

Write-Output ("asset uploaded: " + $uploaded.name + "  " + [math]::Round($uploaded.size / 1MB, 2) + " MB")
Write-Output ("download: " + $uploaded.browser_download_url)
