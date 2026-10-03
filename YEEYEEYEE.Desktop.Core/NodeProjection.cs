using System.Text.Json;

namespace YEEYEEYEE.Desktop;

/// <summary>
/// 把领域模型 WorkflowNode 投影为协议层 OperationRecord，
/// 包含层级 recordType 和 references 缩略图条数据。
/// 同时把 WorkTree 章节条目投影为 L3 层节点。
/// </summary>
public static class NodeProjection
{
    /// <summary>
    /// 把 WorkflowNode 列表转换为协议层 records 数组。
    /// recordType 由 NodeCategory 映射，references 从 canvas.ResolveReferences 填充。
    /// WorkTree 中的章节条目也投影为 L3 层 record。
    /// </summary>
    public static List<object> ProjectRecords(IEnumerable<WorkflowNode> nodes, WorkflowCanvasState canvas)
    {
        var records = new List<object>();
        var seenChapterIds = new HashSet<string>();

        foreach (var node in nodes)
        {
            var recordType = CategoryToRecordType(node.Category);
            var references = ProjectReferences(node, canvas);
            var record = new Dictionary<string, object?>
            {
                ["title"] = node.Title,
                ["content"] = node.Content,
                ["x"] = node.X,
                ["y"] = node.Y,
                ["chapter"] = node.Chapter,
                ["status"] = node.ExecutionStatus.ToString(),
            };
            // 稳定章节 ID（C-4）：Web 端按 ID 筛选与排序，不按 Chapter 文本绑定。
            if (CanvasChapters.ResolveChapterId(canvas, node) is { } chapterId)
                record["chapterId"] = chapterId.ToString();
            if (node.ParentNodeId.HasValue)
                record["parentId"] = node.ParentNodeId.Value.ToString();
            if (references.Count > 0)
                record["references"] = references;
            // 节点出过哪些图 / 视频。**只投影元数据，不投影 Reference**——那一串是服务端的
            // 文件路径（或一段 data URL），网页端既读不到也不该看到；取字节走按 ID 的那条接口。
            // prompt / negativePrompt 也**故意不投影**：它们动辄上千字，而每一次 GET 场景都要带上
            // 所有节点——真要看那两串时按附件单独取，不要为了「也许用得上」把每份场景都撑大。
            if (node.Attachments.Count > 0)
                record["attachments"] = node.Attachments
                    .OrderByDescending(attachment => attachment.AddedAt)
                    .Select(attachment => (object)new
                    {
                        id = attachment.Id.ToString(),
                        kind = attachment.Kind.ToString(),
                        name = attachment.Name,
                        source = attachment.Source,
                        addedAt = attachment.AddedAt
                    })
                    .ToList();

            records.Add(new
            {
                recordId = node.Id.ToString(),
                recordType,
                record
            });

            if (node.Category == NodeCategory.Chapter)
                seenChapterIds.Add(node.WorkTreeItemId?.ToString() ?? node.Id.ToString());
        }

        ProjectWorkTreeChapters(canvas, records, seenChapterIds);

        return records;
    }

    /// <summary>把 WorkTree 中的章节条目投影为 L3 层 record，跳过已有同 ID 的；顺序用章节的显式顺序。</summary>
    private static void ProjectWorkTreeChapters(WorkflowCanvasState canvas, List<object> records, HashSet<string> seenIds)
    {
        var orderedIds = CanvasChapters.List(canvas).Select(chapter => chapter.Id).ToList();
        var ordered = orderedIds
            .Select(id => canvas.WorkTree.FirstOrDefault(item => item.Id == id))
            .Where(item => item is not null)
            .Select(item => item!)
            .ToList();

        foreach (var item in ordered)
        {
            var key = item.Id.ToString();
            if (seenIds.Contains(key)) continue;
            seenIds.Add(key);

            records.Add(new
            {
                recordId = $"wt-{item.Id}",
                recordType = "chapter",
                record = new Dictionary<string, object?>
                {
                    ["title"] = item.Name,
                    ["content"] = item.Prompt ?? string.Empty,
                    ["chapter"] = item.Chapter,
                    ["chapterId"] = item.Id.ToString(),
                    ["workTreeItemId"] = item.Id.ToString(),
                    ["order"] = item.Order,
                    ["status"] = "Draft"
                }
            });
        }
    }

