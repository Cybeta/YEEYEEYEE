using System.Security.Cryptography;
using System.Text.Json;
using YEEYEEYEE.Desktop;

namespace YEEYEEYEE.Web;

internal static class WebGenerationReferences
{
    internal static string? Locate(WorkflowAttachment attachment)
    {
        try
        {
            var reference = attachment.Reference;
            if (reference.StartsWith("asset://", StringComparison.Ordinal))
            {
                if (reference != "asset://" + Path.GetFileName(reference[8..])) return null;
                var path = AssetStore.Resolve(reference);
                if (path is null || !File.Exists(path) || !Path.GetFullPath(path).StartsWith(Path.GetFullPath(AssetStore.Directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
                for (var part = Path.GetFullPath(path); part is not null; part = Path.GetDirectoryName(part))
                    if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) return null;
                return WorkflowAttachment.KindOf(path) == AttachmentKind.Image ? path : null;
            }
            if (!reference.StartsWith("data:image/", StringComparison.Ordinal) || reference.Length > 12 * 1024 * 1024) return null;
            var separator = reference.IndexOf(";base64,", StringComparison.Ordinal);
            if (separator < 0) return null;
            var type = reference[5..separator];
            var bytes = Convert.FromBase64String(reference[(separator + 8)..]);
            if (!WebEntityApi.ValidMedia(bytes, type)) return null;
            var extension = type switch { "image/png" => ".png", "image/jpeg" => ".jpg", "image/webp" => ".webp", _ => null };
            if (extension is null) return null;
            var directory = Path.Combine(AssetStore.Directory, "web-references");
            for (var part = Path.GetFullPath(AssetStore.Directory); part is not null; part = Path.GetDirectoryName(part))
                if (Directory.Exists(part) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) return null;
            Directory.CreateDirectory(directory);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return null;
            var file = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(bytes)) + extension);
            if (File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) return null;
            File.WriteAllBytes(file, bytes);
            return file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or FormatException) { return null; }
    }

    internal static string[] ResolveVideo(ProjectCanvasSceneStore store, Guid nodeId, IReadOnlyList<string> extra) =>
        ResolveVideoTarget(store.GenerationTarget(nodeId), extra);

    internal static string[] ResolveVideoTarget((WorkflowCanvasState Canvas, WorkflowNode Node, long Revision) target, IReadOnlyList<string> extra)
    {
        var check = GenerationAudit.CheckVideoAssets(target.Canvas, target.Node, Locate);
        if (!check.CanSubmit()) throw new InvalidDataException(string.Join("；", check.Errors));
        var paths = extra.Count > 0 ? extra.ToArray()
            : string.IsNullOrEmpty(check.AdoptedFrame) ? Array.Empty<string>() : [check.AdoptedFrame];
        if (paths.Length > 1) throw new InvalidDataException("当前视频接口仅支持一张首帧参考图，不能静默忽略多图");
        if (target.Node.Category == NodeCategory.Storyboard && !string.IsNullOrEmpty(check.AdoptedFrame) && extra.Count > 0 &&
            !SHA256.HashData(File.ReadAllBytes(check.AdoptedFrame)).AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(paths[0]))))
            throw new InvalidDataException("提交首帧与当前采用首帧不一致，请使用当前分镜采用图片");
        return paths;
    }

    internal static string[] Resolve((WorkflowCanvasState Canvas, WorkflowNode Node, long Revision) target, int capacity, IReadOnlyList<string> extra)
    {
        var errors = GenerationAudit.CheckImageAssets(target.Canvas, target.Node, Locate, capacity);
        if (errors.Count > 0) throw new InvalidDataException(string.Join("；", errors));
        var paths = ReferenceImagePicker.ResolveSlots(target.Canvas, target.Node, Locate).Select(s => s.Path).Where(p => p is not null).Cast<string>().Concat(extra).ToArray();
        errors = GenerationAudit.CheckImageSubmission(target.Canvas, target.Node, Locate, paths, capacity);
        if (errors.Count > 0 || paths.Length > capacity)
            throw new InvalidDataException(errors.Count > 0 ? string.Join("；", errors) : "参考图片数量超过模型容量，不能裁剪");
        return paths;
    }

    internal static void RecordEvidence(Dictionary<string, JsonElement> inputs, string[] paths,
        (WorkflowCanvasState Canvas, WorkflowNode Node, long Revision)? target = null)
    {
        inputs["referenceImages"] = JsonSerializer.SerializeToElement(paths);
        inputs["referenceImageHashes"] = JsonSerializer.SerializeToElement(paths.Distinct(StringComparer.Ordinal)
            .ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))), StringComparer.Ordinal));
        if (target is { } snapshot)
            inputs["sourceFingerprints"] = JsonSerializer.SerializeToElement(ReferenceStaleness.Snapshot(snapshot.Canvas, snapshot.Node));
    }

    internal static Dictionary<string, string> Fingerprints(IReadOnlyDictionary<string, JsonElement> inputs) =>
        inputs.TryGetValue("sourceFingerprints", out var value)
            ? value.Deserialize<Dictionary<string, string>>() ?? new(StringComparer.Ordinal)
            : new(StringComparer.Ordinal);

    internal static void ValidateEvidence(IReadOnlyDictionary<string, JsonElement> inputs)
    {
        var paths = inputs.TryGetValue("referenceImages", out var images) ? images.Deserialize<string[]>() ?? [] : [];
        var hashes = inputs.TryGetValue("referenceImageHashes", out var evidence)
            ? evidence.Deserialize<Dictionary<string, string>>() ?? new() : new Dictionary<string, string>();
        if (hashes.Count != paths.Distinct(StringComparer.Ordinal).Count() || paths.Any(p =>
            !hashes.TryGetValue(p, out var expected) || !File.Exists(p) ||
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))) != expected))
            throw new InvalidDataException("参考图片字节或请求覆盖在提交后发生变化，请重新发起生成");
        if (!inputs.TryGetValue("targetNodeId", out var nodeId)) return;
        var recorded = Fingerprints(inputs);
        if (recorded.Count == 0 || !inputs.TryGetValue("webCanvasPath", out var canvasPath) ||
            WebProjectApi.RegisteredCanvas(canvasPath.GetString()!) is not { } mode)
            throw new InvalidDataException("任务缺少可信提交依据或目标画布不可用，请重新发起生成");
        WebProjectApi.InCanvas(mode, () =>
        {
            var target = WebProjectApi.Store(mode)!.GenerationTarget(nodeId.GetGuid());
            // 用同一套兼容比较：旧格式记录只比结构，不把升级本身当成资产变化。
            if (!ReferenceStaleness.SnapshotMatches(target.Canvas, target.Node, recorded, Locate))
                throw new InvalidDataException("资产依据在提交后发生变化，请重新发起生成");
            return true;
        });
    }
}
