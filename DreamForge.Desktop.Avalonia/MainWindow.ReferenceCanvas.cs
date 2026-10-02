using System.Text.Json;
using Avalonia;
using Avalonia.Media;
using DreamForge.Desktop;

namespace DreamForge.Desktop.Avalonia;

/// <summary>
/// 临时引用画布 —— 主窗口里的第二个编辑对象。
///
/// 它**复用同一套画布**：同一个 <c>CanvasSurface</c>、同一套工具栏 / 检查器 / 阶段筛选 / 搜索 /
/// 时间轴 / 剧本视图，唯一不同的是内容与落点：
/// 内容 = 「源头节点 + 它的引用递归铺成的树」（节点与连线都是**真的画布节点与连线**，可以随便拖、随便改）；
/// 落点 = **不落盘**（它不是画布库里的画布，也不进标签记录），「保存修订」在这里的含义是
/// 「把改动写回设定库，并更新源头节点的引用」。
///
/// 为此进入时把 <c>currentCanvas</c> 指到临时画布上——所有节点级功能因此天然对着屏幕上这张，
/// 不用逐个去改；原画布留在 <see cref="ReferenceCanvasSession.Original"/> 里，返回时原样换回。
///
/// 上一版这里是个独立弹窗，只能改描述 / 媒体，功能太少；用户的结论是「直接复用原来的画布功能」，
/// 这一版就是照这个做的。
/// </summary>
public partial class MainWindow
{
    private sealed class ReferenceCanvasSession
    {
        /// <summary>原画布上的那个节点（源头）。它是原画布的对象，写回时按 Id 找到它。
        /// 从浮层里的引用卡进来时，源头不是一个画布节点，见 <see cref="OwnerIsEntity"/>。</summary>
        public required WorkflowNode Owner { get; init; }

        /// <summary>
        /// 源头不是「原画布上的一个节点」，而是「原画布某个节点的某条引用」。
        /// 这种会话写回时只更新设定库，不去找（也找不到）原画布上的节点。
        /// </summary>
        public bool OwnerIsEntity { get; init; }

        /// <summary>临时画布的画布状态（进入时成为 currentCanvas）。</summary>
        public required RecentCanvasState State { get; init; }

        /// <summary>进入之前的原画布状态，返回时原样换回。</summary>
        public required RecentCanvasState Original { get; init; }

        public required string? OriginalPath { get; init; }
        public required string Title { get; init; }

        /// <summary>临时画布上的节点 → 引用卡 Key（实体/变体/版本）。源头节点默认不在里面（按 Id 认），
        /// 但「从引用卡进来」的那一层里源头本身就是一条引用，会带上自己的 Key。</summary>
        public required Dictionary<Guid, string> NodeKeys { get; init; }

        /// <summary>临时画布上有没写回的改动。它不是画布文件的「未保存」，所以单独记。</summary>
        public bool Dirty { get; set; }
    }

    /// <summary>
    /// 临时引用画布的**层级栈**：双击可以一层层往下钻（角色 → 它的子引用 → 再往下），
    /// Esc 一层层退回来。栈底那一层的 <c>Original</c> 才是真画布，往上的每一层指向它下面那一层。
    /// </summary>
    private readonly List<ReferenceCanvasSession> referenceCanvasStack = new();

    /// <summary>当前这一层（栈顶）；为 null 表示在普通画布上。原来的单层字段换成栈顶，读的地方不用改。</summary>
    private ReferenceCanvasSession? referenceCanvas =>
        referenceCanvasStack.Count == 0 ? null : referenceCanvasStack[^1];

    /// <summary>当前在临时画布的第几层（0 = 在真画布上）。</summary>
    private int ReferenceCanvasDepth => referenceCanvasStack.Count;

    private bool InReferenceCanvas => referenceCanvas is not null;

    // ---------------------------------------------------------------- 进入 / 返回

    /// <summary>
    /// 以某个节点为源头，新建一张临时引用画布并切过去。
    /// 它不走 <c>CanvasTab</c>：标签是画布库里的真画布（有文件名、参与保存与恢复），
    /// 而这棵树本来就不该落盘——硬塞进标签列表，就得让保存链路学会「哪些标签不写盘」，得不偿失。
    ///
    /// **可以在临时画布里再往下钻**：新一层压在栈上，它的「上一层」就是当前这一层；
    /// 于是每层的「保存修订」都写回它下面那一层认得的对象，Esc 一层层退。
    /// </summary>
    private void EnterReferenceCanvas(WorkflowNode owner) => OpenReferenceCanvas(owner, ownerIsEntity: false, sourceKey: null);

