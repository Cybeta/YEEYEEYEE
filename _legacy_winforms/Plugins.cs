namespace DreamForge.Desktop;

using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

/// <summary>插件可以挂载到界面的位置。</summary>
public enum ContextTarget
{
    /// <summary>画布上的节点。</summary>
    CanvasNode,

    /// <summary>画布空白处。</summary>
    CanvasBlank,

    /// <summary>工作树资源的实体。</summary>
    Entity,

    /// <summary>工作树资源的变体。</summary>
    Variant,

    /// <summary>变体的参考图（图片条目）。</summary>
    VariantImage
}

/// <summary>右键菜单被触发时传给插件的上下文。</summary>
public sealed class ContextInfo
{
    public ContextTarget Target { get; init; }
    public WorkflowCanvasState Canvas { get; init; } = new();
    public WorkflowNode? Node { get; init; }
    public WorkflowEntity? Entity { get; init; }
    public WorkflowEntityVariant? Variant { get; init; }
    public WorkflowAttachment? Attachment { get; init; }
}

/// <summary>插件请求运行技能时的目标。</summary>
public sealed class SkillRunRequest
{
    public WorkflowEntity? Entity { get; init; }
    public WorkflowEntityVariant? Variant { get; init; }
    public WorkflowNode? Node { get; init; }
}

/// <summary>
/// 插件可用的宿主能力。插件只负责往界面上挂入口（右键菜单、左侧图标、窗口），
/// 内容生产一律通过 <see cref="RunSkillAsync"/> 交给技能完成。
/// </summary>
public interface IPluginHost
{
    /// <summary>注册一个右键菜单项。同一位置可注册多项。</summary>
    void AddContextMenuItem(ContextTarget target, string text, Func<ContextInfo, Task> handler);

    /// <summary>在左侧图标栏注册一个图标，点击后展开由插件提供的面板。</summary>
    void AddRailItem(string glyph, string title, Func<Control> paneFactory);

    /// <summary>注册一个插件窗口，可由插件管理界面或插件自身打开。</summary>
    void RegisterWindow(string id, string title, Func<Form> factory);

    /// <summary>打开已注册的插件窗口。</summary>
    void OpenWindow(string id);

    /// <summary>按技能 id 运行技能；找不到技能或未配置模型时返回失败结果。</summary>
    Task<SkillRunResult> RunSkillAsync(string skillId, SkillRunRequest request);

    /// <summary>当前画布状态；插件应通过它读取数据，修改请走宿主提供的操作。</summary>
    WorkflowCanvasState? CurrentCanvas { get; }

    /// <summary>请求宿主刷新画布与工作树资源显示。</summary>
    void RefreshCanvas();
}

/// <summary>插件入口接口。实现类需要一个无参构造函数。</summary>
public interface IDreamForgePlugin
{
    string Id { get; }
    string Name { get; }
    string Version { get; }

    /// <summary>注册扩展点。抛出的异常会被宿主捕获并记为加载失败，不影响其它插件。</summary>
    void Register(IPluginHost host);
}

/// <summary>插件清单：与 plugin.dll 同目录的 plugin.json。</summary>
public sealed class PluginManifest
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";

    /// <summary>入口类型的完整名称，例如 MyPlugin.MyPluginEntry。</summary>
    public string EntryType { get; set; } = string.Empty;

    /// <summary>插件使用的宿主接口版本；与宿主不一致时拒绝加载。</summary>
    public int ApiVersion { get; set; } = PluginLoader.SupportedApiVersion;

    /// <summary>程序集文件名，默认 plugin.dll。</summary>
    public string Assembly { get; set; } = "plugin.dll";
}

/// <summary>插件加载结果。失败时 <see cref="Error"/> 记录原因，界面据此标红。</summary>
public sealed record LoadedPlugin(PluginManifest Manifest, IDreamForgePlugin? Instance, string Directory, string? Error)
{
    public bool IsLoaded => Instance is not null && Error is null;
}

/// <summary>
/// 插件装载：从插件目录读取 plugin.json 与程序集。
/// 单个插件加载失败只记录错误，不会影响宿主或其它插件。
/// </summary>
public static class PluginLoader
{
    public const int SupportedApiVersion = 1;

