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
/// 「模型看过图给的分数」与「本地筛查说这张文件是坏的」在界面上长得一样，但它们是两件事。
/// </summary>
public enum QualitySource
{
    /// <summary>没判过。**不显示档位**——不是「判成白档」。</summary>
    None = 0,

    /// <summary>本地技术筛查：只判客观坏图（读不出来 / 几乎只有一个颜色 / 尺寸明显不对），不判好看。</summary>
    Technical = 1,

    /// <summary>视觉模型看图的评分。带一个分数和一句话理由，原样展示。</summary>
    Model = 2
}

/// <summary>一格的判定结果。</summary>
public sealed class SlotQuality
{
    /// <summary>没有分数（本地筛查命中的那一类没有分）。</summary>
    public const int NoScore = -1;

    public QualityTier Tier { get; set; }

    public QualitySource Source { get; set; }

    /// <summary>一句话理由（模型给的原文，或本地筛查命中的那条指标）。**原样展示，不加工**。</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>模型给的分数（0–10）；本地筛查没有分数时为 <see cref="NoScore"/>。</summary>
    public int Score { get; set; } = NoScore;

    /// <summary>
    /// 这张图**踩中的负面提示词条目**（原词照抄）。空数组 = 没踩中。
    ///
    /// 为什么单独留一个字段、不并进 <see cref="Reason"/>：它决定卡面要不要裂开，
    /// 而裂开是这张卡在整排里最显眼的一件事——把它混进一句自然语言里，代码就得去解析自己的文案。
    /// </summary>
    public IReadOnlyList<string> NegativeHits { get; set; } = Array.Empty<string>();

    /// <summary>
    /// 踩中负面提示词 → **裂纹卡**。
    ///
    /// 这是用户点名要的一种卡：出图时明确写了「不要多余手指」，结果还是画出来了，这张就该被一眼认出来。
    /// 档位仍然按分数给（分数是分数），但卡面会裂开、档位牌上写的是「裂纹」——两件事都留着，谁也不盖住谁。
    /// </summary>
    public bool IsCracked => Source != QualitySource.None && NegativeHits.Count > 0;

    /// <summary>「8/10」这种写法；没有分数就是空串。</summary>
    public string ScoreLabel => Score == NoScore ? string.Empty : $"{Score}/{QualityJudgement.MaxScore}";
}

/// <summary>模型给出的评分里的一项。</summary>
public sealed record QualityScore(int Index, int Score, IReadOnlyList<string> Hits, string Note);

/// <summary>解析结果：要么得到完整的一份评分，要么得到**给用户看的失败原因**。</summary>
public sealed record QualityParseResult(IReadOnlyList<QualityScore>? Scores, string? Error);

/// <summary>
/// 判定这层的地基：**分数 → 档位**、模型回复的解析、以及本地技术筛查的判据。
///
/// 四条定下来的原则（都是为了让「档位」这件事不撒谎）：
///
/// 1. **档位来自分数，而分数是照着「出图要求 + 负面提示词」给的绝对分，不是同一批里的名次。**
///    名次那套的问题是：同一批里就算六张都很差，也一定有一张「金」，而那张牌说的是「这批里最好」，
///    不是「真的好」。改成绝对分之后，「好」才有固定标准，一张图自己也能判。
/// 2. **分数只在 0–10 的整数上取，档位由固定的分数带决定**（见 <see cref="TierForScore"/>）。
///    分数带是**公开的**：写在界面上、写在文档里，谁都能说「为什么 8 分是红不是金」。
/// 3. **踩中负面提示词就是裂纹卡**（<see cref="SlotQuality.IsCracked"/>）——档位照给，卡面裂开。
/// 4. **判不出来就不显示档位**（<see cref="QualitySource.None"/>），绝不用默认档冒充判过的结果。
/// </summary>
public static class QualityJudgement
{
    public const int MinScore = 0;

    public const int MaxScore = 10;

    /// <summary>
    /// 档位是模型看图给的判断，不是客观结论——这句话要跟着档位一起出现在界面上。
    /// 放在模型层而不是界面层，是为了让它没那么容易被漏掉。
    /// </summary>
    public const string ModelDisclaimer =
        "档位是模型照着你写的提示词与负面词给的判断，不是客观结论：同一张图换个模型可能换档。";

    /// <summary>档位的中文名（界面上就显示这个字）。</summary>
    public static string Label(QualityTier tier) => tier switch
    {
        QualityTier.Gold => "金",
        QualityTier.Red => "红",
        QualityTier.Purple => "紫",
        QualityTier.Blue => "蓝",
        _ => "白"
    };

    /// <summary>档位的分数带，写给人看的一句话（设置页与文档里显示它）。</summary>
    public const string ScoreBandNote =
        "按 0–10 分定档：9 分以上金、7–8 分红、5–6 分紫、3–4 分蓝、2 分以下白；踩中负面提示词的一律是裂纹卡。";

