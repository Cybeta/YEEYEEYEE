using System.Text.Json;

namespace YEEYEEYEE.Desktop;

/// <summary>同步方向：画布 → 工作树、工作树 → 画布，或两侧一起规划。</summary>
public enum SyncDirection { CanvasToWorkTree, WorkTreeToCanvas, Both }

/// <summary>同步计划的单项改动。</summary>
public enum SyncChangeKind
{
    /// <summary>为章节节点新建工作树章节条目。</summary>
    CreateChapterItem,

    /// <summary>给只有显示文本的节点建立稳定锚点。</summary>
    LinkNodeAnchor,

    /// <summary>补齐或调整章节显式顺序。</summary>
    NormalizeChapterOrder,

    /// <summary>章节条目改名（需要人工确认，可能影响其它节点）。</summary>
    RenameChapterItem,

    /// <summary>把工作树章节名同步到章节节点标题（需要人工确认）。</summary>
    UpdateChapterNodeTitle,

    /// <summary>把所属章节名同步到节点显示文本。</summary>
    UpdateNodeChapterText,

    /// <summary>清空指向已删除条目的悬空锚点（需要人工确认，保留文本）。</summary>
    DetachMissingAnchor
}

/// <summary>
/// 同步计划的单项：包含改动前后的可比较值、原因，以及是否需要人工确认。
/// <see cref="RequiresConfirmation"/> 为真的项只在显式确认后才会被应用。
/// </summary>
public sealed record SyncChange(
    SyncChangeKind Kind,
    string ObjectType,
    string ObjectId,
    string FieldPath,
    string Before,
    string After,
    string Reason,
    bool RequiresConfirmation)
{
    public override string ToString() =>
        $"{(RequiresConfirmation ? "[待确认]" : "[可直接应用]")} {Kind} {ObjectType}({ObjectId}) {FieldPath}：{Before} → {After}（{Reason}）";
}

/// <summary>同步冲突代码（只增不改）。</summary>
public static class CanvasSyncConflictCodes
{
    /// <summary>章节文本对应多个同名章节条目，禁止按名称绑定。</summary>
    public const string ChapterNameAmbiguous = "SYNC_CHAPTER_NAME_AMBIGUOUS";

    /// <summary>节点锚点指向的条目已不存在。</summary>
    public const string AnchorDangling = "SYNC_ANCHOR_DANGLING";

    /// <summary>章节节点标题与工作树条目名称不一致，需要在两侧之间做选择。</summary>
    public const string ChapterTitleMismatch = "SYNC_CHAPTER_TITLE_MISMATCH";

    /// <summary>同一章节 ID 出现多次（重复 ID）。</summary>
    public const string ChapterIdDuplicated = "SYNC_CHAPTER_ID_DUPLICATED";

    /// <summary>节点归属章节与它锚点所在章节不一致（跨章节错绑）。</summary>
    public const string CrossChapterBinding = "SYNC_CROSS_CHAPTER_BINDING";
}

/// <summary>同步冲突。阻断冲突存在时整批同步会被拒绝，不会留下半同步状态。</summary>
public sealed record SyncConflict(
    string Code,
    string ObjectType,
    string ObjectId,
    string FieldPath,
    string Detail,
    string Handling,
    bool Blocking)
{
    public override string ToString() =>
        $"{(Blocking ? "[阻断]" : "[提示]")} {Code} {ObjectType}({ObjectId}) {FieldPath}：{Detail}（处理：{Handling}）";
}

/// <summary>
/// 同步计划：只读预览结果，不落盘、不改动任何对象。
/// 计划通过 <see cref="CanvasWorkTreeSync.Apply"/> 一次性应用，或在界面上逐项确认后应用。
/// </summary>
public sealed record SyncPlan(IReadOnlyList<SyncChange> Changes, IReadOnlyList<SyncConflict> Conflicts, SyncDirection Direction)
{
    public string BaselineHash { get; init; } = string.Empty;
    public bool Changed => Changes.Count > 0;

