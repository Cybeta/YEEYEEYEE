using System.Text.Json;
using YEEYEEYEE.Desktop;

namespace YEEYEEYEE.Web;

internal static class WebEntityApi
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
    public sealed record Mutation(long BaseRevision, string Operation, Guid EntityId, Guid? VariantId = null,
        Guid? VersionId = null, string? Description = null, string? Core = null, string? Aliases = null,
        string? Note = null, Guid? AttachmentId = null, string? Name = null, string? MediaType = null, string? Data = null);
    private static IResult Error(int status, string code, string message) => Results.Json(new { code, message }, statusCode: status);
    private static IEnumerable<NodeReference> References(WorkflowEntity entity) => entity.Variants.SelectMany(v =>
        v.References.Concat(v.Versions.SelectMany(version => version.References)));

    internal static bool ValidMedia(byte[] bytes, string type)
    {
        var data = bytes.AsSpan();
        return type switch
        {
            "image/png" => data.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) && data.Length >= 24 && data.Slice(12, 4).SequenceEqual("IHDR"u8),
            "image/jpeg" => data.Length >= 4 && data[0] == 255 && data[1] == 216 && data[2] == 255 && data[^2] == 255 && data[^1] == 217,
            "image/webp" => data.Length >= 16 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8),
            "video/mp4" or "video/quicktime" or "audio/mp4" => data.Length >= 16 && data.Slice(4, 4).SequenceEqual("ftyp"u8),
            "video/webm" => data.StartsWith(new byte[] { 0x1a, 0x45, 0xdf, 0xa3 }),
            "audio/ogg" => data.StartsWith("OggS"u8),
            "audio/flac" => data.StartsWith("fLaC"u8),
            "audio/wav" => data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WAVE"u8),
            "audio/mpeg" => data.Length >= 3 && (data[..3].SequenceEqual("ID3"u8) || data[0] == 255 && (data[1] & 224) == 224),
            _ => false
        };
    }

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/web/entities/{entityId:guid}/variants/{variantId:guid}/media/{attachmentId:guid}",
            (Guid entityId, Guid variantId, Guid attachmentId, Guid? versionId) =>
            {
                if (WebProjectApi.Current is not { IsProject: true, EntitiesPath: { } path } mode)
                    return Error(409, "CANVAS_REQUIRES_PROJECT", "需要项目画布");
                return WebProjectApi.Store(mode)!.EntityAttachmentFile(entityId, variantId, attachmentId, versionId);
            });
        app.MapPost("/api/web/entities/edit", (HttpContext context, Mutation input) =>
        {
            if (!WebAccessGuard.Permissions(context).Contains("canvas.edit"))
                return Error(403, "CANVAS_EDIT_FORBIDDEN", "需要画布编辑权限");
            if (WebProjectApi.Current is not { IsProject: true, EntitiesPath: { } path, CanvasPath: { } canvasPath })
                return Error(409, "CANVAS_REQUIRES_PROJECT", "资源管理需要项目画布");
            if ((input.Description?.Length ?? 0) > 100000 || (input.Core?.Length ?? 0) > 100000 ||
                (input.Aliases?.Length ?? 0) > 2000 || (input.Note?.Length ?? 0) > 2000 || (input.Name?.Length ?? 0) > 200)
                return Error(400, "INVALID_REQUEST", "资源内容过长");
            try
            {
                using var referenceLease = new FileStream(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(canvasPath)!)!, ".references.lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                var bytes = File.ReadAllBytes(path);
                if (CanvasRevision.Of(bytes) != input.BaseRevision) return Error(409, "LIBRARY_REVISION_CONFLICT", "资源库已变化，请重新加载");
                var library = JsonSerializer.Deserialize<ProjectEntityFile>(bytes, Options) ?? throw new JsonException("资源库为空");
                if (library.FormatVersion != ProjectLibrary.CurrentFormat) return Error(409, "LIBRARY_INVALID", "资源库格式不可编辑");
                var entity = library.Entities.FirstOrDefault(e => e.Id == input.EntityId);
                if (entity is null) return Error(404, "ENTITY_NOT_FOUND", "实体不存在");
                var variant = entity.Variants.FirstOrDefault(v => v.Id == input.VariantId);
                var version = variant?.FindVersion(input.VersionId ?? Guid.Empty);
                if (input.Operation != "editEntity" && input.Operation != "deleteEntity" && variant is null)
                    return Error(404, "VARIANT_NOT_FOUND", "变体不存在");
                if (input.Operation is "rollbackVersion" or "deleteVersion" && version is null)
                    return Error(404, "VERSION_NOT_FOUND", "版本不存在");
                if (input.Operation is "deleteEntity" or "deleteVariant" or "deleteVersion")
                {
                    bool Matches(NodeReference r) => r.EntityId == entity.Id &&
                        (input.Operation == "deleteEntity" || r.VariantId == variant!.Id &&
                        (input.Operation == "deleteVariant" || r.VariantVersionId == version!.Id));
                    var directory = Path.GetDirectoryName(canvasPath)!;
                    var projectRoot = Path.GetDirectoryName(directory)!;
                    var paths = Directory.EnumerateFiles(directory, "*.json").Where(p => !p.EndsWith(".edits.json", StringComparison.OrdinalIgnoreCase)).ToList();
                    var draft = Path.Combine(projectRoot, "last-canvas.json");
                    if (File.Exists(draft)) paths.Add(draft);
                    foreach (var source in paths)
                    {
                        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0 ||
                            !CanvasOpenService.TryRead(source, out var state, out _) || state.Canvas is null)
                            return Error(409, "REFERENCE_SCAN_FAILED", "有画布无法安全扫描，已阻止删除");
                        var report = CanvasReferenceScanner.Scan(state.Canvas, source, false, false);
                        if (report.Hits.Any(h => Matches(new NodeReference { EntityId = h.EntityId, VariantId = h.VariantId, VariantVersionId = h.VariantVersionId })) ||
                            state.Canvas.Entities.Where(e => !e.ManagedByProject).SelectMany(References).Any(Matches))
                            return Error(409, "ENTITY_REFERENCED", "画布或章节仍在引用该资源，请先解除引用");
                    }
                    if (library.Entities.SelectMany(References).Any(Matches) || input.Operation == "deleteEntity" && library.Entities.SelectMany(e => e.Variants)
                        .Any(v => v.Layout?.Items.Any(i => i.EntityId == entity.Id) == true || v.Versions.Any(ver => ver.Layout?.Items.Any(i => i.EntityId == entity.Id) == true)))
                        return Error(409, "ENTITY_REFERENCED", "其它资源或历史版本仍在引用该资源");
                }
                switch (input.Operation)
                {
                    case "editEntity": entity.Core = input.Core ?? entity.Core; entity.Aliases = input.Aliases ?? entity.Aliases; break;
                    case "editVariant": variant!.Description = input.Description ?? variant.Description; break;
                    case "commitVersion":
                        variant!.EnsureInitialVersion();
                        if (!variant.HasUncommittedChanges) return Error(409, "VERSION_UNCHANGED", "没有未提交的变体改动");
                        variant.Commit(input.Note ?? ""); break;
                    case "rollbackVersion": variant!.RollbackTo(version!); break;
                    case "deleteEntity": library.Entities.Remove(entity); break;
                    case "deleteVariant": entity.Variants.Remove(variant!); break;
                    case "deleteVersion":
                        if (version!.Number == variant!.CurrentVersion || variant.Versions.Count <= 1)
                            return Error(409, "VERSION_CURRENT", "当前版本或唯一版本不能删除");
                        if (variant.Versions.Any(v => v.SupersedesVersionId == version.Id))
                            return Error(409, "VERSION_REFERENCED", "后续版本仍记录此版本为来源");
                        variant.Versions.Remove(version); break;
                    case "addMedia":
                        var kinds = new Dictionary<string, AttachmentKind> { ["image/png"] = AttachmentKind.Image, ["image/jpeg"] = AttachmentKind.Image,
                            ["image/webp"] = AttachmentKind.Image, ["video/mp4"] = AttachmentKind.Video, ["audio/mpeg"] = AttachmentKind.Audio, ["audio/wav"] = AttachmentKind.Audio };
                        if (input.MediaType is null || !kinds.TryGetValue(input.MediaType, out var kind) || string.IsNullOrWhiteSpace(input.Name) ||
                            input.Data is null || input.Data.Length > 12000000) return Error(400, "MEDIA_INVALID", "请选择不超过 8 MB 的 PNG/JPEG/WebP/MP4/MP3/WAV 文件");
                        byte[] media;
                        try { media = Convert.FromBase64String(input.Data); } catch (FormatException) { return Error(400, "MEDIA_INVALID", "媒体编码无效"); }
                        if (media.Length == 0 || media.Length > 8 * 1024 * 1024 || !ValidMedia(media, input.MediaType))
                            return Error(400, "MEDIA_INVALID", "媒体文件大小或真实格式与声明不符");
                        variant!.Attachments.Add(new WorkflowAttachment { Name = input.Name.Trim(), Kind = kind, Source = "Web 上传", Reference = "data:" + input.MediaType + ";base64," + input.Data }); break;
                    case "renameMedia":
                    case "removeMedia":
                        var attachment = variant!.Attachments.FirstOrDefault(a => a.Id == input.AttachmentId);
                        if (attachment is null) return Error(404, "ASSET_ATTACHMENT_NOT_FOUND", "媒体附件不存在");
                        if (input.Operation == "removeMedia") variant.Attachments.Remove(attachment);
                        else if (!string.IsNullOrWhiteSpace(input.Name)) attachment.Name = input.Name.Trim();
                        else return Error(400, "MEDIA_INVALID", "媒体名称不能为空");
                        break;
                    default: return Error(400, "INVALID_REQUEST", "资源操作不支持");
                }
                if (!ProjectLibrary.TrySave(library, path, bytes, out var error)) return Error(409, "LIBRARY_SAVE_CONFLICT", error);
                return Results.Json(new { saved = true, revision = CanvasRevision.Of(File.ReadAllBytes(path)) });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            { return Error(503, "LIBRARY_UNAVAILABLE", "资源库无法安全保存"); }
        });
    }
}
