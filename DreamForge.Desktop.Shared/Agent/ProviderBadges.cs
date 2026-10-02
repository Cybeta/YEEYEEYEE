namespace DreamForge.Desktop;

/// <summary>
/// 一枚厂家徽标：**两个字母 + 一个区分色**。
/// </summary>
/// <param name="Abbreviation">两个 ASCII 字母（小圆徽标里放得下、也不受字体影响）。</param>
/// <param name="ColorHex">区分色（#RRGGBB）。</param>
/// <param name="IsKnown">这个预设 id 有没有明确条目。表外的 id 退回中性徽标，界面上不会出现空白。</param>
public sealed record ProviderBadge(string Abbreviation, string ColorHex, bool IsKnown);

/// <summary>
/// 「当前用的是哪一家」的那枚小徽标：**自绘的字母 + 颜色**，不是别家的 logo。
///
/// 为什么自绘而不是用官方 logo：各家的标识都是注册商标。要正经用就得逐家遵守品牌规范、
/// 有的还得用官方素材包（先同意条款才能下），而这个仓库里除了自家 logo **一张图片资源都没有**。
/// 「字母 + 颜色」已经足够回答「现在用的是哪一家」，也不牵扯任何授权问题。
///
/// 颜色是**我们自己挑的区分色**（按各家给人的印象取的近似值），用途只有一个：在深色界面上
/// 把它们彼此分开。它不是官方色值，也不要当品牌资产用——真要严谨的品牌色与 logo，
/// 走「官方素材包」那条路（见 TODO 第 6 条）。
///
/// 放在共享层的原因与 <see cref="NodeKindPalette"/> 相同：颜色与缩写本身是数据，
/// 要有测试盯着「表里的每一家都有徽标、且彼此颜色不同」——加了新厂家却忘了配徽标，
/// 应该在测试里报出来，而不是等界面上显示成一片灰。
/// </summary>
public static class ProviderBadges
{
    /// <summary>不认得的厂家用的中性徽标（也用于「自定义」与「本地模拟」这两个没有厂家可言的预设）。</summary>
    public const string NeutralAbbreviation = "AI";

    public const string NeutralColorHex = "#8FA6BD";

    public static ProviderBadge Of(string? presetId)
    {
        var id = (presetId ?? string.Empty).Trim().ToLowerInvariant();
        return id switch
        {
            "deepseek" => new ProviderBadge("DS", "#4D6BFE", true),
            "moonshot" => new ProviderBadge("KM", "#C084FC", true),
            "qwen" => new ProviderBadge("QW", "#22D3EE", true),
            "zhipu" => new ProviderBadge("ZP", "#FBBF24", true),
            "siliconflow" => new ProviderBadge("SF", "#F472B6", true),
            "openai" => new ProviderBadge("OA", "#E2E8F0", true),
            "ollama" => new ProviderBadge("OL", "#FB923C", true),
            // 本地模拟与「自定义」没有「哪一家」可言：给中性徽标，但它们是**明知**的条目
            // （不是「表里没有、猜不出来」），所以 IsKnown 仍然是真——工具提示不会说错话。
            "local" => new ProviderBadge("LC", NeutralColorHex, true),
            "custom" => new ProviderBadge(NeutralAbbreviation, NeutralColorHex, true),
            // 表外的 id：预设表加了新厂家却忘了配徽标时走这里，界面照常能用，测试会报出来。
            _ => new ProviderBadge(NeutralAbbreviation, NeutralColorHex, false)
        };
    }

    /// <summary>徽标的可读说法，用于工具提示（例：「DeepSeek · 徽标 DS」）。</summary>
    public static string Describe(string? presetId, string providerName)
    {
        var badge = Of(presetId);
        var name = string.IsNullOrWhiteSpace(providerName) ? "当前模型" : providerName;
        return badge.IsKnown
            ? $"{name} · 徽标 {badge.Abbreviation}"
            : $"{name} · 徽标 {badge.Abbreviation}（预设表里没有配这一家的徽标，先用中性徽标）";
    }
}
