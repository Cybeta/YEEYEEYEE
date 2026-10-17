using System.Text;
using YEEYEEYEE.Core;
using YEEYEEYEE.Host;

namespace YEEYEEYEE.Web;

internal sealed class WebComfyUiTasks(ComfyUiProvider fallback, ComfyUiConversion conversion, IConfiguration configuration)
    : IExternalTaskProvider, IExternalTaskCanceller, IExternalTaskProgressSource
{
    private const string Prefix = "web-comfy:";

    internal static string Bind(string target, string promptId) =>
        Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(target)) + ":" + promptId;

    private static (string Target, string Prompt)? Parse(string id)
    {
        if (!id.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var split = id.IndexOf(':', Prefix.Length);
        if (split < 0) throw new InvalidOperationException("工作流任务目标无效");
        return (Encoding.UTF8.GetString(Convert.FromBase64String(id[Prefix.Length..split])), id[(split + 1)..]);
    }

    private ComfyUiProvider Provider(HttpClient http) => new(http,
        Path.GetFullPath(LegacyConfig.Text(configuration, "AssetDirectory") ?? Path.Combine(AppContext.BaseDirectory, "assets")));

    public async Task<ExternalTaskUpdate> GetStatusAsync(string externalTaskId, CancellationToken cancellationToken)
    {
        if (Parse(externalTaskId) is not { } route)
            return await fallback.GetStatusAsync(externalTaskId, cancellationToken);
        using var http = conversion.Client(ComfyUiConversion.Target(route.Target));
        var update = await Provider(http).GetStatusAsync(route.Prompt, cancellationToken);
        return update with { ExternalTaskId = externalTaskId };
    }

    public async Task CancelAsync(string externalTaskId, CancellationToken cancellationToken)
    {
        if (Parse(externalTaskId) is not { } route)
        { await fallback.CancelAsync(externalTaskId, cancellationToken); return; }
        using var http = conversion.Client(ComfyUiConversion.Target(route.Target));
        await Provider(http).CancelAsync(route.Prompt, cancellationToken);
    }

    public Task ListenForProgressAsync(string externalTaskId, Action<ExternalTaskUpdate> onUpdate, CancellationToken cancellationToken) =>
        Parse(externalTaskId) is null
            ? fallback.ListenForProgressAsync(externalTaskId, onUpdate, cancellationToken)
            : Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
}
