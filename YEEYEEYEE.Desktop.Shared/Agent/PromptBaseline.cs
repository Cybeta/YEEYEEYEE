namespace YEEYEEYEE.Desktop;

/// <summary>
/// 生成技能的**设定卡与提示词基线**：角色 / 场景 / 道具 / 分镜各自该有哪些字段、按什么顺序写、
/// 以及各自该避开什么。
///
/// 这套顺序不是随手排的，它同时是**权重顺序**：主流模型对靠前的词权重更高（Flux 官方明确说词序有意义，
/// 最重要的元素放最前），SD 系还会按 75 token 分块截断。所以「先本体后环境、先外观后风格」不只是写法偏好，
/// 是出图结果好不好看的直接原因。
///
/// 另外两条来自社区里被反复强调的纪律：
/// 1. **一套服装只能有一种措辞**（前视写「藏青外套」、后视写「深蓝夹克」会被当成两件衣服）；
/// 2. **场景必须写明光源来源与色温**，禁止「一个舒服的咖啡馆」这种没有信息量的写法。
///
/// 为什么放在共享层：技能说明（给模型的 OutputFormat）、节点协助的提示词模板、
/// 以及以后旧端要接同一套基线，都得读**同一份**字段清单——各写一份必然分叉。
/// </summary>
public static class PromptBaseline
{
    /// <summary>一个字段：稳定键、中文小标题、写给模型看的要求。</summary>
    public sealed record Field(string Key, string Label, string Hint);

    /// <summary>
    /// 角色卡字段：外观锚点在前（跨镜头要复用的那些），姿态与画风在后。
    /// 「标志特征」特意要求带位置（左眉上一道疤），只说「有疤」模型会随便放。
    /// </summary>
    public static IReadOnlyList<Field> Character { get; } = new[]
    {
        new Field("identity", "身份", "年龄读感、性别、职业或身份"),
        new Field("body", "体型", "身高、体型（清瘦/健壮/丰满）、体态"),
        new Field("face", "面部", "脸型、肤色、眼型与眼色、眉、鼻、唇"),
        new Field("hair", "发型发色", "长度、质地、颜色、束发方式"),
        new Field("mark", "标志特征", "带位置的辨识点，例如「左眉上一道细疤」「鼻梁几点雀斑」"),
        new Field("outfit", "服装", "每件写「部件 + 材质 + 颜色 + 在身上哪里」，全卡措辞保持一致"),
        new Field("accessory", "配饰与惯用道具", "带位置，例如「左胯皮质挎包」「腰间黄铜钥匙」"),
        new Field("expression", "神态与气质", "中性表情下的整体气质，例如「沉静、有点疲倦」"),
        new Field("pose", "姿态", "参考图用冻结姿势：站立、双臂自然、重心均匀、双脚平行"),
        new Field("lighting", "光影", "参考图用均匀柔光，不做过强的方向光"),
        new Field("style", "画风", "单一画风，例如「干净线条的数码角色设定图」"),
        new Field("aspect", "画幅", "三视图用 16:9 或 3:2，单张全身用 2:3")
    };

