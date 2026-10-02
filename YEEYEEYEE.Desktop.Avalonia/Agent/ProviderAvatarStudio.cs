using YEEYEEYEE.Desktop;

namespace YEEYEEYEE.Desktop.Avalonia;

/// <summary>
/// 用**你自己配置的图像链路**给每个厂家生成一张形象，存进 `provider-art`，开奖的许愿那一拍就会用它。
///
/// 为什么是「应用自己生成」而不是由我离线生成几张图塞进仓库：
/// · 密钥在你手里、在你的账户上出图，我不碰它；
/// · 换模型、加厂家时你自己点一下就能补一张，不必等我发版；
/// · 生成出来的是**这个应用产出的素材**，来源与条款一眼可查，不像网图那样说不清出处。
///
/// **画的是自创角色**：只借这一家的气质、配色与命名意象，明确要求不模仿任何已存在的作品角色或商标——
/// 这与本项目一贯拒绝直接使用第三方 logo / 拟人形象是同一条线。
/// </summary>
internal static class ProviderAvatarStudio
{
    /// <summary>
    /// 出图尺寸。形象在界面上只有 112px 的圆，1024 已经足够清楚，又不会慢到让人等不下去。
    /// </summary>
    public const int Size = 1024;

    /// <summary>
    /// 每个厂家的设计说明：一个意象 + 一句外形，颜色取自这一家的区分色。
    ///
    /// 写成一张表而不是一段通用提示词：八家用同一段话会画出八个长得一样的角色，
    /// 而「每个厂家有自己的形象」的意义就在于一眼能认出是哪一家。
    /// </summary>
    private static readonly Dictionary<string, string> Designs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["deepseek"] = "以深海与鲸为意象：银蓝短发、深蓝外套，衣摆像水波，安静专注的神情",
        ["moonshot"] = "以月亮与夜航为意象：灰紫长发、带星屑的发饰，披一件短披风，手里拿一卷纸",
        ["qwen"] = "以水系与青瓷为意象：青色齐肩发、圆领短衫，发间一枚水纹发卡，笑眼清爽",
        ["zhipu"] = "以书卷与棋盘为意象：琥珀色发、方框眼镜、笔挺的藏青学生制服，神情认真",
        ["siliconflow"] = "以电路与流水为意象：粉发、束起的马尾、连帽短外套，袖口有发光的细线纹样",
        ["openai"] = "以白瓷与回声为意象：银白发、素色高领衫、线条极简，气质冷静",
        ["ollama"] = "以橘色小兽与驼队为意象：橘色短发、毛边连帽衫、脸颊有一道浅色纹路，憨厚可靠",
        ["local"] = "以台灯与工作台为意象：褐色短发、护目镜挂在颈上、多口袋工装，像个小工匠",
        ["custom"] = "以空白画布与铅笔为意象：黑色短发、米色衬衫、手里拿一支笔，干净好相处"
    };

    /// <summary>这一家的形象应该长什么样（写给模型看的一段话）。</summary>
    public static string BuildPrompt(string providerId, string providerName, string colorHex)
    {
        var design = Designs.TryGetValue(providerId, out var brief)
            ? brief
            : Designs["custom"];

        return
            "一张二次元风格的半身角色立绘，正方形构图，人物居中、正面朝向镜头、胸像以上入镜，"
            + $"背景是纯净的深色渐变（以 {colorHex} 为点缀色），带一圈很淡的同色光晕，没有任何文字、符号或商标。"
            + $"角色设计（自创角色）：{design}。"
            + "画风：干净的日式动画赛璐璐上色，线条清晰，五官端正、手指与肢体自然，"
            + "明暗过渡柔和，适合当一枚圆形头像看。"
            + $"这位角色的设定与「{providerName}」这个名字的气质相配，但**必须是原创角色，"
            + "不要模仿任何已存在的作品、角色、吉祥物或商标**。";
    }

    /// <summary>负面提示词：出图时最常见的那几种坏法，加上「别抄现成的」。</summary>
    public const string NegativePrompt =
        "低清，模糊，噪点，多余手指，变形的手，多余肢体，五官不对称，塑料感皮肤，死鱼眼，多张脸，"
        + "文字，水印，logo，签名，边框，拼图，分屏，全身入镜外的裁切，"
        + "已知作品的角色，已知品牌吉祥物，商标，模仿现有角色";

    /// <summary>
    /// 出这一家的形象并存进 `provider-art`。返回**给用户看的一句话**（成功与失败都用它，
    /// 失败时说清是哪一步：没配图像链路 / 接口报错 / 存不下来）。
    /// </summary>
    public static async Task<string> GenerateAsync(string providerId, string providerName, string colorHex)
    {
        IImageProvider provider;
        try
        {
            provider = ImageProviderFactory.Create();
        }
        catch (Exception error)
        {
            return "图像链路创建失败：" + error.Message;
        }

        if (!provider.IsConfigured)
            return "还没配置图像链路：先在「图像接口」里填好模型（地址可留空复用文本接口），再回来生成形象。";

        ImageGenerationResult result;
        try
        {
            result = await provider.GenerateAsync(new ImageGenerationRequest
            {
                Prompt = BuildPrompt(providerId, providerName, colorHex),
                NegativePrompt = NegativePrompt,
                Width = Size,
                Height = Size
            });
        }
        catch (Exception error)
        {
            return "出图请求失败：" + error.Message;
        }

        if (result.Status != ImageGenerationStatus.Succeeded || string.IsNullOrWhiteSpace(result.FilePath))
            return "出图没成：" + (string.IsNullOrWhiteSpace(result.Error) ? "接口没有返回可用的图片数据。" : result.Error);

        try
        {
            var folder = Path.Combine(AppPaths.UserConfigDirectory, ProviderAvatar.FolderName);
            Directory.CreateDirectory(folder);

            // 先删掉这一家别的扩展名：Find 是按扩展名顺序找的，留着旧的那份会盖掉新生成的这张。
            foreach (var stale in ProviderAvatar.PathsFor(providerId)) File.Delete(stale);

            var target = Path.Combine(folder, providerId + ".png");
            File.Copy(result.FilePath, target, overwrite: true);

            // 缓存里可能还记着上一次的结果（甚至记着「没有」），清掉才会读新文件。
            ProviderAvatar.Forget(providerId);

            return $"已生成并保存到 {target}（{result.Provider} · {result.Model}）。下一次开奖就会用它。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "图出来了但存不下来：" + error.Message;
        }
    }
}
