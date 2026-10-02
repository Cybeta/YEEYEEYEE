using System.Text.Json;

namespace YEEYEEYEE.Desktop;

/// <summary>最近画布的持久化结构，字段名称与旧桌面版 JSON 保持兼容。</summary>
public sealed record RecentCanvasState
{
    public string Title { get; init; } = "未命名画布";
    public int Revision { get; init; }
    public string Prompt { get; init; } = string.Empty;
    public string NegativePrompt { get; init; } = string.Empty;
    public int Width { get; init; }
    public int Height { get; init; }
    public int Steps { get; init; }
    public double Cfg { get; init; }
    public string Seed { get; init; } = string.Empty;
    public WorkflowCanvasState Canvas { get; init; } = new();
    public int FormatVersion { get; init; }

    public RecentCanvasState()
    {
    }

    public RecentCanvasState(
        string title,
        int revision,
        string prompt,
        string negativePrompt,
        int width,
        int height,
        int steps,
        double cfg,
        string seed,
        WorkflowCanvasState canvas)
    {
        Title = title;
        Revision = revision;
        Prompt = prompt;
        NegativePrompt = negativePrompt;
        Width = width;
        Height = height;
        Steps = steps;
        Cfg = cfg;
        Seed = seed;
        Canvas = canvas;
    }
}

public sealed record CanvasSummary(string Title, int Revision, DateTimeOffset ModifiedAt, string Path);

/// <summary>跨平台画布 JSON 文件库。</summary>
public static class CanvasLibrary
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    /// <summary>
    /// 画布保存目录。环境变量优先；没有配置时使用当前项目的 canvases 目录；
    /// 没有可用项目时回退到 AppPaths.Combine("canvases")。
    /// </summary>
    public static string Directory
    {
        get
        {
            var configured = EnvCompat.Get("CANVAS_DIR");
            if (!string.IsNullOrWhiteSpace(configured))
                return Path.GetFullPath(configured.Trim());

            try
            {
                // 第 127 轮起 AppPaths / ProjectContext 就住在同一个命名空间里（原先它们被放在
                // YEEYEEYEE.Desktop.Core 下，别处只能靠一个转发壳访问），这里不必再写全限定名。
                var projectRoot = AppPaths.CurrentProject.RootPath;
                if (!string.IsNullOrWhiteSpace(projectRoot))
                    return Path.Combine(projectRoot, "canvases");
            }
            catch (InvalidOperationException)
            {
                // AppPaths.Combine below provides the final compatibility fallback.
            }

            return AppPaths.Combine("canvases");
        }
    }

    public static IReadOnlyList<CanvasSummary> List()
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory)) return Array.Empty<CanvasSummary>();

            var summaries = new List<CanvasSummary>();
            foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
            {
                if (!TryLoad(path, out var state) || state is null) continue;
                try
                {
                    summaries.Add(new CanvasSummary(
                        string.IsNullOrWhiteSpace(state.Title)
                            ? Path.GetFileNameWithoutExtension(path)
                            : state.Title,
                        state.Revision,
                        File.GetLastWriteTimeUtc(path),
                        path));
                }
                catch (IOException)
                {
                    // A file may disappear between enumeration and metadata access.
                }
                catch (UnauthorizedAccessException)
                {
                    // Ignore files that cannot be inspected.
                }
            }

            return summaries.OrderByDescending(item => item.ModifiedAt).ToArray();
        }
        catch (IOException) { return Array.Empty<CanvasSummary>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<CanvasSummary>(); }
    }

    public static bool TryLoad(string path, out RecentCanvasState? state)
    {
        state = null;
        try
        {
            if (!File.Exists(path)) return false;
            state = JsonSerializer.Deserialize<RecentCanvasState>(File.ReadAllText(path), JsonOptions);
            return state is not null;
        }
        catch (JsonException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// 保存画布；existingPath 为空时按标题命名写入库目录，返回最终文件路径。
    ///
    /// 这是画布库的**唯一**保存入口，统一走 <see cref="CanvasSaveService.Save"/>：
    /// 深拷贝 → 迁移 → 校验 → 目标已存在则先备份（备份失败抛 <see cref="CanvasSaveAbortedException"/>
    /// 并中止，不覆盖原文件）→ 临时文件原子替换。标签切换、关闭写回、重命名与命令服务都复用这里，
    /// **不存在旁路的写盘路径**。
    ///
    /// 第 127 轮之前这里有自己的一套「直接序列化 + 临时文件替换」实现，于是同一件事有了两条路径：
    /// 走服务的那条会迁移格式、会备份、会拒绝未知版本，走这里的这条全都不做（测试一直在盯这件事，
    /// 见 `ReworkLibrarySaveUsesFullChain` / `AllSaveEntryPointsShareOnePolicy`）。合并两份分叉时
    /// 以带完整链条的那一份为准。
    /// </summary>
    public static string Save(RecentCanvasState state, string? existingPath = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        System.IO.Directory.CreateDirectory(Directory);
        var path = string.IsNullOrWhiteSpace(existingPath)
            ? PathForTitle(state.Title)
            : existingPath;
        return CanvasSaveService.Save(state, path).Path;
    }

    public static string PathForTitle(string title) =>
        Path.Combine(Directory, SanitizeFileName(title) + ".json");

    public static void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    public static string SanitizeFileName(string title)
    {
        var name = string.IsNullOrWhiteSpace(title) ? "未命名画布" : title.Trim();
        foreach (var invalid in InvalidChars) name = name.Replace(invalid, '_');
        return name.Length > 80 ? name[..80] : name;
    }

    /// <summary>
    /// 修改画布标题并把文件同步重命名为标题，返回新的文件路径。
    /// 目标已存在同名画布且不允许覆盖时抛出 <see cref="IOException"/>。
    /// 覆盖目标前先备份（失败即中止、不覆盖），写入走统一的保存服务（迁移 + 校验 + 原子替换），
    /// 新文件写成功后才删除源文件；任何一步失败都不会让内容丢失。
    ///
    /// 第 127 轮从旧端那份分叉的 CanvasLibrary 里并进来的——它只有旧端有，而测试一直在盯这份行为
    /// （见 `ReworkRenameEntryIsBackedUpAndAtomic`）。并进来的同时补上了这条注释：
    /// 「重命名」不是一个 UI 动作，它跟保存走的是同一条链，所以它必须在这里、而不是在某个窗口里。
    /// </summary>
    public static string? Rename(string path, string newTitle, bool overwrite = false)
    {
        if (!TryLoad(path, out var state) || state is null) return null;
        var updated = state with { Title = newTitle };
        var target = PathForTitle(newTitle);
        var isSameFile = string.Equals(target, path, StringComparison.OrdinalIgnoreCase);
        if (!isSameFile && File.Exists(target) && !overwrite)
            throw new IOException($"画布库中已存在同名画布：{Path.GetFileName(target)}");

        // 统一保存链：写前备份 + 迁移 + 原子替换；失败时目标文件保持原样。
        CanvasSaveService.Save(updated, target);

        if (!isSameFile && File.Exists(path))
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    $"新文件已写入 {Path.GetFileName(target)}，但旧文件 {Path.GetFileName(path)} 无法删除，请手工清理。原因：{error.Message}", error);
            }
        }

        return target;
    }
}
