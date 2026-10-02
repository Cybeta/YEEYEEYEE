using System.Text;

namespace YEEYEEYEE.Desktop;

/// <summary>
/// 让视觉模型给一批图**打分**（照着我们写的出图要求与负面提示词判，不是让它们在内部互相比）。
///
/// **判据写在提示词里、是公开的**——这是第 8 条里「五档的判据是谁定的」那一问的答案：
/// 判据不藏在代码深处，就写在这里，谁都能读、能改、能吵。代码只负责把分数换算成档位。
///
/// 四条定下来的做法，每条都是为了不撒谎：
/// · **要绝对分，不要名次。** 名次是相对的：同一批六张都很差，第一名照样是「金」，而那张牌说的是
///   「这批里最好」，不是「真的好」。绝对分有固定标准（见 <see cref="QualityJudgement.TierForScore"/>），
///   所以一张图也能判，用户也能拿两次出图的结果互相比较。
/// · **负面提示词要逐条对照。** 用户写「不要多余手指」，就是在说「画出多余手指的那张不算数」；
///   不把这份清单交给评审，就等于让评审去猜用户在意的到底是什么。踩中的要**原词报回来**。
/// · **四类看得见的坏图单独点名。** 贴合度；崩坏（手指数量不对、肢体断裂）；穿帮（多出来的物体、文字水印、
///   拼图痕迹、光影矛盾）；**角色与物体互相嵌进去**（人陷进地面、身体与道具穿插融合）。
///   这四类是出图最常见、用户最在意的「不能接受」，所以要问，不能指望评审自己想起来。
/// · **要求理由具体。** 「很好」「不错」这类空话没法让用户判断这个判断值不值得信；
///   理由会原样显示在卡上，所以提示词里就明确要求说清扣分扣在哪。
/// </summary>
public static class ImageQualityJudge
{
    /// <summary>
    /// 系统提示：只输出 JSON。与创作助手的系统提示分开——那套要求输出改动块，会与这里打架。
    /// </summary>
    public const string SystemPrompt =
        "你是看图验收的评审。只输出 JSON，不要解释、不要 Markdown 代码块、不要多余文字。";

    /// <summary>四条评分维度的原文。抽成常量，是因为测试要钉住「这四类必须问到」。</summary>
    public static readonly IReadOnlyList<string> Checks = new[]
    {
        "贴合度：画面有没有画出出图要求里的主体、服装、场景与构图，有没有画错或漏掉要求的东西。",
        "崩坏：多余手指 / 手指数量不对、肢体断裂或粘连、五官扭曲、糊成一团、出现错字乱码。",
        "穿帮：不该出现的东西（文字、水印、边框、拼图痕迹、多出来的人或物）、透视与阴影不合理、光源自相矛盾。",
        "嵌入错误：角色与物体互相穿进去（人陷进地面或家具、身体与道具融合在一起、手插进身体），以及不该裁的部位被裁掉。"
    };

    /// <summary>
    /// 用户提示。图片按**下标从 1 开始**编号，这个编号就是发给模型的顺序，
    /// 所以调用方必须按同一个顺序把图片放进去。
    /// </summary>
    /// <param name="nodeContext">生图这一步在节点树里的位置（节点、类别、章节、引用的设定）。空串表示没有。</param>
    /// <param name="requirement">这一批实际发出去的出图提示词。</param>
    /// <param name="negative">这一批实际发出去的负面提示词。空串表示这次没写负面词。</param>
    /// <param name="count">这一批有几张。</param>
    public static string BuildUserPrompt(string nodeContext, string requirement, string negative, int count)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"下面是同一批出的 {count} 张图，编号 1 到 {count}（编号就是下面图片的先后顺序）。");
        builder.AppendLine("请给**每一张**打一个 0–10 的整数分，评价标准是「它作为这一张图，本身有多达标」——");
        builder.AppendLine("**不要把它们互相比较、不要排名次**，每张的分数要独立地看它自己够不够好。");
        builder.AppendLine();

        if (!string.IsNullOrWhiteSpace(nodeContext))
        {
            builder.AppendLine("**这一步在项目里的位置：**");
            builder.AppendLine(nodeContext.Trim());
            builder.AppendLine();
        }

        builder.AppendLine("**这次出图的要求：**");
        builder.AppendLine(string.IsNullOrWhiteSpace(requirement) ? "（这一批没有留下提示词）" : requirement.Trim());
        builder.AppendLine();
        builder.AppendLine("**这次出图的负面提示词（要逐张、逐条对照）：**");
        builder.AppendLine(string.IsNullOrWhiteSpace(negative)
            ? "（这次没有写负面提示词，hits 一律给空数组）"
            : negative.Trim());
        builder.AppendLine();

        builder.AppendLine("**只看这四件事，不要看别的：**");
        for (var index = 0; index < Checks.Count; index++)
            builder.AppendLine($"{index + 1}. {Checks[index]}");
        builder.AppendLine();

        builder.AppendLine("**打分尺度（这是绝对标准，同一批里分数可以完全一样）：**");
        builder.AppendLine("· 9–10：要的东西都在、没有可指出的毛病，可以直接用。");
        builder.AppendLine("· 7–8：整体达标，有小的瑕疵但不影响使用。");
        builder.AppendLine("· 5–6：能看，但有明显问题（例如某处崩坏、要求的东西只画出一半）。");
        builder.AppendLine("· 3–4：问题很显眼，多半要重出。");
        builder.AppendLine("· 0–2：基本不可用（主体没画出来、整张崩掉、或者踩中负面提示词）。");
        builder.AppendLine();

        builder.AppendLine("**硬性要求：**");
        builder.AppendLine($"· 必须给出 {count} 张**各自**的分数，一张都不能漏，编号不能重复。");
        builder.AppendLine("· 分数是 0–10 的**整数**，不要给小数、不要给区间。");
        builder.AppendLine("· **不要排名次、不要为了拉开差距而故意给不一样的分数**；都很好就给一样的高分。");
        builder.AppendLine("· 每张给一句话理由（20 字以内），说清扣分扣在哪、或者好在哪，不要写空话。");
        builder.AppendLine("· **hits 只填真正踩中的负面提示词原词**（照抄上面的词）；没踩中就给空数组，不要自己编词。");
        builder.AppendLine("· 只输出下面这个形状的 JSON，前后不要加任何解释文字：");
        builder.AppendLine();
        builder.AppendLine("""{"scores":[{"index":1,"score":8,"hits":[],"note":"主体清楚，手指正常"},{"index":2,"score":3,"hits":["多余手指"],"note":"左手多一根手指"}]}""");
        builder.AppendLine();
        builder.AppendLine("scores 数组里每一项的 index 是上面那个图片编号（从 1 起）。");
        return builder.ToString();
    }
}