    public bool HasBlockingConflicts => Conflicts.Any(conflict => conflict.Blocking);

    /// <summary>可直接应用的项（不依赖人工确认）。</summary>
    public IReadOnlyList<SyncChange> Automatic => Changes.Where(change => !change.RequiresConfirmation).ToArray();

    /// <summary>需要人工确认的项。</summary>
    public IReadOnlyList<SyncChange> PendingConfirmation => Changes.Where(change => change.RequiresConfirmation).ToArray();

    public string ToText()
    {
        var lines = new List<string>
        {
            $"同步计划（{DirectionText(Direction)}）：改动 {Changes.Count} 项（可直接应用 {Automatic.Count}、待确认 {PendingConfirmation.Count}），"
            + $"冲突 {Conflicts.Count} 项（阻断 {Conflicts.Count(conflict => conflict.Blocking)} 项），基线 {BaselineHash[..Math.Min(12, BaselineHash.Length)]}。"
        };
        lines.AddRange(Changes.Select(change => "· " + change));
        lines.AddRange(Conflicts.Select(conflict => "! " + conflict));
        return string.Join(Environment.NewLine, lines);
    }

    private static string DirectionText(SyncDirection direction) => direction switch
    {
        SyncDirection.CanvasToWorkTree => "画布 → 工作树",
        SyncDirection.WorkTreeToCanvas => "工作树 → 画布",
        _ => "双向"
    };
}

