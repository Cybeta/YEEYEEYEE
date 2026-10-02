namespace YEEYEEYEE.Desktop;

/// <summary>Entities are shared resources; appearances are chapter-local references, never resource copies.</summary>
public static class UnifiedWorkTree
{
    public static void RefreshResources(WorkflowCanvasState state)
    {
        foreach (var entity in state.Entities)
        {
            var item = state.WorkTree.FirstOrDefault(x => x.Kind == WorkTreeKind.Resource && x.ResourceId == entity.Id);
            if (item is null)
            {
                item = new WorkTreeItem { Kind = WorkTreeKind.Resource, ResourceId = entity.Id, SourceEntityId = entity.Id };
                state.WorkTree.Add(item);
            }
            item.Name = entity.Name;
            item.Prompt = entity.Core;
        }
        state.WorkTree.RemoveAll(x => x.Kind == WorkTreeKind.Resource && !state.Entities.Any(e => e.Id == x.ResourceId));
    }

    public static WorkTreeItem AddAppearance(WorkflowCanvasState state, Guid resourceId, Guid chapterId, Guid? variantId = null, Guid? versionId = null)
    {
        var entity = state.FindEntity(resourceId) ?? throw new InvalidOperationException("资源不存在。");
        var chapter = state.WorkTree.SingleOrDefault(x => x.Id == chapterId && x.Kind == WorkTreeKind.Chapter)
            ?? throw new InvalidOperationException("请先选择章节。");
        var variant = variantId is { } id ? entity.Variants.SingleOrDefault(x => x.Id == id) : entity.Variants.FirstOrDefault();
        if (variant is null) throw new InvalidOperationException("资源变体不存在。");
        if (versionId is { } version && variant.FindVersion(version) is null) throw new InvalidOperationException("锁定版本不存在。");
        var item = new WorkTreeItem { Kind = WorkTreeKind.Appearance, Name = entity.Name, ParentId = chapter.Id,
            ChapterId = chapter.Id, Chapter = chapter.Name, ResourceId = entity.Id, SourceEntityId = entity.Id,
            SourceVariantId = variant.Id, SourceVersionId = versionId, VersionLocked = versionId is not null };
        state.WorkTree.Add(item);
        return item;
    }

    public static WorkflowNode CreateNode(WorkflowCanvasState state, WorkTreeItem item, float x, float y)
    {
        if (!state.WorkTree.Contains(item) || item.Kind == WorkTreeKind.Resource)
            throw new InvalidOperationException("请先将资源添加到章节出场，再拖入画布。");
        var node = new WorkflowNode { WorkTreeItemId = item.Id, X = x, Y = y, ManualPosition = true };
        Project(state, item, node);
        state.Nodes.Add(node);
        return node;
    }

    public static void Edit(WorkflowCanvasState state, WorkTreeItem item, string name, string content)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("名称不能为空。");
        if (state.Nodes.Any(n => n.WorkTreeItemId == item.Id && n.IsLocked)) throw new InvalidOperationException("关联节点已定稿，请先解锁。");
        if (item.Kind == WorkTreeKind.Resource)
        {
            var entity = state.FindEntity(item.ResourceId ?? Guid.Empty) ?? throw new InvalidOperationException("资源不存在。");
            entity.Name = name.Trim(); entity.Core = content;
        }
        item.Name = name.Trim();
        if (item.Kind == WorkTreeKind.Appearance) item.LocalState = content;
        else item.Prompt = content;
        ProjectAll(state, item);
    }

    public static void SetVersion(WorkflowCanvasState state, WorkTreeItem item, Guid? versionId)
    {
        if (item.Kind != WorkTreeKind.Appearance) throw new InvalidOperationException("请选择章节出场引用。");
        if (state.Nodes.Any(n => n.WorkTreeItemId == item.Id && n.IsLocked)) throw new InvalidOperationException("关联节点已定稿。");
        var variant = state.FindEntity(item.ResourceId ?? Guid.Empty)?.Variants.FirstOrDefault(v => v.Id == item.SourceVariantId);
        if (variant is null || versionId is { } id && variant.FindVersion(id) is null) throw new InvalidOperationException("资源版本不存在。");
        item.SourceVersionId = versionId; item.VersionLocked = versionId is not null;
        item.Version = versionId is { } key ? variant.FindVersion(key)!.Label : "最新";
        ProjectAll(state, item);
    }

    public static void FromNode(WorkflowCanvasState state, WorkflowNode node)
    {
        var item = state.WorkTree.FirstOrDefault(x => x.Id == node.WorkTreeItemId);
        if (item is null || item.Kind is not (WorkTreeKind.Appearance or WorkTreeKind.Chapter) || node.IsLocked) return;
        if (state.Nodes.Any(n => n.Id != node.Id && n.WorkTreeItemId == item.Id && n.IsLocked))
        {
            Project(state, item, node);
            return;
        }
        item.Name = node.Title;
        if (item.Kind == WorkTreeKind.Appearance)
        {
            item.LocalState = node.Content;
            var reference = node.References.FirstOrDefault(r => r.EntityId == item.ResourceId);
            if (reference is not null)
            {
                item.SourceVariantId = reference.VariantId;
                item.SourceVersionId = reference.VariantVersionId;
                item.VersionLocked = reference.VariantVersionId is not null;
            }
        }
        else item.Prompt = node.Content;
        ProjectAll(state, item);
    }

    public static void ProjectAll(WorkflowCanvasState state, WorkTreeItem item)
    {
        foreach (var node in state.Nodes.Where(n => n.WorkTreeItemId == item.Id && !n.IsLocked)) Project(state, item, node);
        if (item.Kind == WorkTreeKind.Chapter)
            foreach (var appearance in state.WorkTree.Where(x => x.ChapterId == item.Id))
            { appearance.Chapter = item.Name; ProjectAll(state, appearance); }
    }

    public static void Project(WorkflowCanvasState state, WorkTreeItem item, WorkflowNode node)
    {
        node.Title = item.Name;
        node.Content = item.Kind == WorkTreeKind.Appearance ? item.LocalState : item.Prompt;
        node.Chapter = item.Kind == WorkTreeKind.Chapter ? item.Name : item.Chapter;
        if (item.Kind == WorkTreeKind.Chapter) node.Category = NodeCategory.Chapter;
        if (item.Kind != WorkTreeKind.Appearance) return;
        var entity = state.FindEntity(item.ResourceId ?? Guid.Empty) ?? throw new InvalidOperationException("出场资源缺失。");
        node.Category = entity.Kind switch { EntityKind.Character => NodeCategory.Character, EntityKind.Scene => NodeCategory.Scene, _ => NodeCategory.Prop };
        var reference = node.References.FirstOrDefault(r => r.EntityId == entity.Id);
        if (reference is null) { reference = new NodeReference { EntityId = entity.Id }; node.References.Add(reference); }
        reference.VariantId = item.SourceVariantId ?? Guid.Empty;
        reference.VariantVersionId = item.SourceVersionId;
    }
}
