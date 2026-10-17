using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YEEYEEYEE.Desktop;

/// <summary>下载并解压好的更新包。</summary>
public sealed record UpdatePackage(string Version, string PackagePath, string StagingDirectory, long Size)
{
    /// <summary>解压出来的主程序文件名（脚本要按它启动）。</summary>
    public string ExecutableName => Path.GetFileName(Environment.ProcessPath ?? "YEEYEEYEE.Desktop.Avalonia.exe");
}

/// <summary>
/// 「刚更新过」的标记：应用退出前写下，重启后读到就说明这次替换成了。
/// 带上前一版版本号是为了能判断**到底有没有生效**——脚本换了但程序还是旧版本时，要如实说没成功。
/// </summary>
public sealed record PendingUpdateInfo(string FromVersion, string ToVersion, string Notes, string HtmlUrl);

/// <summary>
/// 上次替换脚本的执行结果（脚本写的，应用重启后来读）。
///
/// 这里的 <see cref="JsonPropertyName"/> **不是装饰**：写这份文件的是替换脚本，键名是 PowerShell 里
/// 那套短名（<c>ok</c> / <c>from</c> / <c>to</c> / <c>error</c> / <c>logPath</c>），而 C# 侧属性名不是。
/// 不加映射的话反序列化会全部落空——成功被读成失败，且失败原因是空字符串，报错时报不出东西。
/// </summary>
public sealed record UpdateApplyResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("from")] string FromVersion,
    [property: JsonPropertyName("to")] string ToVersion,
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("logPath")] string LogPath);

/// <summary>
/// 应用内一键升级：把发行版里的 zip 下载到临时目录、解压、写一个替换脚本，然后由脚本在应用退出后
/// **换掉整个程序目录**并重新启动。
///
/// 为什么必须靠外部脚本：正在运行的 exe 与已加载的 dll 在 Windows 上不能被覆盖，所以「自己换掉自己」
/// 只能把实际动作交给一个已经退出的进程之外的东西。做法是应用把脚本以后台方式拉起来、自己退出，
/// 脚本等进程真的消失后再动文件。
///
/// 三条安全前提（缺一条就不做自替换，退回「打开下载页」）：
/// 1. 程序目录**可写**——装在 Program Files 或任何只读位置时自替换必然失败；
/// 2. 发行版里有 **zip 包**；
/// 3. 解压结果里**真的有主程序**——否则换了就是把程序目录搬空。
/// </summary>
public static class UpdateInstaller
{
    /// <summary>重启后要看的「刚更新完」标记。</summary>
    public static string MarkerPath => AppPaths.ResolveAppFile("pending-update.json");

    /// <summary>替换脚本写下的执行结果。</summary>
    public static string ResultPath => AppPaths.ResolveAppFile("last-update.json");

    /// <summary>更新包与脚本的临时工作目录（按版本分目录，便于重复尝试时不留混乱）。</summary>
    public static string WorkspaceFor(string version) =>
        Path.Combine(Path.GetTempPath(), "yeeeyee-update", SanitizeVersion(version));

