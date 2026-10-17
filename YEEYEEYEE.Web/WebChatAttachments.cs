using System.Text;

namespace YEEYEEYEE.Web;

/// <summary>聊天附件的一种受支持类型：MIME、媒体种类、大小上限，以及能否当生成参考图。</summary>
/// <param name="Mime">规范化后的 MIME（小写、无参数）。</param>
/// <param name="Kind">媒体种类：Image / Video / Audio / Text / Document。</param>
/// <param name="MaxBytes">这一种类型的大小上限。</param>
/// <param name="Referenceable">能否作为生成技能的参考图（目前只有图片可以）。</param>
public sealed record ChatAttachmentType(string Mime, string Kind, long MaxBytes, bool Referenceable);

/// <summary>聊天消息里回显的附件元数据（字节按 ID 走下载接口，不放进消息）。</summary>
public sealed record ChatAttachmentInfo(string Id, string Name, string Type, string Kind, long Size);

/// <summary>
/// 聊天附件的类型注册表：上传校验、参考图判定、消息回显与前端可选类型都问它一份。
///
/// 为什么要成表：类型、签名、大小、种类、能否当参考图这五件事如果散在各处，加一种类型就得
/// 分别改上传、下载、参考图校验和前端 accept，漏一处就是「能传不能用」或「能用不校验」。
/// </summary>
public static class WebChatAttachments
{
    /// <summary>请求体硬上限：任何类型都不会读到超过这个字节数。</summary>
    public const long HardLimit = 16 * 1024 * 1024;

    private const long ImageLimit = 5 * 1024 * 1024;
    private const long MediaLimit = 16 * 1024 * 1024;
    private const long DocumentLimit = 8 * 1024 * 1024;
    private const long TextLimit = 64 * 1024;

    private static readonly ChatAttachmentType[] Supported =
    {
        new("image/png", "Image", ImageLimit, true),
        new("image/jpeg", "Image", ImageLimit, true),
        new("image/webp", "Image", ImageLimit, true),
        new("image/gif", "Image", ImageLimit, true),
        new("video/mp4", "Video", MediaLimit, false),
        new("video/webm", "Video", MediaLimit, false),
        new("video/quicktime", "Video", MediaLimit, false),
        new("audio/mpeg", "Audio", MediaLimit, false),
        new("audio/wav", "Audio", MediaLimit, false),
        new("audio/mp4", "Audio", MediaLimit, false),
        new("audio/flac", "Audio", MediaLimit, false),
        new("audio/ogg", "Audio", MediaLimit, false),
        new("application/pdf", "Document", DocumentLimit, false),
        new("text/plain", "Text", TextLimit, false),
        new("text/markdown", "Text", TextLimit, false),
        new("application/json", "Text", TextLimit, false),
        new("text/csv", "Text", TextLimit, false)
    };

    /// <summary>规范化请求头里的 Content-Type：去参数、去空白、转小写。</summary>
    public static string Normalize(string? contentType) =>
        contentType?.Split(';')[0].Trim().ToLowerInvariant() ?? string.Empty;

    /// <summary>查一种类型；不支持返回 null（调用方据此返回 ATTACHMENT_TYPE_INVALID）。</summary>
    public static ChatAttachmentType? Resolve(string? contentType) =>
        Supported.FirstOrDefault(type => type.Mime == Normalize(contentType));

    /// <summary>媒体种类；认不出的类型返回 Other（不猜）。</summary>
    public static string KindOf(string? contentType) => Resolve(contentType)?.Kind ?? "Other";

    /// <summary>能否作为生成参考图。</summary>
    public static bool IsReferenceable(string? contentType) => Resolve(contentType)?.Referenceable == true;

    /// <summary>声明给前端：可选类型、种类、上限与参考图能力。</summary>
    public static object[] Describe() => Supported
        .Select(type => (object)new { type.Mime, type.Kind, type.MaxBytes, type.Referenceable })
        .ToArray();

    /// <summary>前端 accept 属性用的一行（由服务端声明生成，避免两边各写一份）。</summary>
    public static string AcceptAttribute() => string.Join(",", Supported.Select(type => type.Mime));

    /// <summary>
    /// 字节与声明类型是否相符。图片/视频/音频复用实体媒体的签名校验；
    /// 文本按 UTF-8（含 NUL 视为二进制）判断；GIF 与 PDF 各认自己的魔数。
    /// </summary>
    public static bool SignatureValid(byte[] bytes, string? contentType)
    {
        var mime = Normalize(contentType);
        var data = bytes.AsSpan();
        return mime switch
        {
            "image/gif" => data.Length >= 6
                && (data[..6].SequenceEqual("GIF87a"u8) || data[..6].SequenceEqual("GIF89a"u8)),
            "application/pdf" => data.StartsWith("%PDF-"u8),
            "text/plain" or "text/markdown" or "application/json" or "text/csv" => TextLike(bytes),
            _ => WebEntityApi.ValidMedia(bytes, mime)
        };
    }

    private static bool TextLike(byte[] bytes)
    {
        try
        {
            // 与上传时同一条判据：必须是合法 UTF-8，且不能夹带 NUL（那是二进制，不是文本）。
            return !new UTF8Encoding(false, true).GetString(bytes).Contains('\0');
        }
        catch (DecoderFallbackException) { return false; }
    }
}
