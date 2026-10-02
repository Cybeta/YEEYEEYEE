namespace YEEYEEYEE.Desktop;

/// <summary>项目内资产的可移植引用与文件系统解析。</summary>
public static class AssetStore
{
    private const string Scheme = "asset://";

    public static string Directory
    {
        get
        {
            var configured = EnvCompat.Get("ASSET_DIR");
            if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured.Trim());
            return AppPaths.Combine("assets");
        }
    }

    public static string EnsureDirectory()
    {
        var directory = Directory;
        System.IO.Directory.CreateDirectory(directory);
        return directory;
    }

    public static string ToReference(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        if (path.StartsWith(Scheme, StringComparison.Ordinal)) return path;
        if (!Path.IsPathRooted(path)) return path;
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(Directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return Scheme + Path.GetFileName(full);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { }
        return path;
    }

    public static string? Resolve(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        var path = reference.StartsWith(Scheme, StringComparison.Ordinal)
            ? Path.Combine(Directory, Path.GetFileName(reference[Scheme.Length..]))
            : reference;
        return File.Exists(path) ? path : null;
    }

    public static bool Exists(string? reference) => Resolve(reference) is not null;

    public static bool MoveToRecycleBin(string? reference)
    {
        var path = Resolve(reference);
        if (path is null) return false;
        try { File.Delete(path); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    public static void Normalize(WorkflowCanvasState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        foreach (var node in state.Nodes)
        {
            foreach (var attachment in node.Attachments) attachment.Reference = ToReference(attachment.Reference);
            foreach (var record in node.GenerationHistory) record.Output = ToReference(record.Output);
        }
        foreach (var attachment in EntityAssets.AllAttachments(state))
            attachment.Reference = ToReference(attachment.Reference);
    }
}
