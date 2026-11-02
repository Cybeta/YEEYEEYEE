using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace YEEYEEYEE.Desktop;

/// <summary>项目内资产的可移植引用与文件系统解析。</summary>
public static class AssetStore
{
    private const string Scheme = "asset://";

    public static string Directory
    {
        get
        {
            var configured = EnvCompat.Get("ASSET_DIR");
            if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured.Trim());
            return AppPaths.Combine("assets");
        }
    }

    public static string EnsureDirectory()
    {
        var directory = Directory;
        System.IO.Directory.CreateDirectory(directory);
        return directory;
    }

    public static string ToReference(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        if (path.StartsWith(Scheme, StringComparison.Ordinal)) return path;
        if (!Path.IsPathRooted(path)) return path;
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(Directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return Scheme + Path.GetFileName(full);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { }
        return path;
    }

    public static string? Resolve(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        var path = reference.StartsWith(Scheme, StringComparison.Ordinal)
            ? Path.Combine(Directory, Path.GetFileName(reference[Scheme.Length..]))
            : reference;
        return File.Exists(path) ? path : null;
    }

    public static bool Exists(string? reference) => Resolve(reference) is not null;

    /// <summary>
    /// 文件字节内容的 SHA-256（流式读取，不把整张图片读进内存）。读不到或读失败返回 <c>null</c>。
    ///
    /// 为什么按字节而不是按路径/大小/修改时间：同一路径被另一份图片覆盖时，文件大小与时间戳都可能
    /// 与原来一样（复制时保留时间、两张同尺寸截图等），只比元数据会静默漏检。反过来，内容没变、
    /// 只是改了修改时间，字节哈希也不会变——这正是「内容才是依据」的口径。
    /// </summary>
    public static string? ContentHashOfFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(stream));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public static bool MoveToRecycleBin(string? reference)
    {
        var path = Resolve(reference);
        if (path is null) return false;
        try { File.Delete(path); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    public static void Normalize(WorkflowCanvasState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        foreach (var node in state.Nodes)
        {
            foreach (var attachment in node.Attachments) attachment.Reference = ToReference(attachment.Reference);
            foreach (var record in node.GenerationHistory) record.Output = ToReference(record.Output);
        }
        foreach (var attachment in EntityAssets.AllAttachments(state))
            attachment.Reference = ToReference(attachment.Reference);
    }
}

/// <summary>
/// 参考引用的**可比较短写法**：内嵌 data URL（整段 base64）只留内容哈希。
///
/// 为什么要有：引用指纹的结构部分原先直接拼 <see cref="WorkflowAttachment.Reference"/>。网页端上传的
/// 参考图是以 <c>data:image/png;base64,...</c> 内嵌在实体附件里的，于是一张 5 MB 的参考图会在
/// **每一件引用它的产物**上整段复制一遍——demo 项目的画布因此长到 300 MB 以上，桌面端打开时
/// 光把这段 JSON 读成字符串就要几百 MB，再叠加深拷贝，内存涨到数 GB 并被系统判为无响应。
///
/// 短写法对判定是等价的：同一份字节 → 同一个哈希 → 指纹仍然一致；换一张图 → 哈希变 → 照样报
/// 「设定已更新」。非 data URL 的引用保持原样，所以历史记录与当前计算可以互相换算。
/// </summary>
public static class ReferencePayload
{
    /// <summary>短写法前缀。base64 与指纹分隔符里都不含 <c>#</c>，不会与真实引用撞形。</summary>
    public const string TokenPrefix = "data-url#";

    private const string Scheme = "data:";
    private const string Base64Marker = ";base64,";

    /// <summary>同一段内嵌 data URL 的短写法缓存（按字符串实例，弱引用键：字符串回收后条目自然消失）。</summary>
    private static readonly ConditionalWeakTable<string, string> TokenCache = new();

    /// <summary>data URL 的短写法；不是 data URL（或内容解不开）就原样返回。</summary>
    public static string TokenOf(string? reference)
    {
        if (string.IsNullOrEmpty(reference) || !reference.StartsWith(Scheme, StringComparison.Ordinal)) return reference ?? string.Empty;
        if (!reference.Contains(Base64Marker, StringComparison.Ordinal)) return reference;
        // 画布卡片每次重建都会算结构指纹。同一张内嵌图是同一个字符串实例，哈希只算一次；
        // 不缓存的话每次渲染都要把几十 MB 的 base64 解码再算一遍 SHA-256。
        return TokenCache.GetValue(reference, static value => ComputeToken(value));
    }

    private static string ComputeToken(string reference)
    {
        var separator = reference.IndexOf(Base64Marker, StringComparison.Ordinal);
        try
        {
            var bytes = Convert.FromBase64String(reference[(separator + Base64Marker.Length)..]);
            return TokenPrefix + Convert.ToHexString(SHA256.HashData(bytes));
        }
        catch (FormatException)
        {
            // 解不开就保持原样：宁可留着长文本，也不把一个读不懂的引用换成假哈希。
            return reference;
        }
    }

    /// <summary>
    /// 把一段文本里内嵌的 data URL 换成短写法（其余字符原样保留）。
    /// 用于压缩历史指纹——语义不变，只是同一份字节不再重复存几十份。
    /// </summary>
    public static string CompactText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        Dictionary<string, string>? cache = null;
        return CompactText(text, ref cache);
    }

    /// <summary>
    /// 压缩一份画布上所有产物的引用指纹，返回被改写的条数。
    /// 打开画布时调用：老项目里整段内嵌的 data URL 会在这一步换成短写法，
    /// 后续的深拷贝、标签快照与内存驻留都按小文本走。
    /// </summary>
    public static int Compact(WorkflowCanvasState canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        Dictionary<string, string>? cache = null;
        var changed = 0;
        foreach (var attachment in canvas.Nodes.SelectMany(node => node.Attachments))
        {
            if (attachment.SourceFingerprints.Count == 0) continue;
            foreach (var key in attachment.SourceFingerprints.Keys.ToList())
            {
                var value = attachment.SourceFingerprints[key];
                if (string.IsNullOrEmpty(value) || !value.Contains(Base64Marker, StringComparison.Ordinal)) continue;
                var compacted = CompactText(value, ref cache);
                if (ReferenceEquals(compacted, value)) continue;
                attachment.SourceFingerprints[key] = compacted;
                changed++;
            }
        }
        return changed;
    }

    private static string CompactText(string text, ref Dictionary<string, string>? cache)
    {
        var start = text.IndexOf(Scheme, StringComparison.Ordinal);
        if (start < 0) return text;

        var builder = new StringBuilder(Math.Min(text.Length, 4096));
        var cursor = 0;
        while (start >= 0)
        {
            var separator = text.IndexOf(Base64Marker, start, StringComparison.Ordinal);
            // 只看眼下的这一段：隔着几百个字符才出现的 ";base64," 不属于这个 data 前缀。
            if (separator < 0 || separator - start > 128)
            {
                start = text.IndexOf(Scheme, start + Scheme.Length, StringComparison.Ordinal);
                continue;
            }

            var payloadStart = separator + Base64Marker.Length;
            var end = payloadStart;
            while (end < text.Length && IsBase64Char(text[end])) end++;
            if (end == payloadStart)
            {
                start = text.IndexOf(Scheme, payloadStart, StringComparison.Ordinal);
                continue;
            }

            var reference = text[start..end];
            cache ??= new Dictionary<string, string>(StringComparer.Ordinal);
            if (!cache.TryGetValue(reference, out var token))
            {
                token = TokenOf(reference);
                cache[reference] = token;
            }
            builder.Append(text, cursor, start - cursor).Append(token);
            cursor = end;
            start = text.IndexOf(Scheme, end, StringComparison.Ordinal);
        }

        if (cursor == 0) return text;
        builder.Append(text, cursor, text.Length - cursor);
        return builder.ToString();
    }

    private static bool IsBase64Char(char value) => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/' or '=';
}