/// <summary>同步应用结果：结果画布、已应用项、被跳过项（等待确认）与冲突。</summary>
public sealed record SyncApplyResult(
    WorkflowCanvasState Canvas,
    IReadOnlyList<SyncChange> Applied,
    IReadOnlyList<SyncChange> Skipped,
    IReadOnlyList<SyncConflict> Conflicts,
    bool Refused)
{
    public bool Changed => Applied.Count > 0;

    public string ToText()
    {
        var lines = new List<string>
        {
            Refused
                ? $"同步被拒绝：存在 {Conflicts.Count(conflict => conflict.Blocking)} 项阻断冲突，画布未做任何改动。"
                : $"同步完成：应用 {Applied.Count} 项，跳过 {Skipped.Count} 项（等待确认），冲突 {Conflicts.Count} 项。"
        };
        lines.AddRange(Applied.Select(change => "· 已应用：" + change));
        lines.AddRange(Skipped.Select(change => "· 已跳过：" + change));
        lines.AddRange(Conflicts.Select(conflict => "! " + conflict));
        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// 工作树与章节画布的双向同步（大目标 B / 任务 2–4）。
///
/// 判定规则：
/// · 一律按稳定 ID 与上下文判定：节点锚点（<see cref="WorkflowNode.WorkTreeItemId"/>）优先，
///   其次沿父节点链解析所属章节；只有「唯一同名章节条目」这类文本依据才会标成待确认，绝不静默绑定；
/// · 同名多章节、悬空锚点、重复章节 ID、跨章节错绑都报为冲突；
/// · 计划阶段纯只读（预览不落盘），应用阶段只在副本上进行，存在阻断冲突时整批拒绝，
///   因此不会出现半同步状态；
/// · 应用结果由调用方复用 <see cref="CanvasSaveService.Save"/> 保存，备份与失败保护继续生效。
/// </summary>
public static class CanvasWorkTreeSync
{
    /// <summary>生成同步计划（只读预览）。</summary>
    public static SyncPlan Plan(WorkflowCanvasState canvas, SyncDirection direction = SyncDirection.Both)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        var changes = new List<SyncChange>();
        var conflicts = new List<SyncConflict>();

        var chapters = CanvasChapters.ChapterItems(canvas);
        var duplicatedChapterIds = chapters.GroupBy(item => item.Id).Where(group => group.Count() > 1).Select(group => group.Key).ToList();
        foreach (var id in duplicatedChapterIds)
            conflicts.Add(new SyncConflict(CanvasSyncConflictCodes.ChapterIdDuplicated, "WorkTreeItem", id.ToString(), "WorkTree[].Id",
                $"章节 ID {id} 在画布中出现多次，无法唯一确定同步目标。", "先按校验报告修复重复 ID", Blocking: true));

        if (direction is SyncDirection.WorkTreeToCanvas or SyncDirection.Both)
            PlanWorkTreeToCanvas(canvas, chapters, changes, conflicts);

        if (direction is SyncDirection.CanvasToWorkTree or SyncDirection.Both)
            PlanCanvasToWorkTree(canvas, chapters, changes, conflicts);

        if (direction is SyncDirection.CanvasToWorkTree)
        {
            foreach (var chapter in chapters.Where(item => item.Order == 0))
                changes.Add(new SyncChange(SyncChangeKind.NormalizeChapterOrder, "WorkTreeItem", chapter.Id.ToString(), "Order",
                    "0（未指定）", "按现有顺序补齐", "章节缺少显式顺序，按确定性规则补齐", RequiresConfirmation: false));
        }

        var deduped = Deduplicate(changes);
        return new SyncPlan(deduped, conflicts, direction)
        {
            BaselineHash = BaselineHash(canvas)
        };
    }

    /// <summary>
    /// 应用同步计划。存在阻断冲突时整批拒绝；<paramref name="applyUnconfirmed"/> 为假时跳过待确认项。
    /// 所有改动都在副本上完成，调用方传入的画布对象不会被改动。
    /// </summary>
    public static SyncApplyResult Apply(WorkflowCanvasState canvas, SyncPlan plan, bool applyUnconfirmed = false)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(plan);

        var working = CanvasCloner.Clone(canvas);
        if (!string.Equals(plan.BaselineHash, BaselineHash(canvas), StringComparison.Ordinal))
        {
            var stale = new SyncConflict(
                "SYNC_PLAN_STALE", "Canvas", "", "BaselineHash",
                "画布在生成同步计划后发生变化，必须重新生成计划。",
                "重新预览同步计划", Blocking: true);
            return new SyncApplyResult(working, Array.Empty<SyncChange>(), plan.Changes,
                plan.Conflicts.Append(stale).ToArray(), Refused: true);
        }
        if (plan.HasBlockingConflicts)
            return new SyncApplyResult(working, Array.Empty<SyncChange>(), plan.Changes, plan.Conflicts, Refused: true);

        var applied = new List<SyncChange>();
        var skipped = new List<SyncChange>();

        // 顺序固定：先建章节与顺序，再连锚点，最后同步显示文本，保证引用的目标先存在。
        foreach (var change in Ordered(plan.Changes))
        {
            if (change.RequiresConfirmation && !applyUnconfirmed) { skipped.Add(change); continue; }
            if (ApplyChange(working, change)) applied.Add(change);
            else skipped.Add(change);
        }

        return new SyncApplyResult(working, applied, skipped, plan.Conflicts, Refused: false);
    }

    private static IEnumerable<SyncChange> Ordered(IReadOnlyList<SyncChange> changes)
    {
        int Rank(SyncChangeKind kind) => kind switch
        {
            SyncChangeKind.CreateChapterItem => 0,
            SyncChangeKind.NormalizeChapterOrder => 1,
            SyncChangeKind.RenameChapterItem => 2,
            SyncChangeKind.LinkNodeAnchor => 3,
            SyncChangeKind.DetachMissingAnchor => 4,
            SyncChangeKind.UpdateChapterNodeTitle => 5,
            _ => 6
        };

        return changes.OrderBy(change => Rank(change.Kind)).ThenBy(change => change.ObjectId, StringComparer.Ordinal);
    }

    private static bool ApplyChange(WorkflowCanvasState canvas, SyncChange change)
    {
        switch (change.Kind)
        {
            case SyncChangeKind.CreateChapterItem:
            {
                var item = new WorkTreeItem
                {
                    Id = ParseId(change.ObjectId),
                    Kind = WorkTreeKind.Chapter,
                    Name = change.After,
                    Order = NextOrder(canvas, null)
                };
                canvas.WorkTree.Add(item);
                return true;
            }

            case SyncChangeKind.NormalizeChapterOrder:
                return CanvasChapters.EnsureExplicitOrder(canvas);

            case SyncChangeKind.RenameChapterItem:
            {
                var item = canvas.WorkTree.FirstOrDefault(candidate => candidate.Id == ParseId(change.ObjectId));
                if (item is null) return false;
                item.Name = change.After;
                return true;
            }

            case SyncChangeKind.LinkNodeAnchor:
            {
                var node = canvas.Nodes.FirstOrDefault(candidate => candidate.Id == ParseId(change.ObjectId));
                var item = canvas.WorkTree.FirstOrDefault(candidate => candidate.Id == ParseId(change.After));
                if (node is null || item is null) return false;
                node.WorkTreeItemId = item.Id;
                node.Chapter = item.Name;
                return true;
            }

            case SyncChangeKind.UpdateNodeChapterText:
            {
                var node = canvas.Nodes.FirstOrDefault(candidate => candidate.Id == ParseId(change.ObjectId));
                if (node is null) return false;
                node.Chapter = change.After;
                return true;
            }

            case SyncChangeKind.UpdateChapterNodeTitle:
            {
                var node = canvas.Nodes.FirstOrDefault(candidate => candidate.Id == ParseId(change.ObjectId));
                if (node is null) return false;
                node.Title = change.After;
                return true;
            }

            case SyncChangeKind.DetachMissingAnchor:
            {
                var node = canvas.Nodes.FirstOrDefault(candidate => candidate.Id == ParseId(change.ObjectId));
                if (node is null) return false;
                node.WorkTreeItemId = null;
                return true;
            }

            default:
                return false;
        }
    }

    private static void PlanWorkTreeToCanvas(
        WorkflowCanvasState canvas,
        IReadOnlyList<WorkTreeItem> chapters,
        List<SyncChange> changes,
        List<SyncConflict> conflicts)
    {
        var workTreeIds = canvas.WorkTree.Select(item => item.Id).ToHashSet();
        for (var index = 0; index < canvas.Nodes.Count; index++)
        {
            var node = canvas.Nodes[index];
            var path = $"Nodes[{index}]";
            if (node.WorkTreeItemId is not { } anchorId) continue;

            if (!workTreeIds.Contains(anchorId))
            {
                conflicts.Add(new SyncConflict(CanvasSyncConflictCodes.AnchorDangling, "WorkflowNode", node.Id.ToString(),
                    path + ".WorkTreeItemId",
                    $"节点「{node.Title}」的锚点 {anchorId} 指向的条目已不存在。",
                    "确认后清空锚点（保留显示文本）或重新指定章节", Blocking: false));
                changes.Add(new SyncChange(SyncChangeKind.DetachMissingAnchor, "WorkflowNode", node.Id.ToString(),
                    path + ".WorkTreeItemId", anchorId.ToString(), "（清空）", "锚点已悬空，需要人工确认", RequiresConfirmation: true));
                continue;
            }

            var chapterId = CanvasChapters.ChapterOfAnchor(canvas, anchorId);
            var chapter = chapterId is { } id ? chapters.FirstOrDefault(item => item.Id == id) : null;
            if (chapter is null) continue;

            if (!string.Equals(node.Chapter.Trim(), chapter.Name.Trim(), StringComparison.Ordinal))
            {
                var isCrossChapter = node.Chapter.Trim().Length > 0;
                changes.Add(new SyncChange(SyncChangeKind.UpdateNodeChapterText, "WorkflowNode", node.Id.ToString(),
                    path + ".Chapter", node.Chapter, chapter.Name,
                    isCrossChapter
                        ? "节点显示文本与锚点所属章节不一致（跨章节错绑），确认后按锚点归属统一"
                        : "节点锚定章节「" + chapter.Name + "」，显示文本按 ID 归属补齐",
                    RequiresConfirmation: isCrossChapter));
                if (isCrossChapter)
                    conflicts.Add(new SyncConflict(CanvasSyncConflictCodes.CrossChapterBinding, "WorkflowNode", node.Id.ToString(),
                        path + ".Chapter",
                        $"节点显示文本「{node.Chapter.Trim()}」与锚点解析出的章节「{chapter.Name}」不一致（跨章节错绑）。",
                        "确认后按锚点归属统一显示文本；锚点是权威，改名不会改变归属", Blocking: false));
            }

            if (node.Category == NodeCategory.Chapter && !string.Equals(node.Title.Trim(), chapter.Name.Trim(), StringComparison.Ordinal))
            {
                // 工作树是叙事轴事实源：冲突时把章节名同步到节点标题，但需要人工确认。
                changes.Add(new SyncChange(SyncChangeKind.UpdateChapterNodeTitle, "WorkflowNode", node.Id.ToString(),
                    path + ".Title", node.Title, chapter.Name,
                    "章节节点标题与工作树章节名不一致，按事实源同步", RequiresConfirmation: true));
                conflicts.Add(new SyncConflict(CanvasSyncConflictCodes.ChapterTitleMismatch, "WorkflowNode", node.Id.ToString(),
                    path + ".Title",
                    $"章节节点标题「{node.Title}」与工作树章节「{chapter.Name}」不一致。",
                    "确认后把章节名同步到节点标题，或先改工作树名称", Blocking: false));
            }
        }
    }

    private static void PlanCanvasToWorkTree(
        WorkflowCanvasState canvas,
        IReadOnlyList<WorkTreeItem> chapters,
        List<SyncChange> changes,
        List<SyncConflict> conflicts)
    {
        // 第一遍：按稳定依据建立「节点 → 章节」上下文，并从章节节点派生要新建的章节 ID。
        var context = new Dictionary<Guid, (Guid ChapterId, bool Certain)>();
        var plannedChapters = new Dictionary<Guid, string>(); // 章节节点 ID → 计划新建的章节名
        foreach (var node in canvas.Nodes)
        {
            if (node.WorkTreeItemId is { } anchorId && canvas.WorkTree.Any(item => item.Id == anchorId))
            {
                if (CanvasChapters.ChapterOfAnchor(canvas, anchorId) is { } chapterId) context[node.Id] = (chapterId, true);
                continue;
            }

            if (node.Category != NodeCategory.Chapter) continue;
            var name = node.Title.Trim();
            if (name.Length == 0) continue;
            var existing = chapters.Where(item => string.Equals(item.Name.Trim(), name, StringComparison.Ordinal)).ToList();
            // 没有任何同名章节条目 → 计划新建；恰好一条 → 交给下面的「唯一同名」分支（待确认）；
            // 多条 → 交给「同名歧义」分支（阻断，不绑定）。
            if (existing.Count == 0) plannedChapters[node.Id] = name;
        }

        // 第二遍：章节节点先拿到（新建的）章节 ID，这样它们的子节点可以按上下文继承。
        for (var index = 0; index < canvas.Nodes.Count; index++)
        {
            var node = canvas.Nodes[index];
            if (context.ContainsKey(node.Id)) continue;
            if (!plannedChapters.TryGetValue(node.Id, out var plannedName)) continue;

            var newId = DeterministicChapterId(node.Id);
            changes.Add(new SyncChange(SyncChangeKind.CreateChapterItem, "WorkTreeItem", newId.ToString(), "WorkTree[]",
                "（不存在）", plannedName, "章节节点在工作树里没有对应章节条目", RequiresConfirmation: false));
            changes.Add(new SyncChange(SyncChangeKind.LinkNodeAnchor, "WorkflowNode", node.Id.ToString(),
                $"Nodes[{index}].WorkTreeItemId", "（无锚点）", newId.ToString(),
                "新建章节后建立稳定锚点", RequiresConfirmation: false));
            context[node.Id] = (newId, true);
        }

        // 第三遍：上下文沿父链向子节点传播，保证计划与应用的判定一致、且顺序无关。
        var propagated = true;
        while (propagated)
        {
            propagated = false;
            foreach (var node in canvas.Nodes)
            {
                if (context.ContainsKey(node.Id)) continue;
                if (node.ParentNodeId is not { } parentId) continue;
                if (!context.TryGetValue(parentId, out var parent)) continue;
                context[node.Id] = parent;
                propagated = true;
            }
        }

        // 第四遍：按上下文建立锚点；没有上下文的才退回文本判定（唯一同名待确认、同名歧义阻断）。
        for (var index = 0; index < canvas.Nodes.Count; index++)
        {
            var node = canvas.Nodes[index];
            var path = $"Nodes[{index}]";
            if (node.WorkTreeItemId is { } anchorId && canvas.WorkTree.Any(item => item.Id == anchorId)) continue;

            if (context.TryGetValue(node.Id, out var certain))
            {
                if (changes.Any(change => change.Kind == SyncChangeKind.LinkNodeAnchor && change.ObjectId == node.Id.ToString())) continue;
                changes.Add(new SyncChange(SyncChangeKind.LinkNodeAnchor, "WorkflowNode", node.Id.ToString(),
                    path + ".WorkTreeItemId", "（无锚点）", certain.ChapterId.ToString(),
                    certain.Certain ? "按稳定上下文（锚点或父链）建立锚点" : "按上下文继承的章节建立锚点",
                    RequiresConfirmation: !certain.Certain));
                continue;
            }

            if (node.WorkTreeItemId is { } danglingId && canvas.WorkTree.All(item => item.Id != danglingId))
                continue; // 悬空锚点交给「工作树 → 画布」方向处理

            var text = node.Chapter.Trim();
            var name = node.Category == NodeCategory.Chapter ? node.Title.Trim() : text;
            if (name.Length == 0) continue;

            var matches = chapters.Where(item => string.Equals(item.Name.Trim(), name, StringComparison.Ordinal)).ToList();
            if (matches.Count > 1)
            {
                conflicts.Add(new SyncConflict(CanvasSyncConflictCodes.ChapterNameAmbiguous, "WorkflowNode", node.Id.ToString(),
                    path + ".Chapter",
                    $"文本「{name}」对应 {matches.Count} 个同名章节条目，禁止按名称绑定。",
                    "手工指定锚点，或先合并/改名消除同名", Blocking: true));
                continue;
            }

            if (matches.Count == 1)
            {
                changes.Add(new SyncChange(SyncChangeKind.LinkNodeAnchor, "WorkflowNode", node.Id.ToString(),
                    path + ".WorkTreeItemId", "（无锚点）", matches[0].Id.ToString(),
                    $"存在唯一同名章节「{name}」，确认后建立锚点", RequiresConfirmation: true));
                continue;
            }

            if (node.Category == NodeCategory.Chapter) continue;

            conflicts.Add(new SyncConflict(CanvasSyncConflictCodes.ChapterNameAmbiguous, "WorkflowNode", node.Id.ToString(),
                path + ".Chapter",
                $"节点写了章节文本「{name}」但工作树里没有同名章节条目。",
                "先在章节画布建立章节，或确认后手工指定锚点", Blocking: false));
        }
    }

    /// <summary>新章节 ID 由源节点 ID 确定性派生，重复规划同一画布不会产生不同 ID。</summary>
    private static string BaselineHash(WorkflowCanvasState canvas)
    {
        var json = JsonSerializer.Serialize(canvas);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(json)));
    }

    private static Guid DeterministicChapterId(Guid nodeId)
    {
        var digest = System.Security.Cryptography.SHA256.HashData(nodeId.ToByteArray());
        var created = new Guid(digest.AsSpan(0, 16));
        return created == Guid.Empty ? new Guid(digest.AsSpan(16, 16)) : created;
    }

    private static int NextOrder(WorkflowCanvasState canvas, Guid? parentChapterId)
    {
        var siblings = CanvasChapters.ChapterItems(canvas)
            .Where(item => CanvasChapters.ParentChapterId(canvas, item) == parentChapterId)
            .ToList();
        return siblings.Count == 0 ? CanvasChapters.OrderStep : siblings.Max(item => item.Order) + CanvasChapters.OrderStep;
    }

    private static IReadOnlyList<SyncChange> Deduplicate(IReadOnlyList<SyncChange> changes)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<SyncChange>();
        foreach (var change in changes)
        {
            var key = $"{change.Kind}|{change.ObjectType}|{change.ObjectId}|{change.FieldPath}";
            if (seen.Add(key)) result.Add(change);
        }

        return result;
    }

    private static Guid ParseId(string value) => Guid.TryParse(value, out var id) ? id : Guid.Empty;
}

