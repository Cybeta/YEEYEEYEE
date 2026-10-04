namespace YEEYEEYEE.Desktop;

/// <summary>
/// 各厂家形象的**提示词**：每家一句设计说明 + 构图与画风那几段固定话 + 负面词。
///
/// 为什么单独住这一层：这段话有**两个**消费方——应用内「用当前图像链路重画一张」（设置页），
/// 以及仓库里那批离线生成的形象（`provider-art/`，出图时的原话记在 `provider-art/_generation.json`）。
/// 两份各写一遍的时候，改了一边而另一边没改，同一家就有两个长相，而界面上只会显示其中一个。
/// 现在设计说明只有这一份，两个消费方读的都是它；与记录那边的对账见
/// `ProviderAvatarPromptsMatchTheRecordedBatch` 这条用例。
///
/// **画的是自创角色**：只借这一家的气质、配色与命名意象，明确要求不模仿任何已存在的作品角色或商标——
/// 这与本项目一贯拒绝直接使用第三方 logo / 拟人形象是同一条线。
/// </summary>
public static class ProviderAvatarPrompts
{
    /// <summary>设计说明那一句的起止标记：记录那边是整段提示词，要能把这一句抠出来对账。</summary>
    public const string BriefStart = "角色设计（自创角色）：";

    /// <summary>设计说明的结束标记（后面接的是画风那一段）。</summary>
    public const string BriefEnd = "。画风：";

    /// <summary>认不出的厂家落到哪一份设计。</summary>
    public const string FallbackProviderId = "custom";

    /// <summary>
    /// 每家一句设计说明：一个意象 + 一句外形。颜色不写在这里——它由调用方按这一家的区分色传进来。
    ///
    /// 写成一张表而不是一段通用提示词：九家用同一段话会画出九个长得一样的角色，
    /// 而「每个厂家有自己的形象」的意义就在于一眼能认出是哪一家。
    /// </summary>
    private static readonly Dictionary<string, string> Designs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["deepseek"] = "以深海与鲸为意象：银蓝短发、深蓝外套，衣摆像水波，安静专注的神情",
        ["moonshot"] = "以月亮与夜航为意象：灰紫长发、带星屑的发饰，披一件短披风，手里拿一卷纸",
        ["qwen"] = "以水系与青瓷为意象：青色齐肩发、圆领短衫，发间一枚水纹发卡，笑眼清爽",
        ["zhipu"] = "以书卷与棋盘为意象：琥珀色发、方框眼镜、笔挺的藏青学生制服，神情认真",
        ["siliconflow"] = "以电路与流水为意象：粉发、束起的马尾、连帽短外套，袖口有发光的细线纹样",
        ["openai"] = "以白瓷与回声为意象：银白发、素色高领衫、线条极简，气质冷静",
        ["ollama"] = "以橘色小兽与驼队为意象：橘色短发、毛边连帽衫、脸颊有一道浅色纹路，憨厚可靠",
        ["local"] = "以台灯与工作台为意象：褐色短发、护目镜挂在颈上、多口袋工装，像个小工匠",
        ["custom"] = "以空白画布与铅笔为意象：黑色短发、米色衬衫、手里拿一支笔，干净好相处"
    };

    /// <summary>这一家的设计说明（认不出的厂家落到「自定义」那一句）。</summary>
    public static string Brief(string providerId) =>
        Designs.TryGetValue(providerId, out var brief) ? brief : Designs[FallbackProviderId];

    /// <summary>表里认不认得这一家（用来区分「真按这一家写的」与「落到了兜底」）。</summary>
    public static bool Knows(string providerId) => Designs.ContainsKey(providerId);

    /// <summary>这一家的形象应该长什么样（写给模型看的一段话）。</summary>
    public static string Build(string providerId, string providerName, string colorHex) =>
        "一张二次元风格的半身角色立绘，正方形构图，人物居中、正面朝向镜头、胸像以上入镜，"
        + $"背景是纯净的深色渐变（以 {colorHex} 为点缀色），带一圈很淡的同色光晕，没有任何文字、符号或商标。"
        + $"{BriefStart}{Brief(providerId)}。"
        + "画风：干净的日式动画赛璐璐上色，线条清晰，五官端正、手指与肢体自然，"
        + "明暗过渡柔和，适合当一枚圆形头像看。"
        + $"这位角色的设定与「{providerName}」这个名字的气质相配，但**必须是原创角色，"
        + "不要模仿任何已存在的作品、角色、吉祥物或商标**。";

    /// <summary>负面提示词：出图时最常见的那几种坏法，加上「别抄现成的」。</summary>
    public const string NegativePrompt =
        "低清，模糊，噪点，多余手指，变形的手，多余肢体，五官不对称，塑料感皮肤，死鱼眼，多张脸，"
        + "文字，水印，logo，签名，边框，拼图，分屏，全身入镜外的裁切，"
        + "已知作品的角色，已知品牌吉祥物，商标，模仿现有角色";

    /// <summary>
    /// 从一整段提示词里抠出设计说明那一句（对账用）。抠不出来返回空串——
    /// 那说明提示词的结构变了，调用方据此报「对不上」，而不是拿半句话去比。
    /// </summary>
    public static string ExtractBrief(string prompt)
    {
        if (string.IsNullOrEmpty(prompt)) return string.Empty;
        var start = prompt.IndexOf(BriefStart, StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        start += BriefStart.Length;
        var end = prompt.IndexOf(BriefEnd, start, StringComparison.Ordinal);
        return end < 0 ? string.Empty : prompt[start..end];
    }
}
