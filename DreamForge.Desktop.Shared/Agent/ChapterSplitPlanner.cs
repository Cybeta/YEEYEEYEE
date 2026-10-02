using System.Text;
using System.Text.RegularExpressions;

namespace DreamForge.Desktop;

/// <summary>一章的草稿：标题与正文**全部来自源文本**，不臆造任何内容。</summary>
public sealed record ChapterDraft(string Title, string Content, int Order);

/// <summary>这份草稿是怎么来的。界面要如实说明，否则「拆出 12 章」看不出是照标题拆的还是按长度切的。</summary>
public enum ChapterSplitMode
{
    /// <summary>没拆（没内容）。</summary>
    None,

    /// <summary>按文本里已有的章节标题拆。</summary>
    ByHeading,

    /// <summary>文本里没有章节标题，按长度切分。</summary>
    ByLength
}

public sealed record ChapterSplitResult(ChapterSplitMode Mode, IReadOnlyList<ChapterDraft> Chapters, string Note)
{
    public bool IsEmpty => Chapters.Count == 0;
}

/// <summary>
/// 把一段源文本拆成章节草稿，并从项目设定库里**按名字匹配**这一章的出场资源。
///
/// 为什么要有这一层（而不是像以前那样直接铺一串写死的节点）：
/// 旧实现里「生成章节工作树」不读源文本，直接建「林默 · 阿岚 · 老周」「24 镜」这种写死的节点，
/// 用户会以为项目里已经有内容了——那是假数据。这里改成**只做两件真事**：
/// ① 按文本自身结构（章节标题，或长度）切章；② 用项目里真实存在的设定名去匹配「出场」。
/// 文本里没有的章节、设定库里没有的角色，一个都不编。分镜需要模型能力，交给 Agent 那条路去产出。
/// </summary>
public static class ChapterSplitPlanner
{
    public const int DefaultMaxChapters = 40;

    /// <summary>按长度切分时，一章的目标字数。太小会切得很碎，太大又失去「章」的意义。</summary>
    private const int TargetChapterLength = 1200;

    private const int TitleSummaryLength = 14;