/// <summary>
/// 同步会话：保存最近一次计划、应用结果与撤销快照，供界面「预览 → 应用 → 撤销」使用。
/// 快照只记录应用前的画布 JSON，撤销即恢复该快照，不改动磁盘文件。
/// </summary>
public sealed class CanvasSyncSession
{
    private static readonly JsonSerializerOptions Options = new();

    /// <summary>
    /// 快照连同**它属于哪张画布**一起记（调用方给的身份，桌面端给的是画布标签的 ID）。
    ///
    /// 为什么要记：快照只对生成它的那张画布有效。同步完又切了画布再撤销，等于把 A 的内容灌进 B——
    /// 这条判断放在这里，而不是让每个调用方各自记得比一遍。
    /// </summary>
    private readonly Stack<(string Owner, string Json)> snapshots = new();

    public SyncPlan? LastPlan { get; private set; }

    public SyncApplyResult? LastApply { get; private set; }

    public bool CanUndo => snapshots.Count > 0;

    /// <summary>生成计划（只读预览）。</summary>
    public SyncPlan Plan(WorkflowCanvasState canvas, SyncDirection direction = SyncDirection.Both)
    {
        LastPlan = CanvasWorkTreeSync.Plan(canvas, direction);
        return LastPlan;
    }

    /// <summary>应用计划；应用前记录快照，失败或拒绝时不记录。<paramref name="owner"/> 是这张画布的身份。</summary>
    public SyncApplyResult Apply(WorkflowCanvasState canvas, bool applyUnconfirmed = false, string owner = "")
    {
        if (LastPlan is null) throw new InvalidOperationException("请先生成同步计划。");
        var result = CanvasWorkTreeSync.Apply(canvas, LastPlan, applyUnconfirmed);
        LastApply = result;
        if (!result.Refused && result.Changed) snapshots.Push((owner, JsonSerializer.Serialize(canvas, Options)));
        return result;
    }

    /// <summary>这一份快照能不能用来撤销<paramref name="owner"/> 那张画布。</summary>
    public bool CanUndoFor(string owner) =>
        snapshots.Count > 0 && string.Equals(snapshots.Peek().Owner, owner, StringComparison.Ordinal);

    /// <summary>
    /// 撤销最近一次同步，返回恢复后的画布；没有快照、或者最近那份快照**不属于这张画布**时返回 null
    /// （不抛异常：调用方据此如实说一句「没有可撤销的」即可）。不属于的那种会被丢掉——它那张画布已经换走了。
    /// </summary>
    public WorkflowCanvasState? Undo(string owner = "")
    {
        if (!snapshots.TryPop(out var snapshot)) return null;
        if (!string.Equals(snapshot.Owner, owner, StringComparison.Ordinal)) return null;
        return JsonSerializer.Deserialize<WorkflowCanvasState>(snapshot.Json, Options);
    }

    public void Reset()
    {
        snapshots.Clear();
        LastPlan = null;
        LastApply = null;
    }
}
