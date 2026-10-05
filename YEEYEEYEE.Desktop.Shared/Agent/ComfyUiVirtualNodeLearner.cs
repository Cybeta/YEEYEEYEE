using System.Text.Json.Nodes;
using YEEYEEYEE.Host;

namespace YEEYEEYEE.Desktop;

/// <summary>对某个前端节点类型的裁决。</summary>
public sealed record ComfyUiRuleVerdict(
    string Type,
    ComfyUiVirtualNodeRule? Rule,
    bool Accepted,
    string Reason);

/// <summary>一轮「让大模型认一认」的结果。</summary>
public sealed record ComfyUiRuleLearningResult(
    IReadOnlyList<ComfyUiVirtualNodeRule> Accepted,
    IReadOnlyList<ComfyUiRuleVerdict> Verdicts,
    ComfyUiImportAuditReport Before,
    ComfyUiImportAuditReport After,
    string Note)
{
    public int DroppedBefore => Before.Count(ComfyUiFindingKind.DroppedByConversion);

    public int DroppedAfter => After.Count(ComfyUiFindingKind.DroppedByConversion);

    /// <summary>给人看的结果：认了哪些、没认的为什么、以及修前修后的账。</summary>
    public string Describe()
    {
        var lines = new List<string> { Note };
        foreach (var verdict in Verdicts)
        {
            var what = verdict.Rule is null
                ? verdict.Reason
                : verdict.Rule.Kind switch
                {
                    ComfyUiVirtualNodeKind.PassThrough => $"直通（顺着第 {verdict.Rule.InputSlot} 个输入往下走）",
                    ComfyUiVirtualNodeKind.Value => "自带一个值",
                    _ => "纯界面件"
                };
            lines.Add($"· {verdict.Type} → {(verdict.Accepted ? "已采用：" + what : "没采用：" + what)}"
                + (verdict.Accepted ? string.Empty : $"（{verdict.Reason}）"));
        }
        if (Verdicts.Count > 0)
            lines.Add($"「我们丢了」从 {DroppedBefore} 处降到 {DroppedAfter} 处。");
        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// 让大模型认一认「这些我们不认识的前端节点是干什么的」，然后把它的答案**拿去验证**。
///
/// 为什么只问「一个类型」而不是「一处输入」：一处输入坏掉的根子通常是某个前端节点族不认识，
/// 认出这一族，那台机器上**所有**用到它的工作流一起好——这比逐处打补丁省得多，也才解释得通。
///
/// 为什么答案必须验证：模型说的是猜测，而「我们丢了」是可以**量**的。做法是照它给的规则把那台机器
/// 的所有正文重转一遍（<see cref="ComfyUiLibrary.Reconvert"/>），再看两件事：
/// ①「我们丢了」真的少了；② 没有多出「判断不了」的。两条都成立才采用。
/// 猜错的代价不是「没修好」，而是「把本来接通的线改坏」——所以宁可不用。
/// </summary>
public static class ComfyUiVirtualNodeLearner
{
    /// <summary>一次最多问几个类型：每个都要重转一遍全库（几秒）来验证，问太多就是让用户干等。</summary>
    private const int MaxTypes = 8;

    private const string SystemPrompt =
        "你在帮我们把一份 ComfyUI 的「网页格式」工作流转成 API 格式。只输出一个 JSON 对象，"
        + "不要解释、不要 Markdown 代码块、不要多余文字。";

    /// <param name="progress">
    /// 过程怎么说给用户听。**可能在非 UI 线程上被调**：本方法内部有带 <c>ConfigureAwait(false)</c> 的 await，
    /// 之后那几句就落在工作线程上。界面侧必须先 Post 回 UI 线程再写控件——这里踩过，点「让大模型认一认」
    /// 直接把整个应用带走（InvalidOperationException: Call from invalid thread）。
    /// </param>
    public static async Task<ComfyUiRuleLearningResult> LearnAsync(
        ComfyUiLibraryResult library,
        IAiJsonCompleter completer,
        IReadOnlyList<ComfyUiVirtualNodeRule> alreadyKnown,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(completer);

        var baseline = library.Audit ?? new ComfyUiImportAuditReport(0, 0,
            Array.Empty<ComfyUiImportFinding>(), Array.Empty<ComfyUiUnknownType>());

        var candidates = baseline.UnknownTypes.Take(MaxTypes).ToList();
        if (candidates.Count == 0)
            return new ComfyUiRuleLearningResult(alreadyKnown, Array.Empty<ComfyUiRuleVerdict>(),
                baseline, baseline, "没有需要认的前端节点：这次体检里没有「我们丢了」的输入。");

        var accepted = new List<ComfyUiVirtualNodeRule>(alreadyKnown);
        var verdicts = new List<ComfyUiRuleVerdict>();
        var current = library;
        var round = baseline;

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Invoke($"正在让大模型认「{candidate.Type}」…");

            ComfyUiVirtualNodeRule? proposed;
            try
            {
                if (!library.RawDrafts.TryGetValue(candidate.SampleWorkflowKey, out var raw)
                    || library.ObjectInfo is null)
                {
                    verdicts.Add(new ComfyUiRuleVerdict(candidate.Type, null, false, "这份原稿没留在手上，问不了"));
                    continue;
                }

                var answer = await completer
                    .CompleteJsonAsync(SystemPrompt, BuildPrompt(candidate, raw, library.ObjectInfo), cancellationToken)
                    .ConfigureAwait(false);
                proposed = ProposalFromModelJson(candidate.Type, answer, out var parseError);
                if (proposed is null)
                {
                    verdicts.Add(new ComfyUiRuleVerdict(candidate.Type, null, false, parseError));
                    continue;
                }
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException
                or InvalidOperationException or System.Text.Json.JsonException)
            {
                // 问不动就如实说，不能假装认过：剩下的候选也不再问（多半是同一个原因，例如没配密钥）。
                verdicts.Add(new ComfyUiRuleVerdict(candidate.Type, null, false, "问模型时出错：" + error.Message));
                break;
            }

            var trial = TryRule(current, round, accepted, proposed);
            if (trial.Accepted)
            {
                accepted.Add(proposed);
                current = trial.Library;
                round = trial.Audit;
                verdicts.Add(new ComfyUiRuleVerdict(candidate.Type, proposed, true, string.Empty));
                progress?.Invoke($"「{candidate.Type}」认下来了：丢掉的输入降到 "
                    + $"{round.Count(ComfyUiFindingKind.DroppedByConversion)} 处");
            }
            else
            {
                verdicts.Add(new ComfyUiRuleVerdict(candidate.Type, proposed, false, trial.Reason));
            }
        }

        var skipped = baseline.UnknownTypes.Count - candidates.Count;
        var note = skipped > 0
            ? $"问了 {candidates.Count} 个类型（另有 {skipped} 个这次没问，超出单次上限）。"
            : $"问了 {candidates.Count} 个类型。";

        return new ComfyUiRuleLearningResult(accepted, verdicts, baseline, round, note);
    }

    /// <summary>
    /// 验证一条规则**真的有用**：照它重转全库，再看账。
    /// 采用的条件是「我们丢了的输入变少」**且**「没有多出判断不了的」——两条都要。
    /// </summary>
    public static (bool Accepted, ComfyUiLibraryResult Library, ComfyUiImportAuditReport Audit, string Reason) TryRule(
        ComfyUiLibraryResult library,
        ComfyUiImportAuditReport baseline,
        IReadOnlyList<ComfyUiVirtualNodeRule> accepted,
        ComfyUiVirtualNodeRule proposed)
    {
        var trial = ComfyUiLibrary.Reconvert(library, [.. accepted, proposed]);
        var after = trial.Audit ?? baseline;
        var improved = after.Count(ComfyUiFindingKind.DroppedByConversion)
            < baseline.Count(ComfyUiFindingKind.DroppedByConversion);
        var polluted = after.Count(ComfyUiFindingKind.Unclassified) > 0;

        if (improved && !polluted) return (true, trial, after, string.Empty);
        return (false, library, baseline,
            polluted
                ? "照它改之后出现了判断不了的输入，宁可不用"
                : "照它改之后「我们丢了」一处都没少，多半是认错了");
    }

    /// <summary>
    /// 解析模型的答案。**只认这四种**（<c>passthrough</c> / <c>value</c> / <c>discard</c> / 其他当说不清），
    /// 形状不对就当它没答——宁可不用，也不去猜它想说什么。
    /// 与接口文档那条路同一个做法：解析是纯函数，测试不必真的调模型。
    /// </summary>
    public static ComfyUiVirtualNodeRule? ProposalFromModelJson(string type, string answer, out string error)
    {
        error = string.Empty;
        var text = (answer ?? string.Empty).Trim();
        // 有的模型会把 JSON 包在代码块里，这不是错，取出来就是。
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            error = "模型没有给出 JSON";
            return null;
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(text[start..(end + 1)]);
        }
        catch (System.Text.Json.JsonException)
        {
            error = "模型的 JSON 读不动";
            return null;
        }
        if (parsed is not JsonObject body)
        {
            error = "模型的 JSON 不是对象";
            return null;
        }

        var kind = body["kind"]?.ToString() ?? string.Empty;
        var why = body["why"]?.ToString() ?? string.Empty;
        var source = why.Length > 0 ? $"模型：{why}" : "模型";
        switch (kind)
        {
            case "passthrough":
                return new ComfyUiVirtualNodeRule(type, ComfyUiVirtualNodeKind.PassThrough,
                    ReadIndex(body["input_slot"]), source);
            case "value":
                return new ComfyUiVirtualNodeRule(type, ComfyUiVirtualNodeKind.Value,
                    ReadIndex(body["value_index"]), source);
            case "discard":
                return new ComfyUiVirtualNodeRule(type, ComfyUiVirtualNodeKind.Discard, 0, source);
            default:
                error = why.Length > 0 ? "模型说不清：" + why : "模型说不清";
                return null;
        }
    }

    /// <summary>把一个问题问清楚：它长什么样、它每一路输入接的是谁、它的输出接到了谁。</summary>
    private static string BuildPrompt(ComfyUiUnknownType candidate, string rawDraft, JsonObject objectInfo)
    {
        var shape = ComfyUiImportAuditor.DescribeForModel(rawDraft, candidate.Type, objectInfo);
        return $$"""
            我们不认识这个前端节点类型。它不在服务端的节点定义里，所以转换时被跳过了，
            于是它下游的必填输入整项消失（缺必填输入，提交会被 ComfyUI 拒收）。

            类型：{{candidate.Type}}
            它出现在：{{candidate.SampleWorkflowTitle}}

            {{shape}}
            请判断它**在这个位置上的作用**（不要按名字猜）：
             · "passthrough"：只是个「把连线拉长 / 换个地方接」的转发节点，把某一路输入原样传下去
                → 同时给出 input_slot（第几个输入，从 0 数）
             · "value"：它自己带着一个值（写在控件上），消费方要的就是那个值
                → 同时给出 value_index（第几个控件值，通常是 0）
             · "discard"：纯界面件（备注、标签、开关、预览），本来就不参与计算
             · "unknown"：从这些信息看不出来

            只输出 JSON：{"kind":"passthrough","input_slot":0,"why":"一句话理由"}
            """;
    }

    private static int ReadIndex(JsonNode? value)
        => value is not null && int.TryParse(value.ToString(), out var index) && index >= 0 ? index : 0;
}