    /// <summary>
    /// 从浮层里的一张引用卡进来：源头不是画布节点，而是「某条引用」。
    /// 这种会话的源头那张卡**本身**就是一条引用，所以它的名称 / 描述会写回那个设定（见 TrySaveReferenceCanvas）。
    /// </summary>
    private void EnterReferenceCanvasFromReference(ReferenceCard card)
    {
        if (!HasOpenProject() || currentCanvas is null) return;
        if (card.Blocked)
        {
            StatusText.Text = $"这条引用暂时改不了：{card.BlockedReason}";
            return;
        }
        // 源头节点是**合成**的：它代表这条引用指向的那个设定。
        // Id 由引用的 Key 算出来（而不是每次新建）——这样在它的临时画布里再点它一次，
        // 能被认出来是同一张卡，而不是一层层往下套。
        var stableId = StableIdOf(card.Key);
        if (referenceCanvas is { } current && current.Owner.Id == stableId)
        {
            _ = ShowNodeEditorAsync(current.Owner);
            return;
        }
        var content = currentCanvas.Canvas.ResolveReferenceContent(new NodeReference
        {
            EntityId = card.EntityId,
            VariantId = card.VariantId,
            VariantVersionId = card.VersionId
        });
        if (content is null)
        {
            StatusText.Text = "这条引用已经失效，改不了。";
            return;
        }

        var owner = new WorkflowNode
        {
            Id = stableId,
            Title = content.Label,
            Category = NodeKindPalette.CategoryOf(content.Entity.Kind),
            Content = content.Description,
            Chapter = $"{WorkflowEntity.KindName(content.Entity.Kind)} · {content.Variant.Name}",
            References = content.References.ToList()
        };
        OpenReferenceCanvas(owner, ownerIsEntity: true, sourceKey: card.Key);
    }

