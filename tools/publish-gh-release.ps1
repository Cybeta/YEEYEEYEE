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
$root = Split-Path -Parent $PSScriptRoot
$head = & git -C $root rev-parse --verify HEAD
if ($LASTEXITCODE -ne 0) { throw 'could not resolve current HEAD' }
$head = ($head -join '').Trim()
if ($head -notmatch '^[0-9a-f]{40,64}$') { throw 'invalid current HEAD commit id' }

# Send the exact request through Process stdin; never invoke cmd or print credentials.
$startInfo = New-Object System.Diagnostics.ProcessStartInfo
$startInfo.FileName = (Get-Command git -CommandType Application -ErrorAction Stop).Source
$startInfo.Arguments = 'credential fill'
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.RedirectStandardInput = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$startInfo.EnvironmentVariables['GIT_TERMINAL_PROMPT'] = '0'
$startInfo.EnvironmentVariables['GCM_INTERACTIVE'] = 'Never'
$process = New-Object System.Diagnostics.Process
$process.StartInfo = $startInfo
$credentialOutput = $null
try {
    if (-not $process.Start()) { throw 'could not start git credential fill' }
    $outputTask = $process.StandardOutput.ReadToEndAsync()
    $errorTask = $process.StandardError.ReadToEndAsync()
    $process.StandardInput.Write("protocol=https`nhost=github.com`n`n")
    $process.StandardInput.Close()
    if (-not $process.WaitForExit(30000)) {
        $process.Kill()
        $process.WaitForExit()
        throw 'git credential fill timed out'
    }
    $credentialOutput = $outputTask.GetAwaiter().GetResult()
    $null = $errorTask.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0) { throw 'git credential fill failed; credential output suppressed' }
    $line = $credentialOutput -split "`r?`n" |
        Where-Object { $_ -like 'password=*' } | Select-Object -First 1
    if (-not $line) { throw 'no stored credential for github.com' }
    $token = $line.Substring('password='.Length)
    if ([string]::IsNullOrWhiteSpace($token)) { throw 'stored credential has an empty token' }
}
finally {
    $credentialOutput = $null
    $line = $null
    $outputTask = $null
    $errorTask = $null
    $process.Dispose()
}

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
        target_commitish = $head
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
