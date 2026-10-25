using System.Security.Cryptography;
using System.Text.Json;
using YEEYEEYEE.Desktop;

namespace YEEYEEYEE.Web;

/// <summary>
/// 一次生成实际采用的参考图记录。字段名直接落进任务输入（JSON），供复现与排查：
/// 哪个实体 / 变体、哪张附件、用途、文件路径，以及作为辅助约束的外观描述。
/// </summary>
internal sealed record ReferenceSelectionRecord(
    string entityId, string name, string kind, string variantId, string versionId,
    string attachmentId, string purpose, string purposeLabel, string path, string description);

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

    internal static string[] ResolveVideo(ProjectCanvasSceneStore store, Guid nodeId, IReadOnlyList<string> extra, int capacity = 0) =>
        ResolveVideoTarget(store.GenerationTarget(nodeId), extra, capacity);

    /// <summary>
    /// 视频这一镜实际要带的参考图：**首帧在前**（顺序就是模型的首帧），后面跟实体引用
    /// （角色 → 道具 → 场景，与出图同一个装配方法）。
    ///
    /// 容量 &gt; 1 时才装配实体引用：只收一张图的链路（首帧）带上它们反而会被拒；
    /// 容量 0 表示调用方没给（ComfyUI 那条路另算）。
    /// </summary>
    internal static string[] ResolveVideoTarget((WorkflowCanvasState Canvas, WorkflowNode Node, long Revision) target,
        IReadOnlyList<string> extra, int capacity = 0)
    {
        var check = GenerationAudit.CheckVideoAssets(target.Canvas, target.Node, Locate);
        if (!check.CanSubmit()) throw new InvalidDataException(string.Join("；", check.Errors));
        var paths = new List<string>();
        if (extra.Count > 0) paths.AddRange(extra);
        else if (!string.IsNullOrEmpty(check.AdoptedFrame)) paths.Add(check.AdoptedFrame);
        if (capacity > 1)
            paths.AddRange(ReferenceImagePicker.ResolveSlots(target.Canvas, target.Node, Locate)
                .Select(slot => slot.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Cast<string>());
        var resolved = paths.Distinct(StringComparer.Ordinal).ToArray();
        if (capacity > 0 && resolved.Length > capacity)
            throw new InvalidDataException($"视频接口最多收 {capacity} 张参考图，这次给了 {resolved.Length} 张；"
                + "超出容量的部分不会被裁剪，请先删减引用。");
        if (target.Node.Category == NodeCategory.Storyboard && !string.IsNullOrEmpty(check.AdoptedFrame) && extra.Count > 0 &&
            !SHA256.HashData(File.ReadAllBytes(check.AdoptedFrame)).AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(resolved[0]))))
            throw new InvalidDataException("提交首帧与当前采用首帧不一致，请使用当前分镜采用图片");
        return resolved;
    }

    internal static string[] Resolve((WorkflowCanvasState Canvas, WorkflowNode Node, long Revision) target, int capacity, IReadOnlyList<string> extra)
    {
        var errors = GenerationAudit.CheckImageAssets(target.Canvas, target.Node, Locate, capacity);
        if (errors.Count > 0) throw new InvalidDataException(string.Join("；", errors));
        var paths = ReferenceImagePicker.ResolveSlots(target.Canvas, target.Node, Locate).Select(s => s.Path).Where(p => p is not null).Cast<string>().Concat(extra).ToArray();
        errors = GenerationAudit.CheckImageSubmission(target.Canvas, target.Node, Locate, paths, capacity);
        if (errors.Count > 0 || paths.Length > capacity)
            throw new InvalidDataException(errors.Count > 0 ? string.Join("；", errors)
                : $"请求包含 {paths.Length} 张参考图，超过模型容量 {capacity} 张，不能裁剪；请减少必需资产或提高参考图容量。");
        return paths;
    }

    /// <summary>
    /// 有实体引用时，把「第几张参考图是谁、只许用来控制什么」写进提示词。
    /// 序号与 <see cref="ReferenceImagePicker.ResolveSlots"/> 的顺序一致（角色 → 道具 → 场景），
    /// 也就是模型实际收到的图片顺序；不在这里另算一份，否则迟早会和装配结果错位。
    /// 提示词里已经手写过 Reference image 1 时原样保留，不重复追加。
    /// </summary>
    internal static string WithReferenceGuide(WorkflowCanvasState canvas, WorkflowNode node, string prompt, int trailingCount)
    {
        if (prompt.Contains("Reference image 1:", StringComparison.OrdinalIgnoreCase)) return prompt;
        var guide = ReferenceGuide(canvas, node, trailingCount);
        return guide.Length == 0 ? prompt : prompt.TrimEnd() + "\n\n" + guide;
    }

    /// <summary>
    /// 视频这一镜的附件编号说明：**图片、音频、视频各自从 1 开始编**，并写清每一份是谁的什么参考。
    ///
    /// 为什么必须由服务端生成：编号答案取决于实际提交顺序（首帧在前、实体引用按角色→道具→场景、
    /// 音频/视频按附件顺序），手写迟早会和真实附带顺序错位——模型就会把第 2 张当成场景、
    /// 把音频当成环境声。与出图那条路同一条规矩。
    /// </summary>
    internal static string WithVideoReferenceGuide(WorkflowCanvasState canvas, WorkflowNode node, string prompt,
        IReadOnlyList<string> images, IReadOnlyList<string> audios, IReadOnlyList<string> videos)
    {
        var dialogue = VoiceCasting.Parse(VoiceCasting.DialogueOf(node)).Where(line => line.Text.Length > 0).ToList();
        if (images.Count == 0 && audios.Count == 0 && videos.Count == 0 && dialogue.Count == 0) return prompt;
        if (prompt.Contains("Reference image 1:", StringComparison.OrdinalIgnoreCase)
            || prompt.Contains("Reference audio 1:", StringComparison.OrdinalIgnoreCase)) return prompt;

        var lines = new List<string>();
        if (images.Count > 0)
        {
            var adopted = GenerationAudit.CheckVideoAssets(canvas, node, Locate).AdoptedFrame;
            var slots = ReferenceImagePicker.ResolveSlots(canvas, node, Locate)
                .Where(slot => !string.IsNullOrWhiteSpace(slot.Path))
                .GroupBy(slot => slot.Path!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var contents = canvas.ResolveReferencePairs(node)
                .Where(pair => pair.Content is not null)
                .GroupBy(pair => ReferenceCandidate.KeyOf(pair.Reference), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Content!, StringComparer.Ordinal);

            for (var index = 0; index < images.Count; index++)
            {
                var path = images[index];
                if (!string.IsNullOrEmpty(adopted) && string.Equals(path, adopted, StringComparison.Ordinal))
                {
                    lines.Add($"Reference image {index + 1}: the adopted first frame of this shot; start from it "
                        + "and keep its composition, lighting and character identity.");
                    continue;
                }
                if (slots.TryGetValue(path, out var slot) && contents.TryGetValue(slot.Key, out var content))
                {
                    lines.Add($"Reference image {index + 1}: {UsageOf(content, slot.Label)}.");
                    continue;
                }
                lines.Add($"Reference image {index + 1}: supporting reference; keep only its subject appearance.");
            }
        }

        var speakers = dialogue
            .Select(line => line.Speaker)
            .Where(speaker => speaker.Length > 0 && !VoiceCasting.IsNarrator(speaker))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        for (var index = 0; index < audios.Count; index++)
        {
            // 只有一个说话人时才能把音频归属写实；多个说话人时只按顺序编号，不猜。
            var owner = speakers.Count == 1 ? $" for {speakers[0]}" : string.Empty;
            lines.Add($"Reference audio {index + 1}: voice timbre reference{owner}; use its voice quality, not its words.");
        }
        for (var index = 0; index < videos.Count; index++)
            lines.Add($"Reference video {index + 1}: motion and camera reference; do not copy the person identity from it.");

        if (lines.Count == 0 && dialogue.Count == 0) return prompt;
        if (lines.Count > 0)
            lines.Add("Do not swap these roles: image references define look, audio references define voice, video references define motion.");
        // 实测模型会把短台词改写、往句子里插词；这不是「语气词」问题，必须逐字约束。
        if (dialogue.Count > 0)
            lines.Add("Speak the quoted dialogue exactly as written: same words, same order. "
                + "Do not paraphrase, insert, drop or add any words.");
        return string.IsNullOrEmpty(prompt) ? string.Join("\n", lines) : prompt.TrimEnd() + "\n\n" + string.Join("\n", lines);
    }

    private static string UsageOf(ReferenceContent content, string fallbackLabel)
    {
        var name = string.IsNullOrWhiteSpace(content.Entity.Name) ? fallbackLabel : content.Entity.Name;
        return content.Entity.Kind switch
        {
            EntityKind.Character => $"character reference for {name}; use it only for face, hair, body proportions, clothing and accessories",
            EntityKind.Scene => $"scene reference for {name}; use it only for environment layout, lighting, props and color atmosphere",
            EntityKind.Prop => $"prop reference for {name}; use it only for its shape, material, color and details",
            _ => $"reference for {name}"
        };
    }

    /// <summary>
    /// 这一镜写了台词，提示词里就必须原样出现（去空白比较）。返回 null 表示没问题，
    /// 否则返回缺失的台词清单。
    ///
    /// 为什么拦：视频模型只照提示词说话。台词只存在画面参数里、没写进提示词时，
    /// 出来的是一条无声镜头，而声音闸门又要求有这道台词的证据——最后卡在成片前，
    /// 排查起来会以为是模型不听话。
    /// </summary>
    internal static string? MissingDialogue(WorkflowNode node, string prompt)
    {
        var lines = VoiceCasting.Parse(VoiceCasting.DialogueOf(node)).Where(line => line.Text.Length > 0).ToList();
        if (lines.Count == 0) return null;
        var compact = Compact(prompt);
        var missing = lines.Where(line => !compact.Contains(Compact(line.Text), StringComparison.Ordinal)).ToList();
        return missing.Count == 0 ? null : string.Join("；", missing.Select(line =>
            (line.Speaker.Length > 0 ? line.Speaker + "：" : "旁白：") + line.Text));
    }

    private static string Compact(string value) => new(value.Where(character => !char.IsWhiteSpace(character)).ToArray());

    private static string ReferenceGuide(WorkflowCanvasState canvas, WorkflowNode node, int trailingCount)
    {
        if (node.References.Count == 0) return string.Empty;

        var slots = ReferenceImagePicker.ResolveSlots(canvas, node, Locate)
            .Where(slot => !string.IsNullOrWhiteSpace(slot.Path)).ToList();
        if (slots.Count == 0) return string.Empty;

        var contents = canvas.ResolveReferencePairs(node)
            .Where(pair => pair.Content is not null)
            .GroupBy(pair => ReferenceCandidate.KeyOf(pair.Reference), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Content!, StringComparer.Ordinal);

        var lines = new List<string>();
        for (var index = 0; index < slots.Count; index++)
        {
            contents.TryGetValue(slots[index].Key, out var content);
            var name = string.IsNullOrWhiteSpace(content?.Entity.Name) ? slots[index].Label : content.Entity.Name;
            var usage = content?.Entity.Kind switch
            {
                EntityKind.Character => $"character reference for {name}; use it only for face, hair, body proportions, clothing and accessories",
                EntityKind.Scene => $"scene reference for {name}; use it only for environment layout, lighting, props and color atmosphere",
                EntityKind.Prop => $"prop reference for {name}; use it only for its shape, material, color and details",
                _ => $"reference for {name}"
            };
            lines.Add($"Reference image {index + 1}: {usage}.");
        }
        lines.Add("Do not swap these roles: never use a scene reference to redesign a character.");
        if (trailingCount > 0)
            lines.Add($"{trailingCount} additional reference image(s) follow; treat them only as supporting material.");
        return string.Join("\n", lines);
    }

    internal static void RecordEvidence(Dictionary<string, JsonElement> inputs, string[] paths,
        (WorkflowCanvasState Canvas, WorkflowNode Node, long Revision)? target = null,
        IReadOnlyList<string>? audios = null, IReadOnlyList<string>? videos = null)
    {
        inputs["referenceImages"] = JsonSerializer.SerializeToElement(paths);
        inputs["referenceImageHashes"] = JsonSerializer.SerializeToElement(Hashes(paths));
        // 音频 / 视频参考同样要留字节依据：它们和图片一样会被播放器/模型直接读取，换掉看不出来。
        if (audios is { Count: > 0 })
        {
            inputs["referenceAudios"] = JsonSerializer.SerializeToElement(audios);
            inputs["referenceAudioHashes"] = JsonSerializer.SerializeToElement(Hashes(audios));
        }
        if (videos is { Count: > 0 })
        {
            inputs["referenceVideos"] = JsonSerializer.SerializeToElement(videos);
            inputs["referenceVideoHashes"] = JsonSerializer.SerializeToElement(Hashes(videos));
        }
        if (target is not { } snapshot) return;
        inputs["sourceFingerprints"] = JsonSerializer.SerializeToElement(ReferenceStaleness.Snapshot(snapshot.Canvas, snapshot.Node));
        // 生成记录：这次用了哪个角色、哪张参考图（及其用途）、外观描述与模型，出了问题能按记录复现。
        // 角色的外观描述只作为**辅助约束**记录，不改写用户提示词，避免提示词与参考图互相打架。
        var selections = ReferenceImagePicker.ResolveSlots(snapshot.Canvas, snapshot.Node, Locate);
        var pairs = snapshot.Canvas.ResolveReferencePairs(snapshot.Node)
            .GroupBy(pair => ReferenceCandidate.KeyOf(pair.Reference), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Content, StringComparer.Ordinal);
        var records = selections.Select(slot =>
        {
            pairs.TryGetValue(slot.Key, out var content);
            return new ReferenceSelectionRecord(
                content?.Entity.Id.ToString() ?? string.Empty,
                content?.Entity.Name ?? slot.Label,
                content?.Entity.Kind.ToString() ?? string.Empty,
                content?.Variant.Id.ToString() ?? string.Empty,
                content?.Version?.Id.ToString() ?? string.Empty,
                slot.AttachmentId?.ToString() ?? string.Empty,
                slot.Purpose.ToString(),
                ReferencePurposes.Name(slot.Purpose),
                slot.Path ?? string.Empty,
                content?.Description?.Trim() ?? string.Empty);
        }).ToList();
        inputs["referenceSelections"] = JsonSerializer.SerializeToElement(records);
        inputs["characterReferences"] = JsonSerializer.SerializeToElement(
            records.Where(record => record.kind == nameof(EntityKind.Character)).ToList());
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
        Verify(inputs, "referenceAudios", "referenceAudioHashes", "参考音频");
        Verify(inputs, "referenceVideos", "referenceVideoHashes", "参考视频");
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

    private static Dictionary<string, string> Hashes(IEnumerable<string> paths) =>
        paths.Distinct(StringComparer.Ordinal)
            .ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                StringComparer.Ordinal);

    /// <summary>音频 / 视频参考的字节依据核对：留过哈希就逐个比，没留（旧任务）就不拦。</summary>
    private static void Verify(IReadOnlyDictionary<string, JsonElement> inputs,
        string pathsKey, string hashesKey, string label)
    {
        if (!inputs.TryGetValue(pathsKey, out var raw)) return;
        var paths = raw.Deserialize<string[]>() ?? [];
        var hashes = inputs.TryGetValue(hashesKey, out var evidence)
            ? evidence.Deserialize<Dictionary<string, string>>() ?? new() : new Dictionary<string, string>();
        if (hashes.Count != paths.Distinct(StringComparer.Ordinal).Count() || paths.Any(path =>
            !hashes.TryGetValue(path, out var expected) || !File.Exists(path) ||
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) != expected))
            throw new InvalidDataException($"{label}字节在提交后发生变化，请重新发起生成");
    }
}
