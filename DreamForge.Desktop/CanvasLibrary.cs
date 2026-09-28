using System.Text.Json;

namespace DreamForge.Desktop;

public sealed record CanvasSummary(string Title, int Revision, DateTimeOffset ModifiedAt, string Path);

/// <summary>
/// 画布库：把画布以 JSON 文件存放在本机目录，支持列出、保存、载入与删除。
/// 默认位于项目根文件夹，可用 DREAMFORGE_CANVAS_DIR 覆盖。
/// </summary>
public static class CanvasLibrary
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    public static string Directory
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("DREAMFORGE_CANVAS_DIR");
            if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
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
                summaries.Add(new CanvasSummary(
                    string.IsNullOrWhiteSpace(state.Title) ? Path.GetFileNameWithoutExtension(path) : state.Title,
                    state.Revision,
                    File.GetLastWriteTimeUtc(path),
                    path));
            }
            return summaries.OrderByDescending(summary => summary.ModifiedAt).ToArray();
        }
        catch (IOException) { return Array.Empty<CanvasSummary>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<CanvasSummary>(); }
    }

    /// <summary>保存画布；existingPath 为空时按标题命名写入库目录，返回最终文件路径。</summary>
    public static string Save(RecentCanvasState state, string? existingPath)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = string.IsNullOrWhiteSpace(existingPath)
            ? Path.Combine(Directory, SanitizeFileName(state.Title) + ".json")
            : existingPath;
        File.WriteAllText(path, JsonSerializer.Serialize(state, Options));
        return path;
    }

    public static bool TryLoad(string path, out RecentCanvasState? state)
    {
        state = null;
        try
        {
            if (!File.Exists(path)) return false;
            state = JsonSerializer.Deserialize<RecentCanvasState>(File.ReadAllText(path));
            return state is not null;
        }
        catch (JsonException) { return false; }
        catch (IOException) { return false; }
    }

    /// <summary>按标题查找已存在的画布文件；excludePath 用于排除自身，返回冲突文件路径。</summary>
    public static string? FindByTitle(string title, string? excludePath = null)
    {
        var target = Path.Combine(Directory, SanitizeFileName(title) + ".json");
        if (!File.Exists(target)) return null;
        return string.IsNullOrWhiteSpace(excludePath) || !string.Equals(target, excludePath, StringComparison.OrdinalIgnoreCase)
            ? target
            : null;
    }

    /// <summary>
    /// 修改画布标题并把文件同步重命名为标题，返回新的文件路径。
    /// 目标已存在同名画布且不允许覆盖时抛出 <see cref="IOException"/>。
    /// </summary>
    public static string? Rename(string path, string newTitle, bool overwrite = false)
    {
        if (!TryLoad(path, out var state) || state is null) return null;
        var updated = state with { Title = newTitle };
        var target = Path.Combine(Directory, SanitizeFileName(newTitle) + ".json");
        var isSameFile = string.Equals(target, path, StringComparison.OrdinalIgnoreCase);
        if (!isSameFile && File.Exists(target) && !overwrite)
            throw new IOException($"画布库中已存在同名画布：{Path.GetFileName(target)}");
        File.WriteAllText(target, JsonSerializer.Serialize(updated, Options));
        if (!isSameFile && File.Exists(path))
            File.Delete(path);
        return target;
    }

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

    private static string WorkspacePath => AppPaths.Combine("workspace.json");

    /// <summary>读取上次打开的画布路径；文件已不存在时返回 null。</summary>
    public static string? LoadCurrentCanvasPath()
    {
        try
        {
            if (!File.Exists(WorkspacePath)) return null;
            var state = JsonSerializer.Deserialize<WorkspaceState>(File.ReadAllText(WorkspacePath));
            return state?.CurrentCanvasPath is { Length: > 0 } path && File.Exists(path) ? path : null;
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    /// <summary>记录当前画布路径，使画布绑定可以跨重启保留。</summary>
    public static void SaveCurrentCanvasPath(string? path)
    {
        try
        {
            var directory = Path.GetDirectoryName(WorkspacePath);
            if (!string.IsNullOrWhiteSpace(directory)) System.IO.Directory.CreateDirectory(directory);
            File.WriteAllText(WorkspacePath, JsonSerializer.Serialize(new WorkspaceState(path), Options));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private sealed record WorkspaceState(string? CurrentCanvasPath);
}
