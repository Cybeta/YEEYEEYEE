namespace YEEYEEYEE.Desktop;

public enum VideoGenerationStatus { NotConfigured, Succeeded, Failed }

/// <summary>一次出视频请求：提示词、模型、画幅、时长与参考帧本机路径。</summary>
public sealed class VideoGenerationRequest
{
    public string Prompt { get; init; } = string.Empty;
    public string NegativePrompt { get; init; } = string.Empty;

    /// <summary>本次请求使用的视频模型名；留空表示用设置里的默认视频模型。</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>目标画幅；0 表示不指定，由服务端或执行方决定。</summary>
    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>目标时长（秒）；0 表示由服务端默认。</summary>
    public int Seconds { get; init; }

    /// <summary>参考帧的本机绝对路径列表（图生视频用；文生视频为空）。</summary>
    public IReadOnlyList<string> ReferenceImages { get; init; } = Array.Empty<string>();

    public bool HasReferenceImages => ReferenceImages.Count > 0;
}

public sealed class VideoGenerationResult
{
    public VideoGenerationStatus Status { get; init; }
    public string FilePath { get; init; } = string.Empty;
    public string Error { get; init; } = string.Empty;
    public string Provider { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
}

public interface IVideoProvider
{
    bool IsConfigured { get; }
    string Name { get; }

    Task<VideoGenerationResult> GenerateAsync(VideoGenerationRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// 「出视频执行方尚未接入」。**这不是占位图，也不是伪造结果**：它如实告诉用户
/// 接口说明已经存好、技能已经建好，只差把出视频实现接进来。
/// 有了它，纯出视频的技能不会因为「没有出图模型」而报一条误导性的错误。
/// </summary>
public sealed class UnconfiguredVideoProvider : IVideoProvider
{
    private readonly string detail;

    public UnconfiguredVideoProvider(string detail) => this.detail = detail;

    public bool IsConfigured => false;
    public string Name => "出视频未接入";

    public Task<VideoGenerationResult> GenerateAsync(VideoGenerationRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new VideoGenerationResult
        {
            Status = VideoGenerationStatus.NotConfigured,
            Provider = Name,
            Model = request.Model,
            Error = detail
        });
}

/// <summary>
/// 出视频链路的唯一入口。与 <see cref="ImageProviderFactory"/> 对称：
/// 将来接入真实出视频执行方（例如按文档提交任务再轮询取结果）时，只改这里，
/// 技能、画布与技能执行器都不需要改动。
/// </summary>
public static class VideoProviderFactory
{
    public static IVideoProvider Create(AiProviderConfig? config = null)
    {
        var effective = config ?? AiProviderSettings.Load();
        var detail = effective.IsVideoConfigured
            ? $"视频接口已配置（模型 {effective.VideoModel}），但出视频执行方尚未接入实现，暂时无法真正生成视频。"
            : "尚未配置视频接口（缺少视频模型或地址），出视频执行方也尚未接入实现，暂时无法真正生成视频。";
        return new UnconfiguredVideoProvider(detail);
    }
}
