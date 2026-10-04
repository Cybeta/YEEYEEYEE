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
                // 提示词住在共享层（ProviderAvatarPrompts）：应用内重画与仓库里那批离线生成的形象
                // 读的是同一张表——两份各写一遍的话，同一家会有两个长相。
                Prompt = ProviderAvatarPrompts.Build(providerId, providerName, colorHex),
                NegativePrompt = ProviderAvatarPrompts.NegativePrompt,
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
