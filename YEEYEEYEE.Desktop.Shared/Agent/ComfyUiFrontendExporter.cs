using System.Text.Json.Nodes;

namespace YEEYEEYEE.Desktop;

/// <summary>ComfyUI 官方前端 graphToPrompt 导出器。实现由桌面 UI 宿主注入。</summary>
public interface IComfyUiFrontendExporter : IAsyncDisposable
{
    Task<JsonObject> ExportAsync(JsonObject workflow, CancellationToken cancellationToken);
}

/// <summary>为一个动态 FIFO worker 创建独立的官方前端页面。</summary>
public delegate IComfyUiFrontendExporter ComfyUiFrontendExporterFactory(string baseUrl, int workerId);

/// <summary>共享层在没有 Avalonia 宿主时使用的明确失败实现，不回退到 Playwright。</summary>
public sealed class MissingComfyUiFrontendExporter : IComfyUiFrontendExporter
{
    public Task<JsonObject> ExportAsync(JsonObject workflow, CancellationToken cancellationToken)
        => Task.FromException<JsonObject>(new InvalidOperationException(
            "ComfyUI UI 工作流需要 Avalonia NativeWebView 官方前端宿主；当前调用方未提供 exporter factory，不能回退 Playwright。"));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
