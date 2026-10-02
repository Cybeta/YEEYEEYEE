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

    /// <summary>
    /// 用户级配置目录（按平台惯例）：macOS 为 <c>~/Library/Application Support/DreamForge</c>，
    /// Windows 为 <c>%LOCALAPPDATA%\DreamForge</c>，其它平台为 <c>$XDG_CONFIG_HOME/dreamforge</c>。
    /// 与 <c>DreamForge.Desktop.Core</c> 里的同名实现保持一致（本工程是尚未删掉的旧端，两份都要能编过）。
    /// </summary>
    public static string UserConfigDirectory =>
        Environment.GetEnvironmentVariable("DREAMFORGE_CONFIG_HOME") is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(UserConfigRoot, "DreamForge");

    private static string UserConfigRoot
    {
        get
        {
            if (OperatingSystem.IsMacOS())
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library",
                    "Application Support");
            if (OperatingSystem.IsWindows())
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrWhiteSpace(local)) return local;
            }

            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (!string.IsNullOrWhiteSpace(xdg)) return xdg;

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrWhiteSpace(home) ? ProgramRoot : Path.Combine(home, ".config");
        }
    }

    /// <summary>
    /// 应用级文件的读写位置：用户配置目录优先，程序目录只作旧版本残留或便携部署的回退，
    /// 旧位置的文件会在首次读取时搬进用户配置目录。规则详见
    /// <c>DreamForge.Desktop.Core/AppPaths.cs</c> 的同名方法。
    /// </summary>
    public static string ResolveAppFile(string fileName, bool migrateFromProgramRoot = true)
    {
        var user = Path.Combine(UserConfigDirectory, fileName);
        if (File.Exists(user)) return user;

        var portable = Path.Combine(ProgramRoot, fileName);
        if (!File.Exists(portable)) return user;
        if (!migrateFromProgramRoot) return portable;

        try
        {
            Directory.CreateDirectory(UserConfigDirectory);
            File.Move(portable, user);
            return user;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return portable;
        }
    }

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
