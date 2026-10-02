using System.Text;

namespace DreamForge.Desktop;

public enum SearchHitKind
{
    /// <summary>画布上的节点（能直接定位过去）。</summary>
    Node,

    /// <summary>工作树条目（左栏那棵树里的项）。</summary>
    WorkTreeItem,

    /// <summary>项目设定库里的资源（角色 / 场景 / 道具）。</summary>
    Entity
}

/// <summary>一条搜索结果。带 NodeId 的能直接跳到画布上那个节点。</summary>
public sealed record SearchHit(
    SearchHitKind Kind,
    string Title,
    string Detail,
    string Snippet,
    Guid? NodeId = null,
    Guid? WorkTreeItemId = null,
    Guid? EntityId = null)
{
    public bool CanFocus => NodeId is not null;

    public string KindName => Kind switch
    {
        SearchHitKind.Node => "节点",
        SearchHitKind.WorkTreeItem => "工作树",
        _ => "设定库"
    };
}

/// <summary>
/// 画布 / 工作树 / 设定库的搜索。
///
/// 放在共享代码里：搜索是**数据匹配**，不是界面；放在这里才能被测试固定住，
/// 也免得界面层自己写一套「顺手查一下标题」的模糊匹配。
/// 命中一律**只匹配真实存在的文字**（标题、正文、设定名与别名），不做同义词扩展——
/// 搜不到就是搜不到，比搜出一堆看起来相关的东西更省时间。
/// </summary>
public static class CanvasSearch
{
    public const int DefaultLimit = 12;

    private const int SnippetLength = 40;

    public static IReadOnlyList<SearchHit> Find(WorkflowCanvasState canvas, string? query, int limit = DefaultLimit)
    {
        var hits = new List<SearchHit>();
        var keyword = (query ?? string.Empty).Trim();
        if (keyword.Length == 0) return hits;
        var cap = Math.Max(1, limit);

        // 顺序就是优先级：节点最有用（能定位），其次是工作树条目，最后是设定库。
        foreach (var node in canvas.Nodes)
        {
            if (hits.Count >= cap) break;
            var where = MatchIn(node.Title, node.Content, keyword);
            if (where.Length == 0) continue;
            hits.Add(new SearchHit(
                SearchHitKind.Node,
                node.Title,
                $"{KindLabelOf(node.Category)} · {StatusNameOf(node.ExecutionStatus)}{(node.WorkTreeItemId is null ? string.Empty : " · 已绑工作树")}",
                where,
                NodeId: node.Id));
        }

        foreach (var item in canvas.WorkTree)
        {
            if (hits.Count >= cap) break;
            var where = MatchIn(item.Name, item.Prompt.Length > 0 ? item.Prompt : item.LocalState, keyword);
            if (where.Length == 0) continue;
            var linked = canvas.Nodes.FirstOrDefault(node => node.WorkTreeItemId == item.Id);
            hits.Add(new SearchHit(
                SearchHitKind.WorkTreeItem,
                item.Name,
                $"{WorkTreeItem.KindName(item.Kind)}{(linked is null ? " · 未放上画布" : " · 已在画布上")}",
                where,
                NodeId: linked?.Id,
                WorkTreeItemId: item.Id));
        }

        foreach (var entity in canvas.Entities)
        {
            if (hits.Count >= cap) break;
            var variantText = entity.Variants.Count == 0
                ? string.Empty
                : string.Join(" ", entity.Variants.Select(variant => variant.Description));
            var where = MatchIn(entity.Name, $"{entity.Aliases}\n{entity.Core}\n{variantText}", keyword);
            if (where.Length == 0) continue;
            var linked = canvas.Nodes.FirstOrDefault(node => node.References.Any(reference => reference.EntityId == entity.Id));
            hits.Add(new SearchHit(
                SearchHitKind.Entity,
                entity.Name,
                $"{WorkflowEntity.KindName(entity.Kind)} · {entity.Variants.Count} 个变体{(linked is null ? string.Empty : " · 已被引用")}",
                where,
                NodeId: linked?.Id,
                EntityId: entity.Id));
        }

        return hits;
    }

    /// <summary>在标题或正文里找关键词；命中就给一句上下文片段，没命中返回空串。</summary>
    private static string MatchIn(string title, string body, string keyword)
    {
        if (Contains(title, keyword)) return $"标题：{Trim(title)}";
        var index = body?.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) ?? -1;
        if (index < 0) return string.Empty;
        return Snippet(body!, index, keyword.Length);
    }

    private static bool Contains(string? text, string keyword) =>
        !string.IsNullOrEmpty(text) && text.Contains(keyword, StringComparison.OrdinalIgnoreCase);

    private static string Snippet(string body, int index, int keywordLength)
    {
        var flat = body.Replace("\r\n", " ").Replace('\n', ' ').Trim();
        var start = Math.Max(0, index - SnippetLength / 2);
        if (start > flat.Length) start = Math.Max(0, flat.Length - SnippetLength);
        var length = Math.Min(SnippetLength, flat.Length - start);
        if (length <= 0) return string.Empty;
        var text = flat.Substring(start, length).Trim();
        var builder = new StringBuilder();
        if (start > 0) builder.Append('…');
        builder.Append(text);
        if (start + length < flat.Length) builder.Append('…');
        return builder.ToString();
    }

    private static string Trim(string value)
    {
        var text = value.Trim();
        return text.Length <= SnippetLength ? text : text[..SnippetLength] + "…";
    }

    private static string KindLabelOf(NodeCategory category) => NodeAssistPlanner.KindLabelOf(category);

    /// <summary>节点状态的中文名（界面与搜索结果共用一份，免得两处叫法不一致）。</summary>
    public static string StatusNameOf(NodeExecutionStatus status) => status switch
    {
        NodeExecutionStatus.WaitingForUser => "待补充",
        NodeExecutionStatus.Generating => "生成中",
        NodeExecutionStatus.Completed => "已完成",
        NodeExecutionStatus.Failed => "失败",
        NodeExecutionStatus.NeedsReview => "待确认",
        _ => "草稿"
    };
}