    /// <summary>
    /// 场景卡字段：先空间事实，再光线与色彩，最后机位与氛围。
    /// 「光源」必须写来源与色温——这是场景提示词里最容易被漏、又最影响成片的一项。
    /// </summary>
    public static IReadOnlyList<Field> Scene { get; } = new[]
    {
        new Field("space", "空间类型与规模", "室内/室外、什么空间、多大、有无隔断"),
        new Field("period", "时代与地域", "年代、地域风格；不确定就写「不明确的现代都市」"),
        new Field("time_weather", "时间与天气", "清晨/正午/黄昏/夜/蓝调时刻；晴/阴/雨/雪/雾"),
        new Field("light_source", "光源来源与色温", "必须写来源，例如「天花板暖钨丝灯 3200K 打出几摊光池」「窗外冷月光」"),
        new Field("architecture", "建筑与结构", "拱顶、窗框、隔间、楼梯、柱子这类结构特征"),
        new Field("material", "材质与年代感", "表面材质与磨损，例如「奶油色瓷砖配深色木护墙板」"),
        new Field("prop", "陈设与关键道具", "长柜台、高脚凳、单只咖啡杯这类具体物件"),
        new Field("palette", "色彩基调", "主色 + 辅色 + 对比，例如「暖棕与奶油色，对比窗外冷蓝」"),
        new Field("layers", "前中后景层次", "前景物件 / 中景主体 / 远景延伸"),
        new Field("camera", "机位与景别", "给出远、中、近三档构图与镜头焦段"),
        new Field("mood", "氛围关键词", "三到五个，例如「怀旧、安静、有点压抑」"),
        new Field("style", "画风与画幅", "电影感摄影 / 动画背景 / 概念设定图；画幅 16:9 或 21:9")
    };

    /// <summary>道具卡字段：尺寸与材质决定它看起来「真不真」，剧情功能决定它为什么在这儿。</summary>
    public static IReadOnlyList<Field> Prop { get; } = new[]
    {
        new Field("purpose", "名称与用途", "这是什么、用来做什么"),
        new Field("scale", "尺寸与比例", "长宽高或与人物的比例，例如「一掌长」"),
        new Field("material", "材质与工艺", "主体材质、包边、五金件、做工"),
        new Field("color", "颜色与磨损", "主色与旧化程度，例如「深棕牛皮，边角磨白」"),
        new Field("detail", "关键细节", "带位置，例如「盖内压着一枚铜印」"),
        new Field("relation", "与角色的关系", "谁用它、怎么用、平时放在哪"),
        new Field("story", "剧情功能", "它为什么出现在这个故事里"),
        new Field("style", "画风与画幅", "干净背景的特写，画幅 1:1 或 4:3")
    };

    /// <summary>分镜字段：镜头语言在前，台词音效在后。</summary>
    public static IReadOnlyList<Field> Storyboard { get; } = new[]
    {
        new Field("shot", "镜号与归属", "第几镜、属于哪一章"),
        new Field("shot_size", "景别", "远景/全景/中景/近景/特写"),
        new Field("subject", "主体与动作", "谁在做什么，动作要具体到一个瞬间"),
        new Field("environment", "环境与时间", "在哪、什么时间与天气"),
        new Field("light", "光线方向", "主光从哪来、打在谁身上"),
        new Field("camera_move", "运镜", "固定/推/拉/摇/跟，一句话说清"),
        new Field("duration", "时长", "以秒计"),
        new Field("audio", "台词与音效", "这一镜说什么、听到什么"),
        new Field("cast", "出场设定", "这一镜用到的角色 / 场景 / 道具（挂成引用），不要各占一个画布节点"),
        new Field("prompt", "出图提示词与负面", "可执行的出图提示词，附负面提示词")
    };

    public static IReadOnlyList<Field> FieldsOf(NodeCategory category) => category switch
    {
        NodeCategory.Character => Character,
        NodeCategory.Scene => Scene,
        NodeCategory.Prop => Prop,
        NodeCategory.Storyboard => Storyboard,
        _ => Storyboard
    };

    /// <summary>节点种类是否有自己的基线（企划 / 通用这类没有，用不着硬套）。</summary>
    public static bool HasBaseline(NodeCategory category) => category is
        NodeCategory.Character or NodeCategory.Scene or NodeCategory.Prop or NodeCategory.Storyboard;

    /// <summary>
    /// 把这些字段拼成「写进正文的小标题清单」，例如 <c>【身份】【体型】【面部】…</c>。
    /// 技能说明与提示词模板都直接嵌这一段，保证两边要求同一种写法——
    /// 不统一小标题的话，人对着一堆自由文本根本没法比对两个角色差在哪。
    /// </summary>
    public static string SectionList(NodeCategory category) =>
        string.Concat(FieldsOf(category).Select(field => $"【{field.Label}】"));