    /// <summary>把一条引用的 Key 变成一个**稳定**的 Guid：同一个 Key 每次算出来都一样。</summary>
    private static Guid StableIdOf(string key) =>
        new(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(key)));

    /// <summary>
    /// 以某个节点为源头，新建一张临时引用画布并切过去。
    /// 它不走 <c>CanvasTab</c>：标签是画布库里的真画布（有文件名、参与保存与恢复），
    /// 而这棵树本来就不该落盘——硬塞进标签列表，就得让保存链路学会「哪些标签不写盘」，得不偿失。
    ///
    /// **可以在临时画布里再往下钻**：新一层压在栈上，它的「上一层」就是当前这一层；
    /// 于是每层的「保存修订」都写回它下面那一层认得的对象，Esc 一层层退。
    /// </summary>
    private void OpenReferenceCanvas(WorkflowNode owner, bool ownerIsEntity, string? sourceKey)
    {
        if (!HasOpenProject() || currentCanvas is null) return;

        // 在临时画布里双击**当前这一层的源头节点**会得到和这一层一模一样的树（它的引用正是这一层的第 1 层卡），
        // 白多一层、看着像没反应。这种情况改成打开编辑窗口。
        //
        // 这里**直接**调编辑窗口，绝不再绕回 CanvasSurface_OnNodeDoubleClicked：
        // 双击处理器曾经会判断「有引用就往下钻」再调回这里，两边互相调用会一路递归到栈溢出，程序直接崩。
        if (referenceCanvas is { } current && current.Owner.Id == owner.Id)
        {
            _ = ShowNodeEditorAsync(owner);
            return;
        }

        var tree = CanvasReferenceLayout.Build(currentCanvas.Canvas, owner);
        if (tree.Direct.Count == 0)
        {
            StatusText.Text = "这个节点没有可展开的引用，没有临时画布可建。";
            return;
        }

        // 先把当前编辑收进它自己的标签：返回时换回的是这份实时状态，标签快照必须同步。
        // 已经在临时画布里时这个调用自己会跳过（临时画布不进标签记录）。
        CaptureActiveTab();

        var (state, keys) = BuildReferenceState(owner, tree, currentCanvas.Canvas, sourceKey);
        referenceCanvasStack.Add(new ReferenceCanvasSession
        {
            Owner = owner,
            OwnerIsEntity = ownerIsEntity,
            State = state,
            // 在临时画布里再钻一层时，「上一层」就是当前这张临时画布——所以每层只管自己下面那一层。
            Original = currentCanvas,
            OriginalPath = currentCanvasPath,
            Title = ReferenceCanvasTitle(tree),
            NodeKeys = keys
        });

        currentCanvas = state;
        currentCanvasPath = null;      // 没有文件：这是「临时」在数据上的唯一表现
        CanvasSurfaceControl.State = state.Canvas;
        // 临时画布里不铺引用浮层：这张画布本身就是那棵树，浮层只会让「单击选中、双击往下钻」失灵
        // （第一下点击被浮层吃掉，第二下落在压暗层上把浮层关掉）。原因详见 CanvasSurface.ReferenceOverlayEnabled。
        SyncReferenceOverlayFlag();
        // 阶段筛选是跨画布留着的：带着「章节拆分」进来会把引用卡全筛掉，看起来像画布是空的。
        SelectStage(ProductionStage.All);
        ClearEditHistory();
        RefreshResourceList();
        RefreshOpenCenterView();
        RebuildCanvasTabStrip();
        UpdateCanvasUi(null);
        CanvasSurfaceControl.FitToContent();
        AgentWorkbenchPanel.SyncHostState();
        StatusText.Text = ReferenceCanvasDepth > 1
            ? $"已展开第 {ReferenceCanvasDepth} 层：{tree.Summary}。单击选中节点，双击继续往下钻；按 Esc 退回上一层。"
            : $"已进入临时引用画布：{tree.Summary}。单击选中（节点检查器里可以直接改名称与内容），双击带引用的节点继续往下钻；改完点「保存修订」写回设定库，按 Esc（或点标签条上的芯片）回去。";
    }

    /// <summary>
    /// 退回到下面一层：还在临时画布里就退到上一层，已经是最底一层就回真画布。
    /// 返回不等于放弃：写回是「保存修订」的事，这里只是把编辑对象换回去。
    /// </summary>
    private void LeaveReferenceCanvas()
    {
        if (referenceCanvasStack.Count == 0) return;
        var session = referenceCanvasStack[^1];
        referenceCanvasStack.RemoveAt(referenceCanvasStack.Count - 1);

        currentCanvas = session.Original;
        currentCanvasPath = session.OriginalPath;
        CanvasSurfaceControl.State = currentCanvas?.Canvas;
        // 退到真画布才恢复「单击节点铺引用浮层」；退到上一层临时画布时它仍然是关的。
        SyncReferenceOverlayFlag();
        ClearEditHistory();
        RefreshResourceList();
        RefreshOpenCenterView();
        RebuildCanvasTabStrip();
        UpdateCanvasUi(currentCanvasPath);
        CanvasSurfaceControl.Refresh();
        AgentWorkbenchPanel.SyncHostState();

        if (referenceCanvasStack.Count > 0)
        {
            StatusText.Text = session.Dirty
                ? $"已退回上一层（还剩 {ReferenceCanvasDepth} 层）。这一层上没写回的改动留在了那一层里。"
                : $"已退回上一层（还剩 {ReferenceCanvasDepth} 层）。";
            return;
        }

        // 回到真画布之后才写标签记录：临时画布不该进那份记录。
        PersistCanvasTabs(reportFailure: false);
        StatusText.Text = session.Dirty
            ? "已回到原画布。临时画布上还有没写回的改动（那些改动只在临时画布里，没进设定库）。"
            : "已回到原画布。";
    }

    /// <summary>
    /// 一路退到真画布：切标签 / 新建 / 关标签 / 换项目时用——钻了几层就退几层。
    /// 只退一层的话，人已经到另一张画布了，栈里还留着上一张的层级。
    /// </summary>
    private void LeaveAllReferenceCanvases()
    {
        while (referenceCanvasStack.Count > 0) LeaveReferenceCanvas();
        // 兜一层：即使某一层退出路径漏了，回到真画布时交互也一定是「正常画布」那一套。
        SyncReferenceOverlayFlag();
    }

    /// <summary>
    /// 画布上「单击有引用的节点铺出引用浮层」这个交互，只在**真画布**上开着：
    /// 临时引用画布本身就是那棵树，浮层会让「单击选中、双击往下钻」失灵。
    /// 所有改动栈的地方都调它一次，免得出现「出了临时画布，浮层再也不出现」这种状态残留。
    /// </summary>
    private void SyncReferenceOverlayFlag() =>
        CanvasSurfaceControl.ReferenceOverlayEnabled = referenceCanvasStack.Count == 0;

    private string ReferenceCanvasTitle(ReferenceTree tree)
    {
        var baseTitle = $"引用·{(string.IsNullOrWhiteSpace(tree.OwnerTitle) ? "未命名节点" : tree.OwnerTitle)}";
        var taken = canvasTabs.Select(tab => tab.Title).ToHashSet(StringComparer.Ordinal);
        if (!taken.Contains(baseTitle)) return baseTitle;
        for (var index = 2; ; index++)
        {
            var candidate = $"{baseTitle} ({index})";
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    // ---------------------------------------------------------------- 建这张临时画布

    private (RecentCanvasState State, Dictionary<Guid, string> NodeKeys) BuildReferenceState(
        WorkflowNode owner,
        ReferenceTree tree,
        WorkflowCanvasState source,
        string? sourceKey)
    {
        const double cardWidth = 232;
        // 档与档之间只留「一条连线能过去」的距离：早先留 130，三级下来整棵树宽出 1100 多像素，
        // 一按「适应」就缩得看不清字，用户说的「子节点下级节点都好远」主要就是这里。
        const double columnGap = 76;
        const double rowGap = 16;
        const double origin = 70;

        // 设定与工作树**共享同一批对象**：改它就是改项目里的那一份，不用再拷来拷去。
        // 但列表本身各自一份，免得在临时画布上增删设定会动到原画布。
        var canvas = new WorkflowCanvasState
        {
            Entities = source.Entities.ToList(),
            WorkTree = source.WorkTree.ToList()
        };

        var keys = new Dictionary<Guid, string>();
        var byCardId = new Dictionary<string, WorkflowNode>();
        var columns = tree.Cards.GroupBy(card => card.Depth).OrderBy(group => group.Key).ToList();

        // 同一条竖列里，卡片按**估计高度**逐张往下排（见 EstimateCardHeight）。
        // 为什么只能估：位置必须在建卡片之前定下来，而真实高度要等布局跑完才知道；
        // 估计值取得略宽松——宁可多留几像素，也不让卡片叠在一起（早先固定按 104 排，
        // 卡片一有引用标签就互相压住，那才是真的排版坏掉）。
        var childCounts = tree.Cards
            .Where(card => card.ParentId is not null)
            .GroupBy(card => card.ParentId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        foreach (var column in columns)
        {
            var x = origin + column.Key * (cardWidth + columnGap);
            // **顶对齐**，不再把整档上下居中：源头卡与第 1 层引用从同一条水平线起排，父卡与它的第一个子卡挨在一起。
            // 居中那一版在「某一档只有一张卡、另一档有四五张」时会把单张卡推到屏幕中间，看着离父卡特别远。
            var y = origin;
            foreach (var card in column)
            {
                // 父卡在本列之前就已经建好了（列按层数升序），所以能直接取到父节点的 Id。
                Guid? parentNodeId = !card.IsSource && card.ParentId is { } parentCardId
                    && byCardId.TryGetValue(parentCardId, out var parentNode)
                        ? parentNode.Id
                        : null;
                var node = card.IsSource
                    ? CreateSourceNode(owner, x, y)
                    : CreateReferenceNode(card, source, parentNodeId, x, y);
                byCardId[card.Id] = node;
                canvas.Nodes.Add(node);
                // 源头节点的写回方式：普通会话按 Id 写回原画布上的节点；「从引用卡进来」的会话
                // 源头本身就是一条引用，所以也要带上 Key，由设定那一侧写回。
                if (!card.IsSource) keys[node.Id] = card.Key;
                else if (sourceKey is not null) keys[node.Id] = sourceKey;
                // 逐卡推进（不是整档固定格高）：短卡不必为长卡的空位买单，
                // 一档里既有叶子又有带预览的卡时，间距才对得上每一张自己的实际高度。
                y += EstimateCardHeight(childCounts.GetValueOrDefault(card.Id)) + rowGap;
            }
        }

        foreach (var card in tree.Cards)
        {
            if (card.ParentId is null) continue;
            if (!byCardId.TryGetValue(card.ParentId, out var from)) continue;
            if (!byCardId.TryGetValue(card.Id, out var to)) continue;
            canvas.Edges.Add(new WorkflowEdge { SourceNodeId = from.Id, TargetNodeId = to.Id });
        }

        // 临时画布上的节点**也带上 References**（不只是连线）：
        // 引用浮层、以及「在临时画布里双击继续往下钻」读的都是 References；
        // Agent 协作的「本体设定」那一层同样靠它。只连边不写 References，
        // 在临时画布里双击任何节点都会答「没有可展开的引用」——往下钻就直接断了。
        foreach (var card in tree.Cards)
        {
            if (!byCardId.TryGetValue(card.Id, out var parent)) continue;
            foreach (var child in tree.Cards.Where(item => item.ParentId == card.Id))
            {
                if (child.Blocked) continue;
                if (!byCardId.ContainsKey(child.Id)) continue;
                parent.References.Add(ReferenceOf(child));
            }
        }

        var state = new RecentCanvasState
        {
            Title = ReferenceCanvasTitle(tree),
            Canvas = canvas
        };
        return (state, keys);
    }

    /// <summary>
    /// 源头节点：**一份副本**，Id 与原节点相同（写回时按 Id 找）。
    /// 刻意不共用对象：这样在临时画布里改它不会当场改到原画布，写回与否由「保存修订」决定。
    /// </summary>
    private static WorkflowNode CreateSourceNode(WorkflowNode owner, double x, double y) => new()
    {
        Id = owner.Id,
        Title = string.IsNullOrWhiteSpace(owner.Title) ? "未命名节点" : owner.Title,
        Category = owner.Category,
        Content = owner.Content,
        Chapter = owner.Chapter,
        // 锚点要一起带过来：不然在临时画布里算不出「所属章节」（CanvasChapters 按稳定 ID 解析，不按章节名猜），
        // Agent 协作那句「往上推获取本章内容」就断在这里。
        WorkTreeItemId = owner.WorkTreeItemId,
        ExecutionStatus = NodeExecutionStatus.Completed,
        IsLocked = owner.IsLocked,
        X = (float)x,
        Y = (float)y
    };

    private static WorkflowNode CreateReferenceNode(ReferenceCard card, WorkflowCanvasState source, Guid? parentNodeId, double x, double y)
    {
        var node = new WorkflowNode
        {
            Title = card.Title,
            // 种类直接沿用引用卡上的：颜色因此与画布上的节点是同一套口径。
            Category = card.Category,
            Chapter = card.Blocked ? $"{card.Kind} · {card.BlockedReason}" : $"{card.Kind} · {card.Subtitle}",
            // 临时画布本身就是一棵树，父链要显式连上：这样从任何一张卡都能沿父链找到源头分镜与本章。
            ParentNodeId = parentNodeId,
            ExecutionStatus = card.Blocked ? NodeExecutionStatus.NeedsReview : NodeExecutionStatus.Draft,
            X = (float)x,
            Y = (float)y
        };
        if (card.Blocked)
        {
            // 失效的引用：**把原因写进正文**。只挂在 Chapter 上（卡片上能看到）的话，
            // 检查器里那格「节点内容」就是空的——点了节点看不出任何变化，像是没反应。
            node.Content = $"引用失效：{card.BlockedReason}";
            return node;
        }

        var content = source.ResolveReferenceContent(new NodeReference
        {
            EntityId = card.EntityId,
            VariantId = card.VariantId,
            VariantVersionId = card.VersionId
        });
        if (content is null) return node;

        node.Content = content.Description;
        // 参考图照搬到卡上：临时画布上看到的图与设定里的是同一张。
        node.Attachments = content.Attachments.ToList();
        return node;
    }

    /// <summary>把一张引用卡翻成节点上的引用（实体 / 变体 / 版本三个 Id 一一对应）。</summary>
    private static NodeReference ReferenceOf(ReferenceCard card) => new()
    {
        EntityId = card.EntityId,
        VariantId = card.VariantId,
        VariantVersionId = card.VersionId
    };

    /// <summary>
    /// 一张临时画布卡片的高度**估计**，口径与 CanvasSurface.CreateNodeCard 摆的东西对齐：
    /// 种类色带 + 标题（最多两行）+ 正文两行 + 状态行 + 内边距与间距 ≈ 150；
    /// 每条子引用多一个「引用 · …」标签 ≈ 22；
    /// 有子引用就还会多一条预览带（每行 3 格、每格 64 高）和一条分隔线。
    /// 只估不收口的地方：标题特别长时会多出一两行——所以宁可估宽一点，不让卡片叠在一起。
    /// </summary>
    private static double EstimateCardHeight(int childCount)
    {
        var height = 150d + childCount * 22d;
        if (childCount > 0) height += 14 + Math.Ceiling(childCount / 3d) * 64;
        return height;
    }

    // ---------------------------------------------------------------- 保存（写回设定库与源头节点）

    /// <summary>
    /// 临时画布的「保存」：把节点上的文字与连线翻译回**设定**与**引用**。
    /// ① 每张引用卡 → 那个设定的名称 / 描述 / 媒体 / 子引用；
    /// ② 源头节点 → 原画布上那个节点的正文与引用列表（它的出边就是它引用了谁）。
    /// 写库失败把库与内存一起回滚——不留「界面说失败、内存却已经改了」的中间态。
    /// </summary>
    private bool TrySaveReferenceCanvas(out string message)
    {
        var session = referenceCanvas;
        if (session is null || currentCanvas is null)
        {
            message = "当前不在临时引用画布上。";
            return false;
        }
        if (!canEdit)
        {
            message = "项目或项目库不可写，当前为只读，改动没有写回。";
            return false;
        }

        var source = session.Original.Canvas;
        var temp = currentCanvas.Canvas;
        // 「从引用卡进来」的那一层没有对应的画布节点：源头本身就是一条引用，写回时只更新设定库。
        var ownerNode = session.OwnerIsEntity ? null : source.Nodes.FirstOrDefault(node => node.Id == session.Owner.Id);
        if (!session.OwnerIsEntity && ownerNode is null)
        {
            message = "源头节点已经不在原画布上了，无法写回（原画布可能被关掉或另存过）。";
            return false;
        }

        var libraryPath = ProjectLibrary.FilePath;
        var libraryExisted = File.Exists(libraryPath);
        byte[]? libraryBefore = null;
        try { libraryBefore = libraryExisted ? File.ReadAllBytes(libraryPath) : null; }
        catch (IOException) { libraryBefore = null; }

        var entitySnapshots = new Dictionary<Guid, WorkflowEntity>();
        var touched = new List<WorkflowEntity>();
        var ownerReferencesBefore = ownerNode is null ? new List<NodeReference>() : CloneList(ownerNode.References);
        var ownerTitleBefore = ownerNode?.Title ?? string.Empty;
        var ownerContentBefore = ownerNode?.Content ?? string.Empty;
        var ownerChapterBefore = ownerNode?.Chapter ?? string.Empty;

        try
        {
            // ① 源头节点：正文写回原画布节点；引用列表按它在临时画布上的出边重建
            //    （所以在这里删掉一张卡、或连一条新线，就等于增删一条引用）。
            //    从引用卡进来的那一层没有对应的画布节点（ownerNode 为 null），源头由下面 ② 那条路写回设定。
            var sourceInTemp = temp.Nodes.FirstOrDefault(node => node.Id == session.Owner.Id);
            if (ownerNode is not null)
            {
                if (sourceInTemp is not null)
                {
                    ownerNode.Title = sourceInTemp.Title;
                    ownerNode.Content = sourceInTemp.Content;
                    ownerNode.Chapter = sourceInTemp.Chapter;
                }
                ownerNode.References = ReferencesFromEdges(ownerNode.Id, temp, session);
            }

            // ② 每张引用卡 → 那个设定
            foreach (var (nodeId, key) in session.NodeKeys)
            {
                if (!TryParseKey(key, out var entityId, out var variantId, out var versionId)) continue;
                var node = temp.Nodes.FirstOrDefault(item => item.Id == nodeId);
                if (node is null) continue;      // 卡被删了：它的引用已经随出边一起消失

                var entity = source.FindEntity(entityId);
                var variant = entity?.Variants.FirstOrDefault(item => item.Id == variantId);
                if (entity is null || variant is null) continue;
                // 锁定了版本的引用是只读快照，改了它会让人以为改到了那一版。
                if (versionId is not null && variant.FindVersion(versionId.Value) is null) continue;

                if (!entitySnapshots.ContainsKey(entity.Id))
                {
                    entitySnapshots[entity.Id] = ProjectEntityScope.CloneEntity(entity);
                    touched.Add(entity);
                }
                if (!string.IsNullOrWhiteSpace(node.Title)) entity.Name = node.Title;
                variant.Description = node.Content;
                variant.Attachments = node.Attachments.ToList();
                variant.References = ReferencesFromEdges(nodeId, temp, session);
                variant.Commit("临时引用画布编辑");
            }

            foreach (var entity in touched)
                if (entity.ManagedByProject && !ProjectEntityScope.TryPublish(entity, out var error))
                    throw new IOException($"项目库写入失败：{error}");

            // 原画布上的节点与设定都变了：那张标签要标成待保存，否则用户会以为已经存过了。
            if (activeCanvasTab is not null) activeCanvasTab.Dirty = true;
            session.Dirty = false;
            RebuildCanvasTabStrip();

            var localCount = touched.Count(entity => !entity.ManagedByProject);
            var localNote = localCount > 0
                ? $"；其中 {localCount} 项是画布本地设定，要回主画布「保存修订」才落盘"
                : string.Empty;
            message = session.OwnerIsEntity
                ? (touched.Count == 0
                    ? "这条引用没有可写回的设定内容。"
                    : $"已写回设定库（{touched.Count} 项设定）{localNote}")
                : (touched.Count == 0
                    ? "已更新源头节点的引用（没有设定内容需要写回）。原画布已标为待保存。"
                    : $"已写回设定库（{touched.Count} 项设定），并更新源头节点的引用。原画布已标为待保存{localNote}");
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            var restoreError = string.Empty;
            try
            {
                if (libraryExisted && libraryBefore is not null) File.WriteAllBytes(libraryPath, libraryBefore);
                else if (File.Exists(libraryPath)) File.Delete(libraryPath);
            }
            catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException)
            {
                restoreError = $"项目库回滚失败：{rollback.Message}";
            }
            foreach (var (id, snapshot) in entitySnapshots)
            {
                var index = source.Entities.FindIndex(item => item.Id == id);
                if (index >= 0) source.Entities[index] = snapshot;
            }
            if (ownerNode is not null)
            {
                ownerNode.References = ownerReferencesBefore;
                ownerNode.Title = ownerTitleBefore;
                ownerNode.Content = ownerContentBefore;
                ownerNode.Chapter = ownerChapterBefore;
            }
            message = $"写回失败，已恢复：{error.Message}{(restoreError.Length == 0 ? string.Empty : $"；{restoreError}")}";
            return false;
        }
    }

    /// <summary>某个节点在临时画布上的出边 → 引用列表。连线的顺序就是引用的顺序。</summary>
    private static List<NodeReference> ReferencesFromEdges(Guid parentNodeId, WorkflowCanvasState temp, ReferenceCanvasSession session)
    {
        var result = new List<NodeReference>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in temp.Edges.Where(edge => edge.SourceNodeId == parentNodeId))
        {
            if (!session.NodeKeys.TryGetValue(edge.TargetNodeId, out var key)) continue;
            if (!TryParseKey(key, out var entityId, out var variantId, out var versionId)) continue;
            if (!seen.Add(key)) continue;
            result.Add(new NodeReference { EntityId = entityId, VariantId = variantId, VariantVersionId = versionId });
        }
        return result;
    }

    /// <summary>引用卡的 Key 形如 <c>entity/variant/latest|version</c>（见 <see cref="CanvasReferenceLayout.KeyOf"/>）。</summary>
    private static bool TryParseKey(string key, out Guid entityId, out Guid variantId, out Guid? versionId)
    {
        entityId = Guid.Empty;
        variantId = Guid.Empty;
        versionId = null;
        var parts = key.Split('/');
        if (parts.Length < 3) return false;
        if (!Guid.TryParseExact(parts[0], "N", out entityId)) return false;
        if (!Guid.TryParseExact(parts[1], "N", out variantId)) return false;
        if (parts[2] == "latest") return true;
        if (!Guid.TryParseExact(parts[2], "N", out var parsed)) return false;
        versionId = parsed;
        return true;
    }

    private static List<T> CloneList<T>(IReadOnlyList<T> source) =>
        source.Count == 0 ? new List<T>() : JsonSerializer.Deserialize<List<T>>(JsonSerializer.Serialize(source)) ?? new List<T>();
}
