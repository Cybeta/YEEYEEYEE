namespace DreamForge.Desktop;

public static class AppPaths
{
    private static string? projectRoot;
    private static ProjectContext? currentProject;

    // 应用级配置与程序一起部署，项目级文件则写入当前项目根目录。
    public static string ProgramRoot => AppContext.BaseDirectory.TrimEnd(
        Path.DirectorySeparatorChar,
        Path.AltDirectorySeparatorChar);

    public static string Root => projectRoot ?? throw new InvalidOperationException("尚未打开项目。");
    public static ProjectContext CurrentProject => currentProject ?? throw new InvalidOperationException("尚未打开项目。");

    public static void UseProject(ProjectContext project)
    {
        projectRoot = project.RootPath;
        currentProject = project;
        Directory.CreateDirectory(projectRoot);
    }

    public static string Combine(string name) => Path.Combine(Root, name);

    public static string CombineProgram(string name) => Path.Combine(ProgramRoot, name);

    public static string UserProjectsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "DreamForge",
        "Projects");

    public static bool CanWriteProgramRoot()
    {
        var probe = Path.Combine(ProgramRoot, $".write-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
    }

    public static string DefaultProjectsRoot => GetWritableProjectsRoot();

    public static string GetWritableProjectsRoot()
    {
        var candidates = new[]
        {
            Path.Combine(ProgramRoot, "Projects"),
            UserProjectsRoot,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DreamForge", "Projects")
        };

        foreach (var candidate in candidates)
        {
            if (CanWriteDirectory(candidate)) return candidate;
        }

        // Keep the first candidate as the visible default so the create dialog can report the real error.
        return candidates[0];
    }

    public static bool CanWriteDirectory(string directory)
    {
        var probe = Path.Combine(directory, $".write-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    public static string SanitizeDirectoryName(string name)
    {
        var value = string.IsNullOrWhiteSpace(name) ? "未命名项目" : name.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
        return value.Length > 80 ? value[..80] : value;
    }
}