    /// <summary>
    /// 章节标记出现在行首：「第N章 / 第N节 / 第N回 / 第N卷」、英文 Chapter N、markdown 标题、整行 【小标题】。
    /// 标记之后还允许跟一小段标题文字（「第一章 初到小城」很常见），但**带句读的长句不算**——
    /// 「第一章里他写到了雨。」是正文，不是标题。整篇少于 2 处标记时不算「有章节结构」，会退化成按长度切分。
    /// </summary>
    private static readonly Regex HeadingMarker = new(
        @"^\s*(第\s*[0-9０-９一二三四五六七八九十百千两]+\s*[章节節回卷]|chapter\s*\d+|#{1,6}\s*\S|【[^】]{1,24}】)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const int MaxHeadingTailLength = 24;

    private static bool IsHeading(string line)
    {
        var match = HeadingMarker.Match(line);
        if (!match.Success) return false;
        var tail = line[match.Length..].Trim().Trim('：', ':', '、', '.', '·', '-', '—');
        if (tail.Length > MaxHeadingTailLength) return false;
        return tail.IndexOfAny(new[] { '。', '！', '？', '；', '.', '!', '?', ';' }) < 0;
    }

    public static ChapterSplitResult Split(string? text, int maxChapters = DefaultMaxChapters)
    {
        var limit = Math.Max(1, maxChapters);
        var normalized = Normalize(text);
        if (normalized.Length == 0)
            return new ChapterSplitResult(ChapterSplitMode.None, Array.Empty<ChapterDraft>(), "源节点还没有内容，先把总纲或正文写进去");

        var lines = normalized.Split('\n');
        var headings = new List<int>();
        for (var index = 0; index < lines.Length; index++)
            if (IsHeading(lines[index])) headings.Add(index);

        if (headings.Count >= 2 && headings.Count <= limit)
            return ByHeading(lines, headings);
        if (headings.Count > limit)
            return ByLength(normalized, limit, $"文本里有 {headings.Count} 个章节标题，超过一次拆分的上限 {limit}，改成按长度切分");

        return ByLength(normalized, limit, "文本里没有成节的章节标题，按长度切分");
    }

    private static ChapterSplitResult ByHeading(IReadOnlyList<string> lines, IReadOnlyList<int> headings)
    {
        var chapters = new List<ChapterDraft>();
        for (var index = 0; index < headings.Count; index++)
        {
            var start = headings[index];
            var end = index + 1 < headings.Count ? headings[index + 1] : lines.Count;
            var title = CleanHeading(lines[start]);
            if (title.Length == 0) title = $"第 {index + 1} 章";
            var body = string.Join("\n", lines.Skip(start + 1).Take(end - start - 1)).Trim();
            chapters.Add(new ChapterDraft(title, body, index + 1));
        }
        return new ChapterSplitResult(ChapterSplitMode.ByHeading, chapters, $"按文本里的 {chapters.Count} 个章节标题拆");
    }

    private static ChapterSplitResult ByLength(string text, int limit, string reason)
    {
        var paragraphs = text
            .Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(block => block.Trim())
            .Where(block => block.Length > 0)
            .ToList();
        if (paragraphs.Count == 0) return new ChapterSplitResult(ChapterSplitMode.None, Array.Empty<ChapterDraft>(), "源节点还没有内容");

        var total = paragraphs.Sum(block => block.Length);
        var desired = Math.Clamp((int)Math.Round(total / (double)TargetChapterLength), 1, Math.Min(limit, paragraphs.Count));
        var targetPerChapter = Math.Max(1, total / desired);

        var chapters = new List<ChapterDraft>();
        var buffer = new List<string>();
        var length = 0;
        foreach (var paragraph in paragraphs)
        {
            buffer.Add(paragraph);
            length += paragraph.Length;
            // 攒够目标长度就收一章；最后一章由循环外的收尾处理。
            if (length < targetPerChapter || chapters.Count == desired - 1) continue;
            chapters.Add(MakeDraft(chapters.Count + 1, buffer));
            buffer.Clear();
            length = 0;
        }
        if (buffer.Count > 0) chapters.Add(MakeDraft(chapters.Count + 1, buffer));

        var note = $"{reason}（{chapters.Count} 章，约每章 {targetPerChapter} 字）";
        return new ChapterSplitResult(ChapterSplitMode.ByLength, chapters, note);
    }

    private static ChapterDraft MakeDraft(int order, IReadOnlyList<string> blocks)
    {
        var body = string.Join("\n\n", blocks).Trim();
        return new ChapterDraft($"第{ChineseNumber(order)}章 · {Summarize(body)}", body, order);
    }

    /// <summary>用正文首句开头一段当章节标题的后半截——标题也来自原文，不编。</summary>
    private static string Summarize(string body)
    {
        var firstLine = body.Split('\n').FirstOrDefault(line => line.Trim().Length > 0)?.Trim() ?? string.Empty;
        var cut = firstLine.IndexOfAny(new[] { '。', '！', '？', '；', '.', '!', '?', ';', '，', ',' });
        var sentence = cut > 0 ? firstLine[..cut] : firstLine;
        sentence = sentence.Trim();
        return sentence.Length <= TitleSummaryLength ? sentence : sentence[..TitleSummaryLength];
    }

    private static string CleanHeading(string line)
    {
        var text = line.Trim();
        text = Regex.Replace(text, @"^#{1,6}\s*", string.Empty);
        text = text.Trim('【', '】').Trim();
        text = Regex.Replace(text, @"\s+", " ");
        return text;
    }

    private static string Normalize(string? text) =>
        (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    /// <summary>1..99 的中文数字，章节序号用（第1章 → 第一章）。</summary>
    public static string ChineseNumber(int value)
    {
        if (value <= 0) return value.ToString();
        if (value < 10) return Digits[value];
        if (value == 10) return "十";
        if (value < 20) return "十" + Digits[value - 10];
        if (value < 100)
        {
            var tens = value / 10;
            var ones = value % 10;
            return Digits[tens] + "十" + (ones == 0 ? string.Empty : Digits[ones]);
        }
        return value.ToString();
    }

    private static readonly string[] Digits = { "零", "一", "二", "三", "四", "五", "六", "七", "八", "九" };

    /// <summary>
    /// 这一章里「出场」了哪些项目设定（按设定名或别名在正文里出现来判定）。
    /// 匹配不到的章节就是没有出场项——**不会**因为「一章总得有角色」而补一个。
    /// </summary>
    public static IReadOnlyList<WorkflowEntity> MatchEntities(WorkflowCanvasState canvas, string chapterText)
    {
        var hits = new List<WorkflowEntity>();
        if (string.IsNullOrWhiteSpace(chapterText)) return hits;
        foreach (var entity in canvas.Entities)
        {
            if (Mentions(chapterText, entity.Name) || AliasesOf(entity.Aliases).Any(alias => Mentions(chapterText, alias)))
                hits.Add(entity);
        }
        return hits;
    }

    /// <summary>设定名至少两个字才参与匹配：单字名（「他」「云」）会在正文里满地误命中。</summary>
    private static bool Mentions(string text, string name)
    {
        var value = (name ?? string.Empty).Trim();
        return value.Length >= 2 && text.Contains(value, StringComparison.Ordinal);
    }

    private static IEnumerable<string> AliasesOf(string raw) =>
        (raw ?? string.Empty).Split(new[] { '·', ',', '，', '、', ';', '；', '|', '/', ' ' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>把草稿拼成一段可以喂给模型或写进状态栏的多行文本（调试与预览用）。</summary>
    public static string Describe(ChapterSplitResult result)
    {
        if (result.IsEmpty) return result.Note;
        var builder = new StringBuilder(result.Note);
        foreach (var chapter in result.Chapters)
            builder.Append($"\n· {chapter.Title}（{chapter.Content.Length} 字）");
        return builder.ToString();
    }
}
