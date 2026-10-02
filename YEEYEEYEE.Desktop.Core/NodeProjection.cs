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
            var thumbRef = content.Attachments
                .FirstOrDefault(a => a.Kind == AttachmentKind.Image)?.Reference;
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
}
