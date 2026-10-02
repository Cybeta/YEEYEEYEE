namespace YEEYEEYEE.Desktop;

/// <summary>
/// 「选了哪一家 + 哪个模型」换算成配置字段的结果。
///
/// 抽成 UI-free 的纯映射，理由有两个：
/// 1）这个映射有两处界面要用（新主端的模型设置、首次启动的接入引导），各写一遍迟早会分叉，
///    出现「设置里选了 Kimi 不发送采样参数、引导里却发送」这种只有用户才会撞上的不一致；
/// 2）预设表要跟着服务商长期维护（下线旧模型、改地址），映射只有一份且有测试盯着才敢改。
///
/// 只覆盖预设负责的字段：密钥、工作目录、主题、图像 / 视频接口等一律不碰。
/// </summary>
public sealed record ProviderPresetValues(
    string Endpoint,
    AiApiFormat Format,
    bool UseFullUrl,
    bool SendsSamplingParameters,
    string ModelId,
    int ContextWindow,
    int MaxOutputTokens,
    bool SupportsVision,
    bool UseLocal)
{
    /// <summary>
    /// 这家的能力值该不该由用户自己填：自定义接口（预设表不认识它），或这家没有预置型号（无从替他判断）。
    /// 厂家预设则由所选模型自动生效。
    ///
    /// 这条规则同时管着两扇门的界面（模型设置与接入引导）与保存时的取值：两边必须一致，
    /// 否则会出现「引导里按预设写死、设置里按手填」这种同一次接入两个结果的怪事，所以放在这里由测试盯着。
    /// </summary>
    public static bool NeedsManualCapabilities(ProviderPreset preset) =>
        !preset.IsLocal && (preset.Id == ProviderPreset.Custom.Id || preset.Models.Count == 0);

    /// <summary>
    /// 算出某一家的字段值。传 <paramref name="model"/> 表示用户又挑了一个具体型号；
    /// 传 null 表示按这家**第一个已核实的型号**来（拿不到型号就返回空，交给用户手填）。
    /// </summary>
    public static ProviderPresetValues Resolve(ProviderPreset preset, ProviderModel? model)
    {
        // 本地模拟：没有地址也没有模型，唯一要传递的信息就是「走本地兜底」。
        if (preset.IsLocal)
            return new ProviderPresetValues(string.Empty, preset.Format, false, false, string.Empty, 0, 0, false, true);

        var chosen = model ?? preset.DefaultModel;
        return new ProviderPresetValues(
            preset.Endpoint,
            preset.Format,
            false,
            preset.SendsSamplingParameters,
            chosen?.Id ?? string.Empty,
            chosen?.ContextWindow ?? 0,
            chosen?.MaxOutputTokens ?? 0,
            chosen?.SupportsVision ?? false,
            false);
    }

    /// <summary>
    /// 把这份换算结果说成一句人话，用在「厂家预设已按某模型自动启用……」的提示上。
    ///
    /// 存在的意义是**别让自动生效的东西悄悄生效**：识图、长上下文、要不要发采样参数这些以前是静默填进去的，
    /// 用户在界面上看不到，出了事（例如发了采样参数被服务端拒）也找不到原因。这里把结论摊开给他看。
    /// </summary>
    public string DescribeCapabilities()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(ModelId)) parts.Add($"模型 {ModelId}");
        parts.Add(SupportsVision ? "支持图片输入" : "不支持图片输入");
        parts.Add(ContextWindow > 0 ? $"上下文 {FormatTokens(ContextWindow)}" : "未声明上下文窗口");
        if (MaxOutputTokens > 0) parts.Add($"单次输出上限 {FormatTokens(MaxOutputTokens)}");
        parts.Add(SendsSamplingParameters ? "发送采样参数" : "不发送采样参数（服务端固定）");
        return string.Join(" · ", parts);
    }

    /// <summary>token 数折成人看的写法：1048576 → 1M，384000 → 384K，否则原样。</summary>
    private static string FormatTokens(int tokens) => tokens switch
    {
        >= 1_000_000 => $"{tokens / 1_000_000d:0.#}M",
        >= 1_000 => $"{tokens / 1_000d:0.#}K",
        _ => tokens.ToString()
    };
}
