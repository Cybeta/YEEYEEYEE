namespace YEEYEEYEE.Desktop;

/// <summary>
/// 「一键出图 / 一键出章节视频」这一次到底要跑什么：意图、要不要补图、两个池子、每镜多少秒。
///
/// 为什么把这份东西单独抽出来：界面只负责把用户的选择拼成它，执行那边只认它。
/// 于是「缺几张图、几段视频、要花多少钱」这些都能在**不碰界面**的地方算清楚并钉进测试——
/// 这正是这条链最该钉住的部分（它一动手就是花钱的）。
/// </summary>
public sealed record OneClickRunRequest(
    GenerationIntent Intent,
    bool FillMissingImages,
    IReadOnlyList<GenerationAuditItem> ImageItems,
    IReadOnlyList<GenerationAuditItem> VideoItems,
    SitePoolChoice? ImagePool,
    SitePoolChoice? VideoPool,
    int Seconds)
{
    /// <summary>这一次要补几张图。**清单本身就是要做的事**，所以数量从它算出来，不另存一个数（免得两边对不上）。</summary>
    public int ImageCount => ImageItems.Count;

    /// <summary>这一次要出几段视频。</summary>
    public int VideoCount => VideoItems.Count;

    /// <summary>这一次真的要出图（勾了补齐、且确实缺图）。</summary>
    public bool WantsImages => FillMissingImages && ImageCount > 0;

    /// <summary>这一次真的要出视频（意图是出视频、且确实缺视频）。</summary>
    public bool WantsVideos => Intent == GenerationIntent.Video && VideoCount > 0;
}

/// <summary>
/// 时长与池子档位对不对得上。
///
/// 为什么要有这一步：各家视频接口对时长的支持**写在池子清单里**（<see cref="SitePool.Seconds"/>），
/// 而用户可能填一个这家根本没有的秒数。清单上明写着别的档位时，那多半就是个 400——
/// 与其让他白等几十秒再收到一句服务端报错，不如在**点下去之前**说出来。
///
/// 三条口径都是「如实说」，不是「替服务端裁决」：
/// · 清单没写时长（0）= 不知道，交给服务端判断（与参考图能力那个三态字段同一条规矩）；
/// · 清单写的正是这个秒数 = 对得上；
/// · 清单写了**别的**秒数 = 提醒一句，但**不拦**——清单可能是旧的，用户也可能知道服务端能吃下。
/// </summary>
public static class VideoDurationPolicy
{
    /// <summary>返回「要不要提醒」以及那句话。</summary>
    public static (bool Mismatch, string Note) Check(SitePool? pool, int seconds)
    {
        if (seconds <= 0) return (false, "时长留空：由服务端按模型的默认值决定。");
        if (pool is null) return (false, $"这次不走池子（用设置里的视频接口）：清单里没有它的时长档位，{seconds}s 交给服务端判断。");
        if (pool.Seconds <= 0) return (false, $"「{pool.Label}」的清单没写时长，{seconds}s 交给服务端判断。");
        if (pool.Seconds == seconds) return (false, $"「{pool.Label}」登记的时长就是 {seconds}s。");

        return (true,
            $"「{pool.Label}」清单里登记的是 {pool.Seconds}s，不是 {seconds}s——这个池子多半不会接受 {seconds}s，"
            + $"可能白等一次。换一个池子，或把它改成 {pool.Seconds}s 再出。");
    }

    /// <summary>候选里**能接受这个秒数**的那些池子（清单没写时长的也算「不知道，可以试」）。</summary>
    public static IReadOnlyList<SitePool> Accepting(IEnumerable<SitePool> pools, int seconds) =>
        seconds <= 0
            ? pools.ToList()
            : pools.Where(pool => pool.Seconds <= 0 || pool.Seconds == seconds).ToList();
}

/// <summary>
/// 一键这一次大概要花多少积分。**两笔分开报**（补图一笔、出视频一笔），最后给一个合计——
/// 用户要知道的是「这一下总共多少」，而不是两个孤零零的数字。
///
/// 算不出来的如实说算不出来：池子清单没登记数字单价时（<see cref="SitePool.UnitPrice"/> 为 0）
/// 报一个 0 会被读成「免费」，那比不报价更坏。
/// </summary>
public static class OneClickCost
{
    public static IReadOnlyList<string> Describe(OneClickRunRequest request)
    {
        var lines = new List<string>();

        if (request.WantsImages)
        {
            var price = request.ImagePool?.Pool.UnitPrice ?? 0;
            lines.Add(price > 0
                ? $"补图 {request.ImageCount} 张：单价 {price:0.####}，预计 {price * request.ImageCount:0.####} 积分。"
                : $"补图 {request.ImageCount} 张；这个池子没登记数字单价，算不出图的花费。");
        }

        if (request.WantsVideos)
        {
            var price = request.VideoPool?.Pool.UnitPrice ?? 0;
            lines.Add(price > 0
                ? $"出视频 {request.VideoCount} 段：单价 {price:0.####}，预计 {price * request.VideoCount:0.####} 积分。"
                : $"出视频 {request.VideoCount} 段；这个池子没登记数字单价，算不出视频的花费。");
        }

        if (lines.Count == 0) return new[] { "没有要生成的东西：这一层的产物都是齐的。" };

        // 只有一笔时不另起一行「合计」——那只是把刚说过的数再说一遍。
        if (lines.Count == 1) return lines;

        var imagePrice = request.WantsImages ? request.ImagePool?.Pool.UnitPrice ?? 0 : 0;
        var videoPrice = request.WantsVideos ? request.VideoPool?.Pool.UnitPrice ?? 0 : 0;
        var imageTotal = imagePrice * request.ImageCount;
        var videoTotal = videoPrice * request.VideoCount;
        if (imagePrice > 0 || videoPrice > 0)
        {
            var bothKnown = (!request.WantsImages || imagePrice > 0) && (!request.WantsVideos || videoPrice > 0);
            lines.Add(bothKnown
                ? $"合计约 {imageTotal + videoTotal:0.####} 积分（这两笔是分开计费的，加起来才是这一下的总账）。"
                : $"已知的那部分合计约 {imageTotal + videoTotal:0.####} 积分；还有一笔没单价，实际会比这更多。");
        }

        return lines;
    }
}
