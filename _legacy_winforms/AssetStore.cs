namespace DreamForge.Desktop;

/// <summary>
/// 本地图片资产的位置解析：画布中保存可移植引用（asset://文件名），运行时再解析为本机绝对路径，
/// 避免把某一台机器的绝对路径写进画布文件。
/// </summary>
public static class AssetStore
{
    private const string Scheme = "asset://";

    /// <summary>资产目录：默认在项目根文件夹，可在“设置”中或通过 DREAMFORGE_ASSET_DIR 指定。</summary>
    public static string Directory
    {
        get
        {
            var configured = AiProviderSettings.Load().AssetDirectory;
            if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
            return AppPaths.Combine("assets");
        }
    }

    public static string EnsureDirectory()
    {
        var directory = Directory;
        System.IO.Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>把绝对路径转换为可移植引用；不在资产目录内的路径原样返回。</summary>
    public static string ToReference(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        if (path.StartsWith(Scheme, StringComparison.Ordinal)) return path;
        if (!Path.IsPathRooted(path)) return path;
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(Directory);
            if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return Scheme + Path.GetFileName(full);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // 非法路径原样返回，由解析阶段判定为不可用。
        }
        return path;
    }

    /// <summary>解析引用为本机可读路径；文件不存在时返回 null。</summary>
    public static string? Resolve(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        var path = reference.StartsWith(Scheme, StringComparison.Ordinal)
            ? Path.Combine(Directory, Path.GetFileName(reference[Scheme.Length..]))
            : reference;
        return File.Exists(path) ? path : null;
    }

    public static bool Exists(string? reference) => Resolve(reference) is not null;

    /// <summary>
    /// 把资产文件移入回收站（不永久删除，由用户自行清空回收站）。
    /// 卷上不可回收的情况下系统可能直接删除，这是 Windows 自身行为。
    /// </summary>
    public static bool MoveToRecycleBin(string? reference)
    {
        var path = Resolve(reference);
        if (path is null) return false;
        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>把已保存画布中的绝对路径规范化为可移植引用，便于跨机器打开。</summary>
    public static void Normalize(WorkflowCanvasState state)
    {
        foreach (var node in state.Nodes)
        {
            foreach (var attachment in node.Attachments)
                attachment.Reference = ToReference(attachment.Reference);
            foreach (var record in node.GenerationHistory)
                record.Output = ToReference(record.Output);
        }
        foreach (var attachment in EntityAssets.AllAttachments(state))
            attachment.Reference = ToReference(attachment.Reference);
    }
}