    public static PendingUpdateInfo? ReadMarker()
    {
        try
        {
            if (!File.Exists(MarkerPath)) return null;
            return JsonSerializer.Deserialize<PendingUpdateInfo>(File.ReadAllText(MarkerPath));
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void WriteMarker(PendingUpdateInfo info) => WriteJson(MarkerPath, info);

    public static void ClearMarker()
    {
        try { if (File.Exists(MarkerPath)) File.Delete(MarkerPath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public static UpdateApplyResult? ReadLastResult()
    {
        try
        {
            if (!File.Exists(ResultPath)) return null;
            return JsonSerializer.Deserialize<UpdateApplyResult>(File.ReadAllText(ResultPath));
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void ClearLastResult()
    {
        try { if (File.Exists(ResultPath)) File.Delete(ResultPath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// 下载更新包并解压。进度用 0~1 报出来；服务端没给 Content-Length 时只在结束时报 1。
    /// 解压后**校验目录里真的有主程序**，没有就抛——这种包换了等于毁掉安装目录。
    /// </summary>
    public static async Task<UpdatePackage> DownloadAsync(
        HttpClient http,
        ReleaseAsset asset,
        string version,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var workspace = WorkspaceFor(version);
        if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
        Directory.CreateDirectory(workspace);

        var packagePath = Path.Combine(workspace, asset.Name);
        using (var response = await http.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? asset.Size;

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = File.Create(packagePath);

            var buffer = new byte[81920];
            long copied = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                copied += read;
                if (total > 0) progress?.Report(Math.Clamp((double)copied / total, 0, 1));
            }
            progress?.Report(1);
        }

        var staging = Path.Combine(workspace, "staging");
        Directory.CreateDirectory(staging);
        ZipFile.ExtractToDirectory(packagePath, staging, overwriteFiles: true);
        staging = Flatten(staging);

        var executable = Path.GetFileName(Environment.ProcessPath ?? string.Empty);
        if (executable.Length == 0 || !File.Exists(Path.Combine(staging, executable)))
        {
            throw new InvalidOperationException(
                $"更新包里没有主程序 {executable}，解压结果不能用：{staging}");
        }

        return new UpdatePackage(version, packagePath, staging, new FileInfo(packagePath).Length);
    }

    /// <summary>
    /// 写替换脚本并**后台拉起**它。调用方紧接着要退出应用——脚本会等这个进程消失再动文件。
    /// 返回脚本路径，便于在失败提示里告诉用户日志在哪。
    /// </summary>
    public static string Launch(UpdatePackage package)
    {
        var target = AppPaths.ProgramRoot;
        var exeName = Path.GetFileName(Environment.ProcessPath ?? "YEEYEEYEE.Desktop.Avalonia.exe");
        if (exeName.Length == 0) exeName = "YEEYEEYEE.Desktop.Avalonia.exe";

        var scriptPath = Path.Combine(Path.GetDirectoryName(package.StagingDirectory)!, "apply-update.ps1");
        File.WriteAllText(scriptPath, SwapScript, new System.Text.UTF8Encoding(false));

        var start = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(scriptPath)!
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(scriptPath);
        start.ArgumentList.Add("-TargetDir");
        start.ArgumentList.Add(target);
        start.ArgumentList.Add("-SourceDir");
        start.ArgumentList.Add(package.StagingDirectory);
        start.ArgumentList.Add("-ExeName");
        start.ArgumentList.Add(exeName);
        start.ArgumentList.Add("-WaitPid");
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        start.ArgumentList.Add("-ResultPath");
        start.ArgumentList.Add(ResultPath);
        start.ArgumentList.Add("-FromVersion");
        start.ArgumentList.Add(AppVersion.Text(AppVersion.Current));
        start.ArgumentList.Add("-ToVersion");
        start.ArgumentList.Add(package.Version);

        Process.Start(start);
        return scriptPath;
    }

    /// <summary>解压结果只有一层目录时钻进去（大多数打包工具会套一层版本目录）。</summary>
    private static string Flatten(string directory)
    {
        var entries = Directory.GetFileSystemEntries(directory);
        if (entries.Length == 1 && Directory.Exists(entries[0])) return entries[0];
        return directory;
    }

    private static void WriteJson<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string SanitizeVersion(string version)
    {
        var buffer = new System.Text.StringBuilder();
        foreach (var ch in version) buffer.Append(char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_');
        var text = buffer.ToString().Trim();
        return text.Length == 0 ? "unknown" : text;
    }

    /// <summary>
    /// 替换脚本。**必须只含 ASCII**：它由 PowerShell 5 执行，含中文的 UTF-8 无 BOM 文件会被按 ANSI 读，
    /// 中文字符串会变成乱码甚至语法错误（这个坑本轮已经踩过两次）。
    /// </summary>
    internal const string SwapScript = """
param(
    [Parameter(Mandatory=$true)][string]$TargetDir,
    [Parameter(Mandatory=$true)][string]$SourceDir,
    [Parameter(Mandatory=$true)][string]$ExeName,
    [Parameter(Mandatory=$true)][int]$WaitPid,
    [Parameter(Mandatory=$true)][string]$ResultPath,
    [string]$FromVersion = "",
    [string]$ToVersion = "",
    [int]$LivenessSeconds = 5
)

$ErrorActionPreference = 'Stop'
$log = $ResultPath + '.log'

function Log([string]$text) {
    try { Add-Content -LiteralPath $log -Value ((Get-Date).ToString('HH:mm:ss') + '  ' + $text) } catch { }
}

function Write-Result([bool]$ok, [string]$errorText) {
    $payload = @{
        ok      = $ok
        from    = $FromVersion
        to      = $ToVersion
        error   = $errorText
        logPath = $log
    } | ConvertTo-Json -Compress
    try {
        [System.IO.File]::WriteAllText($ResultPath, $payload, (New-Object System.Text.UTF8Encoding($false)))
    } catch { }
}

# Copy a single user file. The new package normally does not contain these names; if it does,
# only an identical file is accepted. Silently overwriting user data is never allowed.
function Copy-UserFile([string]$source, [string]$target) {
    if (Test-Path -LiteralPath $target) {
        $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
        $targetHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
        if ($sourceHash -eq $targetHash) { return }
        throw "the new package already contains a different file: $target"
    }
    Copy-Item -LiteralPath $source -Destination $target -Force -ErrorAction Stop
}

# Merge a user directory into the new version. A name collision means the package or another
# copy of the data already exists there; stop instead of guessing which one wins.
function Copy-UserTree([string]$source, [string]$target) {
    if (-not (Test-Path -LiteralPath $target)) {
        Copy-Item -LiteralPath $source -Destination $target -Recurse -Force -ErrorAction Stop
        return
    }
    if (-not (Get-Item -LiteralPath $target).PSIsContainer) {
        throw "a file already exists where a directory is expected: $target"
    }
    foreach ($entry in Get-ChildItem -LiteralPath $source -Force) {
        $destination = Join-Path $target $entry.Name
        if ($entry.PSIsContainer) { Copy-UserTree $entry.FullName $destination }
        else { Copy-UserFile $entry.FullName $destination }
    }
}

# Package-shipped defaults (for example provider-art) may share a name with a user-adjusted
# copy. Here the user copy is the one that must survive, so overwrite is intentional.
function Copy-OverlayTree([string]$source, [string]$target) {
    if (-not (Test-Path -LiteralPath $target)) { New-Item -ItemType Directory -Force -Path $target | Out-Null }
    foreach ($entry in Get-ChildItem -LiteralPath $source -Force) {
        $destination = Join-Path $target $entry.Name
        if ($entry.PSIsContainer) { Copy-OverlayTree $entry.FullName $destination }
        else { Copy-Item -LiteralPath $entry.FullName -Destination $destination -Force -ErrorAction Stop }
    }
}

$stamp = (Get-Date).ToString('yyyyMMddHHmmss')
$old = $TargetDir + '.old-' + $stamp
$failed = $TargetDir + '.failed-' + $stamp

# Put the previous installation back exactly as it was. Used for every failure after the
# swap point: the old directory is kept until the new process proves it can stay alive.
function Restore-Previous([string]$reason) {
    try {
        if (Test-Path -LiteralPath $TargetDir) {
            Move-Item -LiteralPath $TargetDir -Destination $failed -ErrorAction Stop
        }
        Move-Item -LiteralPath $old -Destination $TargetDir -ErrorAction Stop
    } catch {
        Log ("ROLLBACK FAILED: " + $_.Exception.Message + " ; previous files are at " + $old)
        Write-Result $false ($reason + " ; rollback failed: " + $_.Exception.Message + " ; previous files are at " + $old)
        exit 1
    }
    Log ("previous version restored: " + $reason)
    Write-Result $false ($reason + " ; previous version restored")
    exit 1
}

try {
    Log "waiting for pid $WaitPid to exit"
    $deadline = (Get-Date).AddSeconds(120)
    while ((Get-Process -Id $WaitPid -ErrorAction SilentlyContinue) -and ((Get-Date) -lt $deadline)) {
        Start-Sleep -Milliseconds 400
    }
    Start-Sleep -Milliseconds 900

    if (-not (Test-Path -LiteralPath (Join-Path $SourceDir $ExeName))) {
        Log "staging is missing $ExeName"
        Write-Result $false "staging is missing $ExeName"
        exit 1
    }
    if (-not (Test-Path -LiteralPath $TargetDir)) {
        Log "target dir not found: $TargetDir"
        Write-Result $false "target dir not found: $TargetDir"
        exit 1
    }

    $probe = Join-Path $TargetDir ('.write-probe-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        [System.IO.File]::WriteAllText($probe, '')
        Remove-Item -LiteralPath $probe -Force
    } catch {
        Log "target dir is not writable: $TargetDir"
        Write-Result $false "target dir is not writable (portable install required): $TargetDir"
        exit 1
    }

    Log "moving $TargetDir -> $old"
    try {
        Move-Item -LiteralPath $TargetDir -Destination $old -ErrorAction Stop
    } catch {
        Log ("cannot move target aside: " + $_.Exception.Message)
        Write-Result $false ("cannot move target aside: " + $_.Exception.Message)
        exit 1
    }

    Log "moving $SourceDir -> $TargetDir"
    try {
        Move-Item -LiteralPath $SourceDir -Destination $TargetDir -ErrorAction Stop
    } catch {
        Log ("swap failed: " + $_.Exception.Message + " ; restoring")
        try { Move-Item -LiteralPath $old -Destination $TargetDir -ErrorAction Stop } catch {
            Log ("RESTORE ALSO FAILED: " + $_.Exception.Message + " ; old files are at " + $old)
            Write-Result $false ("swap failed and restore failed; your files are at " + $old + " : " + $_.Exception.Message)
            exit 1
        }
        Write-Result $false ("swap failed, previous version restored: " + $_.Exception.Message)
        exit 1
    }

    # Carry user data that lives beside a portable install. Configuration and keys normally
    # live in the user config directory (see AppPaths), but old or portable copies can still
    # sit here; losing them would be silent data loss.
    $carryError = ""
    foreach ($item in @('Projects', 'sites')) {
        $from = Join-Path $old $item
        if (-not (Test-Path -LiteralPath $from)) { continue }
        Log ("carrying user data forward: " + $item)
        try { Copy-UserTree $from (Join-Path $TargetDir $item) }
        catch { $carryError = "$item : " + $_.Exception.Message; break }
    }
    if ($carryError.Length -eq 0) {
        $fromArt = Join-Path $old 'provider-art'
        if (Test-Path -LiteralPath $fromArt) {
            Log "carrying user data forward: provider-art"
            try { Copy-OverlayTree $fromArt (Join-Path $TargetDir 'provider-art') }
            catch { $carryError = "provider-art : " + $_.Exception.Message }
        }
    }
    if ($carryError.Length -eq 0) {
        foreach ($file in @('ai-config.json', 'ai-key.bin', 'recent-projects.json')) {
            $from = Join-Path $old $file
            if (-not (Test-Path -LiteralPath $from)) { continue }
            Log ("carrying user data forward: " + $file)
            try { Copy-UserFile $from (Join-Path $TargetDir $file) }
            catch { $carryError = "$file : " + $_.Exception.Message; break }
        }
    }
    if ($carryError.Length -gt 0) {
        Log ("could not carry user data forward: " + $carryError)
        Restore-Previous ("could not carry user data forward (" + $carryError + ")")
    }

    $exePath = Join-Path $TargetDir $ExeName
    Log "starting $exePath"
    $child = $null
    try {
        $child = Start-Process -FilePath $exePath -WorkingDirectory $TargetDir -PassThru -ErrorAction Stop
    } catch {
        Log ("start failed: " + $_.Exception.Message)
        Restore-Previous ("files were replaced but restarting failed: " + $_.Exception.Message)
    }

    if ($LivenessSeconds -gt 0) {
        Start-Sleep -Seconds $LivenessSeconds
        $alive = $null
        if ($child -ne $null) { $alive = Get-Process -Id $child.Id -ErrorAction SilentlyContinue }
        if ($alive -eq $null) {
            Log "the new version exited within $LivenessSeconds seconds"
            Restore-Previous ("the new version exited within " + $LivenessSeconds + " seconds")
        }
    }

    Write-Result $true ""
    Log "update applied"

    if ($LivenessSeconds -gt 0) {
        Remove-Item -LiteralPath $old -Recurse -Force -ErrorAction SilentlyContinue
        Log "done"
    } else {
        Log "liveness check disabled; the previous directory is kept for inspection"
    }
}
catch {
    Log ("unexpected: " + $_.Exception.Message)
    Write-Result $false ("unexpected failure: " + $_.Exception.Message)
    exit 1
}
""";
}