    /// <summary>带说明的小标题清单：给模型看的版本（写「【身份】年龄读感、性别、职业或身份」这样的对照）。</summary>
    public static string SectionGuide(NodeCategory category, string separator = "；") =>
        string.Join(separator, FieldsOf(category).Select(field => $"【{field.Label}】{field.Hint}"));

    /// <summary>
    /// 角色转面图（turnaround / model sheet）的规格。
    ///
    /// **视图数量按行业惯例来：3 到 4，不是越多越好。** 动画与游戏的角色设定图标准是
    /// 「正面 + 侧面 + 背面」三视图，或再加「四分之三」/「面部特写」成四视图；
    /// 「**九视图不是转面图的标准**」——九宫格在中文语境里指的是多表情 / 多姿势那一类
    /// （那属于 <see cref="ExpressionSpec"/>），而 3D 一致性研究（MVDream 那一支）用的是**四个正交视角**。
    /// 理由不是省事，而是**模型在单张图内维持一致性的能力有上限**：面板一多，
    /// 各视图之间的五官、服装、比例就开始互相打架，侧面与背面最早崩。
    /// </summary>
    public static string TurnaroundSpec =>
        "转面图：**一张 16:9 横向**，**正面 / 四分之三 / 侧面 / 背面**四视图并排，"
        + "同一尺度同一比例、脚踩同一条地平线、纯白背景、平光不投影、中性站姿（双臂自然下垂、双脚平行）；"
        + "四个视图的服装与发型措辞必须**逐字相同**（前面写「藏青棉布外套」，背面也得是这五个字）。"
        + "侧 / 背视图频繁崩坏（配饰、手部、不对称设计）时**降回正面 / 侧面 / 背面三视图**，"
        + "对崩掉的那一视角单独重出——一张干净的视图胜过一张失败的转面图，不要再往同一张里塞更多视角。";

    /// <summary>单张正面全身锚点：一眼认人用的那一张，不是转面图的一部分。</summary>
    public static string AnchorSpec =>
        "正面全身锚点：竖向 2:3 或 3:4，人物居中、全身入镜不裁切、纯白背景、平光、中性站姿；"
        + "**单独出一张**，别和转面图挤在同一张里。";

    /// <summary>
    /// 跨镜头一致性纪律。这一条比参考图数量重要得多：
    /// 参考图 2–4 张就够（至少一张正面 + 一张侧面），再多边际收益迅速下降、甚至引入漂移；
    /// 而「同一段外观锚点逐字复用」是必须的——把「藏青外套」写成「深蓝夹克」会被模型当成两件衣服，
    /// 这是角色漂移的头号原因，而且完全可以避免。
    /// </summary>
    public static string ConsistencyRule =>
        "①外观锚点写成一段固定描述，**每个镜头的提示词里逐字复制**，只替换动作 / 情绪 / 景别 / 光线；"
        + "②参考图 2–4 张足够（至少一张正面 + 一张侧面），每张标明它锁什么（锁脸 / 锁服装 / 锁比例）；"
        + "③先出转面图把外观钉死，再出表情表；④同一角色的所有图共用同一段画风描述与同一个种子（模型支持时）。";

    /// <summary>
    /// 负面提示词怎么用——模型方言差异很大，写错等于白写。
    /// </summary>
    public static string NegativeUsageNote =>
        "负面提示词按模型方言给：SD / SDXL 系吃密集负面词；Flux 系**不支持**负面提示词，"
        + "要把负面词改写成正向描述（写「手部结构正确」而不是「多余手指」）；"
        + "Gemini / Nano Banana 一类**不要**给长负面清单，会触发调用异常，同样改成正向表述。"
        + "反向用法：**只要单张视图**（单张全身锚点、道具特写、场景基准图）时，"
        + "要把「多个视角、并排、拼图、分屏」写进负面——否则模型会自作主张排成一排。";

