namespace YEEYEEYEE.Desktop;

/// <summary>
/// 把「这一轮花了多少」算成能直接摆上界面的几行字。
///
/// 为什么单独一个类型、而不是写在面板里：面板在 Avalonia 工程，测试工程引用不到它。
/// 而这里每一条口径都是**会被读错**的东西——速率怎么算、命中率的分母是谁、取不到时显示什么，
/// 一旦错了，界面上给出的就是假数据，比不显示更误导。所以它必须能被钉住。
///
/// 形状参照 dsh 的做法：一排**能折行的小胶囊** + 一份点开的明细。挤成一行、尾巴被省略号吃掉，
/// 等于把「tok/s / 上下文 / 缓存」这三项——这一行存在的全部理由——直接藏了。
/// </summary>
public sealed record AiUsageReport(IReadOnlyList<string> Chips, IReadOnlyList<string> Details)
{
    public static readonly AiUsageReport None = new(Array.Empty<string>(), Array.Empty<string>());

    /// <summary>服务商一个字都没回传时，界面上那行说明。</summary>
    public const string MissingNote = "用量：—（这一家接口没有回传 token 用量）";

    public bool HasAnything => Chips.Count > 0;

    /// <summary>一行说清（工具提示与「复制」用）。</summary>
    public string Summary => string.Join("  ·  ", Chips);

    /// <summary>
    /// 组装一次请求的用量说明。
    /// </summary>
    /// <param name="usage">服务端回传的用量；没回传就是 <see cref="AiUsage.Empty"/>。</param>
    /// <param name="contextWindow">模型声明的上下文窗口（token）；0 = 未声明，不显示占比。</param>
    /// <param name="decodeSeconds">第一个字到最后一个正文字的秒数（**含思考段**）；null = 没测到。</param>
    /// <param name="firstTokenSeconds">从发出请求到第一个字的秒数（含排队与网络）；null = 没测到。</param>
    public static AiUsageReport From(AiUsage usage, int contextWindow, double? decodeSeconds, double? firstTokenSeconds)
    {
        var chips = new List<string>();
        var details = new List<string>();

        var input = usage.InputTokens;
        var output = usage.CompletionTokens;

        if (input > 0 || output > 0)
        {
            chips.Add($"↑{Short(input)} ↓{Short(output)}");
            details.Add($"输入 {input:N0} token（其中缓存命中 {usage.CacheHitTokens:N0}、未命中 {usage.CacheMissTokens:N0}）");
            details.Add($"输出 {output:N0} token");
        }
        else
        {
            // 明确的「没有」，而不是 0——0 会被读成「这一轮没花 token」。
            details.Add("这一家接口没有回传 token 用量，所以看不到 ↑↓ / 缓存 / 上下文 / 速率：不是 0，是没有。");
        }

        // 命中率的分母是**计费输入侧**（命中 + 未命中），与服务端 prompt_tokens 的口径一致。
        // 拿 total_tokens（含输出）当分母会把比率压低，读起来像「缓存不爱命中」——那是算错了，不是事实。
        var billedInput = usage.CacheHitTokens + usage.CacheMissTokens;
        if (billedInput > 0)
        {
            var rate = (double)usage.CacheHitTokens / billedInput;
            chips.Add($"缓存 {rate:P0}");
            details.Add($"缓存命中率 {rate:P1}（命中 {usage.CacheHitTokens:N0} ÷ 计费输入 {billedInput:N0}；命中的部分按服务商规则通常便宜得多）");
        }

        if (contextWindow > 0 && input > 0)
        {
            chips.Add($"上下文 {Short(input)}/{Short(contextWindow)}");
            details.Add($"本次请求占上下文窗口 {input * 100.0 / contextWindow:0.#}%（窗口按模型声明值 {contextWindow:N0} token 算）");
        }

        if (output > 0 && decodeSeconds is > 0)
        {
            var rate = output / decodeSeconds.Value;
            chips.Add($"{rate:0} tok/s");
            details.Add($"解码速率 {rate:0.0} tok/s（输出 token ÷ 第一个字到最后一个字之间的 {decodeSeconds.Value:0.0} 秒）");
        }

        if (firstTokenSeconds is > 0)
        {
            chips.Add($"首字 {firstTokenSeconds.Value:0.0}s");
            details.Add($"首字延迟 {firstTokenSeconds.Value:0.0} 秒（从发出请求到第一个字；这一段含排队与网络，所以**不**计入解码速率）");
        }

        return new AiUsageReport(chips, details);
    }

    /// <summary>大数字缩写着看：12.3K / 1.2M。</summary>
    public static string Short(long value) => value switch
    {
        >= 1_000_000 => $"{value / 1_000_000.0:0.#}M",
        >= 1_000 => $"{value / 1_000.0:0.#}K",
        _ => value.ToString()
    };
}
