namespace YEEYEEYEE.Desktop;

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

    /// <summary>
    /// 用户级配置目录（按平台惯例）：
    /// macOS 为 <c>~/Library/Application Support/YEEYEEYEE</c>，Windows 为 <c>%LOCALAPPDATA%\YEEYEEYEE</c>，
    /// 其它平台为 <c>$XDG_CONFIG_HOME/yeeeyee</c>（未设置时 <c>~/.config/yeeeyee</c>）。
    ///
    /// **为什么不能继续写在程序旁边**：macOS 的程序在 .app 包内，那里不该被写入（签名也会因此失效）；
    /// Windows 上程序目录也可能落在 Program Files 这类只读位置。
    /// 便携部署或自动化测试可用 YEEYEEYEE_CONFIG_HOME 指定这个目录本身。
    /// </summary>
    public static string UserConfigDirectory =>
        EnvCompat.Get("CONFIG_HOME") is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : PreferPopulated(
                Path.Combine(UserConfigRoot, "YEEYEEYEE"),
                Path.Combine(UserConfigRoot, "DreamForge"),
                directory => ConfigFileNames.Any(name => File.Exists(Path.Combine(directory, name))));

    /// <summary>判断「这个配置目录里到底有没有东西」用的文件——有任意一个就算有。</summary>
    private static readonly string[] ConfigFileNames = { "ai-config.json", "recent-projects.json", "ai-key.bin" };

    /// <summary>
    /// 改名后的目录名：**谁真的装着东西就用谁**（新目录优先，但只有个空壳不算数）。
    ///
    /// 为什么不能只判断「目录存在」：改名之后程序自己会顺手把新目录建出来（初始化的副作用），
    /// 于是「新目录存在、旧目录里有配置」这种状态很容易出现，而只按存在与否判断就会选中那个空壳，
    /// 用户看到的是「密钥要我重填、最近项目没了」——文件其实好端端躺在旧目录里。所以这里看的是内容。
    /// 两处都没有内容（首次运行）时才用新目录，准备新建。
    /// </summary>
    internal static string PreferPopulated(string preferred, string legacy, Func<string, bool> hasData)
    {
        if (hasData(preferred)) return preferred;
        if (hasData(legacy)) return legacy;
        return preferred;
    }

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
    /// 需要固定某一份文件（便携部署、自动化测试）用 YEEYEEYEE_CONFIG 显式指定路径。
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

    public static string UserProjectsRoot => ProjectsDirectoryUnder(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));

    /// <summary>
    /// 用户文档下的项目目录：同样是「新名字优先、但看内容」——旧目录里有工程时继续用旧的，
    /// 否则改名会让用户以为自己的工程不见了。
    /// </summary>
    private static string ProjectsDirectoryUnder(string root) => PreferPopulated(
        Path.Combine(root, "YEEYEEYEE", "Projects"),
        Path.Combine(root, "DreamForge", "Projects"),
        HasAnyEntry);

    private static bool HasAnyEntry(string directory)
    {
        try
        {
            return Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

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
            ProjectsDirectoryUnder(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
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
