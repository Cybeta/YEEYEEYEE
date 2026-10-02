namespace DreamForge.Desktop;

public static class AppPaths
{
    private static string? projectRoot;
    private static ProjectContext? currentProject;

    // Application configuration lives beside the deployed program; project files live under the current project root.
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
    /// 用户级配置目录（按平台惯例）：
    /// macOS 为 <c>~/Library/Application Support/DreamForge</c>，Windows 为 <c>%LOCALAPPDATA%\DreamForge</c>，
    /// 其它平台为 <c>$XDG_CONFIG_HOME/dreamforge</c>（未设置时 <c>~/.config/dreamforge</c>）。
    ///
    /// **为什么不能继续写在程序旁边**：macOS 的程序在 .app 包内，那里不该被写入（签名也会因此失效）；
    /// Windows 上程序目录也可能落在 Program Files 这类只读位置。
    /// 便携部署或自动化测试可用 DREAMFORGE_CONFIG_HOME 指定这个目录本身。
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
    /// 应用级文件（<c>ai-config.json</c>、<c>recent-projects.json</c>、密钥文件等）的读写位置。
    ///
    /// 规则：**用户配置目录优先**，程序目录只作为旧版本残留或便携部署的回退。
    /// 旧版本把文件写在程序旁边，这里在首次读取时就地搬进用户配置目录——不搬的话同一个应用
    /// 会留下两份配置各写各的，用户改了其中一份不生效。搬迁失败（目录只读等）就继续用旧位置，
    /// 迁移问题不该让用户丢配置。
    /// 需要固定某一份文件（便携部署、自动化测试）用 DREAMFORGE_CONFIG 显式指定路径。
    /// </summary>
    public static string ResolveAppFile(string fileName, bool migrateFromProgramRoot = true)
    {
        var user = Path.Combine(UserConfigDirectory, fileName);
        if (File.Exists(user)) return user;

        var portable = Path.Combine(ProgramRoot, fileName);
        if (!File.Exists(portable)) return user;      // 两边都没有：新建的文件写用户配置目录
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