    /// <summary>
    /// 把节点的 NodeReference 列表解析成缩略图条数据。
    /// 缩略图引用取变体的第一张图片附件（如果有）。
    /// </summary>
    private static List<object> ProjectReferences(WorkflowNode node, WorkflowCanvasState canvas)
    {
        var result = new List<object>();
        var pairs = canvas.ResolveReferencePairs(node);
        foreach (var (reference, content) in pairs)
        {
            if (content is null)
            {
                result.Add(new
                {
                    entityId = reference.EntityId == Guid.Empty ? null : reference.EntityId.ToString(),
                    name = "未解析引用",
                    kind = "Unknown",
                    variantId = reference.VariantId == Guid.Empty ? null : reference.VariantId.ToString(),
                    variantVersionId = reference.VariantVersionId?.ToString(),
                    thumbnailRef = string.Empty,
                    variantLabel = "引用目标缺失",
                    unresolved = true,
                    versions = Array.Empty<object>()
                });
                continue;
            }
            // 哪一张图当预览由 EntityAssets.PreviewImage 说了算：网页端取缩略图那条接口问的是同一个方法。
            var thumbRef = EntityAssets.PreviewImage(content)?.Reference;
            result.Add(new
            {
                entityId = content.Entity.Id.ToString(),
                name = content.Entity.Name,
                kind = content.Entity.Kind.ToString(),
                variantId = reference.VariantId.ToString(),
                variantVersionId = reference.VariantVersionId?.ToString(),
                thumbnailRef = thumbRef ?? string.Empty,
                variantLabel = content.Label,
                versions = content.Variant.Versions
                    .OrderByDescending(version => version.Number)
                    .Select(version => new
                    {
                        id = version.Id.ToString(),
                        label = version.Label,
                        number = version.Number,
                        note = version.Note,
                        createdAt = version.CreatedAt
                    })
                    .ToList()
            });
        }
        return result;
    }

    /// <summary>
    /// NodeCategory → 协议层 recordType 的映射，
    /// 前端用 layerOf(recordType) 推算层级。
    /// </summary>
    private static string CategoryToRecordType(NodeCategory category) => category switch
    {
        NodeCategory.StoryPlan => "story-plan",
        NodeCategory.StoryOutline => "story-outline",
        NodeCategory.Chapter => "chapter",
        NodeCategory.Storyboard => "storyboard",
        NodeCategory.Character => "character",
        NodeCategory.Scene => "scene-description",
        NodeCategory.Prop => "prop",
        NodeCategory.Product => "product",
        _ => "general"
    };

    /// <summary>
    /// 反向：网页端回传的 recordType 是什么类别。
    ///
    /// 与上面那张表**必须是一对**，所以紧挨着放——分开写迟早会出现「甲认得乙、乙认不得甲」。
    /// 认不出的回 <c>null</c>：调用方据此如实拒绝，而不是猜一个 <c>general</c> 收下
    /// （猜错了会静默地把一个角色节点变成通用节点）。
    /// </summary>
    public static NodeCategory? RecordTypeToCategory(string? recordType) => recordType switch
    {
        "story-plan" => NodeCategory.StoryPlan,
        "story-outline" => NodeCategory.StoryOutline,
        "chapter" => NodeCategory.Chapter,
        "storyboard" => NodeCategory.Storyboard,
        "character" => NodeCategory.Character,
        "scene-description" => NodeCategory.Scene,
        "prop" => NodeCategory.Prop,
        "product" => NodeCategory.Product,
        "general" => NodeCategory.General,
        _ => null
    };
}
