using System.Text.Json;
using YEEYEEYEE.Core;
using YEEYEEYEE.Desktop;
using YEEYEEYEE.Host;

namespace YEEYEEYEE.Web;

internal sealed class WebInvocationExecutor : IInvocationExecutor
{
    private const string VideoTool = "web.text-to-video";
    private readonly IInvocationExecutor imageExecutor;
    private readonly IVideoProvider videoProvider;
    private readonly ComfyUiExecutor comfyUiExecutor;
    private readonly bool comfyUiVideoBackend;

    public WebInvocationExecutor(
        IInvocationExecutor imageExecutor,
        IVideoProvider videoProvider,
        ComfyUiExecutor comfyUiExecutor,
        bool comfyUiVideoBackend)
    {
        this.imageExecutor = imageExecutor;
        this.videoProvider = videoProvider;
        this.comfyUiExecutor = comfyUiExecutor;
        this.comfyUiVideoBackend = comfyUiVideoBackend;
    }

    public async Task<ExecutionOutput> ExecuteAsync(SessionContext session, Invocation invocation, Job job, CancellationToken cancellationToken)
    {
        if (!string.Equals(invocation.Tool, VideoTool, StringComparison.Ordinal))
            return await imageExecutor.ExecuteAsync(session, invocation, job, cancellationToken).ConfigureAwait(false);

        if (comfyUiVideoBackend)
            return await comfyUiExecutor.ExecuteAsync(session, invocation, job, cancellationToken).ConfigureAwait(false);

        var prompt = ReadString(invocation.Inputs, "prompt");
        var model = ReadString(invocation.Inputs, "model");
        var seconds = ReadInt(invocation.Inputs, "seconds");
        var result = await videoProvider.GenerateAsync(new VideoGenerationRequest
        {
            Prompt = prompt,
            Model = model,
            Seconds = seconds
        }, cancellationToken).ConfigureAwait(false);

        if (result.Status != VideoGenerationStatus.Succeeded || string.IsNullOrWhiteSpace(result.FilePath))
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? "视频 provider 未返回成功结果" : result.Error);
        if (!File.Exists(result.FilePath))
            throw new InvalidOperationException("视频 provider 声称成功，但输出文件不存在");
        if (!IsVideoFile(result.FilePath))
            throw new InvalidOperationException("视频 provider 返回的产物不是受支持的视频文件，已拒绝作为成功结果");

        return new ExecutionOutput
        {
            Outputs = [new AssetRef { Role = "video", Ref = AssetStore.ToReference(result.FilePath) }]
        };
    }

    private static string ReadString(IReadOnlyDictionary<string, JsonElement> inputs, string name) =>
        inputs.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int ReadInt(IReadOnlyDictionary<string, JsonElement> inputs, string name) =>
        inputs.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
            ? result
            : 0;

    private static bool IsVideoFile(string path) =>
        string.Equals(Path.GetExtension(path), ".mp4", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Path.GetExtension(path), ".webm", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Path.GetExtension(path), ".mov", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Path.GetExtension(path), ".mkv", StringComparison.OrdinalIgnoreCase);
}