    /// <summary>
    /// 分数 → 档位。**这是唯一一处定档的规则**，分数带就写在这里。
    ///
    /// 带子按「用户能记住」来切：上界留得紧（金要 9 分以上），所以金是稀有的；
    /// 中段宽（5–6 紫、7–8 红），因为「还行」与「不错」之间的差别本来就模糊，切太细会显得精确得没有道理。
    /// </summary>
    public static QualityTier TierForScore(int score)
    {
        if (score < MinScore) return QualityTier.White;
        if (score >= 9) return QualityTier.Gold;
        if (score >= 7) return QualityTier.Red;
        if (score >= 5) return QualityTier.Purple;
        if (score >= 3) return QualityTier.Blue;
        return QualityTier.White;
    }

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

    /// <summary>把踩中的负面词拼成一行（卡面与页脚都用这个写法）。</summary>
    public static string DescribeHits(IReadOnlyList<string> hits) => string.Join("、", hits);

    /// <summary>
    /// 把「模型给的分数」落成每一格的档位。<paramref name="slotIndices"/> 是**发给模型的那几格**的序号
    /// （发给它的顺序就是它在提示词里的编号），返回按序号排好的结果。
    /// </summary>
    public static IReadOnlyList<(int Index, SlotQuality Quality)> ToGrades(
        IReadOnlyList<int> slotIndices, IReadOnlyList<QualityScore> scores)
    {
        // 提示词里给模型的编号是「第几张」（1 基，按发送顺序），先把它映射回槽位序号。
        var slotOfNumber = new Dictionary<int, int>();
        for (var order = 0; order < slotIndices.Count; order++) slotOfNumber[order + 1] = slotIndices[order];

        var grades = new List<(int Index, SlotQuality Quality)>();
        foreach (var score in scores)
        {
            if (!slotOfNumber.TryGetValue(score.Index, out var slotIndex)) continue;
            grades.Add((slotIndex, new SlotQuality
            {
                Tier = TierForScore(score.Score),
                Source = QualitySource.Model,
                Reason = score.Note,
                Score = score.Score,
                NegativeHits = score.Hits
            }));
        }

        // 交出去之前按槽位序号排好：调用方要的是「第几格是什么档」，不是模型回复的顺序。
        grades.Sort((left, right) => left.Index.CompareTo(right.Index));
        return grades;
    }

    /// <summary>
    /// 从模型回复里读出评分。**容错但要完整**：
    /// 外面包了 ``` 代码块、或前后带了说明文字都能读；但**每一格都必须有且只有一个分数**，
    /// 而且分数必须是 0–10 的整数——半份评分会给出武断的档位，越界的分数会让分数带失去意义。
    /// </summary>
    public static QualityParseResult ParseScores(string reply, int expectedCount)
    {
        if (expectedCount < 1) return new(null, "没有可判的图。");

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
            if (!document.RootElement.TryGetProperty("scores", out var list) || list.ValueKind != JsonValueKind.Array)
                return new(null, "模型返回的 JSON 里没有 scores 数组。");

            var parsed = new List<QualityScore>();
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                // ValueKind 必须先判：JsonElement 的 TryGetInt32 在「类型不是数字」时是**抛异常**的，
                // 不是返回 false——直接用它读模型回的 "8" 会把整个判定炸掉。
                if (!item.TryGetProperty("index", out var indexElement)
                    || indexElement.ValueKind != JsonValueKind.Number
                    || !indexElement.TryGetInt32(out var index))
                    continue;

                if (!item.TryGetProperty("score", out var scoreElement)
                    || scoreElement.ValueKind != JsonValueKind.Number
                    || !scoreElement.TryGetInt32(out var score))
                    return new(null, $"第 {index} 张没有给出整数分数。");

                // 越界的分数**不作数**：夹到边界会把「模型答错了」伪装成「它给了满分」。
                if (score < MinScore || score > MaxScore)
                    return new(null, $"第 {index} 张的分数 {score} 超出了 {MinScore}–{MaxScore} 的范围。");

                var hits = new List<string>();
                if (item.TryGetProperty("hits", out var hitsElement) && hitsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var hit in hitsElement.EnumerateArray())
                    {
                        if (hit.ValueKind != JsonValueKind.String) continue;
                        var text = (hit.GetString() ?? string.Empty).Trim();
                        if (text.Length > 0) hits.Add(text);
                    }
                }

                var note = item.TryGetProperty("note", out var noteElement) && noteElement.ValueKind == JsonValueKind.String
                    ? (noteElement.GetString() ?? string.Empty).Trim()
                    : string.Empty;

                parsed.Add(new QualityScore(index, score, hits, note));
            }

            if (parsed.Count != expectedCount)
                return new(null, $"评分数量不对：收到 {parsed.Count} 项，应当是 {expectedCount} 项。");

            var seen = new HashSet<int>();
            foreach (var item in parsed)
            {
                if (item.Index < 1 || item.Index > expectedCount || !seen.Add(item.Index))
                    return new(null, $"评分里出现了重复或越界的编号（{item.Index}）。");
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
/// 命中就判最低档并把命中的那条写进理由；没命中就**不判**（返回 null），把「好不好」留给模型。
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
