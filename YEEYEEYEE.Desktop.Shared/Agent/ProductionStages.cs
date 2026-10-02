namespace YEEYEEYEE.Desktop;

/// <summary>
/// 制作阶段。左栏那排按钮与画布上方那排芯片**共用这一份定义**：
/// 两边各写一套的话，很快就会出现「左栏五个、芯片六个」这种对不上的情况。
/// </summary>
public enum ProductionStage
{
    /// <summary>不筛，全部节点。</summary>
    All,

    /// <summary>导入：按「这个节点有没有素材」算，不是按节点类型。</summary>
    Import,

    /// <summary>章节拆分：源文本拆出来的章节节点。</summary>
    ChapterSplit,

    /// <summary>工作树：已经绑到工作树条目上的节点。</summary>
    WorkTree,

    /// <summary>分镜：分镜节点。</summary>
    Storyboard,

    /// <summary>成品：成品节点。</summary>
    Product
}

/// <summary>一个阶段在界面上要显示的东西：名字、真实计数、是不是当前选中的那个。</summary>
public sealed record StageSummary(ProductionStage Stage, string Label, int Count, bool IsActive);

/// <summary>
/// 阶段规则：怎么筛、统计什么、显示成什么文字。
///
/// 为什么放在共享的 UI-free 代码里：这些是**数据规则**（哪些节点属于哪一步、这一步有多少东西），
/// 界面只负责画出来。规则放在这里才能被测试钉住，下次改口径不用靠肉眼核对界面。
/// </summary>
public static class ProductionStageRules
{
    /// <summary>界面上从左到右的顺序（左栏按钮与画布芯片都用它）。</summary>
    public static readonly IReadOnlyList<ProductionStage> Order = new[]
    {
        ProductionStage.All,
        ProductionStage.Import,
        ProductionStage.ChapterSplit,
        ProductionStage.WorkTree,
        ProductionStage.Storyboard,
        ProductionStage.Product
    };

    public static string LabelOf(ProductionStage stage) => stage switch
    {
        ProductionStage.Import => "导入",
        ProductionStage.ChapterSplit => "章节拆分",
        ProductionStage.WorkTree => "工作树",
        ProductionStage.Storyboard => "分镜",
        ProductionStage.Product => "成品",
        _ => "全部"
    };

    /// <summary>一句话说明这个阶段按什么筛，用在提示与状态栏里。</summary>
    public static string RuleOf(ProductionStage stage) => stage switch
    {
        ProductionStage.Import => "带素材的节点",
        ProductionStage.ChapterSplit => "章节节点",
        ProductionStage.WorkTree => "已绑工作树条目的节点",
        ProductionStage.Storyboard => "分镜节点",
        ProductionStage.Product => "成品节点",
        _ => string.Empty
    };

    /// <summary>这个节点算不算这个阶段。</summary>
    public static bool Matches(ProductionStage stage, WorkflowNode node) => stage switch
    {
        ProductionStage.Import => node.Attachments.Count > 0,
        ProductionStage.ChapterSplit => node.Category == NodeCategory.Chapter,
        ProductionStage.WorkTree => node.WorkTreeItemId is not null,
        ProductionStage.Storyboard => node.Category == NodeCategory.Storyboard,
        ProductionStage.Product => node.Category == NodeCategory.Product,
        _ => true
    };

    /// <summary>
    /// 这个阶段有多少「东西」。注意单位并不都是节点：
    /// 导入数素材、工作树数条目（左栏那棵树里的项），其余数节点。
    /// </summary>
    public static int Count(WorkflowCanvasState canvas, ProductionStage stage) => stage switch
    {
        ProductionStage.Import => canvas.Nodes.Sum(node => node.Attachments.Count),
        ProductionStage.WorkTree => canvas.WorkTree.Count,
        ProductionStage.All => canvas.Nodes.Count,
        _ => canvas.Nodes.Count(node => Matches(stage, node))
    };

    /// <summary>计数后面跟的单位。</summary>
    public static string UnitOf(ProductionStage stage) => stage switch
    {
        ProductionStage.Import => "项素材",
        ProductionStage.WorkTree => "项",
        _ => "节点"
    };

    public static string DescribeCount(ProductionStage stage, int count) => $"{count} {UnitOf(stage)}";

    /// <summary>一次算齐所有阶段（界面按这个铺芯片、按钮和状态栏）。</summary>
    public static IReadOnlyList<StageSummary> Summarize(WorkflowCanvasState canvas, ProductionStage active) =>
        Order.Select(stage => new StageSummary(stage, LabelOf(stage), Count(canvas, stage), stage == active)).ToList();

    /// <summary>画布上该显示哪些节点：按当前阶段筛，返回 null 表示全部显示。</summary>
    public static Func<WorkflowNode, bool>? FilterOf(ProductionStage stage) =>
        stage == ProductionStage.All ? null : node => Matches(stage, node);

    /// <summary>当前阶段为空时给一句人话，说明「为什么画布是空的」和下一步去哪。</summary>
    public static string EmptyHintOf(ProductionStage stage) => stage switch
    {
        ProductionStage.Import => "这个画布还没有节点带素材。把图片拖到节点上，或在节点上右键出图，素材就会挂上来。",
        ProductionStage.ChapterSplit => "还没有章节节点。选中一个总纲 / 正文节点，点右上「生成章节工作树」按文本拆章。",
        ProductionStage.WorkTree => "还没有节点绑到工作树。生成章节工作树，或从左侧工作树把条目拖进画布。",
        ProductionStage.Storyboard => "还没有分镜节点。章节节点上右键「让 Agent 把这一章拆成分镜与角色/场景节点」。",
        ProductionStage.Product => "还没有成品节点。分镜出图合成之后，把这一版的成品节点建出来。",
        _ => "此画布暂无节点 · 点击「节点」新建，或从左侧工作树拖入资源"
    };
}
