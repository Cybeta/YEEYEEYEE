namespace DreamForge.Desktop;

public sealed record StorageUsage(string Path, int FileCount, long Bytes)
{
    public string Display => $"{Path}（{FileCount} 个文件，{Format(Bytes)}）";

    public static string Format(long bytes) =>
        bytes >= 1024L * 1024 * 1024 ? $"{bytes / 1024d / 1024 / 1024:F2} GB"
        : bytes >= 1024L * 1024 ? $"{bytes / 1024d / 1024:F2} MB"
        : bytes >= 1024 ? $"{bytes / 1024d:F1} KB"
        : $"{bytes} B";
}

public sealed record AssetReference(string FileName, long Bytes, int ReferenceCount, IReadOnlyList<string> ReferencedBy);

/// <summary>
/// 本地存储维护：统计各处占用，列出资产引用关系，并清理不再被任何画布引用的图片资产。
/// </summary>
public static class StorageMaintenance
{
    /// <summary>未保存到画布库的草稿画布（项目根文件夹下的 last-canvas.json）。</summary>
    public static string DraftCanvasPath => AppPaths.Combine("last-canvas.json");

    public static string ConfigPath => AiProviderSettings.ConfigFilePath;

    public static string JobDatabasePath => DesktopExecutionHost.JobDatabasePath;

    public static StorageUsage Measure(string path, string searchPattern = "*")
    {
        try
        {
            if (File.Exists(path))
            {
                var file = new FileInfo(path);
                return new StorageUsage(path, 1, file.Length);
            }
            if (!Directory.Exists(path)) return new StorageUsage(path, 0, 0);
            var files = Directory.EnumerateFiles(path, searchPattern, SearchOption.TopDirectoryOnly).ToArray();
            return new StorageUsage(path, files.Length, files.Sum(file => new FileInfo(file).Length));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new StorageUsage(path, 0, 0);
        }
    }

    /// <summary>列出资产目录中的图片，并标注每张图被多少个画布引用、被哪些画布引用。</summary>
    public static IReadOnlyList<AssetReference> ListAssets(IEnumerable<(string Title, WorkflowCanvasState Canvas)> canvases)
    {
        var snapshot = canvases.ToArray();
        if (!Directory.Exists(AssetStore.Directory)) return Array.Empty<AssetReference>();

        var assets = new List<AssetReference>();
        foreach (var path in Directory.EnumerateFiles(AssetStore.Directory))
        {
            if (!IsImageFile(path)) continue;
            var fileName = Path.GetFileName(path);
            var referencedBy = new List<string>();
            var count = 0;
            foreach (var (title, canvas) in snapshot)
            {
                var matches = CanvasPackage.CollectReferences(canvas)
                    .Count(reference => string.Equals(
                        Path.GetFileName(reference["asset://".Length..]),
                        fileName,
                        StringComparison.OrdinalIgnoreCase));
                if (matches == 0) continue;
                count += matches;
                referencedBy.Add(title);
            }
            assets.Add(new AssetReference(fileName, new FileInfo(path).Length, count, referencedBy));
        }
        return assets.OrderByDescending(asset => asset.Bytes).ToArray();
    }

    /// <summary>统计某个资产引用在给定画布集合中出现的次数（节点预览与生成历史都算引用）。</summary>
    public static int CountReferences(string reference, IEnumerable<WorkflowCanvasState> canvases)
    {
        if (string.IsNullOrWhiteSpace(reference)) return 0;
        var name = Path.GetFileName(reference.Replace("asset://", string.Empty, StringComparison.Ordinal));
        var count = 0;
        foreach (var canvas in canvases)
            count += CanvasPackage.CollectReferences(canvas)
                .Count(candidate => string.Equals(
                    Path.GetFileName(candidate.Replace("asset://", string.Empty, StringComparison.Ordinal)),
                    name,
                    StringComparison.OrdinalIgnoreCase));
        return count;
    }

    /// <summary>从候选引用中筛出已无任何画布引用的文件。</summary>
    public static IReadOnlyList<string> FindUnreferenced(IEnumerable<string> candidates, IEnumerable<WorkflowCanvasState> canvases)
    {
        var snapshot = canvases.ToArray();
        return candidates
            .Where(reference => reference.StartsWith("asset://", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(reference => CountReferences(reference, snapshot) == 0)
            .ToArray();
    }

    /// <summary>
    /// 把资产目录中未被任何画布引用的图片移入回收站（不永久删除）。
    /// 只处理图片扩展名，避免误删用户放入的其它文件。
    /// </summary>
    public static (int Moved, long Freed) RecycleUnreferencedAssets(
        WorkflowCanvasState current,
        IEnumerable<WorkflowCanvasState> savedCanvases)
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var canvas in savedCanvases.Append(current))
            foreach (var reference in CanvasPackage.CollectReferences(canvas))
                referenced.Add(Path.GetFileName(reference["asset://".Length..]));

        var directory = AssetStore.Directory;
        if (!Directory.Exists(directory)) return (0, 0);

        var moved = 0;
        long freed = 0;
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            if (!IsImageFile(path)) continue;
            if (referenced.Contains(Path.GetFileName(path))) continue;
            var size = new FileInfo(path).Length;
            if (!AssetStore.MoveToRecycleBin("asset://" + Path.GetFileName(path))) continue;
            moved++;
            freed += size;
        }
        return (moved, freed);
    }

    private static bool IsImageFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }
}
