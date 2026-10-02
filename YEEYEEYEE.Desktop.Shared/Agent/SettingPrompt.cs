namespace YEEYEEYEE.Desktop;

/// <summary>
/// 从一个**设定**（实体 + 变体）拼出可执行的出图提示词。
///
/// 为什么单独一份、而且放在共享层：这串文字会同时出现在「引用画廊」里给人改、被出图链路直接拿去用，
/// 两端读的必须是同一份；而「提示词该按什么顺序写」这件事已经有基线在管（<see cref="PromptBaseline"/>），
/// 这里只负责把**设定数据**按那份基线的写法拼起来，不另立一套。
///
/// 两条优先级：①设定描述里 AI 已经写好的「出图提示词」段直接用它——那是按基线写出来的、比现拼的更贴；
/// ②没有才按「种类 + 名称 + 变体 + 描述 + 该种类的参考图规格」拼一份最小可用的。
/// </summary>
public static class SettingPrompt
{
    /// <summary>描述里可能出现的提示词段标题（按基线写的设定会带这一段）。</summary>
    private static readonly string[] PromptMarkers = { "出图提示词", "出图 Prompt" };

    /// <summary>这段提示词开始与结束的分界：下一个「【小标题】」或「负面提示词」之前都算它。</summary>
    private static readonly string[] PromptStops = { "【", "负面提示词", "Negative" };

    /// <summary>拼一份能直接出图的提示词。空描述不会拼出「空气」——至少给出种类、名称与参考图规格。</summary>
    public static string Compose(WorkflowEntity entity, WorkflowEntityVariant variant)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(variant);

        if (ExtractFromDescription(variant.Description) is { } written) return written;

        var parts = new List<string> { $"{WorkflowEntity.KindName(entity.Kind)}「{entity.Name}」{VariantSuffix(variant)}" };
        var description = Flatten(variant.Description);
        if (description.Length > 0) parts.Add(description);
        if (SpecOf(entity.Kind).Length > 0) parts.Add(SpecOf(entity.Kind));
        return string.Join("\n", parts);
    }

    /// <summary>这个设定该避开的（按种类分；没有基线的种类落到通用那一条）。</summary>
    public static string Negative(WorkflowEntity entity) =>
        PromptBaseline.NegativeFor(NodeKindPalette.CategoryOf(entity?.Kind ?? EntityKind.Prop));

    /// <summary>
    /// 打开预览时该填哪一份提示词：**附件上存的那一份优先**（就是上次真出图发出去的那条，
    /// 里面可能有人改过的措辞），没有才按设定现拼。手放的素材没存提示词，落回现拼那一条。
    /// </summary>
    public static string Prefer(string? stored, WorkflowEntity entity, WorkflowEntityVariant variant) =>
        string.IsNullOrWhiteSpace(stored) ? Compose(entity, variant) : stored.Trim();

    /// <summary>
    /// 在描述里找「出图提示词」那一段；找不到返回 null（由调用方现拼）。
    /// 约定（基线也是这么要求的）：正文按【小标题】分段，提示词写在末尾另起一段。
    /// </summary>
    public static string? ExtractFromDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;
        var text = description.Replace("\r\n", "\n");

        foreach (var marker in PromptMarkers)
        {
            var index = text.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0) continue;

            // 基线里这一段写成「【出图提示词】…」，所以标题的收尾括号也要一起剪掉。
            var rest = text[(index + marker.Length)..].TrimStart(' ', '　', '：', ':', '—', '-', '\n', '】', ']', '》');
            foreach (var stop in PromptStops)
            {
                var cut = rest.IndexOf(stop, StringComparison.Ordinal);
                if (cut > 0) rest = rest[..cut];
            }
            rest = rest.Trim();
            if (rest.Length > 0) return rest;
        }
        return null;
    }

    private static string VariantSuffix(WorkflowEntityVariant variant) =>
        string.IsNullOrWhiteSpace(variant.Name) || variant.Name == "默认" ? string.Empty : $"（{variant.Name}）";

    /// <summary>
    /// 这个种类的参考图规格（角色是转面图、场景是基准图、道具是特写）。
    /// 这里取的是**能直接进提示词**的那一句：`TurnaroundSpec` 里还带着「崩了就降级」这类给人看的
    /// 纪律说明，混进提示词只会污染模型看到的字（纪律该去技能说明里读）。
    /// </summary>
    private static string SpecOf(EntityKind kind) => kind switch
    {
        EntityKind.Character => PromptBaseline.TurnaroundPromptLine,
        EntityKind.Scene => "远景宽幅交代空间关系与光源方向，画面里不出现主要人物（基准图）",
        _ => "居中特写，干净背景，材质与磨损细节清晰"
    };

    /// <summary>
    /// 把分段写成的描述压成一行：换行变顿号、【小标题】去掉（提示词里不需要标签，标签是给人看的）。
    /// </summary>
    private static string Flatten(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : string.Join("，", text.Replace("\r\n", "\n")
                .Split('\n')
                .Select(StripLeadingLabel)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0));

    private static string StripLeadingLabel(string line)
    {
        var trimmed = line.TrimStart(' ', '　');
        if (!trimmed.StartsWith('【')) return line;
        var end = trimmed.IndexOf('】');
        return end < 0 ? line : trimmed[(end + 1)..];
    }
}
