using System.Text.Json;

namespace YEEYEEYEE.Desktop;

/// <summary>抽卡用的质量档。**数值即高低**（白最低、金最高），档位之间的比较直接用这个顺序。</summary>
public enum QualityTier
{
    White = 0,
    Blue = 1,
    Purple = 2,
    Red = 3,
    Gold = 4
}

/// <summary>
/// 这一档是**谁说的**。必须一路带着来源，不能只留一个档位：
/// 「模型觉得这张最好」与「这一批里它确实排第一」在界面上长得一样，但它们是两件事。
/// </summary>
public enum QualitySource
{
    /// <summary>没判过。**不显示档位**——不是「判成白档」。</summary>
    None = 0,

    /// <summary>本地技术筛查：只判客观坏图（读不出来 / 几乎只有一个颜色 / 尺寸明显不对），不判好看。</summary>
    Technical = 1,

    /// <summary>视觉模型看出来的名次。带一句话理由，原样展示。</summary>
    Model = 2
}

/// <summary>一格的判定结果。</summary>
public sealed class SlotQuality
{
    public QualityTier Tier { get; set; }

    public QualitySource Source { get; set; }

    /// <summary>一句话理由（模型给的原文，或本地筛查命中的那条指标）。**原样展示，不加工**。</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>名次（1 起）。<see cref="QualitySource.Technical"/> 没有名次时为 0。</summary>
    public int Rank { get; set; }

    /// <summary>参与排名的张数（用来显示「第 2 名 / 共 6 张」）。</summary>
    public int Ranked { get; set; }
}

/// <summary>模型给出的名次里的一项。</summary>
public sealed record QualityRanking(int Index, string Note);

/// <summary>解析结果：要么得到一份完整名次，要么得到**给用户看的失败原因**。</summary>
public sealed record QualityParseResult(IReadOnlyList<QualityRanking>? Ranking, string? Error);

/// <summary>
/// 判定这层的地基：**名次 → 档位**、模型回复的解析、以及本地技术筛查的判据。
///
/// 三条定下来的原则（都是为了让「档位」这件事不撒谎）：
///
/// 1. **档位来自同一批里的名次，不是绝对分数。** 绝对分数没法标定——「8 分算不算金」永远要重新吵一次，
///    而且模型给绝对分很不稳。而「这 6 张里哪张最好」是它答得可靠的，也正是用户真正要的信息。
///    所以：**第 1 名金、第 2 名红、第 3 名紫、其余蓝、最后一名白**。相应地，
///    **只有一张时不判档**——没有可比的第二张，名次没有意义。
/// 2. **本地筛查只判客观坏图。** 分辨率高不等于好看、对比度高不等于好看，所以本地那一层不碰「质量」，
///    只判「这张图明显是坏的」：读不出来、整张几乎一个颜色、尺寸比要求的小一大截。
/// 3. **判不出来就不显示档位**（<see cref="QualitySource.None"/>），绝不用默认档冒充判过的结果。
/// </summary>
public static class QualityJudgement
{
    /// <summary>
    /// 能参与排名的**最少张数**。一张不判：名次的意义来自比较，一张没有比较对象。
    /// </summary>
    public const int MinImagesToRank = 2;

    /// <summary>
    /// 档位是模型看图给的判断，不是客观结论——这句话要跟着档位一起出现在界面上。
    /// 放在模型层而不是界面层，是为了让它没那么容易被漏掉。
    /// </summary>
    public const string ModelDisclaimer = "档位是模型看图的判断，不是客观结论：同一批再判一次未必一样。";

    /// <summary>档位的中文名（界面上就显示这个字）。</summary>
    public static string Label(QualityTier tier) => tier switch
    {
        QualityTier.Gold => "金",
        QualityTier.Red => "红",
        QualityTier.Purple => "紫",
        QualityTier.Blue => "蓝",
        _ => "白"
    };

