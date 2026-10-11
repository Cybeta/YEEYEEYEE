using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YEEYEEYEE.Desktop;

namespace YEEYEEYEE.Web;

/// <summary>Request-scoped selection; IDs never grant filesystem access outside registered projects.</summary>
internal static class WebProjectApi
{
    private static readonly ConcurrentDictionary<string, WebCanvasMode> Canvases = new();
    private static readonly ConcurrentDictionary<Guid, ProjectContext> Projects = new();
    private static readonly AsyncLocal<WebCanvasMode?> CurrentMode = new();
    public static WebCanvasMode? Current => CurrentMode.Value;
    public static string Scope => Current?.DefaultCanvasPath ?? "";
    public static string ScopedOwner(string owner) => string.IsNullOrEmpty(Scope) ? owner : owner + ":" + Id(Scope);
    private static readonly object Gate = new();
    private static readonly ConcurrentDictionary<string, ProjectCanvasSceneStore> Stores = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, EditLeaseStore> LeaseStores = new(StringComparer.OrdinalIgnoreCase);
    public static ProjectCanvasSceneStore? Store(WebCanvasMode mode) => mode is { IsProject: true, CanvasPath: not null }
        ? Stores.GetOrAdd(mode.CanvasPath, path => new ProjectCanvasSceneStore(path, mode.EntitiesPath)) : null;
    internal static WebCanvasMode? RegisteredCanvas(string path) => Canvases.Values.FirstOrDefault(mode =>
        string.Equals(mode.DefaultCanvasPath, path, StringComparison.OrdinalIgnoreCase));
    internal static T InCanvas<T>(WebCanvasMode mode, Func<T> action)
    {
        var previous = CurrentMode.Value;
        CurrentMode.Value = mode;
        try { return action(); }
        finally { CurrentMode.Value = previous; }
    }
    private static TimeSpan LeaseLifetime = EditLeaseStore.DefaultLifetime;
    public static EditLeaseStore? Leases(WebCanvasMode mode) => mode.CanvasPath is { } path
        ? LeaseStores.GetOrAdd(path, p => new EditLeaseStore(p, LeaseLifetime)) : null;
    private static string Id(string path) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())))[..24];
    private static bool Inside(string path, string root) => Path.GetFullPath(path).StartsWith(
        Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
        StringComparison.OrdinalIgnoreCase);
    private static void CheckPath(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("项目路径不能经过符号链接或目录联接");
    }
    private static ProjectContext OpenProject(string root)
    {
        CheckPath(root);
        CheckPath(Path.Combine(root, "project.json"));
        CheckPath(Path.Combine(root, "project", "entities.json"));
        CheckPath(Path.Combine(root, "canvases"));
        var project = ProjectContext.Open(root);
        if (project is null || project.Descriptor.Id == Guid.Empty || !Directory.Exists(Path.Combine(root, "canvases")) || !File.Exists(Path.Combine(root, "project", "entities.json")))
            throw new IOException("项目描述、画布目录或资源库不存在或无效");
        return project;
    }
    private static IEnumerable<string> CanvasPaths(ProjectContext project) => Directory.EnumerateFiles(Path.Combine(project.RootPath, "canvases"), "*.json")
        .Where(p => !p.EndsWith(".edits.json", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
    private static object Canvas(string path, string entities)
    {
        CheckPath(path);
        var id = Id(path);
        Canvases[id] = new WebCanvasMode(path, entities, true, null);
        return new { id, title = Path.GetFileNameWithoutExtension(path) };
    }
    private static object Describe(ProjectContext project)
    {
        project = OpenProject(project.RootPath);
        if (Projects.TryGetValue(project.Descriptor.Id, out var existing) && !string.Equals(existing.RootPath, project.RootPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("项目 ID 与已登记项目重复");
        var entities = Path.Combine(project.RootPath, "project", "entities.json");
        var canvases = CanvasPaths(project).Select(p => Canvas(p, entities)).ToArray();
        Projects[project.Descriptor.Id] = project;
        return new { id = project.Descriptor.Id, name = project.Descriptor.Name, type = project.Descriptor.Type.ToString(), canvases };
    }
    public sealed record CreateRequest(string Name, string? Type);
    public sealed record OpenRequest(string Path);
    public sealed record CanvasRequest(string? Title);
    private static IResult Error(int status, string code, string message) => Results.Json(new { code, message }, statusCode: status);
    public static void Map(WebApplication app)
    {
        LeaseLifetime = EditLeaseApi.LifetimeOf(app.Configuration);
        var configured = WebCanvasMode.Resolve(app.Configuration);
        var parent = LegacyConfig.Text(app.Configuration, "WebProjectsDirectory");
        if (string.IsNullOrWhiteSpace(parent) && configured.IsProject)
            parent = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(configured.CanvasPath!))!);
        parent = string.IsNullOrWhiteSpace(parent) ? null : Path.GetFullPath(parent);
        void Register(string root)
        {
            try { Describe(OpenProject(root)); }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            { app.Logger.LogWarning("项目未登记：{Reason}", ex.Message); }
        }
        if (configured.IsProject) Register(Path.GetDirectoryName(Path.GetDirectoryName(configured.CanvasPath!)!)!);
        if (parent is not null && Directory.Exists(parent))
        {
            CheckPath(parent);
            foreach (var directory in Directory.EnumerateDirectories(parent))
                if (File.Exists(Path.Combine(directory, "project.json"))) Register(directory);
        }
        app.Use(async (context, next) =>
        {
            var previous = CurrentMode.Value;
            var id = context.Request.Query["canvas"].ToString();
            if (id.Length == 0) id = context.Request.Headers["X-Canvas-Id"].ToString();
            if (id.Length > 0 && !Canvases.ContainsKey(id))
            { await Error(404, "CANVAS_NOT_FOUND", "画布未登记，请重新打开项目").ExecuteAsync(context); return; }
            var selected = id.Length == 0 ? configured : Canvases[id];
            if (selected.DefaultIsProject && context.Request.Path.StartsWithSegments("/api/web") && !context.Request.Path.StartsWithSegments("/api/web/projects"))
            {
                try
                {
                    CheckPath(selected.DefaultCanvasPath!);
                    OpenProject(Path.GetDirectoryName(Path.GetDirectoryName(selected.DefaultCanvasPath!)!)!);
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
                { await Error(503, "PROJECT_CANVAS_UNAVAILABLE", "项目路径无法安全访问").ExecuteAsync(context); return; }
            }
            CurrentMode.Value = selected;
            try { await next(context); }
            finally { CurrentMode.Value = previous; }
        });
        app.MapGet("/api/web/projects", (HttpContext c) =>
        {
            var catalog = new List<object>();
            foreach (var project in Projects.Values)
            {
                try { catalog.Add(Describe(project)); }
                catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }
            }
            return Results.Json(new { projects = catalog, defaultCanvasId = configured.DefaultCanvasPath is null ? null : Id(configured.DefaultCanvasPath), canCreate = parent is not null && Directory.Exists(parent) && WebAccessGuard.Permissions(c).Contains("canvas.edit"), canEdit = WebAccessGuard.Permissions(c).Contains("canvas.edit") });
        });
        app.MapPost("/api/web/projects/open", (HttpContext c, OpenRequest body) =>
        {
            if (!WebAccessGuard.Permissions(c).Contains("canvas.edit")) return Error(403, "CANVAS_EDIT_FORBIDDEN", "需要画布编辑权限");
            if (parent is null) return Error(503, "PROJECT_DIRECTORY_NOT_CONFIGURED", "请配置 WebProjectsDirectory");
            try
            {
                if (string.IsNullOrWhiteSpace(body.Path)) return Error(400, "PROJECT_PATH_INVALID", "项目路径不能为空");
                var root = Path.GetFullPath(Path.IsPathFullyQualified(body.Path) ? body.Path : Path.Combine(parent, body.Path));
                if (!Inside(root, parent)) return Error(400, "PROJECT_PATH_INVALID", "项目必须位于服务端项目目录内");
                lock (Gate) return Results.Json(Describe(OpenProject(root)));
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return Error(400, "PROJECT_OPEN_FAILED", ex.Message); }
        });
        app.MapPost("/api/web/projects", (HttpContext c, CreateRequest body) =>
        {
            if (!WebAccessGuard.Permissions(c).Contains("canvas.edit")) return Error(403, "CANVAS_EDIT_FORBIDDEN", "需要画布编辑权限");
            if (parent is null) return Error(503, "PROJECT_DIRECTORY_NOT_CONFIGURED", "请配置 WebProjectsDirectory");
            if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Length > 80) return Error(400, "PROJECT_NAME_INVALID", "项目名称需为1至80字");
            var directoryName = AppPaths.SanitizeDirectoryName(body.Name);
            if (directoryName.TrimEnd('.') is "" || directoryName.EndsWith('.')) return Error(400, "PROJECT_NAME_INVALID", "项目名称不能以点结尾");
            if (!Enum.TryParse<ProjectType>(body.Type ?? "Other", true, out var type) || !Enum.IsDefined(type)) return Error(400, "PROJECT_TYPE_INVALID", "项目类型无效");
            lock (Gate)
            {
                try
                {
                    CheckPath(parent);
                    var project = ProjectContext.Create(parent, body.Name, type);
                    Directory.CreateDirectory(Path.Combine(project.RootPath, "project"));
                    File.WriteAllText(Path.Combine(project.RootPath, "project", "entities.json"), JsonSerializer.Serialize(new ProjectEntityFile(), new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
                    File.WriteAllBytes(Path.Combine(project.RootPath, "canvases", "画布1.json"), CanvasFileWriter.Serialize(CanvasTabRules.CreateBlank("画布1")));
                    return Results.Json(Describe(project));
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return Error(409, "PROJECT_CREATE_FAILED", ex.Message); }
            }
        });
        app.MapPost("/api/web/projects/{projectId:guid}/canvases", (HttpContext c, Guid projectId, CanvasRequest body) =>
        {
            if (!WebAccessGuard.Permissions(c).Contains("canvas.edit")) return Error(403, "CANVAS_EDIT_FORBIDDEN", "需要画布编辑权限");
            if (!Projects.TryGetValue(projectId, out var project)) return Error(404, "PROJECT_NOT_FOUND", "项目未打开");
            lock (Gate)
            {
                try
                {
                    project = OpenProject(project.RootPath);
                    if (project.Descriptor.Id != projectId) return Error(409, "PROJECT_CHANGED", "项目描述已变化，请重新打开项目");
                    var directory = Path.Combine(project.RootPath, "canvases");
                    var titles = CanvasPaths(project).Select(p => Path.GetFileNameWithoutExtension(p)).ToArray();
                    var title = body.Title?.Trim() ?? CanvasTabRules.NextTitle(titles);
                    if (CanvasTabRules.DescribeNameConflict(title, titles) is not null || title.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || title.EndsWith('.') || title.EndsWith(".edits", StringComparison.OrdinalIgnoreCase))
                        return Error(400, "CANVAS_NAME_INVALID", "画布名称无效或重复");
                    var path = Path.Combine(directory, title + ".json");
                    CheckPath(path);
                    using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                    stream.Write(CanvasFileWriter.Serialize(CanvasTabRules.CreateBlank(title)));
                    return Results.Json(Canvas(path, Path.Combine(project.RootPath, "project", "entities.json")));
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return Error(409, "CANVAS_CREATE_FAILED", ex.Message); }
            }
        });
    }
}