    /// <summary>转面图特有的崩坏：视图之间互相粘连、串脸、串衣服。这一组必须进角色的负面词。</summary>
    public static string ViewBleedNegative =>
        "merged views, views blending into one, overlapping figures, figures touching each other, "
        + "different face between views, different hair between views, different outfit between views, "
        + "different body proportions between views, 视图粘连, 多个视角混在一起, 串脸, 串衣服, "
        + "拼图, 分屏, cropped figure, figure cut off at frame edge, 人物被裁切";

    /// <summary>
    /// 转面图那段**能直接进提示词**的说法（不含「崩了就降级」这类给人看的说明——
    /// 那些是纪律，不是提示词，混进去只会污染模型看到的字）。
    /// </summary>
    public static string TurnaroundPromptLine =>
        "角色转面图，正面 / 四分之三 / 侧面 / 背面四视图并排，同一尺度，脚踩同一地平线，"
        + "纯白背景，平光，中性站姿，四个视图的服装与发型措辞完全一致，横向 16:9";

    /// <summary>
    /// 角色表情表：**单独一张**，并且必须排在转面图之后做（外观没钉死就先做表情，脸型一定漂）。
    /// 注意「九宫格」指的就是这一类多表情网格，不是九个全身转向。
    /// </summary>
    public static string ExpressionSpec =>
        "表情表：**单独一张**（不要和转面图挤在一起），肩部以上六格并排——"
        + "中性 / 微笑 / 皱眉 / 惊讶 / 难过 / 坚定，同一画风、同一脸型、同一发型措辞、纯白背景、平光。";

    /// <summary>
    /// 按种类给负面提示词。人物与场景要避开的东西完全不同，用一份通用清单等于两边都没挡住。
    /// 注意：不是所有模型都支持负面提示词（Flux 系不支持），不支持时这些词要改写成正向描述。
    /// </summary>
    public static string NegativeFor(NodeCategory category) => category switch
    {
        NodeCategory.Character =>
            "低清，模糊，噪点，多余手指，变形的手，多余肢体，五官不对称，塑料感皮肤，死鱼眼，多张脸，"
            + "全身入镜外的裁切，换装，换发型，画风不一致，文字水印，logo，"
            // 转面图最容易崩的不是手指，是**视图之间互相粘连**：串脸、串衣服、比例各自为政。
            + ViewBleedNegative,
        NodeCategory.Scene =>
            "过曝天空，死平光，光源不明，漂浮的物体，不可能的建筑结构，透视错乱，色带，镜头光晕伪影，"
            + "卡通化（若目标为写实），画面出现主要人物（基准图不要人），多个视角并排，拼图，分屏，文字水印，logo",
        NodeCategory.Prop =>
            "低清，模糊，比例失真，材质糊成一团，背景杂乱，多余物件，多个视角并排，拼图，分屏，文字水印，logo",
        _ =>
            "低清，模糊，多余手指，变形的手，多余肢体，文字水印，logo，杂乱背景，过度磨皮"
    };

    /// <summary>出图提示词的骨架：主体 → 外观事实 → 情境 → 画风与画幅。顺序即权重。</summary>
    public static string PromptSkeleton(NodeCategory category) => category switch
    {
        NodeCategory.Character => "角色：「身份 + 体型 + 面部 + 发型 + 标志特征」→「服装 + 配饰」→「姿态 + 光影」→「画风 + 画幅」",
        NodeCategory.Scene => "场景：「空间类型 + 时代地域 + 时间天气」→「光源来源与色温 + 材质」→「陈设 + 前中后景」→「机位 + 氛围 + 画风」",
        NodeCategory.Prop => "道具：「名称用途 + 尺寸比例」→「材质工艺 + 颜色磨损」→「关键细节」→「干净背景 + 画风」",
        _ => "分镜：「景别 + 主体动作」→「环境时间 + 光线方向」→「运镜备注」→「画风 + 画幅」"
    };
}
