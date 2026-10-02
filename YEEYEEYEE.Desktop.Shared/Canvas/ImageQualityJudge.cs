using System.Text;

namespace YEEYEEYEE.Desktop;

/// <summary>
/// 让视觉模型给一批图排个名次。
///
/// **判据写在提示词里、是公开的**——这是第 8 条里「五档的判据是谁定的」那一问的答案：
/// 判据不藏在代码深处，就写在这里，谁都能读、能改、能吵。代码只负责把名次换档位。
///
/// 三条定下来的做法，每条都是为了不撒谎：
/// · **只要名次，不要分数。** 分数要标定（多少分算金），标定每次都得重吵一遍；
///   名次是相对的，不需要标定，而「这几张里哪张最好」也确实是模型答得可靠的那个问题。
/// · **不许并列。** 并列会让「第 1 名」落到两张图上，档位就没法定——名次必须是一个完整排列。
/// · **要求理由具体。** 「很好」「不错」这类空话没法让用户判断这个判断值不值得信；
///   理由会原样显示在卡上，所以提示词里就明确要求说清「为什么排这个位置」。
/// </summary>
public static class ImageQualityJudge
{
    /// <summary>
    /// 系统提示：只输出 JSON。与创作助手的系统提示分开——那套要求输出改动块，会与这里打架。
    /// </summary>
    public const string SystemPrompt =
        "你是看图挑图的评审。只输出 JSON，不要解释、不要 Markdown 代码块、不要多余文字。";

    /// <summary>
    /// 用户提示。图片按**下标从 1 开始**编号，这个编号就是发给模型的顺序，
    /// 所以调用方必须按同一个顺序把图片放进去。
    /// </summary>
    public static string BuildUserPrompt(string nodeTitle, string requirement, int count)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"下面是同一批出的 {count} 张图，编号 1 到 {count}（编号就是下面图片的先后顺序）。");
        builder.AppendLine("请按「作为这一批候选里的一张，它有多值得留下」从好到差，排出一个完整名次。");
        builder.AppendLine();
        builder.AppendLine("**只看这三件事，不要看别的：**");
        builder.AppendLine("1. 与出图要求的贴合度。");
        builder.AppendLine("2. 画面有没有明显崩坏：多手多指、肢体断裂、五官扭曲、糊成一团、出现错字乱码。");
        builder.AppendLine("3. 构图与光影：主体是否清楚、有没有被裁掉、明暗是否合理。");
        builder.AppendLine();
        builder.AppendLine("**硬性要求：**");
        builder.AppendLine($"· 必须是 {count} 张的**完整名次**：不许并列，不许漏掉任何一张。");
        builder.AppendLine("· 每张给一句话理由（20 字以内），说清它排在这个位置的具体原因，不要写空话。");
        builder.AppendLine("· **只排序、不打分**，不要输出任何分数。");
        builder.AppendLine("· 只输出下面这个形状的 JSON，前后不要加任何解释文字：");
        builder.AppendLine();
        builder.AppendLine("""{"ranking":[{"index":1,"note":"为什么它排第一"},{"index":2,"note":"为什么它排第二"}]}""");
        builder.AppendLine();
        builder.AppendLine("ranking 数组里**第 1 项就是第 1 名**；index 是上面那个图片编号（从 1 起）。");
        builder.AppendLine();
        builder.AppendLine($"出图要求：{(string.IsNullOrWhiteSpace(requirement) ? "（这一批没有留下提示词）" : requirement)}");
        if (!string.IsNullOrWhiteSpace(nodeTitle)) builder.AppendLine($"所属节点：{nodeTitle}");
        return builder.ToString();
    }
}
