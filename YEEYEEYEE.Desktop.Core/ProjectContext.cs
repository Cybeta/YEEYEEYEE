using System.Text.Json;

namespace YEEYEEYEE.Desktop;

public enum ProjectType
{
    Video,
    Image,
    Novel,
    Other
}

public sealed class ProjectDescriptor
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "未命名项目";
    public ProjectType Type { get; set; } = ProjectType.Other;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastOpenedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ProjectContext
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private ProjectContext(string rootPath, ProjectDescriptor descriptor)
    {
        RootPath = rootPath;
        Descriptor = descriptor;
    }

    public string RootPath { get; }
    public ProjectDescriptor Descriptor { get; }
    public string ProjectFilePath => Path.Combine(RootPath, "project.json");

    public static ProjectContext Create(string parentDirectory, string name, ProjectType type = ProjectType.Other)
    {
        if (string.IsNullOrWhiteSpace(parentDirectory))
            throw new ArgumentException("项目存放位置不能为空。", nameof(parentDirectory));

        var safeName = string.IsNullOrWhiteSpace(name) ? "未命名项目" : name.Trim();
        var parent = Path.GetFullPath(parentDirectory.Trim());
        if (!Directory.Exists(parent))
            throw new DirectoryNotFoundException($"项目存放位置不存在：{parent}");

        var root = Path.Combine(parent, AppPaths.SanitizeDirectoryName(safeName));
        if (Directory.Exists(root) || File.Exists(root))
            throw new IOException($"项目目录已存在：{root}");

        var createdRoot = false;
        try
        {
            Directory.CreateDirectory(root);
            createdRoot = true;
            var context = new ProjectContext(root, new ProjectDescriptor { Name = safeName, Type = type });
            context.EnsureDirectories();
            context.Save();
            return context;
        }
        catch
        {
            if (createdRoot)
            {
                try { Directory.Delete(root, recursive: true); }
                catch { }
            }
            throw;
        }
    }

    public static ProjectContext? Open(string rootPath)
    {
        var root = Path.GetFullPath(rootPath);
        var projectFile = Path.Combine(root, "project.json");
        if (!File.Exists(projectFile)) return null;
        try
        {
            var descriptor = JsonSerializer.Deserialize<ProjectDescriptor>(File.ReadAllText(projectFile));
            if (descriptor is null) return null;
            return new ProjectContext(root, descriptor);
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public void Save()
    {
        Directory.CreateDirectory(RootPath);
        File.WriteAllText(ProjectFilePath, JsonSerializer.Serialize(Descriptor, Options));
    }

    private void EnsureDirectories()
    {
        foreach (var name in new[] { "canvases", "assets", "skills", "plugins" })
            Directory.CreateDirectory(Path.Combine(RootPath, name));
    }
}

public static class ProjectHistory
{
    // 应用级文件：写在用户配置目录，不再写在程序旁边（macOS 的程序在 .app 包内，那里不可写）。
    private static string HistoryPath => AppPaths.ResolveAppFile("recent-projects.json");

    public static IReadOnlyList<string> List()
    {
        var stored = new List<string>();
        try
        {
            if (File.Exists(HistoryPath))
            {
                var values = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(HistoryPath));
                if (values is not null) stored.AddRange(values);
            }
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException) { }

        var valid = stored.Where(IsAccessibleProject).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList();
        try
        {
            var directory = Path.GetDirectoryName(HistoryPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(HistoryPath, JsonSerializer.Serialize(valid));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return valid;
    }

    private static bool IsAccessibleProject(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;
            return File.Exists(Path.Combine(path, "project.json")) && File.GetAttributes(path) >= 0;
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
    }

    public static bool Add(string path)
    {
        try
        {
            var paths = List().Where(item => Directory.Exists(item) && !string.Equals(item, path, StringComparison.OrdinalIgnoreCase)).ToList();
            paths.Insert(0, path);
            var directory = Path.GetDirectoryName(HistoryPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(HistoryPath, JsonSerializer.Serialize(paths.Take(12).ToArray()));
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}