    /// <summary>
    /// 每一档翻开发射的光点数。白档给得比「没判过」还少——白是**判出来的最低档**，
    /// 而没判过是「不知道」，两者不该长得一样。
    /// </summary>
    public static int SparksFor(QualityTier tier) => tier switch
    {
        QualityTier.Gold => 12,
        QualityTier.Red => 9,
        QualityTier.Purple => 7,
        QualityTier.Blue => 5,
        _ => 3
    };

    /// <summary>每一档卡背后那团光的加亮倍数。金档最亮，白档反而压一点。</summary>
    public static double GlowFor(QualityTier tier) => tier switch
    {
        QualityTier.Gold => 1.45,
        QualityTier.Red => 1.3,
        QualityTier.Purple => 1.18,
        QualityTier.Blue => 1.06,
        _ => 0.82
    };

    /// <summary>
    /// 名次 → 档位。<paramref name="total"/> 是**参与排名的张数**。
    ///
    /// 写成这个形状（而不是一张固定的表）是为了让「少的批次不给最高档」这条自动成立：
    /// 两张里排第一的那张只到金（它确实是这批最好的），但两张里的第二张直接是白（它是最后一名）。
    /// 中段（红/紫）要求批次至少有那么长，否则「第 2 名」在两三张里太容易拿到，说了等于没说。
    /// </summary>
    public static QualityTier TierForRank(int rank, int total)
    {
        if (total < MinImagesToRank || rank < 1 || rank > total) return QualityTier.White;
        if (rank == 1) return QualityTier.Gold;
        if (rank == total) return QualityTier.White;
        if (rank == 2) return QualityTier.Red;
        if (rank == 3) return QualityTier.Purple;
        return QualityTier.Blue;
    }

    /// <summary>
    /// 把「模型给的名次」落成每一格的档位。<paramref name="slotIndices"/> 是**发给模型的那几格**的序号
    /// （发给它的顺序就是它在提示词里的编号），返回按序号排好的结果。
    /// </summary>
    public static IReadOnlyList<(int Index, SlotQuality Quality)> ToGrades(
        IReadOnlyList<int> slotIndices, IReadOnlyList<QualityRanking> ranking)
    {
        var total = slotIndices.Count;

        // 提示词里给模型的编号是「第几张」（1 基，按发送顺序），先把它映射回槽位序号。
        var slotOfNumber = new Dictionary<int, int>();
        for (var order = 0; order < total; order++) slotOfNumber[order + 1] = slotIndices[order];

        // ranking 是**按名次排好的**（第 0 项就是第 1 名），所以它在数组里的位置就是名次。
        var grades = new List<(int Index, SlotQuality Quality)>();
        for (var position = 0; position < ranking.Count; position++)
        {
            if (!slotOfNumber.TryGetValue(ranking[position].Index, out var slotIndex)) continue;
            grades.Add((slotIndex, new SlotQuality
            {
                Tier = TierForRank(position + 1, total),
                Source = QualitySource.Model,
                Reason = ranking[position].Note,
                Rank = position + 1,
                Ranked = total
            }));
        }

        // 交出去之前按槽位序号排好：调用方要的是「第几格是什么档」，不是模型的名次顺序。
        grades.Sort((left, right) => left.Index.CompareTo(right.Index));
        return grades;
    }

    /// <summary>
    /// 从模型回复里读出名次。**容错但要完整**：
    /// 外面包了 ``` 代码块、或前后带了说明文字都能读；但名次必须是**恰好这几格的完整排列**
    /// （不重不漏），否则整份作废——半份名次会给出武断的档位，那比没有档位更糟。
    /// </summary>
    public static QualityParseResult ParseRanking(string reply, int expectedCount)
    {
        if (expectedCount < MinImagesToRank) return new(null, "只出了一张，没有可比的第二张，不判档位。");

        var json = ExtractJson(reply);
        if (json is null) return new(null, "模型没有返回可解析的 JSON。");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException error)
        {
            return new(null, $"模型返回的 JSON 解析失败：{error.Message}");
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("ranking", out var list) || list.ValueKind != JsonValueKind.Array)
                return new(null, "模型返回的 JSON 里没有 ranking 数组。");

