namespace DreamForge.Desktop;

/// <summary>
/// 节点种类配色（UI-free 的**唯一一份口径**）：画布节点、画布上的引用浮层、临时引用画布三处共用。
///
/// 规则是「色相 = 功能族，族内靠邻近色相区分」：
/// - 企划族（紫）——最上游的意图，两个色靠得近：它们本来就很少需要互相区分；
/// - 结构族（品牌蓝）——章节是叙事骨架，也是应用主色，留着最有辨识度；
/// - 执行族（青 / 品红）——生产环节，与结构族明显拉开；
/// - 设定族（橙 / 绿 / 黄绿）——这三类经常同时挂在一个分镜上，所以彼此拉得最开；
/// - 兜底（灰蓝）——未分类。
///
/// 早先这里只有四个颜色，九个种类里**七个与他类完全同色**（角色/场景/分镜一色，道具/通用/企划/大纲一色），
/// 所以「按颜色分类型」实际上是失效的。测试盯住「九类九色」，避免以后又被改回去。
///
/// 颜色只用在这种「种类色带」和进度条上，**不用作卡片底色**：画面不会变成彩色卡片墙，
/// 只是多一层可扫读的分类线索；文字标签照旧保留，颜色是第二线索。
/// </summary>
public static class NodeKindPalette
{
    public const string PlanningHex = "#A78BFA";    // 故事企划 · 紫
    public const string OutlineHex = "#818CF8";     // 故事大纲 · 蓝紫
    public const string ChapterHex = "#4D9BFF";     // 章节 · 品牌蓝（不变）
    public const string StoryboardHex = "#2DD4BF";  // 分镜 · 青
    public const string ProductHex = "#E879F9";     // 成品 · 品红
    public const string CharacterHex = "#FB923C";   // 出场角色 · 橙
    public const string SceneHex = "#4ADE80";       // 场景 · 绿
    public const string PropHex = "#A3E635";        // 道具 · 黄绿
    public const string NeutralHex = "#8FA6BD";     // 通用 · 灰蓝（不变）

    /// <summary>种类主色（#RRGGBB）。九个种类各不相同。</summary>
    public static string HexOf(NodeCategory category) => category switch
    {
        NodeCategory.StoryPlan => PlanningHex,
        NodeCategory.StoryOutline => OutlineHex,
        NodeCategory.Chapter => ChapterHex,
        NodeCategory.Storyboard => StoryboardHex,
        NodeCategory.Product => ProductHex,
        NodeCategory.Character => CharacterHex,
        NodeCategory.Scene => SceneHex,
        NodeCategory.Prop => PropHex,
        _ => NeutralHex
    };

    /// <summary>设定种类 → 画布节点种类。带这份对应关系是为了让引用卡与画布节点用同一套配色。</summary>
    public static NodeCategory CategoryOf(EntityKind kind) => kind switch
    {
        EntityKind.Character => NodeCategory.Character,
        EntityKind.Scene => NodeCategory.Scene,
        _ => NodeCategory.Prop
    };
}