    /// <summary>插件目录：默认在项目根文件夹，可用 DREAMFORGE_PLUGIN_DIR 覆盖。</summary>
    public static string Directory
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("DREAMFORGE_PLUGIN_DIR");
            return string.IsNullOrWhiteSpace(configured)
                ? AppPaths.Combine("plugins")
                : Path.GetFullPath(configured);
        }
    }

    public static string EnsureDirectory()
    {
        var directory = Directory;
        System.IO.Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>扫描插件目录下的一级子目录，逐个尝试加载。</summary>
    public static List<LoadedPlugin> LoadAll()
    {
        var loaded = new List<LoadedPlugin>();
        if (!System.IO.Directory.Exists(Directory)) return loaded;

        foreach (var directory in System.IO.Directory.EnumerateDirectories(Directory).OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            var manifestPath = Path.Combine(directory, "plugin.json");
            if (!File.Exists(manifestPath)) continue;
            PluginManifest? manifest = null;
            try
            {
                manifest = JsonSerializer.Deserialize<PluginManifest>(
                    File.ReadAllText(manifestPath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException error)
            {
                loaded.Add(new LoadedPlugin(new PluginManifest { Id = Path.GetFileName(directory), Name = Path.GetFileName(directory) }, null, directory, $"plugin.json 解析失败：{error.Message}"));
                continue;
            }

            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id))
            {
                loaded.Add(new LoadedPlugin(new PluginManifest { Id = Path.GetFileName(directory), Name = Path.GetFileName(directory) }, null, directory, "plugin.json 缺少 id"));
                continue;
            }
            if (manifest.ApiVersion != SupportedApiVersion)
            {
                loaded.Add(new LoadedPlugin(manifest, null, directory, $"插件接口版本 {manifest.ApiVersion} 与宿主 {SupportedApiVersion} 不一致，已拒绝加载"));
                continue;
            }

            var assemblyPath = Path.Combine(directory, manifest.Assembly);
            if (!File.Exists(assemblyPath))
            {
                loaded.Add(new LoadedPlugin(manifest, null, directory, $"找不到程序集 {manifest.Assembly}"));
                continue;
            }

            try
            {
                // 每个插件用独立的可卸载上下文加载；宿主程序集共享，保证接口类型一致。
                var context = new PluginLoadContext(assemblyPath);
                var assembly = context.LoadFromAssemblyPath(assemblyPath);
                var type = string.IsNullOrWhiteSpace(manifest.EntryType)
                    ? assembly.GetTypes().FirstOrDefault(candidate => typeof(IDreamForgePlugin).IsAssignableFrom(candidate) && !candidate.IsAbstract)
                    : assembly.GetType(manifest.EntryType);
                if (type is null || Activator.CreateInstance(type) is not IDreamForgePlugin instance)
                {
                    loaded.Add(new LoadedPlugin(manifest, null, directory, "找不到实现 IDreamForgePlugin 的入口类型"));
                    continue;
                }
                loaded.Add(new LoadedPlugin(manifest, instance, directory, null));
            }
            catch (Exception error) when (error is BadImageFormatException or FileLoadException or MissingMethodException or TargetInvocationException or TypeLoadException or InvalidOperationException)
            {
                loaded.Add(new LoadedPlugin(manifest, null, directory, $"加载失败：{error.Message}"));
            }
        }
        return loaded;
    }

    /// <summary>插件上下文：宿主与框架程序集回落到默认上下文，插件自己的依赖从插件目录解析。</summary>
    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver resolver;

        public PluginLoadContext(string pluginPath) : base(isCollectible: true) =>
            resolver = new AssemblyDependencyResolver(pluginPath);

        protected override Assembly? Load(AssemblyName name)
        {
            var shared = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase));
            if (shared is not null) return shared;
            var path = resolver.ResolveAssemblyToPath(name);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
    }
}

/// <summary>
/// 内置示例插件：把「人物一键三视图」技能挂到工作树资源变体与参考图的右键菜单上。
/// 它演示了插件的边界——只负责挂入口，实际内容由技能产出。
/// </summary>
public sealed class ThreeViewMenuPlugin : IDreamForgePlugin
{
    public string Id => "builtin.three-view";
    public string Name => "人物一键三视图（内置示例）";
    public string Version => "1.0.0";

    public void Register(IPluginHost host)
    {
        host.AddContextMenuItem(ContextTarget.Variant, "人物一键三视图", info => RunAsync(host, info));
        host.AddContextMenuItem(ContextTarget.VariantImage, "用这张图生成三视图", info => RunAsync(host, info));
    }

    private static Task RunAsync(IPluginHost host, ContextInfo info) =>
        host.RunSkillAsync("character-three-view", new SkillRunRequest { Entity = info.Entity, Variant = info.Variant });
}