            var parsed = new List<QualityRanking>();
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("index", out var indexElement) || !indexElement.TryGetInt32(out var index))
                    continue;
                var note = item.TryGetProperty("note", out var noteElement) && noteElement.ValueKind == JsonValueKind.String
                    ? (noteElement.GetString() ?? string.Empty).Trim()
                    : string.Empty;
                parsed.Add(new QualityRanking(index, note));
            }

            if (parsed.Count != expectedCount)
                return new(null, $"名次数量不对：收到 {parsed.Count} 项，应当是 {expectedCount} 项。");

            var seen = new HashSet<int>();
            foreach (var item in parsed)
            {
                if (item.Index < 1 || item.Index > expectedCount || !seen.Add(item.Index))
                    return new(null, $"名次里出现了重复或越界的编号（{item.Index}）。");
            }

            return new(parsed, null);
        }
    }

    /// <summary>从回复里抠出最外层的那个 JSON 对象：前后有说明文字、外面包了 ``` 都还能读。</summary>
    private static string? ExtractJson(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;
        var start = reply.IndexOf('{');
        var end = reply.LastIndexOf('}');
        return start < 0 || end <= start ? null : reply[start..(end + 1)];
    }
}

/// <summary>
/// 本地技术筛查要的那几个数。**由界面层量出来**（只有界面层能解码图片），规则本身在
/// <see cref="TechnicalScreening"/> 里，所以规则可测、测量不可测的那一半留在薄薄的一层适配里。
/// </summary>
/// <param name="SampledPixels">采了多少个像素。</param>
/// <param name="DistinctColors">采样里有多少种颜色（相近的并成一桶——只关心「是不是一个颜色」）。</param>
/// <param name="ExpectedShortSide">出图要求的短边；0 表示不检查这一条。</param>
public readonly record struct ImageFacts(
    bool Decoded,
    int Width,
    int Height,
    int SampledPixels,
    int DistinctColors,
    int ExpectedShortSide);

/// <summary>
/// 本地技术筛查。**只判客观坏图，绝不判好看。**
///
/// 为什么这一层不能碰「质量」：分辨率高不等于好看、对比度高不等于好看、文件大不等于好看。
/// 拿这些当质量分，等于给用户一个看起来有依据、其实没有依据的评级。
/// 所以这里只认三件**可以指出来、也可以被验证**的事：读不出来、整张几乎一个颜色、尺寸比要求小一大截。
/// 命中就判最低档并把命中的那条写进理由；没命中就**不判**（返回 null），把「好不好」留给模型或干脆不判。
/// </summary>
public static class TechnicalScreening
{
    public static SlotQuality? Screen(in ImageFacts facts)
    {
        if (!facts.Decoded) return Whitish("这个文件读不出来（多半没写完整）");
        if (facts.Width <= 0 || facts.Height <= 0) return Whitish("读到的尺寸是空的");

        // 采样里只有一个颜色 → 整张是纯色，正常出图不会这样（那是没出成内容）。
        if (facts.SampledPixels > 0 && facts.DistinctColors <= 1)
            return Whitish("整张图几乎只有一个颜色，多半是没有出成内容");

        // 短边连要求的一半都不到 → 服务商给的尺寸明显不对，缩小了也就是一张糊图。
        if (facts.ExpectedShortSide > 0 && Math.Min(facts.Width, facts.Height) * 2 < facts.ExpectedShortSide)
            return Whitish($"尺寸只有 {facts.Width}×{facts.Height}，比要求的小很多");

        return null;
    }

    private static SlotQuality Whitish(string reason) => new()
    {
        Tier = QualityTier.White,
        Source = QualitySource.Technical,
        Reason = reason
    };
}
