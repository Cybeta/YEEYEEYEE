namespace YEEYEEYEE.Desktop;

/// <summary>一个执行方候选及其能力匹配结果，供菜单和设置页显示可用/不可用原因。</summary>
public sealed record GenerationProviderCandidate<T>(
    T Provider,
    bool IsMatch,
    string MissingCapabilities,
    string Id = "")
    where T : class
{
    public string Name => Provider switch
    {
        IImageProvider image => image.Name,
        IVideoProvider video => video.Name,
        _ => Provider.GetType().Name
    };

    public string Reason => IsMatch
        ? "能力匹配"
        : Provider is UnconfiguredVideoProvider video
            ? video.ConfigurationDetail
            : MissingCapabilities.Length > 0
                ? MissingCapabilities
                : "未配置";
}

public static class GenerationProviderSelection
{
    public static GenerationProviderCandidate<T> Evaluate<T>(
        T provider,
        IEnumerable<GenerationCapability>? required,
        string id = "")
        where T : class
    {
        var capabilities = provider switch
        {
            IImageProvider image => image.Capabilities,
            IVideoProvider video => video.Capabilities,
            _ => Empty
        };
        var missing = capabilities.MissingDescription(required);
        var configured = provider switch
        {
            IImageProvider image => image.IsConfigured,
            IVideoProvider video => video.IsConfigured,
            _ => true
        };
        return new GenerationProviderCandidate<T>(provider, configured && missing.Length == 0, missing, id);
    }

    public static IReadOnlyList<GenerationProviderCandidate<T>> EvaluateAll<T>(
        IEnumerable<(string Id, T Provider)> providers,
        IEnumerable<GenerationCapability>? required)
        where T : class => providers
            .Select(item => Evaluate(item.Provider, required, item.Id))
            .ToArray();

    private static readonly IReadOnlySet<GenerationCapability> Empty =
        new HashSet<GenerationCapability>();
}
