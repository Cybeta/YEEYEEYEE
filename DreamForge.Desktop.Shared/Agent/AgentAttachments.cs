using System.Text;

namespace DreamForge.Desktop;

/// <summary>发给模型的附件种类。</summary>
public enum AgentAttachmentKind
{
    /// <summary>图片：以 data URL 随消息发送，走模型的多模态通道。</summary>
    Image,

    /// <summary>文本文件：直接读成文本拼进消息正文，任何模型都能用。</summary>
    Text
}

/// <summary>
/// 一条待发送给模型的附件。
/// 图片与文本走**两条完全不同的路**：图片是报文里的图片块（需要模型支持图像理解），
/// 文本文件只是被读成字符串拼进消息正文——后者不依赖服务商的任何文件接口。
/// </summary>
public sealed class AgentAttachment
{
    public required string Name { get; init; }
    public AgentAttachmentKind Kind { get; init; }

    /// <summary>图片：完整 data URL；文本：文件正文。</summary>
    public string Payload { get; init; } = string.Empty;

    /// <summary>文本附件被截断时为 true，提示词与界面都要如实说明。</summary>
    public bool Truncated { get; init; }

    /// <summary>原始文件路径，仅用于展示与去重。</summary>
    public string SourcePath { get; init; } = string.Empty;

    public string Describe() => Kind switch
    {
        AgentAttachmentKind.Image => IsDataUrl
            ? $"图片 · {Format((Payload.Length - Payload.IndexOf(',') - 1) * 3 / 4)}"
            : "图片 · 外部地址",
        _ => $"文本 · {Payload.Length:N0} 字符{(Truncated ? "（已截断）" : string.Empty)}"
    };

    private bool IsDataUrl => Payload.StartsWith("data:", StringComparison.OrdinalIgnoreCase);

    private static string Format(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024d / 1024d:F1} MB"
        : $"{Math.Max(1, bytes / 1024d):F0} KB";
}

/// <summary>加载结果：要么得到附件，要么得到**给用户看的失败原因**。</summary>
public sealed record AgentAttachmentResult(AgentAttachment? Attachment, string? Error);

/// <summary>
/// 把本地文件变成可发送的附件。所有拒绝都给出可执行的原因，不做静默跳过。
/// </summary>
public static class AgentAttachmentLoader
{
    /// <summary>单张图片上限。base64 之后体积约为原始的 1.34 倍，再大请求体就不合适了。</summary>
    public const long MaxImageBytes = 8 * 1024 * 1024;

    /// <summary>文本附件上限（字符）。超出部分截断，并在提示词里明确标注，不假装读全了。</summary>
    public const int MaxTextCharacters = 200_000;

    private static readonly Dictionary<string, string> ImageMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
        [".gif"] = "image/gif"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".json", ".csv", ".tsv", ".log", ".yml", ".yaml",
        ".xml", ".html", ".htm", ".css", ".cs", ".ts", ".tsx", ".js", ".py", ".sql",
        ".ini", ".toml", ".sh", ".ps1", ".bat"
    };

    /// <summary>对话框用的过滤器：图片与文本分两组，另有全部文件（选到不支持的会给出原因）。</summary>
    public const string ImageFilter = "图片 (*.png;*.jpg;*.jpeg;*.webp;*.gif)|*.png;*.jpg;*.jpeg;*.webp;*.gif";
    public const string TextFilter = "文本文件 (*.md;*.txt;*.json;*.csv;*.log;*.yml;*.xml;*.cs;*.ts;*.js;*.py)|*.md;*.txt;*.json;*.csv;*.log;*.yml;*.xml;*.cs;*.ts;*.js;*.py";

    public static AgentAttachmentResult Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return new(null, "文件不存在。");

        var name = Path.GetFileName(path);
        var extension = Path.GetExtension(path);

        if (ImageMimeTypes.TryGetValue(extension, out var mimeType)) return LoadImage(path, name, mimeType);
        if (TextExtensions.Contains(extension)) return LoadText(path, name);

        // PDF / Word / 压缩包这类二进制：DeepSeek 明确不支持文件输入，不能假装上传成功。
        return new(null,
            $"「{name}」不是可发送的类型。图片支持 PNG / JPG / WebP / GIF，文本支持 md / txt / json / csv 等普通文本。" +
            "PDF、Word 这类二进制文件需要先转成文本——接口不接受二进制文件上传。");
    }

    /// <summary>
    /// 把文本附件拼进消息正文。图片不走这里（它们在报文的图片块里），
    /// 所以这条路径对**任何模型**都有效，不依赖多模态能力。
    /// </summary>
    public static string ComposeContent(string question, IReadOnlyList<AgentAttachment> attachments)
    {
        var files = attachments.Where(item => item.Kind == AgentAttachmentKind.Text).ToList();
        if (files.Count == 0) return question;

        var builder = new StringBuilder(question);
        foreach (var file in files)
        {
            builder.AppendLine();
            builder.AppendLine();
            builder.AppendLine($"【附件：{file.Name}{(file.Truncated ? "（文件过长，已截断，以下不是全文）" : string.Empty)}】");
            builder.Append(file.Payload);
        }
        return builder.ToString();
    }

    /// <summary>图片附件对应的 data URL 列表。</summary>
    public static List<string> CollectImages(IReadOnlyList<AgentAttachment> attachments) =>
        attachments.Where(item => item.Kind == AgentAttachmentKind.Image)
            .Select(item => item.Payload)
            .Where(payload => !string.IsNullOrWhiteSpace(payload))
            .ToList();

    /// <summary>
    /// 从 data URL 里拆出 media type 与 base64 数据。
    /// Anthropic 的 image 块要求这两项分开给，不能直接塞一个 data URL。
    /// 传入的不是 data URL（例如 http 图片地址）时返回 null。
    /// </summary>
    public static (string MediaType, string Data)? SplitDataUrl(string url)
    {
        if (!url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;
        var comma = url.IndexOf(',');
        if (comma < 0) return null;
        var header = url[5..comma];
        var semicolon = header.IndexOf(';');
        var mediaType = semicolon < 0 ? header : header[..semicolon];
        return string.IsNullOrWhiteSpace(mediaType) ? null : (mediaType, url[(comma + 1)..]);
    }

    private static AgentAttachmentResult LoadImage(string path, string name, string mimeType)
    {
        var size = new FileInfo(path).Length;
        if (size > MaxImageBytes)
            return new(null, $"「{name}」{FormatSize(size)} 超过单张图片上限 {FormatSize(MaxImageBytes)}，请先压缩后再上传。");

        try
        {
            var dataUrl = $"data:{mimeType};base64,{Convert.ToBase64String(File.ReadAllBytes(path))}";
            return new(new AgentAttachment
            {
                Name = name,
                Kind = AgentAttachmentKind.Image,
                Payload = dataUrl,
                SourcePath = path
            }, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new(null, $"「{name}」读取失败：{error.Message}");
        }
    }

    private static AgentAttachmentResult LoadText(string path, string name)
    {
        try
        {
            var text = File.ReadAllText(path);
            var truncated = text.Length > MaxTextCharacters;
            if (truncated) text = text[..MaxTextCharacters];
            return new(new AgentAttachment
            {
                Name = name,
                Kind = AgentAttachmentKind.Text,
                Payload = text,
                Truncated = truncated,
                SourcePath = path
            }, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return new(null, $"「{name}」读取失败：{error.Message}");
        }
    }

    private static string FormatSize(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024d / 1024d:F1} MB"
        : $"{Math.Max(1, bytes / 1024d):F0} KB";
}
